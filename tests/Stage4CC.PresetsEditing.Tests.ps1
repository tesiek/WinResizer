$ErrorActionPreference = 'Stop'

$presetsViewPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\PresetsView.xaml'
[xml]$presetsView = Get-Content -LiteralPath $presetsViewPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($presetsView.NameTable)
$namespaceManager.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaceManager.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$namespaceManager.AddNamespace('ui', 'http://schemas.lepo.co/wpfui/2022/xaml')

$newPresetButton = $presetsView.SelectSingleNode('//ui:Button[@Content="New Preset"]', $namespaceManager)
$captureWindowButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Capture Window"]', $namespaceManager)
$captureButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Capture"]', $namespaceManager)
$removeButton = $presetsView.SelectSingleNode('//ui:Button[@Content="Remove"]', $namespaceManager)
$dataGrid = $presetsView.SelectSingleNode('//ui:DataGrid', $namespaceManager)
$nameColumn = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@x:Name="PresetNameColumn"]', $namespaceManager)
$topColumn = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@x:Name="TopColumn"]', $namespaceManager)
$leftColumn = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@x:Name="LeftColumn"]', $namespaceManager)
$widthColumn = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@x:Name="WidthColumn"]', $namespaceManager)
$heightColumn = $presetsView.SelectSingleNode('//wpf:DataGridTemplateColumn[@x:Name="HeightColumn"]', $namespaceManager)

$editableColumns = @($nameColumn, $topColumn, $leftColumn, $widthColumn, $heightColumn)
if ($null -eq $newPresetButton -or
    $newPresetButton.GetAttribute('Click') -ne 'NewPreset_Click' -or
    $newPresetButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $captureWindowButton -or $captureWindowButton.GetAttribute('Click') -ne 'CaptureWindow_Click' -or $captureWindowButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $captureButton -or $captureButton.GetAttribute('Click') -ne 'Capture_Click' -or $captureButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $removeButton -or $removeButton.GetAttribute('Click') -ne 'Remove_Click' -or
    $removeButton.GetAttribute('IsEnabled') -eq 'False' -or
    $null -eq $dataGrid -or $dataGrid.GetAttribute('CellEditEnding') -ne 'PresetsGrid_CellEditEnding' -or
    $dataGrid.GetAttribute('PreparingCellForEdit') -ne 'PresetsGrid_PreparingCellForEdit' -or
    $dataGrid.GetAttribute('RowEditEnding') -ne 'PresetsGrid_RowEditEnding' -or
    $editableColumns.Count -ne 5 -or
    ($editableColumns | Where-Object {
        $null -eq $_ -or $_.GetAttribute('IsReadOnly') -eq 'True' -or
        [string]::IsNullOrWhiteSpace($_.GetAttribute('SortMemberPath')) -or
        $null -eq $_.SelectSingleNode('wpf:DataGridTemplateColumn.CellEditingTemplate//wpf:TextBox[@x:Name="PresetEditor"]', $namespaceManager)
    }).Count -ne 0 -or
    $presetsView.InnerText -match 'Rename') {
    throw 'Presets Add/Remove/inline editing XAML contract is invalid.'
}

$presetsViewCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Views\PresetsView.xaml.cs') -Raw
$presetsViewModelCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Presentation\PresetsViewModel.cs') -Raw
if ($presetsViewCode -notmatch 'DispatcherPriority\.Background' -or
    $presetsViewCode -notmatch 'PresetEditSnapshot' -or
    $presetsViewCode -notmatch 'TryParseInteger' -or
    $presetsViewCode -notmatch 'ProfileId' -or
    $presetsViewCode -notmatch 'PresetId' -or
    $presetsViewModelCode -notmatch 'AddPreset\(' -or
    $presetsViewModelCode -notmatch 'UpdatePreset\(' -or
    $presetsViewModelCode -notmatch 'RemovePreset\(' -or
    $presetsViewModelCode -notmatch 'IEditableCollectionView' -or
    $presetsViewModelCode -match 'ConfigFactory|WindowPreset') {
    throw 'Presets Add/Remove/inline editing runtime contract is invalid.'
}

Write-Output 'PASS: Presets XAML exposes Add/Remove/Capture, with ID-safe deferred inline editing and no Rename.'

$project = Join-Path $PSScriptRoot 'Stage4CCPresetsEditingHarness/Stage4CCPresetsEditingHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 4C-C Presets editing harness failed.'
}
