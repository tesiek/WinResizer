$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll','WinResizer.exe') {
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

function Assert-True($condition, [string]$message) {
    if (!$condition) { throw $message }
}

function Read-Json([string]$path) {
    return ,([Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($path)))
}

function Write-ConfigJson(
    [string]$path,
    [string]$profileId,
    [string]$profileName,
    [string]$theme = 'System',
    [string]$processName = 'sample.exe',
    [string]$title = 'Sample',
    [string]$hotkey = 'S',
    [switch]$omitTheme,
    [switch]$includeIgnored) {
    $themeProperty = if ($omitTheme) { '' } else { '"ThemePreference": "' + $theme + '",' }
    $ignoredProperty = if ($includeIgnored) {
        '"IgnoredWindows": [{"Active": true, "Process": "' + $processName + '", "Class": "SampleClass", "Title": "' + $title + '", "TitleMatch": "Exact"}]'
    } else {
        '"IgnoredWindows": []'
    }
    $json = @"
{
  "CurrentProfileId": "$profileId",
  "Configs": [
    {
      "ProfileId": "$profileId",
      "ProfileName": "$profileName",
      "DisableInFullScreen": true,
      "RestoreAllIncludeMinimized": false,
      "NotifyOnSaved": false,
      "EnableResizeByTitle": true,
      "EnableAutoResizeDelay": true,
      "Keys": {
        "Save": {"ModifierKeys": ["Ctrl"], "Key": "$hotkey"}
      },
      "WindowSizes": [
        {
          "Name": "$processName",
          "Title": "$title",
          "Rect": {"Left": 10, "Top": 20, "Right": 810, "Bottom": 620},
          "AutoResize": true,
          "AutoResizeDelay": 250
        }
      ],
      "Presets": []
    }
  ],
  $themeProperty
  $ignoredProperty
}
"@
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path))) | Out-Null
    [IO.File]::WriteAllText($path, $json)
}

function Get-UnwrappedException([Exception]$exception) {
    if ($exception -is [Management.Automation.MethodInvocationException] -and $null -ne $exception.InnerException) {
        return $exception.InnerException
    }
    return $exception
}

