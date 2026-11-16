$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/WinResizer/bin/x64/Release/net48'
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.Common.dll'))

function New-Hotkey($modifier, $key) {
    $hotkey = New-Object WinResizer.Common.Shortcuts.Hotkeys
    foreach ($value in $modifier) { [void]$hotkey.ModifierKeys.Add($value) }
    $hotkey.Key = $key
    return $hotkey
}

$canonical = New-Hotkey @('Ctrl', 'Alt') 'Oemtilde'
$differentCase = New-Hotkey @('ctrl', 'alt') 'oemTilde'
$duplicateCase = New-Hotkey @('Ctrl', 'ctrl', 'Alt') 'OEMTILDE'
$differentKey = New-Hotkey @('Ctrl', 'Alt') 'OemOpenBrackets'

if (!$canonical.Equals($differentCase)) { throw 'Hotkeys equality is case-sensitive' }
if (!$canonical.Equals($duplicateCase)) { throw 'Case-variant duplicate modifiers are not normalized' }
if ($canonical.GetHashCode() -ne $differentCase.GetHashCode()) { throw 'Equal hotkeys have different hash codes' }
if ($canonical.GetHashCode() -ne $duplicateCase.GetHashCode()) { throw 'Normalized hotkeys have different hash codes' }
if ($canonical.Equals($differentKey)) { throw 'Different hotkeys compare equal' }

'PASS: hotkey equality and hash code are case-insensitive and consistent.'
