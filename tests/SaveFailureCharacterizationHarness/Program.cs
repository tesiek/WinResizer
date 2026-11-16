using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WinResizer.Base;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;
using WinRect = WinResizer.Common.Windows.Rect;
using WinWindowState = WinResizer.Common.Windows.WindowState;

namespace SaveFailureCharacterizationHarness;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-SaveFailure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CharacterizeSaveActivePersistenceFailure(Path.Combine(root, "active"));
            CharacterizeSaveAllPersistenceFailure(Path.Combine(root, "all"));

            Console.WriteLine("PASS: Save Active rolls back live WindowSizes after persistence failure.");
            Console.WriteLine("PASS: Save All commits once and rolls back the complete batch after persistence failure.");
            Console.WriteLine("NOT TESTABLE AUTOMATICALLY: deterministic stale-HWND/window-access failure has no injection seam.");
            Console.WriteLine($"PASS: Transactional save verification ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static void CharacterizeSaveActivePersistenceFailure(string directory)
    {
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "WinResizer.config.json");
        ConfigFactory.SetPortablePath(configPath);
        ConfigFactory.UseDefault();
        ConfigFactory.Profiles.CompensateDwmFrameEffects = false;
        ConfigFactory.Current.EnableResizeByTitle = true;
        ConfigFactory.Current.NotifyOnSaved = true;

        using var form = CreateForm("Save Active failure " + Guid.NewGuid().ToString("N"), 180, 140);
        WinResizerRuntime? runtime = null;
        try
        {
            RunWithForegroundForm(form, () =>
            {
                var processName = ProcessName(form);
                var originalRect = new WinRect(20, 30, 420, 330);
                ConfigFactory.Current.WindowSizes.Add(NewRule(processName, form.Text, originalRect));
                ConfigFactory.Current.WindowSizes.Add(NewRule(processName, "*", originalRect));
                ConfigFactory.Save();
                var diskBefore = File.ReadAllText(configPath);
                var originalRules = ConfigFactory.Current.WindowSizes.ToList();
                var originalValues = originalRules.Select(RuleValues.Capture).ToList();

                runtime = NewRuntime();
                runtime.Start();
                var originalTokens = runtime.GetProcesses(ConfigFactory.Current.ProfileId)
                    .Select(process => process.ProcessToken)
                    .ToList();
                var notifications = new List<RuntimeNotificationEventArgs>();
                runtime.NotificationRaised += (_, notification) => notifications.Add(notification);

                var expected = Resizer.GetPlacement(form.Handle, false).Rect;
                Assert(!Equal(originalRect, expected), "Save Active fixture did not differ from disk geometry.");

                var failed = false;
                using (var locked = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        runtime.Save();
                    }
                    catch (ConfigurationPersistenceException)
                    {
                        failed = true;
                    }

                    Assert(File.ReadAllText(configPath) == diskBefore,
                        "Save Active persistence failure changed disk state.");
                }

                var liveRule = Rule(processName, form.Text);
                Assert(failed, "Save Active did not surface the injected persistence failure.");
                Assert(Equal(liveRule.Rect, originalRect),
                    "Save Active did not restore the original live geometry.");
                AssertRulesAndTokensUnchanged(runtime, originalRules, originalValues, originalTokens,
                    "Save Active rollback");
                Assert(notifications.Count == 0,
                    "Save Active emitted a success notification after persistence failure.");

                var displaced = Offset(expected, 140, 90);
                Assert(Resizer.MoveWindow(form.Handle, displaced, false),
                    "Could not displace the Save Active fixture.");
                Application.DoEvents();
                WindowUtils.ResizeWindow(form.Handle, ConfigFactory.Current, null, null);
                Application.DoEvents();
                Assert(Equal(Resizer.GetPlacement(form.Handle, false).Rect, originalRect),
                    "The next Restore did not use the persisted pre-failure Save Active geometry.");
            });
        }
        finally
        {
            runtime?.Dispose();
        }
    }

    private static void CharacterizeSaveAllPersistenceFailure(string directory)
    {
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "WinResizer.config.json");
        ConfigFactory.SetPortablePath(configPath);
        ConfigFactory.UseDefault();
        ConfigFactory.Profiles.CompensateDwmFrameEffects = false;
        ConfigFactory.Current.EnableResizeByTitle = true;
        ConfigFactory.Current.NotifyOnSaved = true;

        using var first = CreateForm("Save All first " + Guid.NewGuid().ToString("N"), 240, 180);
        using var second = CreateForm("Save All second " + Guid.NewGuid().ToString("N"), 720, 240);
        WinResizerRuntime? runtime = null;
        first.Show();
        second.Show();
        Application.DoEvents();
        try
        {
            var processName = ProcessName(first);
            Assert(ProcessName(second).Equals(processName, StringComparison.OrdinalIgnoreCase),
                "Save All fixtures unexpectedly use different processes.");

            var originalRect = new WinRect(40, 50, 440, 350);
            ConfigFactory.Current.WindowSizes.Add(NewRule(processName, first.Text, originalRect));
            ConfigFactory.Current.WindowSizes.Add(NewRule(processName, second.Text, originalRect));
            ConfigFactory.Current.WindowSizes.Add(NewRule(processName, "*", originalRect));
            ConfigFactory.Save();

            var logs = new List<string>();
            var saveAttempts = 0;
            runtime = NewRuntime(logs.Add, () =>
            {
                saveAttempts++;
                ConfigFactory.Save();
            });
            runtime.Start();
            var notifications = new List<RuntimeNotificationEventArgs>();
            runtime.NotificationRaised += (_, notification) => notifications.Add(notification);

            runtime.SaveAll();
            var successfulFirst = Rule(processName, first.Text).Rect;
            var successfulSecond = Rule(processName, second.Text).Rect;
            Assert(notifications.Count == 1 && notifications[0].Level == RuntimeNotificationLevel.Success,
                "Successful Save All did not emit exactly one success notification.");
            Assert(Equal(successfulFirst, Resizer.GetPlacement(first.Handle, false).Rect) &&
                   Equal(successfulSecond, Resizer.GetPlacement(second.Handle, false).Rect),
                "Successful Save All did not persist both fixture geometries.");
            Assert(saveAttempts == 1,
                "Successful Save All did not perform exactly one persistence operation.");

            notifications.Clear();
            logs.Clear();
            saveAttempts = 0;
            var originalRules = ConfigFactory.Current.WindowSizes.ToList();
            var originalValues = originalRules.Select(RuleValues.Capture).ToList();
            var originalTokens = runtime.GetProcesses(ConfigFactory.Current.ProfileId)
                .Select(process => process.ProcessToken)
                .ToList();
            Assert(Resizer.MoveWindow(first.Handle, Offset(successfulFirst, 80, 60), false),
                "Could not move the first Save All fixture.");
            Assert(Resizer.MoveWindow(second.Handle, Offset(successfulSecond, -70, 100), false),
                "Could not move the second Save All fixture.");
            Application.DoEvents();

            var diskBefore = File.ReadAllText(configPath);

            using (var locked = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try
                {
                    runtime.SaveAll();
                }
                catch (ConfigurationPersistenceException)
                {
                    failed = true;
                }

                Assert(failed, "Save All did not surface the batch persistence failure.");
                Assert(File.ReadAllText(configPath) == diskBefore,
                    "Save All persistence failure changed disk state.");
            }

            Assert(saveAttempts == 1,
                "Failed Save All did not perform exactly one persistence attempt.");
            Assert(Equal(Rule(processName, first.Text).Rect, successfulFirst) &&
                   Equal(Rule(processName, second.Text).Rect, successfulSecond),
                "Save All did not restore the complete pre-batch live state.");
            AssertRulesAndTokensUnchanged(runtime, originalRules, originalValues, originalTokens,
                "Save All rollback");
            Assert(notifications.Count == 0,
                "Save All emitted a false-positive success notification after persistence failure.");
            Assert(logs.Count == 0,
                "Save All unexpectedly logged the surfaced batch persistence failure.");
        }
        finally
        {
            runtime?.Dispose();
            first.Close();
            second.Close();
            Application.DoEvents();
        }
    }

    private static WinResizerRuntime NewRuntime(Action<string>? log = null, Action? saveConfiguration = null) =>
        new(new WinResizerRuntimeOptions
        {
            EnableGlobalHotkeys = false,
            EnableWindowMonitoring = false,
            Log = log,
            SaveConfiguration = saveConfiguration,
        });

    private static void AssertRulesAndTokensUnchanged(
        WinResizerRuntime runtime,
        IReadOnlyList<WindowSize> expectedRules,
        IReadOnlyList<RuleValues> expectedValues,
        IReadOnlyList<string> expectedTokens,
        string operation)
    {
        var actualRules = ConfigFactory.Current.WindowSizes.ToList();
        Assert(actualRules.Count == expectedRules.Count,
            $"{operation} changed the rule count.");
        Assert(actualRules.Zip(expectedRules, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal),
            $"{operation} changed rule identity or order.");
        Assert(actualRules.Zip(expectedValues, (actual, expected) => expected.Matches(actual)).All(equal => equal),
            $"{operation} did not restore all rule values.");

        var actualTokens = runtime.GetProcesses(ConfigFactory.Current.ProfileId)
            .Select(process => process.ProcessToken)
            .ToList();
        Assert(actualTokens.SequenceEqual(expectedTokens),
            $"{operation} changed process tokens.");
    }

    private sealed class RuleValues
    {
        private readonly string _name;
        private readonly string _title;
        private readonly WinRect _rect;
        private readonly WinWindowState _state;
        private readonly WinResizer.Common.Windows.Point _maximizedPosition;
        private readonly bool _autoResize;
        private readonly int _autoResizeDelay;

        private RuleValues(WindowSize rule)
        {
            _name = rule.Name;
            _title = rule.Title;
            _rect = rule.Rect;
            _state = rule.State;
            _maximizedPosition = rule.MaximizedPosition;
            _autoResize = rule.AutoResize;
            _autoResizeDelay = rule.AutoResizeDelay;
        }

        public static RuleValues Capture(WindowSize rule) => new(rule);

        public bool Matches(WindowSize rule) =>
            rule.Name == _name &&
            rule.Title == _title &&
            Equal(rule.Rect, _rect) &&
            rule.State == _state &&
            rule.MaximizedPosition.X == _maximizedPosition.X &&
            rule.MaximizedPosition.Y == _maximizedPosition.Y &&
            rule.AutoResize == _autoResize &&
            rule.AutoResizeDelay == _autoResizeDelay;
    }

    private static Form CreateForm(string title, int left, int top) => new()
    {
        Text = title,
        StartPosition = FormStartPosition.Manual,
        Location = new System.Drawing.Point(left, top),
        Size = new Size(420, 320),
        ShowInTaskbar = true,
        TopMost = true,
    };

    private static WindowSize NewRule(string processName, string title, WinRect rect) => new()
    {
        Name = processName,
        Title = title,
        Rect = rect,
        State = WinWindowState.Normal,
    };

    private static WindowSize Rule(string processName, string title) =>
        ConfigFactory.Current.WindowSizes.Single(rule =>
            rule.Name.Equals(processName, StringComparison.OrdinalIgnoreCase) && rule.Title == title);

    private static string ProcessName(Form form) =>
        Resizer.GetRealProcessName(form.Handle)
        ?? throw new InvalidOperationException("Could not resolve the fixture process.");

    private static WinRect Offset(WinRect rect, int x, int y) =>
        new(rect.Left + x, rect.Top + y, rect.Right + x, rect.Bottom + y);

    private static bool Equal(WinRect left, WinRect right) =>
        left.Left == right.Left && left.Top == right.Top &&
        left.Right == right.Right && left.Bottom == right.Bottom;

    private static void RunWithForegroundForm(Form form, Action action)
    {
        Exception? failure = null;
        EventHandler? shown = null;
        shown = (_, _) =>
        {
            form.Shown -= shown;
            form.BeginInvoke(new Action(() =>
            {
                try
                {
                    Assert(TryActivateForeground(form), "The test window did not become foreground.");
                    action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    form.Close();
                }
            }));
        };

        form.Shown += shown;
        form.ShowDialog();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static bool TryActivateForeground(Form form)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                           AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                ShowWindow(form.Handle, 5);
                BringWindowToTop(form.Handle);
                SwitchToThisWindow(form.Handle, true);
                SetActiveWindow(form.Handle);
                SetFocus(form.Handle);
                SetForegroundWindow(form.Handle);
                form.BringToFront();
                form.Activate();
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            Application.DoEvents();
            if (Resizer.GetForegroundHandle() == form.Handle)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
