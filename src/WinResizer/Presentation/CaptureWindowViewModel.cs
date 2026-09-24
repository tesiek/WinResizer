using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinResizer.Core.WindowControl;
using WinResizer.Services;

namespace WinResizer.Presentation;

internal sealed class CapturedWindow
{
    public CapturedWindow(CaptureWindowItem window, CaptureGeometry geometry)
    {
        Window = window;
        Geometry = geometry;
    }

    public CaptureWindowItem Window { get; }
    public CaptureGeometry Geometry { get; }
}

internal sealed class CaptureWindowViewModel : INotifyPropertyChanged
{
    private readonly ICaptureWindowSource _source;
    private CaptureWindowItem? _selectedWindow;
    private CaptureGeometry? _preview;
    private string _status;

    public CaptureWindowViewModel(ICaptureWindowSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Windows = new ObservableCollection<CaptureWindowItem>(source.GetWindows());
        _status = Windows.Count == 0 ? "No user windows are available for Capture." : "Select a window to preview its geometry.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<CaptureWindowItem> Windows { get; }
    public CaptureWindowItem? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (ReferenceEquals(_selectedWindow, value)) return;
            _selectedWindow = value;
            Changed();
            ReadSelected(out _);
        }
    }

    public string Top => _preview?.Top.ToString() ?? "—";
    public string Left => _preview?.Left.ToString() ?? "—";
    public string Width => _preview?.Width.ToString() ?? "—";
    public string Height => _preview?.Height.ToString() ?? "—";
    public string Status => _status;
    public bool CanCapture => _selectedWindow != null;

    public bool TryCaptureSelected(out CapturedWindow? result)
    {
        result = null;
        // Never return a cached preview: revalidate and read at the user's final click.
        if (!ReadSelected(out var geometry)) return false;
        result = new CapturedWindow(_selectedWindow!, geometry);
        return true;
    }

    private bool ReadSelected(out CaptureGeometry geometry)
    {
        geometry = default;
        _preview = null;
        var success = _selectedWindow != null && _source.TryCapture(_selectedWindow, out geometry, out _status);
        if (_selectedWindow is null) _status = "Select a window to preview its geometry.";
        if (success)
        {
            _preview = geometry;
            _status = "Normal and snapped windows are supported. Minimized and maximized windows must be restored first.";
        }

        foreach (var property in new[] { nameof(Top), nameof(Left), nameof(Width), nameof(Height), nameof(Status), nameof(CanCapture) })
            Changed(property);
        return success;
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
