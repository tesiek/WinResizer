$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SaveFailureCharacterizationHarness/SaveFailureCharacterizationHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Save failure characterization harness failed.'
}
