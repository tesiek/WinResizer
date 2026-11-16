$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json','WinResizer.Common','WinResizer.Configuration') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin "$name.dll"))
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-import-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
try {
    $saved = Join-Path $temp 'saved.json'
    $inputFile = Join-Path $temp 'import.json'
    [WinResizer.Configuration.ConfigFactory]::SetPath($saved, (Join-Path $temp 'portable.json'))
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $original = [WinResizer.Configuration.ConfigFactory]::Current
    $preset = New-Object WinResizer.Configuration.WindowPreset
    $preset.Name = 'Keep preset'
    $original.Presets.Add($preset)
    [WinResizer.Configuration.ConfigFactory]::Save()
    $before = [IO.File]::ReadAllText($saved)
    $cases = @('{broken', 'null', '{}', '{"Configs":[]}', '{"Configs":[null]}',
        '{"Configs":[{"ProfileId":"bad","WindowSizes":null}]}',
        '{"Configs":[{"ProfileId":"bad","Presets":[null]}]}')
    $nestedCases = @(
        '"Presets":[{"Hotkey":{"ModifierKeys":null,"Key":"A"}}]',
        '"Keys":{"Save":{"ModifierKeys":null,"Key":"S"}}',
        '"Presets":[{"Hotkey":{"ModifierKeys":[null],"Key":"A"}}]',
        '"Presets":[{"Hotkey":{"ModifierKeys":["Bogus"],"Key":"A"}}]',
        '"Presets":[{"Hotkey":{"ModifierKeys":["Ctrl"],"Key":null}}]',
        '"WindowSizes":[{"Name":null,"Title":"*"}]',
        '"WindowSizes":[{"Name":"firefox.exe","Title":null}]',
        '"ProfileName":null',
        '"Presets":[{"Name":null}]'
    )
    foreach ($fields in $nestedCases) {
        $cases += '{"CurrentProfileId":"bad","Configs":[{"ProfileId":"bad",' + $fields + '}]}'
    }
    $cases += '{"CurrentProfileId":"bad","Configs":[{"ProfileId":"bad","WindowSizes":[],"Keys":{"99":{"ModifierKeys":["Ctrl"],"Key":"A"}}}]}'
    $cases += '{"CurrentProfileId":"bad","Configs":[{"ProfileId":"bad","WindowSizes":[],"Presets":[{"Id":"same","Name":"one"},{"Id":"same","Name":"two"}]}]}'
    $cases += '{"WindowSizes":[],"SaveKey":{"ModifierKeys":null,"Key":"S"}}'
    $cases += '{"WindowSizes":[{"Name":null,"Title":"*"}]}'
    foreach ($json in $cases) {
        [IO.File]::WriteAllText($inputFile, $json)
        $failed = $false
        try { [WinResizer.Configuration.ConfigFactory]::Load($inputFile) } catch { $failed = $true }
        if (!$failed) { throw "Invalid input accepted: $json" }
        if (![object]::ReferenceEquals($original, [WinResizer.Configuration.ConfigFactory]::Current)) {
            throw 'Active profile changed after failure'
        }
        [WinResizer.Configuration.ConfigFactory]::Save()
        if ([IO.File]::ReadAllText($saved) -cne $before) { throw 'Configuration changed after failed import and subsequent Save' }
    }
    try { [WinResizer.Configuration.ConfigFactory]::Load((Join-Path $temp 'missing.json')); throw 'Missing input accepted' }
    catch [System.Management.Automation.MethodInvocationException] {}
    if (![object]::ReferenceEquals($original, [WinResizer.Configuration.ConfigFactory]::Current)) { throw 'Missing file changed state' }
    [IO.File]::WriteAllText($inputFile, '{"CurrentProfileId":"new","Configs":[{"ProfileId":"new","ProfileName":"Imported","WindowSizes":[],"Presets":null}]}')
    [WinResizer.Configuration.ConfigFactory]::Load($inputFile)
    if ([WinResizer.Configuration.ConfigFactory]::Current.ProfileId -ne 'new') { throw 'Modern import failed' }
    if ($null -eq [WinResizer.Configuration.ConfigFactory]::Current.Presets) { throw 'Presets compatibility failed' }
    [IO.File]::WriteAllText($inputFile, '{"DisableInFullScreen":false,"RestoreAllIncludeMinimized":true,"NotifyOnSaved":true,"EnableResizeByTitle":false,"EnableAutoResizeDelay":true,"WindowSizes":[],"SaveKey":{"ModifierKeys":["Ctrl","Alt"],"Key":"S"}}')
    [WinResizer.Configuration.ConfigFactory]::Load($inputFile)
    if ([WinResizer.Configuration.ConfigFactory]::Current.ProfileName -ne 'default') { throw 'Legacy migration failed' }
    if ([WinResizer.Configuration.ConfigFactory]::Current.Keys.Count -ne 1) { throw 'Legacy hotkey migration failed' }
    $legacy = [WinResizer.Configuration.ConfigFactory]::Current
    if ($legacy.DisableInFullScreen -or !$legacy.RestoreAllIncludeMinimized -or !$legacy.NotifyOnSaved -or
        $legacy.EnableResizeByTitle -or !$legacy.EnableAutoResizeDelay) { throw 'Legacy profile options migration failed' }
    'PASS: 20 invalid JSON/structure/nested-data cases, unknown hotkey type, duplicate preset IDs, missing file, subsequent Save preserves data, modern import, null Presets, legacy migration.'
}
finally {
    # Only remove files created by this test, never recurse into user directories.
    foreach ($file in 'saved.json','import.json','WinResizer.config.pre-import.bak') {
        $target = Join-Path $temp $file
        if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
    }
    [IO.Directory]::Delete($temp)
}
