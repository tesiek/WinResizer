$ErrorActionPreference = 'Stop'

$presetsViewPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\PresetsView.xaml'
[xml]$presetsView = Get-Content -LiteralPath $presetsViewPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($presetsView.NameTable)
$namespaceManager.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaceManager.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$namespaceManager.AddNamespace('ui', 'http://schemas.lepo.co/wpfui/2022/xaml')

$activeColumn = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@Header="Active"]', $namespaceManager)
$activeCheckBox = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@Header="Active"]//wpf:CheckBox', $namespaceManager)
$setKeyButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Set Key"]', $namespaceManager)
$captureButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Capture"]', $namespaceManager)
$removeButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Remove"]', $namespaceManager)
$newPresetButton = $presetsView.SelectSingleNode('//ui:Button[@Content="New Preset"]', $namespaceManager)
$captureWindowButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Capture Window"]', $namespaceManager)
$dataGrid = $presetsView.SelectSingleNode('//ui:DataGrid', $namespaceManager)

if ($null -eq $activeColumn -or
    $activeColumn.GetAttribute('IsReadOnly') -ne 'True' -or
    $null -eq $activeCheckBox -or
    $activeCheckBox.GetAttribute('IsChecked') -ne '{Binding Enabled, Mode=OneWay}' -or
    $activeCheckBox.GetAttribute('Click') -ne 'Active_Click' -or
    $null -eq $setKeyButton -or
    $setKeyButton.GetAttribute('Click') -ne 'SetKey_Click' -or
    $null -eq $captureButton -or $captureButton.GetAttribute('Click') -ne 'Capture_Click' -or $captureButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $newPresetButton -or $newPresetButton.GetAttribute('Click') -ne 'NewPreset_Click' -or $newPresetButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $captureWindowButton -or $captureWindowButton.GetAttribute('Click') -ne 'CaptureWindow_Click' -or $captureWindowButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $removeButton -or $removeButton.GetAttribute('Click') -ne 'Remove_Click' -or
    $null -eq $dataGrid) {
    throw 'Presets XAML is not wired to the required runtime actions.'
}

$presetsViewCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Views\PresetsView.xaml.cs') -Raw
$presetsViewModelCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Presentation\PresetsViewModel.cs') -Raw
$dialogCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Views\HotkeyCaptureDialog.xaml.cs') -Raw
$hotkeysViewCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Views\HotkeysView.xaml.cs') -Raw
$hotkeysViewXaml = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Views\HotkeysView.xaml') -Raw

if ($presetsViewCode -match 'RegisterHotKey|UnRegisterHotKey' -or
    $presetsViewModelCode -match 'ConfigFactory|RegisterHotKey|UnRegisterHotKey' -or
    $presetsViewCode -notmatch 'SetPresetEnabled\(row, enabled\)' -or
    $presetsViewCode -notmatch 'SetPresetHotkey\(row, null\)' -or
    $presetsViewModelCode -notmatch 'GetPresets\(currentProfile.Id\)' -or
    $presetsViewModelCode -notmatch 'ProfileId' -or
    $presetsViewModelCode -notmatch 'PresetId' -or
    $presetsViewModelCode -notmatch 'preset\.Y' -or
    $presetsViewModelCode -notmatch 'preset\.X' -or
    $dialogCode -notmatch 'HotkeyCaptureResult' -or
    $dialogCode -notmatch 'allowClear = false' -or
    $hotkeysViewCode -notmatch 'allowClear:\s*true' -or
    $hotkeysViewCode -notmatch 'SetHotkey\(row, null\)' -or
    $hotkeysViewCode -match 'RegisterHotKey|UnRegisterHotKey' -or
    $hotkeysViewXaml -match 'Content="Clear"') {
    throw 'Presets runtime wiring or Global Hotkeys compatibility checks failed.'
}

Write-Output 'PASS: Presets use runtime IDs, explicit Active click and Set Key/Clear; Global Hotkeys share dialog Clear through Runtime.'

$project = Join-Path $PSScriptRoot 'Stage4CBPresetsWpfHarness/Stage4CBPresetsWpfHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 4C-B Presets WPF harness failed.'
}
