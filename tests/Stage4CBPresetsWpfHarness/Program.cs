using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Runtime;
using WinResizer.Presentation;

namespace Stage4CBPresetsWpfHarness;

internal sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    private readonly Dictionary<int, (ModifierKeys Modifier, Keys Key)> _registered =
        new Dictionary<int, (ModifierKeys Modifier, Keys Key)>();
    private EventHandler<KeyPressedEventArgs>? _keyPressed;
    private int _nextId;

    public Keys? FailKey { get; set; }

    public event EventHandler<KeyPressedEventArgs>? KeyPressed
    {
        add => _keyPressed += value;
        remove => _keyPressed -= value;
    }

    public int RegisterHotKey(ModifierKeys modifier, Keys key)
    {
        if (FailKey == key)
        {
            throw new HotkeyHookException("Injected registration failure.");
        }

        if (_registered.Values.Any(item => item.Modifier == modifier && item.Key == key))
        {
            throw new HotkeyHookException("The hotkey is already registered.");
        }

        var id = ++_nextId;
        _registered.Add(id, (modifier, key));
        return id;
    }

    public void UnRegisterHotKey(int id) => _registered.Remove(id);

    public void UnRegisterHotKey() => _registered.Clear();

    public bool Has(ModifierKeys modifier, Keys key) =>
        _registered.Values.Any(item => item.Modifier == modifier && item.Key == key);

    public void Dispose() => _registered.Clear();
}

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-stage4cb-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        WinResizerRuntime? runtime = null;
        PresetsViewModel? viewModel = null;
        try
        {
            var configPath = Path.Combine(temporaryRoot, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(configPath);
            ConfigFactory.UseDefault();
            var profileA = ConfigFactory.Current;
            var profileB = ConfigFactory.ProfileAdd("Profile B");

            var presetA = new WindowPreset
            {
                Name = "Preset 10",
                Enabled = true,
                X = 11,
                Y = 22,
                Width = 1100,
                Height = 700,
                Hotkey = CreateHotkey("F13"),
            };
            var presetB = new WindowPreset
            {
                Name = "Preset 2",
                Enabled = false,
                X = -8,
                Y = 5,
                Width = 900,
                Height = 600,
                Hotkey = CreateHotkey("F14"),
            };
            profileA.Presets.Add(presetA);
            profileA.Presets.Add(presetB);
            profileB.Presets.Add(new WindowPreset
            {
                Name = "Profile B Preset",
                Enabled = true,
                X = 30,
                Y = 40,
                Width = 800,
                Height = 500,
                Hotkey = CreateHotkey("F15"),
            });
            ConfigFactory.Save();

            var registrar = new FakeHotkeyRegistrar();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar,
                OwnsHotkeyRegistrar = false,
                EnableWindowMonitoring = false,
            });
            runtime.Start();

            viewModel = new PresetsViewModel(runtime, Dispatcher.CurrentDispatcher);
            Assert(viewModel.Presets.Count == 2, "WPF did not load the real active-profile presets.");
            var rowA = viewModel.Presets.Single(row => row.PresetId == presetA.Id);
            var rowB = viewModel.Presets.Single(row => row.PresetId == presetB.Id);
            Assert(rowA.ProfileId == profileA.ProfileId && rowB.ProfileId == profileA.ProfileId,
                "Rows did not retain the active ProfileId.");
            Assert(rowA.Top == 22 && rowA.Left == 11 && rowA.Width == 1100 && rowA.Height == 700,
                "Top/Left geometry mapping from RuntimePresetInfo is incorrect.");
            Assert(rowA.Hotkey == "Ctrl+F13", "Runtime hotkey display is not canonical.");
            Assert(rowA.Enabled && !rowB.Enabled, "Active state was not loaded from runtime.");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F13), "Enabled preset hotkey was not registered.");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F14), "Disabled preset hotkey was registered.");

            viewModel.SetPresetEnabled(rowA, false);
            DrainDispatcher();
            Assert(!rowA.Enabled && rowA.Hotkey == "Ctrl+F13", "Active OFF did not preserve the hotkey display.");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F13), "Active OFF did not unregister the preset hotkey.");

            viewModel.SetPresetHotkey(rowA, CreateHotkey("F16"));
            DrainDispatcher();
            Assert(!rowA.Enabled && rowA.Hotkey == "Ctrl+F16", "Set Key changed Active or display while OFF.");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F16), "Set Key registered a disabled preset.");

            viewModel.SetPresetHotkey(rowA, null);
            DrainDispatcher();
            Assert(!rowA.Enabled && rowA.Hotkey == "-", "Clear changed Active or did not show '-'.");

            viewModel.SetPresetHotkey(rowA, CreateHotkey("F17"));
            viewModel.SetPresetEnabled(rowA, true);
            DrainDispatcher();
            Assert(rowA.Enabled && rowA.Hotkey == "Ctrl+F17", "Set Key/Active ON sequence is incorrect.");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F17), "Active ON did not register the preserved new hotkey.");

            var sortedRows = viewModel.Presets
                .OrderBy(row => row.Name, StringComparer.Ordinal)
                .ToList();
            var sortedRowB = sortedRows.Single(row => row.PresetId == presetB.Id);
            viewModel.SetPresetHotkey(sortedRowB, CreateHotkey("F18"));
            DrainDispatcher();
            Assert(ConfigFactory.Current.Presets.Single(item => item.Id == presetB.Id).Hotkey?.Key == "F18",
                "Set Key after sorting changed the wrong preset.");
            Assert(ConfigFactory.Current.Presets.Single(item => item.Id == presetA.Id).Hotkey?.Key == "F17",
                "Set Key after sorting changed the identity of another preset.");

            var countBeforeAdd = viewModel.Presets.Count;
            runtime.AddPreset(profileA.ProfileId, "Runtime Added");
            DrainDispatcher();
            Assert(viewModel.Presets.Count == countBeforeAdd + 1,
                "PresetsChanged did not synchronize the WPF list.");

            Assert(runtime.SwitchProfile(profileB.ProfileId), "Profile switch to B failed.");
            DrainDispatcher();
            Assert(viewModel.Presets.Count == 1 && viewModel.Presets[0].ProfileId == profileB.ProfileId,
                "WPF did not refresh to the newly active profile.");
            Assert(viewModel.Presets[0].Name == "Profile B Preset", "Profile B preset data is incorrect.");

            Assert(Task.Run(() => runtime.SwitchProfile(profileA.ProfileId)).GetAwaiter().GetResult(),
                "External/tray-style profile switch to A failed.");
            DrainDispatcher();
            Assert(viewModel.Presets.Count == countBeforeAdd + 1 &&
                   viewModel.Presets.All(row => row.ProfileId == profileA.ProfileId),
                "External/tray-style profile switch did not refresh Presets.");

            var currentRowA = viewModel.Presets.Single(row => row.PresetId == presetA.Id);
            var previousHotkey = currentRowA.Hotkey;
            registrar.FailKey = Keys.F19;
            try
            {
                viewModel.SetPresetHotkey(currentRowA, CreateHotkey("F19"));
                throw new InvalidOperationException("Runtime registration failure was not propagated.");
            }
            catch (HotkeyHookException)
            {
            }

            viewModel.Refresh();
            DrainDispatcher();
            Assert(currentRowA.Hotkey == previousHotkey && currentRowA.Enabled,
                "Runtime failure did not restore the real WPF state.");
            Assert(ConfigFactory.Current.Presets.Single(item => item.Id == presetA.Id).Hotkey?.Key == "F17",
                "Runtime failure changed the production preset model.");

            Console.WriteLine(
                "PASS: real WPF Presets mapping, ProfileId/PresetId identity, Active transactions, Set Key/Clear, " +
                "PresetsChanged/profile synchronization, sorting-safe operations, and runtime rollback.");
            return 0;
        }
        finally
        {
            viewModel?.Dispose();
            runtime?.Dispose();
            try
            {
                Directory.Delete(temporaryRoot, true);
            }
            catch
            {
            }
        }
    }

    private static Hotkeys CreateHotkey(string key) => new Hotkeys
    {
        ModifierKeys = new HashSet<string> { "Ctrl" },
        Key = key,
    };

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
