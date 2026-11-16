$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HotkeyRecordingHarness/HotkeyRecordingHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Current WPF hotkey recorder harness failed.' }
