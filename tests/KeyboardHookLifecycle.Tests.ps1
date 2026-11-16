$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$hookSource = Get-Content -LiteralPath (Join-Path $root 'src/WinResizer.Core/Shortcuts/GlobalKeyboardHook.cs') -Raw

if ($hookSource -notmatch 'if \(hooked == IntPtr\.Zero\)' -or
    $hookSource -notmatch 'throw new Win32Exception\(Marshal\.GetLastWin32Error\(\)\)') {
    throw 'SetWindowsHookEx failure is not converted to a Win32Exception before storing the handle.'
}

$project = Join-Path $PSScriptRoot 'KeyboardHookLifecycleHarness/KeyboardHookLifecycleHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) { throw 'Keyboard hook lifecycle harness failed.' }
