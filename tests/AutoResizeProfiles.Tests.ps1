$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}

$type = [WinResizer.Runtime.WinResizerRuntime]
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$context = $null
$handler = $type.GetField('_windowEventHandler', $flags)

function Assert-Listener($expected) {
    if (($null -ne $handler.GetValue($context)) -ne $expected) { throw "Expected listener: $expected" }
}

function Subscription-Count($list) {
    $count = 0
    foreach ($field in $list.GetType().GetFields($flags)) {
        $value = $field.GetValue($list)
        if ($value -is [Delegate]) {
            foreach ($d in $value.GetInvocationList()) {
                if ([object]::ReferenceEquals($d.Target, $context)) { $count++ }
            }
        }
    }
    return $count
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-auto-profiles-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
try {
    [WinResizer.Configuration.ConfigFactory]::SetPortablePath((Join-Path $temp 'WinResizer.config.json'))
    [WinResizer.Configuration.ConfigFactory]::UseDefault()
    $a = [WinResizer.Configuration.ConfigFactory]::Current
    $b = [WinResizer.Configuration.ConfigFactory]::Profiles.Add('Auto')
    $window = New-Object WinResizer.Configuration.WindowSize
    $window.Name = 'WinResizer-regression-nonexistent.exe'
    $window.AutoResize = $true
    $b.WindowSizes.Add($window)

    $options = New-Object WinResizer.Runtime.WinResizerRuntimeOptions
    $options.EnableGlobalHotkeys = $false
    $context = New-Object WinResizer.Runtime.WinResizerRuntime($options)
    $context.Start()

    Assert-Listener $false
    [void][WinResizer.Configuration.ConfigFactory]::Profiles.Switch($b.ProfileId)
    Assert-Listener $true
    if ((Subscription-Count $a.WindowSizes) -ne 0) { throw 'Old list still subscribed' }
    if ((Subscription-Count $b.WindowSizes) -ne 1) { throw 'Duplicate or missing subscription' }

    $window.AutoResize = $false
    $b.WindowSizes.ResetBindings()
    Assert-Listener $false
    $window.AutoResize = $true
    $b.WindowSizes.ResetBindings()
    Assert-Listener $true

    [void][WinResizer.Configuration.ConfigFactory]::Profiles.Switch($a.ProfileId)
    Assert-Listener $false

    $old = $a.WindowSizes
    $a.WindowSizes = $b.WindowSizes
    $observe = $type.GetMethod('ObserveCurrentWindowSizes', $flags)
    [void]$observe.Invoke($context, @())
    Assert-Listener $true
    if ((Subscription-Count $old) -ne 0) { throw 'Replaced list still subscribed' }
    'PASS: shared runtime handles off/on profile transitions, list edits, no duplicate subscriptions, old list detachment, and replacement lists.'
}
finally {
    if ($null -ne $context) { $context.Dispose() }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
