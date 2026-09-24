using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Runtime;

namespace WinResizer.Presentation;

public enum ProfileOption { NotifyOnSaved, RestoreAllIncludeMinimized, DisableInFullScreen, EnableResizeByTitle, EnableAutoResizeDelay }

public sealed class HotkeysViewModel : IDisposable, INotifyPropertyChanged
{
    private readonly WinResizerRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private bool _notifyOnSaved;
    private bool _includeMinimized, _disableInFullScreen, _resizeByTitle, _autoResizeDelay;
    private bool _disposed;

    public HotkeysViewModel(WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.HotkeysChanged += OnHotkeysChanged;
        _runtime.ProfileOptionsChanged += OnProfileOptionsChanged;
        ReconcileHotkeys();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; } =
        new ObservableCollection<HotkeyRowViewModel>();

    public bool NotifyOnSaved
    {
        get => _notifyOnSaved;
        set
        {
            if (_disposed || _notifyOnSaved == value)
            {
                return;
            }

            _runtime.SetNotifyOnSaved(value);
            SetField(ref _notifyOnSaved, value);
        }
    }

    public bool IncludeMinimized => _includeMinimized;
    public bool DisableInFullScreen => _disableInFullScreen;
    public bool ResizeByTitle => _resizeByTitle;
    public bool AutoResizeDelay => _autoResizeDelay;

    public void SetProfileOption(ProfileOption option, bool enabled)
    {
        if (_disposed) return;
        try
        {
            switch (option)
            {
                case ProfileOption.NotifyOnSaved: NotifyOnSaved = enabled; break;
                case ProfileOption.RestoreAllIncludeMinimized: _runtime.SetRestoreAllIncludeMinimized(enabled); break;
                case ProfileOption.DisableInFullScreen: _runtime.SetDisableInFullScreen(enabled); break;
                case ProfileOption.EnableResizeByTitle: _runtime.SetEnableResizeByTitle(enabled); break;
                case ProfileOption.EnableAutoResizeDelay: _runtime.SetEnableAutoResizeDelay(enabled); break;
                default: throw new ArgumentOutOfRangeException(nameof(option));
            }
        }
        finally { Refresh(); }
    }

    public void SetHotkey(HotkeyRowViewModel row, Hotkeys? hotkey)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        _runtime.SetHotkey(row.ActionId, hotkey);
    }

    public void Refresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(ReconcileHotkeys));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.HotkeysChanged -= OnHotkeysChanged;
        _runtime.ProfileOptionsChanged -= OnProfileOptionsChanged;
    }

    private void OnHotkeysChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void OnProfileOptionsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void ReconcileHotkeys()
    {
        if (_disposed)
        {
            return;
        }

        var hotkeys = _runtime.GetHotkeys();
        SetField(ref _notifyOnSaved, _runtime.GetNotifyOnSaved(), nameof(NotifyOnSaved));
        SetField(ref _includeMinimized, _runtime.GetRestoreAllIncludeMinimized(), nameof(IncludeMinimized));
        SetField(ref _disableInFullScreen, _runtime.GetDisableInFullScreen(), nameof(DisableInFullScreen));
        SetField(ref _resizeByTitle, _runtime.GetEnableResizeByTitle(), nameof(ResizeByTitle));
        SetField(ref _autoResizeDelay, _runtime.GetEnableAutoResizeDelay(), nameof(AutoResizeDelay));
        // Re-publish even unchanged values to undo a checkbox's local click on Save failure.
        foreach (var name in new[] { nameof(NotifyOnSaved), nameof(IncludeMinimized), nameof(DisableInFullScreen), nameof(ResizeByTitle), nameof(AutoResizeDelay) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        var currentTypes = new HashSet<HotkeysType>(hotkeys.Select(hotkey => hotkey.Type));

        for (var index = Hotkeys.Count - 1; index >= 0; index--)
        {
            if (!currentTypes.Contains(Hotkeys[index].ActionId))
            {
                Hotkeys.RemoveAt(index);
            }
        }

        for (var index = 0; index < hotkeys.Count; index++)
        {
            var hotkey = hotkeys[index];
            var row = Hotkeys.FirstOrDefault(item => item.ActionId == hotkey.Type);
            if (row is null)
            {
                row = new HotkeyRowViewModel(hotkey.Type);
                Hotkeys.Insert(index, row);
            }
            else
            {
                var currentIndex = Hotkeys.IndexOf(row);
                if (currentIndex != index)
                {
                    Hotkeys.Move(currentIndex, index);
                }
            }

            row.Update(GetActionDisplayName(hotkey.Type), FormatHotkey(hotkey.Hotkey));
        }

        CollectionViewSource.GetDefaultView(Hotkeys)?.Refresh();
    }

    private static string FormatHotkey(Hotkeys? hotkey) =>
        hotkey is not null && hotkey.IsValid() && !string.IsNullOrWhiteSpace(hotkey.ToKeysString())
            ? hotkey.ToKeysString()
            : "-";

    private static string GetActionDisplayName(HotkeysType type) =>
        type switch
        {
            HotkeysType.Save => "Save Active Window",
            HotkeysType.Restore => "Restore Active Window",
            HotkeysType.SaveAll => "Save All Windows",
            HotkeysType.RestoreAll => "Restore All Windows",
            _ => type.ToString(),
        };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class HotkeyRowViewModel : INotifyPropertyChanged
{
    private string _action = string.Empty;
    private string _currentKey = string.Empty;

    public HotkeyRowViewModel(HotkeysType actionId)
    {
        ActionId = actionId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HotkeysType ActionId { get; }

    public string Action
    {
        get => _action;
        private set => SetField(ref _action, value);
    }

    public string CurrentKey
    {
        get => _currentKey;
        private set => SetField(ref _currentKey, value);
    }

    internal void Update(string action, string currentKey)
    {
        Action = action;
        CurrentKey = currentKey;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
