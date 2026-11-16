$ErrorActionPreference = 'Stop'

# The current WPF harness verifies profile option controls, bindings, refresh guards,
# persistence failures, and import presentation. Keep that behavior covered here.
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Stage4BHotkeys.Tests.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Current WPF profile option controls failed.' }

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-options-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$path = Join-Path $temp 'WinResizer.config.json'
$runtime = $null
try {
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath($path)
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $options = New-Object WinResizer.Runtime.WinResizerRuntimeOptions
    $options.EnableGlobalHotkeys = $false
    $options.EnableWindowMonitoring = $false
    $runtime = New-Object WinResizer.Runtime.WinResizerRuntime($options)
    $runtime.Start()
    $aId = [WinResizer.Configuration.ConfigFactory]::Current.ProfileId
    $bId = $runtime.AddProfile('Other').Id
    $cases = @(
        @{ Name='NotifyOnSaved'; Get={ $runtime.GetNotifyOnSaved() }; Set={ param($v) $runtime.SetNotifyOnSaved($v) } },
        @{ Name='RestoreAllIncludeMinimized'; Get={ $runtime.GetRestoreAllIncludeMinimized() }; Set={ param($v) $runtime.SetRestoreAllIncludeMinimized($v) } },
        @{ Name='DisableInFullScreen'; Get={ $runtime.GetDisableInFullScreen() }; Set={ param($v) $runtime.SetDisableInFullScreen($v) } },
        @{ Name='EnableResizeByTitle'; Get={ $runtime.GetEnableResizeByTitle() }; Set={ param($v) $runtime.SetEnableResizeByTitle($v) } },
        @{ Name='EnableAutoResizeDelay'; Get={ $runtime.GetEnableAutoResizeDelay() }; Set={ param($v) $runtime.SetEnableAutoResizeDelay($v) } }
    )
    function Assert-Options([bool]$expected) {
        foreach ($case in $cases) {
            if ((& $case.Get) -ne $expected) { throw "Runtime option $($case.Name) did not follow the active profile." }
            if ([WinResizer.Configuration.ConfigFactory]::Current.($case.Name) -ne $expected) { throw "Live option $($case.Name) differs from Runtime." }
        }
    }
    foreach ($case in $cases) { & $case.Set $false }
    Assert-Options $false
    [void]$runtime.SwitchProfile($bId)
    foreach ($case in $cases) { & $case.Set $true }
    Assert-Options $true
    $stored = [Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($path))
    $storedB = @($stored.Configs | Where-Object { $_.ProfileId.ToString() -eq $bId })[0]
    if ($null -eq $storedB) { throw "Profile B was not persisted: expected $bId; ids $(@($stored.Configs | ForEach-Object { $_.ProfileId }))" }
    foreach ($case in $cases) {
        if ($storedB[$case.Name].ToString() -ne 'True') { throw "Profile B option $($case.Name) was not persisted." }
    }
    [void]$runtime.SwitchProfile($aId)
    Assert-Options $false
    [void]$runtime.SwitchProfile($bId)
    Assert-Options $true
    [void]$runtime.SwitchProfile($aId)
    Assert-Options $false
    $before = [IO.File]::ReadAllText($path)
    $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $failed = $false
        try { $runtime.SetEnableAutoResizeDelay($true) } catch { $failed = $true }
        if (!$failed) { throw 'Locked persistence did not fail.' }
        Assert-Options $false
    }
    finally { $lock.Dispose() }
    if ([IO.File]::ReadAllText($path) -cne $before) { throw 'Failed Save changed persisted options.' }
    $runtime.SetEnableAutoResizeDelay($true)
    if (!$runtime.GetEnableAutoResizeDelay()) { throw 'User edit did not persist after failed Save.' }

    $import = [Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($path))
    $importA = @($import.Configs | Where-Object { $_.ProfileId.ToString() -eq $aId })[0]
    $importA['NotifyOnSaved'] = $true
    $importA['EnableAutoResizeDelay'] = $false
    $importPath = Join-Path $temp 'import.json'
    [IO.File]::WriteAllText($importPath, $import.ToString())
    [WinResizer.Configuration.ConfigFactory]::Load($importPath)
    if (!$runtime.GetNotifyOnSaved() -or $runtime.GetEnableAutoResizeDelay()) { throw 'Same-profile import did not refresh Runtime options.' }
    $savedImport = [Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($path))
    $savedA = @($savedImport.Configs | Where-Object { $_.ProfileId.ToString() -eq $aId })[0]
    if ($savedA['NotifyOnSaved'].ToString() -ne 'True' -or $savedA['EnableAutoResizeDelay'].ToString() -ne 'False') {
        throw 'Imported options were not persisted in portable config.'
    }
    'PASS: current WPF option controls plus Runtime A-B-A scope, five persisted options, rollback, post-failure edit, and same-profile import.'
}
finally {
    if ($null -ne $runtime) { $runtime.Dispose() }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
