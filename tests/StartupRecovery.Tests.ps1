$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-recovery-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$path = Join-Path $temp 'config.json'
$created = New-Object 'System.Collections.Generic.List[string]'
$created.Add($path)
try {
    [WinResizer.Configuration.ConfigFactory]::SetPath($path, (Join-Path $temp 'portable.json'))
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $broken = '{"Configs": [ damaged'
    [IO.File]::WriteAllText($path, $broken)
    $failed = $false
    try { [WinResizer.Configuration.ConfigFactory]::Load() } catch { $failed = $true }
    if (!$failed) { throw 'Broken config accepted' }
    $backup = [WinResizer.Configuration.ConfigFactory]::RecoverFromLoadFailure()
    $created.Add($backup)
    if ([IO.File]::ReadAllText($path) -cne $broken -or [IO.File]::ReadAllText($backup) -cne $broken) { throw 'Original bytes not preserved' }
    [WinResizer.Configuration.ConfigFactory]::Save()
    if ([IO.File]::ReadAllText($backup) -cne $broken) { throw 'Later save overwrote backup' }
    [IO.File]::WriteAllText($path, $broken)
    $second = [WinResizer.Configuration.ConfigFactory]::RecoverFromLoadFailure()
    $created.Add($second)
    if ($backup -eq $second) { throw 'Repeated recovery reused backup' }
    $original = [WinResizer.Configuration.ConfigFactory]::Current
    $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $failed = $false
        try { [void][WinResizer.Configuration.ConfigFactory]::RecoverFromLoadFailure() } catch { $failed = $true }
        if (!$failed) { throw 'Backup failure ignored' }
        if (![object]::ReferenceEquals($original, [WinResizer.Configuration.ConfigFactory]::Current)) { throw 'Defaults applied despite backup failure' }
    } finally { $lock.Dispose() }
    'PASS: damaged original preserved, backup survives later Save, unique backups, failed backup prevents fallback.'
}
finally {
    # Nonrecursive cleanup confined to this test's unique temporary directory.
    foreach ($file in [IO.Directory]::GetFiles($temp)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($temp)
}
