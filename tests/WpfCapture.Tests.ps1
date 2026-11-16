$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'WpfCaptureHarness/WpfCaptureHarness.csproj'
$friendTargets = Join-Path $PSScriptRoot 'WinResizer.PrivateHarnesses.targets'
& dotnet run --project $project -c Release -p:PlatformTarget=x64 "-p:CustomAfterMicrosoftCommonTargets=$friendTargets"
if ($LASTEXITCODE -ne 0) { throw 'WPF Capture harness failed.' }
