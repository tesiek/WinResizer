$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}

function Make-Key([string]$key) {
    $value = New-Object WinResizer.Common.Shortcuts.Hotkeys
    [void]$value.ModifierKeys.Add('Ctrl')
    [void]$value.ModifierKeys.Add('Shift')
    $value.Key = $key
    return $value
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-preset-registration-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$hook = $null
$competitor = $null
$runtime = $null
$registrations = New-Object 'System.Collections.Generic.Dictionary[string,int]'
try {
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath((Join-Path $temp 'WinResizer.config.json'))
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $profileId = [WinResizer.Configuration.ConfigFactory]::Current.ProfileId
    $hook = New-Object WinResizer.Core.Shortcuts.KeyboardHook
    $competitor = New-Object WinResizer.Core.Shortcuts.KeyboardHook
    $options = New-Object WinResizer.Runtime.WinResizerRuntimeOptions
    $options.EnableGlobalHotkeys = $false
    $options.EnableWindowMonitoring = $false
    $options.HotkeyRegistrar = $hook
    $options.OwnsHotkeyRegistrar = $false
    $options.RegisteredPresetHotkeys = $registrations
    $runtime = New-Object WinResizer.Runtime.WinResizerRuntime($options)
    $runtime.Start()
    $presetId = $runtime.AddPreset($profileId, 'Real registration').PresetId
    $old = Make-Key 'F22'
    $new = Make-Key 'F23'
    [void]$runtime.SetPresetHotkey($profileId, $presetId, $old)
    if (!$registrations.ContainsKey($presetId)) { throw 'Initial preset registration is missing.' }
    $oldId = $registrations[$presetId]
    $occupiedId = $competitor.RegisterHotKey(6, [System.Windows.Forms.Keys]::F23)
    $before = [Newtonsoft.Json.JsonConvert]::SerializeObject([WinResizer.Configuration.ConfigFactory]::Current.Presets[0])
    $failed = $false
    try { [void]$runtime.SetPresetHotkey($profileId, $presetId, $new) } catch { $failed = $true }
    if (!$failed) { throw 'Occupied shortcut was accepted.' }
    if ($registrations[$presetId] -ne $oldId) { throw 'Old registration ID changed after conflict.' }
    if ([Newtonsoft.Json.JsonConvert]::SerializeObject([WinResizer.Configuration.ConfigFactory]::Current.Presets[0]) -cne $before) {
        throw 'Serialized preset changed after conflict.'
    }
    $oldStillReserved = $false
    try { [void]$competitor.RegisterHotKey(6, [System.Windows.Forms.Keys]::F22) } catch { $oldStillReserved = $true }
    if (!$oldStillReserved) { throw 'Old shortcut was released on failure.' }
    [void]$runtime.SetPresetHotkey($profileId, $presetId, $old)
    if ($registrations[$presetId] -ne $oldId) { throw 'Unchanged key re-registered.' }
    $competitor.UnRegisterHotKey($occupiedId)
    [void]$runtime.SetPresetHotkey($profileId, $presetId, $new)
    if ([WinResizer.Configuration.ConfigFactory]::Current.Presets[0].Hotkey.Key -ne 'F23') { throw 'Replacement failed.' }
    [void]$competitor.RegisterHotKey(6, [System.Windows.Forms.Keys]::F22)
    [void]$runtime.SetPresetHotkey($profileId, $presetId, $null)
    if ($null -ne [WinResizer.Configuration.ConfigFactory]::Current.Presets[0].Hotkey -or $registrations.ContainsKey($presetId)) {
        throw 'Clear failed.'
    }
    [void]$competitor.RegisterHotKey(6, [System.Windows.Forms.Keys]::F23)
    [void]$runtime.SetPresetEnabled($profileId, $presetId, $false)
    [void]$runtime.SetPresetHotkey($profileId, $presetId, $new)
    if ([WinResizer.Configuration.ConfigFactory]::Current.Presets[0].Hotkey.Key -ne 'F23' -or $registrations.ContainsKey($presetId)) {
        throw 'Disabled preset registered a key.'
    }
    'PASS: current Runtime and real Windows registration preserve old key on conflict, unchanged key, replacement, clear and disabled preset.'
}
finally {
    if ($null -ne $runtime) { $runtime.Dispose() }
    if ($null -ne $hook) { $hook.Dispose() }
    if ($null -ne $competitor) { $competitor.Dispose() }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
