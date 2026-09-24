using System;
using System.Windows.Forms;

namespace WinResizer.Configuration;

/// <summary>The main-key contract shared by candidate validation and runtime registration.</summary>
internal static class HotkeyMainKey
{
    public static bool TryParse(string? value, out Keys key)
    {
        if (!Enum.TryParse(value, true, out key) ||
            !Enum.IsDefined(typeof(Keys), key) ||
            key == Keys.None || key == Keys.KeyCode || key == Keys.Modifiers ||
            (key & Keys.Modifiers) != Keys.None || IsModifierOnly(key))
        {
            key = Keys.None;
            return false;
        }

        return true;
    }

    private static bool IsModifierOnly(Keys key) =>
        key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin;
}
