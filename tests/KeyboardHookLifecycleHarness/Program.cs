using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using WinResizer.Core.Shortcuts;

namespace KeyboardHookLifecycleHarness;

internal static class Program
{
    private const int Iterations = 200;
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        try
        {
            VerifyAbi();
            VerifyLifecycle();
            VerifyStress();
            Console.WriteLine($"PASS: keyboard hook x64 ABI and lifecycle ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyAbi()
    {
        var assembly = typeof(GlobalKeyboardHook).Assembly;
        var nativeMethods = assembly.GetType("WinResizer.Core.Shortcuts.NativeMethods", throwOnError: true)!;
        var callback = nativeMethods.GetNestedType("KeyboardHookProc", BindingFlags.NonPublic)!;
        var callbackInvoke = callback.GetMethod("Invoke")!;
        Assert(callbackInvoke.ReturnType == typeof(IntPtr), "Hook callback does not return pointer-sized LRESULT.");
        Assert(callbackInvoke.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(int), typeof(IntPtr), typeof(IntPtr) }),
            "Hook callback WPARAM/LPARAM parameters are not pointer-sized.");
        Assert(callback.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention == CallingConvention.Winapi,
            "Hook callback does not use the WinAPI calling convention.");

        var setHook = nativeMethods.GetMethod("SetWindowsHookEx", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert(setHook.ReturnType == typeof(IntPtr), "SetWindowsHookEx HHOOK is not pointer-sized.");
        Assert(setHook.GetParameters()[2].ParameterType == typeof(IntPtr),
            "SetWindowsHookEx HINSTANCE is not pointer-sized.");
        Assert(setHook.GetCustomAttribute<DllImportAttribute>()?.SetLastError == true,
            "SetWindowsHookEx does not preserve the Win32 error code.");

        var unhook = nativeMethods.GetMethod("UnhookWindowsHookEx", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert(unhook.GetParameters().Single().ParameterType == typeof(IntPtr),
            "UnhookWindowsHookEx HHOOK is not pointer-sized.");
        Assert(unhook.GetCustomAttribute<DllImportAttribute>()?.SetLastError == true,
            "UnhookWindowsHookEx does not preserve the Win32 error code.");

        var callNext = nativeMethods.GetMethod("CallNextHookEx", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert(callNext.ReturnType == typeof(IntPtr), "CallNextHookEx does not return pointer-sized LRESULT.");
        Assert(callNext.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(IntPtr) }),
            "CallNextHookEx HHOOK/WPARAM/LPARAM types do not match x64 ABI.");

        Assert(typeof(KeyboardHookStruct).GetField(nameof(KeyboardHookStruct.dwExtraInfo))!.FieldType == typeof(UIntPtr),
            "KBDLLHOOKSTRUCT.dwExtraInfo is not ULONG_PTR-sized.");
        Assert(typeof(GlobalKeyboardHook).GetField("_hookProc", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "The native callback delegate is not rooted in an instance field.");
    }

    private static void VerifyLifecycle()
    {
        var hook = new GlobalKeyboardHook();
        hook.Hook();
        Assert(ReadHookHandle(hook) != IntPtr.Zero, "Hook did not retain the installed native handle.");
        var firstHandle = ReadHookHandle(hook);
        hook.Hook();
        Assert(ReadHookHandle(hook) == firstHandle, "A repeated Hook call installed a second hook.");
        hook.UnHook();
        Assert(ReadHookHandle(hook) == IntPtr.Zero, "UnHook did not clear the native handle.");
        hook.UnHook();
        Assert(ReadHookHandle(hook) == IntPtr.Zero, "A repeated UnHook was not a no-op.");
        hook.Dispose();
        hook.Dispose();
        Assert(ReadHookHandle(hook) == IntPtr.Zero, "Repeated Dispose restored or retained a hook handle.");

        var disposeDirectly = new GlobalKeyboardHook();
        disposeDirectly.Hook();
        disposeDirectly.Dispose();
        disposeDirectly.Dispose();
        Assert(ReadHookHandle(disposeDirectly) == IntPtr.Zero, "Dispose did not remove the active hook.");

        var unhookThenDispose = new GlobalKeyboardHook();
        unhookThenDispose.Hook();
        unhookThenDispose.UnHook();
        unhookThenDispose.Dispose();
        Assert(ReadHookHandle(unhookThenDispose) == IntPtr.Zero, "Dispose after UnHook was not idempotent.");

        try
        {
            unhookThenDispose.Hook();
            throw new InvalidOperationException("Hook after Dispose did not fail.");
        }
        catch (ObjectDisposedException)
        {
            _assertions++;
        }
    }

    private static void VerifyStress()
    {
        CollectFinalizers();
        var before = ReadCurrentHandleCount();
        for (var index = 0; index < Iterations; index++)
        {
            using var hook = new GlobalKeyboardHook();
            hook.Hook();
            Assert(ReadHookHandle(hook) != IntPtr.Zero, $"Iteration {index}: hook installation failed.");
            hook.UnHook();
            hook.UnHook();
            Assert(ReadHookHandle(hook) == IntPtr.Zero, $"Iteration {index}: stale hook handle remained.");
        }

        var immediate = ReadCurrentHandleCount();
        CollectFinalizers();
        var afterFinalizers = ReadCurrentHandleCount();
        Assert(immediate - before <= 24,
            $"Hook stress left unbounded immediate handle growth: {before} -> {immediate}.");
        Assert(afterFinalizers - before <= 24,
            $"Hook stress left unbounded handle growth: {before} -> {afterFinalizers}.");
        Console.WriteLine($"TRACE hook handles ({Iterations} iterations): {before} -> {immediate} -> {afterFinalizers}");
    }

    private static IntPtr ReadHookHandle(GlobalKeyboardHook hook) =>
        (IntPtr)typeof(GlobalKeyboardHook)
            .GetField("_hooked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(hook)!;

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

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
