$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ProfileFailureCharacterizationHarness/ProfileFailureCharacterizationHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Profile failure characterization harness failed.'
}
