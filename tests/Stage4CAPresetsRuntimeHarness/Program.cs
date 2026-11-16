using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Runtime;

internal sealed class FakeRegistrar : IGlobalHotkeyRegistrar
{
    private int _nextId;
    public readonly Dictionary<int, (ModifierKeys Modifier, Keys Key)> Active = new();
    public Keys? FailKey { get; set; }
    public int RegisterCount { get; private set; }
    public int UnregisterCount { get; private set; }
    public event EventHandler<KeyPressedEventArgs>? KeyPressed;

    public void Raise(KeyPressedEventArgs args) => KeyPressed?.Invoke(this, args);

    public int RegisterHotKey(ModifierKeys modifier, Keys key)
    {
        if (FailKey == key || Active.Values.Any(item => item.Modifier == modifier && item.Key == key))
            throw new HotkeyHookException();

        var id = ++_nextId;
        Active.Add(id, (modifier, key));
        RegisterCount++;
        return id;
    }

    public void UnRegisterHotKey(int id)
    {
        Active.Remove(id);
        UnregisterCount++;
    }

    public void UnRegisterHotKey()
    {
        UnregisterCount += Active.Count;
        Active.Clear();
    }

    public bool Has(ModifierKeys modifier, Keys key) =>
        Active.Values.Any(item => item.Modifier == modifier && item.Key == key);

    public void Dispose() => UnRegisterHotKey();
}

internal static class Program
{
    private static int _assertions;

    private static Hotkeys Key(Keys key) => new()
    {
        ModifierKeys = new HashSet<string> { "Ctrl" },
        Key = key.ToString(),
    };

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertPreset(RuntimePresetInfo preset, bool enabled, string? key)
    {
        Assert(preset.Enabled == enabled, $"Enabled mismatch for {preset.PresetId}.");
        Assert(string.Equals(preset.Hotkey?.Key, key, StringComparison.Ordinal),
            $"Hotkey mismatch for {preset.PresetId}: {preset.Hotkey?.Key} vs {key}.");
    }

