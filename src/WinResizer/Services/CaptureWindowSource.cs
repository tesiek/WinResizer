using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using WinResizer.Core.WindowControl;
using WinResizer.Configuration;

namespace WinResizer.Services;

internal sealed class CaptureWindowItem
{
    public CaptureWindowItem(IntPtr handle, uint processId, string processName, string title)
    {
        Handle = handle;
        ProcessId = processId;
        ProcessName = processName;
        Title = title;
    }

    public IntPtr Handle { get; }
    public uint ProcessId { get; }
    public string ProcessName { get; }
    public string Title { get; }
    public string DisplayText => $"[{ProcessName}] — {Title}";
}

internal interface ICaptureWindowSource
{
    IReadOnlyList<CaptureWindowItem> GetWindows();
    bool TryCapture(CaptureWindowItem window, out CaptureGeometry geometry, out string error);
}

internal sealed class CaptureWindowSource : ICaptureWindowSource
{
    private readonly uint _ownProcessId = GetCurrentProcessId();
    private readonly Func<IntPtr> _getOwnMainWindowHandle;

    public CaptureWindowSource() : this(GetOwnMainWindowHandle) { }

    internal CaptureWindowSource(Func<IntPtr> getOwnMainWindowHandle)
    {
        _getOwnMainWindowHandle = getOwnMainWindowHandle;
    }

    public IReadOnlyList<CaptureWindowItem> GetWindows()
    {
        var windows = new List<CaptureWindowItem>();
        foreach (var handle in Resizer.GetOpenUserWindows())
        {
            if (TryGetItem(handle, out var item))
                windows.Add(item!);
        }

        windows.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayText, b.DisplayText));
        return windows;
    }

    public bool TryCapture(CaptureWindowItem window, out CaptureGeometry geometry, out string error)
    {
        geometry = default;
        if (!TryGetItem(window.Handle, out var current) || current!.ProcessId != window.ProcessId)
        {
            error = "The selected window was closed, replaced, or is no longer available for Capture.";
            return false;
        }

        // This helper is the only source of both preview and final geometry.
        if (!Resizer.TryGetCaptureGeometry(
                window.Handle,
                out geometry,
                out var failure,
                ConfigFactory.Profiles.CompensateDwmFrameEffects))
        {
            error = failure switch
            {
                CaptureGeometryFailure.Minimized => "Minimized windows cannot be captured. Restore the window and select it again.",
                CaptureGeometryFailure.Maximized => "Maximized windows cannot be captured. Restore the window and select it again.",
                _ => "The current window geometry could not be captured. The window may have closed or become unavailable.",
            };
            return false;
        }

        if (!Resizer.TryGetWindowProcessId(window.Handle, out var processId) || processId != window.ProcessId)
        {
            geometry = default;
            error = "The selected window was closed or replaced before Capture completed.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TryGetItem(IntPtr handle, out CaptureWindowItem? item)
    {
        try { return TryReadItem(handle, out item); }
        catch (InvalidOperationException) { item = null; return false; }
        catch (Win32Exception) { item = null; return false; }
    }

    private bool TryReadItem(IntPtr handle, out CaptureWindowItem? item)
    {
        item = null;
        if (!Resizer.TryGetWindowProcessId(handle, out var processId) ||
            !Resizer.IsEligibleForUserWindow(handle))
            return false;

        using var process = Resizer.GetRealProcess(handle);
        if (process is null ||
            !IsAllowedOwnWindow(processId, _ownProcessId, handle, _getOwnMainWindowHandle()) ||
            (processId != _ownProcessId && !IsUserProcess(processId, _ownProcessId, process.ProcessName)))
            return false;

        var title = Resizer.GetWindowTitle(handle) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        item = new CaptureWindowItem(handle, processId, process.ProcessName, title);
        return true;
    }

    internal static bool IsUserProcess(uint processId, uint ownProcessId, string name) =>
        processId != ownProcessId && !name.StartsWith("WinResizer", StringComparison.OrdinalIgnoreCase) &&
        !Resizer.IsInvisibleProcess(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");

    internal static bool IsUserWindow(IntPtr handle, IntPtr root, uint style, uint exStyle, string className, bool cloaked)
        => Resizer.IsUserWindow(handle, root, style, exStyle, className, true, cloaked,
            new WinResizer.Common.Windows.Rect(0, 0, 1, 1));

    internal static bool IsAllowedOwnWindow(uint processId, uint ownProcessId, IntPtr handle, IntPtr mainWindowHandle) =>
        processId != ownProcessId || (mainWindowHandle != IntPtr.Zero && handle == mainWindowHandle);

    private static IntPtr GetOwnMainWindowHandle()
    {
        var mainWindow = Application.Current?.MainWindow;
        return mainWindow is null ? IntPtr.Zero : new WindowInteropHelper(mainWindow).Handle;
    }

    private static uint GetCurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return (uint)process.Id;
    }

}
