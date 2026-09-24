using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using WinResizer.Base;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Core.WindowControl;

namespace WinResizer.Runtime;

public sealed partial class WinResizerRuntime : IDisposable
{
    private readonly IGlobalHotkeyRegistrar _hotkeyRegistrar;
    private readonly bool _ownsHotkeyRegistrar;
    private readonly bool _enableGlobalHotkeys;
    private readonly bool _enableWindowMonitoring;
    private readonly IDictionary<HotkeysType, int> _registeredHotkeys;
    private readonly IDictionary<string, int> _registeredPresetHotkeys;
    private readonly Action _saveConfiguration;
    private readonly Action<string> _log;

    private AutoResizeConfigurationSnapshotStore? _snapshotStore;
    private AutoResizeCoordinator? _autoResizeCoordinator;
    private WindowEventHandler? _windowEventHandler;
    private BindingList<WindowSize>? _observedWindowSizes;
    private bool _started;
    private bool _disposed;

    public WinResizerRuntime(WinResizerRuntimeOptions? options = null)
    {
        options ??= new WinResizerRuntimeOptions();
        _hotkeyRegistrar = options.HotkeyRegistrar ?? new KeyboardHook();
        _ownsHotkeyRegistrar = options.HotkeyRegistrar is null || options.OwnsHotkeyRegistrar;
        _enableGlobalHotkeys = options.EnableGlobalHotkeys;
        _enableWindowMonitoring = options.EnableWindowMonitoring;
        _registeredHotkeys = options.RegisteredHotkeys ?? new Dictionary<HotkeysType, int>();
        _registeredPresetHotkeys = options.RegisteredPresetHotkeys ?? new Dictionary<string, int>();
        _saveConfiguration = options.SaveConfiguration ?? ConfigFactory.Save;
        _log = options.Log ?? (_ => { });
    }

    public event EventHandler<RuntimeNotificationEventArgs>? NotificationRaised;

    public event EventHandler? ProfilesChanged;

    public event EventHandler? HotkeysChanged;

    public event EventHandler? ProfileOptionsChanged;

    public event EventHandler? PresetsChanged;

    public bool IsStarted => _started && !_disposed;

    public bool IsDisposed => _disposed;

    public IGlobalHotkeyRegistrar HotkeyRegistrar => _hotkeyRegistrar;

