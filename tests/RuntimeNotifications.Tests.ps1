$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Stage4B1RuntimeHarness/Stage4B1RuntimeHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 4B.1 runtime notification harness failed.'
}
