$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ConfigCallbackFailureCharacterizationHarness/ConfigCallbackFailureCharacterizationHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Config callback failure characterization harness failed.'
}
