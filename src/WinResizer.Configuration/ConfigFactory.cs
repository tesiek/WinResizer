using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WinResizer.Common.Shortcuts;

namespace WinResizer.Configuration;

public static class ConfigFactory
{
    private const string PreImportBackupFileName = "WinResizer.config.pre-import.bak";
    private static Action<string>? _callbackFailureLogger;

    public static string ConfigPath { get; private set; } = string.Empty;

    public static string PortableConfigPath => ConfigPath;

    public static string ConfigDirectory => Path.GetDirectoryName(ConfigPath) ?? string.Empty;

    public static void InitializePortablePath(string? baseDirectory = null)
    {
        SetPortablePath(ConfigPathProvider.GetPortableConfigPath(baseDirectory));
    }

    public static void SetPortablePath(string portablePath)
    {
        if (string.IsNullOrWhiteSpace(portablePath))
        {
            throw new ArgumentException("A portable configuration path is required.", nameof(portablePath));
        }

        ConfigPath = Path.GetFullPath(portablePath);
    }

    // Kept as a source-compatible bridge for existing callers and tests.  The
    // second argument is intentionally ignored: there is no roaming/AppData
    // configuration mode anymore.
    [Obsolete("Use SetPortablePath. The second path is no longer supported.")]
    public static void SetPath(string portablePath, string ignoredLegacyPath) => SetPortablePath(portablePath);

