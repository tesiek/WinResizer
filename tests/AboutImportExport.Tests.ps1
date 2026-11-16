$ErrorActionPreference = 'Stop'
& dotnet run --project (Join-Path $PSScriptRoot 'AboutImportExportHarness/AboutImportExportHarness.csproj') -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'About Import/Export harness failed.' }
