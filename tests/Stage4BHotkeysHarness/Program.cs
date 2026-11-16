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
using WinResizer.Services;
using WinResizer.Sorting;

namespace Stage4BHotkeysHarness;

internal static class Program
{
    private static int _assertions;
    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-stage4b-hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        WinResizerRuntime? runtime = null;
        HotkeysViewModel? viewModel = null;
        try
        {
            var configPath = Path.Combine(temporaryRoot, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(configPath);
            ConfigFactory.UseDefault();
            var profileA = ConfigFactory.Current;
            ConfigFactory.ProfileRename(profileA.ProfileId, "Profile A");
            profileA.SetKeys(HotkeysType.Save, CreateHotkey("S", "Ctrl", "Shift"));
            profileA.SetKeys(HotkeysType.Restore, CreateHotkey("R", "Ctrl", "Shift"));

            var profileB = ConfigFactory.ProfileAdd("Profile B");
            profileB.SetKeys(HotkeysType.Save, CreateHotkey("F1", "Ctrl", "Alt"));
            profileB.SetKeys(HotkeysType.Restore, CreateHotkey("F2", "Ctrl", "Alt"));
            profileB.SetKeys(HotkeysType.SaveAll, CreateHotkey("F3", "Ctrl", "Alt"));
            ConfigFactory.Save();

            var registrar = new FakeHotkeyRegistrar();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar,
                OwnsHotkeyRegistrar = false,
                EnableWindowMonitoring = false,
            });
            runtime.Start();

            viewModel = new HotkeysViewModel(runtime, Dispatcher.CurrentDispatcher);
            Assert(viewModel.Hotkeys.Count == 4, "Presentation did not show the complete HotkeysType catalog.");
            var saveRow = Find(viewModel, HotkeysType.Save);
            var restoreRow = Find(viewModel, HotkeysType.Restore);
            var saveAllRow = Find(viewModel, HotkeysType.SaveAll);
            var restoreAllRow = Find(viewModel, HotkeysType.RestoreAll);
            Assert(saveRow.Action == "Save Active Window" &&
                   restoreRow.Action == "Restore Active Window" &&
                   saveAllRow.Action == "Save All Windows" &&
                   restoreAllRow.Action == "Restore All Windows",
                "WPF Hotkeys action labels do not use the WinResizer terminology.");
            Assert(saveRow.CurrentKey == "Ctrl+Shift+S", "Active Profile A Save hotkey is incorrect.");
            Assert(restoreRow.CurrentKey == "Ctrl+Shift+R", "Active Profile A Restore hotkey is incorrect.");
            Assert(saveAllRow.CurrentKey == "-" && restoreAllRow.CurrentKey == "-",
                "Unassigned actions were not visible as unassigned rows.");
            Assert(registrar.RegisterCount == 2, "Unassigned actions were registered as global hotkeys.");
            Assert(SubscriptionCount(runtime, viewModel) == 1, "Hotkeys presentation subscribed more than once.");

            Assert(runtime.SwitchProfile(profileB.ProfileId), "Runtime switch to Profile B failed.");
            DrainDispatcher();
            Assert(Find(viewModel, HotkeysType.Save).CurrentKey == "Ctrl+Alt+F1",
                "WPF did not show Profile B Save hotkey after a profile switch.");
            Assert(Find(viewModel, HotkeysType.SaveAll).CurrentKey == "Ctrl+Alt+F3",
                "WPF did not show Profile B Save All hotkey after a profile switch.");
            Assert(Find(viewModel, HotkeysType.RestoreAll).CurrentKey == "-",
                "Profile B Restore All should remain unassigned.");
            Assert(registrar.RegisterCount == 5, "Profile switch did not reload exactly the assigned B hotkeys.");

            SwitchExternal(runtime, profileA.ProfileId);
            DrainDispatcher();
            Assert(Find(viewModel, HotkeysType.Save).CurrentKey == "Ctrl+Shift+S",
                "External/tray-style switch did not refresh WPF Hotkeys.");
            Assert(registrar.RegisterCount == 7, "External switch did not reload Profile A hotkeys.");

