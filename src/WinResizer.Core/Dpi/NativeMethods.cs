using System;
using System.Runtime.InteropServices;
using WinResizer.Common.Windows;

namespace WinResizer.Core.Dpi;

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("SHCore.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwareness(PROCESS_DPI_AWARENESS awareness);

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDPIAware();

    [DllImport("SHcore.dll")]
    internal static extern int GetProcessDpiAwareness(IntPtr hWnd, out PROCESS_DPI_AWARENESS value);

    [DllImport("user32.dll")]
    internal static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiContextA, IntPtr dpiContextB);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromRect([In] ref Rect rect, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    internal enum PROCESS_DPI_AWARENESS
    {
        Process_DPI_Unaware = 0,
        Process_System_DPI_Aware = 1,
        Process_Per_Monitor_DPI_Aware = 2
    }

    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    internal enum MONITOR_FLAGS
    {
        MONITOR_DEFAULTTONULL = 0,
        MONITOR_DEFAULTTOPRIMARY = 1,
        MONITOR_DEFAULTTONEAREST = 2,
    }
}
