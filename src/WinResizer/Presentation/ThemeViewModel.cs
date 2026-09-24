using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Threading;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Services;

namespace WinResizer.Presentation;

public sealed class ThemeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly ThemeService _service;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ThemeViewModel(WinResizerRuntime runtime, ThemeService service, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.ThemePreferenceChanged += OnThemePreferenceChanged;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> ThemeOptions => _service.ThemeOptions;
    public string SelectedTheme { get; private set; } = "System";
    public bool IsRefreshing { get; private set; }

    public void SetTheme(string theme)
    {
        if (IsRefreshing || theme == SelectedTheme) return;
        if (!Enum.TryParse<ThemePreference>(theme, out var preference) ||
            !Enum.IsDefined(typeof(ThemePreference), preference))
            throw new ArgumentException("Unknown theme.", nameof(theme));
        try { _runtime.SetThemePreference(preference); }
        finally { Refresh(); }
    }

    public void Refresh()
    {
        if (_disposed) return;
        IsRefreshing = true;
        try
        {
            SelectedTheme = _runtime.GetThemePreference().ToString();
            _service.SelectedTheme = SelectedTheme;
            // Republish even on failure so a changed ComboBox restores Runtime state.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTheme)));
        }
        finally { IsRefreshing = false; }
    }

    private void OnThemePreferenceChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Refresh));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.ThemePreferenceChanged -= OnThemePreferenceChanged;
    }
}
