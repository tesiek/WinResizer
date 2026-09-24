using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinResizer.Core.WindowControl;

namespace WinResizer.Services;

internal sealed class IgnoredWindowCaptureItem
{
    public IgnoredWindowCaptureItem(IntPtr handle, uint processId, string process, string className, string title)
    {
        Handle = handle;
        ProcessId = processId;
        Process = process;
        Class = className;
        Title = title;
    }

    public IntPtr Handle { get; }
    public uint ProcessId { get; }
    public string Process { get; }
    public string Class { get; }
    public string Title { get; }
    public string DisplayText => $"[{Process}] [{Class}] — {Title}";
}

internal interface IIgnoredWindowCaptureSource
{
    IReadOnlyList<IgnoredWindowCaptureItem> GetWindows();
    bool TryCapture(IgnoredWindowCaptureItem preview, out IgnoredWindowCaptureItem? current, out string error);
}

internal sealed class IgnoredWindowCaptureSource : IIgnoredWindowCaptureSource
{
    private readonly uint _ownProcessId;
    public IgnoredWindowCaptureSource() : this(GetCurrentProcessId()) { }
    internal IgnoredWindowCaptureSource(uint ownProcessId) => _ownProcessId = ownProcessId;

    public IReadOnlyList<IgnoredWindowCaptureItem> GetWindows()
    {
        var result = new List<IgnoredWindowCaptureItem>();
        // Resizer.GetOpenWindows excludes empty titles; metadata Capture must include them.
        EnumWindows((handle, _) =>
        {
            if (TryRead(handle, out var item)) result.Add(item!);
            return true;
        }, IntPtr.Zero);
        result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayText, b.DisplayText));
        return result;
    }

    public bool TryCapture(IgnoredWindowCaptureItem preview, out IgnoredWindowCaptureItem? current, out string error)
    {
        current = null;
        error = "The selected window was closed, replaced, or is no longer available. Select another window.";
        // Read all metadata afresh, bracketed by HWND/PID checks inside TryRead.
        if (!Resizer.TryGetWindowProcessId(preview.Handle, out var pid) || pid != preview.ProcessId ||
            !TryRead(preview.Handle, out var fresh) || fresh!.ProcessId != preview.ProcessId)
            return false;
        current = fresh;
        error = string.Empty;
        return true;
    }

    private bool TryRead(IntPtr handle, out IgnoredWindowCaptureItem? item)
    {
        item = null;
        try
        {
            if (!Resizer.TryGetWindowProcessId(handle, out var pid) || pid == _ownProcessId ||
                !Resizer.IsWindowVisible(handle)) return false;
            var className = Resizer.GetWindowClassName(handle);
            if (className is null || !IsUserWindow(handle, GetAncestor(handle, 2), GetWindowLong(handle, -16),
                GetWindowLong(handle, -20), className, IsCloaked(handle))) return false;
            using var process = Resizer.GetRealProcess(handle);
            if (process is null || !CaptureWindowSource.IsUserProcess(pid, _ownProcessId, process.ProcessName) ||
                process.Id == _ownProcessId) return false;
            var name = process.MainModule?.ModuleName;
            if (string.IsNullOrWhiteSpace(name)) return false;
            var title = Resizer.GetWindowTitle(handle) ?? string.Empty;
            if (!Resizer.TryGetWindowProcessId(handle, out var after) || after != pid) return false;
            item = new IgnoredWindowCaptureItem(handle, pid, name!, className, title);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    // Share helper classification only, not Presets' title or capture-geometry restrictions.
    // Captioned/framed owned/tool popups and maximized user windows remain eligible.
    internal static bool IsUserWindow(IntPtr handle, IntPtr root, uint style, uint exStyle, string className, bool cloaked) =>
        CaptureWindowSource.IsUserWindow(handle, root, style, exStyle, className, cloaked);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern uint GetWindowLong(IntPtr handle, int index);
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out int value, int size);
    private static bool IsCloaked(IntPtr handle)
    {
        try { return DwmGetWindowAttribute(handle, 14, out var value, sizeof(int)) == 0 && value != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static uint GetCurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return (uint)process.Id;
    }
}
