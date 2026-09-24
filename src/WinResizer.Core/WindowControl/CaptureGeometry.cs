using WinResizer.Common.Windows;

namespace WinResizer.Core.WindowControl;

/// <summary>
/// Current raw or visible window bounds converted for SetPlacement. Width/Height are pixels;
/// Top/Left are workspace coordinates, or screen coordinates for WS_EX_TOOLWINDOW.
/// </summary>
public readonly struct CaptureGeometry
{
    internal CaptureGeometry(Rect placementRect)
    {
        PlacementRect = placementRect;
        Left = placementRect.Left;
        Top = placementRect.Top;
        Width = checked(placementRect.Right - placementRect.Left);
        Height = checked(placementRect.Bottom - placementRect.Top);
    }

    public int Top { get; }
    public int Left { get; }
    public int Width { get; }
    public int Height { get; }
    public Rect PlacementRect { get; }
}

public enum CaptureGeometryFailure
{
    None,
    InvalidWindow,
    NotTopLevel,
    NotVisible,
    Minimized,
    Maximized,
    WindowRectUnavailable,
    MonitorUnavailable,
    InvalidGeometry,
}