    private static void EnsureConfiguredPath()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            throw new InvalidOperationException("The portable configuration path has not been initialized.");
        }
    }

    public static readonly Profiles Profiles = new();

    public delegate void ConfigurationReplacingEvent();

    public static event ConfigurationReplacingEvent? ConfigurationReplacing;

    public delegate void ConfigurationReplacedEvent();

    public static event ConfigurationReplacedEvent? ConfigurationReplaced;

    public delegate void ConfigurationSavedEvent();

    public static event ConfigurationSavedEvent? ConfigurationSaved;

    public static void SetCallbackFailureLogger(Action<string>? logger) =>
        _callbackFailureLogger = logger;

    public static Config Current => GetCurrentConfig();

    #region Config

    public static void Load()
    {
        EnsureConfiguredPath();
        if (!File.Exists(ConfigPath))
        {
            UseDefault();
            Save();
            return;
        }

        var parsed = ParseCandidate(ConfigPath);
        if (parsed.RequiresPersistence)
        {
            WriteProfiles(ConfigPath, parsed.Profiles, null);
        }

        ReplaceLive(parsed);
    }

    /// <summary>
    /// Imports an external configuration into the active portable file.  The
    /// active file is written before the live model is replaced.
    /// </summary>
    public static void Load(string path)
        => LoadWithBackup(path);

    /// <summary>Imports into the portable configuration and returns the actual backup path,
    /// or null when there was no previous configuration file. Failures throw before live replacement.</summary>
    public static string? LoadWithBackup(string path)
    {
        EnsureConfiguredPath();
        var parsed = ParseCandidate(path);
        var backupPath = File.Exists(ConfigPath)
            ? Path.Combine(ConfigDirectory, PreImportBackupFileName)
            : null;
        if (!string.IsNullOrWhiteSpace(backupPath) &&
            Path.GetFullPath(path).Equals(Path.GetFullPath(backupPath), StringComparison.OrdinalIgnoreCase))
        {
            backupPath = Path.Combine(
                ConfigDirectory,
                $"WinResizer.config.pre-import-{Guid.NewGuid():N}.bak");
        }
        WriteProfiles(ConfigPath, parsed.Profiles, backupPath);
        ReplaceLive(parsed);
        return backupPath;
    }

    /// <summary>
    /// Loads a caller-supplied CLI/API configuration without changing or
    /// writing the active portable configuration path.
    /// </summary>
    public static void LoadExplicit(string path)
    {
        var parsed = ParseCandidate(path);
        ReplaceLive(parsed);
    }

    public static void Export(string path)
    {
        EnsureConfiguredPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An export path is required.", nameof(path));
        }

        var destination = Path.GetFullPath(path);
        if (destination.Equals(ConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The export path must be different from the active configuration path.");
        }

        WriteProfiles(destination, Profiles, null);
    }

    private static ParsedConfiguration ParseCandidate(string path)
    {
        var text = ReadConfigurationText(path);
        var document = JObject.Parse(text);
        var normalizedTheme = NormalizeThemePreference(document);
        var migrated = document.GetValue("Configs", StringComparison.OrdinalIgnoreCase) is null;
        Profiles candidate;
        if (migrated)
        {
            if (document.GetValue("WindowSizes", StringComparison.OrdinalIgnoreCase) is null)
                throw new JsonSerializationException("Unrecognized configuration format.");

            var config = LoadOldConfig(text)
                ?? throw new JsonSerializationException("Configuration is empty.");
            config.ProfileName = Profiles.DefaultProfileName;
            config.ProfileId = Config.GenerateConfigId();
            candidate = new Profiles(config.ProfileId, new System.Collections.Generic.List<Config> { config });
        }
        else
        {
            candidate = document.ToObject<Profiles>()
                ?? throw new JsonSerializationException("Configuration is empty.");
        }

        var normalizedIgnoredWindows = NormalizeIgnoredWindows(candidate);
        var normalizedAutoResizeDelays = NormalizeAutoResizeDelays(candidate);

        if (candidate.Configs is null || candidate.Configs.Count == 0 ||
            candidate.Configs.Any(c => c is null || string.IsNullOrWhiteSpace(c.ProfileId) ||
                                      c.WindowSizes is null || c.WindowSizes.Any(w => w is null)) ||
            candidate.Configs.Select(c => c.ProfileId).Distinct().Count() != candidate.Configs.Count)
            throw new JsonSerializationException("Invalid profiles.");

        foreach (var config in candidate.Configs)
        {
            ValidateConfigData(config);
            config.Presets = config.Presets ?? new BindingList<WindowPreset>();
            if (config.Presets.Any(p => p is null))
                throw new JsonSerializationException("Invalid preset.");
        }

        var currentId = candidate.Configs.Any(c => c.ProfileId == candidate.CurrentProfileId)
            ? candidate.CurrentProfileId
            : candidate.Configs[0].ProfileId;
        var normalizedCurrentProfileId = !string.Equals(
            candidate.CurrentProfileId,
            currentId,
            StringComparison.Ordinal);
        if (normalizedCurrentProfileId)
        {
            candidate.CurrentProfileId = currentId;
        }

        return new ParsedConfiguration(
            candidate,
            currentId,
            migrated || normalizedAutoResizeDelays || normalizedTheme ||
            normalizedIgnoredWindows || normalizedCurrentProfileId);
    }

    private static string ReadConfigurationText(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A configuration path is required.", nameof(path));
        }

        var fullPath = path;
        try
        {
            fullPath = Path.GetFullPath(path);
            return File.ReadAllText(fullPath);
        }
        catch (Exception exception) when (exception is IOException ||
                                          exception is UnauthorizedAccessException ||
                                          exception is ArgumentException ||
                                          exception is NotSupportedException)
        {
            throw new ConfigurationReadException(fullPath, exception);
        }
    }

    private static bool NormalizeThemePreference(JObject document)
    {
        var token = document.GetValue("ThemePreference", StringComparison.OrdinalIgnoreCase);
        if (token is null)
        {
            return false;
        }

        if (token.Type == JTokenType.String &&
            Enum.TryParse(token.Value<string>(), true, out ThemePreference textValue) &&
            Enum.IsDefined(typeof(ThemePreference), textValue))
        {
            var canonical = textValue.ToString();
            if (!string.Equals(token.Value<string>(), canonical, StringComparison.Ordinal))
            {
                document["ThemePreference"] = canonical;
                return true;
            }

            return false;
        }

        if (token.Type == JTokenType.Integer)
        {
            try
            {
                var numericValue = (ThemePreference)Convert.ToInt32(token.Value<long>());
                if (Enum.IsDefined(typeof(ThemePreference), numericValue))
                {
                    document["ThemePreference"] = numericValue.ToString();
                    return true;
                }
            }
            catch (Exception exception) when (exception is OverflowException ||
                                              exception is InvalidCastException ||
                                              exception is FormatException)
            {
                // Unknown numeric values use System below.
            }
        }

        document["ThemePreference"] = ThemePreference.System.ToString();
        return true;
    }

    private static void ReplaceLive(ParsedConfiguration parsed)
    {
        // All candidate parsing, validation, and (for imports) disk writes are
        // complete before any live object is replaced.
        InvokeConfigurationReplacingSubscribers();
        Profiles.Replace(
            parsed.Profiles.Configs,
            parsed.CurrentProfileId,
            parsed.Profiles.IgnoredWindows,
            parsed.Profiles.ThemePreference,
            parsed.Profiles.CompensateDwmFrameEffects);
        InvokeConfigurationReplacedSubscribers();
    }

    private static void WriteProfiles(string path, Profiles profiles, string? backupPath)
    {
        var json = JsonConvert.SerializeObject(profiles, Formatting.Indented);
        WriteTextAtomically(path, json, backupPath);
    }

    private static void WriteTextAtomically(string path, string content, string? backupPath)
    {
        var fullPath = Path.GetFullPath(path);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The configuration directory is unavailable.");
            }

            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                {
                    writer.Write(content);
                    writer.Flush();
                }

                stream.Flush(true);
            }

            if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(fullPath))
            {
                File.Copy(fullPath, Path.GetFullPath(backupPath), true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        catch (Exception exception)
        {
            throw new ConfigurationPersistenceException(fullPath, "write", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A failed cleanup must not hide the original write result.
            }
        }
    }

    private sealed class ParsedConfiguration
    {
        public ParsedConfiguration(Profiles profiles, string currentProfileId, bool requiresPersistence)
        {
            Profiles = profiles;
            CurrentProfileId = currentProfileId;
            RequiresPersistence = requiresPersistence;
        }

        public Profiles Profiles { get; }

        public string CurrentProfileId { get; }

        public bool RequiresPersistence { get; }
    }

    private static bool NormalizeAutoResizeDelays(Profiles profiles)
    {
        var changed = false;
        foreach (var config in profiles.Configs ?? Enumerable.Empty<Config>())
        {
            if (config?.WindowSizes is not null)
            {
                changed |= AutoResizeDelaySettings.Normalize(config.WindowSizes);
            }
        }

        return changed;
    }

    private static bool NormalizeIgnoredWindows(Profiles profiles)
    {
        var changed = profiles.IgnoredWindows is null;
        profiles.IgnoredWindows ??= new BindingList<IgnoredWindowRule>();

        for (var index = profiles.IgnoredWindows.Count - 1; index >= 0; index--)
        {
            var rule = profiles.IgnoredWindows[index];
            if (rule is null)
            {
                profiles.IgnoredWindows.RemoveAt(index);
                changed = true;
                continue;
            }

            if (!Enum.IsDefined(typeof(IgnoredWindowTitleMatch), rule.TitleMatch))
            {
                rule.TitleMatch = IgnoredWindowTitleMatch.Exact;
                rule.Active = false;
                changed = true;
            }
        }

        return changed;
    }

    private static void ValidateConfigData(Config config)
    {
        if (config.ProfileName is null || config.WindowSizes is null ||
            config.WindowSizes.Any(w => w is null || string.IsNullOrWhiteSpace(w.Name) || w.Title is null))
            throw new JsonSerializationException("Invalid process or profile data.");

        foreach (var entry in config.Keys)
        {
            if (!Enum.IsDefined(typeof(HotkeysType), entry.Key))
                throw new JsonSerializationException($"Invalid hotkey type: {entry.Key}.");

            ValidateHotkeyData(entry.Value, $"Profiles[\"{config.ProfileName}\"].Keys.{entry.Key}.Key");
        }

        if (config.Presets != null)
        {
            foreach (var preset in config.Presets)
            {
                if (preset is null || string.IsNullOrWhiteSpace(preset.Id) || preset.Name is null)
                    throw new JsonSerializationException("Invalid preset data.");
                ValidateHotkeyData(preset.Hotkey, $"Profiles[\"{config.ProfileName}\"].Presets[\"{preset.Name}\"].Hotkey.Key");
            }

            if (config.Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != config.Presets.Count)
                throw new JsonSerializationException("Duplicate preset IDs.");
        }

#pragma warning disable CS0612
        ValidateHotkeyData(config.SaveKey, $"Profiles[\"{config.ProfileName}\"].SaveKey.Key");
        ValidateHotkeyData(config.RestoreKey, $"Profiles[\"{config.ProfileName}\"].RestoreKey.Key");
        ValidateHotkeyData(config.RestoreAllKey, $"Profiles[\"{config.ProfileName}\"].RestoreAllKey.Key");
#pragma warning restore CS0612
    }

    private static void ValidateHotkeyData(Hotkeys? hotkey, string location)
    {
        // A null shortcut means unassigned; its modifier collection must exist
        // when an actual shortcut object is supplied.
        if (hotkey is null) return;

        if (hotkey.ModifierKeys is null ||
            hotkey.ModifierKeys.Any(m => string.IsNullOrWhiteSpace(m) ||
                !Enum.TryParse(m, true, out ModifierKeys modifier) ||
                !Enum.IsDefined(typeof(ModifierKeys), modifier)))
            throw new JsonSerializationException("Invalid hotkey modifiers.");

        // Preserve empty legacy shortcut objects, but reject partial combinations.
        var hasKey = !string.IsNullOrWhiteSpace(hotkey.Key);
        if (hasKey != (hotkey.ModifierKeys.Count > 0))
            throw new JsonSerializationException("Incomplete hotkey.");

        if (hasKey && !HotkeyMainKey.TryParse(hotkey.Key, out _))
            throw new JsonSerializationException($"Invalid hotkey key at {location}: \"{hotkey.Key}\".");
    }
    public static string? RecoverFromLoadFailure()
    {
        EnsureConfiguredPath();
        // Preserve the original before defaults can be saved by any later UI action.
        // A failed backup must propagate: continuing would risk losing user data.
        string? backupPath = null;
        if (File.Exists(ConfigPath))
        {
            backupPath = ConfigPath + ".load-failed-" + Guid.NewGuid().ToString("N") + ".bak";
            try
            {
                File.Copy(ConfigPath, backupPath, false);
            }
            catch (Exception exception)
            {
                throw new ConfigurationPersistenceException(
                    ConfigPath,
                    "create a recovery backup",
                    exception);
            }
        }

        UseDefault();
        return backupPath;
    }

    public static void Save()
    {
        PersistCandidate(Profiles);
        NotifyConfigurationSaved();
    }

    private static void PersistCandidate(Profiles candidate)
    {
        EnsureConfiguredPath();
        WriteProfiles(ConfigPath, candidate, null);
    }

    private static void NotifyConfigurationSaved()
    {
        InvokeBestEffort(
            "WindowSizes.ResetBindings",
            Current.WindowSizes.ResetBindings);
        if (Current.Presets is not null)
        {
            InvokeBestEffort(
                "Presets.ResetBindings",
                Current.Presets.ResetBindings);
        }

        InvokeConfigurationSavedSubscribers();
    }

    private static void InvokeConfigurationReplacingSubscribers()
    {
        var subscribers = ConfigurationReplacing;
        if (subscribers is null)
        {
            return;
        }

        foreach (ConfigurationReplacingEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(nameof(ConfigurationReplacing), subscriber, subscriber.Invoke);
        }
    }

    private static void InvokeConfigurationReplacedSubscribers()
    {
        var subscribers = ConfigurationReplaced;
        if (subscribers is null)
        {
            return;
        }

        foreach (ConfigurationReplacedEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(nameof(ConfigurationReplaced), subscriber, subscriber.Invoke);
        }
    }

    private static void InvokeConfigurationSavedSubscribers()
    {
        var subscribers = ConfigurationSaved;
        if (subscribers is null)
        {
            return;
        }

        foreach (ConfigurationSavedEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(nameof(ConfigurationSaved), subscriber, subscriber.Invoke);
        }
    }

    private static void InvokeProfileSwitchingSubscribers(string currentProfileId, string nextProfileId)
    {
        var subscribers = Profiles.ProfileEvents.ProfileSwitching;
        if (subscribers is null)
        {
            return;
        }

        foreach (ProfileEvents.ProfileSwitchingEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(
                nameof(ProfileEvents.ProfileSwitching),
                subscriber,
                () => subscriber(currentProfileId, nextProfileId));
        }
    }

    private static void InvokeProfileSwitchSubscribers(string profileId)
    {
        var subscribers = Profiles.ProfileEvents.ProfileSwitch;
        if (subscribers is null)
        {
            return;
        }

        foreach (ProfileEvents.ProfileSwitchEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(
                nameof(ProfileEvents.ProfileSwitch),
                subscriber,
                () => subscriber(profileId));
        }
    }

    private static void InvokeProfileAddSubscribers(string profileId, string profileName)
    {
        var subscribers = Profiles.ProfileEvents.ProfileAdd;
        if (subscribers is null)
        {
            return;
        }

        foreach (ProfileEvents.ProfileAddEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(
                nameof(ProfileEvents.ProfileAdd),
                subscriber,
                () => subscriber(profileId, profileName));
        }
    }

    private static void InvokeProfileRemoveSubscribers(string profileId)
    {
        var subscribers = Profiles.ProfileEvents.ProfileRemove;
        if (subscribers is null)
        {
            return;
        }

        foreach (ProfileEvents.ProfileRemoveEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(
                nameof(ProfileEvents.ProfileRemove),
                subscriber,
                () => subscriber(profileId));
        }
    }

    private static void InvokeProfileRenameSubscribers(string profileId, string profileName)
    {
        var subscribers = Profiles.ProfileEvents.ProfileRename;
        if (subscribers is null)
        {
            return;
        }

        foreach (ProfileEvents.ProfileRenameEvent subscriber in subscribers.GetInvocationList())
        {
            InvokeBestEffort(
                nameof(ProfileEvents.ProfileRename),
                subscriber,
                () => subscriber(profileId, profileName));
        }
    }

    private static void InvokeBestEffort(string stage, Action callback) =>
        InvokeBestEffort(stage, callback, callback);

    private static void InvokeBestEffort(string stage, Delegate callback, Action invoke)
    {
        try
        {
            invoke();
        }
        catch (Exception exception)
        {
            ReportCallbackFailure(stage, callback, exception);
        }
    }

    private static void ReportCallbackFailure(string stage, Delegate callback, Exception exception)
    {
        var declaringType = callback.Method.DeclaringType?.FullName;
        var callbackName = string.IsNullOrWhiteSpace(declaringType)
            ? callback.Method.Name
            : declaringType + "." + callback.Method.Name;
        var message =
            $"Configuration callback failed during {stage} ({callbackName}): {exception}";

        try
        {
            _callbackFailureLogger?.Invoke(message);
        }
        catch
        {
            // Logging must never turn a post-commit notification failure into an operation failure.
        }

        try
        {
            Trace.TraceError(message);
        }
        catch
        {
            // Diagnostic fallback is also best effort.
        }
    }

    private static Profiles CreatePersistenceCandidate(
        IEnumerable<Config> configs,
        string currentProfileId) =>
        new(currentProfileId, configs.ToList())
        {
            ThemePreference = Profiles.ThemePreference,
            CompensateDwmFrameEffects = Profiles.CompensateDwmFrameEffects,
            IgnoredWindows = Profiles.IgnoredWindows,
        };

    #endregion

    #region Profiles

    public static void UseDefault() =>
        Profiles.UseDefault();

    public static Config ProfileAdd(string profileName)
    {
        var profile = Config.NewConfig(profileName);
        var candidate = CreatePersistenceCandidate(
            Profiles.Configs.Concat(new[] { profile }),
            Profiles.CurrentProfileId);
        PersistCandidate(candidate);

        Profiles.Configs.Add(profile);
        InvokeProfileAddSubscribers(profile.ProfileId, profile.ProfileName);
        NotifyConfigurationSaved();
        return profile;
    }

    public static void ProfileRename(string profileId, string profileName)
    {
        var profile = Profiles.Configs.FirstOrDefault(item => item.ProfileId == profileId);
        if (profile is null)
        {
            return;
        }

        var previousName = profile.ProfileName;
        profile.ProfileName = profileName;
        try
        {
            Save();
        }
        catch (ConfigurationPersistenceException)
        {
            profile.ProfileName = previousName;
            throw;
        }

        InvokeProfileRenameSubscribers(profileId, profileName);
    }

    public static bool ProfileRemove(string profileId)
    {
        var profile = Profiles.Configs.FirstOrDefault(item =>
            item.ProfileId.Equals(profileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return false;
        }

        var candidate = CreatePersistenceCandidate(
            Profiles.Configs.Where(item => !ReferenceEquals(item, profile)),
            Profiles.CurrentProfileId);
        PersistCandidate(candidate);

        Profiles.Configs.Remove(profile);
        InvokeProfileRemoveSubscribers(profile.ProfileId);
        NotifyConfigurationSaved();
        return true;
    }

    public static bool ProfileSwitch(string profileId)
    {
        if (profileId.Equals(Profiles.CurrentProfileId, StringComparison.Ordinal))
        {
            Save();
            return true;
        }

        var profile = Profiles.Configs.FirstOrDefault(item =>
            item.ProfileId.Equals(profileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return false;
        }

        var candidate = CreatePersistenceCandidate(Profiles.Configs, profile.ProfileId);
        PersistCandidate(candidate);

        InvokeProfileSwitchingSubscribers(Profiles.CurrentProfileId, profile.ProfileId);
        Profiles.CurrentProfileId = profile.ProfileId;
        InvokeProfileSwitchSubscribers(profile.ProfileId);
        NotifyConfigurationSaved();
        return true;
    }

    #endregion

    public static Hotkeys? GetKeys(this Config config, HotkeysType type)
    {
        return config.Keys.TryGetValue(type, out var k) ? k : null;
    }

    public static Hotkeys SetKeys(this Config config, HotkeysType type, Hotkeys hotkeys)
    {
        var configKeys = config.GetKeys(type) ?? new Hotkeys();
        configKeys.ModifierKeys.Clear();
        foreach (var key in hotkeys.ModifierKeys)
        {
            configKeys.ModifierKeys.Add(key);
        }

        configKeys.Key = hotkeys.Key;
        config.Keys[type] = configKeys;
        return configKeys;
    }

    private static Config GetCurrentConfig()
    {
        var cur = Profiles.Current;
        if (cur is not null)
        {
            return cur;
        }

        var f = Profiles.Configs.FirstOrDefault();
        if (f is null)
        {
            return Profiles.UseDefault();
        }

        ProfileSwitch(f.ProfileId);
        return f;
    }

    #region migrate config(v1.1.0) to profiles(v1.2.0)

    private static Config? LoadOldConfig(string text)
    {
        var c = JsonConvert.DeserializeObject<Config>(text);
        if (c is null)
        {
            return null;
        }

        ValidateConfigData(c);

        var config = new Config();
        foreach (var key in c.Keys)
        {
            config.SetKeys(key.Key, key.Value);
        }

        config.DisableInFullScreen = c.DisableInFullScreen;
        config.RestoreAllIncludeMinimized = c.RestoreAllIncludeMinimized;
        config.NotifyOnSaved = c.NotifyOnSaved;
        config.EnableResizeByTitle = c.EnableResizeByTitle;
        config.EnableAutoResizeDelay = c.EnableAutoResizeDelay;
        config.WindowSizes = c.WindowSizes;
        config.Presets = c.Presets ?? new BindingList<WindowPreset>();

        config.Migrate(c);

        if (!config.WindowSizes.Any())
        {
            return config;
        }

        var sortedInstance = new BindingList<WindowSize>(
            config.WindowSizes
                .OrderBy(w => w.Name)
                .ThenBy(w => w.Title)
                .ToList()
        );
        config.WindowSizes = sortedInstance;
        return config;
    }

    #endregion
}
