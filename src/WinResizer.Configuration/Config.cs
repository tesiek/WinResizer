using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WinResizer.Common.Shortcuts;

namespace WinResizer.Configuration;

public class Config
{
    public string ProfileId { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public bool DisableInFullScreen { get; set; } = true;

    public bool RestoreAllIncludeMinimized { get; set; } = false;

    public bool NotifyOnSaved { get; set; } = false;

    public bool EnableResizeByTitle { get; set; } = true;

    public bool EnableAutoResizeDelay { get; set; } = false;

    public Dictionary<HotkeysType, Hotkeys> Keys { get; } = new();

    public BindingList<WindowSize> WindowSizes { get; set; } = new();

    public BindingList<WindowPreset> Presets { get; set; } = new();

    public BindingList<WindowSize> GetWindowSizes()
    {
        return WindowSizes;
    }

    [Obsolete]
    public Hotkeys? SaveKey { internal get; set; }

    [Obsolete]
    public Hotkeys? RestoreKey { internal get; set; }

    [Obsolete]
    public Hotkeys? RestoreAllKey { internal get; set; }

    public static Dictionary<HotkeysType, Hotkeys> DefaultKeys => new()
    {
        {
            HotkeysType.Save, new Hotkeys
            {
                ModifierKeys = new HashSet<string>
                {
                    "Ctrl", "Win", "Alt"
                },
                Key = "S"
            }
        },
        {
            HotkeysType.Restore, new Hotkeys
            {
                ModifierKeys = new HashSet<string>
                {
                    "Ctrl", "Win", "Alt"
                },
                Key = "R"
            }
        }
    };

    public static Config NewConfig(string profileName)
    {
        var c = new Config
        {
            ProfileId = GenerateConfigId(), DisableInFullScreen = true, ProfileName = profileName,
        };

        foreach (var key in DefaultKeys)
        {
            c.Keys.Add(key.Key, key.Value);
        }

        return c;
    }

    public void Migrate(Config migrateConfig)
    {
#pragma warning disable CS0612
        if (migrateConfig.SaveKey is not null && migrateConfig.SaveKey.IsValid())
        {
            Keys.Add(HotkeysType.Save, migrateConfig.SaveKey);
        }

        if (migrateConfig.RestoreKey is not null && migrateConfig.RestoreKey.IsValid())
        {
            Keys.Add(HotkeysType.Restore, migrateConfig.RestoreKey);
        }

        if (migrateConfig.RestoreAllKey is not null && migrateConfig.RestoreAllKey.IsValid())
        {
            Keys.Add(HotkeysType.RestoreAll, migrateConfig.RestoreAllKey);
        }
#pragma warning restore CS0612
    }

    public bool Equals(Config? x, Config? y)
    {
        if (x is null || y is null)
        {
            return false;
        }

        return x.ProfileId.Equals(y.ProfileId, StringComparison.Ordinal);
    }

    public int GetHashCode(Config obj)
    {
        return obj.ProfileId.GetHashCode();
    }

    public string? CheckHotkeyConflict(Hotkeys hotkey, string? excludePresetId = null, HotkeysType? excludeType = null)
    {
        if (hotkey is null || !hotkey.IsValid())
        {
            return null;
        }

        foreach (var entry in Keys)
        {
            if (excludeType.HasValue && entry.Key == excludeType.Value)
            {
                continue;
            }

            if (entry.Value is not null && entry.Value.Equals(hotkey))
            {
                return $"Hotkeys -> '{entry.Key}'";
            }
        }

        if (Presets is not null)
        {
            foreach (var preset in Presets)
            {
                if (!string.IsNullOrEmpty(excludePresetId) && preset.Id == excludePresetId)
                {
                    continue;
                }

                if (preset.Hotkey is not null && preset.Hotkey.Equals(hotkey))
                {
                    var name = string.IsNullOrWhiteSpace(preset.Name) ? "Unnamed" : preset.Name;
                    return $"Preset -> '{name}'";
                }
            }
        }

        return null;
    }

    public static string GenerateConfigId() =>
        Guid.NewGuid().ToString("N");
}
