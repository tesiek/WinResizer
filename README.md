# WinResizer

WinResizer is a portable Windows 10/11 x64 utility built with WPF and .NET Framework 4.8. It saves and restores window positions and sizes, applies reusable presets, and automatically resizes matched windows.

WinResizer is distributed as a portable application. Extract the release ZIP and run `WinResizer.exe` directly.

## Portable configuration

On first run, the production application creates its configuration beside the executable:

```text
WinResizer.config.json
```

The portable release ZIP intentionally does not contain this file. Extracting a newer release over an existing directory therefore leaves the user's configuration untouched.

## Build

Requirements:

- Windows x64
- .NET Framework 4.8 developer tools
- Visual Studio 2022 or compatible MSBuild

Production WPF build:

```powershell
dotnet build .\src\WinResizer\WinResizer.csproj -c Release -f net48 -p:Platform=x64 -p:PlatformTarget=x64
```

Expected executable:

```text
src\WinResizer\bin\x64\Release\net48\WinResizer.exe
```

Solution:

```text
WinResizer.sln
```

## CLI

The repository also contains the `WinResizer.CLI` source project. The CLI is not included in the standard portable WinResizer release.

```powershell
WinResizer.CLI.exe resize --help
```

## Upstream and license

WinResizer is derived from the original [caoyue/WindowResizer](https://github.com/caoyue/WindowResizer) project. The original project name, repository URL, author attribution, and MIT license are retained where they describe the upstream work.
