using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using WinResizer.Common.Exceptions;
using WinResizer.Services;

namespace WinResizer.Presentation;

internal sealed class IgnoredWindowCaptureViewModel : INotifyPropertyChanged
{
    private readonly IIgnoredWindowCaptureSource _source;
    private readonly Action<IgnoredWindowCaptureItem> _save;
    private IgnoredWindowCaptureItem? _selected;
    private string _status;

    public IgnoredWindowCaptureViewModel(IIgnoredWindowCaptureSource source, Action<IgnoredWindowCaptureItem> save)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        Windows = new ObservableCollection<IgnoredWindowCaptureItem>(source.GetWindows());
        _status = Windows.Count == 0 ? "No user windows are available for Capture." : "Select a window. Metadata will be read again when you capture.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<IgnoredWindowCaptureItem> Windows { get; }
    public IgnoredWindowCaptureItem? SelectedWindow
    {
        get => _selected;
        set
        {
            _selected = value;
            foreach (var name in new[] { nameof(SelectedWindow), nameof(Process), nameof(Class), nameof(Title), nameof(CanCapture) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    public string Process => _selected?.Process ?? "—";
    public string Class => _selected?.Class ?? "—";
    public string Title => _selected?.Title ?? "—";
    public string Status => _status;
    public bool CanCapture => _selected is not null;

    public bool TryCaptureSelected()
    {
        if (_selected is null) return false;
        if (!_source.TryCapture(_selected, out var fresh, out var error)) { SetStatus(error); return false; }
        try { _save(fresh!); return true; }
        catch (Exception exception) when (exception is IOException || exception is WinResizerException ||
            exception is ArgumentException || exception is InvalidOperationException)
        {
            PortableLog.Append($"Ignored Windows Capture failed: {exception}");
            SetStatus(exception is IOException ? "Capture was not saved because the portable configuration could not be saved." : exception.Message);
            return false;
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }
}