$unicodeSegment = -join ([char[]](0x005A, 0x0061, 0x017C, 0x00F3, 0x0142, 0x0107))
$temp = Join-Path ([IO.Path]::GetTempPath()) ('Window Resizer Portable Test ' + $unicodeSegment + '-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$store = $null
$replacedHandler = $null
$oldAppData = $env:APPDATA
$oldLocalAppData = $env:LOCALAPPDATA
try {
    $portableBase = Join-Path $temp 'Portable Base With Spaces'
    $differentCwd = Join-Path $temp 'Different CWD'
    [void][IO.Directory]::CreateDirectory($portableBase)
    [void][IO.Directory]::CreateDirectory($differentCwd)

    # The resolver is based on the supplied executable base directory and does not
    # depend on the process current directory.
    $resolved = [WinResizer.Configuration.ConfigPathProvider]::GetPortableConfigPath($portableBase)
    Assert-True ($resolved -eq [IO.Path]::Combine([IO.Path]::GetFullPath($portableBase), 'WinResizer.config.json')) 'Portable resolver returned an unexpected path'

    # First run creates a valid portable file immediately.
    $firstRunPath = Join-Path $portableBase 'WinResizer.config.json'
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($firstRunPath)
    Assert-True (![IO.File]::Exists($firstRunPath)) 'First-run fixture unexpectedly already exists'
    Push-Location $differentCwd
    try {
        [WinResizer.Configuration.ConfigFactory]::Load()
    } finally {
        Pop-Location
    }
    Assert-True ([IO.File]::Exists($firstRunPath)) 'First run did not create the portable configuration'
    Assert-True (![IO.File]::Exists((Join-Path $differentCwd 'WinResizer.config.json'))) 'Configuration was written to CWD'
    $firstJson = Read-Json $firstRunPath
    Assert-True ($firstJson.Configs.Count -eq 1 -and $firstJson.Configs.Item(0).Value[string]('ProfileName') -eq 'default') 'First-run defaults are incomplete'
    Assert-True ($firstJson.CurrentProfileId.ToString() -eq $firstJson.Configs.Item(0).Value[string]('ProfileId')) 'First-run CurrentProfileId is invalid'
    Assert-True ($firstJson.ThemePreference.ToString() -eq 'System') 'First-run ThemePreference is not System'

    # A test-only AppData file is ignored by normal portable startup.
    $fakeAppDataPath = Join-Path (Join-Path $temp 'Fake AppData') 'WinResizer.config.json'
    Write-ConfigJson $fakeAppDataPath 'appdata-profile' 'AppData profile' 'Dark'
    $fakeAppDataBytes = [IO.File]::ReadAllText($fakeAppDataPath)
    $appDataPortable = Join-Path (Join-Path $temp 'Portable Ignores AppData') 'WinResizer.config.json'
    $env:APPDATA = Join-Path $temp 'Fake AppData'
    $env:LOCALAPPDATA = Join-Path $temp 'Fake LocalAppData'
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($appDataPortable)
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::ConfigPath -eq [IO.Path]::GetFullPath($appDataPortable)) 'Portable path changed while ignoring AppData'
    Assert-True ([IO.File]::Exists($appDataPortable)) 'Portable file was not created when AppData fixture existed'
    Assert-True ([IO.File]::ReadAllText($fakeAppDataPath) -ceq $fakeAppDataBytes) 'AppData fixture was modified'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileName -eq 'default') 'AppData profile was loaded during normal startup'

    # Existing portable data is loaded without rewriting an already normalized file.
    $existingPath = Join-Path (Join-Path $temp 'Existing Portable') 'WinResizer.config.json'
    Write-ConfigJson $existingPath 'existing-profile' 'Existing profile' 'Light'
    $existingBytes = [IO.File]::ReadAllText($existingPath)
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($existingPath)
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileId -eq 'existing-profile') 'Existing portable profile was not loaded'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::Light) ("Existing ThemePreference was not loaded: " + [WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference)
    Assert-True ([IO.File]::ReadAllText($existingPath) -ceq $existingBytes) 'Existing normalized portable config was rewritten unexpectedly'

    # An invalid or missing current profile ID is normalized to the first
    # profile before a candidate is persisted.
    $invalidCurrent = Read-Json $existingPath
    $invalidCurrent['CurrentProfileId'] = [Newtonsoft.Json.Linq.JToken]::FromObject('missing-profile')
    [IO.File]::WriteAllText($existingPath, $invalidCurrent.ToString())
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileId -eq 'existing-profile') 'Invalid CurrentProfileId did not fall back to the first profile'
    Assert-True ((Read-Json $existingPath).CurrentProfileId.ToString() -eq 'existing-profile') 'Invalid CurrentProfileId was not persisted in normalized form'

    [WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference = [WinResizer.Configuration.ThemePreference]::Dark
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::System) 'UseDefault did not reset the global theme preference'

    # Legacy v1 data still migrates into the portable path.
    $legacyPath = Join-Path (Join-Path $temp 'Legacy Portable') 'WinResizer.config.json'
    $legacyJson = @'
{
  "DisableInFullScreen": false,
  "RestoreAllIncludeMinimized": true,
  "NotifyOnSaved": true,
  "EnableResizeByTitle": false,
  "EnableAutoResizeDelay": true,
  "WindowSizes": [],
  "SaveKey": {"ModifierKeys": ["Ctrl", "Alt"], "Key": "S"}
}
'@
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($legacyPath)) | Out-Null
    [IO.File]::WriteAllText($legacyPath, $legacyJson)
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($legacyPath)
    [WinResizer.Configuration.ConfigFactory]::Load()
    $legacyOutput = Read-Json $legacyPath
    Assert-True ($legacyOutput.Configs.Count -eq 1 -and $legacyOutput.Configs.Item(0).Value[string]('ProfileName') -eq 'default') 'Legacy configuration was not migrated'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.NotifyOnSaved -and [WinResizer.Configuration.ConfigFactory]::Current.RestoreAllIncludeMinimized) 'Legacy options were not preserved'

    # Missing and unknown theme values normalize safely; Light/Dark round-trip as strings.
    $themeMissingPath = Join-Path (Join-Path $temp 'Theme Missing') 'WinResizer.config.json'
    Write-ConfigJson $themeMissingPath 'theme-missing' 'Theme missing' 'System' -omitTheme
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($themeMissingPath)
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::System) 'Missing ThemePreference did not default to System'

    $themeRoundTripPath = Join-Path (Join-Path $temp 'Theme Roundtrip') 'WinResizer.config.json'
    Write-ConfigJson $themeRoundTripPath 'theme-light' 'Theme light' 'Light'
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($themeRoundTripPath)
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::Light) 'Light theme did not load'
    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ((Read-Json $themeRoundTripPath).ThemePreference.ToString() -eq 'Light') 'Light theme did not round-trip as a string'
    Write-ConfigJson $themeRoundTripPath 'theme-dark' 'Theme dark' 'Dark'
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::Dark) 'Dark theme did not load'
    Write-ConfigJson $themeRoundTripPath 'theme-unknown' 'Theme unknown' 'Solarized'
    [WinResizer.Configuration.ConfigFactory]::Load()
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::System) 'Unknown theme did not normalize to System'
    Assert-True ((Read-Json $themeRoundTripPath).ThemePreference.ToString() -eq 'System') 'Unknown theme normalization was not persisted'

    # First-run write failure reports a persistence exception and keeps the
    # portable path; no AppData fallback is attempted.
    $failureAppDataRoot = Join-Path $temp 'Failure AppData'
    $env:APPDATA = $failureAppDataRoot
    $env:LOCALAPPDATA = Join-Path $temp 'Failure LocalAppData'
    $blockedParent = Join-Path $temp 'Blocked Parent'
    [IO.File]::WriteAllText($blockedParent, 'not a directory')
    $blockedConfig = Join-Path $blockedParent 'WinResizer.config.json'
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($blockedConfig)
    $firstRunFailure = $null
    try { [WinResizer.Configuration.ConfigFactory]::Load() } catch { $firstRunFailure = Get-UnwrappedException $_.Exception }
    Assert-True ($firstRunFailure -is [WinResizer.Configuration.ConfigurationPersistenceException]) 'First-run write failure did not use ConfigurationPersistenceException'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::ConfigPath -eq [IO.Path]::GetFullPath($blockedConfig)) 'Write failure changed the active path'
    Assert-True (![IO.File]::Exists((Join-Path $failureAppDataRoot 'WinResizer.config.json'))) 'Write failure created an AppData fallback'

    # Import replaces live state only after the active portable file is written.
    $activePath = Join-Path (Join-Path $temp 'Import Active') 'WinResizer.config.json'
    $externalPath = Join-Path (Join-Path $temp 'Backup') 'import.json'
    Write-ConfigJson $activePath 'active-profile' 'Active profile' 'Light' 'old.exe' 'Old' 'A'
    Write-ConfigJson $externalPath 'active-profile' 'Imported profile' 'Dark' 'new.exe' 'New' 'I' -includeIgnored
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($activePath)
    [WinResizer.Configuration.ConfigFactory]::Load()
    $oldCurrent = [WinResizer.Configuration.ConfigFactory]::Current
    $externalBytes = [IO.File]::ReadAllText($externalPath)
    $script:portableReplaceCount = 0
    $replacedHandler = [WinResizer.Configuration.ConfigFactory+ConfigurationReplacedEvent] { $script:portableReplaceCount++ }
    [WinResizer.Configuration.ConfigFactory]::add_ConfigurationReplaced($replacedHandler)
    $runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.Runtime.dll'))
    $storeType = $runtimeAssembly.GetType('WinResizer.Runtime.AutoResizeConfigurationSnapshotStore', $true)
    $store = [Activator]::CreateInstance($storeType, [Reflection.BindingFlags]'Instance,Public,NonPublic', $null, @(), $null)
    $refresh = $storeType.GetMethod('Refresh', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    $currentProperty = $storeType.GetProperty('Current', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    [void]$refresh.Invoke($store, @())
    $oldSnapshot = $currentProperty.GetValue($store, $null)
    [WinResizer.Configuration.ConfigFactory]::Load($externalPath)
    Assert-True ($script:portableReplaceCount -eq 1) 'Import did not raise ConfigurationReplaced exactly once'
    Assert-True (![object]::ReferenceEquals($oldCurrent, [WinResizer.Configuration.ConfigFactory]::Current)) 'Import did not replace the live configuration object'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileId -eq 'active-profile' -and [WinResizer.Configuration.ConfigFactory]::Current.ProfileName -eq 'Imported profile') 'Import did not load the candidate profile'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.ThemePreference -eq [WinResizer.Configuration.ThemePreference]::Dark) 'Import did not load the candidate theme'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.WindowSizes[0].Name -eq 'new.exe') 'Import did not load process data'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Profiles.IgnoredWindows.Count -eq 1) 'Import did not load ignored windows'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::ConfigPath -eq [IO.Path]::GetFullPath($activePath)) 'Import changed ConfigPath'
    Assert-True ([IO.File]::ReadAllText($externalPath) -ceq $externalBytes) 'Import modified the external file'
    Assert-True ([IO.File]::Exists((Join-Path (Split-Path $activePath -Parent) 'WinResizer.config.pre-import.bak')) ) 'Import did not create the pre-import backup'
    $importedSnapshot = $currentProperty.GetValue($store, $null)
    Assert-True ($importedSnapshot.WindowSizes[0].Name -eq 'new.exe' -and $importedSnapshot.IgnoredWindows.Count -eq 1) 'ConfigurationReplaced did not refresh the runtime snapshot'
    [WinResizer.Configuration.ConfigFactory]::Save()
    Assert-True ((Read-Json $activePath).Configs.Item(0).Value[string]('ProfileName') -eq 'Imported profile') 'Post-import Save did not use the portable path'

    # Invalid import leaves active disk and live state untouched.
    $activeBeforeInvalid = [IO.File]::ReadAllText($activePath)
    $invalidPath = Join-Path $temp 'invalid-import.json'
    [IO.File]::WriteAllText($invalidPath, '{broken')
    $invalidCurrent = [WinResizer.Configuration.ConfigFactory]::Current
    $invalidFailure = $null
    try { [WinResizer.Configuration.ConfigFactory]::Load($invalidPath) } catch { $invalidFailure = Get-UnwrappedException $_.Exception }
    Assert-True ($invalidFailure -is [Newtonsoft.Json.JsonReaderException]) 'Invalid import did not report a JSON reader error'
    Assert-True ([object]::ReferenceEquals($invalidCurrent, [WinResizer.Configuration.ConfigFactory]::Current)) 'Invalid import changed live state'
    Assert-True ([IO.File]::ReadAllText($activePath) -ceq $activeBeforeInvalid) 'Invalid import changed active disk data'

    # A valid import with an active-file write failure does not replace live
    # state, snapshots, hotkeys, active bytes, or the external source.
    $failureActive = Join-Path (Join-Path $temp 'Import Write Failure') 'WinResizer.config.json'
    $failureExternal = Join-Path (Join-Path $temp 'Import Write Failure Backup') 'import.json'
    Write-ConfigJson $failureActive 'failure-profile' 'Failure old' 'Light' 'old-failure.exe' 'Old failure' 'O'
    Write-ConfigJson $failureExternal 'failure-profile' 'Failure imported' 'Dark' 'new-failure.exe' 'New failure' 'N'
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($failureActive)
    [WinResizer.Configuration.ConfigFactory]::Load()
    [void]$refresh.Invoke($store, @())
    $failureCurrent = [WinResizer.Configuration.ConfigFactory]::Current
    $failureSnapshot = $currentProperty.GetValue($store, $null)
    $failureActiveBytes = [IO.File]::ReadAllText($failureActive)
    $failureExternalBytes = [IO.File]::ReadAllText($failureExternal)
    $failureLock = [IO.File]::Open($failureActive, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $writeFailure = $null
        try { [WinResizer.Configuration.ConfigFactory]::Load($failureExternal) } catch { $writeFailure = Get-UnwrappedException $_.Exception }
        Assert-True ($writeFailure -is [WinResizer.Configuration.ConfigurationPersistenceException]) 'Import write failure did not report ConfigurationPersistenceException'
    } finally {
        $failureLock.Dispose()
    }
    Assert-True ([object]::ReferenceEquals($failureCurrent, [WinResizer.Configuration.ConfigFactory]::Current)) 'Failed import replaced live state'
    Assert-True ($currentProperty.GetValue($store, $null).WindowSizes[0].Name -eq $failureSnapshot.WindowSizes[0].Name) 'Failed import changed the runtime snapshot'
    Assert-True ([IO.File]::ReadAllText($failureActive) -ceq $failureActiveBytes) 'Failed import changed active disk data'
    Assert-True ([IO.File]::ReadAllText($failureExternal) -ceq $failureExternalBytes) 'Failed import changed external source data'
    Assert-True ((Get-ChildItem (Split-Path $failureActive -Parent) -Filter '*.tmp' -ErrorAction SilentlyContinue).Count -eq 0) 'Failed import left a temporary file'

    # Explicit CLI/API path remains opt-in and never changes the active portable path.
    $cliExternal = Join-Path (Join-Path $temp 'CLI Explicit') 'config.json'
    Write-ConfigJson $cliExternal 'cli-profile' 'CLI profile' 'Dark' 'cli.exe' 'CLI' 'C'
    $cliActiveBytes = [IO.File]::ReadAllText($failureActive)
    $cliLoaded = [WinResizer.Base.ConfigUtils]::Load($cliExternal, $null)
    Assert-True $cliLoaded 'Explicit CLI configuration load failed'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::ConfigPath -eq [IO.Path]::GetFullPath($failureActive)) 'Explicit CLI load changed the active portable path'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileName -eq 'CLI profile') 'Explicit CLI configuration was not loaded'
    Assert-True ([IO.File]::ReadAllText($failureActive) -ceq $cliActiveBytes) 'Explicit CLI load modified the active portable file'

    if ($null -ne $replacedHandler) {
        [WinResizer.Configuration.ConfigFactory]::remove_ConfigurationReplaced($replacedHandler)
        $replacedHandler = $null
    }
    if ($null -ne $store) {
        $store.Dispose()
        $store = $null
    }

    # Export serializes the current model directly and leaves the active path.
    $exportActive = Join-Path (Join-Path $temp 'Export Active') 'WinResizer.config.json'
    $exportPath = Join-Path (Join-Path $temp 'Export Destination') 'export.json'
    Write-ConfigJson $exportActive 'export-profile' 'Export profile' 'Dark' 'export.exe' 'Export' 'E'
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($exportActive)
    [WinResizer.Configuration.ConfigFactory]::Load()
    $exportActiveBytes = [IO.File]::ReadAllText($exportActive)
    [WinResizer.Configuration.ConfigFactory]::Export($exportPath)
    Assert-True ([WinResizer.Configuration.ConfigFactory]::ConfigPath -eq [IO.Path]::GetFullPath($exportActive)) 'Export changed ConfigPath'
    $exportJson = Read-Json $exportPath
    Assert-True ($exportJson.Configs.Item(0).Value[string]('ProfileName') -eq 'Export profile' -and $exportJson.ThemePreference.ToString() -eq 'Dark') 'Export did not contain the active model'
    Assert-True ([IO.File]::ReadAllText($exportActive) -ceq $exportActiveBytes) 'Export unexpectedly rewrote active disk data'
    [WinResizer.Configuration.ConfigFactory]::Current.NotifyOnSaved = $true
    [WinResizer.Configuration.ConfigFactory]::Save()
    $savedExportJson = Read-Json $exportActive
    $unchangedExportJson = Read-Json $exportPath
    Assert-True ($savedExportJson.Configs.Item(0).Value[bool]('NotifyOnSaved')) 'Save after export did not use active portable path'
    Assert-True (!$unchangedExportJson.Configs.Item(0).Value[bool]('NotifyOnSaved')) 'Export file changed after a later active Save'
    $exportFailureDirectory = Join-Path $temp 'Export Failure Destination'
    [void][IO.Directory]::CreateDirectory($exportFailureDirectory)
    $exportLive = [WinResizer.Configuration.ConfigFactory]::Current
    $exportFailure = $null
    try { [WinResizer.Configuration.ConfigFactory]::Export($exportFailureDirectory) } catch { $exportFailure = Get-UnwrappedException $_.Exception }
    Assert-True ($exportFailure -is [WinResizer.Configuration.ConfigurationPersistenceException]) 'Export failure did not report ConfigurationPersistenceException'
    Assert-True ([object]::ReferenceEquals($exportLive, [WinResizer.Configuration.ConfigFactory]::Current) -and [IO.File]::Exists($exportActive) -and (Read-Json $exportActive).Configs.Item(0).Value[bool]('NotifyOnSaved')) 'Export failure changed active/runtime state'

    # Atomic Save leaves valid JSON and cleans temporary files; a locked target
    # leaves the previous final file intact.
    $atomicBefore = [IO.File]::ReadAllText($exportActive)
    [WinResizer.Configuration.ConfigFactory]::Current.ProfileName = 'Atomic in memory only'
    $atomicLock = [IO.File]::Open($exportActive, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $atomicFailure = $null
        try { [WinResizer.Configuration.ConfigFactory]::Save() } catch { $atomicFailure = Get-UnwrappedException $_.Exception }
        Assert-True ($atomicFailure -is [WinResizer.Configuration.ConfigurationPersistenceException]) 'Locked atomic Save did not report ConfigurationPersistenceException'
    } finally {
        $atomicLock.Dispose()
    }
    Assert-True ([IO.File]::ReadAllText($exportActive) -ceq $atomicBefore) 'Locked atomic Save damaged the previous final file'
    Assert-True ((Get-ChildItem (Split-Path $exportActive -Parent) -Filter '*.tmp' -ErrorAction SilentlyContinue).Count -eq 0) 'Atomic Save left a temporary file after failure'
    [WinResizer.Configuration.ConfigFactory]::Current.ProfileName = 'Export profile'
    [WinResizer.Configuration.ConfigFactory]::Save()
    [void](Read-Json $exportActive)

    # Log output is beside the configured executable base and logging failures
    # never throw a second exception.
    $logType = ([Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.exe'))).GetType('WinResizer.Services.PortableLog', $true)
    $logFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
    $setLogDirectory = $logType.GetMethod('SetDirectory', $logFlags)
    $appendLog = $logType.GetMethod('Append', $logFlags)
    [void]$setLogDirectory.Invoke($null, [object[]]@([string]$portableBase))
    [void]$appendLog.Invoke($null, [object[]]@('portable log test'))
    $logPath = Join-Path $portableBase 'WinResizer.error.log'
    Assert-True ([IO.File]::Exists($logPath)) 'Portable log was not written beside the executable base'
    Assert-True ([IO.File]::ReadAllText($logPath).Contains('portable log test')) 'Portable log lost the appended message'
    $logFailureTarget = Join-Path $temp 'log-target-file'
    [IO.File]::WriteAllText($logFailureTarget, 'file')
    [void]$setLogDirectory.Invoke($null, [object[]]@([string]$logFailureTarget))
    [void]$appendLog.Invoke($null, [object[]]@('this must not throw'))

    # Source guards prevent accidental reintroduction of AppData/CWD path logic.
    $configFactorySource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Configuration/ConfigFactory.cs'))
    $configUtilsSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Base/ConfigUtils.cs'))
    $appSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer/App.xaml.cs'))
    Assert-True (!$configFactorySource.Contains('Environment.SpecialFolder.ApplicationData') -and !$configFactorySource.Contains('CurrentDirectory')) 'ConfigFactory still contains AppData/CWD resolution'
    Assert-True (!$configUtilsSource.Contains('Environment.SpecialFolder.ApplicationData') -and !$configUtilsSource.Contains('CurrentDirectory')) 'ConfigUtils still contains AppData/CWD resolution'
    Assert-True ($appSource.Contains('ConfigFactory.InitializePortablePath(AppContext.BaseDirectory)') -and !$appSource.Contains('Application.StartupPath')) 'WPF App does not use the portable config path'

    'PASS: portable resolver, first run, AppData isolation, existing and legacy configs, ThemePreference normalization, import transactionality and backup, failed import preservation, explicit CLI path, export semantics, atomic save, CWD/spaces/Unicode, and safe logging.'
}
finally {
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalAppData
    if ($null -ne $replacedHandler) {
        [WinResizer.Configuration.ConfigFactory]::remove_ConfigurationReplaced($replacedHandler)
    }
    if ($null -ne $store) {
        $store.Dispose()
    }
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath((Join-Path $temp 'cleanup.config.json'))
    if ([IO.Directory]::Exists($temp)) {
        [IO.Directory]::Delete($temp, $true)
    }
}
