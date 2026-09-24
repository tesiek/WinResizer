using System;
using System.ComponentModel;
using WinResizer.Core.Startup;

namespace WinResizer.Presentation;

public sealed class StartupViewModel : INotifyPropertyChanged
{
    private readonly SystemStartupService _startup;
    public StartupViewModel(SystemStartupService startup) => _startup = startup ?? throw new ArgumentNullException(nameof(startup));
    public bool IsEnabled { get; private set; }
    public bool IsAvailable { get; private set; }
    public string? ErrorMessage { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        try
        {
            IsEnabled = _startup.IsEnabled;
            IsAvailable = true;
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            ErrorMessage = "Could not read Windows startup settings. " + exception.Message;
            throw;
        }
        finally { Notify(); }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled) _startup.Enable();
            else _startup.Disable();
            Refresh();
        }
        catch
        {
            // Re-read the system state, including a write that may have completed before a failure.
            // If reading fails too, retain the last known state and disable the control.
            try { Refresh(); } catch { }
            throw;
        }
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorMessage)));
    }
}
