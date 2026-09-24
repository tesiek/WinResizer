using System;
using System.Runtime.InteropServices;
using WinResizer.Common.Windows;
using DpiNative = WinResizer.Core.Dpi.NativeMethods;

namespace WinResizer.Core.WindowControl;

// Instance-scoped seam keeps failure and coordinate tests independent of desktop settings.
internal interface ICaptureGeometryApi
{
    bool IsWindow(IntPtr handle);
    bool IsWindowVisible(IntPtr handle);
    bool IsIconic(IntPtr handle);
    bool IsZoomed(IntPtr handle);
    uint GetStyle(IntPtr handle);
    uint GetExStyle(IntPtr handle);
    bool TryGetWindowRect(IntPtr handle, out Rect rect);
    bool TryGetVisibleWindowRect(IntPtr handle, out Rect rect);
    IntPtr MonitorFromRect(Rect screenRect);
    bool TryGetMonitorInfo(IntPtr monitor, out Rect bounds, out Rect work);
}

internal static class CaptureGeometryReader
{
    internal static bool TryRead(
        IntPtr handle,
        ICaptureGeometryApi api,
        out CaptureGeometry geometry,
        out CaptureGeometryFailure failure) =>
        TryRead(handle, api, false, out geometry, out failure);

    internal static bool TryRead(
        IntPtr handle,
        ICaptureGeometryApi api,
        bool compensateDwmFrameEffects,
        out CaptureGeometry geometry,
        out CaptureGeometryFailure failure)
    {
        geometry = default;
        failure = ValidateWindow(handle, api);
        if (failure != CaptureGeometryFailure.None)
            return false;

        if (!api.TryGetWindowRect(handle, out var rawScreenRect))
        {
            failure = CaptureGeometryFailure.WindowRectUnavailable;
            return false;
        }

        if (!HasValidSize(rawScreenRect))
        {
            failure = CaptureGeometryFailure.InvalidGeometry;
            return false;
        }

        // DWM failure deliberately falls back to the legacy raw rectangle.
        var screenRect = compensateDwmFrameEffects &&
                         api.TryGetVisibleWindowRect(handle, out var visibleScreenRect)
            ? visibleScreenRect
            : rawScreenRect;
        if (!HasValidSize(screenRect))
        {
            failure = CaptureGeometryFailure.InvalidGeometry;
            return false;
        }

        // Always pass the unconverted virtual-screen rectangle to Win32.
        var monitor = api.MonitorFromRect(screenRect);
        if (monitor == IntPtr.Zero || !api.TryGetMonitorInfo(monitor, out var bounds, out var work))
        {
            failure = CaptureGeometryFailure.MonitorUnavailable;
            return false;
        }

        if (!HasValidSize(bounds) || !HasValidSize(work))
        {
            failure = CaptureGeometryFailure.InvalidGeometry;
            return false;
        }

        failure = ValidateWindow(handle, api);
        if (failure != CaptureGeometryFailure.None)
            return false;

        var toolWindow = (api.GetExStyle(handle) & (uint)NativeMethods.WindowExStyles.WS_EX_TOOLWINDOW) != 0;
        // Subtract only the reserved top/left inset, NOT the monitor/work origin.
        // This preserves virtual-desktop coordinates on secondary/negative monitors.
        var offsetX = toolWindow ? 0L : (long)work.Left - bounds.Left;
        var offsetY = toolWindow ? 0L : (long)work.Top - bounds.Top;
        try
        {
            var placementRect = new Rect(
                checked((int)(screenRect.Left - offsetX)),
                checked((int)(screenRect.Top - offsetY)),
                checked((int)(screenRect.Right - offsetX)),
                checked((int)(screenRect.Bottom - offsetY)));
            geometry = new CaptureGeometry(placementRect);
            return true;
        }
        catch (OverflowException)
        {
            failure = CaptureGeometryFailure.InvalidGeometry;
            return false;
        }
    }

    private static CaptureGeometryFailure ValidateWindow(IntPtr handle, ICaptureGeometryApi api)
    {
        if (handle == IntPtr.Zero || !api.IsWindow(handle))
            return CaptureGeometryFailure.InvalidWindow;
        if ((api.GetStyle(handle) & (uint)NativeMethods.WindowStyles.WS_CHILD) != 0)
            return CaptureGeometryFailure.NotTopLevel;
        if (api.IsIconic(handle))
            return CaptureGeometryFailure.Minimized;
        if (api.IsZoomed(handle))
            return CaptureGeometryFailure.Maximized;
        return api.IsWindowVisible(handle) ? CaptureGeometryFailure.None : CaptureGeometryFailure.NotVisible;
    }

    private static bool HasValidSize(Rect rect) =>
        (long)rect.Right - rect.Left > 0 && (long)rect.Right - rect.Left <= int.MaxValue &&
        (long)rect.Bottom - rect.Top > 0 && (long)rect.Bottom - rect.Top <= int.MaxValue;
}

internal sealed class NativeCaptureGeometryApi : ICaptureGeometryApi
{
    internal static readonly NativeCaptureGeometryApi Instance = new NativeCaptureGeometryApi();

    private NativeCaptureGeometryApi() { }

    public bool IsWindow(IntPtr handle) => NativeMethods.IsWindow(handle);
    public bool IsWindowVisible(IntPtr handle) => NativeMethods.IsWindowVisible(handle);
    public bool IsIconic(IntPtr handle) => NativeMethods.IsIconic(handle);
    public bool IsZoomed(IntPtr handle) => NativeMethods.IsZoomed(handle);
    public uint GetStyle(IntPtr handle) => unchecked((uint)NativeMethods.GetWindowLongPtr(handle, -16).ToInt64());
    public uint GetExStyle(IntPtr handle) => unchecked((uint)NativeMethods.GetWindowLongPtr(handle, -20).ToInt64());

    public bool TryGetWindowRect(IntPtr handle, out Rect rect)
    {
        rect = default;
        return NativeMethods.GetWindowRect(handle, ref rect);
    }

    public bool TryGetVisibleWindowRect(IntPtr handle, out Rect rect) =>
        WindowFrameGeometry.TryGetVisibleRect(handle, out rect);

    public IntPtr MonitorFromRect(Rect screenRect) =>
        DpiNative.MonitorFromRect(ref screenRect, (uint)DpiNative.MONITOR_FLAGS.MONITOR_DEFAULTTONEAREST);

    public bool TryGetMonitorInfo(IntPtr monitor, out Rect bounds, out Rect work)
    {
        var info = new DpiNative.MonitorInfo { Size = Marshal.SizeOf(typeof(DpiNative.MonitorInfo)) };
        var success = DpiNative.GetMonitorInfo(monitor, ref info);
        bounds = info.Monitor;
        work = info.Work;
        return success;
    }
}
