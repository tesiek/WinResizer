$ErrorActionPreference = 'Stop'

$viewPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\ProcessesView.xaml'
[xml]$view = Get-Content -LiteralPath $viewPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($view.NameTable)
$namespaceManager.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaceManager.AddNamespace('ui', 'http://schemas.lepo.co/wpfui/2022/xaml')

$dataGrid = $view.SelectSingleNode('//ui:DataGrid', $namespaceManager)
$allColumns = @($view.SelectNodes('//ui:DataGrid.Columns/*', $namespaceManager))
$columns = @($allColumns | Where-Object { $_.GetAttribute('Header') -ne '' })
$headers = @($columns | ForEach-Object { $_.GetAttribute('Header') })
$bindings = @($columns | ForEach-Object { $_.GetAttribute('Binding') })
$expectedHeaders = @('Process', 'Title', 'Top', 'Left', 'Right', 'Bottom', 'Auto', 'Delay')
if ($null -eq $dataGrid -or
    $dataGrid.GetAttribute('ItemsSource') -ne '{Binding Processes}' -or
    $columns.Count -ne 8 -or $allColumns.Count -ne 9 -or
    ($headers -join '|') -cne ($expectedHeaders -join '|') -or
    ($bindings -match '\b(Width|Height)\b').Count -gt 0) {
    throw 'Processes WPF view does not expose the required eight-column data contract.'
}

if ($null -eq $view.SelectSingleNode('//ui:Button[@Content="Add" and @Click="Add_Click"]', $namespaceManager) -or
    $null -eq $view.SelectSingleNode('//wpf:DataGridTemplateColumn[@IsReadOnly="True" and @CanUserSort="False"]//ui:Button[@Content="Remove" and @Click="Remove_Click"]', $namespaceManager)) {
    throw 'Processes Add/Remove actions are not wired.'
}

foreach ($header in @('Process', 'Title', 'Top', 'Left', 'Right', 'Bottom', 'Delay')) {
    $column = $columns | Where-Object { $_.GetAttribute('Header') -eq $header }
    if ($column.GetAttribute('IsReadOnly') -eq 'True' -or
        $column.GetAttribute('SortMemberPath') -ne $header -or
        $null -eq $column.SelectSingleNode('./wpf:DataGridTemplateColumn.CellEditingTemplate//wpf:TextBox', $namespaceManager)) {
        throw "$header must have an inline editor with its own field identity."
    }
}
$auto = $view.SelectSingleNode('//wpf:CheckBox[@Click="Auto_Click"]', $namespaceManager)
if ($null -eq $auto -or $auto.GetAttribute('IsChecked') -ne '{Binding Auto, Mode=OneWay}') {
    throw 'Auto checkbox is not wired to the explicit runtime operation.'
}

$settingsCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\SettingsPrototypeWindow.xaml.cs') -Raw
$viewModelCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\WinResizer\Presentation\ProcessesViewModel.cs') -Raw
if ($settingsCode -match 'ProcessRow|Processes\.Add' -or
    $viewModelCode -notmatch 'GetProcesses\(' -or
    $viewModelCode -notmatch 'ProcessesChanged' -or
    $viewModelCode -notmatch 'ProfilesChanged' -or
    $viewModelCode -notmatch 'BeginInvoke' -or
    $viewModelCode -notmatch 'ProcessToken') {
    throw 'Processes WPF read-only ViewModel is not wired to the Runtime API/lifecycle.'
}

Write-Output 'PASS: Processes keeps eight data columns, seven explicit inline editors and Auto click.'

$project = Join-Path $PSScriptRoot 'ProcessesWpfReadOnlyHarness/ProcessesWpfReadOnlyHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Processes WPF read-only harness failed.'
}
