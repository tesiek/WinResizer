$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $root 'src/WinResizer.CLI/bin/Release/net472/WinResizer.CLI.exe'
$bin = Split-Path $cli -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinResizer-cli-a-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $temp 'fixture.json'
$successConfig = Join-Path $temp 'success.json'
$script:assertions = 0

function Assert-True($condition, [string]$message) {
    $script:assertions++
    if (!$condition) { throw $message }
}

function Invoke-Cli([string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($cli)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $arguments) { [void]$start.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::Start($start)
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.HasExited -and $watch.ElapsedMilliseconds -lt 30000) {
            [Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 10
        }
        if (!$process.HasExited) {
            $process.Kill()
            throw "CLI did not exit within 30 seconds: $($arguments -join ' ')"
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $process.StandardOutput.ReadToEnd()
            Stderr = $process.StandardError.ReadToEnd()
        }
    }
    finally { $process.Dispose() }
}

if (![IO.File]::Exists($cli)) { throw "Build the CLI before this test: $cli" }
[void][IO.Directory]::CreateDirectory($temp)
$form = $null
$otherForm = $null
try {
    [IO.File]::WriteAllText($config, '{"CurrentProfileId":"cli-a","Configs":[{"ProfileId":"cli-a","ProfileName":"CLI-A","WindowSizes":[]}]}')
    $source = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.Base/WindowCmd.cs'))
    $optionSource = [IO.File]::ReadAllText((Join-Path $root 'src/WinResizer.CLI/Commands/Options.cs'))

    # F1: static characterization; no native enumeration/foreground seam exists in WindowCmd.
    Assert-True ($source.Contains('var windows = Resizer.GetOpenWindows();')) 'CLI no longer enumerates open windows'
    Assert-True ($source.Contains('bool resizeAllProcesses = string.IsNullOrEmpty(process);')) 'CLI no longer treats omitted process as all processes'
    Assert-True ($source.Contains('if (!resizeAllProcesses)') -and !$source.Contains('GetForegroundHandle')) 'Foreground/process branch changed'
    $processHelp = 'Filter by process name; if omitted, consider windows from all processes.'
    Assert-True ($optionSource.Contains($processHelp) -and !$optionSource.Contains('foreground process')) 'F1 process help does not describe the optional filter'
    Assert-True ($optionSource.Contains('Window title regex filter; if omitted, no title filter is applied.')) 'Title filter help does not describe regex behavior'

    $help = Invoke-Cli @('resize', '--help')
    Assert-True ($help.ExitCode -eq 0 -and $help.Stdout.Contains($processHelp) -and !$help.Stdout.Contains('foreground process')) 'CLI help does not describe the all-process default'

    # F2: no target means no successful placement.
    $nonexistentProcess = 'cli-a-absent-' + [guid]::NewGuid().ToString('N') + '.exe'
    $none = Invoke-Cli @('resize', '--config', $config, '--process', $nonexistentProcess, '--verbose')
    Assert-True ($none.ExitCode -eq 1) 'Zero matching windows did not return exit code 1'
    Assert-True ($none.Stdout.Contains('No windows resized.')) 'Zero-window verbose report changed'

    # One controlled top-level HWND matches the CLI filter, but the fixture has no saved rule.
    $title = 'CLI-A-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $form = [Windows.Forms.Form]::new()
    $form.Text = $title
    $form.ShowInTaskbar = $true
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(80, 80)
    $form.Size = [Drawing.Size]::new(300, 180)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $processName = [Diagnostics.Process]::GetCurrentProcess().MainModule.ModuleName
    $match = Invoke-Cli @('resize', '--config', $config, '--process', $processName, '--title', [regex]::Escape($title), '--verbose')
    Assert-True ($match.ExitCode -eq 1) 'Matched HWND without a saved rule did not return exit code 1'
    Assert-True ($match.Stdout.Contains($title) -and $match.Stdout.Contains('No saved settings.')) "Matched failure was not reported: stdout=$($match.Stdout); stderr=$($match.Stderr)"

    $fixture = @{
        CurrentProfileId = 'cli-a'
        Configs = @(@{
            ProfileId = 'cli-a'
            ProfileName = 'CLI-A'
            EnableResizeByTitle = $true
            WindowSizes = @(@{
                Name = $processName
                Title = $title
                Rect = @{ Left = 200; Top = 180; Right = 500; Bottom = 360 }
                State = 0
            })
        })
    }
    [IO.File]::WriteAllText($successConfig, ($fixture | ConvertTo-Json -Depth 10))
    $success = Invoke-Cli @('resize', '--config', $successConfig, '--process', $processName, '--title', ('^' + [regex]::Escape($title) + '$'), '--verbose')
    [Windows.Forms.Application]::DoEvents()
    Assert-True ($success.ExitCode -eq 0 -and $success.Stdout.Contains($title)) "Successful placement did not return exit code 0: exit=$($success.ExitCode); stdout=$($success.Stdout); stderr=$($success.Stderr)"
    Assert-True ($form.Left -ne 80 -or $form.Top -ne 80) 'Successful placement did not move the controlled window'

    $otherForm = [Windows.Forms.Form]::new()
    $otherForm.Text = $title + '-no-rule'
    $otherForm.ShowInTaskbar = $true
    $otherForm.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $otherForm.Location = [Drawing.Point]::new(80, 280)
    $otherForm.Size = [Drawing.Size]::new(300, 180)
    $otherForm.Show()
    [Windows.Forms.Application]::DoEvents()
    $partial = Invoke-Cli @('resize', '--config', $successConfig, '--process', $processName, '--title', [regex]::Escape($title), '--verbose')
    Assert-True ($partial.ExitCode -eq 0 -and $partial.Stdout.Contains($title + '-no-rule') -and $partial.Stdout.Contains('No saved settings.')) "Partial success did not preserve exit code 0: exit=$($partial.ExitCode); stdout=$($partial.Stdout); stderr=$($partial.Stderr)"

    # F3: production title matching has an explicit finite timeout and a failure branch.
    Assert-True ($source.Contains('new Regex(title, RegexOptions.None, TimeSpan.FromMilliseconds(500))')) 'Title regex has no explicit 500 ms timeout or changed options'
    Assert-True ($source.Contains('catch (RegexMatchTimeoutException)') -and $source.Contains('Window title matching timed out.')) 'Regex timeout is not reported as a failed operation'
    $invalidRegex = Invoke-Cli @('resize', '--config', $config, '--process', $nonexistentProcess, '--title', '[')
    Assert-True ($invalidRegex.ExitCode -eq 0 -and $invalidRegex.Stdout.Contains('Unterminated [] set')) "PRE-FIX: invalid title regex exit/report changed: exit=$($invalidRegex.ExitCode); stdout=$($invalidRegex.Stdout); stderr=$($invalidRegex.Stderr)"

    # The nested quantified pattern reliably reaches the bounded timeout on a controlled long title.
    $form.Text = ('a' * 512) + '!'
    [Windows.Forms.Application]::DoEvents()
    $timedOut = Invoke-Cli @('resize', '--config', $config, '--process', $processName, '--title', '^(a+)+$')
    Assert-True ($timedOut.ExitCode -eq 1 -and $timedOut.Stdout.Contains('Window title matching timed out.')) "Regex timeout did not return the existing failure code: exit=$($timedOut.ExitCode); stdout=$($timedOut.Stdout); stderr=$($timedOut.Stderr)"

    # F6: invoke the real verbose renderer with controlled title text; no real HWND is needed.
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Spectre.Console.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'WinResizer.Base.dll'))
    $assembly = [Reflection.Assembly]::LoadFrom($cli)
    $verbose = $assembly.GetType('WinResizer.CLI.Commands.ResizeCommand').GetMethod('Verbose', [Reflection.BindingFlags]'NonPublic,Static')
    $targetType = [WinResizer.Base.WindowCmd+TargetWindow]
    $listType = [Collections.Generic.List``1].MakeGenericType($targetType)

    function Invoke-Verbose([string]$windowTitle, [string]$windowProcess = 'fixture.exe', [string]$resultText = '') {
        $items = [Activator]::CreateInstance($listType)
        $item = [Activator]::CreateInstance($targetType, [object[]]@([IntPtr]::new(1), $windowProcess, $windowTitle))
        $item.Result = $resultText
        [void]$items.Add($item)
        $writer = [IO.StringWriter]::new()
        $original = [Spectre.Console.AnsiConsole]::Console
        $settings = [Spectre.Console.AnsiConsoleSettings]::new()
        $settings.Out = [Spectre.Console.AnsiConsoleOutput]::new($writer)
        try {
            [Spectre.Console.AnsiConsole]::Console = [Spectre.Console.AnsiConsole]::Create($settings)
            [void]$verbose.Invoke($null, [object[]](,$items))
            return [pscustomobject]@{ Text = $writer.ToString(); Error = $null }
        }
        catch {
            $errorType = if ($_.Exception.InnerException) { $_.Exception.InnerException.GetType().FullName } else { $_.Exception.GetType().FullName }
            return [pscustomobject]@{ Text = $writer.ToString(); Error = $errorType }
        }
        finally { [Spectre.Console.AnsiConsole]::Console = $original; $writer.Dispose() }
    }

    $marked = Invoke-Verbose '[red]CLI-A-TITLE[/]'
    Assert-True ($null -eq $marked.Error -and $marked.Text.Contains('[red]CLI-A-TITLE[/]')) 'Marked-up title was not rendered literally'
    $malformed = Invoke-Verbose '['
    Assert-True ($null -eq $malformed.Error -and $malformed.Text.Contains('[')) "Malformed markup in a title still fails verbose rendering: error=$($malformed.Error); output=$($malformed.Text)"
    $otherFields = Invoke-Verbose 'Ordinary title' '[bold]P[/]' '[blue]E[/]'
    Assert-True ($null -eq $otherFields.Error -and $otherFields.Text.Contains('[bold]P[/]') -and $otherFields.Text.Contains('[blue]E[/]')) 'Dynamic process or result text was interpreted as markup'
    $ordinary = Invoke-Verbose 'Ordinary title'
    Assert-True ($null -eq $ordinary.Error -and $ordinary.Text.Contains('Ordinary title') -and $ordinary.Text.Contains('fixture.exe')) 'Ordinary verbose table changed'

    $missingConfig = Invoke-Cli @('resize', '--config', (Join-Path $temp 'missing.json'))
    Assert-True ($missingConfig.ExitCode -ne 0) 'Missing config unexpectedly returned success'

    "PASS: CLI-B1 F1/F3/F6 and CLI-B2 F2 real resize results; $script:assertions assertions."
}
finally {
    if ($null -ne $otherForm) { $otherForm.Close(); $otherForm.Dispose() }
    if ($null -ne $form) { $form.Close(); $form.Dispose() }
    if ([IO.File]::Exists($successConfig)) { [IO.File]::Delete($successConfig) }
    if ([IO.File]::Exists($config)) { [IO.File]::Delete($config) }
    if ([IO.Directory]::Exists($temp)) { [IO.Directory]::Delete($temp) }
}
