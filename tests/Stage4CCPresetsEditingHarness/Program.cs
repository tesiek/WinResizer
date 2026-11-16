using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Views;
using WinResizer.Sorting;

namespace Stage4CCPresetsEditingHarness;

internal sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    private readonly Dictionary<int, (ModifierKeys Modifier, Keys Key)> _registered =
        new Dictionary<int, (ModifierKeys Modifier, Keys Key)>();
    private EventHandler<KeyPressedEventArgs>? _keyPressed;
    private int _nextId;

    public Keys? FailKey { get; set; }

    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

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
        RegisterCount++;
        return id;
    }

    public void UnRegisterHotKey(int id)
    {
        if (_registered.Remove(id))
        {
            UnregisterCount++;
        }
    }

    public void UnRegisterHotKey()
    {
        UnregisterCount += _registered.Count;
        _registered.Clear();
    }

    public bool Has(ModifierKeys modifier, Keys key) =>
        _registered.Values.Any(item => item.Modifier == modifier && item.Key == key);

    public void Dispose() => UnRegisterHotKey();
}

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-stage4cc-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        var saveFailure = false;
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
                Enabled = false,
                X = 10,
                Y = 20,
                Width = 1000,
                Height = 700,
                Hotkey = CreateHotkey("F20"),
            };
            var presetB = new WindowPreset
            {
                Name = "Preset 2",
                Enabled = true,
                X = 30,
                Y = 40,
                Width = 1200,
                Height = 800,
                Hotkey = CreateHotkey("F21"),
            };
            profileA.Presets.Add(presetA);
            profileA.Presets.Add(presetB);
            profileB.Presets.Add(new WindowPreset
            {
                Name = "B Preset",
                Enabled = true,
                Width = 900,
                Height = 600,
                Hotkey = CreateHotkey("F22"),
            });
            ConfigFactory.Save();

            var registrar = new FakeHotkeyRegistrar();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar,
                OwnsHotkeyRegistrar = false,
                EnableWindowMonitoring = false,
                SaveConfiguration = () =>
                {
                    if (saveFailure)
                    {
                        throw new IOException("Injected Save failure.");
                    }

                    ConfigFactory.Save();
                },
            });
            runtime.Start();
            viewModel = new PresetsViewModel(runtime, Dispatcher.CurrentDispatcher);

            var rowA = viewModel.Presets.Single(row => row.PresetId == presetA.Id);
            var rowB = viewModel.Presets.Single(row => row.PresetId == presetB.Id);
            var existingIds = new HashSet<string>(viewModel.Presets.Select(row => row.PresetId), StringComparer.Ordinal);

            var added = viewModel.AddPreset();
            DrainDispatcher();
            Assert(added.Name == "Preset 3" && added.Enabled && added.X == 0 && added.Y == 0 &&
                   added.Width == 1280 && added.Height == 720 && added.Hotkey is null,
                "New Preset did not use the production defaults.");
            Assert(!existingIds.Contains(added.PresetId) && viewModel.Presets.Any(row => row.PresetId == added.PresetId),
                "New Preset did not create a unique ID or refresh the WPF list.");

            var registerCountBeforeUpdate = registrar.RegisterCount;
            var unregisterCountBeforeUpdate = registrar.UnregisterCount;
            viewModel.UpdatePreset(rowA.ProfileId, rowA.PresetId, "Preset 2", 21, -11, 1111, 777);
            DrainDispatcher();
            var updatedA = runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == rowA.PresetId);
            Assert(updatedA.Name == "Preset 2" && updatedA.Y == 21 && updatedA.X == -11 &&
                   updatedA.Width == 1111 && updatedA.Height == 777,
                "Name/geometry update did not map to the runtime fields.");
            Assert(!updatedA.Enabled && updatedA.Hotkey?.Key == "F20",
                "Name/geometry update changed Enabled or Hotkey for an inactive preset.");
            Assert(registerCountBeforeUpdate == registrar.RegisterCount &&
                   unregisterCountBeforeUpdate == registrar.UnregisterCount,
                "Ordinary Update unexpectedly changed global hotkey registrations.");

            viewModel.UpdatePreset(rowB.ProfileId, rowB.PresetId, "Preset 2", 41, 31, 1210, 810);
            DrainDispatcher();
            var updatedB = runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == rowB.PresetId);
            Assert(updatedB.Name == "Preset 2" && updatedB.Enabled && updatedB.Hotkey?.Key == "F21",
                "Duplicate names were rejected or Active/Hotkey were changed.");
            Assert(registrar.Has(ModifierKeys.Ctrl, Keys.F21),
                "Active preset registration disappeared after ordinary geometry update.");

            var sortedView = new ListCollectionView(viewModel.Presets);
            sortedView.CustomSort = new PresetNameComparer();
            var sortedRowA = sortedView.Cast<PresetRowViewModel>().Single(row => row.PresetId == rowA.PresetId);
            viewModel.UpdatePreset(sortedRowA.ProfileId, sortedRowA.PresetId, "Preset 10", 22, -12, 1112, 778);
            DrainDispatcher();
            Assert(runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == rowA.PresetId).X == -12,
                "Sorted inline edit targeted the wrong PresetId.");

            var parseMethod = typeof(PresetsView).GetMethod(
                "TryParseInteger",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("TryParseInteger method was not found.");
            var parseArguments = new object[] { "abc", 0 };
            var parsed = (bool)parseMethod.Invoke(null, parseArguments)!;
            Assert(!parsed, "Invalid numeric input was accepted by the WPF parser.");
            Assert(runtime.GetPresets(profileA.ProfileId).Single(item => item.PresetId == rowA.PresetId).Width == 1112,
                "Invalid numeric input changed the backend value.");

            TestInlineEditors(viewModel, runtime, rowA);

            var activeRow = viewModel.Presets.Single(row => row.PresetId == rowB.PresetId);
            Assert(viewModel.RemovePreset(activeRow), "Remove did not remove the selected preset.");
            DrainDispatcher();
            Assert(!runtime.GetPresets(profileA.ProfileId).Any(item => item.PresetId == rowB.PresetId) &&
                   !viewModel.Presets.Any(row => row.PresetId == rowB.PresetId),
                "Remove did not remove the correct sorted PresetId.");
            Assert(!registrar.Has(ModifierKeys.Ctrl, Keys.F21),
                "Runtime did not unregister the removed active preset.");

            var addedRow = viewModel.Presets.Single(row => row.PresetId == added.PresetId);
            saveFailure = true;
            try
            {
                viewModel.RemovePreset(addedRow);
                throw new InvalidOperationException("Remove Save failure was accepted.");
            }
            catch (IOException)
            {
            }

            viewModel.Refresh();
            DrainDispatcher();
            Assert(runtime.GetPresets(profileA.ProfileId).Any(item => item.PresetId == added.PresetId) &&
                   viewModel.Presets.Any(row => row.PresetId == added.PresetId),
                "Remove failure did not preserve the preset and UI row.");
            saveFailure = false;

            Assert(runtime.SwitchProfile(profileB.ProfileId), "Profile switch to B failed.");
            DrainDispatcher();
            Assert(viewModel.Presets.Count == 1 && viewModel.Presets[0].ProfileId == profileB.ProfileId,
                "Profile switch did not refresh the correct Presets list.");
            Assert(Task.Run(() => runtime.SwitchProfile(profileA.ProfileId)).GetAwaiter().GetResult(),
                "External/tray-style profile switch to A failed.");
            DrainDispatcher();
            Assert(viewModel.Presets.All(row => row.ProfileId == profileA.ProfileId),
                "External/tray-style profile switch did not refresh Presets.");

            Console.WriteLine(
                "PASS: Add defaults/identity, duplicate names, sorted ID-safe inline Name/geometry updates, " +
                "invalid numeric rejection, Enabled/Hotkey preservation, registration stability, Remove rollback, " +
                "active registration cleanup, profile synchronization, and deferred refresh contract.");
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

    private static void TestInlineEditors(
        PresetsViewModel viewModel,
        WinResizerRuntime runtime,
        PresetRowViewModel row)
    {
        // Load the production styles without starting the production App lifetime/tray.
        var application = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
        };
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = Wpf.Ui.Appearance.ApplicationTheme.Light,
        });
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
        {
            application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml"),
            });
        }
        var presetsView = new PresetsView { DataContext = viewModel };
        var window = new System.Windows.Window
        {
            Content = presetsView,
            Width = 1200,
            Height = 500,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0,
        };
        try
        {
            window.Show();
            DrainDispatcher();
            var grid = (System.Windows.Controls.DataGrid)presetsView.FindName("PresetsGrid");
            foreach (var column in grid.Columns.Where(column =>
                         column.Header is string header &&
                         new[] { "Preset Name", "Top", "Left", "Width", "Height" }.Contains(header)))
            {
                Assert(!grid.IsReadOnly && !column.IsReadOnly,
                    $"The real WPF column '{column.Header}' is read-only.");
            }

            System.Windows.Controls.TextBox? editor = null;
            grid.PreparingCellForEdit += (_, args) =>
            {
                var presenter = (System.Windows.Controls.ContentPresenter)args.EditingElement;
                presenter.ApplyTemplate();
                editor = presenter.ContentTemplate.FindName("PresetEditor", presenter)
                    as System.Windows.Controls.TextBox;
            };

            void Edit(string header, string text, bool commitRow = true, bool cancel = false)
            {
                editor = null;
                var column = grid.Columns.Single(item => Equals(item.Header, header));
                grid.ScrollIntoView(row, column);
                grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(row, column);
                grid.UpdateLayout();
                Assert(grid.BeginEdit(), $"BeginEdit failed for '{header}'.");
                Assert(editor != null, $"No text editor was created for '{header}'.");
                editor!.Text = text;
                Assert(cancel
                    ? grid.CancelEdit(System.Windows.Controls.DataGridEditingUnit.Cell)
                    : grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true),
                    $"Finishing the cell edit failed for '{header}'.");
                DrainDispatcher();
                if (commitRow)
                {
                    Assert(grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true),
                        $"Finishing the row edit failed for '{header}'.");
                    DrainDispatcher();
                }
            }

            RuntimePresetInfo Current() => runtime.GetPresets(row.ProfileId)
                .Single(item => item.PresetId == row.PresetId);

            var collectionView = (ListCollectionView)CollectionViewSource.GetDefaultView(viewModel.Presets);
            collectionView.CustomSort = new PresetNameComparer();
            Edit("Preset Name", "Inline 20", commitRow: false);
            Assert(Current().Name == "Inline 20" && row.Name == "Inline 20",
                "Inline Name did not reach the runtime and displayed row during EditItem.");
            Edit("Top", "123");
            Assert(Current().Name == "Inline 20" && Current().Y == 123,
                "Editing the next cell lost the previous committed value or targeted the wrong preset.");
            Edit("Left", "-234");
            Edit("Width", "1300");
            Edit("Height", "850");
            Assert(Current().X == -234 && Current().Y == 123 &&
                   Current().Width == 1300 && Current().Height == 850,
                "The real inline geometry edits were not persisted correctly.");
            Edit("Width", "abc");
            Edit("Height", "2147483648");
            Assert(Current().Width == 1300 && Current().Height == 850,
                "Invalid/overflow numeric input reached the runtime.");
            Edit("Preset Name", "Cancelled", cancel: true);
            Assert(Current().Name == "Inline 20", "Escape/cancel committed an edit.");
            Assert(!Current().Enabled && Current().Hotkey?.Key == "F20",
                "Real inline edits changed Active or Hotkey.");
            Assert(!collectionView.IsEditingItem && row.Name == "Inline 20" &&
                   row.Top == 123 && row.Left == -234 && row.Width == 1300 && row.Height == 850,
                "Deferred refresh did not reconcile the completed real WPF edit transaction.");
            Console.WriteLine("PASS: real styled WPF BeginEdit/commit for all five columns, consecutive cells, " +
                "sorted identity, invalid/overflow input, cancel, and edit-transaction-safe refresh.");
        }
        finally
        {
            window.Close();
        }
    }

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

    private sealed class PresetNameComparer : IComparer
    {
        public int Compare(object? x, object? y) => NaturalStringComparer.Instance.Compare(
            (x as PresetRowViewModel)?.Name,
            (y as PresetRowViewModel)?.Name);
    }
}
