using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Windows;
using static WinResizer.Core.WindowControl.NativeMethods;
using static WinResizer.Core.Dpi.NativeMethods;
using WindowPlacement = WinResizer.Common.Windows.WindowPlacement;

namespace WinResizer.Core.WindowControl;

public static class Resizer
{
    /// <summary>
    ///    Get all open windows
    /// </summary>
    /// <returns></returns>
    public static List<IntPtr> GetOpenWindows()
    {
        var shellWindow = GetShellWindow();
        var windows = new List<IntPtr>();

        EnumWindows(delegate(IntPtr hWnd, IntPtr _)
        {
            if (hWnd == shellWindow) return true;
            if (!IsWindow(hWnd)) return true;
            if (!IsWindowVisible(hWnd)) return true;

            var length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Returns visible top-level windows which represent user-facing windows.
    /// Function-specific state rules (for example minimized Capture rejection)
    /// remain the responsibility of the caller.
    /// </summary>
    public static List<IntPtr> GetOpenUserWindows() =>
        GetOpenWindows().Where(IsEligibleForUserWindow).ToList();

    public static bool IsEligibleForUserWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return false;

        var rect = new Rect();
        if (!GetWindowRect(hWnd, ref rect))
            return false;

        return IsUserWindow(
            hWnd,
            GetAncestor(hWnd, (uint)GetAncestorFlags.GA_ROOT),
            GetWindowStyle(hWnd),
            GetWindowExStyle(hWnd),
            GetWindowClassName(hWnd) ?? string.Empty,
            IsWindowVisible(hWnd),
            IsWindowCloaked(hWnd),
            rect);
    }

    public static bool IsUserWindow(
        IntPtr handle,
        IntPtr root,
        uint style,
        uint exStyle,
        string className,
        bool visible,
        bool cloaked,
        Rect rect)
    {
        if (handle == IntPtr.Zero || root != handle || !visible || cloaked ||
            (style & (uint)WindowStyles.WS_CHILD) != 0 ||
            !HasValidSize(rect) || TechnicalWindowClasses.Contains(className))
        {
            return false;
        }

        var hasCaption = (style & (uint)WindowStyles.WS_CAPTION) != 0;
        var hasThickFrame = (style & (uint)WindowStyles.WS_THICKFRAME) != 0;
        var isToolWindow = (exStyle & (uint)WindowExStyles.WS_EX_TOOLWINDOW) != 0;
        var isAppWindow = (exStyle & (uint)WindowExStyles.WS_EX_APPWINDOW) != 0;
        var isNoActivate = (exStyle & (uint)WindowExStyles.WS_EX_NOACTIVATE) != 0;

        // Keep captioned user dialogs even when they are owned/tool windows.
        // Reject only the strong helper signature shared by Capture and Save All.
        return hasCaption || hasThickFrame || isAppWindow || (!isToolWindow && !isNoActivate);
    }

    public static IntPtr GetForegroundHandle()
    {
        return GetForegroundWindow();
    }

    public static bool IsChildWindow(IntPtr hWnd)
    {
        var r = GetParent(hWnd);
        return r != IntPtr.Zero;
    }

    /// <summary>
    /// Determines whether a window is a safe candidate for the automatic
    /// Processes/Auto Resize path. This intentionally does not classify every
    /// popup or owned window as technical; ambiguous windows are allowed to
    /// continue to the existing process/title matching logic.
    /// </summary>
    public static bool IsEligibleForAutoResize(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return false;

        var style = GetWindowStyle(hWnd);
        if ((style & (uint)WindowStyles.WS_CHILD) != 0)
            return false;

        var root = GetAncestor(hWnd, (uint)GetAncestorFlags.GA_ROOT);
        if (root != IntPtr.Zero && root != hWnd)
            return false;

        if (!IsWindowVisible(hWnd))
            return false;

        if (IsWindowCloaked(hWnd))
            return false;

        var rect = new Rect();
        if (!GetWindowRect(hWnd, ref rect))
            return false;

        var width = (long)rect.Right - rect.Left;
        var height = (long)rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return false;

        var exStyle = GetWindowExStyle(hWnd);
        var isPopup = (style & (uint)WindowStyles.WS_POPUP) != 0;
        var hasCaption = (style & (uint)WindowStyles.WS_CAPTION) != 0;
        var hasThickFrame = (style & (uint)WindowStyles.WS_THICKFRAME) != 0;
        var isDisabled = (style & (uint)WindowStyles.WS_DISABLED) != 0;
        var isToolWindow = (exStyle & (uint)WindowExStyles.WS_EX_TOOLWINDOW) != 0;
        var isAppWindow = (exStyle & (uint)WindowExStyles.WS_EX_APPWINDOW) != 0;
        var isNoActivate = (exStyle & (uint)WindowExStyles.WS_EX_NOACTIVATE) != 0;

        // A captionless, non-application popup which is also a tool/no-activate
        // window is an overwhelmingly strong technical-window signal. No
        // individual style flag is treated as a rejection on its own.
        if (isPopup && !hasCaption && !hasThickFrame && !isAppWindow
            && (isToolWindow || isNoActivate))
        {
            return false;
        }

        // WS_DISABLED is intentionally only read as a diagnostic signal. A
        // disabled top-level window may still be a valid user window.
        _ = isDisabled;
        return true;
    }

    public static IntPtr GetWindowOwner(IntPtr hWnd)
    {
        return hWnd == IntPtr.Zero
            ? IntPtr.Zero
            : GetWindow(hWnd, (uint)GetWindowCommands.GW_OWNER);
    }

    public static string? GetWindowClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return null;

        const int nChars = 256;
        var buffer = new StringBuilder(nChars);
        return GetClassName(hWnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    public static WindowState GetWindowState(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return WindowState.Normal;

        const int GWL_STYLE = -16;
        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        if ((style & (int)WindowStyles.WS_MAXIMIZE) == (int)WindowStyles.WS_MAXIMIZE)
        {
            return WindowState.Maximized;
        }

        return (style & (int)WindowStyles.WS_MINIMIZE) == (int)WindowStyles.WS_MINIMIZE
            ? WindowState.Minimized
            : WindowState.Normal;
    }

    public static bool IsWindowVisible(IntPtr hWnd)
    {
        return NativeMethods.IsWindowVisible(hWnd);
    }

    private static uint GetWindowStyle(IntPtr hWnd)
    {
        return unchecked((uint)GetWindowLongPtr(hWnd, -16).ToInt64());
    }

    private static uint GetWindowExStyle(IntPtr hWnd)
    {
        return unchecked((uint)GetWindowLongPtr(hWnd, -20).ToInt64());
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try
        {
            var result = DwmGetWindowAttribute(
                hWnd,
                (uint)DwmWindowAttributes.DWMWA_CLOAKED,
                out int cloaked,
                sizeof(int));

            return result == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public static string? GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return null;
        }

        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var titleLength = GetWindowTextLength(hWnd);
            if (titleLength <= 0)
            {
                if (!IsWindow(hWnd))
                {
                    return null;
                }

                continue;
            }

            if (titleLength == int.MaxValue)
            {
                return null;
            }

            var capacity = titleLength + 1;
            var title = new StringBuilder(capacity);
            var copied = GetWindowText(hWnd, title, capacity);
            if (copied <= 0)
            {
                if (!IsWindow(hWnd))
                {
                    return null;
                }

                continue;
            }

            if (copied < capacity - 1 || GetWindowTextLength(hWnd) <= copied)
            {
                return title.ToString();
            }
        }

        return null;
    }

    public static void MaximizeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        ShowWindow(hWnd, (int)ShowWindowCommands.ShowMaximized);
    }

    public static bool MoveWindow(IntPtr hWnd, Rect rect, bool compensateDwmFrameEffects = false)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return false;

        Func<bool> move = () =>
        {
            ShowWindow(hWnd, (int)ShowWindowCommands.Normal);
            var target = compensateDwmFrameEffects &&
                         WindowFrameGeometry.TryGetRawTarget(hWnd, rect, out var compensated)
                ? compensated
                : rect;
            var result = SetWindowPos(hWnd, 0, target.Left, target.Top,
                target.Right - target.Left, target.Bottom - target.Top,
                (int)SetWindowPosFlags.SWP_NOOWNERZORDER);
            if (!result || !compensateDwmFrameEffects ||
                !WindowFrameGeometry.TryGetRawTarget(hWnd, rect, out var corrected))
            {
                return result;
            }

            // A cross-monitor move can change DPI-scaled frame margins. Re-read
            // them at the destination and make one correcting placement pass.
            return SetWindowPos(hWnd, 0, corrected.Left, corrected.Top,
                       corrected.Right - corrected.Left, corrected.Bottom - corrected.Top,
                       (int)SetWindowPosFlags.SWP_NOOWNERZORDER);
        };
        return compensateDwmFrameEffects ? PhysicalPixelAction(move) : move();
    }

