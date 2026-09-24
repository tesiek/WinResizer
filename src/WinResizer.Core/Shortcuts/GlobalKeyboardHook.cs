using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static WinResizer.Core.Shortcuts.NativeMethods;

namespace WinResizer.Core.Shortcuts;

public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly KeyboardHookProc _hookProc;
    private IntPtr _hooked;
    private bool _disposed;

    public event KeyEventHandler? KeyDown;

    public event KeyEventHandler? KeyUp;

    public GlobalKeyboardHook()
    {
        _hookProc = HookProc;
    }

    public void Hook()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GlobalKeyboardHook));
        if (_hooked != IntPtr.Zero)
            return;

        var hooked = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
        if (hooked == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _hooked = hooked;
    }

    public void UnHook()
    {
        if (_hooked == IntPtr.Zero)
            return;

        if (!UnhookWindowsHookEx(_hooked))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _hooked = IntPtr.Zero;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hooked, code, wParam, lParam);
        }

        var hookData = Marshal.PtrToStructure<KeyboardHookStruct>(lParam);
        Keys key = (Keys)hookData.vkCode;
        KeyEventArgs keyEventArgs = new KeyEventArgs(key);
        var message = wParam.ToInt64();
        if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && KeyDown != null)
        {
            KeyDown(this, keyEventArgs);
        }
        else if ((message == WM_KEYUP || message == WM_SYSKEYUP) && KeyUp != null)
        {
            KeyUp(this, keyEventArgs);
        }

        return keyEventArgs.Handled ? new IntPtr(1) : CallNextHookEx(_hooked, code, wParam, lParam);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            UnHook();
        }
        else if (_hooked != IntPtr.Zero)
        {
            try
            {
                _ = UnhookWindowsHookEx(_hooked);
            }
            catch
            {
                // A finalizer must not allow native cleanup failures to escape.
            }

            _hooked = IntPtr.Zero;
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~GlobalKeyboardHook()
    {
        Dispose(false);
    }
}
