using WinResizer.Common.Shortcuts;

namespace WinResizer.Runtime;

/// <summary>
/// Read-only runtime view of a preset. The configuration model remains owned
/// by the runtime and is never exposed to presentation code.
/// </summary>
public sealed class RuntimePresetInfo
{
    public RuntimePresetInfo(
        string presetId,
        string name,
        bool enabled,
        int x,
        int y,
        int width,
        int height,
        Hotkeys? hotkey)
    {
        PresetId = presetId;
        Name = name;
        Enabled = enabled;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Hotkey = hotkey;
    }

    public string PresetId { get; }

    public string Name { get; }

    public bool Enabled { get; }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public Hotkeys? Hotkey { get; }
}
