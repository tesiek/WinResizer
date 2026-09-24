using System;

namespace WinResizer.Core.Theme;

public static class ThemeDetect
{
    public static bool IsDarkModeEnable()
    {
        try
        {
            return NativeMethods.ShouldSystemUseDarkMode();
        }
        catch
        {
            return false;
        }
    }
}
