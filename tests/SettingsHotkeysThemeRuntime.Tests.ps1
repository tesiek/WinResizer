$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SettingsHotkeysThemeRuntimeHarness/SettingsHotkeysThemeRuntimeHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Settings/Hotkeys/Theme runtime harness failed.' }
