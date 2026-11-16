$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'IgnoredWindowsRuntimeHarness/IgnoredWindowsRuntimeHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Ignored Windows runtime harness failed.' }
