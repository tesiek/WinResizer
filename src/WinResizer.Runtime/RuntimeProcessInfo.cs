using WinResizer.Common.Windows;

namespace WinResizer.Runtime;

/// <summary>
/// Detached, read-only projection. ProcessToken is runtime-only: it survives
/// profile switches but must be reacquired after configuration replacement.
/// Coordinates are Win32 RECT edges, not WPF DIPs or width/height.
/// </summary>
public sealed class RuntimeProcessInfo
{
    internal RuntimeProcessInfo(string profileId, string processToken, string process,
        string title, Rect rect, bool autoResize, int delay, WindowState state,
        Point maximizedPosition)
    {
        ProfileId = profileId;
        ProcessToken = processToken;
        Process = process;
        Title = title;
        Top = rect.Top;
        Left = rect.Left;
        Right = rect.Right;
        Bottom = rect.Bottom;
        AutoResize = autoResize;
        Delay = delay;
        State = state;
        MaximizedPosition = maximizedPosition;
    }

    public string ProfileId { get; }
    public string ProcessToken { get; }
    public string Process { get; }
    public string Title { get; }
    public int Top { get; }
    public int Left { get; }
    public int Right { get; }
    public int Bottom { get; }
    public bool AutoResize { get; }
    public int Delay { get; }
    public WindowState State { get; }
    public Point MaximizedPosition { get; }
}
