$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($view in 'HotkeysView', 'PresetsView') {
    $source = Get-Content -LiteralPath (Join-Path $root "src/WinResizer/Views/$view.xaml.cs") -Raw
    if ($source -notmatch 'catch \(HotkeyNotAllowedException exception\)\s*\{\s*ViewModel.Refresh\(\);\s*ShowError\(exception.Message, HotkeyNotAllowedException.Title\);' -or
        $source -notmatch 'MessageBox.Show\([\s\S]*?message,\s*title,') {
        throw "$view does not refresh and display the dedicated policy error."
    }
}
Write-Output 'PASS: both WPF Set Key paths handle Hotkey Not Allowed separately from registration failures.'
& dotnet run --project (Join-Path $PSScriptRoot 'HotkeyAssignmentPolicyHarness/HotkeyAssignmentPolicyHarness.csproj') -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Hotkey assignment policy harness failed.' }
