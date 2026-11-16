$ErrorActionPreference = 'Stop'

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

Add-Type @'
using System;
using System.Reflection;
using System.Threading.Tasks;

public static class SnapshotReader
{
    public static void AssertStable(object store, PropertyInfo currentProperty, int iterations)
    {
        var tasks = new Task[4];
        for (var worker = 0; worker < tasks.Length; worker++)
        {
            tasks[worker] = Task.Run(() =>
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    var snapshot = currentProperty.GetValue(store, null);
                    if (snapshot == null || Count(snapshot, "WindowSizes") != 1 || Count(snapshot, "IgnoredWindows") != 1)
                        throw new InvalidOperationException("Background reader observed an incomplete snapshot.");
                }
            });
        }

        Task.WaitAll(tasks);
    }

    private static int Count(object snapshot, string propertyName)
    {
        var value = snapshot.GetType().GetProperty(propertyName).GetValue(snapshot, null);
        return (int)value.GetType().GetProperty("Count").GetValue(value, null);
    }
}
'@

function Assert-True($condition, [string]$message) {
    if (!$condition) { throw $message }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-runtime-snapshot-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$store = $null
$savedHandler = $null
$replacedHandler = $null
try {
    $saved = Join-Path $temp 'saved.json'
    $portable = Join-Path $temp 'portable.json'
    $import = Join-Path $temp 'same-profile-import.json'
    [WinResizer.Configuration.ConfigFactory]::SetPath($saved, $portable)

    $profiles = [WinResizer.Configuration.ConfigFactory]::Profiles
    $config = $profiles.UseDefault()
    $processRule = New-Object WinResizer.Configuration.WindowSize
    $processRule.Name = 'test.exe'
    $processRule.Title = '*'
    $processRule.AutoResize = $true
    $processRule.AutoResizeDelay = 700
    $processRule.Rect = [WinResizer.Common.Windows.Rect]::new(10, 20, 300, 400)
    $config.WindowSizes.Add($processRule)
    $ignoredRule = New-Object WinResizer.Configuration.IgnoredWindowRule
    $ignoredRule.Process = 'test.exe'
    $ignoredRule.Class = 'TestClass'
    $ignoredRule.Title = 'before'
    $ignoredRule.TitleMatch = [WinResizer.Configuration.IgnoredWindowTitleMatch]::Exact
    $profiles.AddIgnoredWindow($ignoredRule)

    $saveCount = 0
    $replaceCount = 0
    $savedHandler = [WinResizer.Configuration.ConfigFactory+ConfigurationSavedEvent] { $script:saveCount++ }
    $replacedHandler = [WinResizer.Configuration.ConfigFactory+ConfigurationReplacedEvent] { $script:replaceCount++ }
    [WinResizer.Configuration.ConfigFactory]::add_ConfigurationSaved($savedHandler)
    [WinResizer.Configuration.ConfigFactory]::add_ConfigurationReplaced($replacedHandler)

    $runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.Runtime.dll'))
    $storeType = $runtimeAssembly.GetType('WinResizer.Runtime.AutoResizeConfigurationSnapshotStore', $true)
    $store = [Activator]::CreateInstance($storeType, [Reflection.BindingFlags]'Instance,Public,NonPublic', $null, @(), $null)
    $refresh = $storeType.GetMethod('Refresh', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    $currentProperty = $storeType.GetProperty('Current', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    [void]$refresh.Invoke($store, @())

    $snapshotA = $currentProperty.GetValue($store, $null)
    Assert-True ($null -ne $snapshotA) 'Initial snapshot was not published without SettingForm'
    Assert-True ($snapshotA.WindowSizes.Count -eq 1) 'Initial snapshot does not contain the process rule'
    Assert-True ($snapshotA.IgnoredWindows.Count -eq 1) 'Initial snapshot does not contain the ignored rule'

    $storeSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Runtime/AutoResizeConfigurationSnapshotStore.cs'))
    $coordinatorSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Runtime/AutoResizeCoordinator.cs'))
    $runtimeSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Runtime/WinResizerRuntime.cs'))
    $traySource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer/Services/TrayService.cs'))
    Assert-True (!$storeSource.Contains('SettingForm') -and !$storeSource.Contains('Control.Invoke')) 'Snapshot store still depends on UI'
    Assert-True (!$coordinatorSource.Contains('SettingForm') -and !$coordinatorSource.Contains('.Invoke(')) 'Auto Resize coordinator still contains a UI invocation path'
    Assert-True (!$traySource.Contains('ConfigReload +=') -and !$runtimeSource.Contains('SettingForm')) 'Shared runtime still depends on SettingForm.ConfigReload'
    Assert-True (([regex]::Matches($runtimeSource, 'ReloadHotkeysForCurrentProfile\(\);')).Count -eq 1) 'Profile switch hotkey reload is not wired exactly once'

    # Background readers only see complete published references.
    [SnapshotReader]::AssertStable($store, $currentProperty, 250)

    # Mutating the live model does not mutate an already published snapshot.
    $config.WindowSizes[0].AutoResize = $false
    $config.WindowSizes[0].AutoResizeDelay = 25
    $config.WindowSizes[0].Name = 'changed.exe'
    $config.WindowSizes[0].Title = 'changed title'
    $config.WindowSizes[0].Rect = [WinResizer.Common.Windows.Rect]::new(11, 22, 333, 444)
    $profiles.IgnoredWindows[0].Active = $false
    $profiles.IgnoredWindows[0].Process = 'changed.exe'
    $profiles.IgnoredWindows[0].Class = 'ChangedClass'
    $profiles.IgnoredWindows[0].Title = 'after'
    $profiles.IgnoredWindows[0].TitleMatch = [WinResizer.Configuration.IgnoredWindowTitleMatch]::Contains
    Assert-True ($snapshotA.WindowSizes[0].AutoResize -eq $true -and $snapshotA.WindowSizes[0].AutoResizeDelay -eq 700) 'Snapshot A changed after live process mutation'
    Assert-True ($snapshotA.IgnoredWindows[0].Title -eq 'before') 'Snapshot A changed after live ignored-rule mutation'
    $snapshotA.WindowSizes[0].AutoResize = $false
    $snapshotA.IgnoredWindows[0].Title = 'mutated copy'
    Assert-True ($snapshotA.WindowSizes[0].AutoResize -eq $true -and $snapshotA.IgnoredWindows[0].Title -eq 'before') 'Snapshot exposed mutable internal data'

    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ($saveCount -gt 0) 'ConfigurationSaved was not raised after Save'
    $snapshotB = $currentProperty.GetValue($store, $null)
    Assert-True ($snapshotB.WindowSizes[0].Name -eq 'changed.exe' -and $snapshotB.WindowSizes[0].Title -eq 'changed title' -and $snapshotB.WindowSizes[0].AutoResize -eq $false -and $snapshotB.WindowSizes[0].AutoResizeDelay -eq 25) 'Snapshot B did not contain saved process changes'
    Assert-True ($snapshotB.IgnoredWindows[0].Active -eq $false -and $snapshotB.IgnoredWindows[0].Process -eq 'changed.exe' -and $snapshotB.IgnoredWindows[0].Class -eq 'ChangedClass' -and $snapshotB.IgnoredWindows[0].Title -eq 'after' -and $snapshotB.IgnoredWindows[0].TitleMatch -eq [WinResizer.Configuration.IgnoredWindowTitleMatch]::Contains) 'Snapshot B did not contain saved ignored-rule changes'

    $extraProcess = New-Object WinResizer.Configuration.WindowSize
    $extraProcess.Name = 'extra.exe'
    $extraProcess.Title = '*'
    $config.WindowSizes.Add($extraProcess)
    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ($currentProperty.GetValue($store, $null).WindowSizes.Count -eq 2) 'Added process rule was not published'
    [void]$config.WindowSizes.Remove($extraProcess)
    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ($currentProperty.GetValue($store, $null).WindowSizes.Count -eq 1) 'Removed process rule was not published'

    $extraIgnored = New-Object WinResizer.Configuration.IgnoredWindowRule
    $extraIgnored.Process = 'extra.exe'
    $profiles.AddIgnoredWindow($extraIgnored)
    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ($currentProperty.GetValue($store, $null).IgnoredWindows.Count -eq 2) 'Added ignored rule was not published'
    [void]$profiles.RemoveIgnoredWindow($extraIgnored)
    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ($currentProperty.GetValue($store, $null).IgnoredWindows.Count -eq 1) 'Removed ignored rule was not published'

    # A profile switch publishes the new profile before ConfigFactory.Save finishes.
    $other = $profiles.Add('other')
    [void][WinResizer.Configuration.ConfigFactory]::ProfileSwitch($other.ProfileId)
    $snapshotOther = $currentProperty.GetValue($store, $null)
    Assert-True ($snapshotOther.WindowSizes.Count -eq 0) 'Profile switch did not publish the new profile snapshot'
    [void][WinResizer.Configuration.ConfigFactory]::ProfileSwitch($config.ProfileId)

    # Replacement with the same CurrentProfileId raises a post-replacement event and refreshes the store.
    $sameId = $config.ProfileId
    $sameIdJson = @"
{
  "CurrentProfileId": "$sameId",
  "Configs": [
    {
      "ProfileId": "$sameId",
      "ProfileName": "Imported",
      "WindowSizes": [
        { "Name": "imported.exe", "Title": "Imported", "AutoResize": true, "AutoResizeDelay": 1000 }
      ]
    }
  ],
  "IgnoredWindows": [
    { "Active": true, "Process": "imported.exe", "Class": "ImportedClass", "Title": "Imported", "TitleMatch": "Exact" }
  ]
}
"@
    [IO.File]::WriteAllText($import, $sameIdJson)
    [WinResizer.Configuration.ConfigFactory]::Load($import)
    Assert-True ($replaceCount -eq 1) 'ConfigurationReplaced was not raised exactly once for same-ID import'
    $snapshotImported = $currentProperty.GetValue($store, $null)
    Assert-True ($snapshotImported.WindowSizes[0].Name -eq 'imported.exe' -and $snapshotImported.WindowSizes[0].AutoResizeDelay -eq 1000) 'Same-ID import did not publish new process data'
    Assert-True ($snapshotImported.IgnoredWindows[0].Class -eq 'ImportedClass') 'Same-ID import did not publish new ignored-rule data'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileId -eq $sameId) 'Same-ID import changed the active profile ID'

    'PASS: initial snapshot without SettingForm, atomic background reads, immutable copied data, Save publication, profile-switch publication, and same-profile-ID ConfigurationReplaced refresh.'
}
finally {
    if ($null -ne $replacedHandler) { [WinResizer.Configuration.ConfigFactory]::remove_ConfigurationReplaced($replacedHandler) }
    if ($null -ne $savedHandler) { [WinResizer.Configuration.ConfigFactory]::remove_ConfigurationSaved($savedHandler) }
    if ($null -ne $store) { $store.Dispose() }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
