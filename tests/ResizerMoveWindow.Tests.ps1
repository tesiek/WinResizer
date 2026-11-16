$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'WinResizer.Common','WinResizer.Core') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin "$name.dll"))
}

$form = New-Object System.Windows.Forms.Form
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedToolWindow
$form.ShowInTaskbar = $false
$form.Size = New-Object System.Drawing.Size(200, 150)
$form.Location = New-Object System.Drawing.Point(100, 100)

try {
    [void]$form.CreateControl()
    [void]$form.Show()
    [System.Windows.Forms.Application]::DoEvents()

    $target = [WinResizer.Common.Windows.Rect]::new(120, 130, 420, 330)
    $result = [WinResizer.Core.WindowControl.Resizer]::MoveWindow($form.Handle, $target)
    if (!$result) { throw 'MoveWindow reported failure for a valid window handle' }

    $actual = [WinResizer.Core.WindowControl.Resizer]::GetRect($form.Handle)
    if ($actual.Left -ne $target.Left -or $actual.Top -ne $target.Top -or
        $actual.Right -ne $target.Right -or $actual.Bottom -ne $target.Bottom) {
        throw "Window rectangle mismatch: expected $target, got $actual"
    }

    if ([WinResizer.Core.WindowControl.Resizer]::MoveWindow([IntPtr]::Zero, $target)) {
        throw 'MoveWindow accepted a zero window handle'
    }

    'PASS: MoveWindow reports success for a valid handle and failure for an invalid handle.'
}
finally {
    if ($null -ne $form) {
        $form.Close()
        $form.Dispose()
    }
}
