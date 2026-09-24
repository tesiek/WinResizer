using System;
using WinResizer.Configuration;

namespace WinResizer.Runtime;

public sealed partial class WinResizerRuntime
{
    public event EventHandler? ThemePreferenceChanged;

    public event EventHandler? DwmFrameCompensationChanged;

    public bool GetRestoreAllIncludeMinimized() => GetProfileOption(config => config.RestoreAllIncludeMinimized);
    public void SetRestoreAllIncludeMinimized(bool enabled) => SetProfileOption(
        config => config.RestoreAllIncludeMinimized, (config, value) => config.RestoreAllIncludeMinimized = value, enabled);

    public bool GetDisableInFullScreen() => GetProfileOption(config => config.DisableInFullScreen);
    public void SetDisableInFullScreen(bool enabled) => SetProfileOption(
        config => config.DisableInFullScreen, (config, value) => config.DisableInFullScreen = value, enabled);

    public bool GetEnableResizeByTitle() => GetProfileOption(config => config.EnableResizeByTitle);
    public void SetEnableResizeByTitle(bool enabled) => SetProfileOption(
        config => config.EnableResizeByTitle, (config, value) => config.EnableResizeByTitle = value, enabled);

    public bool GetEnableAutoResizeDelay() => GetProfileOption(config => config.EnableAutoResizeDelay);
    public void SetEnableAutoResizeDelay(bool enabled) => SetProfileOption(
        config => config.EnableAutoResizeDelay, (config, value) => config.EnableAutoResizeDelay = value, enabled);

    public ThemePreference GetThemePreference()
    {
        ThrowIfNotRunning();
        return NormalizeTheme(ConfigFactory.Profiles.ThemePreference);
    }

    public void SetThemePreference(ThemePreference preference)
    {
        ThrowIfNotRunning();
        preference = NormalizeTheme(preference);
        var profiles = ConfigFactory.Profiles;
        var previous = profiles.ThemePreference;
        if (previous == preference) return;
        profiles.ThemePreference = preference;
        try { _saveConfiguration(); }
        catch { profiles.ThemePreference = previous; throw; }
        RaiseThemePreferenceChanged();
    }

    public bool GetCompensateDwmFrameEffects()
    {
        ThrowIfNotRunning();
        return ConfigFactory.Profiles.CompensateDwmFrameEffects;
    }

    public void SetCompensateDwmFrameEffects(bool enabled)
    {
        ThrowIfNotRunning();
        var profiles = ConfigFactory.Profiles;
        var previous = profiles.CompensateDwmFrameEffects;
        if (previous == enabled) return;
        profiles.CompensateDwmFrameEffects = enabled;
        try { _saveConfiguration(); }
        catch
        {
            profiles.CompensateDwmFrameEffects = previous;
            _snapshotStore?.Refresh();
            throw;
        }
        _snapshotStore?.Refresh();
        RaiseDwmFrameCompensationChanged();
    }

    private bool GetProfileOption(Func<Config, bool> get)
    {
        ThrowIfNotRunning();
        return get(ConfigFactory.Current);
    }

    private void SetProfileOption(Func<Config, bool> get, Action<Config, bool> set, bool enabled)
    {
        ThrowIfNotRunning();
        var config = ConfigFactory.Current;
        var previous = get(config);
        if (previous == enabled) return;
        set(config, enabled);
        try { _saveConfiguration(); }
        catch
        {
            set(config, previous);
            _snapshotStore?.Refresh();
            throw;
        }
        // Also supports persistence seams that do not emit ConfigurationSaved.
        // Ordinary option edits publish a fresh snapshot without cancelling pending work.
        _snapshotStore?.Refresh();
        RaiseProfileOptionsChanged();
    }

    private static ThemePreference NormalizeTheme(ThemePreference preference) =>
        Enum.IsDefined(typeof(ThemePreference), preference) ? preference : ThemePreference.System;

    private void RaiseThemePreferenceChanged()
    {
        try { ThemePreferenceChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { _log($"ThemePreferenceChanged handler failed: {exception}"); }
    }

    private void RaiseDwmFrameCompensationChanged()
    {
        try { DwmFrameCompensationChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { _log($"DwmFrameCompensationChanged handler failed: {exception}"); }
    }
}
