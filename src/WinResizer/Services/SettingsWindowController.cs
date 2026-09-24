using System;

namespace WinResizer.Services;

public interface ISettingsWindowHandle
{
    bool IsVisible { get; }

    bool IsMinimized { get; }

    void ShowWindow();

    void RestoreWindow();

    void ActivateWindow();

    void HideWindow();

    void CloseForApplicationExit();
}

public sealed class SettingsWindowController
{
    private readonly Func<ISettingsWindowHandle> _factory;
    private ISettingsWindowHandle? _window;
    private bool _isExiting;

    public SettingsWindowController(Func<ISettingsWindowHandle> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public bool HasWindow => _window is not null;

    public bool IsExiting => _isExiting;

    public void Show()
    {
        if (_isExiting)
        {
            return;
        }

        _window ??= _factory();
        if (!_window.IsVisible)
        {
            _window.ShowWindow();
        }

        if (_window.IsMinimized)
        {
            _window.RestoreWindow();
        }

        _window.ActivateWindow();
    }

    public void Hide()
    {
        if (!_isExiting && _window?.IsVisible == true)
        {
            _window.HideWindow();
        }
    }

    public void BeginExit()
    {
        _isExiting = true;
    }

    public void CloseForApplicationExit()
    {
        _isExiting = true;
        var window = _window;
        _window = null;
        window?.CloseForApplicationExit();
    }
}
