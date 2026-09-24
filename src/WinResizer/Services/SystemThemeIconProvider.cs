using System;
using Microsoft.Win32;

namespace WinResizer.Services;

internal enum SystemThemeIconVariant
{
    DarkSymbol,
    LightSymbol,
}

internal static class SystemThemeIconProvider
{
    internal static SystemThemeIconVariant SelectIconVariant(int? systemUsesLightTheme) =>
        systemUsesLightTheme == 0
            ? SystemThemeIconVariant.LightSymbol
            : SystemThemeIconVariant.DarkSymbol;

    internal static int ReadSystemUsesLightThemeOrDefault() =>
        TryReadSystemUsesLightTheme(out var value) ? value : 1;

    internal static bool TryReadSystemUsesLightTheme(out int value)
    {
        value = 1;
        try
        {
            using (var personalize = Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                       writable: false))
            {
                var raw = personalize?.GetValue(
                    "SystemUsesLightTheme",
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (raw is int intValue && (intValue == 0 || intValue == 1))
                {
                    value = intValue;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // A registry read failure keeps the safe light-system fallback.
        }

        return false;
    }
}
