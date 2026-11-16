$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'StatusBarActiveProfileHarness/StatusBarActiveProfileHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Status bar active profile harness failed.' }
