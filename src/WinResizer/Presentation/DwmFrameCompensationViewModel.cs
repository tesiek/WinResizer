using System;
using System.ComponentModel;
using System.Windows.Threading;
using WinResizer.Runtime;

namespace WinResizer.Presentation;

public sealed class DwmFrameCompensationViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public DwmFrameCompensationViewModel(WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.DwmFrameCompensationChanged += OnChanged;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CompensateDwmFrameEffects { get; private set; }

    public void SetCompensateDwmFrameEffects(bool enabled)
    {
        if (_disposed || enabled == CompensateDwmFrameEffects) return;
        try { _runtime.SetCompensateDwmFrameEffects(enabled); }
        finally { Refresh(); }
    }

    public void Refresh()
    {
        if (_disposed) return;
        CompensateDwmFrameEffects = _runtime.GetCompensateDwmFrameEffects();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompensateDwmFrameEffects)));
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Refresh));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.DwmFrameCompensationChanged -= OnChanged;
    }
}
