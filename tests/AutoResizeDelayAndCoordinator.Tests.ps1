$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}

# PowerShell does not apply the application's binding redirects itself.
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = (New-Object Reflection.AssemblyName($eventArgs.Name)).Name
    $path = Join-Path $bin ($name + '.dll')
    if ([IO.File]::Exists($path)) { return [Reflection.Assembly]::LoadFrom($path) }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

function Wait-For([int]$milliseconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $milliseconds) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 15
    }
}

function Assert-True($condition, [string]$message) {
    if (!$condition) { throw $message }
}

function Assert-At([Windows.Forms.Form]$form, [int]$left, [int]$top) {
    $rect = [WinResizer.Core.WindowControl.Resizer]::GetRect($form.Handle)
    if ($rect.Left -ne $left -or $rect.Top -ne $top) {
        throw "Expected form at $left,$top; got $($rect.Left),$($rect.Top)"
    }
}

function New-TestForm([string]$title, [int]$left, [int]$top) {
    $form = New-Object Windows.Forms.Form
    $form.Text = $title
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::Sizable
    $form.ShowInTaskbar = $true
    $form.Size = New-Object Drawing.Size(220, 160)
    $form.Location = New-Object Drawing.Point($left, $top)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    return $form
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-delay-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$forms = New-Object 'System.Collections.Generic.List[System.Windows.Forms.Form]'
$coordinator = $null
try {
    # Load normalizes groups case-insensitively using the clamped first rule.
    $saved = Join-Path $temp 'saved.json'
    $input = Join-Path $temp 'input.json'
    [WinResizer.Configuration.ConfigFactory]::SetPath($saved, (Join-Path $temp 'portable.json'))
    $json = @'
{
  "CurrentProfileId": "delay",
  "Configs": [
    {
      "ProfileId": "delay",
      "ProfileName": "delay",
      "WindowSizes": [
        { "Name": "Firefox.exe", "Title": "*", "AutoResizeDelay": 500 },
        { "Name": "firefox.exe", "Title": "one", "AutoResizeDelay": -100 },
        { "Name": "FIREFOX.EXE", "Title": "two", "AutoResizeDelay": 15000 },
        { "Name": "negative.exe", "Title": "*", "AutoResizeDelay": -100 },
        { "Name": "zero.exe", "Title": "*", "AutoResizeDelay": 0 },
        { "Name": "normal.exe", "Title": "*", "AutoResizeDelay": 500 },
        { "Name": "max.exe", "Title": "*", "AutoResizeDelay": 10000 },
        { "Name": "high.exe", "Title": "*", "AutoResizeDelay": 15000 }
      ]
    }
  ]
}
'@
    [IO.File]::WriteAllText($input, $json)
    [WinResizer.Configuration.ConfigFactory]::Load($input)
    $delays = [WinResizer.Configuration.ConfigFactory]::Current.WindowSizes | ForEach-Object { $_.AutoResizeDelay }
    $expected = @(500, 500, 500, 0, 0, 500, 10000, 10000)
    Assert-True (($delays -join ',') -eq ($expected -join ',')) 'JSON delay normalization/clamping failed'
    Assert-True ([IO.File]::Exists($saved)) 'Normalized configuration was not persisted'
    $savedJson = [Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($saved))
    $savedDelays = @($savedJson.Configs[0].WindowSizes | ForEach-Object { [int]$_.AutoResizeDelay })
    Assert-True (($savedDelays -join ',') -eq ($expected -join ',')) 'Saved JSON is not delay-consistent'

    # Build a live snapshot provider for the internal coordinator without using
    # ConfigFactory. The provider returns a fresh copy for every coordinator phase.
    $config = New-Object WinResizer.Configuration.Config
    $config.EnableResizeByTitle = $true
    $config.EnableAutoResizeDelay = $true
    $ignored = New-Object 'System.Collections.Generic.List[WinResizer.Configuration.IgnoredWindowRule]'
    $first = New-TestForm 'Auto Resize coordinator first' 30 30
    $forms.Add($first)
    $processName = [WinResizer.Core.WindowControl.Resizer]::GetRealProcessName($first.Handle)
    Assert-True (![string]::IsNullOrWhiteSpace($processName)) 'Could not resolve the test window process'
    Assert-True ([WinResizer.Core.WindowControl.Resizer]::IsEligibleForAutoResize($first.Handle)) 'Test form is not eligible for Auto Resize'
    $rule = New-Object WinResizer.Configuration.WindowSize
    $rule.Name = $processName
    $rule.Title = '*'
    $rule.AutoResize = $true
    $rule.AutoResizeDelay = 450
    $rule.Rect = [WinResizer.Common.Windows.Rect]::new(480, 180, 700, 340)
    $config.WindowSizes.Add($rule)

    $runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.Runtime.dll'))
    $coordinatorType = $runtimeAssembly.GetType('WinResizer.Runtime.AutoResizeCoordinator', $true)
    $snapshotType = $runtimeAssembly.GetType('WinResizer.Runtime.AutoResizeConfigurationSnapshot', $true)
    $capture = $snapshotType.GetMethod('Capture', [Reflection.BindingFlags]'Static,Public,NonPublic')
    $funcType = [System.Func``1].MakeGenericType($snapshotType)
    $arguments = [System.Linq.Expressions.Expression[]]@(
        [System.Linq.Expressions.Expression]::Constant($config),
        [System.Linq.Expressions.Expression]::Constant($ignored),
        [System.Linq.Expressions.Expression]::Constant($false))
    $provider = [System.Linq.Expressions.Expression]::Lambda(
        $funcType,
        [System.Linq.Expressions.Expression]::Call($capture, $arguments),
        [System.Linq.Expressions.ParameterExpression[]]@()).Compile()
    $coordinator = [Activator]::CreateInstance(
        $coordinatorType,
        [Reflection.BindingFlags]'Instance,Public,NonPublic',
        $null,
        [object[]]@($provider),
        $null)
    $schedule = $coordinatorType.GetMethod('Schedule', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    $cancel = $coordinatorType.GetMethod('CancelPending', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    $pendingField = $coordinatorType.GetField('_pending', [Reflection.BindingFlags]'Instance,NonPublic')
    function Pending-Count { return $pendingField.GetValue($coordinator).Count }
    function Schedule-Form([Windows.Forms.Form]$form) { [void]$schedule.Invoke($coordinator, [object[]]@($form.Handle)) }

    # One event wins over a duplicate while it is pending; the duplicate does not restart the delay.
    Schedule-Form $first
    Wait-For 150
    Schedule-Form $first
    Assert-True ((Pending-Count) -eq 1) 'Duplicate HWND + PID created a second pending operation'
    Wait-For 360
    Assert-At $first 480 180
    Wait-For 80
    Assert-True ((Pending-Count) -eq 0) 'Completed operation was not removed from pending'

    # Different HWNDs from one process remain independent and begin without waiting for each other.
    $second = New-TestForm 'Auto Resize coordinator second' 40 240
    $third = New-TestForm 'Auto Resize coordinator third' 60 440
    $forms.Add($second); $forms.Add($third)
    $rule.AutoResizeDelay = 500
    Schedule-Form $second
    Wait-For 80
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Schedule-Form $third
    $timer.Stop()
    Assert-True ($timer.ElapsedMilliseconds -lt 100) 'Second WindowOpened event was blocked by the first delay'
    Assert-True ((Pending-Count) -eq 2) 'Different HWNDs from one process were not pending independently'
    Wait-For 550
    Assert-At $second 480 180
    Assert-At $third 480 180

    # An Any rule is rejected before the delay and never creates a lasting pending operation.
    $anyIgnored = New-TestForm 'Auto Resize coordinator ignored any' 1020 240
    $forms.Add($anyIgnored)
    $rule.AutoResizeDelay = 450
    $ignoreAnyRule = New-Object WinResizer.Configuration.IgnoredWindowRule
    $ignoreAnyRule.Active = $true
    $ignoreAnyRule.Process = $processName
    $ignoreAnyRule.TitleMatch = [WinResizer.Configuration.IgnoredWindowTitleMatch]::Any
    $ignored.Add($ignoreAnyRule)
    Schedule-Form $anyIgnored
    Wait-For 100
    Assert-True ((Pending-Count) -eq 0) 'Ignored Any rule waited instead of stopping before delay'
    Assert-At $anyIgnored 1020 240
    $ignored.Clear()

    # A cancelled operation does not resize, is cleaned up, and does not remove a replacement operation.
    $cancelled = New-TestForm 'Auto Resize coordinator cancelled' 70 640
    $forms.Add($cancelled)
    $rule.AutoResizeDelay = 450
    Schedule-Form $cancelled
    Wait-For 90
    [void]$cancel.Invoke($coordinator, @())
    Wait-For 500
    Assert-At $cancelled 70 640
    Assert-True ((Pending-Count) -eq 0) 'Cancelled operation remained pending'
    Schedule-Form $cancelled
    Wait-For 80
    Assert-True ((Pending-Count) -eq 1) 'Replacement operation was removed by old cleanup'
    [void]$cancel.Invoke($coordinator, @())
    Wait-For 80

    # Fresh post-delay data controls the final action, while Delay changes affect only new events.
    $disabled = New-TestForm 'Auto Resize coordinator disabled' 740 30
    $forms.Add($disabled)
    $rule.AutoResizeDelay = 350
    $rule.AutoResize = $true
    Schedule-Form $disabled
    Wait-For 90
    $rule.AutoResize = $false
    Wait-For 350
    Assert-At $disabled 740 30
    $rule.AutoResize = $true

    $removed = New-TestForm 'Auto Resize coordinator removed rule' 1020 440
    $forms.Add($removed)
    Schedule-Form $removed
    Wait-For 90
    [void]$config.WindowSizes.Remove($rule)
    Wait-For 350
    Assert-At $removed 1020 440
    $config.WindowSizes.Add($rule)

    $ignoredForm = New-TestForm 'Auto Resize coordinator ignored after delay' 740 240
    $forms.Add($ignoredForm)
    Schedule-Form $ignoredForm
    Wait-For 90
    $ignoreRule = New-Object WinResizer.Configuration.IgnoredWindowRule
    $ignoreRule.Active = $true
    $ignoreRule.Process = $processName
    $ignoreRule.Title = $ignoredForm.Text
    $ignoreRule.TitleMatch = [WinResizer.Configuration.IgnoredWindowTitleMatch]::Exact
    $ignored.Add($ignoreRule)
    Wait-For 350
    Assert-At $ignoredForm 740 240
    $ignored.Clear()

    $oldDelay = New-TestForm 'Auto Resize coordinator old delay' 740 440
    $newDelay = New-TestForm 'Auto Resize coordinator new delay' 740 640
    $forms.Add($oldDelay); $forms.Add($newDelay)
    $rule.AutoResizeDelay = 600
    Schedule-Form $oldDelay
    Wait-For 90
    $rule.AutoResizeDelay = 0
    Schedule-Form $newDelay
    Wait-For 160
    Assert-At $newDelay 480 180
    Assert-At $oldDelay 740 440
    Wait-For 470
    Assert-At $oldDelay 480 180

    # Closing an HWND during the delay is harmless and releases its pending entry.
    $closed = New-TestForm 'Auto Resize coordinator closed' 1020 30
    $forms.Add($closed)
    $rule.AutoResizeDelay = 300
    Schedule-Form $closed
    Wait-For 60
    $closed.Close()
    Wait-For 350
    Assert-True ((Pending-Count) -eq 0) 'Closed HWND remained pending'

    # The implementation keeps owner PID in the identity and validates it again before placement.
    $source = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Runtime/AutoResizeCoordinator.cs'))
    Assert-True ($source.Contains('new WindowIdentity(handle, processId)')) 'Coordinator identity is missing HWND + PID'
    Assert-True ($source.Contains('currentProcessId == identity.ProcessId')) 'Coordinator does not revalidate owner PID'
    Assert-True ($source.Contains('Task.Delay(delay, pending.Cancellation.Token)')) 'Coordinator does not use cancellable Task.Delay'
    Assert-True (!$source.Contains('Thread.Sleep')) 'Coordinator still contains Thread.Sleep'

    'PASS: delay normalization/clamping, one event, duplicate suppression, independent HWNDs, nonblocking second event, cancellation/generation cleanup, fresh disabled/ignored rules, delay-change semantics, closed HWND, PID revalidation implementation.'
}
finally {
    if ($null -ne $coordinator) { $coordinator.Dispose() }
    foreach ($form in $forms) {
        if (!$form.IsDisposed) { $form.Close(); $form.Dispose() }
    }
    foreach ($file in 'saved.json','input.json') {
        $path = Join-Path $temp $file
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp) }
}
