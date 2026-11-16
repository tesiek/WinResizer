$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$assemblyPath = Join-Path $root 'src\WinResizer.Common\bin\x64\Release\net47\WinResizer.Common.dll'
if (!(Test-Path -LiteralPath $assemblyPath)) {
    $assemblyPath = Join-Path $root 'src\WinResizer.Common\bin\Release\net47\WinResizer.Common.dll'
}
if (!(Test-Path -LiteralPath $assemblyPath)) {
    throw "WinResizer.Common.dll was not found at the expected Release output paths."
}

[void][Reflection.Assembly]::LoadFrom($assemblyPath)

function New-Hotkey([string[]] $modifiers, [string] $key) {
    $hotkey = New-Object WinResizer.Common.Shortcuts.Hotkeys
    foreach ($modifier in $modifiers) {
        [void]$hotkey.ModifierKeys.Add($modifier)
    }
    $hotkey.Key = $key
    return $hotkey
}

function Assert-Display([string[]] $modifiers, [string] $key, [string] $expected) {
    $actual = (New-Hotkey $modifiers $key).ToKeysString()
    if ($actual -ne $expected) {
        throw "Expected $expected, got $actual for modifiers [$($modifiers -join ',')]."
    }
}

Assert-Display @() 'S' 'S'
Assert-Display @('Ctrl') 'S' 'Ctrl+S'
Assert-Display @('Alt', 'Ctrl') 'S' 'Ctrl+Alt+S'
Assert-Display @('Ctrl', 'Win', 'Alt') 'S' 'Win+Ctrl+Alt+S'
Assert-Display @('Shift', 'Win', 'Alt', 'Ctrl') 'S' 'Win+Ctrl+Alt+Shift+S'
Assert-Display @('RWin', 'LWin', 'Ctrl') 'S' 'Win+Ctrl+S'
Assert-Display @('Shift', 'Alt', 'Win', 'Ctrl') 'K' 'Win+Ctrl+Alt+Shift+K'
Assert-Display @('shift', 'WIN', 'alt', 'CTRL') 'K' 'Win+Ctrl+Alt+Shift+K'

Write-Output 'PASS: canonical hotkey display order and separator are independent of HashSet insertion order.'
