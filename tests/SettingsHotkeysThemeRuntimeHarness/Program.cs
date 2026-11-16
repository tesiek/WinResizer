using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;

internal static class Program
{
    private static int _assertions;
    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectSaveFailure(Action action)
    {
        try { action(); }
        catch (IOException) { _assertions++; return; }
        throw new InvalidOperationException("Expected persistence failure.");
    }

    private static object Private(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static JObject Document() => JObject.Parse(JsonConvert.SerializeObject(ConfigFactory.Profiles));
    private static Hotkeys Key(string key) => new Hotkeys { Key = key, ModifierKeys = new HashSet<string> { "Ctrl" } };

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) { return exception; }
    }

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-Settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WinResizerRuntime? runtime = null;
        try
        {
            var path = Path.Combine(directory, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(path);
            ConfigFactory.UseDefault();
            var a = ConfigFactory.Current;
            var b = ConfigFactory.Profiles.Add("B");
            ConfigFactory.Save();
            var failSave = false;
            var saves = 0;
            var registrar = new Registrar();
            var registrations = new Dictionary<HotkeysType, int>();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableWindowMonitoring = false, HotkeyRegistrar = registrar, RegisteredHotkeys = registrations,
                SaveConfiguration = () => { if (failSave) throw new IOException("test failure"); ConfigFactory.Save(); saves++; },
            });
            runtime.Start();
            var optionsEvents = 0;
            var hotkeysEvents = 0;
            var themeEvents = 0;
            runtime.ProfileOptionsChanged += (_, _) => optionsEvents++;
            runtime.HotkeysChanged += (_, _) => hotkeysEvents++;
            runtime.ThemePreferenceChanged += (_, _) => themeEvents++;
            Assert(runtime.GetHotkeys().Single(row => row.Type == HotkeysType.Save).Hotkey!.Equals(Config.DefaultKeys[HotkeysType.Save]) &&
                runtime.GetHotkeys().Single(row => row.Type == HotkeysType.Restore).Hotkey!.Equals(Config.DefaultKeys[HotkeysType.Restore]) &&
                runtime.GetHotkeys().Where(row => row.Type == HotkeysType.SaveAll || row.Type == HotkeysType.RestoreAll).All(row => row.Hotkey == null),
                "Fresh defaults changed.");

            var options = new (string Field, Func<bool> Get, Action<bool> Set)[]
            {
                (nameof(Config.RestoreAllIncludeMinimized), runtime.GetRestoreAllIncludeMinimized, runtime.SetRestoreAllIncludeMinimized),
                (nameof(Config.DisableInFullScreen), runtime.GetDisableInFullScreen, runtime.SetDisableInFullScreen),
                (nameof(Config.EnableResizeByTitle), runtime.GetEnableResizeByTitle, runtime.SetEnableResizeByTitle),
                (nameof(Config.EnableAutoResizeDelay), runtime.GetEnableAutoResizeDelay, runtime.SetEnableAutoResizeDelay),
            };
            foreach (var option in options)
            {
                var previous = option.Get();
                var expected = Document();
                ((JObject)((JArray)expected["Configs"]!)[0]!)[option.Field] = !previous;
                var beforeEvents = optionsEvents;
                var beforeSaves = saves;
                option.Set(!previous);
                Assert(option.Get() == !previous && JToken.DeepEquals(expected, Document()), "Option changed unrelated data: " + option.Field);
                Assert(optionsEvents == beforeEvents + 1 && saves == beforeSaves + 1, "Option did not save/notify once.");
                option.Set(!previous);
                Assert(optionsEvents == beforeEvents + 1 && saves == beforeSaves + 1, "Option no-op persisted/notified.");
                failSave = true;
                try { ExpectSaveFailure(() => option.Set(previous)); }
                finally { failSave = false; }
                Assert(option.Get() == !previous && JToken.DeepEquals(expected, Document()) && optionsEvents == beforeEvents + 1,
                    "Option failure changed config or emitted success.");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectSaveFailure(() => option.Set(previous));
                Assert(option.Get() == !previous && JToken.DeepEquals(expected, Document()), "Real Save failure did not roll back.");
            }
            var aValues = options.Select(option => option.Get()).ToArray();
            var optionsBeforeSwitch = optionsEvents;
            runtime.SwitchProfile(b.ProfileId);
            for (var i = 0; i < options.Length; i++) Assert(options[i].Get() != aValues[i], "B did not retain its own option.");
            runtime.SwitchProfile(a.ProfileId);
            for (var i = 0; i < options.Length; i++) Assert(options[i].Get() == aValues[i], "A option lost after A-B-A.");
            Assert(optionsEvents == optionsBeforeSwitch + 2, "Switch did not notify option consumers.");

            var actions = Enum.GetValues(typeof(HotkeysType)).Cast<HotkeysType>().ToArray();
            for (var i = 0; i < actions.Length; i++) runtime.SetHotkey(actions[i], Key("F" + (i + 1)));
            foreach (var action in actions)
            {
                var oldId = registrations[action];
                var expected = Document();
                var beforeEvents = hotkeysEvents;
                failSave = true;
                try { ExpectSaveFailure(() => runtime.SetHotkey(action, null)); }
                finally { failSave = false; }
                Assert(JToken.DeepEquals(expected, Document()) && registrations[action] == oldId && registrar.Ids.Contains(oldId) &&
                    hotkeysEvents == beforeEvents, "Failed Clear changed hotkey/registration/event.");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectSaveFailure(() => runtime.SetHotkey(action, null));
                Assert(JToken.DeepEquals(expected, Document()) && registrar.Ids.Contains(oldId), "Real Clear Save failure lost registration.");
                var otherIds = registrations.Where(entry => entry.Key != action).ToDictionary(entry => entry.Key, entry => entry.Value);
                var beforeUnregister = registrar.Removed.Count;
                ((JObject)((JArray)expected["Configs"]!)[0]!["Keys"]!).Remove(action.ToString());
                runtime.SetHotkey(action, null);
                Assert(!a.Keys.ContainsKey(action) && runtime.GetHotkeys().Single(row => row.Type == action).Hotkey == null &&
                    JToken.DeepEquals(expected, Document()), "Clear was not isolated or left an assigned hotkey.");
                Assert(registrar.Removed.Count == beforeUnregister + 1 && registrar.Removed.Last() == oldId &&
                    !registrations.ContainsKey(action) && otherIds.All(entry => registrations[entry.Key] == entry.Value),
                    "Clear removed the wrong registration.");
                Assert(hotkeysEvents == beforeEvents + 1, "Clear did not notify once.");
                var beforeNoOpSave = saves;
                runtime.SetHotkey(action, null);
                Assert(saves == beforeNoOpSave && hotkeysEvents == beforeEvents + 1, "Unassigned Clear was not no-op.");
                runtime.SetHotkey(action, Key("F" + ((int)action + 4)));
                Assert(registrations.ContainsKey(action) && a.Keys[action].IsValid(), "Reassignment after Clear failed.");
            }
            runtime.SetHotkey(HotkeysType.Save, null);
            runtime.SwitchProfile(b.ProfileId);
            Assert(runtime.GetHotkeys().Single(row => row.Type == HotkeysType.Save).Hotkey != null, "Clear affected B.");
            runtime.SwitchProfile(a.ProfileId);
            Assert(runtime.GetHotkeys().Single(row => row.Type == HotkeysType.Save).Hotkey == null, "Cleared A shortcut returned after switch.");

            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark, ThemePreference.System })
            {
                var expected = Document();
                expected["ThemePreference"] = theme.ToString();
                var beforeEvents = themeEvents;
                runtime.SetThemePreference(theme);
                Assert(runtime.GetThemePreference() == theme && JToken.DeepEquals(expected, Document()) && themeEvents == beforeEvents + 1,
                    "Theme mutation changed profile data or failed to notify.");
                Assert(JObject.Parse(File.ReadAllText(path))["ThemePreference"]!.Value<string>() == theme.ToString(), "Theme not persisted.");
                runtime.SwitchProfile(b.ProfileId);
                runtime.SwitchProfile(a.ProfileId);
                Assert(runtime.GetThemePreference() == theme && themeEvents == beforeEvents + 1, "Theme depended on active profile.");
                ConfigFactory.Load();
                Assert(runtime.GetThemePreference() == theme && themeEvents == beforeEvents + 2, "Reload did not retain/notify Theme.");
            }
            var beforeFailureDocument = Document();
            var beforeFailureThemeEvents = themeEvents;
            failSave = true;
            try { ExpectSaveFailure(() => runtime.SetThemePreference(ThemePreference.Dark)); }
            finally { failSave = false; }
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                ExpectSaveFailure(() => runtime.SetThemePreference(ThemePreference.Dark));
            Assert(JToken.DeepEquals(beforeFailureDocument, Document()) && themeEvents == beforeFailureThemeEvents, "Theme Save failure did not roll back.");
            var importPath = Path.Combine(directory, "import.json");
            var imported = Document();
            imported["ThemePreference"] = "Dark";
            File.WriteAllText(importPath, imported.ToString());
            ConfigFactory.Load(importPath);
            Assert(runtime.GetThemePreference() == ThemePreference.Dark && themeEvents == beforeFailureThemeEvents + 1,
                "Import did not refresh Theme.");
            imported["ThemePreference"] = "unknown-theme";
            File.WriteAllText(importPath, imported.ToString());
            ConfigFactory.Load(importPath);
            Assert(runtime.GetThemePreference() == ThemePreference.System, "Unknown imported Theme not normalized.");
            runtime.SetThemePreference(ThemePreference.Dark);
            runtime.SetThemePreference((ThemePreference)999);
            Assert(runtime.GetThemePreference() == ThemePreference.System, "Unknown API Theme not normalized.");

            // Probe a real pending HWND operation while each option is saved.
            using (var form = new Form { Text = "pending settings test", Width = 250, Height = 180 })
            {
                form.Show();
                Application.DoEvents();
                var current = ConfigFactory.Current;
                current.WindowSizes.Add(new WindowSize { Name = Resizer.GetRealProcessName(form.Handle)!, Title = "*",
                    AutoResize = true, AutoResizeDelay = 10000 });
                runtime.SetEnableAutoResizeDelay(true);
                ConfigFactory.Save();
                var coordinator = (AutoResizeCoordinator)Private(runtime, "_autoResizeCoordinator");
                coordinator.Schedule(form.Handle);
                var generation = (long)Private(coordinator, "_generation");
                Assert(((IDictionary)Private(coordinator, "_pending")).Count == 1, "Pending probe did not start.");
                foreach (var option in options)
                {
                    option.Set(!option.Get());
                    Assert((long)Private(coordinator, "_generation") == generation && ((IDictionary)Private(coordinator, "_pending")).Count == 1,
                        "Ordinary option Save cancelled pending: " + option.Field);
                }
                Assert(runtime.CurrentAutoResizeSnapshot!.EnableResizeByTitle == runtime.GetEnableResizeByTitle() &&
                    runtime.CurrentAutoResizeSnapshot.EnableAutoResizeDelay == runtime.GetEnableAutoResizeDelay(), "Option snapshots stale.");
                coordinator.CancelPending();
                form.Close();
            }
            runtime.SetThemePreference(ThemePreference.Dark);
            runtime.Dispose();
            ConfigFactory.UseDefault();
            ConfigFactory.Load();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions { EnableGlobalHotkeys = false, EnableWindowMonitoring = false });
            runtime.Start();
            Assert(runtime.GetThemePreference() == ThemePreference.Dark, "New runtime after restart did not restore Theme.");
            Assert(runtime.GetHotkeys().Single(row => row.Type == HotkeysType.Save).Hotkey == null, "Cleared Save not persistent after restart.");
            runtime.Dispose();
            runtime = null;
            TestImportHotkeyValidation(directory);
            Console.WriteLine($"PASS: Settings/Hotkeys/Theme runtime and import hotkey validation ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { runtime?.Dispose(); Directory.Delete(directory, true); }
    }

    private static void TestImportHotkeyValidation(string directory)
    {
        var activePath = ConfigFactory.ConfigPath;
        var importPath = Path.Combine(directory, "hotkey-import.json");
        ConfigFactory.UseDefault();
        ConfigFactory.Save();
        var baseline = Document();
        var profileName = (string)((JArray)baseline["Configs"]!)[0]!["ProfileName"]!;
        var registrar = new Registrar();
        var registrations = new Dictionary<HotkeysType, int>();
        var logs = new List<string>();
        using (var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
               {
                   EnableWindowMonitoring = false,
                   HotkeyRegistrar = registrar,
                   RegisteredHotkeys = registrations,
                   Log = logs.Add,
               }))
        {
            runtime.Start();
            var replacing = 0;
            var replaced = 0;
            var hotkeysChanged = 0;
            ConfigFactory.ConfigurationReplacingEvent onReplacing = () => replacing++;
            ConfigFactory.ConfigurationReplacedEvent onReplaced = () => replaced++;
            ConfigFactory.ConfigurationReplacing += onReplacing;
            ConfigFactory.ConfigurationReplaced += onReplaced;
            runtime.HotkeysChanged += (_, _) => hotkeysChanged++;
            try
            {
                JObject Profile(JObject document) => (JObject)((JArray)document["Configs"]!)[0]!;
                JObject Shortcut(string value) => new JObject
                {
                    ["ModifierKeys"] = new JArray("Ctrl"),
                    ["Key"] = value,
                };
                void Reject(JObject candidate, string location, string value)
                {
                    var diskBefore = File.ReadAllText(activePath);
                    var liveBefore = ConfigFactory.Current;
                    var currentPath = ConfigFactory.ConfigPath;
                    var registeredBefore = new HashSet<int>(registrar.Ids);
                    var assignmentsBefore = new Dictionary<HotkeysType, int>(registrations);
                    var attemptsBefore = registrar.Attempts.Count;
                    var releasesBefore = registrar.UnregisterAllCount;
                    var logsBefore = logs.Count;
                    var replacingBefore = replacing;
                    var replacedBefore = replaced;
                    var hotkeysBefore = hotkeysChanged;
                    File.WriteAllText(importPath, candidate.ToString());
                    var failure = Capture(() => ConfigFactory.Load(importPath));
                    Assert(failure is JsonSerializationException &&
                           failure.Message == $"Invalid hotkey key at {location}: \"{value}\".",
                        "Invalid Key did not produce a location-specific validation error: " + value + " (" + failure + ")");
                    Assert(File.ReadAllText(activePath) == diskBefore &&
                           ConfigFactory.ConfigPath == currentPath && ReferenceEquals(ConfigFactory.Current, liveBefore),
                        "Invalid Key changed disk, config path or live profile: " + value);
                    Assert(replacing == replacingBefore && replaced == replacedBefore && hotkeysChanged == hotkeysBefore,
                        "Invalid Key raised replacement/hotkey events: " + value);
                    Assert(registrar.UnregisterAllCount == releasesBefore && registrar.Attempts.Count == attemptsBefore &&
                           registrar.Ids.SetEquals(registeredBefore) && logs.Count == logsBefore &&
                           registrations.Count == assignmentsBefore.Count &&
                           assignmentsBefore.All(entry => registrations.TryGetValue(entry.Key, out var id) && id == entry.Value),
                        "Invalid Key changed registered hotkeys: " + value);
                }

                foreach (var badKey in new[] { "THIS_IS_NOT_A_KEY", "999999", "None", "Control", "ControlKey", "ShiftKey", "Menu", "LWin" })
                {
                    var candidate = (JObject)baseline.DeepClone();
                    Profile(candidate)["Keys"]!["Save"] = Shortcut(badKey);
                    Reject(candidate, $"Profiles[\"{profileName}\"].Keys.Save.Key", badKey);
                }

                foreach (var action in new[] { HotkeysType.Restore, HotkeysType.SaveAll, HotkeysType.RestoreAll })
                {
                    var candidate = (JObject)baseline.DeepClone();
                    ((JObject)Profile(candidate)["Keys"]!)[action.ToString()] = Shortcut("THIS_IS_NOT_A_KEY");
                    Reject(candidate, $"Profiles[\"{profileName}\"].Keys.{action}.Key", "THIS_IS_NOT_A_KEY");
                }

                var invalidPreset = (JObject)baseline.DeepClone();
                Profile(invalidPreset)["Presets"] = new JArray(new JObject
                {
                    ["Id"] = "invalid-key-preset",
                    ["Name"] = "Invalid key preset",
                    ["Enabled"] = true,
                    ["Hotkey"] = Shortcut("THIS_IS_NOT_A_KEY"),
                });
                Reject(invalidPreset, $"Profiles[\"{profileName}\"].Presets[\"Invalid key preset\"].Hotkey.Key", "THIS_IS_NOT_A_KEY");

                var legacyInvalid = new JObject
                {
                    ["WindowSizes"] = new JArray(),
                    ["SaveKey"] = Shortcut("THIS_IS_NOT_A_KEY"),
                };
                Reject(legacyInvalid, "Profiles[\"\"].SaveKey.Key", "THIS_IS_NOT_A_KEY");

                foreach (var goodKey in new[] { "A", "a", "65", "Enter", "Return", " A " })
                {
                    var candidate = (JObject)baseline.DeepClone();
                    Profile(candidate)["Keys"]!["Save"] = Shortcut(goodKey);
                    File.WriteAllText(importPath, candidate.ToString());
                    var beforeReplacing = replacing;
                    var beforeReplaced = replaced;
                    var beforeReload = registrar.UnregisterAllCount;
                    ConfigFactory.Load(importPath);
                    Assert(replacing == beforeReplacing + 1 && replaced == beforeReplaced + 1 &&
                           registrar.UnregisterAllCount == beforeReload + 1 && ConfigFactory.Current.Keys[HotkeysType.Save].Key == goodKey,
                        "Valid key import did not follow normal replacement flow: " + goodKey);
                    Assert((string?)JObject.Parse(File.ReadAllText(activePath))["Configs"]![0]!["Keys"]!["Save"]!["Key"] == goodKey,
                        "Valid key import was not persisted: " + goodKey);
                }

                var validPreset = (JObject)baseline.DeepClone();
                Profile(validPreset)["Presets"] = new JArray(new JObject
                {
                    ["Id"] = "valid-key-preset",
                    ["Name"] = "Valid key preset",
                    ["Enabled"] = true,
                    ["Hotkey"] = Shortcut("F5"),
                });
                File.WriteAllText(importPath, validPreset.ToString());
                ConfigFactory.Load(importPath);
                Assert(ConfigFactory.Current.Presets.Single().Hotkey!.Key == "F5" &&
                       registrar.Attempts.Any(attempt => attempt.Key == Keys.F5),
                    "Valid preset hotkey did not survive import/registration.");

                File.WriteAllText(importPath, baseline.ToString());
                ConfigFactory.Load(importPath);
                Assert(ConfigFactory.Current.Keys[HotkeysType.Save].Equals(Config.DefaultKeys[HotkeysType.Save]) &&
                       ConfigFactory.Current.Keys[HotkeysType.Restore].Equals(Config.DefaultKeys[HotkeysType.Restore]),
                    "Default hotkeys did not survive import.");

                var emptyShortcut = (JObject)baseline.DeepClone();
                Profile(emptyShortcut)["Keys"]!["Save"] = new JObject
                {
                    ["ModifierKeys"] = new JArray(),
                    ["Key"] = null,
                };
                File.WriteAllText(importPath, emptyShortcut.ToString());
                ConfigFactory.Load(importPath);
                Assert(!ConfigFactory.Current.Keys[HotkeysType.Save].IsValid(),
                    "Empty unassigned shortcut object was rejected.");
                File.WriteAllText(importPath, baseline.ToString());
                ConfigFactory.Load(importPath);

                Assert(Key("A").GetKey() == Keys.A && Key("a").GetKey() == Keys.A &&
                       Key("65").GetKey() == Keys.A && Key(" A ").GetKey() == Keys.A,
                    "Shared main-key parser changed valid letter/number behavior.");
                Assert(Key("Enter").GetKey() == Key("Return").GetKey(),
                    "Shared main-key parser lost enum alias behavior.");
                foreach (var badKey in new[] { "THIS_IS_NOT_A_KEY", "999999", "None", "Control", "ControlKey", "ShiftKey", "Menu", "LWin", "", "   " })
                    Assert(Capture(() => Key(badKey).GetKey()) is HotkeyNotValidException,
                        "Runtime GetKey accepted invalid main key: " + badKey);

                var invalidModifier = (JObject)baseline.DeepClone();
                Profile(invalidModifier)["Keys"]!["Save"] = new JObject
                {
                    ["ModifierKeys"] = new JArray("Bogus"),
                    ["Key"] = "S",
                };
                var diskBeforeModifier = File.ReadAllText(activePath);
                var liveBeforeModifier = ConfigFactory.Current;
                File.WriteAllText(importPath, invalidModifier.ToString());
                var modifierFailure = Capture(() => ConfigFactory.Load(importPath));
                Assert(modifierFailure is JsonSerializationException && modifierFailure.Message == "Invalid hotkey modifiers." &&
                       ReferenceEquals(liveBeforeModifier, ConfigFactory.Current) && File.ReadAllText(activePath) == diskBeforeModifier,
                    "Existing modifier validation changed.");
            }
            finally
            {
                ConfigFactory.ConfigurationReplacing -= onReplacing;
                ConfigFactory.ConfigurationReplaced -= onReplaced;
            }
        }
        var invalidStartup = (JObject)baseline.DeepClone();
        ((JObject)((JArray)invalidStartup["Configs"]!)[0]!["Keys"]!)["Save"]!["Key"] = "THIS_IS_NOT_A_KEY";
        var liveBeforeStartup = ConfigFactory.Current;
        File.WriteAllText(activePath, invalidStartup.ToString());
        var startupFailure = Capture(ConfigFactory.Load);
        Assert(startupFailure is JsonSerializationException && ReferenceEquals(ConfigFactory.Current, liveBeforeStartup),
            "Startup load no longer uses candidate validation or mutated live data after failure.");
        File.WriteAllText(activePath, baseline.ToString());
        ConfigFactory.Load();
    }

    private sealed class Registrar : IGlobalHotkeyRegistrar
    {
        public readonly HashSet<int> Ids = new HashSet<int>();
        public readonly List<int> Removed = new List<int>();
        public readonly List<(ModifierKeys Modifier, Keys Key)> Attempts = new List<(ModifierKeys, Keys)>();
        public int UnregisterAllCount { get; private set; }
        private int _next;
        public event EventHandler<KeyPressedEventArgs>? KeyPressed { add { } remove { } }
        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            Attempts.Add((modifier, key));
            var id = ++_next;
            Ids.Add(id);
            return id;
        }
        public void UnRegisterHotKey(int id) { Removed.Add(id); Ids.Remove(id); }
        public void UnRegisterHotKey() { UnregisterAllCount++; Ids.Clear(); }
        public void Dispose() => Ids.Clear();
    }
}
