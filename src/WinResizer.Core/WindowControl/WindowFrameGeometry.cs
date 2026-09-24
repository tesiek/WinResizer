using System;
using System.Runtime.InteropServices;
using WinResizer.Common.Windows;
using static WinResizer.Core.WindowControl.NativeMethods;

namespace WinResizer.Core.WindowControl;

/// <summary>
/// Converts user-visible DWM bounds to the raw Win32 rectangle used by window placement.
/// All native reads are performed by the caller in a physical-pixel DPI context.
/// </summary>
internal static class WindowFrameGeometry
{
    internal static bool TryGetRawTarget(IntPtr handle, Rect visibleTarget, out Rect rawTarget)
    {
        rawTarget = visibleTarget;
        var raw = default(Rect);
        if (!GetWindowRect(handle, ref raw) || !HasValidSize(raw) ||
            !TryGetVisibleRect(handle, out var visible))
        {
            return false;
        }

        return TryCalculateRawTarget(raw, visible, visibleTarget, out rawTarget);
    }

    internal static bool TryGetVisibleTarget(IntPtr handle, Rect rawTarget, out Rect visibleTarget)
    {
        visibleTarget = rawTarget;
        var raw = default(Rect);
        if (!GetWindowRect(handle, ref raw) || !HasValidSize(raw) ||
            !TryGetVisibleRect(handle, out var visible))
        {
            return false;
        }

        return TryCalculateVisibleTarget(raw, visible, rawTarget, out visibleTarget);
    }

    internal static bool TryGetVisibleRect(IntPtr handle, out Rect visible)
    {
        visible = default;
        try
        {
            return DwmGetWindowAttribute(
                       handle,
                       (uint)DwmWindowAttributes.DWMWA_EXTENDED_FRAME_BOUNDS,
                       out visible,
                       Marshal.SizeOf(typeof(Rect))) == 0 &&
                   HasValidSize(visible);
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

    internal static bool TryCalculateRawTarget(
        Rect currentRaw,
        Rect currentVisible,
        Rect visibleTarget,
        out Rect rawTarget)
    {
        rawTarget = visibleTarget;
        if (!HasValidSize(currentRaw) || !HasValidSize(currentVisible) || !HasValidSize(visibleTarget))
        {
            return false;
        }

        try
        {
            var leftMargin = (long)currentVisible.Left - currentRaw.Left;
            var topMargin = (long)currentVisible.Top - currentRaw.Top;
            var rightMargin = (long)currentRaw.Right - currentVisible.Right;
            var bottomMargin = (long)currentRaw.Bottom - currentVisible.Bottom;
            rawTarget = new Rect(
                checked((int)(visibleTarget.Left - leftMargin)),
                checked((int)(visibleTarget.Top - topMargin)),
                checked((int)(visibleTarget.Right + rightMargin)),
                checked((int)(visibleTarget.Bottom + bottomMargin)));
            return HasValidSize(rawTarget);
        }
        catch (OverflowException)
        {
            rawTarget = visibleTarget;
            return false;
        }
    }

    internal static bool TryCalculateVisibleTarget(
        Rect currentRaw,
        Rect currentVisible,
        Rect rawTarget,
        out Rect visibleTarget)
    {
        visibleTarget = rawTarget;
        if (!HasValidSize(currentRaw) || !HasValidSize(currentVisible) || !HasValidSize(rawTarget))
        {
            return false;
        }

        try
        {
            var leftMargin = (long)currentVisible.Left - currentRaw.Left;
            var topMargin = (long)currentVisible.Top - currentRaw.Top;
            var rightMargin = (long)currentRaw.Right - currentVisible.Right;
            var bottomMargin = (long)currentRaw.Bottom - currentVisible.Bottom;
            visibleTarget = new Rect(
                checked((int)(rawTarget.Left + leftMargin)),
                checked((int)(rawTarget.Top + topMargin)),
                checked((int)(rawTarget.Right - rightMargin)),
                checked((int)(rawTarget.Bottom - bottomMargin)));
            return HasValidSize(visibleTarget);
        }
        catch (OverflowException)
        {
            visibleTarget = rawTarget;
            return false;
        }
    }

    private static bool HasValidSize(Rect rect) =>
        (long)rect.Right - rect.Left > 0 && (long)rect.Right - rect.Left <= int.MaxValue &&
        (long)rect.Bottom - rect.Top > 0 && (long)rect.Bottom - rect.Top <= int.MaxValue;
}
