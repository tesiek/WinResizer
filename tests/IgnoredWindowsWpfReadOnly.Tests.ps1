$ErrorActionPreference = 'Stop'

$viewPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\IgnoredWindowsView.xaml'
[xml]$view = Get-Content -LiteralPath $viewPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($view.NameTable)
$namespaceManager.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaceManager.AddNamespace('ui', 'http://schemas.lepo.co/wpfui/2022/xaml')

$dataGrid = $view.SelectSingleNode('//ui:DataGrid', $namespaceManager)
$allColumns = @($view.SelectNodes('//ui:DataGrid.Columns/*', $namespaceManager))
$columns = @($allColumns | Where-Object { $_.GetAttribute('Header') -ne '' })
$headers = @($columns | ForEach-Object { $_.GetAttribute('Header') })
$expectedHeaders = @('Active', 'Process', 'Class', 'Title', 'TitleMatch')
if ($null -eq $dataGrid -or
    $dataGrid.GetAttribute('ItemsSource') -ne '{Binding IgnoredWindows}' -or
    $columns.Count -ne 5 -or $allColumns.Count -ne 6 -or
    ($headers -join '|') -cne ($expectedHeaders -join '|')) {
    throw 'Ignored Windows WPF view does not expose the required five read-only columns.'
}

foreach ($header in @('Process', 'Class', 'Title')) {
    $column = $columns | Where-Object { $_.GetAttribute('Header') -eq $header }
    if ($column.GetAttribute('SortMemberPath') -ne $header -or
        $null -eq $column.SelectSingleNode('./wpf:DataGridTemplateColumn.CellEditingTemplate//wpf:TextBox', $namespaceManager)) {
        throw "$header has no inline editor."
    }
}
if ($null -eq $view.SelectSingleNode('//wpf:CheckBox[@Click="Active_Click"]', $namespaceManager) -or
    $null -eq $view.SelectSingleNode('//wpf:ComboBox[@SelectionChanged="TitleMatch_SelectionChanged"]', $namespaceManager)) {
    throw 'Active/TitleMatch are not wired.'
}

$settingsCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\SettingsPrototypeWindow.xaml.cs') -Raw
$viewModelCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Presentation\IgnoredWindowsViewModel.cs') -Raw
$modelsCodePath = Join-Path $PSScriptRoot '..\src\WinResizer\PrototypeModels.cs'
if ($settingsCode -match 'IgnoredWindowRow|Sample ignored window|TitleMatchOptions' -or
    (Test-Path -LiteralPath $modelsCodePath) -or
    $viewModelCode -notmatch 'GetIgnoredWindows\(' -or
    $viewModelCode -notmatch 'IgnoredWindowsChanged' -or
    $viewModelCode -notmatch 'BeginInvoke' -or
    $viewModelCode -notmatch 'IgnoredWindowToken' -or
    $viewModelCode -match 'ProfilesChanged') {
    throw 'Ignored Windows WPF read-only ViewModel is not wired to the global Runtime lifecycle.'
}

if ($null -eq $view.SelectSingleNode('//ui:Button[@Content="Remove" and @Click="Remove_Click"]', $namespaceManager) -or
    $null -eq $view.SelectSingleNode('//ui:Button[@Content="Add" and @Click="Add_Click"]', $namespaceManager) -or
    $null -eq $view.SelectSingleNode('//ui:Button[@Content="Capture Window" and @Click="Capture_Click"]', $namespaceManager)) {
    throw 'Ignored Windows sample actions are still active.'
}

Write-Output 'PASS: Ignored Windows exposes five runtime-backed columns with explicit editing without sample actions.'

$project = Join-Path $PSScriptRoot 'IgnoredWindowsWpfReadOnlyHarness/IgnoredWindowsWpfReadOnlyHarness.csproj'
$friendTargets = Join-Path $PSScriptRoot 'WinResizer.PrivateHarnesses.targets'
& dotnet run --project $project -c Release -p:PlatformTarget=x64 "-p:CustomAfterMicrosoftCommonTargets=$friendTargets" -p:OutputPath=bin\x64\Release\net48\ -p:AppendTargetFrameworkToOutputPath=false -p:IntermediateOutputPath=obj\x64\Release\net48\
if ($LASTEXITCODE -ne 0) {
    throw 'Ignored Windows WPF read-only harness failed.'
}
