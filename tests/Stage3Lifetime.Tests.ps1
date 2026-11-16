$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'Stage3LifetimeHarness/Stage3LifetimeHarness.csproj'
$friendTargets = Join-Path $PSScriptRoot 'WinResizer.PrivateHarnesses.targets'

& dotnet run --project $project -c Release -p:PlatformTarget=x64 "-p:CustomAfterMicrosoftCommonTargets=$friendTargets" -- $root
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 3 lifetime harness failed.'
}
