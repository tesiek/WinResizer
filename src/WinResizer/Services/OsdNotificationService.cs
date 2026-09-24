using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinResizer.Runtime;

namespace WinResizer.Services;

internal enum OsdIconKind
{
    Success,
    Info,
    Warning,
    Error,
}

internal static class OsdIconResources
{
    internal static OsdIconKind ForLevel(RuntimeNotificationLevel level)
    {
        switch (level)
        {
            case RuntimeNotificationLevel.Success:
                return OsdIconKind.Success;
            case RuntimeNotificationLevel.Warning:
                return OsdIconKind.Warning;
            case RuntimeNotificationLevel.Error:
                return OsdIconKind.Error;
            case RuntimeNotificationLevel.Info:
            default:
                return OsdIconKind.Info;
        }
    }

    internal static string GetAssetFileName(OsdIconKind kind)
    {
        switch (kind)
        {
            case OsdIconKind.Success:
                return "success.png";
            case OsdIconKind.Warning:
                return "warning.png";
            case OsdIconKind.Error:
                return "error.png";
            case OsdIconKind.Info:
            default:
                return "info.png";
        }
    }

    internal static Uri GetResourceUri(OsdIconKind kind) =>
        new Uri(
            $"/WinResizer;component/Resources/Osd/{GetAssetFileName(kind)}",
            UriKind.Relative);

    internal static ImageSource LoadImage(OsdIconKind kind)
    {
        var resource = Application.GetResourceStream(GetResourceUri(kind));
        if (resource is null)
        {
            throw new InvalidOperationException($"OSD icon resource was not found: {GetAssetFileName(kind)}");
        }

        using (resource.Stream)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = resource.Stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}

internal interface IOsdWindow : IDisposable
{
    bool IsVisible { get; }

    void ShowNotification(string title, string message, ImageSource icon);

    void HideNotification();
}

internal interface IOsdTimer
{
    event EventHandler? Tick;

    TimeSpan Interval { get; set; }

    bool IsEnabled { get; }

    void Start();

    void Stop();
}

internal sealed class OsdNotificationService : IDisposable
{
    internal static readonly TimeSpan DisplayDuration = TimeSpan.FromMilliseconds(2500);

    private readonly Dispatcher _dispatcher;
    private readonly Func<IOsdWindow> _windowFactory;
    private readonly IOsdTimer _timer;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _displayDuration;
    private readonly Dictionary<OsdIconKind, ImageSource> _icons = new Dictionary<OsdIconKind, ImageSource>();
    private IOsdWindow? _window;
    private DateTime _hideDeadlineUtc;
    private long _generation;
    private long _timerGeneration;
    private bool _disposed;

    internal OsdNotificationService(Dispatcher dispatcher)
        : this(
            dispatcher,
            () => new OsdWindow(),
            new DispatcherOsdTimer(dispatcher),
            () => DateTime.UtcNow,
            DisplayDuration)
    {
    }

    internal OsdNotificationService(
        Dispatcher dispatcher,
        Func<IOsdWindow> windowFactory,
        IOsdTimer timer,
        Func<DateTime> utcNow,
        TimeSpan displayDuration)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        if (displayDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(displayDuration));
        }

        _displayDuration = displayDuration;
        _timer.Tick += OnTimerTick;
    }

    internal void Show(string title, string message, RuntimeNotificationLevel level)
    {
        var generation = Interlocked.Increment(ref _generation);
        var requestedAtUtc = _utcNow();
        var kind = OsdIconResources.ForLevel(level);
        Dispatch(() => ShowCore(generation, requestedAtUtc, title, message, kind));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
        Dispatch(DisposeCore);
    }

    private void ShowCore(
        long generation,
        DateTime requestedAtUtc,
        string title,
        string message,
        OsdIconKind kind)
    {
        if (_disposed || generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        _timer.Stop();
        _window = _window ?? _windowFactory();
        if (!_icons.TryGetValue(kind, out var icon))
        {
            icon = OsdIconResources.LoadImage(kind);
            _icons.Add(kind, icon);
        }

        _window.ShowNotification(title, message, icon);
        _timerGeneration = generation;
        _hideDeadlineUtc = requestedAtUtc.Add(_displayDuration);
        RestartTimerForRemainingDuration();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _timerGeneration != Interlocked.Read(ref _generation))
        {
            _timer.Stop();
            return;
        }

        var remaining = _hideDeadlineUtc - _utcNow();
        if (remaining > TimeSpan.Zero)
        {
            _timer.Stop();
            _timer.Interval = remaining;
            _timer.Start();
            return;
        }

        _timer.Stop();
        _window?.HideNotification();
    }

    private void RestartTimerForRemainingDuration()
    {
        var remaining = _hideDeadlineUtc - _utcNow();
        _timer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void DisposeCore()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _window?.Dispose();
        _window = null;
        _icons.Clear();
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown can race with a final runtime notification.
        }
    }
}

internal sealed class DispatcherOsdTimer : IOsdTimer
{
    private readonly DispatcherTimer _timer;

    internal DispatcherOsdTimer(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _timer.Tick += (sender, args) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Tick;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public bool IsEnabled => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}
