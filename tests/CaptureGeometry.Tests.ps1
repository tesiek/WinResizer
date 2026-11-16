$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'CaptureGeometryHarness/CaptureGeometryHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Capture geometry harness failed.'
}
