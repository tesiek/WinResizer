using System;
using Newtonsoft.Json;
using WinResizer.Common.Shortcuts;
using WinResizer.Common.Windows;

namespace WinResizer.Configuration;

public class WindowPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public Hotkeys? Hotkey { get; set; }

    [JsonIgnore]
    public Rect Rect => new(X, Y, X + Width, Y + Height);
}
