$ErrorActionPreference = 'Stop'
$runnerName = Split-Path -Leaf $PSCommandPath

$tests = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.Tests.ps1' -File |
    Where-Object Name -ne $runnerName |
    Sort-Object Name)
$failed = @()
foreach ($test in $tests) {
    Write-Output "Running $($test.Name)"
    try {
        & pwsh -NoProfile -File $test.FullName
        if ($LASTEXITCODE -ne 0) { throw "Exit code $LASTEXITCODE" }
        Write-Output "PASS $($test.Name)"
    }
    catch {
        $failed += $test.Name
        Write-Output "FAIL $($test.Name): $_"
    }
}

Write-Output "$($tests.Count - $failed.Count)/$($tests.Count) PASS"
if ($tests.Count -eq 0) { Write-Error 'No test runners found.'; exit 1 }
if ($failed.Count -gt 0) {
    Write-Output "Failed: $($failed -join ', ')"
    exit 1
}
exit 0
