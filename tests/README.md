# WinResizer regression tests

This is the WinResizer regression/integration test suite.

## Requirements

- Windows x64 and PowerShell 7 (`pwsh`).
- .NET SDK / MSBuild compatible with the solution (the release workflow uses .NET SDK 8).
- .NET Framework 4.8 runtime and the targeting packs used by the solution (4.7, 4.7.2 and 4.8).
- NuGet access for the initial restore.

From the repository root, build Release x64 before running the full suite:

```powershell
dotnet restore .\WinResizer.sln -p:Configuration=Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet msbuild .\WinResizer.sln /m /t:Rebuild /p:Configuration=Release /p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
pwsh -NoProfile -File .\tests\Run-All.Tests.ps1
```

The expected summary is `39/39 PASS`. The runner executes each test sequentially in a separate PowerShell process and returns a nonzero exit code if any test fails. Harness projects restore and build through `dotnet run`; some scripts load the solution's existing Release output.

## Desktop tests

The full suite needs an interactive, unlocked Windows desktop. Some tests use WPF/WinForms windows, change focus, register global hotkeys, and install keyboard hooks. Run it in a separate session or VM with WinResizer closed, and avoid interacting with the desktop during the tests.

Tests use temporary configuration files and fixtures. Mixed-DPI, multi-monitor and Windows Snap behavior still require manual validation; the full suite is not a guarantee of headless CI compatibility.
