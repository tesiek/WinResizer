using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinResizer.Services;

internal sealed class OsdWindow : Window, IOsdWindow
{
    private const double WorkAreaMargin = 16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly Image _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private bool _disposed;

    internal OsdWindow()
    {
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _icon = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _title = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
        };
        _message = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
        };

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_message);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(_icon);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        Content = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 82)),
            BorderThickness = new Thickness(1),
            Child = content,
        };

        SourceInitialized += OnSourceInitialized;
    }

    public void ShowNotification(string title, string message, ImageSource icon)
    {
        if (_disposed)
        {
            return;
        }

        _title.Text = title ?? string.Empty;
        _message.Text = message ?? string.Empty;
        _icon.Source = icon;

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionInWorkArea();
    }

    public void HideNotification()
    {
        if (!_disposed && IsVisible)
        {
            Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SourceInitialized -= OnSourceInitialized;
        Close();
    }

    private void PositionInWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - WorkAreaMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - WorkAreaMargin);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            handle,
            GwlExStyle,
            new IntPtr(extendedStyle | WsExToolWindow | WsExNoActivate));
    }

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));

    private static IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);
}
