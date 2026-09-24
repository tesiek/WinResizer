using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;

namespace WinResizer.Core.Shortcuts;

/// <summary>Validates new assignments only; existing configuration registration is unchanged.</summary>
public static class HotkeyAssignmentPolicy
{
    public static void Validate(Hotkeys? hotkey)
    {
        if (hotkey is null) return;

        var modifiers = hotkey.GetModifierKeys();
        if ((modifiers & (ModifierKeys.Ctrl | ModifierKeys.Alt)) == (ModifierKeys.Ctrl | ModifierKeys.Alt) &&
            (modifiers & ModifierKeys.Win) == 0)
        {
            throw new HotkeyNotAllowedException();
        }
    }
}
