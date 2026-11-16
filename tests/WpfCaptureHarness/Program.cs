using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WinResizer.Common.Shortcuts;
using WinResizer.Base;
using Rect = WinResizer.Common.Windows.Rect;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Services;
using WinResizer.Views;
using Keys = System.Windows.Forms.Keys;
using ModifierKeys = WinResizer.Common.Shortcuts.ModifierKeys;

namespace WpfCaptureHarness;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        try
        {
            TestSelection();
            TestFiltering();
            TestInvisibleProcessCulture();
            TestLongWindowTitles();
            TestProcessHandleLifetime();
            TestRestoreAllEligibilityCharacterization();
            TestRuntimeIdentity();
            TestDialogLayout();
            TestProcessesCapture();
            Console.WriteLine($"PASS: WPF Capture ({_assertions} assertions): preview/final fresh read, failures, " +
                "ID-safe runtime save/rollback, independent Active/Hotkey, filtering, process handle lifetime, and scrollbar gutters.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static CaptureGeometry Geometry(int left, int top, int width, int height) =>
        (CaptureGeometry)Activator.CreateInstance(typeof(CaptureGeometry), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { new Rect(left, top, left + width, top + height) }, null)!;

    private static void TestSelection()
    {
        var source = new FakeSource();
        var model = new CaptureWindowViewModel(source);
        Assert(source.ListReads == 1 && !model.CanCapture, "List was not loaded once/no selection guard failed");
        model.SelectedWindow = model.Windows[0];
        Assert(source.GeometryReads == 1 && model.Top == "20" && model.Left == "10" &&
            model.Width == "800" && model.Height == "600", "Preview geometry did not come from the source");
        source.Geometry = Geometry(-1900, 40, 960, 1000);
        Assert(model.TryCaptureSelected(out var result) && result!.Geometry.Left == -1900 &&
            result.Geometry.Width == 960 && source.GeometryReads == 2,
            "Final Capture reused old preview instead of reading fresh geometry");
        foreach (var error in new[] { "The selected window was closed.", "Minimized windows cannot be captured.", "Maximized windows cannot be captured." })
        {
            source.Error = error;
            Assert(!model.TryCaptureSelected(out result) && result == null && model.Top == "—" && model.Status == error,
                "Capture failure did not clear the preview/report the error");
        }
        source.Error = null;
        Assert(model.TryCaptureSelected(out _), "Restored window could not be retried without list refresh");
        Assert(source.ListReads == 1, "Preview/final Capture refreshed the window list");
    }

    private static void TestFiltering()
    {
        var handle = new IntPtr(123);
        bool Allowed(uint style = 0x00C00000, uint exStyle = 0, string name = "MozillaWindowClass", bool cloaked = false) =>
            CaptureWindowSource.IsUserWindow(handle, handle, style, exStyle, name, cloaked);
        Assert(Allowed(), "Normal user window was excluded");
        Assert(Allowed(exStyle: 0x80), "Captioned user tool/owned window was excluded");
        Assert(!Allowed(style: 0x40000000), "Child window was allowed");
        Assert(!CaptureWindowSource.IsUserWindow(handle, new IntPtr(456), 0x00C00000, 0, "User", false), "Non-root window was allowed");
        Assert(!Allowed(cloaked: true), "Cloaked window was allowed");
        Assert(!Allowed(style: 0x80000000, exStyle: 0x80), "Captionless tool popup was allowed");
        Assert(!Allowed(style: 0, exStyle: 0x08000000), "No-activate helper was allowed");
        Assert(!Allowed(name: "Shell_TrayWnd") && !Allowed(name: "WorkerW") && !Allowed(name: "tooltips_class32"),
            "Technical shell/tooltip windows were allowed");
        Assert(!Resizer.IsUserWindow(handle, handle, 0x00C00000, 0, "User", false, false,
                new Rect(10, 10, 10, 100)) &&
            !Resizer.IsUserWindow(handle, handle, 0x00C00000, 0, "User", false, false,
                new Rect(10, 10, 100, 10)), "Zero-size window was allowed");
        Assert(!Resizer.IsUserWindow(handle, handle, 0x00C00000, 0, "User", false, false,
                new Rect(10, 10, 100, 100)) &&
            !Resizer.IsUserWindow(handle, handle, 0x00C00000, 0, "User", true, true,
                new Rect(10, 10, 100, 100)), "Invisible/cloaked window was allowed");
        Assert(CaptureWindowSource.IsAllowedOwnWindow(1, 1, handle, handle) &&
            !CaptureWindowSource.IsAllowedOwnWindow(1, 1, new IntPtr(456), handle) &&
            CaptureWindowSource.IsAllowedOwnWindow(2, 1, new IntPtr(456), handle),
            "Own main/helper HWND selection failed");
        var userHandle = new IntPtr(1001);
        var helperHandle = new IntPtr(1002);
        var sameProcessCandidates = new[]
        {
            new { Handle = userHandle, Style = 0x00C40000u, ExStyle = 0u, Rect = new Rect(0, 0, 800, 600) },
            new { Handle = helperHandle, Style = 0x94090000u, ExStyle = 0x80u, Rect = new Rect(960, 600, 960, 600) },
        };
        var eligible = sameProcessCandidates.Where(candidate => Resizer.IsUserWindow(
            candidate.Handle, candidate.Handle, candidate.Style, candidate.ExStyle,
            "SameProcessWindow", true, false, candidate.Rect)).ToArray();
        Assert(eligible.Length == 1 && eligible[0].Handle == userHandle,
            "Same-process zero-size tool helper could overwrite the user-facing HWND");
        Assert(Resizer.IsUserWindow(handle, handle, 0x00C40000, 0, "WinResizerMain", true, false,
                   new Rect(0, 0, 800, 600)) &&
               !Resizer.IsUserWindow(handle, handle, 0x80000000, 0x08000080, "OsdWindow", true, false,
                   new Rect(0, 0, 300, 100)) &&
               !CaptureWindowSource.IsAllowedOwnWindow(1, 1, new IntPtr(456), handle),
            "WinResizer main/OSD/Capture Window eligibility contract failed");
        Assert(!CaptureWindowSource.IsUserProcess(1, 1, "Firefox") &&
            !CaptureWindowSource.IsUserProcess(2, 1, "WinResizer") &&
            !CaptureWindowSource.IsUserProcess(2, 1, "TextInputHost") &&
            CaptureWindowSource.IsUserProcess(2, 1, "Firefox"), "Own/technical process filter failed");
        var native = new CaptureWindowSource();
        Assert(!native.TryCapture(new CaptureWindowItem(IntPtr.Zero, 1, "closed", "closed"), out _, out _, out var error) &&
            !string.IsNullOrEmpty(error), "Invalid/closed HWND was not rejected by production source");
    }

    private static void TestInvisibleProcessCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var name in new[] { "en-US", "tr-TR" })
            {
                var culture = new CultureInfo(name);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                Assert(Resizer.IsInvisibleProcess("TEXTINPUTHOST.EXE"),
                    $"Technical process name was not filtered under {name}.");
                Assert(Resizer.IsInvisibleProcess("textinputhost.exe") &&
                       Resizer.IsInvisibleProcess("TeXtInPuThOsT.ExE") &&
                       !CaptureWindowSource.IsUserProcess(2, 1, "textinputhost"),
                    $"Mixed-case technical process name was not filtered under {name}.");
                Assert(!Resizer.IsInvisibleProcess("nottextinputhost.exe") &&
                       CaptureWindowSource.IsUserProcess(2, 1, "Firefox"),
                    $"Unrelated process was filtered under {name}.");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static void TestLongWindowTitles()
    {
        var sharedPrefix = new string('T', 320);
        var fullTitle = sharedPrefix + " — full title A";
        var differentTitle = sharedPrefix + " — full title B";
        using var form = new System.Windows.Forms.Form
        {
            Text = fullTitle,
            ShowInTaskbar = true,
            Width = 640,
            Height = 480,
        };
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            Assert(Resizer.GetWindowTitle(form.Handle) == fullTitle,
                "The central title reader truncated a title longer than 255 characters");

            var captured = new CaptureWindowSource(() => form.Handle).GetWindows()
                .Single(item => item.Handle == form.Handle);
            Assert(captured.Title == fullTitle,
                "Capture Window did not expose the complete long title");

            Assert(WindowUtils.IsProcessAvailable(form.Handle, out var processName, null),
                "The long-title fixture process could not be resolved");
            var config = new Config { EnableResizeByTitle = true };
            Assert(WindowUtils.TryUpdateWindowSize(form.Handle, config, null, out _) &&
                config.WindowSizes.Single(item => item.Title == fullTitle).Title == fullTitle,
                "Save Active did not store the complete long title");

            var rule = new WindowSize
            {
                Name = processName,
                Title = fullTitle,
                Rect = new Rect(0, 0, 640, 480),
                AutoResize = true,
            };
            var canPlaceCalled = false;
            WindowUtils.TryResizeAutoWindow(form.Handle, processName, new[] { rule }, true,
                Array.Empty<IgnoredWindowRule>(), false, () => { canPlaceCalled = true; return false; });
            Assert(canPlaceCalled,
                "Exact process matching did not use the complete long title");

            rule.Title = differentTitle;
            canPlaceCalled = false;
            WindowUtils.TryResizeAutoWindow(form.Handle, processName, new[] { rule }, true,
                Array.Empty<IgnoredWindowRule>(), false, () => { canPlaceCalled = true; return false; });
            Assert(!canPlaceCalled,
                "Exact process matching ignored a difference after character 255");

            var ignoredRule = new IgnoredWindowRule
            {
                Active = true,
                Process = processName,
                Title = fullTitle,
                TitleMatch = IgnoredWindowTitleMatch.Exact,
            };
            Assert(IgnoredWindowMatcher.IsIgnoredForAutoResize(new[] { ignoredRule }, processName,
                    () => string.Empty, Resizer.GetWindowTitle(form.Handle)),
                "Ignored Windows exact matching did not use the complete long title");
            ignoredRule.Title = differentTitle;
            Assert(!IgnoredWindowMatcher.IsIgnoredForAutoResize(new[] { ignoredRule }, processName,
                    () => string.Empty, Resizer.GetWindowTitle(form.Handle)),
                "Ignored Windows exact matching ignored a difference after character 255");

            form.Text = differentTitle;
            System.Windows.Forms.Application.DoEvents();
            Assert(Resizer.GetWindowTitle(form.Handle) == differentTitle,
                "The title reader returned stale data after a long title changed");
        }
        finally
        {
            form.Close();
            System.Windows.Forms.Application.DoEvents();
        }
    }

    private static void TestProcessHandleLifetime()
    {
        using var form = new System.Windows.Forms.Form
        {
            Text = "WinResizer process lifetime fixture",
            ShowInTaskbar = true,
        };
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            ResolveProcess(form.Handle, 50);
            CollectFinalizers();
            var before = ReadCurrentHandleCount();
            ResolveProcess(form.Handle, 1500);
            var immediate = ReadCurrentHandleCount();
            CollectFinalizers();
            var afterFinalizers = ReadCurrentHandleCount();
            var immediateGrowth = immediate - before;
            var finalGrowth = afterFinalizers - before;

            Assert(immediateGrowth <= 16,
                $"Process resolution leaked handles before finalization: before={before}, immediate={immediate}");
            Assert(finalGrowth <= 16,
                $"Process resolution left unbounded handle growth: before={before}, after={afterFinalizers}");
            Console.WriteLine(
                $"TRACE process handles: before={before}, immediate={immediate}, after-finalizers={afterFinalizers}");
        }
        finally
        {
            form.Close();
            System.Windows.Forms.Application.DoEvents();
        }
    }

    private static void TestRestoreAllEligibilityCharacterization()
    {
        const int wsPopup = unchecked((int)0x80000000);
        const int wsVisible = 0x10000000;
        const int wsCaption = 0x00C00000;
        const int wsThickFrame = 0x00040000;
        const int wsExToolWindow = 0x00000080;
        const int wsExNoActivate = 0x08000000;

        var root = Path.Combine(Path.GetTempPath(), "WinResizer-RestoreEligibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var normal = CreateProbeForm("C6A normal", 40, 40);
        using var minimized = CreateProbeForm("C6A minimized", 80, 80);
        using var maximized = CreateProbeForm("C6A maximized", 120, 120);
        using var hidden = CreateProbeForm("C6A hidden", 160, 160, false);
        using var zeroSize = new ProbeNativeWindow(
            "C6A zero-size", wsPopup | wsVisible, 0, 200, 200, 0, 0);
        using var captionlessTool = new ProbeNativeWindow(
            "C6A captionless tool", wsPopup | wsVisible, wsExToolWindow, 240, 240, 180, 120);
        using var noActivate = new ProbeNativeWindow(
            "C6A noactivate", wsPopup | wsVisible, wsExNoActivate, 280, 280, 180, 120);
        using var ownedDialog = new ProbeNativeWindow(
            "C6A owned dialog", wsPopup | wsVisible | wsCaption | wsThickFrame,
            wsExToolWindow, 320, 320, 220, 140, normal.Handle);
        using var xyHelper = new ProbeNativeWindow(
            "C6A XY helper", wsPopup | wsVisible, wsExToolWindow, 360, 360, 0, 0);

        try
        {
            normal.Show();
            minimized.Show();
            maximized.Show();
            minimized.WindowState = System.Windows.Forms.FormWindowState.Minimized;
            maximized.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            System.Windows.Forms.Application.DoEvents();

            ConfigFactory.SetPortablePath(Path.Combine(root, "WinResizer.config.json"));
            ConfigFactory.UseDefault();
            ConfigFactory.Profiles.CompensateDwmFrameEffects = false;
            var profile = ConfigFactory.Current;
            profile.EnableResizeByTitle = true;
            profile.RestoreAllIncludeMinimized = false;
            profile.WindowSizes.Clear();

            var probes = new[]
            {
                new RestoreProbe("normal visible", normal.Handle, normal.Text, new Rect(700, 100, 960, 300)),
                new RestoreProbe("minimized", minimized.Handle, minimized.Text, new Rect(710, 110, 970, 310)),
                new RestoreProbe("maximized", maximized.Handle, maximized.Text, new Rect(720, 120, 980, 320)),
                new RestoreProbe("hidden", hidden.Handle, hidden.Text, new Rect(730, 130, 990, 330)),
                new RestoreProbe("zero-size helper", zeroSize.Handle, zeroSize.Title, new Rect(740, 140, 1000, 340)),
                new RestoreProbe("captionless tool", captionlessTool.Handle, captionlessTool.Title, new Rect(750, 150, 1010, 350)),
                new RestoreProbe("noactivate helper", noActivate.Handle, noActivate.Title, new Rect(760, 160, 1020, 360)),
                new RestoreProbe("captioned owned dialog/tool", ownedDialog.Handle, ownedDialog.Title, new Rect(770, 170, 1030, 370)),
                new RestoreProbe("XYplorer-like helper", xyHelper.Handle, xyHelper.Title, new Rect(780, 180, 1040, 380)),
            };
            var processName = Resizer.GetRealProcessName(normal.Handle) ??
                throw new InvalidOperationException("Could not resolve the Restore All probe process.");
            foreach (var probe in probes)
            {
                profile.WindowSizes.Add(new WindowSize
                {
                    Name = processName,
                    Title = probe.Title,
                    Rect = probe.Target,
                    State = WinResizer.Common.Windows.WindowState.Normal,
                });
            }

            Assert(SaveAllEligible(normal.Handle) && CaptureStatus(normal.Handle) == (true, true),
                "Normal visible window eligibility changed.");
            Assert(!SaveAllEligible(minimized.Handle) && CaptureStatus(minimized.Handle) == (true, false),
                "Minimized characterization changed: Save All must skip it and Capture must reject final geometry.");
            Assert(SaveAllEligible(maximized.Handle) && CaptureStatus(maximized.Handle) == (true, false),
                "Maximized characterization changed: Save All must accept it and Capture must reject final geometry.");
            Assert(!SaveAllEligible(hidden.Handle) && CaptureStatus(hidden.Handle) == (false, false),
                "Hidden window unexpectedly entered Save All or Capture.");
            Assert(!SaveAllEligible(zeroSize.Handle) && CaptureStatus(zeroSize.Handle) == (false, false),
                "Zero-size helper unexpectedly entered Save All or Capture.");
            Assert(!SaveAllEligible(captionlessTool.Handle) && CaptureStatus(captionlessTool.Handle) == (false, false),
                "Captionless tool helper unexpectedly entered Save All or Capture.");
            Assert(!SaveAllEligible(noActivate.Handle) && CaptureStatus(noActivate.Handle) == (false, false),
                "No-activate helper unexpectedly entered Save All or Capture.");
            Assert(!SaveAllEligible(ownedDialog.Handle) && CaptureStatus(ownedDialog.Handle) == (true, true),
                "Captioned owned dialog characterization changed.");
            Assert(!SaveAllEligible(xyHelper.Handle) && CaptureStatus(xyHelper.Handle) == (false, false),
                "XYplorer-like helper unexpectedly entered Save All or Capture.");
            Assert(!Resizer.IsUserWindow(normal.Handle, normal.Handle, 0x00C00000, 0,
                    "C6AProbe", true, true, new Rect(0, 0, 200, 100)),
                "Cloaked-window baseline characterization changed.");

            var enumerated = Resizer.GetOpenUserWindows().ToHashSet();
            Assert(enumerated.Contains(normal.Handle) && enumerated.Contains(minimized.Handle) &&
                   enumerated.Contains(maximized.Handle) && !enumerated.Contains(hidden.Handle) &&
                   !enumerated.Contains(zeroSize.Handle) && !enumerated.Contains(captionlessTool.Handle) &&
                   !enumerated.Contains(noActivate.Handle) && enumerated.Contains(ownedDialog.Handle) &&
                   !enumerated.Contains(xyHelper.Handle),
                "Restore All user-window eligibility regression.");

            var errors = new List<string>();
            WindowUtils.ResizeAllWindow(profile, errors.Add);
            System.Windows.Forms.Application.DoEvents();
            Assert(errors.Count == 0, "Restore All reported an unexpected per-window failure.");
            Assert(AtTarget(normal.Handle, probes[0].Target), "Normal matching window was not restored.");
            Assert(!AtTarget(minimized.Handle, probes[1].Target) &&
                   Resizer.GetWindowState(minimized.Handle) == WinResizer.Common.Windows.WindowState.Minimized,
                "Restore All attempted a minimized window while Include Minimized was disabled.");
            Assert(AtTarget(maximized.Handle, probes[2].Target), "Maximized matching window was not restored.");
            Assert(!AtTarget(hidden.Handle, probes[3].Target), "Restore All attempted a hidden window.");
            Assert(!AtTarget(zeroSize.Handle, probes[4].Target),
                "Restore All attempted the matching zero-size helper.");
            Assert(!AtTarget(captionlessTool.Handle, probes[5].Target),
                "Restore All attempted the matching captionless tool.");
            Assert(!AtTarget(noActivate.Handle, probes[6].Target),
                "Restore All attempted the matching no-activate helper.");
            Assert(!AtTarget(ownedDialog.Handle, probes[7].Target),
                "Restore All unexpectedly attempted an owned dialog rejected by IsProcessAvailable.");
            Assert(!AtTarget(xyHelper.Handle, probes[8].Target),
                "Restore All attempted the matching XYplorer-like helper.");

            profile.RestoreAllIncludeMinimized = true;
            WindowUtils.ResizeAllWindow(profile, errors.Add);
            System.Windows.Forms.Application.DoEvents();
            Assert(AtTarget(minimized.Handle, probes[1].Target) &&
                   Resizer.GetWindowState(minimized.Handle) == WinResizer.Common.Windows.WindowState.Normal,
                "Restore All did not restore a minimized matching window when Include Minimized was enabled.");

            Console.WriteLine(
                "REGRESSION: Restore All rejects matching zero-size, captionless-tool, no-activate, and " +
                "XYplorer-like helper HWNDs; minimized is controlled by Include Minimized; hidden and owned are skipped.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static bool SaveAllEligible(IntPtr handle) =>
        Resizer.GetOpenUserWindows().Contains(handle) &&
        Resizer.GetWindowState(handle) != WinResizer.Common.Windows.WindowState.Minimized &&
        WindowUtils.IsProcessAvailable(handle, out _, null);

    private static (bool Listed, bool Capturable) CaptureStatus(IntPtr handle)
    {
        var source = new CaptureWindowSource(() => handle);
        var item = source.GetWindows().SingleOrDefault(candidate => candidate.Handle == handle);
        return item is null ? (false, false) : (true, source.TryCapture(item, out _, out _, out _));
    }

    private static bool AtTarget(IntPtr handle, Rect target)
    {
        var actual = Resizer.GetPlacement(handle).Rect;
        return actual.Top == target.Top && actual.Left == target.Left &&
               actual.Right == target.Right && actual.Bottom == target.Bottom;
    }

    private static System.Windows.Forms.Form CreateProbeForm(
        string title, int left, int top, bool createVisible = true)
    {
        var form = new System.Windows.Forms.Form
        {
            Text = title,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Left = left,
            Top = top,
            Width = 260,
            Height = 200,
            ShowInTaskbar = true,
        };
        if (!createVisible)
        {
            _ = form.Handle;
        }
        return form;
    }

    private static void ResolveProcess(IntPtr handle, int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            if (string.IsNullOrWhiteSpace(Resizer.GetProcessName(handle)) ||
                string.IsNullOrWhiteSpace(Resizer.GetRealProcessName(handle)) ||
                !WindowUtils.IsProcessAvailable(handle, out var processName, null) ||
                string.IsNullOrWhiteSpace(processName))
            {
                throw new InvalidOperationException("Process-name resolution failed during the lifetime test.");
            }

            using var process = Resizer.GetRealProcess(handle);
            if (process is null || process.Id <= 0)
            {
                throw new InvalidOperationException("Caller-owned Process resolution failed during the lifetime test.");
            }
        }
    }

    private static int ReadCurrentHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
    }

    private static void CollectFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void TestRuntimeIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-WpfCapture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ConfigFactory.SetPortablePath(Path.Combine(root, "WinResizer.config.json"));
            ConfigFactory.UseDefault();
            var profileA = ConfigFactory.Current;
            var profileB = ConfigFactory.ProfileAdd("B");
            var preset = new WindowPreset { Name = "Keep Name", Enabled = false,
                Hotkey = new Hotkeys { Key = "F20", ModifierKeys = new HashSet<string> { "Ctrl" } } };
            profileA.Presets.Add(preset);
            ConfigFactory.Save();
            var failSave = false;
            using var registrar = new FakeRegistrar();
            using var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableWindowMonitoring = false, HotkeyRegistrar = registrar, OwnsHotkeyRegistrar = false,
                SaveConfiguration = () => { if (failSave) throw new IOException("Injected failure"); ConfigFactory.Save(); },
            });
            runtime.Start();
            using var model = new PresetsViewModel(runtime, Dispatcher.CurrentDispatcher);
            var ids = (Profile: profileA.ProfileId, Preset: preset.Id);
            var captured = new CapturedWindow(new CaptureWindowItem(new IntPtr(123), 2, "Firefox", "Window"), Geometry(-8, 0, 960, 1040));
            runtime.SwitchProfile(profileB.ProfileId);
            model.CapturePreset(ids.Profile, ids.Preset, captured);
            var saved = runtime.GetPresets(ids.Profile).Single();
            Assert(saved.Name == "Keep Name" && !saved.Enabled && saved.Hotkey?.Key == "F20" &&
                saved.X == -8 && saved.Y == 0 && saved.Width == 960 && saved.Height == 1040,
                "Capture lost identity/metadata across a profile switch");
            Assert(runtime.GetPresets(profileB.ProfileId).Count == 0, "Capture changed the newly active profile");
            var added = model.AddCapturedPreset(ids.Profile, captured);
            Assert(added.Name == "Firefox" && added.Enabled && added.Hotkey == null && added.PresetId != ids.Preset &&
                added.X == -8 && added.Width == 960, "Capture Window did not create a named independent preset");
            failSave = true;
            try { model.CapturePreset(ids.Profile, ids.Preset,
                    new CapturedWindow(captured.Window, Geometry(500, 500, 300, 300))); throw new Exception("Save failure accepted"); }
            catch (IOException) { }
            Assert(runtime.GetPresets(ids.Profile).Single(item => item.PresetId == ids.Preset).X == -8,
                "Capture bypassed the runtime rollback transaction");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestDialogLayout()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml") });
        // Production supplies the already-created shell HWND. The identity seam proves
        // that this one own-process window is accepted while helper/dialog HWNDs are not.
        application.MainWindow = new Window();

        var source = new FakeSource(100);
        var dialog = new CaptureWindowDialog(source) { Opacity = 0, ShowActivated = false };
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            var list = (ListBox)dialog.FindName("WindowList");
            var viewer = Find<ScrollViewer>(list)!;
            var presenter = Find<ScrollContentPresenter>(viewer)!;
            var bars = Descendants(viewer).OfType<ScrollBar>().ToArray();
            Assert(Math.Abs(dialog.Width - 840) < 1 && Math.Abs(dialog.Height - 495) < 1,
                $"Dialog has the wrong initial size: {dialog.Width} x {dialog.Height}");
            Assert(viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible &&
                viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible, "Both overflow scrollbars were not available");
            var vertical = bars.Single(bar => bar.Orientation == Orientation.Vertical);
            var horizontal = bars.Single(bar => bar.Orientation == Orientation.Horizontal);
            Assert(presenter.ActualWidth + vertical.ActualWidth <= viewer.ActualWidth + 0.1 &&
                presenter.ActualHeight + horizontal.ActualHeight <= viewer.ActualHeight + 0.1,
                "Scrollbars overlap the content viewport");
            Assert(presenter.TranslatePoint(new System.Windows.Point(presenter.ActualWidth, 0), viewer).X <=
                vertical.TranslatePoint(new System.Windows.Point(0, 0), viewer).X + 0.1,
                "Vertical scrollbar overlaps the text horizontally");
            viewer.ScrollToHorizontalOffset(100);
            viewer.ScrollToVerticalOffset(10);
            dialog.UpdateLayout();
            Assert(viewer.HorizontalOffset > 0 && viewer.VerticalOffset > 0, "Scrolling the list did not work");
            Assert(source.ListReads == 1, "The dialog automatically refreshed its list");
            var ownHandle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
            Assert(!new CaptureWindowSource().GetWindows().Any(item => item.Handle == ownHandle),
                "Production list contains its own Capture dialog");
        }
        finally { dialog.Close(); }

        foreach (var reject in new[] { false, true })
        {
            var finalSource = new FakeSource();
            var modal = new CaptureWindowDialog(finalSource) { Opacity = 0, ShowActivated = false };
            finalSource.Geometry = Geometry(-8, 50, 960, 1040);
            if (reject) finalSource.Error = "Maximized windows cannot be captured.";
            Exception? callbackError = null;
            modal.Loaded += (_, _) => modal.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    Descendants(modal).OfType<Button>().Single(button => Equals(button.Content, "Capture"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    if (reject)
                        Assert(modal.Result == null && ((CaptureWindowViewModel)modal.DataContext).Status == finalSource.Error,
                            "Final button rejection saved a stale preview or failed to display the error");
                }
                catch (Exception exception) { callbackError = exception; }
                finally { if (modal.Result == null) modal.Close(); }
            }));
            var accepted = modal.ShowDialog();
            if (callbackError != null) throw callbackError;
            Assert(reject ? accepted != true && modal.Result == null :
                accepted == true && modal.Result!.Geometry.Top == 50 && modal.Result.Geometry.Left == -8,
                "The real Capture button/dialog result did not use the final read");
            Assert(finalSource.ListReads == 1 && finalSource.GeometryReads == 2,
                "The real dialog refreshed its list or did not reread on the final click");
        }
    }

    private static void TestProcessesCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-ProcessesCapture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(path);
            ConfigFactory.UseDefault();
            var profile = ConfigFactory.Current;
            var otherProfile = ConfigFactory.ProfileAdd("Capture B");
            profile.WindowSizes.Add(new WindowSize { Name = "ACTUAL-MODULE.EXE", Title = "old title", AutoResizeDelay = 650 });
            ConfigFactory.Save();
            var saves = 0;
            var failSave = false;
            using var registrar = new FakeRegistrar();
            using var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableWindowMonitoring = false, HotkeyRegistrar = registrar, OwnsHotkeyRegistrar = false,
                SaveConfiguration = () => { saves++; if (failSave) throw new IOException("Injected failure"); ConfigFactory.Save(); },
            });
            runtime.Start();
            using var model = new ProcessesViewModel(runtime, Dispatcher.CurrentDispatcher);
            var source = new FakeSource { Geometry = Geometry(-1900, -400, 960, 700) };
            var picker = new CaptureWindowViewModel(source);
            picker.SelectedWindow = picker.Windows[0];
            source.FinalTitle = "title changed after selection";
            Assert(picker.TryCaptureSelected(out var capture) && capture!.Window.Title == source.FinalTitle &&
                capture.Window.ProcessModuleName == "actual-module.exe", "Final capture metadata was stale.");
            var before = saves;
            var added = model.AddCapturedProcess(model.BeginCapture(), capture!);
            Assert(saves == before + 1 && runtime.GetProcesses(profile.ProfileId).Count == 2,
                "Capture did not append exactly one rule with one persistence save.");
            Assert(added.Process == "actual-module.exe" && added.Title == source.FinalTitle && !added.AutoResize &&
                added.Delay == 650 && added.State == WinResizer.Common.Windows.WindowState.Normal &&
                added.MaximizedPosition.X == 0 && added.MaximizedPosition.Y == 0,
                "Captured process/defaults/group delay did not match the Runtime contract.");
            Assert(added.Left == -1900 && added.Top == -400 && added.Right == -940 && added.Bottom == 300,
                "Capture converted placement coordinates or wrote Width/Height as edges.");

            var view = new ProcessesView { DataContext = model };
            var host = new Window { Content = view, Width = 1000, Height = 500, Opacity = 0, ShowActivated = false };
            try
            {
                host.Show(); DrainCaptureDispatcher(); host.UpdateLayout();
                var grid = (DataGrid)view.FindName("ProcessesGrid");
                Assert(Descendants(view).OfType<Button>().Any(button => Equals(button.Content, "Capture Window")),
                    "Processes Capture Window button is missing.");
                var collection = System.Windows.Data.CollectionViewSource.GetDefaultView(model.Processes);
                collection.SortDescriptions.Add(new System.ComponentModel.SortDescription("Title", System.ComponentModel.ListSortDirection.Descending));
                var select = typeof(ProcessesView).GetMethod("SelectProcess", BindingFlags.NonPublic | BindingFlags.Instance)!;
                select.Invoke(view, new object[] { added }); DrainCaptureDispatcher();
                Assert(grid.SelectedItem is ProcessRowViewModel selected && selected.ProcessToken == added.ProcessToken,
                    "Selection after refresh/sorting did not use the new rule token.");

                var bytes = File.ReadAllBytes(path); before = saves;
                var duplicateCapture = new CapturedWindow(new CaptureWindowItem(new IntPtr(123), 2, "different display", added.Title,
                    "ACTUAL-MODULE.EXE"), Geometry(300, 300, 100, 100));
                var duplicate = model.AddCapturedProcess(model.BeginCapture(), duplicateCapture);
                select.Invoke(view, new object[] { duplicate }); DrainCaptureDispatcher();
                Assert(duplicate.ProcessToken == added.ProcessToken && saves == before && bytes.SequenceEqual(File.ReadAllBytes(path)) &&
                    duplicate.Left == -1900 && model.Processes.Count == 2,
                    "Duplicate capture saved, overwrote geometry, or appended a second selector.");
                Assert(grid.SelectedItem is ProcessRowViewModel existing && existing.ProcessToken == added.ProcessToken,
                    "Duplicate did not select the existing row after view sorting.");

                source.FinalTitle = added.Title.ToUpperInvariant(); picker.TryCaptureSelected(out capture);
                var caseTitle = model.AddCapturedProcess(model.BeginCapture(), capture!);
                Assert(caseTitle.ProcessToken != added.ProcessToken && caseTitle.Title == source.FinalTitle,
                    "Title duplicate comparison ignored case instead of matching backend semantics.");

                runtime.SetEnableResizeByTitle(false);
                picker.TryCaptureSelected(out capture);
                var wildcard = model.AddCapturedProcess(model.BeginCapture(), capture!);
                Assert(wildcard.Title == "*" && wildcard.Delay == 650 && !wildcard.AutoResize,
                    "Resize By Title OFF did not create a usable wildcard.");
                before = saves;
                Assert(model.AddCapturedProcess(model.BeginCapture(), capture!).ProcessToken == wildcard.ProcessToken && saves == before,
                    "Wildcard capture created an identical selector.");

                runtime.SetEnableResizeByTitle(true);
                source.FinalTitle = "failure title"; picker.TryCaptureSelected(out capture);
                bytes = File.ReadAllBytes(path); var tokens = runtime.GetProcesses(profile.ProfileId).Select(row => row.ProcessToken).ToArray();
                failSave = true; before = saves;
                try { model.AddCapturedProcess(model.BeginCapture(), capture!); throw new Exception("Failure was accepted"); }
                catch (IOException) { }
                finally { failSave = false; }
                DrainCaptureDispatcher();
                Assert(saves == before + 1 && tokens.SequenceEqual(runtime.GetProcesses(profile.ProfileId).Select(row => row.ProcessToken)) &&
                    bytes.SequenceEqual(File.ReadAllBytes(path)) && model.Processes.Count == tokens.Length,
                    "Failed capture did not roll back disk/live/UI state.");

                var context = model.BeginCapture();
                runtime.SwitchProfile(otherProfile.ProfileId);
                before = saves;
                try { model.AddCapturedProcess(context, capture!); throw new Exception("Profile switch capture accepted"); }
                catch (InvalidOperationException error) { Assert(error.Message.Contains("profiles changed"), "Profile change was not explained."); }
                Assert(saves == before && runtime.GetProcesses(otherProfile.ProfileId).Count == 0, "Capture mutated the newly active profile.");
                runtime.SwitchProfile(profile.ProfileId);
                before = saves;
                try { model.AddCapturedProcess(context, capture!); throw new Exception("A-B-A capture accepted"); }
                catch (InvalidOperationException) { }
                Assert(saves == before, "A-B-A failed to invalidate the original dialog context.");

                ConfigFactory.LoadExplicit(path); DrainCaptureDispatcher();
                var reloaded = runtime.GetProcesses(profile.ProfileId).Single(row => row.Title == added.Title);
                Assert(reloaded.Process == added.Process && reloaded.Left == added.Left && reloaded.Right == added.Right &&
                    reloaded.Top == added.Top && reloaded.Bottom == added.Bottom && !reloaded.AutoResize && reloaded.Delay == 650,
                    "Captured process did not survive configuration reload.");

                var manual = model.AddProcess();
                Assert(manual.Process == "process.exe" && manual.Title == "" && manual.Right == 1 && manual.Bottom == 1,
                    "Capture changed the existing manual Add behavior.");
            }
            finally { host.Close(); }

            using var form = new System.Windows.Forms.Form { Text = "native preview title", Width = 600, Height = 400, ShowInTaskbar = false };
            form.Show(); System.Windows.Forms.Application.DoEvents();
            var native = new CaptureWindowSource(() => form.Handle, requireProcessModule: true);
            var item = native.GetWindows().Single(row => row.Handle == form.Handle);
            form.Text = "native final title"; System.Windows.Forms.Application.DoEvents();
            Assert(native.TryCapture(item, out var nativeGeometry, out var current, out _) && current!.Title == form.Text,
                "Production source did not read a changed title at final capture.");
            using var process = Resizer.GetRealProcess(form.Handle);
            Assert(current!.ProcessModuleName == process!.MainModule!.ModuleName && current.ProcessModuleName != current.ProcessName,
                "Production source did not return the actual executable module name.");
            Assert(nativeGeometry.Width > 0 && nativeGeometry.Height > 0, "Production capture did not return valid geometry.");
            Assert(!native.TryCapture(new CaptureWindowItem(item.Handle, item.ProcessId + 1, item.ProcessName, item.Title),
                out _, out _, out _), "Wrong PID was accepted by process capture.");
            var normalSource = new CaptureWindowSource(() => form.Handle);
            Assert(normalSource.TryCapture(item, out _, out var presetMetadata, out _) && presetMetadata!.ProcessModuleName == null,
                "Presets unexpectedly required an executable module name.");
            form.Close();
            Assert(!native.TryCapture(item, out _, out _, out _), "Closed window was accepted by process capture.");
            Console.WriteLine("PASS: Processes Capture: fresh executable/title, selectors, atomic save/reload/rollback, profile guards, token selection and negative placement coordinates.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DrainCaptureDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject => Descendants(root).OfType<T>().FirstOrDefault();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakeSource : ICaptureWindowSource
    {
        private readonly int _count;
        public FakeSource(int count = 1) { _count = count; }
        public CaptureGeometry Geometry = Program.Geometry(10, 20, 800, 600);
        public string? Error;
        public int ListReads;
        public int GeometryReads;
        public IReadOnlyList<CaptureWindowItem> GetWindows()
        {
            ListReads++;
            return Enumerable.Range(0, _count).Select(i => new CaptureWindowItem(new IntPtr(123 + i), 2, "Firefox", new string('W', 300) + i)).ToList();
        }
        public string FinalTitle = "fresh final title";
        public bool TryCapture(CaptureWindowItem item, out CaptureGeometry geometry, out CaptureWindowItem? current, out string error)
        {
            GeometryReads++; geometry = Geometry; error = Error ?? string.Empty;
            current = Error == null ? new CaptureWindowItem(item.Handle, item.ProcessId, item.ProcessName, FinalTitle, "actual-module.exe") : null;
            return Error == null;
        }
    }

    private sealed class RestoreProbe
    {
        public RestoreProbe(string category, IntPtr handle, string title, Rect target)
        {
            Category = category;
            Handle = handle;
            Title = title;
            Target = target;
        }

        public string Category { get; }
        public IntPtr Handle { get; }
        public string Title { get; }
        public Rect Target { get; }
    }

    private sealed class ProbeNativeWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        public ProbeNativeWindow(
            string title, int style, int exStyle, int x, int y, int width, int height, IntPtr owner = default)
        {
            Title = title;
            CreateHandle(new System.Windows.Forms.CreateParams
            {
                Caption = title,
                Style = style,
                ExStyle = exStyle,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Parent = owner,
            });
        }

        public string Title { get; }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }
    private sealed class FakeRegistrar : IGlobalHotkeyRegistrar
    {
        private int _id;
        public event EventHandler<KeyPressedEventArgs>? KeyPressed { add { } remove { } }
        public int RegisterHotKey(ModifierKeys modifier, Keys key) => ++_id;
        public void UnRegisterHotKey(int id) { }
        public void UnRegisterHotKey() { }
        public void Dispose() { }
    }
}
