$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}

$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = (New-Object Reflection.AssemblyName($eventArgs.Name)).Name
    $path = Join-Path $bin ($name + '.dll')
    if ([IO.File]::Exists($path)) { return [Reflection.Assembly]::LoadFrom($path) }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-default-hotkeys-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$fakeSource = Join-Path $temp 'DefaultHotkeyFakeRegistrar.cs'
$fakeAssembly = Join-Path $temp 'DefaultHotkeyFakeRegistrar.dll'
$frameworkDir = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $frameworkDir 'csc.exe'
if (![IO.File]::Exists($csc)) { throw "The .NET Framework C# compiler was not found: $csc" }
[IO.File]::WriteAllText($fakeSource, @'
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using WinResizer.Common.Shortcuts;
using WinResizer.Core.Shortcuts;

public sealed class DefaultHotkeyFakeRegistrar : IGlobalHotkeyRegistrar
{
    private int _nextId;

    public List<ModifierKeys> Modifiers { get; private set; }

    public List<Keys> Keys { get; private set; }

    public event EventHandler<KeyPressedEventArgs> KeyPressed;

    public DefaultHotkeyFakeRegistrar()
    {
        Modifiers = new List<ModifierKeys>();
        Keys = new List<Keys>();
    }

    public int RegisterHotKey(ModifierKeys modifier, Keys key)
    {
        Modifiers.Add(modifier);
        Keys.Add(key);
        return ++_nextId;
    }

    public void UnRegisterHotKey(int id)
    {
    }

    public void UnRegisterHotKey()
    {
    }

    public void Dispose()
    {
    }
}

'@)
& $csc /nologo /target:library /nowarn:0067 "/out:$fakeAssembly" "/reference:$(Join-Path $frameworkDir 'mscorlib.dll')" "/reference:$(Join-Path $frameworkDir 'System.dll')" "/reference:$(Join-Path $frameworkDir 'System.Core.dll')" "/reference:$(Join-Path $frameworkDir 'System.Windows.Forms.dll')" "/reference:$(Join-Path $bin 'WinResizer.Common.dll')" "/reference:$(Join-Path $bin 'WinResizer.Core.dll')" $fakeSource
if ($LASTEXITCODE -ne 0 -or ![IO.File]::Exists($fakeAssembly)) { throw 'Could not compile the test hotkey registrar.' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($fakeAssembly))

function Assert-True($condition, [string]$message) {
    if (!$condition) { throw $message }
}

function Assert-Hotkey($hotkey, [string]$key, [string[]]$modifiers, [string]$label) {
    Assert-True ($null -ne $hotkey) "$label is missing"
    Assert-True ($hotkey.Key -eq $key) "$label key mismatch: expected $key, got $($hotkey.Key)"
    $actual = @($hotkey.ModifierKeys | Sort-Object)
    $expected = @($modifiers | Sort-Object)
    Assert-True (($actual -join '|') -eq ($expected -join '|')) "$label modifiers mismatch"
}

function Get-ConfigHotkey($config, $type) {
    return $config.Keys[$type]
}

function Assert-DefaultKeys($config, [string]$label) {
    Assert-Hotkey (Get-ConfigHotkey $config ([WinResizer.Configuration.HotkeysType]::Save)) 'S' @('Ctrl', 'Win', 'Alt') "$label Save"
    Assert-Hotkey (Get-ConfigHotkey $config ([WinResizer.Configuration.HotkeysType]::Restore)) 'R' @('Ctrl', 'Win', 'Alt') "$label Restore"
    Assert-True ($null -eq (Get-ConfigHotkey $config ([WinResizer.Configuration.HotkeysType]::SaveAll))) "$label Save All is not empty"
    Assert-True ($null -eq (Get-ConfigHotkey $config ([WinResizer.Configuration.HotkeysType]::RestoreAll))) "$label Restore All is not empty"
    Assert-True ($config.Presets.Count -eq 0) "$label Presets are not empty"
}

$configPath = Join-Path $temp 'WinResizer.config.json'
$runtime = $null
try {
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($configPath)
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $fresh = [WinResizer.Configuration.ConfigFactory]::Current
    Assert-DefaultKeys $fresh 'Fresh factory'

    [WinResizer.Configuration.ConfigFactory]::Save()
    $freshJson = [Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($configPath))
    $keyNames = @($freshJson.Configs[0].Keys.Properties() | ForEach-Object Name)
    Assert-True ($keyNames -contains 'Save' -and $keyNames -contains 'Restore') 'Fresh JSON is missing Save or Restore'
    Assert-True (($keyNames -notcontains 'SaveAll') -and ($keyNames -notcontains 'RestoreAll')) 'Fresh JSON contains an empty action hotkey'
    Assert-True ([int]$freshJson.Configs[0].Presets.Count -eq 0) 'Fresh JSON Presets are not empty'
    Assert-True (@($freshJson.Configs[0].Keys.Save.ModifierKeys) -contains 'Win') 'Fresh JSON does not serialize Win'

    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-DefaultKeys ([WinResizer.Configuration.ConfigFactory]::Current) 'Fresh round-trip'

    $fake = [DefaultHotkeyFakeRegistrar]::new()
    $options = [WinResizer.Runtime.WinResizerRuntimeOptions]::new()
    $options.HotkeyRegistrar = $fake
    $options.OwnsHotkeyRegistrar = $false
    $options.EnableWindowMonitoring = $false
    $runtime = [WinResizer.Runtime.WinResizerRuntime]::new($options)
    $runtime.Start()
    $expectedModifier = [WinResizer.Common.Shortcuts.ModifierKeys]::Ctrl -bor
        [WinResizer.Common.Shortcuts.ModifierKeys]::Win -bor
        [WinResizer.Common.Shortcuts.ModifierKeys]::Alt
    Assert-True ($fake.Modifiers.Count -eq 2) "Fresh runtime registered $($fake.Modifiers.Count) hotkeys instead of 2"
    Assert-True (($fake.Modifiers | Where-Object { $_ -eq $expectedModifier }).Count -eq 2) 'Fresh runtime did not register Ctrl + Win + Alt'
    Assert-True (($fake.Keys -contains [Windows.Forms.Keys]::S) -and ($fake.Keys -contains [Windows.Forms.Keys]::R)) 'Fresh runtime registered the wrong action keys'
    $runtime.Dispose()
    $runtime = $null

    $existingJson = @'
{
  "CurrentProfileId": "existing-profile",
  "ThemePreference": "System",
  "IgnoredWindows": [],
  "Configs": [
    {
      "ProfileId": "existing-profile",
      "ProfileName": "Existing",
      "WindowSizes": [],
      "Keys": {
        "Save": { "ModifierKeys": ["Ctrl", "Alt"], "Key": "S" },
        "Restore": { "ModifierKeys": ["Ctrl", "Alt"], "Key": "R" },
        "SaveAll": { "ModifierKeys": ["Ctrl", "Shift"], "Key": "F11" },
        "RestoreAll": { "ModifierKeys": ["Ctrl", "Shift"], "Key": "F12" }
      },
      "Presets": [
        {
          "Id": "existing-preset",
          "Name": "Keep preset",
          "Enabled": true,
          "X": 10,
          "Y": 20,
          "Width": 800,
          "Height": 600,
          "Hotkey": { "ModifierKeys": ["Ctrl", "Alt"], "Key": "P" }
        }
      ]
    }
  ]
}
'@
    [IO.File]::WriteAllText($configPath, $existingJson)
    [WinResizer.Configuration.ConfigFactory]::Load()
    $existing = [WinResizer.Configuration.ConfigFactory]::Current
    Assert-Hotkey (Get-ConfigHotkey $existing ([WinResizer.Configuration.HotkeysType]::Save)) 'S' @('Ctrl', 'Alt') 'Existing Save'
    Assert-Hotkey (Get-ConfigHotkey $existing ([WinResizer.Configuration.HotkeysType]::Restore)) 'R' @('Ctrl', 'Alt') 'Existing Restore'
    Assert-Hotkey (Get-ConfigHotkey $existing ([WinResizer.Configuration.HotkeysType]::SaveAll)) 'F11' @('Ctrl', 'Shift') 'Existing Save All'
    Assert-Hotkey (Get-ConfigHotkey $existing ([WinResizer.Configuration.HotkeysType]::RestoreAll)) 'F12' @('Ctrl', 'Shift') 'Existing Restore All'
    Assert-Hotkey $existing.Presets[0].Hotkey 'P' @('Ctrl', 'Alt') 'Existing preset'
    Assert-True ($existing.Presets.Count -eq 1 -and $existing.Presets[0].Enabled) 'Existing preset was changed'

    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $profileA = [WinResizer.Configuration.ConfigFactory]::Current
    $profileB = [WinResizer.Configuration.ConfigFactory]::Profiles.Add('Second')
    Assert-DefaultKeys $profileA 'Profile A'
    Assert-DefaultKeys $profileB 'New profile B'

    $legacyJson = @'
{
  "WindowSizes": [],
  "SaveKey": { "ModifierKeys": ["Ctrl", "Alt"], "Key": "S" },
  "RestoreKey": { "ModifierKeys": ["Ctrl", "Alt"], "Key": "R" },
  "RestoreAllKey": { "ModifierKeys": ["Ctrl", "Shift"], "Key": "F12" }
}
'@
    [IO.File]::WriteAllText($configPath, $legacyJson)
    [WinResizer.Configuration.ConfigFactory]::Load()
    $legacy = [WinResizer.Configuration.ConfigFactory]::Current
    Assert-Hotkey (Get-ConfigHotkey $legacy ([WinResizer.Configuration.HotkeysType]::Save)) 'S' @('Ctrl', 'Alt') 'Legacy Save'
    Assert-Hotkey (Get-ConfigHotkey $legacy ([WinResizer.Configuration.HotkeysType]::Restore)) 'R' @('Ctrl', 'Alt') 'Legacy Restore'
    Assert-Hotkey (Get-ConfigHotkey $legacy ([WinResizer.Configuration.HotkeysType]::RestoreAll)) 'F12' @('Ctrl', 'Shift') 'Legacy Restore All'
    Assert-True ($null -eq (Get-ConfigHotkey $legacy ([WinResizer.Configuration.HotkeysType]::SaveAll))) 'Legacy missing Save All was filled unexpectedly'

    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $save = Get-ConfigHotkey ([WinResizer.Configuration.ConfigFactory]::Current) ([WinResizer.Configuration.HotkeysType]::Save)
    $empty = [WinResizer.Common.Shortcuts.Hotkeys]::new()
    Assert-True ($null -eq [WinResizer.Configuration.ConfigFactory]::Current.CheckHotkeyConflict($empty)) 'Empty hotkey was treated as a conflict'
    Assert-True ($null -eq [WinResizer.Configuration.ConfigFactory]::Current.CheckHotkeyConflict($save, $null, [WinResizer.Configuration.HotkeysType]::Save)) 'Save conflicted with Restore'

    'PASS: fresh defaults, indented JSON round-trip, exactly two runtime registrations, existing and legacy hotkey preservation, new profile defaults, empty hotkey semantics, conflict handling, and empty Presets.'
}
finally {
    if ($null -ne $runtime) { $runtime.Dispose() }
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath((Join-Path $temp 'cleanup.config.json'))
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