    private static string WriteImport(string directory, string name, string profileName, string presetName, Keys key)
    {
        var profile = Config.NewConfig(profileName);
        profile.Presets.Add(new WindowPreset
        {
            Name = presetName,
            Enabled = true,
            Width = 1280,
            Height = 720,
            Hotkey = Key(key),
        });
        var profiles = new Profiles(profile.ProfileId, new List<Config> { profile });
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, JsonConvert.SerializeObject(profiles, Formatting.Indented));
        return path;
    }

    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-Stage4CA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "WinResizer.config.json");
        var saveFailure = false;
        var registrar = new FakeRegistrar();
        var registeredHotkeys = new Dictionary<HotkeysType, int>();
        var registeredPresets = new Dictionary<string, int>();

        try
        {
            ConfigFactory.SetPortablePath(configPath);
            ConfigFactory.Profiles.UseDefault();
            var profileA = ConfigFactory.Current;
            var profileB = ConfigFactory.Profiles.Add("B");
            ConfigFactory.Save();

            void Save()
            {
                if (saveFailure)
                    throw new IOException("Injected Save failure.");
                ConfigFactory.Save();
            }

            using var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar,
                OwnsHotkeyRegistrar = false,
                EnableWindowMonitoring = false,
                RegisteredHotkeys = registeredHotkeys,
                RegisteredPresetHotkeys = registeredPresets,
                SaveConfiguration = Save,
            });
            runtime.Start();
            var eventCount = 0;
            runtime.PresetsChanged += (_, _) => eventCount++;

            var preset = runtime.AddPreset(profileA.ProfileId, "Main");
            preset = runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F1));
            AssertPreset(preset, true, "F1");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F1), "A: H1 was not registered.");

            preset = runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, false);
            AssertPreset(preset, false, "F1");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F1), "B: H1 remained registered.");

            preset = runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, true);
            AssertPreset(preset, true, "F1");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F1), "C: H1 was not restored.");

            runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, false);
            preset = runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F2));
            AssertPreset(preset, false, "F2");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F2), "D: inactive H2 was registered.");
            preset = runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, null);
            AssertPreset(preset, false, null);

            runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F1));
            runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, true);
            preset = runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F2));
            AssertPreset(preset, true, "F2");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F2) && !registrar.Has(ModifierKeys.Ctrl, Keys.F1), "F: replacement is wrong.");
            preset = runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, null);
            AssertPreset(preset, true, null);
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F2), "G: cleared H2 remained registered.");

            runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F1));
            registrar.FailKey = Keys.F2;
            var eventsBeforeFailure = eventCount;
            var jsonBeforeRegistrationFailure = File.ReadAllText(configPath);
            ExpectFailure(() => runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F2)), "H: registration failure was accepted.");
            preset = runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == preset.PresetId);
            AssertPreset(preset, true, "F1");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F1), "H: H1 was not preserved.");
            Assert(File.ReadAllText(configPath) == jsonBeforeRegistrationFailure, "H: JSON changed after registration failure.");
            Assert(eventCount == eventsBeforeFailure, "H: failure raised PresetsChanged.");

            registrar.FailKey = null;
            runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, false);
            registrar.FailKey = Keys.F1;
            eventsBeforeFailure = eventCount;
            ExpectFailure(() => runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, true), "I: enable failure was accepted.");
            preset = runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == preset.PresetId);
            AssertPreset(preset, false, "F1");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F1), "I: H1 was registered after failed enable.");
            Assert(eventCount == eventsBeforeFailure, "I: failure raised PresetsChanged.");

            registrar.FailKey = null;
            runtime.SetPresetEnabled(profileA.ProfileId, preset.PresetId, true);
            var registerBeforeNoOp = registrar.RegisterCount;
            var unregisterBeforeNoOp = registrar.UnregisterCount;
            runtime.SetPresetHotkey(profileA.ProfileId, preset.PresetId, Key(Keys.F1));
            Assert(registerBeforeNoOp == registrar.RegisterCount && unregisterBeforeNoOp == registrar.UnregisterCount, "J: same key was not a no-op.");

            Assert(runtime.RemovePreset(profileA.ProfileId, preset.PresetId), "K: active remove failed.");
            Assert(!registeredPresets.ContainsKey(preset.PresetId), "K: registration map retained removed preset.");

            var second = runtime.AddPreset(profileA.ProfileId, "Second");
            second = runtime.SetPresetHotkey(profileA.ProfileId, second.PresetId, Key(Keys.F1));
            var secondId = second.PresetId;
            second = runtime.UpdatePreset(profileA.ProfileId, second.PresetId, "Second Updated", 10, 20, 1000, 800);
            Assert(second.PresetId == secondId && second.Enabled && second.Hotkey?.Key == "F1" &&
                   second.X == 10 && second.Y == 20 && second.Width == 1000 && second.Height == 800,
                "Update changed fields outside its contract.");
            var jsonBeforeRemoveFailure = File.ReadAllText(configPath);
            saveFailure = true;
            ExpectFailure(() => runtime.RemovePreset(profileA.ProfileId, second.PresetId), "L: remove Save failure was accepted.");
            Assert(runtime.GetPresets(profileA.ProfileId).Any(item => item.PresetId == second.PresetId), "L: preset was lost after Save failure.");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F1), "L: registration was lost after Save failure.");
            Assert(File.ReadAllText(configPath) == jsonBeforeRemoveFailure, "L: JSON changed after Remove Save failure.");

            var jsonBeforeSetFailure = File.ReadAllText(configPath);
            ExpectFailure(() => runtime.SetPresetHotkey(profileA.ProfileId, second.PresetId, Key(Keys.F2)), "M: Set Key Save failure was accepted.");
            AssertPreset(runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == second.PresetId), true, "F1");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F1), "M: H1 was lost after Save failure.");
            Assert(File.ReadAllText(configPath) == jsonBeforeSetFailure, "M: JSON changed after Set Key Save failure.");
            var jsonBeforeEnableFailure = File.ReadAllText(configPath);
            ExpectFailure(() => runtime.SetPresetEnabled(profileA.ProfileId, second.PresetId, false), "N: Active Save failure was accepted.");
            AssertPreset(runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == second.PresetId), true, "F1");
            Assert(File.ReadAllText(configPath) == jsonBeforeEnableFailure, "N: JSON changed after Active Save failure.");
            saveFailure = false;

            var inactive = runtime.AddPreset(profileB.ProfileId, "Inactive");
            inactive = runtime.SetPresetHotkey(profileB.ProfileId, inactive.PresetId, Key(Keys.F5));
            Assert(!registeredPresets.ContainsKey(inactive.PresetId), "Inactive profile preset was registered.");
            var inactiveConflict = runtime.AddPreset(profileB.ProfileId, "Conflict");
            ExpectFailure(() => runtime.SetPresetHotkey(profileB.ProfileId, inactiveConflict.PresetId, Key(Keys.F5)), "Inactive conflict was accepted.");

            runtime.SwitchProfile(profileB.ProfileId);
            Assert(!registeredPresets.ContainsKey(second.PresetId) && registeredPresets.ContainsKey(inactive.PresetId), "O: profile B registrations are wrong.");
            runtime.SwitchProfile(profileA.ProfileId);
            Assert(registeredPresets.ContainsKey(second.PresetId) && !registeredPresets.ContainsKey(inactive.PresetId), "O: profile A registrations are wrong.");

            var importPath = WriteImport(directory, "import.json", "Imported", "Imported preset", Keys.F3);
            ConfigFactory.Load(importPath);
            var importedProfileId = ConfigFactory.Current.ProfileId;
            var imported = runtime.GetPresets(importedProfileId).Single();
            Assert(imported.Enabled && imported.Hotkey?.Key == "F3", "P: imported preset model is wrong.");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F3) && registeredPresets.Count == 1, "P: imported registrations are wrong.");

            var identical = Path.Combine(directory, "identical.json");
            runtime.SetPresetHotkey(importedProfileId, imported.PresetId, Key(Keys.D1));
            ConfigFactory.Export(identical);
            var registerBefore = registrar.RegisterCount;
            var unregisterBefore = registrar.UnregisterCount;
            var oldCount = registrar.Active.Count;
            ConfigFactory.ConfigurationReplacingEvent replacing = () => Assert(registrar.Active.Count == 0,
                "Replacement started while old global/preset registrations were still active.");
            ConfigFactory.ConfigurationReplacing += replacing;
            try { ConfigFactory.Load(identical); }
            finally { ConfigFactory.ConfigurationReplacing -= replacing; }
            Assert(registrar.Active.Count == oldCount && registrar.RegisterCount == registerBefore + oldCount
                && registrar.UnregisterCount == unregisterBefore + oldCount, "Identical import did not release/recreate registrations exactly once.");
            registerBefore = registrar.RegisterCount; unregisterBefore = registrar.UnregisterCount;
            ConfigFactory.Save();
            Assert(registrar.RegisterCount == registerBefore && registrar.UnregisterCount == unregisterBefore,
                "Ordinary Save reset hotkeys.");

            registrar.FailKey = Keys.F4;
            var failingImport = WriteImport(directory, "failing-import.json", "Failing", "Failing preset", Keys.F4);
            ConfigFactory.Load(failingImport);
            var failingProfileId = ConfigFactory.Current.ProfileId;
            var failing = runtime.GetPresets(failingProfileId).Single();
            Assert(failing.Enabled && failing.Hotkey?.Key == "F4", "Q: registration failure changed the model.");
            Assert(!registeredPresets.ContainsKey(failing.PresetId), "Q: failed registration remained mapped.");

            var json = File.ReadAllText(configPath);
            Assert(json.IndexOf("\"Enabled\": true", StringComparison.Ordinal) >= 0, "Q: registration failure rewrote Enabled.");
            Assert(eventCount > 0, "PresetsChanged was never raised after successful operations.");
            Console.WriteLine($"PASS: Presets runtime API transaction matrix A-Q, profile scope, import reload, rollback, and no-op semantics ({_assertions} assertions).");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
