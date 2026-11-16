using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Threading;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Runtime;
using WinResizer.Presentation;

internal static class Program
{
    private static int _assertions;
    private static Hotkeys Key(string key, params string[] modifiers) => new()
    {
        Key = key, ModifierKeys = new HashSet<string>(modifiers),
    };

    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (HotkeyNotAllowedException exception)
        {
            Assert(HotkeyNotAllowedException.Title == "Hotkey Not Allowed", "Incorrect policy title.");
            Assert(exception.Message == "Ctrl+Alt shortcuts without the Windows key can conflict with AltGr and prevent typing national characters.\nAdd the Windows key or choose another shortcut.", "Incorrect policy message.");
            return;
        }
        throw new InvalidOperationException("Unsafe assignment was accepted.");
    }

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-assignment-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rejected = new[]
            {
                Key("S", "Ctrl", "Alt"), Key("S", "Ctrl", "Alt", "Shift"),
                Key("D1", "Ctrl", "Alt"), Key("F1", "Ctrl", "Alt", "Shift"),
                Key("OemComma", "ctrl", "ALT"),
            };
            var allowed = new[]
            {
                Key("S", "Win", "Ctrl", "Alt"), Key("S", "Win", "Ctrl", "Alt", "Shift"),
                Key("S", "Ctrl", "Shift"), Key("S", "Alt", "Shift"),
                Key("S", "Win", "Alt"), Key("S", "Win", "Ctrl"),
            };
            foreach (var key in rejected) Reject(() => HotkeyAssignmentPolicy.Validate(key));
            foreach (var key in allowed) { HotkeyAssignmentPolicy.Validate(key); Assert(true, "Allowed policy."); }
            HotkeyAssignmentPolicy.Validate(null);

            ConfigFactory.SetPortablePath(Path.Combine(root, "WinResizer.config.json"));
            ConfigFactory.UseDefault();
            var a = ConfigFactory.Current;
            a.SetKeys(HotkeysType.Restore, Key("R", "Ctrl", "Alt"));
            var preset = new WindowPreset { Name = "Legacy", Hotkey = Key("P", "Ctrl", "Alt"), Enabled = true };
            a.Presets.Add(preset);
            var b = ConfigFactory.ProfileAdd("B");
            b.SetKeys(HotkeysType.Save, Key("F9", "Ctrl", "Alt"));
            b.Presets.Add(new WindowPreset { Name = "Legacy B", Hotkey = Key("F10", "Ctrl", "Alt"), Enabled = true });
            ConfigFactory.Save();
            var registrar = new Registrar();
            var saves = 0;
            using var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar, OwnsHotkeyRegistrar = false, EnableWindowMonitoring = false,
                SaveConfiguration = () => { saves++; ConfigFactory.Save(); },
            });
            runtime.Start();
            Assert(registrar.Has(ModifierKeys.Ctrl | ModifierKeys.Alt, Keys.R), "Legacy global startup changed.");
            Assert(registrar.Has(ModifierKeys.Ctrl | ModifierKeys.Alt, Keys.P), "Legacy preset startup changed.");
            using var globals = new HotkeysViewModel(runtime, Dispatcher.CurrentDispatcher);
            using var presets = new PresetsViewModel(runtime, Dispatcher.CurrentDispatcher);
            var inactivePreset = runtime.AddPreset(b.ProfileId);
            var events = 0;
            runtime.HotkeysChanged += (_, _) => events++;
            runtime.PresetsChanged += (_, _) => events++;
            foreach (var action in Enum.GetValues(typeof(HotkeysType)).Cast<HotkeysType>())
            {
                foreach (var key in rejected)
                {
                    var old = a.GetKeys(action)?.ToKeysString();
                    var display = globals.Hotkeys.Single(row => row.ActionId == action).CurrentKey;
                    var calls = registrar.RegisterCalls;
                    var oldSaves = saves;
                    var oldEvents = events;
                    var registrations = registrar.Active.Count;
                    var disk = File.ReadAllText(ConfigFactory.ConfigPath);
                    Reject(() => globals.SetHotkey(globals.Hotkeys.Single(row => row.ActionId == action), key));
                    globals.Refresh();
                    Assert(a.GetKeys(action)?.ToKeysString() == old, "Global config changed.");
                    Assert(globals.Hotkeys.Single(row => row.ActionId == action).CurrentKey == display, "Global WPF display changed.");
                    Assert(registrar.RegisterCalls == calls && registrar.Active.Count == registrations, "Rejected global registered.");
                    Assert(saves == oldSaves && events == oldEvents && File.ReadAllText(ConfigFactory.ConfigPath) == disk, "Rejected global saved/emitted events.");
                }
            }
            foreach (var initial in new Hotkeys?[] { preset.Hotkey, null })
            {
                runtime.SetPresetHotkey(a.ProfileId, preset.Id, null);
                // Install legacy directly to test preservation, without creating it via a setter.
                preset.Hotkey = initial;
                ConfigFactory.Save();
                presets.Refresh();
                foreach (var key in rejected)
                {
                    var calls = registrar.RegisterCalls;
                    var oldSaves = saves;
                    var oldEvents = events;
                    var old = preset.Hotkey?.ToKeysString();
                    var registrations = registrar.Active.Count;
                    var disk = File.ReadAllText(ConfigFactory.ConfigPath);
                    var display = presets.Presets.Single(row => row.PresetId == preset.Id).Hotkey;
                    Reject(() => presets.SetPresetHotkey(presets.Presets.Single(row => row.PresetId == preset.Id), key));
                    presets.Refresh();
                    Assert(preset.Hotkey?.ToKeysString() == old, "Preset config changed.");
                    Assert(presets.Presets.Single(row => row.PresetId == preset.Id).Hotkey == display, "Preset WPF display changed.");
                    Assert(registrar.RegisterCalls == calls && registrar.Active.Count == registrations && saves == oldSaves && events == oldEvents && File.ReadAllText(ConfigFactory.ConfigPath) == disk, "Rejected preset registered/saved/emitted events.");
                    Reject(() => runtime.SetPresetHotkey(b.ProfileId, inactivePreset.PresetId, key));
                    Assert(runtime.GetPresets(b.ProfileId).Single(row => row.PresetId == inactivePreset.PresetId).Hotkey is null && saves == oldSaves, "Inactive preset was mutated/saved.");
                }
            }
            runtime.SetHotkey(HotkeysType.Save, null);
            foreach (var key in allowed)
            {
                runtime.SetHotkey(HotkeysType.SaveAll, key);
                Assert(a.GetKeys(HotkeysType.SaveAll)!.Equals(key), "Allowed global rejected.");
                runtime.SetHotkey(HotkeysType.SaveAll, null);
                runtime.SetPresetHotkey(a.ProfileId, preset.Id, key);
                Assert(preset.Hotkey!.Equals(key), "Allowed preset rejected.");
                runtime.SetPresetHotkey(a.ProfileId, preset.Id, null);
            }
            Assert(runtime.SwitchProfile(b.ProfileId), "Switch failed.");
            Assert(registrar.Has(ModifierKeys.Ctrl | ModifierKeys.Alt, Keys.F9), "Legacy switch changed.");
            Assert(registrar.Has(ModifierKeys.Ctrl | ModifierKeys.Alt, Keys.F10), "Legacy preset switch changed.");
            var import = Path.Combine(root, "import.json");
            ConfigFactory.Export(import);
            ConfigFactory.Load(import);
            Assert(registrar.Has(ModifierKeys.Ctrl | ModifierKeys.Alt, Keys.F9), "Legacy import changed.");
            Assert(registrar.Has(ModifierKeys.Ctrl | ModifierKeys.Alt, Keys.F10), "Legacy preset import changed.");
            Assert(ConfigFactory.Current.GetKeys(HotkeysType.Save)!.ToKeysString() == "Ctrl+Alt+F9", "Legacy data changed.");
            Console.WriteLine($"PASS: {_assertions} assertions; assignment policy, both Runtime/WPF models, no registration/save, Clear and legacy startup/switch/import.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Registrar : IGlobalHotkeyRegistrar
    {
        private int _id;
        public int RegisterCalls { get; private set; }
        public Dictionary<int, (ModifierKeys Modifier, Keys Key)> Active { get; } = new();
        public event EventHandler<KeyPressedEventArgs>? KeyPressed { add { } remove { } }
        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            RegisterCalls++;
            Active.Add(++_id, (modifier, key));
            return _id;
        }
        public bool Has(ModifierKeys modifier, Keys key) => Active.Values.Any(value => value.Modifier == modifier && value.Key == key);
        public void UnRegisterHotKey(int id) => Active.Remove(id);
        public void UnRegisterHotKey() => Active.Clear();
        public void Dispose() => Active.Clear();
    }
}