            var sorted = new ListCollectionView(viewModel.Hotkeys);
            sorted.CustomSort = new HotkeyActionComparer();
            var sortedSaveAllRow = sorted.Cast<HotkeyRowViewModel>().Single(row => row.ActionId == HotkeysType.SaveAll);
            var saveAllReplacement = CreateHotkey("F4", "Ctrl", "Shift");
            viewModel.SetHotkey(sortedSaveAllRow, saveAllReplacement);
            DrainDispatcher();
            Assert(ConfigFactory.Current.GetKeys(HotkeysType.SaveAll)?.Equals(saveAllReplacement) == true,
                "Set Key did not update the action selected after sorting.");
            Assert(Find(viewModel, HotkeysType.Save).CurrentKey == "Ctrl+Shift+S",
                "Set Key after sorting changed the wrong action.");
            Assert(Find(viewModel, HotkeysType.SaveAll).CurrentKey == "Ctrl+Shift+F4",
                "Set Key did not refresh the changed action display.");
            Assert(registrar.RegisterCount == 8,
                "Set Key did not register an unassigned action exactly once.");

            var saveReplacement = CreateHotkey("F5", "Ctrl", "Shift");
            viewModel.SetHotkey(
                sorted.Cast<HotkeyRowViewModel>().Single(row => row.ActionId == HotkeysType.Save),
                saveReplacement);
            DrainDispatcher();
            Assert(ConfigFactory.Current.GetKeys(HotkeysType.Save)?.Equals(saveReplacement) == true,
                "Set Key did not update an already registered action.");
            Assert(registrar.RegisterCount == 9 && registrar.UnregisterCount >= 1,
                "Set Key did not replace the old registered combination exactly once.");