    public AutoResizeConfigurationSnapshot? CurrentAutoResizeSnapshot => _snapshotStore?.Current;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WinResizerRuntime));
        }

        if (_started)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ConfigFactory.ConfigPath) || ConfigFactory.Profiles.Current is null)
        {
            throw new InvalidOperationException("Portable configuration must be loaded before the runtime starts.");
        }

        try
        {
            _snapshotStore = new AutoResizeConfigurationSnapshotStore();
            _snapshotStore.Refresh();
            _autoResizeCoordinator = new AutoResizeCoordinator(() => _snapshotStore.Current, _log);

            SubscribeConfigurationEvents();
            ObserveCurrentWindowSizes();

            if (_enableGlobalHotkeys)
            {
                _hotkeyRegistrar.KeyPressed += OnKeyPressed;
                RegisterHotkeys();
            }

            _started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyList<RuntimeProfileInfo> GetProfiles()
    {
        var currentId = ConfigFactory.Current.ProfileId;
        return ConfigFactory.Profiles.Configs
            .Select(profile => new RuntimeProfileInfo(
                profile.ProfileId,
                profile.ProfileName,
                profile.ProfileId.Equals(currentId, StringComparison.Ordinal)))
            .ToList();
    }

    public bool SwitchProfile(string profileId)
    {
        ThrowIfNotRunning();
        return ConfigFactory.ProfileSwitch(profileId);
    }

    public IReadOnlyList<RuntimeHotkeyInfo> GetHotkeys()
    {
        ThrowIfNotRunning();
        return Enum.GetValues(typeof(HotkeysType))
            .Cast<HotkeysType>()
            .Select(type => new RuntimeHotkeyInfo(type, CloneHotkey(ConfigFactory.Current.GetKeys(type))))
            .ToList();
    }

    public IReadOnlyList<RuntimePresetInfo> GetPresets(string profileId)
    {
        ThrowIfNotRunning();
        var profile = GetRequiredProfile(profileId);
        return profile.Presets
            .Select(ToRuntimePresetInfo)
            .ToList();
    }

    public RuntimePresetInfo AddPreset(
        string profileId,
        string? name = null,
        int x = 0,
        int y = 0,
        int width = 1280,
        int height = 720)
    {
        ThrowIfNotRunning();
        var profile = GetRequiredProfile(profileId);
        var preset = new WindowPreset
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? $"Preset {profile.Presets.Count + 1}"
                : name!,
            Enabled = true,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Hotkey = null,
        };

        profile.Presets.Add(preset);
        try
        {
            SaveConfiguration();
        }
        catch
        {
            profile.Presets.Remove(preset);
            throw;
        }

        RaisePresetsChanged();
        return ToRuntimePresetInfo(preset);
    }

    public RuntimePresetInfo UpdatePreset(
        string profileId,
        string presetId,
        string name,
        int x,
        int y,
        int width,
        int height)
    {
        ThrowIfNotRunning();
        var preset = GetRequiredPreset(profileId, presetId, out _);
        var previousName = preset.Name;
        var previousX = preset.X;
        var previousY = preset.Y;
        var previousWidth = preset.Width;
        var previousHeight = preset.Height;

        preset.Name = name ?? string.Empty;
        preset.X = x;
        preset.Y = y;
        preset.Width = width;
        preset.Height = height;
        try
        {
            SaveConfiguration();
        }
        catch
        {
            preset.Name = previousName;
            preset.X = previousX;
            preset.Y = previousY;
            preset.Width = previousWidth;
            preset.Height = previousHeight;
            throw;
        }

        RaisePresetsChanged();
        return ToRuntimePresetInfo(preset);
    }

    public RuntimePresetInfo SetPresetEnabled(string profileId, string presetId, bool enabled)
    {
        ThrowIfNotRunning();
        var preset = GetRequiredPreset(profileId, presetId, out var profile);
        if (preset.Enabled == enabled)
        {
            return ToRuntimePresetInfo(preset);
        }

        var isCurrentProfile = IsCurrentProfile(profile);
        if (enabled && isCurrentProfile && IsValidPresetHotkey(preset.Hotkey))
        {
            EnsurePresetHotkeyDoesNotConflict(profile, preset, preset.Hotkey!);
        }

        if (!enabled)
        {
            preset.Enabled = false;
            try
            {
                SaveConfiguration();
            }
            catch
            {
                preset.Enabled = true;
                throw;
            }

            if (isCurrentProfile)
            {
                UnregisterPreset(preset.Id);
            }
        }
        else
        {
            var newRegistrationId = 0;
            var newRegistrationCreated = false;
            var staleRegistrationId = 0;
            var hasStaleRegistration = isCurrentProfile &&
                                       _registeredPresetHotkeys.TryGetValue(
                                           preset.Id,
                                           out staleRegistrationId);
            if (isCurrentProfile && IsValidPresetHotkey(preset.Hotkey))
            {
                newRegistrationId = RegisterPreset(preset.Hotkey!);
                newRegistrationCreated = true;
            }

            preset.Enabled = true;
            try
            {
                SaveConfiguration();
            }
            catch
            {
                preset.Enabled = false;
                if (newRegistrationCreated)
                {
                    _hotkeyRegistrar.UnRegisterHotKey(newRegistrationId);
                }

                throw;
            }

            if (newRegistrationCreated)
            {
                if (hasStaleRegistration)
                {
                    _hotkeyRegistrar.UnRegisterHotKey(staleRegistrationId);
                }

                _registeredPresetHotkeys[preset.Id] = newRegistrationId;
            }
            else if (isCurrentProfile)
            {
                UnregisterPreset(preset.Id);
            }
        }

        RaisePresetsChanged();
        return ToRuntimePresetInfo(preset);
    }

    public RuntimePresetInfo SetPresetHotkey(string profileId, string presetId, Hotkeys? hotkey)
    {
        ThrowIfNotRunning();
        var preset = GetRequiredPreset(profileId, presetId, out var profile);
        var candidate = CloneHotkey(hotkey);
        if (candidate is not null && !candidate.IsValid())
        {
            throw new HotkeyNotValidException("A hotkey must contain at least one modifier and one key.");
        }

        HotkeyAssignmentPolicy.Validate(candidate);

        if (HotkeysEqual(preset.Hotkey, candidate))
        {
            return ToRuntimePresetInfo(preset);
        }

        if (candidate is not null)
        {
            EnsurePresetHotkeyDoesNotConflict(profile, preset, candidate);
        }

        var isCurrentProfile = IsCurrentProfile(profile);
        var shouldRegister = isCurrentProfile && preset.Enabled && IsValidPresetHotkey(candidate);
        var previousHotkey = CloneHotkey(preset.Hotkey);
        var oldRegistrationId = 0;
        var hasOldRegistration = _registeredPresetHotkeys.TryGetValue(preset.Id, out oldRegistrationId);
        var newRegistrationId = 0;
        var newRegistrationCreated = false;

        if (shouldRegister)
        {
            newRegistrationId = RegisterPreset(candidate!);
            newRegistrationCreated = true;
        }

        preset.Hotkey = candidate;
        try
        {
            SaveConfiguration();
        }
        catch
        {
            preset.Hotkey = previousHotkey;
            if (newRegistrationCreated)
            {
                _hotkeyRegistrar.UnRegisterHotKey(newRegistrationId);
            }

            throw;
        }

        if (isCurrentProfile)
        {
            if (hasOldRegistration)
            {
                _hotkeyRegistrar.UnRegisterHotKey(oldRegistrationId);
            }

            if (newRegistrationCreated)
            {
                _registeredPresetHotkeys[preset.Id] = newRegistrationId;
            }
            else
            {
                _registeredPresetHotkeys.Remove(preset.Id);
            }
        }

        RaisePresetsChanged();
        return ToRuntimePresetInfo(preset);
    }

    public bool RemovePreset(string profileId, string presetId)
    {
        ThrowIfNotRunning();
        var preset = GetRequiredPreset(profileId, presetId, out var profile);
        var index = profile.Presets.IndexOf(preset);
        profile.Presets.RemoveAt(index);
        try
        {
            SaveConfiguration();
        }
        catch
        {
            profile.Presets.Insert(index, preset);
            throw;
        }

        if (IsCurrentProfile(profile))
        {
            UnregisterPreset(preset.Id);
        }

        RaisePresetsChanged();
        return true;
    }

    public bool GetNotifyOnSaved()
    {
        ThrowIfNotRunning();
        return ConfigFactory.Current.NotifyOnSaved;
    }

    public void SetNotifyOnSaved(bool enabled)
    {
        ThrowIfNotRunning();
        var config = ConfigFactory.Current;
        if (config.NotifyOnSaved == enabled)
        {
            return;
        }

        var previous = config.NotifyOnSaved;
        config.NotifyOnSaved = enabled;
        try
        {
            ConfigFactory.Save();
        }
        catch
        {
            config.NotifyOnSaved = previous;
            throw;
        }

        RaiseProfileOptionsChanged();
    }

    public void SetHotkey(HotkeysType type, Hotkeys? hotkey)
    {
        ThrowIfNotRunning();
        if (!Enum.IsDefined(typeof(HotkeysType), type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "The hotkey action is not supported.");
        }

        if (hotkey is null)
        {
            ClearHotkey(type);
            return;
        }

        if (hotkey.ModifierKeys is null || !hotkey.IsValid())
        {
            throw new HotkeyNotValidException("A hotkey must contain at least one modifier and one key.");
        }

        HotkeyAssignmentPolicy.Validate(hotkey);

        var currentConfig = ConfigFactory.Current;
        var currentHotkey = currentConfig.GetKeys(type);
        if (currentHotkey is not null && currentHotkey.Equals(hotkey))
        {
            return;
        }

        var conflict = currentConfig.CheckHotkeyConflict(hotkey, excludeType: type);
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"Hotkey {hotkey.ToKeysString()} is already used by {conflict}.");
        }

        var previousHotkey = CloneHotkey(currentHotkey);
        var candidate = CloneHotkey(hotkey)!;
        var newRegistrationId = 0;
        var newRegistrationCreated = false;

        try
        {
            if (_enableGlobalHotkeys)
            {
                newRegistrationId = _hotkeyRegistrar.RegisterHotKey(
                    candidate.GetModifierKeys(),
                    candidate.GetKey());
                newRegistrationCreated = true;
            }

            currentConfig.SetKeys(type, candidate);
            _saveConfiguration();

            if (_enableGlobalHotkeys)
            {
                if (_registeredHotkeys.TryGetValue(type, out var oldRegistrationId))
                {
                    _hotkeyRegistrar.UnRegisterHotKey(oldRegistrationId);
                }

                _registeredHotkeys[type] = newRegistrationId;
            }

            newRegistrationCreated = false;
        }
        catch
        {
            if (previousHotkey is null)
            {
                currentConfig.Keys.Remove(type);
            }
            else
            {
                currentConfig.SetKeys(type, previousHotkey);
            }

            if (newRegistrationCreated)
            {
                _hotkeyRegistrar.UnRegisterHotKey(newRegistrationId);
            }

            throw;
        }

        RaiseHotkeysChanged();
    }

    private void ClearHotkey(HotkeysType type)
    {
        var config = ConfigFactory.Current;
        var hadEntry = config.Keys.TryGetValue(type, out var previous);
        var hadRegistration = _registeredHotkeys.TryGetValue(type, out var registrationId);
        if (!hadEntry && !hadRegistration) return;

        config.Keys.Remove(type);
        try { _saveConfiguration(); }
        catch
        {
            if (hadEntry) config.Keys[type] = previous!;
            throw;
        }

        if (hadRegistration)
        {
            _hotkeyRegistrar.UnRegisterHotKey(registrationId);
            _registeredHotkeys.Remove(type);
        }
        RaiseHotkeysChanged();
    }

    public RuntimeProfileInfo AddProfile(string profileName)
    {
        ThrowIfNotRunning();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(profileName));
        }

        var profile = ConfigFactory.ProfileAdd(profileName.Trim());
        return new RuntimeProfileInfo(profile.ProfileId, profile.ProfileName, false);
    }

    public bool RenameProfile(string profileId, string profileName)
    {
        ThrowIfNotRunning();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("Profile name cannot be empty.", nameof(profileName));
        }

        if (!ConfigFactory.Profiles.Configs.Any(profile =>
                profile.ProfileId.Equals(profileId, StringComparison.Ordinal)))
        {
            return false;
        }

        ConfigFactory.ProfileRename(profileId, profileName);
        return true;
    }

    public bool RemoveProfile(string profileId)
    {
        ThrowIfNotRunning();
        var profiles = GetProfiles();
        var profile = profiles.FirstOrDefault(item => item.Id.Equals(profileId, StringComparison.Ordinal));
        if (profile is null || profile.IsCurrent || profiles.Count <= 1)
        {
            return false;
        }

        return ConfigFactory.ProfileRemove(profileId);
    }

    public void Save()
    {
        ThrowIfNotRunning();
        var config = ConfigFactory.Current;
        var snapshot = WindowSizesSnapshot.Capture(config.WindowSizes);
        var window = Resizer.GetForegroundHandle();
        var raiseListChangedEvents = config.WindowSizes.RaiseListChangedEvents;
        var changed = false;
        config.WindowSizes.RaiseListChangedEvents = false;
        string processName;
        try
        {
            changed = WindowUtils.TryUpdateWindowSize(window, config, OnGetProcessFailed, out processName);
            if (changed)
            {
                try
                {
                    SaveConfiguration();
                }
                catch (ConfigurationPersistenceException)
                {
                    snapshot.Restore(config.WindowSizes);
                    throw;
                }
            }
        }
        catch
        {
            if (!changed)
            {
                snapshot.Restore(config.WindowSizes);
            }

            throw;
        }
        finally
        {
            config.WindowSizes.RaiseListChangedEvents = raiseListChangedEvents;
            if (changed)
            {
                config.WindowSizes.ResetBindings();
            }
        }

        if (changed && config.NotifyOnSaved)
        {
            Notify(
                "Config Saved",
                $"Process <{processName}> saved!",
                RuntimeNotificationLevel.Success,
                true);
        }
    }

    public void SaveAll()
    {
        ThrowIfNotRunning();
        var config = ConfigFactory.Current;
        var snapshot = WindowSizesSnapshot.Capture(config.WindowSizes);
        var windows = Resizer.GetOpenUserWindows();
        var raiseListChangedEvents = config.WindowSizes.RaiseListChangedEvents;
        var changed = false;
        config.WindowSizes.RaiseListChangedEvents = false;
        try
        {
            foreach (var window in windows)
            {
                try
                {
                    if (Resizer.IsEligibleForUserWindow(window) &&
                        Resizer.GetWindowState(window) != WindowState.Minimized)
                    {
                        changed |= WindowUtils.TryUpdateWindowSize(window, config, null, out _);
                    }
                }
                catch (Exception)
                {
                    // A window can close after enumeration; continue with the remainder.
                }
            }

            if (changed)
            {
                try
                {
                    SaveConfiguration();
                }
                catch (ConfigurationPersistenceException)
                {
                    snapshot.Restore(config.WindowSizes);
                    throw;
                }
            }
        }
        finally
        {
            config.WindowSizes.RaiseListChangedEvents = raiseListChangedEvents;
            if (changed)
            {
                config.WindowSizes.ResetBindings();
            }
        }

        if (config.NotifyOnSaved)
        {
            Notify("Config Saved", "Current processes saved.", RuntimeNotificationLevel.Success, true);
        }
    }

    public void RestoreAll()
    {
        ThrowIfNotRunning();
        WindowUtils.ResizeAllWindow(ConfigFactory.Current, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;

        UnsubscribeConfigurationEvents();
        StopObservingWindowSizes();

        _windowEventHandler?.Dispose();
        _windowEventHandler = null;

        _autoResizeCoordinator?.Dispose();
        _autoResizeCoordinator = null;

        _snapshotStore?.Dispose();
        _snapshotStore = null;

        if (_enableGlobalHotkeys)
        {
            _hotkeyRegistrar.KeyPressed -= OnKeyPressed;
        }

        if (_ownsHotkeyRegistrar)
        {
            _hotkeyRegistrar.Dispose();
        }
        else
        {
            _hotkeyRegistrar.UnRegisterHotKey();
        }

        _registeredHotkeys.Clear();
        _registeredPresetHotkeys.Clear();
        InvalidateProcessTokens();
        InvalidateIgnoredWindowTokens();
    }

    private void SubscribeConfigurationEvents()
    {
        var events = ConfigFactory.Profiles.ProfileEvents;
        events.ProfileAdd += OnProfileAdded;
        events.ProfileSwitching += OnProfileSwitching;
        events.ProfileSwitch += OnProfileSwitched;
        events.ProfileRename += OnProfileRenamed;
        events.ProfileRemove += OnProfileRemoved;
        ConfigFactory.ConfigurationReplacing += OnConfigurationReplacing;
        ConfigFactory.ConfigurationReplaced += OnConfigurationReplaced;
        ConfigFactory.ConfigurationSaved += OnProcessesConfigurationSaved;
        SubscribeIgnoredWindowsConfigurationEvents();
    }

    private void UnsubscribeConfigurationEvents()
    {
        var events = ConfigFactory.Profiles.ProfileEvents;
        events.ProfileAdd -= OnProfileAdded;
        events.ProfileSwitching -= OnProfileSwitching;
        events.ProfileSwitch -= OnProfileSwitched;
        events.ProfileRename -= OnProfileRenamed;
        events.ProfileRemove -= OnProfileRemoved;
        ConfigFactory.ConfigurationReplacing -= OnConfigurationReplacing;
        ConfigFactory.ConfigurationReplaced -= OnConfigurationReplaced;
        ConfigFactory.ConfigurationSaved -= OnProcessesConfigurationSaved;
        UnsubscribeIgnoredWindowsConfigurationEvents();
    }

    private void OnProfileAdded(string profileId, string profileName) => RaiseProfilesChanged();

    private void OnProfileRenamed(string profileId, string profileName) => RaiseProfilesChanged();

    private void OnProfileRemoved(string profileId) => RaiseProfilesChanged();

    private void OnProfileSwitching(string currentProfileId, string nextProfileId) =>
        _autoResizeCoordinator?.CancelPending();

    private void OnConfigurationReplacing()
    {
        _autoResizeCoordinator?.CancelPending();
        InvalidateProcessTokens();
        ReleaseHotkeys();
    }

    private void OnProfileSwitched(string profileId)
    {
        ObserveCurrentWindowSizes();
        ReloadHotkeysForCurrentProfile();
        RaiseProfilesChanged();
        RaiseHotkeysChanged();
        RaiseProfileOptionsChanged();
        RaisePresetsChanged();
        RaiseProcessesChanged();
        Notify(
            "Config Reloaded",
            $"Profile switched to <{ConfigFactory.Current.ProfileName}>.",
            RuntimeNotificationLevel.Success);
    }

    private void OnConfigurationReplaced()
    {
        ObserveCurrentWindowSizes();
        if (_enableGlobalHotkeys) RegisterHotkeys();
        RaiseProfilesChanged();
        RaiseHotkeysChanged();
        RaiseProfileOptionsChanged();
        RaiseThemePreferenceChanged();
        RaiseDwmFrameCompensationChanged();
        RaisePresetsChanged();
        RaiseProcessesChanged();
    }

    private void ObserveCurrentWindowSizes()
    {
        var current = ConfigFactory.Current.WindowSizes;
        if (!ReferenceEquals(_observedWindowSizes, current))
        {
            StopObservingWindowSizes();
            _observedWindowSizes = current;
            _observedWindowSizes.ListChanged += OnWindowSizesChanged;
        }

        UpdateWindowMonitoring();
    }

    private void StopObservingWindowSizes()
    {
        if (_observedWindowSizes is null)
        {
            return;
        }

        _observedWindowSizes.ListChanged -= OnWindowSizesChanged;
        _observedWindowSizes = null;
    }

    private void OnWindowSizesChanged(object? sender, ListChangedEventArgs e) => UpdateWindowMonitoring();

    private void UpdateWindowMonitoring()
    {
        if (!_enableWindowMonitoring)
        {
            return;
        }

        var autoSizeEnabled = ConfigFactory.Current.WindowSizes.Any(item => item.AutoResize);
        if (_windowEventHandler is null && autoSizeEnabled)
        {
            _windowEventHandler = new WindowEventHandler(OnWindowCreated);
            _windowEventHandler.AddWindowCreateHandle();
        }
        else if (_windowEventHandler is not null && !autoSizeEnabled)
        {
            _windowEventHandler.Dispose();
            _windowEventHandler = null;
        }
    }

    private void OnWindowCreated(IntPtr handle) => _autoResizeCoordinator?.Schedule(handle);

    private void ReloadHotkeysForCurrentProfile()
    {
        if (!_enableGlobalHotkeys)
        {
            return;
        }

        ReleaseHotkeys();
        RegisterHotkeys();
    }

    private void ReleaseHotkeys()
    {
        if (!_enableGlobalHotkeys) return;
        _hotkeyRegistrar.UnRegisterHotKey();
        _registeredHotkeys.Clear();
        _registeredPresetHotkeys.Clear();
    }

    private void RegisterHotkeys()
    {
        foreach (var type in Enum.GetValues(typeof(HotkeysType)).Cast<HotkeysType>())
        {
            RegisterHotkey(type);
        }

        RegisterPresetHotkeys();
    }

    private void RegisterPresetHotkeys()
    {
        if (ConfigFactory.Current.Presets is null)
        {
            return;
        }

        foreach (var preset in ConfigFactory.Current.Presets)
        {
            if (!preset.Enabled || preset.Hotkey is null || !preset.Hotkey.IsValid())
            {
                continue;
            }

            try
            {
                var id = _hotkeyRegistrar.RegisterHotKey(
                    preset.Hotkey.GetModifierKeys(),
                    preset.Hotkey.GetKey());
                _registeredPresetHotkeys[preset.Id] = id;
            }
            catch (Exception exception)
            {
                var error = $"Register preset hotkey {preset.Hotkey.ToKeysString()} failed.";
                _log($"{error}: {exception}");
                Notify("Hotkey Registration Failed", error, RuntimeNotificationLevel.Error);
            }
        }
    }

    private int RegisterPreset(Hotkeys hotkey) =>
        _hotkeyRegistrar.RegisterHotKey(hotkey.GetModifierKeys(), hotkey.GetKey());

    private void UnregisterPreset(string presetId)
    {
        if (_registeredPresetHotkeys.TryGetValue(presetId, out var registrationId))
        {
            _hotkeyRegistrar.UnRegisterHotKey(registrationId);
            _registeredPresetHotkeys.Remove(presetId);
        }
    }

    private static bool IsValidPresetHotkey(Hotkeys? hotkey) =>
        hotkey is not null && hotkey.IsValid();

    private static void EnsurePresetHotkeyDoesNotConflict(
        Config profile,
        WindowPreset preset,
        Hotkeys hotkey)
    {
        var conflict = profile.CheckHotkeyConflict(hotkey, excludePresetId: preset.Id);
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"Hotkey {hotkey.ToKeysString()} is already used by {conflict}.");
        }
    }

    private void RegisterHotkey(HotkeysType type)
    {
        var hotkeys = WindowUtils.GetKeys(type);
        if (hotkeys is null)
        {
            return;
        }

        if (!hotkeys.IsValid())
        {
            Notify("Invalid Hotkey", $"{type} window hotkeys not valid.", RuntimeNotificationLevel.Warning);
        }

        try
        {
            var id = _hotkeyRegistrar.RegisterHotKey(hotkeys.GetModifierKeys(), hotkeys.GetKey());
            _registeredHotkeys[type] = id;
        }
        catch (Exception exception)
        {
            var error = $"Register hotkey {hotkeys.ToKeysString()} failed.";
            _log($"{error}: {exception}");
            Notify("Hotkey Registration Failed", error, RuntimeNotificationLevel.Error);
        }
    }

    private void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        try
        {
            if (ConfigFactory.Current.DisableInFullScreen && Resizer.IsForegroundFullScreen())
            {
                return;
            }

            var keys = WindowUtils.GetKeys(HotkeysType.RestoreAll);
            if (keys.KeysEqual(e.Modifier, e.Key))
            {
                RestoreAll();
                return;
            }

            keys = WindowUtils.GetKeys(HotkeysType.SaveAll);
            if (keys.KeysEqual(e.Modifier, e.Key))
            {
                SaveAll();
                return;
            }

            keys = WindowUtils.GetKeys(HotkeysType.Save);
            if (keys.KeysEqual(e.Modifier, e.Key))
            {
                Save();
                return;
            }

            keys = WindowUtils.GetKeys(HotkeysType.Restore);
            if (keys.KeysEqual(e.Modifier, e.Key))
            {
                var window = Resizer.GetForegroundHandle();
                WindowUtils.ResizeWindow(window, ConfigFactory.Current, OnGetProcessFailed, OnConfigNoMatch);
                return;
            }

            var matchedPreset = ConfigFactory.Current.Presets?.FirstOrDefault(preset =>
                preset.Enabled && preset.Hotkey.KeysEqual(e.Modifier, e.Key));
            if (matchedPreset is not null)
            {
                ApplyPreset(Resizer.GetForegroundHandle(), matchedPreset);
            }
        }
        catch (Exception exception)
        {
            _log($"Hotkey action failed: {exception}");
            Notify(
                "An Error Occurred",
                "An error occurred. Check the log file for more details.",
                RuntimeNotificationLevel.Error);
        }
    }

    private static void ApplyPreset(IntPtr handle, WindowPreset preset)
    {
        if (handle == IntPtr.Zero || Resizer.IsChildWindow(handle))
        {
            return;
        }

        var processName = Resizer.GetRealProcessName(handle);
        if (processName is not null && Resizer.IsInvisibleProcess(processName))
        {
            return;
        }

        WindowUtils.ApplyPreset(handle, preset);
    }

    private void OnConfigNoMatch(string processName, string windowTitle)
    {
        Notify(
            "No Operations Available",
            $"No saved settings for <{processName} :: {windowTitle}>.",
            RuntimeNotificationLevel.Info);
    }

    private void OnGetProcessFailed(Process process, Exception exception)
    {
        var message = $"Unable to resize process <{process.ProcessName}>, elevated privileges may be required.";
        _log($"{message}\nException: {exception}");
        Notify("Operation Failed", message, RuntimeNotificationLevel.Warning);
    }

    private void RaiseProfilesChanged()
    {
        try
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _log($"ProfilesChanged handler failed: {exception}");
        }
    }

    private void RaiseHotkeysChanged()
    {
        try
        {
            HotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _log($"HotkeysChanged handler failed: {exception}");
        }
    }

    private void RaiseProfileOptionsChanged()
    {
        try
        {
            ProfileOptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _log($"ProfileOptionsChanged handler failed: {exception}");
        }
    }

    private void RaisePresetsChanged()
    {
        try
        {
            PresetsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _log($"PresetsChanged handler failed: {exception}");
        }
    }

    private static Config GetRequiredProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        return ConfigFactory.Profiles.Configs.FirstOrDefault(profile =>
                   profile.ProfileId.Equals(profileId, StringComparison.Ordinal))
               ?? throw new ArgumentException("The profile was not found.", nameof(profileId));
    }

    private static WindowPreset GetRequiredPreset(
        string profileId,
        string presetId,
        out Config profile)
    {
        profile = GetRequiredProfile(profileId);
        if (string.IsNullOrWhiteSpace(presetId))
        {
            throw new ArgumentException("A preset ID is required.", nameof(presetId));
        }

        return profile.Presets.FirstOrDefault(preset =>
                   preset.Id.Equals(presetId, StringComparison.Ordinal))
               ?? throw new ArgumentException("The preset was not found.", nameof(presetId));
    }

    private static bool IsCurrentProfile(Config profile) =>
        ConfigFactory.Current.ProfileId.Equals(profile.ProfileId, StringComparison.Ordinal);

    private void SaveConfiguration() => _saveConfiguration();

    private sealed class WindowSizesSnapshot
    {
        private readonly IReadOnlyList<WindowSizeSnapshot> _items;

        private WindowSizesSnapshot(IReadOnlyList<WindowSizeSnapshot> items)
        {
            _items = items;
        }

        public static WindowSizesSnapshot Capture(BindingList<WindowSize> windowSizes) =>
            new(windowSizes.Select(rule => new WindowSizeSnapshot(rule)).ToList());

        public void Restore(BindingList<WindowSize> windowSizes)
        {
            foreach (var item in _items)
            {
                item.Restore();
            }

            windowSizes.Clear();
            foreach (var item in _items)
            {
                windowSizes.Add(item.Rule);
            }
        }
    }

    private sealed class WindowSizeSnapshot
    {
        private readonly string _name;
        private readonly string _title;
        private readonly Rect _rect;
        private readonly WindowState _state;
        private readonly Point _maximizedPosition;
        private readonly bool _autoResize;
        private readonly int _autoResizeDelay;

        public WindowSizeSnapshot(WindowSize rule)
        {
            Rule = rule;
            _name = rule.Name;
            _title = rule.Title;
            _rect = rule.Rect;
            _state = rule.State;
            _maximizedPosition = rule.MaximizedPosition;
            _autoResize = rule.AutoResize;
            _autoResizeDelay = rule.AutoResizeDelay;
        }

        public WindowSize Rule { get; }

        public void Restore()
        {
            Rule.Name = _name;
            Rule.Title = _title;
            Rule.Rect = _rect;
            Rule.State = _state;
            Rule.MaximizedPosition = _maximizedPosition;
            Rule.AutoResize = _autoResize;
            Rule.AutoResizeDelay = _autoResizeDelay;
        }
    }

    private static RuntimePresetInfo ToRuntimePresetInfo(WindowPreset preset) =>
        new(
            preset.Id,
            preset.Name,
            preset.Enabled,
            preset.X,
            preset.Y,
            preset.Width,
            preset.Height,
            CloneHotkey(preset.Hotkey));

    private static bool HotkeysEqual(Hotkeys? left, Hotkeys? right) =>
        left is null ? right is null : right is not null && left.Equals(right);

    private static Hotkeys? CloneHotkey(Hotkeys? hotkey)
    {
        if (hotkey is null)
        {
            return null;
        }

        return new Hotkeys
        {
            ModifierKeys = hotkey.ModifierKeys is null
                ? new HashSet<string>()
                : new HashSet<string>(hotkey.ModifierKeys),
            Key = hotkey.Key,
        };
    }

    private void Notify(
        string title,
        string message,
        RuntimeNotificationLevel level,
        bool openProcessSettings = false)
    {
        try
        {
            NotificationRaised?.Invoke(
                this,
                new RuntimeNotificationEventArgs(title, message, level, openProcessSettings));
        }
        catch (Exception exception)
        {
            _log($"Runtime notification handler failed: {exception}");
        }
    }

    private void ThrowIfNotRunning()
    {
        if (!IsStarted)
        {
            throw new InvalidOperationException("The WinResizer runtime is not running.");
        }
    }
}