    public static string? GetProcessName(IntPtr hWnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
            using var proc = Process.GetProcessById((int)pid);
            return proc.MainModule?.ModuleName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryGetWindowProcessId(IntPtr hWnd, out uint processId)
    {
        processId = 0;
        return hWnd != IntPtr.Zero && IsWindow(hWnd) &&
               GetWindowThreadProcessId(hWnd, out processId) != 0 && processId != 0;
    }

    public static string? GetRealProcessName(IntPtr hWnd)
    {
        try
        {
            using var proc = GetRealProcess(hWnd);
            return proc?.MainModule?.ModuleName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the process represented by a window. The returned Process is caller-owned
    /// and must be disposed.
    /// </summary>
    public static Process? GetRealProcess(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return null;

        Process? foregroundProcess = null;
        try
        {
            _ = GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0)
                return null;

            foregroundProcess = Process.GetProcessById((int)pid);
            if (foregroundProcess.ProcessName == "ApplicationFrameHost")
                return GetRealProcess(foregroundProcess);

            var result = foregroundProcess;
            foregroundProcess = null;
            return result;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            foregroundProcess?.Dispose();
        }
    }

    private static Process? GetRealProcess(Process foregroundProcess)
    {
        if (foregroundProcess.MainWindowHandle == IntPtr.Zero)
            return null;

        Process? realProcess = null;
        try
        {
            EnumChildWindows(foregroundProcess.MainWindowHandle, (hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                var candidate = GetProcess(pid);
                if (candidate is null)
                    return true;

                try
                {
                    if (candidate.ProcessName == "ApplicationFrameHost")
                        return true;

                    realProcess?.Dispose();
                    realProcess = candidate;
                    candidate = null;
                }
                finally
                {
                    candidate?.Dispose();
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            realProcess?.Dispose();
            return null;
        }

        return realProcess;
    }

    private static Process? GetProcess(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Rect GetRect(IntPtr hWnd)
    {
        var rect = new Rect();
        GetWindowRect(hWnd, ref rect);
        return rect;
    }

    public static WindowPlacement GetPlacement(IntPtr hWnd, bool compensateDwmFrameEffects = false)
    {
        return compensateDwmFrameEffects
            ? PhysicalPixelAction(() => GetPlacementPhysical(hWnd, true))
            : GetPlacementPhysical(hWnd, false);
    }

    private static bool HasValidSize(Rect rect) =>
        (long)rect.Right - rect.Left > 0 && (long)rect.Bottom - rect.Top > 0;

    private static readonly HashSet<string> TechnicalWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "tooltips_class32",
        "#32768", "SysShadow", "IME", "MSCTFIME UI",
    };

    private static WindowPlacement GetPlacementPhysical(IntPtr hWnd, bool compensateDwmFrameEffects)
    {
        var nativePlacement = NativeMethods.WindowPlacement.Default;
        if (!GetWindowPlacement(hWnd, ref nativePlacement))
        {
            throw new WinResizerException($"Cannot get window placement of {hWnd}");
        }

        var state = nativePlacement.ShowCmd switch
        {
            ShowWindowCommands.ShowMaximized => WindowState.Maximized,
            ShowWindowCommands.ShowMinimized => WindowState.Minimized,
            _ => WindowState.Normal,
        };

        var userRect = nativePlacement.NormalPosition;
        if (compensateDwmFrameEffects)
        {
            var originalPlacement = nativePlacement;
            var temporarilyNormalized = state != WindowState.Normal;
            try
            {
                if (temporarilyNormalized)
                {
                    var normalPlacement = nativePlacement;
                    normalPlacement.ShowCmd = ShowWindowCommands.Normal;
                    if (!SetWindowPlacement(hWnd, ref normalPlacement))
                    {
                        return ToWindowPlacement(nativePlacement, state, userRect);
                    }

                    FlushDwm();
                }

                if (CaptureGeometryReader.TryRead(
                        hWnd,
                        NativeCaptureGeometryApi.Instance,
                        true,
                        out var visibleGeometry,
                        out _))
                {
                    userRect = visibleGeometry.PlacementRect;
                }
            }
            finally
            {
                if (temporarilyNormalized)
                {
                    if (!SetWindowPlacement(hWnd, ref originalPlacement))
                    {
                        ShowWindow(hWnd, state == WindowState.Maximized
                            ? (int)ShowWindowCommands.ShowMaximized
                            : (int)ShowWindowCommands.ShowMinimized);
                    }
                    FlushDwm();
                }
            }
        }

        return ToWindowPlacement(nativePlacement, state, userRect);
    }

    private static WindowPlacement ToWindowPlacement(
        NativeMethods.WindowPlacement nativePlacement,
        WindowState state,
        Rect rect) =>
        new()
        {
            Rect = rect,
            WindowState = state,
            MaximizedPosition = nativePlacement.MaxPosition,
        };

    private static void FlushDwm()
    {
        try
        {
            _ = DwmFlush();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Captures the current raw Win32 rectangle or, when enabled, the visible DWM frame bounds,
    /// in the coordinate system expected by SetPlacement for this window.
    /// Native reads are forced into a PMv2 physical-pixel context; no WPF DIPs or restore bounds are used.
    /// Minimized, maximized, hidden and child windows are not supported.
    /// </summary>
    public static bool TryGetCaptureGeometry(
        IntPtr hWnd,
        out CaptureGeometry geometry,
        out CaptureGeometryFailure failure,
        bool compensateDwmFrameEffects = false)
    {
        CaptureGeometry result = default;
        CaptureGeometryFailure resultFailure = default;
        Func<bool> capture = () => CaptureGeometryReader.TryRead(
            hWnd,
            NativeCaptureGeometryApi.Instance,
            compensateDwmFrameEffects,
            out result,
            out resultFailure);
        var success = compensateDwmFrameEffects ? PhysicalPixelAction(capture) : capture();
        geometry = result;
        failure = resultFailure;
        return success;
    }

    public static bool SetPlacement(
        IntPtr hWnd,
        Rect rect,
        Point maximizedPosition,
        WindowState state,
        bool compensateDwmFrameEffects = false)
    {
        if (hWnd == IntPtr.Zero)
            return false;

        Func<bool> place = () =>
        {
            // Read normal-window frame margins. Maximized borders are not representative.
            ShowWindow(hWnd, (int)ShowWindowCommands.Normal);
            var target = compensateDwmFrameEffects &&
                         WindowFrameGeometry.TryGetRawTarget(hWnd, rect, out var compensated)
                ? compensated
                : rect;

            var placement = NativeMethods.WindowPlacement.Default;
            placement.MaxPosition = maximizedPosition;
            placement.NormalPosition = target;
            var requestedShowCommand = state switch
            {
                WindowState.Maximized => ShowWindowCommands.ShowMaximized,
                WindowState.Minimized => ShowWindowCommands.ShowMinimized,
                _ => ShowWindowCommands.Normal,
            };
            // With compensation, establish the destination normal rectangle first.
            // This lets us re-read DPI-scaled frame margins before restoring Max/Min state.
            placement.ShowCmd = compensateDwmFrameEffects
                ? ShowWindowCommands.Normal
                : requestedShowCommand;

            var currentMonitor = MonitorFromWindow(hWnd, (uint)MONITOR_FLAGS.MONITOR_DEFAULTTONEAREST);
            if (!SetWindowPlacement(hWnd, ref placement))
                return false;

            // NormalPosition is workspace coordinates for ordinary top-level windows.
            // MonitorFromRect requires screen coordinates, so inspect the actual window
            // after applying instead. Keep the second placement pass on monitor changes.
            var targetMonitor = MonitorFromWindow(hWnd, (uint)MONITOR_FLAGS.MONITOR_DEFAULTTONEAREST);
            if (compensateDwmFrameEffects)
            {
                if (WindowFrameGeometry.TryGetRawTarget(hWnd, rect, out var corrected))
                {
                    // A cross-monitor move can change DPI-scaled frame margins. Re-read
                    // them at the destination and make one correcting placement pass.
                    placement.NormalPosition = corrected;
                    if (!SetWindowPlacement(hWnd, ref placement))
                        return false;
                }
                else if (currentMonitor != targetMonitor && !SetWindowPlacement(hWnd, ref placement))
                {
                    return false;
                }

                if (requestedShowCommand == ShowWindowCommands.Normal)
                    return true;

                placement.ShowCmd = requestedShowCommand;
                return SetWindowPlacement(hWnd, ref placement);
            }

            return currentMonitor == targetMonitor || SetWindowPlacement(hWnd, ref placement);
        };
        return compensateDwmFrameEffects ? PhysicalPixelAction(place) : place();
    }

    private static T PhysicalPixelAction<T>(Func<T> action)
    {
        // GetWindowRect is DPI-virtualized for unaware callers while DWM extended
        // frame bounds are physical pixels. A PMv2 thread context keeps both reads,
        // the configured target and the placement call in one physical-pixel space.
        var previous = IntPtr.Zero;
        try
        {
            previous = SetThreadDpiAwarenessContext(
                WinResizer.Core.Dpi.NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            return action();
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    public static bool IsForegroundFullScreen(Screen? screen = null)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindow(foreground))
            return false;

        screen ??= Screen.FromHandle(foreground);
        var bounds = screen.Bounds;
        var rect = GetRect(foreground);
        return rect.Left == bounds.Left
            && rect.Top == bounds.Top
            && rect.Right == bounds.Right
            && rect.Bottom == bounds.Bottom;
    }

    public static bool IsInvisibleProcess(string processName)
    {
        return InvisibleProcesses.Contains(processName);
    }

    private static readonly HashSet<string> InvisibleProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TEXTINPUTHOST.EXE"
    };
}
