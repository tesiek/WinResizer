$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $root 'src/WinResizer/bin/x64/Release/net48'
foreach ($name in 'Newtonsoft.Json.dll','WinResizer.Common.dll','WinResizer.Configuration.dll','WinResizer.Core.dll','WinResizer.Base.dll','WinResizer.Runtime.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin $name))
}

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

$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-invalidation-' + [guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temp)
$form = $null
$coordinator = $null
$preHandler = $null
$postHandler = $null
$replaceHandler = $null
try {
    $saved = Join-Path $temp 'saved.json'
    $import = Join-Path $temp 'same-profile-import.json'
    [WinResizer.Configuration.ConfigFactory]::SetPath($saved, (Join-Path $temp 'portable.json'))
    $profiles = [WinResizer.Configuration.ConfigFactory]::Profiles
    $a = $profiles.UseDefault()
    $a.ProfileName = 'A'
    $b = $profiles.Add('B')

    $form = New-Object Windows.Forms.Form
    $form.Text = 'Auto Resize invalidation'
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Size = New-Object Drawing.Size(220, 160)
    $form.Location = New-Object Drawing.Point(80, 80)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $processName = [WinResizer.Core.WindowControl.Resizer]::GetRealProcessName($form.Handle)
    Assert-True (![string]::IsNullOrWhiteSpace($processName)) 'Could not resolve test window process'
    $rule = New-Object WinResizer.Configuration.WindowSize
    $rule.Name = $processName
    $rule.Title = '*'
    $rule.AutoResize = $true
    $rule.AutoResizeDelay = 450
    $rule.Rect = [WinResizer.Common.Windows.Rect]::new(540, 220, 760, 380)
    $a.EnableResizeByTitle = $true
    $a.EnableAutoResizeDelay = $true
    $a.WindowSizes.Add($rule)

    $runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.Runtime.dll'))
    $coordinatorType = $runtimeAssembly.GetType('WinResizer.Runtime.AutoResizeCoordinator', $true)
    $snapshotType = $runtimeAssembly.GetType('WinResizer.Runtime.AutoResizeConfigurationSnapshot', $true)
    $capture = $snapshotType.GetMethod('Capture', [Reflection.BindingFlags]'Static,Public,NonPublic')
    $funcType = [System.Func``1].MakeGenericType($snapshotType)
    $ignored = New-Object 'System.Collections.Generic.List[WinResizer.Configuration.IgnoredWindowRule]'
    $arguments = [System.Linq.Expressions.Expression[]]@(
        [System.Linq.Expressions.Expression]::Constant($a),
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
    $generationField = $coordinatorType.GetField('_generation', [Reflection.BindingFlags]'Instance,NonPublic')
    function Pending-Count { return $pendingField.GetValue($coordinator).Count }
    function Schedule-Form { [void]$schedule.Invoke($coordinator, [object[]]@($form.Handle)) }

    $timeline = New-Object 'System.Collections.Generic.List[string]'
    $preHandler = [WinResizer.Configuration.ProfileEvents+ProfileSwitchingEvent] {
        param($currentProfileId, $nextProfileId)
        $timeline.Add("pre:$([WinResizer.Configuration.ConfigFactory]::Profiles.CurrentProfileId)->$nextProfileId")
        [void]$cancel.Invoke($coordinator, @())
        $timeline.Add("invalidated:$([WinResizer.Configuration.ConfigFactory]::Profiles.CurrentProfileId)")
    }
    $postHandler = [WinResizer.Configuration.ProfileEvents+ProfileSwitchEvent] {
        param($profileId)
        $timeline.Add("post:$([WinResizer.Configuration.ConfigFactory]::Profiles.CurrentProfileId)")
    }
    $profiles.ProfileEvents.ProfileSwitching += $preHandler
    $profiles.ProfileEvents.ProfileSwitch += $postHandler

    # A pending operation is synchronously invalidated while A is still current.
    Schedule-Form
    Wait-For 80
    Assert-True ((Pending-Count) -eq 1) 'Profile-switch test did not start a pending operation'
    Assert-True ($profiles.Switch($b.ProfileId)) 'A -> B profile switch failed'
    Assert-True ($profiles.Switch($a.ProfileId)) 'B -> A profile switch failed'
    Wait-For 500
    Assert-At $form 80 80
    $expectedTimeline = @("pre:$($a.ProfileId)->$($b.ProfileId)", "invalidated:$($a.ProfileId)", "post:$($b.ProfileId)", "pre:$($b.ProfileId)->$($a.ProfileId)", "invalidated:$($b.ProfileId)", "post:$($a.ProfileId)")
    Assert-True (($timeline -join '|') -eq ($expectedTimeline -join '|')) 'Pre-switch invalidation did not occur before CurrentProfileId changed'
    $beforeSameSwitch = $timeline.Count
    Assert-True ($profiles.Switch($a.ProfileId)) 'A -> A switch unexpectedly failed'
    Assert-True ($timeline.Count -eq $beforeSameSwitch) 'A -> A emitted a pre-switch invalidation'
    Assert-True ((Pending-Count) -eq 0) 'A -> B -> A left the original pending operation active'

    # An import with the same textual profile ID still invalidates before either live collection changes.
    $oldConfig = $profiles.Current
    $oldIgnored = $profiles.IgnoredWindows
    $replaceTimeline = New-Object 'System.Collections.Generic.List[string]'
    $replaceHandler = [WinResizer.Configuration.ConfigFactory+ConfigurationReplacingEvent] {
        $replaceTimeline.Add("replace:$([object]::ReferenceEquals($oldConfig, [WinResizer.Configuration.ConfigFactory]::Profiles.Current)):$([object]::ReferenceEquals($oldIgnored, [WinResizer.Configuration.ConfigFactory]::Profiles.IgnoredWindows))")
        [void]$cancel.Invoke($coordinator, @())
        $replaceTimeline.Add("invalidated:$([WinResizer.Configuration.ConfigFactory]::Profiles.CurrentProfileId)")
    }
    [WinResizer.Configuration.ConfigFactory]::add_ConfigurationReplacing($replaceHandler)
    Schedule-Form
    Wait-For 80
    Assert-True ((Pending-Count) -eq 1) 'Import test did not start a pending operation'
    $sameIdJson = @"
{
  "CurrentProfileId": "$($a.ProfileId)",
  "Configs": [
    { "ProfileId": "$($a.ProfileId)", "ProfileName": "Imported A", "WindowSizes": [] }
  ],
  "IgnoredWindows": []
}
"@
    [IO.File]::WriteAllText($import, $sameIdJson)
    [WinResizer.Configuration.ConfigFactory]::Load($import)
    Wait-For 500
    Assert-At $form 80 80
    Assert-True (($replaceTimeline -join '|') -eq "replace:True:True|invalidated:$($a.ProfileId)") 'ConfigurationReplacing was not raised before live Configs and IgnoredWindows changed'
    Assert-True ([WinResizer.Configuration.ConfigFactory]::Current.ProfileId -eq $a.ProfileId) 'Same-ID import did not retain the requested current profile ID'
    Assert-True (![object]::ReferenceEquals($oldConfig, [WinResizer.Configuration.ConfigFactory]::Current)) 'Import did not replace the live configuration instance'
    Assert-True ((Pending-Count) -eq 0) 'Same-profile-ID import left its old pending operation active'

    # Save serializes the current model without raising ConfigurationReplacing or touching generation.
    Schedule-Form
    Wait-For 80
    Assert-True ((Pending-Count) -eq 1) 'Save test did not start a pending operation'
    $generationBeforeSave = [long]$generationField.GetValue($coordinator)
    [WinResizer.Configuration.ConfigFactory]::Save()
    Wait-For 80
    Assert-True (([long]$generationField.GetValue($coordinator)) -eq $generationBeforeSave) 'Save unexpectedly invalidated coordinator generation'
    Assert-True ((Pending-Count) -eq 1) 'Save unexpectedly cancelled a pending operation'
    [void]$cancel.Invoke($coordinator, @())

    # The UIA exception boundary remains limited to AutomationElement.Current.
    $windowEventHandlerSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Core/WindowControl/WindowEventHandler.cs'))
    $tryStart = $windowEventHandlerSource.IndexOf('try', $windowEventHandlerSource.IndexOf('IntPtr handle'))
    $catchStart = $windowEventHandlerSource.IndexOf('catch (InvalidOperationException)', $tryStart)
    $callbackStart = $windowEventHandlerSource.IndexOf('windowCreatedHandler(handle)', $catchStart)
    Assert-True ($tryStart -ge 0 -and $catchStart -gt $tryStart -and $callbackStart -gt $catchStart) 'InvalidOperationException catch is not limited to the UIA Current read'
    Assert-True ($windowEventHandlerSource.Contains('handle = new IntPtr(src.Current.NativeWindowHandle);')) 'UIA handle read is not protected by the narrow exception boundary'

    $runtimeSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Runtime/WinResizerRuntime.cs'))
    Assert-True ($runtimeSource.Contains('ProfileSwitching += OnProfileSwitching')) 'Shared runtime does not subscribe the coordinator to pre-switch events'
    Assert-True ($runtimeSource.Contains('ConfigurationReplacing += OnConfigurationReplacing')) 'Shared runtime does not subscribe the coordinator to pre-replace events'
    Assert-True ($runtimeSource.Contains('private void OnProfileSwitching') -and $runtimeSource.Contains('_autoResizeCoordinator?.CancelPending();')) 'Shared runtime pre-change handlers do not invalidate pending work'

    'PASS: pre-switch ordering, A -> B -> A cancellation, same-ID import invalidation, config-replace ordering, Save without invalidation, shared-runtime pre-change wiring, and narrow UIA InvalidOperationException boundary.'
}
finally {
    if ($null -ne $replaceHandler) { [WinResizer.Configuration.ConfigFactory]::remove_ConfigurationReplacing($replaceHandler) }
    if ($null -ne $preHandler) {
        [WinResizer.Configuration.ConfigFactory]::Profiles.ProfileEvents.ProfileSwitching =
            [Delegate]::Remove([WinResizer.Configuration.ConfigFactory]::Profiles.ProfileEvents.ProfileSwitching, $preHandler)
    }
    if ($null -ne $postHandler) {
        [WinResizer.Configuration.ConfigFactory]::Profiles.ProfileEvents.ProfileSwitch =
            [Delegate]::Remove([WinResizer.Configuration.ConfigFactory]::Profiles.ProfileEvents.ProfileSwitch, $postHandler)
    }
    if ($null -ne $coordinator) { $coordinator.Dispose() }
    if ($null -ne $form -and !$form.IsDisposed) { $form.Close(); $form.Dispose() }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp, $true) }
}
