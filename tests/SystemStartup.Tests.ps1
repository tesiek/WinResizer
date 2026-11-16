$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Get-Content (Join-Path $root 'src/WinResizer/App.xaml.cs') -Raw
if ($app -notmatch 'try\s*\{\s*StartupService = SystemStartupService.CreateForCurrentHost\(\);\s*StartupService.RepairPathIfEnabled\(\);\s*\}\s*catch \(Exception exception\)' -or
    $app.IndexOf('StartupService.RepairPathIfEnabled') -gt $app.IndexOf('new SettingsWindowController')) {
    throw 'Startup repair is not independent of About or is not protected against failures.'
}
$views = Join-Path $root 'src/WinResizer/Views'
$startupViews = @(Get-ChildItem $views -Filter '*.xaml' | Where-Object { (Get-Content $_.FullName -Raw).Contains('Run on system startup') })
if ($startupViews.Count -ne 1 -or $startupViews[0].Name -ne 'SettingsView.xaml') { throw 'Startup must exist only in Settings.' }
& dotnet run --project (Join-Path $PSScriptRoot 'SystemStartupHarness/SystemStartupHarness.csproj') -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'System startup harness failed.' }
