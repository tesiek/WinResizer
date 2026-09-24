using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace WinResizer.Configuration;

public class Profiles
{
    private readonly object _ignoredWindowsSync = new();

    public Profiles()
    {
    }

    [JsonConstructor]
    public Profiles(string currentProfileId, List<Config> configs)
    {
        CurrentProfileId = currentProfileId;
        Configs = configs;
    }

    public string CurrentProfileId { get; internal set; } = string.Empty;

    public List<Config> Configs { get; private set; } = new();

    /// <summary>
    /// Global theme preference.  It intentionally lives beside Profiles and is
    /// not part of an individual profile.
    /// </summary>
    [JsonConverter(typeof(ThemePreferenceJsonConverter))]
    public ThemePreference ThemePreference { get; set; } = ThemePreference.System;

    /// <summary>
    /// Global geometry preference. When enabled, configured rectangles represent
    /// the user-visible DWM frame rather than the raw Win32 window rectangle.
    /// </summary>
    public bool CompensateDwmFrameEffects { get; set; } = true;

    /// <summary>
    /// Window ignore rules are global and do not belong to an individual profile.
    /// </summary>
    public BindingList<IgnoredWindowRule> IgnoredWindows { get; set; } = new();

    [JsonIgnore] public const string DefaultProfileName = "default";

    [JsonIgnore] public Config? Current => Get(CurrentProfileId);

    [JsonIgnore] public readonly ProfileEvents ProfileEvents = new();

    public Config UseDefault()
    {
        Configs.Clear();
        ThemePreference = ThemePreference.System;
        CompensateDwmFrameEffects = true;
        lock (_ignoredWindowsSync)
        {
            IgnoredWindows ??= new BindingList<IgnoredWindowRule>();
            IgnoredWindows.Clear();
        }
        var defaultConfig = Add(DefaultProfileName);
        CurrentProfileId = defaultConfig.ProfileId;
        return defaultConfig;
    }

    public List<IgnoredWindowRule> GetIgnoredWindowsSnapshot()
    {
        lock (_ignoredWindowsSync)
        {
            IgnoredWindows ??= new BindingList<IgnoredWindowRule>();
            return IgnoredWindows
                .Where(rule => rule is not null)
                .Select(rule => new IgnoredWindowRule
                {
                    Active = rule.Active,
                    Process = rule.Process,
                    Class = rule.Class,
                    Title = rule.Title,
                    TitleMatch = rule.TitleMatch,
                })
                .ToList();
        }
    }

    public void AddIgnoredWindow(IgnoredWindowRule rule)
    {
        lock (_ignoredWindowsSync)
        {
            IgnoredWindows ??= new BindingList<IgnoredWindowRule>();
            IgnoredWindows.Add(rule);
        }
    }

    public bool RemoveIgnoredWindow(IgnoredWindowRule rule)
    {
        lock (_ignoredWindowsSync)
        {
            IgnoredWindows ??= new BindingList<IgnoredWindowRule>();
            return IgnoredWindows.Remove(rule);
        }
    }

    public void Replace(
        IEnumerable<Config> configs,
        string currentProfileId,
        IEnumerable<IgnoredWindowRule> ignoredWindows,
        ThemePreference themePreference = ThemePreference.System,
        bool compensateDwmFrameEffects = true)
    {
        Configs.Clear();
        Configs.AddRange(configs);
        ReplaceIgnoredWindows(ignoredWindows);
        CurrentProfileId = currentProfileId;
        ThemePreference = Enum.IsDefined(typeof(ThemePreference), themePreference)
            ? themePreference
            : ThemePreference.System;
        CompensateDwmFrameEffects = compensateDwmFrameEffects;
    }

    public void ReplaceIgnoredWindows(IEnumerable<IgnoredWindowRule> rules)
    {
        lock (_ignoredWindowsSync)
        {
            IgnoredWindows = new BindingList<IgnoredWindowRule>(rules.Where(rule => rule is not null).ToList());
        }
    }

    public bool Rename(string profileId, string profileName)
    {
        var profile = Get(profileId);
        if (profile is null)
        {
            return false;
        }

        profile.ProfileName = profileName;
        ProfileEvents.ProfileRename?.Invoke(profileId, profileName);
        return true;
    }

    public Config Add(string profileName)
    {
        var newConfig = Config.NewConfig(profileName);
        Configs.Add(newConfig);
        ProfileEvents.ProfileAdd?.Invoke(newConfig.ProfileId, newConfig.ProfileName);
        return newConfig;
    }

    public bool Remove(string profileId)
    {
        var config = Get(profileId);
        if (config is null)
        {
            return false;
        }

        Configs.Remove(config);
        ProfileEvents.ProfileRemove?.Invoke(config.ProfileId);
        return true;
    }


    public bool Switch(string profileId)
    {
        if (profileId.Equals(CurrentProfileId, StringComparison.Ordinal))
        {
            return true;
        }

        var p = Get(profileId);
        if (p is null)
        {
            return false;
        }

        ProfileEvents.ProfileSwitching?.Invoke(CurrentProfileId, p.ProfileId);
        CurrentProfileId = p.ProfileId;
        ProfileEvents.ProfileSwitch?.Invoke(p.ProfileId);
        return true;
    }

    private Config? Get(string profileId) =>
        Configs.FirstOrDefault(i => i.ProfileId.Equals(profileId, StringComparison.Ordinal));
}
