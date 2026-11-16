$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'Stage4CAPresetsRuntimeHarness/Stage4CAPresetsRuntimeHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 4C-A Presets runtime harness failed.'
}
