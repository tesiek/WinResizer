using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinResizer.Services;

public sealed class ThemeService : INotifyPropertyChanged, IDisposable
{
    private Window? _window;
    private bool _isWatchingSystemTheme;
    private string _selectedTheme = "System";

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "System", "Light", "Dark" };

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is not ("System" or "Light" or "Dark") || _selectedTheme == value)
            {
                return;
            }

            _selectedTheme = value;
            OnPropertyChanged();
            ApplySelectedTheme();
        }
    }

    public void Attach(Window window)
    {
        _window = window;
        ApplicationThemeManager.Changed += OnThemeChanged;
        ApplySelectedTheme();
    }

    public void Dispose()
    {
        StopWatchingSystemTheme();
        ApplicationThemeManager.Changed -= OnThemeChanged;
        _window = null;
    }

    private void ApplySelectedTheme()
    {
        if (_window is null)
        {
            return;
        }

        if (SelectedTheme == "System")
        {
            ApplicationThemeManager.ApplySystemTheme(updateAccent: true);

            if (!_isWatchingSystemTheme)
            {
                SystemThemeWatcher.Watch(_window, WindowBackdropType.None, updateAccents: true);
                _isWatchingSystemTheme = true;
            }

            ApplyWindowResources();

            return;
        }

        StopWatchingSystemTheme();
        ApplicationThemeManager.Apply(
            SelectedTheme == "Dark" ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.None,
            updateAccent: true);
        ApplyWindowResources();
    }

    private void OnThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (_window is null)
        {
            return;
        }

        _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ApplyWindowResources));
    }

    private void ApplyWindowResources()
    {
        if (_window is null)
        {
            return;
        }

        _window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        _window.SetResourceReference(Window.ForegroundProperty, "WindowForeground");
    }

    private void StopWatchingSystemTheme()
    {
        if (!_isWatchingSystemTheme || _window is null)
        {
            return;
        }

        SystemThemeWatcher.UnWatch(_window);
        _isWatchingSystemTheme = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
