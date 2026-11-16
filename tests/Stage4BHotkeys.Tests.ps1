$ErrorActionPreference = 'Stop'
$hotkeysViewPath = Join-Path $PSScriptRoot '..\src\WinResizer\Views\HotkeysView.xaml'
[xml]$hotkeysView = Get-Content -LiteralPath $hotkeysViewPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($hotkeysView.NameTable)
$namespaceManager.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaceManager.AddNamespace('ui', 'http://schemas.lepo.co/wpfui/2022/xaml')
$setKeyButton = $hotkeysView.SelectSingleNode('//ui:Button[@Content="Set Key"]', $namespaceManager)
$actionColumn = $hotkeysView.SelectSingleNode('//wpf:DataGridTextColumn[@Header="Action"]', $namespaceManager)
$currentKeyColumn = $hotkeysView.SelectSingleNode('//wpf:DataGridTextColumn[@Header="Current Key"]', $namespaceManager)
$notifyCheckBox = $hotkeysView.SelectSingleNode('//wpf:CheckBox[@Content="Notify On Saved"]', $namespaceManager)
if ($null -eq $setKeyButton -or $setKeyButton.GetAttribute('Click') -ne 'SetKey_Click' -or
    $null -eq $actionColumn -or $null -eq $currentKeyColumn -or
    $null -eq $notifyCheckBox -or
    $notifyCheckBox.GetAttribute('IsChecked') -notmatch 'NotifyOnSaved' -or
    $hotkeysView.InnerText -match 'prototype only') {
    throw 'Hotkeys WPF view is not wired to the production Set Key flow.'
}
Write-Output 'PASS: Hotkeys view exposes the production Set Key action without sample-only text.'

$project = Join-Path $PSScriptRoot 'Stage4BHotkeysHarness/Stage4BHotkeysHarness.csproj'
& dotnet run --project $project -c Release -p:PlatformTarget=x64
if ($LASTEXITCODE -ne 0) {
    throw 'Stage 4B Hotkeys harness failed.'
}
