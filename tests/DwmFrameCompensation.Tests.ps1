$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& dotnet run --project (Join-Path $PSScriptRoot 'DwmFrameCompensationHarness/DwmFrameCompensationHarness.csproj') -c Release -p:PlatformTarget=x64 -- $root
if ($LASTEXITCODE -ne 0) { throw 'DWM frame compensation harness failed.' }
