using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using WinResizer.Base;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;
using DpiNative = WinResizer.Core.Dpi.NativeMethods;
using WinPoint = WinResizer.Common.Windows.Point;
using WindowState = WinResizer.Common.Windows.WindowState;

namespace CaptureGeometryHarness;

internal static class Program
{
    private static int _assertions;
    private static readonly IntPtr Handle = new IntPtr(123);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Read-only diagnostic entry point for manual Windows Snap/DPI checks.
            if (args.Length == 2 && args[0] == "--capture")
            {
                var handle = new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture));
                if (!Resizer.TryGetCaptureGeometry(handle, out var geometry, out var failure))
                {
                    Console.Error.WriteLine("Capture rejected: " + failure);
                    return 1;
                }

                Console.WriteLine($"Top={geometry.Top} Left={geometry.Left} " +
                                  $"Width={geometry.Width} Height={geometry.Height}");
                return 0;
            }

            TestCoordinates();
            TestDwmFrameCompensation();
            TestFailures();
            TestNativeWindows();
            Console.WriteLine($"PASS: Capture geometry ({_assertions} assertions). Windows Snap is NOT simulated.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void TestCoordinates()
    {
        var api = new FakeApi();
        AssertGeometry(api, new Rect(100, 120, 900, 720), "Normal window");
        api.Work = new Rect(40, 32, 1920, 1080);
        AssertGeometry(api, new Rect(60, 88, 860, 688), "Left/top taskbar inset");
        api.Work = new Rect(0, 0, 1880, 1040);
        AssertGeometry(api, api.ScreenRect, "Right/bottom taskbar keeps origin");

        api.Bounds = new Rect(-1920, -1080, 0, 0);
        api.Work = new Rect(-1880, -1048, 0, 0);
        api.ScreenRect = new Rect(-1810, -950, -1010, -350);
        AssertGeometry(api, new Rect(-1850, -982, -1050, -382), "Negative monitor coordinates");

        api.Bounds = new Rect(1920, 240, 3840, 1320);
        api.Work = new Rect(1960, 272, 3840, 1320);
        api.ScreenRect = new Rect(2100, 400, 2900, 1000);
        AssertGeometry(api, new Rect(2060, 368, 2860, 968), "Second monitor with nonzero origin");

        api.ExStyle = (uint)NativeMethods.WindowExStyles.WS_EX_TOOLWINDOW;
        AssertGeometry(api, api.ScreenRect, "Tool window uses screen coordinates");

        // Two identical current rects produce the same capture even if restore bounds
        // would differ. This is a deterministic input test, not a Windows Snap test.
        api.ExStyle = 0;
        Assert(CaptureGeometryReader.TryRead(Handle, api, out var first, out _), "First repeat capture failed");
        Assert(CaptureGeometryReader.TryRead(Handle, api, out var second, out _), "Second repeat capture failed");
        Assert(Equal(first.PlacementRect, second.PlacementRect), "Same current rectangle changed capture");
    }

    private static void TestDwmFrameCompensation()
    {
        var raw = new Rect(-7, 0, 1572, 1177);
        var visible = new Rect(0, 0, 1565, 1170);
        var target = new Rect(0, 0, 1565, 1170);
        Assert(WindowFrameGeometry.TryCalculateRawTarget(raw, visible, target, out var result),
            "Documented DWM margin example was rejected");
        Assert(Equal(result, raw), "Documented DWM margin example produced the wrong raw target");
        Assert(WindowFrameGeometry.TryCalculateVisibleTarget(raw, visible, raw, out var visibleResult) &&
               Equal(visibleResult, visible), "Raw-to-visible conversion did not invert the documented example");

        Assert(WindowFrameGeometry.TryCalculateRawTarget(
            new Rect(10, 20, 410, 320), new Rect(10, 20, 410, 320),
            new Rect(50, 60, 450, 360), out result) &&
            Equal(result, new Rect(50, 60, 450, 360)), "Zero margins changed the target");

        Assert(WindowFrameGeometry.TryCalculateRawTarget(
            new Rect(90, 180, 530, 650), new Rect(100, 205, 500, 620),
            new Rect(-1200, 275, -800, 690), out result) &&
            Equal(result, new Rect(-1210, 250, -770, 720)),
            "Asymmetric margins/non-zero top/negative coordinates were calculated incorrectly");
        Assert(WindowFrameGeometry.TryCalculateVisibleTarget(
            new Rect(90, 180, 530, 650), new Rect(100, 205, 500, 620),
            result, out visibleResult) && Equal(visibleResult, new Rect(-1200, 275, -800, 690)),
            "Asymmetric raw-to-visible conversion did not round-trip");

        var api = new FakeApi
        {
            ScreenRect = raw,
            VisibleScreenRect = visible,
        };
        Assert(CaptureGeometryReader.TryRead(Handle, api, true, out var compensated, out _) &&
               Equal(compensated.PlacementRect, visible), "Compensated Capture did not use visible bounds");
        Assert(api.VisibleRectReads == 1, "Compensated Capture did not query DWM bounds");

        api.VisibleRectReads = 0;
        Assert(CaptureGeometryReader.TryRead(Handle, api, false, out var legacy, out _) &&
               Equal(legacy.PlacementRect, raw), "Compensation OFF changed raw Capture semantics");
        Assert(api.VisibleRectReads == 0, "Compensation OFF queried DWM bounds");

        api.VisibleRectSuccess = false;
        Assert(CaptureGeometryReader.TryRead(Handle, api, true, out var fallback, out _) &&
               Equal(fallback.PlacementRect, raw), "DWM failure did not fall back to raw Capture geometry");
    }

    private static void AssertGeometry(FakeApi api, Rect expected, string name)
    {
        Assert(CaptureGeometryReader.TryRead(Handle, api, out var geometry, out var failure), name + " rejected");
        Assert(failure == CaptureGeometryFailure.None, name + " returned failure status");
        Assert(Equal(geometry.PlacementRect, expected), name + " converted incorrectly");
        Assert(geometry.Left == expected.Left && geometry.Top == expected.Top &&
               geometry.Width == expected.Right - expected.Left && geometry.Height == expected.Bottom - expected.Top,
            name + " Top/Left/Width/Height mismatch");
        Assert(Equal(api.LastMonitorRect, api.ScreenRect), "MonitorFromRect received workspace instead of screen coordinates");
    }

    private static void TestFailures()
    {
        AssertFailure(new FakeApi(), CaptureGeometryFailure.InvalidWindow, IntPtr.Zero);
        AssertFailure(new FakeApi { ValidWindow = false }, CaptureGeometryFailure.InvalidWindow);
        AssertFailure(new FakeApi { Minimized = true }, CaptureGeometryFailure.Minimized);
        AssertFailure(new FakeApi { Maximized = true }, CaptureGeometryFailure.Maximized);
        AssertFailure(new FakeApi { Visible = false }, CaptureGeometryFailure.NotVisible);
        AssertFailure(new FakeApi { Style = (uint)NativeMethods.WindowStyles.WS_CHILD }, CaptureGeometryFailure.NotTopLevel);
        var rectFailure = new FakeApi { RectSuccess = false };
        AssertFailure(rectFailure, CaptureGeometryFailure.WindowRectUnavailable);
        Assert(rectFailure.RectReads == 1 && rectFailure.MonitorReads == 0,
            "GetWindowRect failure was not handled before monitor lookup");
        AssertFailure(new FakeApi { Monitor = IntPtr.Zero }, CaptureGeometryFailure.MonitorUnavailable);
        AssertFailure(new FakeApi { MonitorInfoSuccess = false }, CaptureGeometryFailure.MonitorUnavailable);
        AssertFailure(new FakeApi { ScreenRect = new Rect(10, 10, 10, 20) }, CaptureGeometryFailure.InvalidGeometry);
        AssertFailure(new FakeApi { ScreenRect = new Rect(int.MinValue, 0, int.MaxValue, 100) }, CaptureGeometryFailure.InvalidGeometry);
        AssertFailure(new FakeApi
        {
            Bounds = new Rect(int.MinValue, 0, -1, 1080),
            Work = new Rect(int.MinValue + 40, 0, -1, 1080),
            ScreenRect = new Rect(int.MinValue + 10, 100, int.MinValue + 810, 700),
        }, CaptureGeometryFailure.InvalidGeometry);
        AssertFailure(new FakeApi { MaximizeAfterRectRead = true }, CaptureGeometryFailure.Maximized);
    }

    private static void AssertFailure(FakeApi api, CaptureGeometryFailure expected, IntPtr? handle = null)
    {
        Assert(!CaptureGeometryReader.TryRead(handle ?? Handle, api, out var geometry, out var failure),
            expected + " was accepted");
        Assert(failure == expected && geometry.Width == 0 && geometry.Height == 0,
            expected + " did not return the explicit failure/default result");
    }

    private static void TestNativeWindows()
    {
        Assert(!Resizer.TryGetCaptureGeometry(IntPtr.Zero, out _, out var invalid) &&
               invalid == CaptureGeometryFailure.InvalidWindow, "Native zero HWND was accepted");
        foreach (var screen in Screen.AllScreens)
        {
            foreach (var toolWindow in new[] { false, true })
            {
                using var form = new TestForm
                {
                    StartPosition = FormStartPosition.Manual,
                    FormBorderStyle = toolWindow ? FormBorderStyle.SizableToolWindow : FormBorderStyle.Sizable,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Bounds = new Rectangle(screen.WorkingArea.Left + 100, screen.WorkingArea.Top + 100, 400, 300),
                };
                form.Show();
                Application.DoEvents();
                Assert(Resizer.TryGetCaptureGeometry(form.Handle, out var capture, out var failure),
                    "Native capture failed: " + failure);
                var original = Resizer.GetRect(form.Handle);
                var placement = Resizer.GetPlacement(form.Handle);
                Assert(Equal(capture.PlacementRect, placement.Rect),
                    "Normal native capture does not match the existing placement coordinate system");
                var nativeMonitor = DpiNative.MonitorFromWindow(form.Handle, (uint)DpiNative.MONITOR_FLAGS.MONITOR_DEFAULTTONEAREST);
                Assert(NativeCaptureGeometryApi.Instance.TryGetMonitorInfo(nativeMonitor, out var bounds, out var work),
                    "Native monitor info failed");
                var dx = toolWindow ? 0 : work.Left - bounds.Left;
                var dy = toolWindow ? 0 : work.Top - bounds.Top;
                Assert(capture.Left == original.Left - dx && capture.Top == original.Top - dy &&
                       capture.Width == original.Right - original.Left && capture.Height == original.Bottom - original.Top,
                    "Native capture geometry mismatch");
                Assert(((NativeCaptureGeometryApi.Instance.GetExStyle(form.Handle) &
                         (uint)NativeMethods.WindowExStyles.WS_EX_TOOLWINDOW) != 0) == toolWindow,
                    "The native fixture has the wrong tool-window style");

                var otherScreen = Array.Find(Screen.AllScreens, item => item.DeviceName != screen.DeviceName);
                var moveX = otherScreen?.WorkingArea.Left + 120 ?? original.Left + 40;
                var moveY = otherScreen?.WorkingArea.Top + 120 ?? original.Top + 40;
                Assert(Resizer.MoveWindow(form.Handle, new Rect(moveX, moveY, moveX + 400, moveY + 300)),
                    "Native test move failed");
                Application.DoEvents();
                Assert(Resizer.SetPlacement(form.Handle, capture.PlacementRect, new WinPoint(0, 0), WindowState.Normal),
                    "Capture -> SetPlacement failed");
                Application.DoEvents();
                Assert(Equal(Resizer.GetRect(form.Handle), original), "Native Capture -> Apply changed the full window rectangle");
                Assert(Resizer.TryGetCaptureGeometry(form.Handle, out var recapture, out _) &&
                       Equal(recapture.PlacementRect, capture.PlacementRect), "Native repeated capture changed geometry");

                Assert(Resizer.TryGetCaptureGeometry(form.Handle, out var visibleCapture, out _, true),
                    "Native visible-bounds capture failed");
                Assert(Resizer.MoveWindow(form.Handle, new Rect(moveX + 30, moveY + 30, moveX + 430, moveY + 330)),
                    "Native visible-bounds test move failed");
                Application.DoEvents();
                ConfigFactory.Profiles.CompensateDwmFrameEffects = true;
                Assert(WindowUtils.ApplyPreset(form.Handle, new WindowPreset
                {
                    X = visibleCapture.Left,
                    Y = visibleCapture.Top,
                    Width = visibleCapture.Width,
                    Height = visibleCapture.Height,
                }), "Visible Capture -> compensated Preset Apply failed");
                Application.DoEvents();
                Assert(Resizer.TryGetCaptureGeometry(form.Handle, out var visibleRecapture, out _, true) &&
                       Equal(visibleRecapture.PlacementRect, visibleCapture.PlacementRect),
                    "Visible Capture -> compensated Apply changed visible bounds");

                form.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                Assert(!Resizer.TryGetCaptureGeometry(form.Handle, out _, out var minimized) &&
                       minimized == CaptureGeometryFailure.Minimized, "Native minimized window was accepted");
                form.WindowState = FormWindowState.Normal;
                form.WindowState = FormWindowState.Maximized;
                Application.DoEvents();
                Assert(!Resizer.TryGetCaptureGeometry(form.Handle, out _, out var maximized) &&
                       maximized == CaptureGeometryFailure.Maximized, "Native maximized window was accepted");
                form.Close();
            }
        }
        Console.WriteLine($"PASS: native Win32 normal/tool-window Capture -> Apply and minimized/maximized rejection " +
                          $"on {Screen.AllScreens.Length} available monitor(s). Mixed DPI/Snap require manual verification.");
    }

    private static bool Equal(Rect a, Rect b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    private sealed class FakeApi : ICaptureGeometryApi
    {
        public bool ValidWindow = true;
        public bool Visible = true;
        public bool Minimized;
        public bool Maximized;
        public bool RectSuccess = true;
        public bool MonitorInfoSuccess = true;
        public bool MaximizeAfterRectRead;
        public uint Style;
        public uint ExStyle;
        public Rect ScreenRect = new Rect(100, 120, 900, 720);
        public Rect VisibleScreenRect = new Rect(100, 120, 900, 720);
        public Rect Bounds = new Rect(0, 0, 1920, 1080);
        public Rect Work = new Rect(0, 0, 1920, 1040);
        public IntPtr Monitor = new IntPtr(456);
        public Rect LastMonitorRect;
        public int RectReads;
        public int VisibleRectReads;
        public bool VisibleRectSuccess = true;
        public int MonitorReads;
        public bool IsWindow(IntPtr handle) => ValidWindow;
        public bool IsWindowVisible(IntPtr handle) => Visible;
        public bool IsIconic(IntPtr handle) => Minimized;
        public bool IsZoomed(IntPtr handle) => Maximized;
        public uint GetStyle(IntPtr handle) => Style;
        public uint GetExStyle(IntPtr handle) => ExStyle;
        public bool TryGetWindowRect(IntPtr handle, out Rect rect)
        {
            RectReads++;
            rect = ScreenRect;
            if (MaximizeAfterRectRead) Maximized = true;
            return RectSuccess;
        }
        public bool TryGetVisibleWindowRect(IntPtr handle, out Rect rect)
        {
            VisibleRectReads++;
            rect = VisibleScreenRect;
            return VisibleRectSuccess;
        }
        public IntPtr MonitorFromRect(Rect screenRect)
        {
            MonitorReads++;
            LastMonitorRect = screenRect;
            return Monitor;
        }
        public bool TryGetMonitorInfo(IntPtr monitor, out Rect bounds, out Rect work)
        {
            bounds = Bounds;
            work = Work;
            return MonitorInfoSuccess;
        }
    }
}
