$ErrorActionPreference = 'Stop'
& dotnet run --project (Join-Path $PSScriptRoot 'PersistentWpfThemeHarness/PersistentWpfThemeHarness.csproj') -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Persistent WPF Theme harness failed.' }
