using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;

namespace WinResizer.Runtime;

public sealed class RuntimeHotkeyInfo
{
    public RuntimeHotkeyInfo(HotkeysType type, Hotkeys? hotkey)
    {
        Type = type;
        Hotkey = hotkey;
    }

    public HotkeysType Type { get; }

    public Hotkeys? Hotkey { get; }
}
