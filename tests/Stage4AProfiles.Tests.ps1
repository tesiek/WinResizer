$ErrorActionPreference = 'Stop'
$profilesViewPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\ProfilesView.xaml'
[xml]$profilesView = Get-Content -LiteralPath $profilesViewPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($profilesView.NameTable)
$namespaceManager.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaceManager.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$namespaceManager.AddNamespace('ui', 'http://schemas.lepo.co/wpfui/2022/xaml')
$activeColumn = $profilesView.SelectSingleNode('//wpf:DataGridTemplateColumn[@Header="Active"]', $namespaceManager)
$activeCheckBox = $null
if ($null -ne $activeColumn) {
    $activeCheckBox = $activeColumn.SelectSingleNode('./wpf:DataGridTemplateColumn.CellTemplate/wpf:DataTemplate/wpf:CheckBox', $namespaceManager)
}
if ($null -eq $activeColumn -or
    $activeColumn.GetAttribute('IsReadOnly') -ne 'True' -or
    $null -eq $activeCheckBox -or
    $activeCheckBox.GetAttribute('IsChecked') -ne '{Binding IsActive, Mode=OneWay}' -or
    $activeCheckBox.GetAttribute('Click') -ne 'Active_Click' -or
    $null -eq $activeCheckBox.GetAttribute('Style')) {
    throw 'Active column is not configured as a clickable one-way runtime state.'
}
$activeStyle = $profilesView.SelectSingleNode('//wpf:Style[@x:Key="ActiveProfileCheckBoxStyle"]', $namespaceManager)
$hitTestSetter = $null
$focusSetter = $null
if ($null -ne $activeStyle) {
    $hitTestSetter = $activeStyle.SelectSingleNode('./wpf:Setter[@Property="IsHitTestVisible" and @Value="False"]', $namespaceManager)
    $focusSetter = $activeStyle.SelectSingleNode('./wpf:Setter[@Property="Focusable" and @Value="False"]', $namespaceManager)
}
if ($null -eq $activeStyle -or $null -ne $hitTestSetter -or $null -eq $focusSetter) {
    throw 'Active checkbox does not have the required clickable style.'
}
$switchButton = $profilesView.SelectSingleNode('//ui:*[@Content="Switch"]', $namespaceManager)
if ($null -ne $switchButton) {
    throw 'Profiles UI still contains the Switch action.'
}
$nameColumn = $profilesView.SelectSingleNode('//wpf:DataGridTemplateColumn[@x:Name="ProfileNameColumn"]', $namespaceManager)
if ($null -eq $nameColumn -or
    $nameColumn.GetAttribute('Header') -ne 'Name' -or
    $nameColumn.GetAttribute('SortMemberPath') -ne 'Name' -or
    $null -eq $nameColumn.SelectSingleNode('./wpf:DataGridTemplateColumn.CellEditingTemplate/wpf:DataTemplate/wpf:TextBox[@x:Name="ProfileNameEditor"]', $namespaceManager)) {
    throw 'Profiles Name column is not configured for inline runtime editing.'
}
$renameButton = $profilesView.SelectSingleNode('//ui:*[@Content="Rename"]', $namespaceManager)
if ($null -ne $renameButton) {
    throw 'Profiles UI still contains the removed Rename action.'
}
$profilesCodeBehindPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\ProfilesView.xaml.cs'
$profilesCodeBehind = Get-Content -LiteralPath $profilesCodeBehindPath -Raw
if ($profilesCodeBehind -match 'SwitchProfile_Click|RenameProfile_Click') {
    throw 'Profiles code-behind still contains a removed profile action handler.'
}
Write-Output 'PASS: Active is a clickable one-way runtime state and Switch is removed.'

$project = Join-Path $PSScriptRoot 'Stage4AProfilesHarness/Stage4AProfilesHarness.csproj'

& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 4A profiles harness failed.'
}
