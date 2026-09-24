using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Common.Shortcuts;
using WinResizer.Runtime;

namespace WinResizer.Presentation;

public sealed class PresetsViewModel : IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _refreshQueued;

    public PresetsViewModel(WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime.PresetsChanged += OnPresetsChanged;
        ReconcilePresets();
    }

    public ObservableCollection<PresetRowViewModel> Presets { get; } =
        new ObservableCollection<PresetRowViewModel>();

    public RuntimePresetInfo AddPreset()
    {
        return _runtime.AddPreset(GetCurrentProfileId());
    }

    public void SetPresetEnabled(PresetRowViewModel row, bool enabled)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        _runtime.SetPresetEnabled(row.ProfileId, row.PresetId, enabled);
    }

    public void SetPresetHotkey(PresetRowViewModel row, Hotkeys? hotkey)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        _runtime.SetPresetHotkey(row.ProfileId, row.PresetId, hotkey);
    }

    public bool RemovePreset(PresetRowViewModel row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        return _runtime.RemovePreset(row.ProfileId, row.PresetId);
    }

    public RuntimePresetInfo UpdatePreset(
        string profileId,
        string presetId,
        string name,
        int top,
        int left,
        int width,
        int height)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(presetId))
        {
            throw new ArgumentException("A preset ID is required.", nameof(presetId));
        }

        var updated = _runtime.UpdatePreset(profileId, presetId, name, left, top, width, height);
        // Updating an existing row is safe during EditItem; collection refresh is deferred.
        Presets.FirstOrDefault(row =>
            row.ProfileId.Equals(profileId, StringComparison.Ordinal) &&
            row.PresetId.Equals(presetId, StringComparison.Ordinal))?.Update(updated);
        return updated;
    }

    internal RuntimePresetInfo? GetPreset(string profileId, string presetId) =>
        _runtime.GetPresets(profileId).FirstOrDefault(preset =>
            preset.PresetId.Equals(presetId, StringComparison.Ordinal));

    internal string GetCaptureProfileId() => GetCurrentProfileId();

    internal RuntimePresetInfo AddCapturedPreset(string profileId, CapturedWindow capture) =>
        _runtime.AddPreset(profileId, capture.Window.ProcessName,
            capture.Geometry.Left, capture.Geometry.Top, capture.Geometry.Width, capture.Geometry.Height);

    internal RuntimePresetInfo CapturePreset(string profileId, string presetId, CapturedWindow capture)
    {
        var current = GetPreset(profileId, presetId)
            ?? throw new InvalidOperationException("The preset no longer exists.");
        return UpdatePreset(profileId, presetId, current.Name,
            capture.Geometry.Top, capture.Geometry.Left, capture.Geometry.Width, capture.Geometry.Height);
    }

    public void Refresh()
    {
        QueueRefresh(DispatcherPriority.DataBind);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.PresetsChanged -= OnPresetsChanged;
    }

    private void OnPresetsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        Refresh();
    }

    private void ReconcilePresets()
    {
        if (_disposed)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(Presets);
        if (view is IEditableCollectionView editable &&
            (editable.IsEditingItem || editable.IsAddingNew))
        {
            // The view requests a refresh after RowEditEnding, once WPF ends the transaction.
            return;
        }

        var currentProfile = _runtime.GetProfiles().FirstOrDefault(profile => profile.IsCurrent);
        if (currentProfile is null)
        {
            Presets.Clear();
            return;
        }

        var presets = _runtime.GetPresets(currentProfile.Id);
        var currentIds = new HashSet<string>(
            presets.Select(preset => preset.PresetId),
            StringComparer.Ordinal);

        for (var index = Presets.Count - 1; index >= 0; index--)
        {
            if (!currentIds.Contains(Presets[index].PresetId) ||
                !Presets[index].ProfileId.Equals(currentProfile.Id, StringComparison.Ordinal))
            {
                Presets.RemoveAt(index);
            }
        }

        for (var index = 0; index < presets.Count; index++)
        {
            var preset = presets[index];
            var row = Presets.FirstOrDefault(item =>
                item.ProfileId.Equals(currentProfile.Id, StringComparison.Ordinal) &&
                item.PresetId.Equals(preset.PresetId, StringComparison.Ordinal));
            if (row is null)
            {
                row = new PresetRowViewModel(currentProfile.Id, preset.PresetId);
                Presets.Insert(index, row);
            }
            else
            {
                var currentIndex = Presets.IndexOf(row);
                if (currentIndex != index)
                {
                    Presets.Move(currentIndex, index);
                }
            }

            row.Update(preset);
        }

        view?.Refresh();
    }

    private string GetCurrentProfileId()
    {
        var currentProfile = _runtime.GetProfiles().FirstOrDefault(profile => profile.IsCurrent);
        return currentProfile?.Id
            ?? throw new InvalidOperationException("There is no active profile.");
    }

    private void QueueRefresh(DispatcherPriority priority)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.BeginInvoke(
            priority,
            new Action(() =>
            {
                _refreshQueued = false;
                ReconcilePresets();
            }));
    }
}

public sealed class PresetRowViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _enabled;
    private int _top;
    private int _left;
    private int _width;
    private int _height;
    private string _hotkey = "-";

    public PresetRowViewModel(string profileId, string presetId)
    {
        ProfileId = string.IsNullOrWhiteSpace(profileId)
            ? throw new ArgumentException("A profile ID is required.", nameof(profileId))
            : profileId;
        PresetId = string.IsNullOrWhiteSpace(presetId)
            ? throw new ArgumentException("A preset ID is required.", nameof(presetId))
            : presetId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileId { get; }

    public string PresetId { get; }

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        private set => SetField(ref _enabled, value);
    }

    public int Top
    {
        get => _top;
        private set => SetField(ref _top, value);
    }

    public int Left
    {
        get => _left;
        private set => SetField(ref _left, value);
    }

    public int Width
    {
        get => _width;
        private set => SetField(ref _width, value);
    }

    public int Height
    {
        get => _height;
        private set => SetField(ref _height, value);
    }

    public string Hotkey
    {
        get => _hotkey;
        private set => SetField(ref _hotkey, value);
    }

    internal void Update(RuntimePresetInfo preset)
    {
        Name = preset.Name;
        Enabled = preset.Enabled;
        Top = preset.Y;
        Left = preset.X;
        Width = preset.Width;
        Height = preset.Height;
        Hotkey = FormatHotkey(preset.Hotkey);
    }

    private static string FormatHotkey(Hotkeys? hotkey) =>
        hotkey is not null && hotkey.IsValid() && !string.IsNullOrWhiteSpace(hotkey.ToKeysString())
            ? hotkey.ToKeysString()
            : "-";

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
