$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ProcessesRuntimeHarness/ProcessesRuntimeHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Processes runtime harness failed.' }