            var registrationCountBeforeConflict = registrar.RegisterCount;
            var currentRestoreAll = ConfigFactory.Current.GetKeys(HotkeysType.RestoreAll);
            try
            {
                viewModel.SetHotkey(
                    Find(viewModel, HotkeysType.RestoreAll),
                    CreateHotkey("F4", "Ctrl", "Shift"));
                throw new InvalidOperationException("Conflicting hotkey was accepted.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("already used by", StringComparison.Ordinal))
            {
            }

            DrainDispatcher();
            Assert(ConfigFactory.Current.GetKeys(HotkeysType.RestoreAll) == currentRestoreAll,
                "Conflict changed the unassigned action in the production config.");
            Assert(Find(viewModel, HotkeysType.RestoreAll).CurrentKey == "-",
                "Conflict did not restore the real unassigned UI state.");
            Assert(registrar.RegisterCount == registrationCountBeforeConflict,
                "Conflict changed active global registrations.");

            var previousSave = ConfigFactory.Current.GetKeys(HotkeysType.Save);
            var registrationCountBeforeRegistrarFailure = registrar.RegisterCount;
            registrar.FailNextRegistration = true;
            try
            {
                viewModel.SetHotkey(
                    Find(viewModel, HotkeysType.Save),
                    CreateHotkey("F6", "Ctrl", "Shift"));
                throw new InvalidOperationException("Registrar failure was not propagated.");
            }
            catch (HotkeyHookException)
            {
            }

            DrainDispatcher();
            Assert(ConfigFactory.Current.GetKeys(HotkeysType.Save)?.Equals(previousSave) == true,
                "Registrar failure changed the production config.");
            Assert(Find(viewModel, HotkeysType.Save).CurrentKey == "Ctrl+Shift+F5",
                "Registrar failure did not restore the real UI state.");
            Assert(registrar.RegisterCount == registrationCountBeforeRegistrarFailure,
                "Registrar failure changed active registrations.");

            ConfigFactory.Load();
            DrainDispatcher();
            Assert(Find(viewModel, HotkeysType.SaveAll).CurrentKey == "Ctrl+Shift+F4",
                "Configuration round-trip lost the changed hotkey.");
            Assert(SubscriptionCount(runtime, viewModel) == 1,
                "Configuration refresh accumulated Hotkeys subscriptions.");

            TestOptionsAndClear(runtime, configPath);

            var createdWindows = 0;
            var windowController = new SettingsWindowController(() =>
            {
                createdWindows++;
                return new FakeHotkeysWindow(runtime);
            });
            windowController.Show();
            windowController.Hide();
            windowController.Show();
            Assert(createdWindows == 1, "Show/hide/show created more than one Hotkeys window.");
            Assert(PresentationSubscriptionCount(runtime) == 2,
                "Show/hide/show accumulated Hotkeys subscriptions.");
            windowController.CloseForApplicationExit();
            Assert(PresentationSubscriptionCount(runtime) == 1,
                "Closing Settings did not dispose its Hotkeys presentation.");

            viewModel.Dispose();
            Assert(SubscriptionCount(runtime, viewModel) == 0, "Dispose did not detach HotkeysChanged.");
            var rowsAfterDispose = viewModel.Hotkeys.Count;
            runtime.SetHotkey(HotkeysType.Save, CreateHotkey("F6", "Ctrl", "Shift"));
            DrainDispatcher();
            Assert(viewModel.Hotkeys.Count == rowsAfterDispose,
                "Disposed Hotkeys presentation still received runtime events.");

            Console.WriteLine(
                "PASS: real per-profile hotkeys, complete action catalog, unassigned rows, ID-safe Set Key after sorting, " +
                "profile/external synchronization, production conflict rollback, registrar rollback, persistence, " +
                $"subscription stability, options/Clear UI, and clean disposal ({_assertions} assertions).");
            return 0;
        }
        finally
        {
            viewModel?.Dispose();
            runtime?.Dispose();
            System.Windows.Application.Current?.Shutdown();
            try
            {
                Directory.Delete(temporaryRoot, true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void SwitchExternal(WinResizerRuntime runtime, string profileId)
    {
        Task.Run(() => runtime.SwitchProfile(profileId)).GetAwaiter().GetResult();
    }

    private static Hotkeys CreateHotkey(string key, params string[] modifiers) => new Hotkeys
    {
        ModifierKeys = new HashSet<string>(modifiers),
        Key = key,
    };

    private static HotkeyRowViewModel Find(HotkeysViewModel viewModel, HotkeysType type) =>
        viewModel.Hotkeys.Single(row => row.ActionId == type);

    private static int SubscriptionCount(WinResizerRuntime runtime, HotkeysViewModel viewModel) =>
        GetHotkeysChangedHandlers(runtime)?.GetInvocationList()
            .Count(handler => ReferenceEquals(handler.Target, viewModel)) ?? 0;

    private static int PresentationSubscriptionCount(WinResizerRuntime runtime) =>
        GetHotkeysChangedHandlers(runtime)?.GetInvocationList()
            .Count(handler => handler.Target is HotkeysViewModel) ?? 0;

    private static Delegate? GetHotkeysChangedHandlers(WinResizerRuntime runtime)
    {
        var field = typeof(WinResizerRuntime).GetField("HotkeysChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HotkeysChanged backing field not found.");
        return field.GetValue(runtime) as Delegate;
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
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void TestOptionsAndClear(WinResizerRuntime runtime, string path)
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml"),
            });
        var window = new WinResizer.SettingsPrototypeWindow(runtime) { ShowInTaskbar = false, Opacity = 0 };
        var saves = 0;
        ConfigFactory.ConfigurationSavedEvent onSave = () => saves++;
        ConfigFactory.ConfigurationSaved += onSave;
        try
        {
            window.Show();
            DrainDispatcher();
            var model = window.ProfileOptions;
            var tabs = (System.Windows.Controls.TabControl)window.FindName("SettingsTabs");
            var hotkeys = (System.Windows.Controls.UserControl)window.FindName("HotkeysPage");
            Assert(tabs.Items.Cast<System.Windows.Controls.TabItem>().Any(tab => Equals(tab.Header, "Settings")), "Settings tab is missing.");
            var processesTab = tabs.Items.Cast<System.Windows.Controls.TabItem>().Single(tab => Equals(tab.Header, "Processes"));
            var processes = (System.Windows.Controls.UserControl)processesTab.Content;
            Assert(processes.DataContext is ProcessesViewModel, "Processes table ViewModel was replaced by profile options.");
            Assert(tabs.Items.Cast<System.Windows.Controls.TabItem>().Select(tab => tab.Header.ToString()).SequenceEqual(
                new[] { "Hotkeys", "Presets", "Processes", "Ignored", "Profiles", "Settings", "About" }), "Final tab layout changed.");
            var options = new (ProfileOption Id, string Text, Func<bool> Get)[]
            {
                (ProfileOption.NotifyOnSaved, "Notify On Saved", () => model.NotifyOnSaved),
                (ProfileOption.RestoreAllIncludeMinimized, "Restore All: Include minimized", () => model.IncludeMinimized),
                (ProfileOption.DisableInFullScreen, "Disable Hotkeys in Full Screen", () => model.DisableInFullScreen),
                (ProfileOption.EnableResizeByTitle, "Resize By Title", () => model.ResizeByTitle),
                (ProfileOption.EnableAutoResizeDelay, "Auto Resize Delay", () => model.AutoResizeDelay),
            };
            var profiles = ConfigFactory.Profiles.Configs;
            var aId = ConfigFactory.Current.ProfileId;
            var bId = profiles.Single(profile => profile.ProfileId != aId).ProfileId;
            foreach (var profile in profiles)
            {
                var value = profile.ProfileId == bId;
                profile.NotifyOnSaved = profile.RestoreAllIncludeMinimized = profile.DisableInFullScreen =
                    profile.EnableResizeByTitle = profile.EnableAutoResizeDelay = value;
            }
            ConfigFactory.Save();
            model.Refresh();
            DrainDispatcher();
            foreach (var id in new[] { bId, aId })
            {
                runtime.SwitchProfile(id);
                var savedAfterSwitch = saves;
                DrainDispatcher();
                Assert(options.All(option => option.Get() == (id == bId)), "Profile option values stale after switch.");
                Assert(saves == savedAfterSwitch, "Profile refresh wrote configuration.");
            }
            foreach (var option in options)
            {
                tabs.SelectedItem = (int)option.Id < 3 ? tabs.Items[0] : processesTab;
                DrainDispatcher();
                var view = (int)option.Id < 3 ? hotkeys : processes;
                var check = Descendants<System.Windows.Controls.CheckBox>(view).Single(item => Equals(item.Content, option.Text));
                if ((int)option.Id >= 3)
                {
                    var panel = (System.Windows.Controls.Panel)check.Parent;
                    var toolbar = (System.Windows.Controls.StackPanel)panel.Parent;
                    Assert(ReferenceEquals(panel.DataContext, model) && toolbar.Children.Contains(
                        (System.Windows.UIElement)processes.FindName("AddProcessButton")), "Processes option is not beside Add with isolated option binding.");
                }
                Assert(check.IsChecked == false, "Checkbox did not bind active profile.");
                var before = saves;
                check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                check.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                DrainDispatcher();
                Assert(option.Get() && saves == before + 1 && check.IsChecked == true, "Checkbox click did not save exactly once.");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                    try { model.SetProfileOption(option.Id, false); throw new InvalidOperationException("Option Save failure accepted."); }
                    catch (IOException) { }
                    DrainDispatcher();
                    Assert(option.Get() && check.IsChecked == true && saves == before + 1, "Option failure did not restore checkbox/Runtime: " + option.Id);
                }
            }
            var imported = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            foreach (var profile in imported["Configs"]!)
                foreach (var field in new[] { "NotifyOnSaved", "RestoreAllIncludeMinimized", "DisableInFullScreen", "EnableResizeByTitle", "EnableAutoResizeDelay" })
                    profile[field] = false;
            var importPath = Path.Combine(Path.GetDirectoryName(path)!, "options-import.json");
            File.WriteAllText(importPath, imported.ToString());
            ConfigFactory.Load(importPath);
            var afterImport = saves;
            DrainDispatcher();
            Assert(options.All(option => !option.Get()) && saves == afterImport, "Import option refresh stale or persisted again.");
            foreach (var option in options)
            {
                tabs.SelectedItem = (int)option.Id < 3 ? tabs.Items[0] : processesTab;
                DrainDispatcher();
                var view = (int)option.Id < 3 ? hotkeys : processes;
                var check = Descendants<System.Windows.Controls.CheckBox>(view).Single(item => Equals(item.Content, option.Text));
                Assert(check.IsChecked == false, "Imported value not presented by checkbox.");
            }
            Assert(model.Hotkeys.Count == 4, "Import lost hotkey rows.");
            tabs.SelectedIndex = 0;
            DrainDispatcher();
            var grid = (System.Windows.Controls.DataGrid)hotkeys.FindName("HotkeysGrid");
            Assert(Descendants<System.Windows.Controls.CheckBox>(hotkeys).Count() == 3, "Hotkeys option grouping changed.");
            foreach (var action in Enum.GetValues(typeof(HotkeysType)).Cast<HotkeysType>())
            {
                var key = CreateHotkey("F" + ((int)action + 8), "Ctrl", "Shift");
                var row = Find(model, action);
                model.SetHotkey(row, key);
                DrainDispatcher();
                grid.UpdateLayout();
                var button = Descendants<System.Windows.Controls.Button>(grid).Single(item => Equals(item.Content, "Set Key") && ReferenceEquals(item.DataContext, row));
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var dialog = app.Windows.OfType<WinResizer.Views.HotkeyCaptureDialog>().Single();
                    var clear = (System.Windows.Controls.Button)dialog.FindName("ClearButton");
                    Assert(clear.Visibility == System.Windows.Visibility.Visible, "Global dialog hides Clear.");
                    clear.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }));
                button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                DrainDispatcher();
                Assert(row.CurrentKey == "-" && !ConfigFactory.Current.Keys.ContainsKey(action), "Dialog Clear did not unassign/display '-'.");
                model.SetHotkey(row, key);
                DrainDispatcher();
                Assert(row.CurrentKey == key.ToKeysString(), "Reassign after dialog Clear failed.");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { model.SetHotkey(row, null); throw new InvalidOperationException("Clear Save failure accepted."); }
                    catch (IOException) { model.Refresh(); }
                    DrainDispatcher();
                    Assert(row.CurrentKey == key.ToKeysString() && ConfigFactory.Current.GetKeys(action)!.Equals(key), "Clear failure changed display/hotkey.");
                }
            }
        }
        finally
        {
            ConfigFactory.ConfigurationSaved -= onSave;
            ((ISettingsWindowHandle)window).CloseForApplicationExit();
        }
    }

    private sealed class HotkeyActionComparer : IComparer
    {
        public int Compare(object? x, object? y) => NaturalStringComparer.Instance.Compare(
            (x as HotkeyRowViewModel)?.Action,
            (y as HotkeyRowViewModel)?.Action);
    }

    private sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        private readonly Dictionary<int, (ModifierKeys Modifier, Keys Key)> _registered =
            new Dictionary<int, (ModifierKeys Modifier, Keys Key)>();
        private EventHandler<KeyPressedEventArgs>? _keyPressed;
        private int _nextId;

        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public bool FailNextRegistration { get; set; }

        public event EventHandler<KeyPressedEventArgs>? KeyPressed
        {
            add => _keyPressed += value;
            remove => _keyPressed -= value;
        }

        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            if (FailNextRegistration)
            {
                FailNextRegistration = false;
                throw new HotkeyHookException();
            }

            if (_registered.Values.Any(value => value.Modifier == modifier && value.Key == key))
            {
                throw new HotkeyHookException("The hotkey is already registered.");
            }

            RegisterCount++;
            var id = ++_nextId;
            _registered.Add(id, (modifier, key));
            return id;
        }

        public void UnRegisterHotKey(int id)
        {
            UnregisterCount++;
            _registered.Remove(id);
        }

        public void UnRegisterHotKey()
        {
            _registered.Clear();
        }

        public void Dispose()
        {
            _registered.Clear();
            _keyPressed = null;
        }
    }

    private sealed class FakeHotkeysWindow : ISettingsWindowHandle
    {
        private readonly HotkeysViewModel _viewModel;

        public FakeHotkeysWindow(WinResizerRuntime runtime)
        {
            _viewModel = new HotkeysViewModel(runtime, Dispatcher.CurrentDispatcher);
        }

        public bool IsVisible { get; private set; }

        public bool IsMinimized => false;

        public void ShowWindow() => IsVisible = true;

        public void RestoreWindow()
        {
        }

        public void ActivateWindow()
        {
        }

        public void HideWindow() => IsVisible = false;

        public void CloseForApplicationExit()
        {
            IsVisible = false;
            _viewModel.Dispose();
        }
    }
}
