using System;
using System.Runtime.InteropServices;

namespace WinResizer.Core.Shortcuts;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc callback, IntPtr hInstance, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hInstance);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr idHook, int nCode, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x100;
    internal const int WM_KEYUP = 0x101;
    internal const int WM_SYSKEYDOWN = 0x104;
    internal const int WM_SYSKEYUP = 0x105;
}

[StructLayout(LayoutKind.Sequential)]
public struct KeyboardHookStruct
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public UIntPtr dwExtraInfo;
}
