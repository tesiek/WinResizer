using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Forms;
using System.Windows.Threading;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridCellInfo = System.Windows.Controls.DataGridCellInfo;
using DataGridColumn = System.Windows.Controls.DataGridColumn;
using DataGridEditingUnit = System.Windows.Controls.DataGridEditingUnit;
using TextBox = System.Windows.Controls.TextBox;
using CollectionViewSource = System.Windows.Data.CollectionViewSource;
using WpfMouse = System.Windows.Input.Mouse;
using WpfMouseButton = System.Windows.Input.MouseButton;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Services;
using WinResizer.Sorting;
using WinResizer.Views;

namespace Stage4AProfilesHarness;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-stage4a-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        WinResizerRuntime? runtime = null;
        ProfilesViewModel? viewModel = null;
        try
        {
            var configPath = Path.Combine(temporaryRoot, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(configPath);
            ConfigFactory.UseDefault();
            var profileA = ConfigFactory.Current;
            ConfigFactory.ProfileRename(profileA.ProfileId, "Profile 10");
            profileA.EnableResizeByTitle = false;

            var profileB = ConfigFactory.ProfileAdd("Profile 2");
            profileB.EnableResizeByTitle = true;
            Assert(ConfigFactory.ProfileSwitch(profileB.ProfileId), "Could not select Profile B before startup.");
            ConfigFactory.Save();

            var registrar = new FakeHotkeyRegistrar();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar,
                OwnsHotkeyRegistrar = false,
                EnableWindowMonitoring = false,
            });
            var runtimeProfileEvents = 0;
            runtime.ProfilesChanged += (_, _) => runtimeProfileEvents++;
            runtime.Start();

            viewModel = new ProfilesViewModel(runtime, Dispatcher.CurrentDispatcher);
            Assert(viewModel.Profiles.Count == 2, "Presentation did not load both initial profiles.");
            var rowA = Find(viewModel, profileA.ProfileId);
            var rowB = Find(viewModel, profileB.ProfileId);
            Assert(!rowA.IsActive && rowB.IsActive, "Presentation did not mark Profile B active.");
            Assert(SubscriptionCount(runtime, viewModel) == 1, "Profiles presentation subscribed more than once.");
            Assert(registrar.RegisterCount == 2, "Initial profile hotkeys were not registered exactly once.");

            Assert(viewModel.SwitchProfile(rowA), "WPF profile switch A failed.");
            DrainDispatcher();
            Assert(ConfigFactory.Profiles.CurrentProfileId == profileA.ProfileId, "WPF switch did not change backend CurrentProfileId.");
            Assert(rowA.IsActive && !rowB.IsActive, "WPF switch did not update presentation Active state.");
            Assert(runtime.CurrentAutoResizeSnapshot?.EnableResizeByTitle == false, "Runtime snapshot did not switch to Profile A.");
            Assert(registrar.RegisterCount == 4, "WPF switch did not reload profile hotkeys once.");

            Task.Run(() => runtime.SwitchProfile(profileB.ProfileId)).GetAwaiter().GetResult();
            DrainDispatcher();
            Assert(ConfigFactory.Profiles.CurrentProfileId == profileB.ProfileId, "External switch did not change backend CurrentProfileId.");
            Assert(!rowA.IsActive && rowB.IsActive, "External switch did not update presentation Active state.");
            Assert(runtime.CurrentAutoResizeSnapshot?.EnableResizeByTitle == true, "Runtime snapshot did not switch to Profile B.");
            Assert(registrar.RegisterCount == 6, "External switch did not reload profile hotkeys once.");

            TestProfilesView(runtime, viewModel, rowA, rowB, () => runtimeProfileEvents);

            var eventsBeforeRenameTests = runtimeProfileEvents;
            TestTransactionalRename(runtime, viewModel, configPath, profileA.ProfileId, profileB.ProfileId);
            var transactionalRenameEvents = runtimeProfileEvents - eventsBeforeRenameTests;

            var profileAId = rowA.ProfileId;
            Assert(viewModel.RenameProfile(rowA, "Profile 10 renamed"), "Rename A failed.");
            DrainDispatcher();
            Assert(rowA.ProfileId == profileAId && rowA.Name == "Profile 10 renamed", "Rename changed ID or failed to update Name.");
            Assert(viewModel.RenameProfile(rowA, "Profile 10"), "Rename A reset failed.");
            DrainDispatcher();

            var beforeAdd = viewModel.Profiles.Count;
            viewModel.AddProfile("Added");
            DrainDispatcher();
            Assert(viewModel.Profiles.Count == beforeAdd + 1, "Add did not create exactly one presentation row.");
            var addedRows = viewModel.Profiles.Where(profile => profile.Name == "Added").ToList();
            Assert(addedRows.Count == 1, "Added profile row is missing or duplicated.");
            var addedRow = addedRows[0];
            var addedConfig = ConfigFactory.Profiles.Configs.Single(profile => profile.ProfileId == addedRow.ProfileId);
            AssertDefaultProfile(addedConfig);

            Assert(viewModel.RenameProfile(addedRow, "Added renamed"), "Rename added profile failed.");
            DrainDispatcher();
            Assert(addedRow.Name == "Added renamed", "Rename did not update the existing row instance.");
            Assert(viewModel.RemoveProfile(addedRow), "Remove added profile failed.");
            DrainDispatcher();
            Assert(viewModel.Profiles.All(profile => profile.ProfileId != addedRow.ProfileId), "Removed profile remained in presentation.");

            var sorted = new System.Windows.Data.ListCollectionView(viewModel.Profiles);
            sorted.CustomSort = new ProfileNameComparer();
            Assert(((ProfileRowViewModel)sorted.GetItemAt(0)).ProfileId == profileB.ProfileId,
                "Natural sort did not place Profile 2 before Profile 10.");
            Assert(viewModel.RemoveProfile(rowA), "Remove by sorted row ID failed.");
            DrainDispatcher();
            Assert(ConfigFactory.Profiles.Configs.All(profile => profile.ProfileId != profileAId),
                "Remove targeted a collection index instead of ProfileId.");
            Assert(ConfigFactory.Profiles.Configs.Any(profile => profile.ProfileId == profileB.ProfileId),
                "Remove deleted the wrong profile after sorting.");
            Assert(!viewModel.RemoveProfile(rowB), "Active/last profile was removable.");

            ConfigFactory.Load();
            DrainDispatcher();
            Assert(viewModel.Profiles.Count == 1 && Find(viewModel, profileB.ProfileId).IsActive,
                "Config round-trip did not preserve final profiles and CurrentProfileId.");
            Assert(runtimeProfileEvents == 15 + transactionalRenameEvents,
                $"Profile operations produced {runtimeProfileEvents} unexpected runtime events.");
            Assert(SubscriptionCount(runtime, viewModel) == 1, "Refreshes accumulated profile event subscriptions.");

            var createdWindows = 0;
            var windowController = new SettingsWindowController(() =>
            {
                createdWindows++;
                return new FakeProfilesWindow(runtime);
            });
            windowController.Show();
            windowController.Hide();
            windowController.Show();
            Assert(createdWindows == 1, "Show/hide/show created more than one Settings window.");
            Assert(PresentationSubscriptionCount(runtime) == 2,
                "Show/hide/show accumulated ProfilesChanged subscriptions.");
            windowController.CloseForApplicationExit();
            Assert(PresentationSubscriptionCount(runtime) == 1,
                "Closing the Settings window did not dispose its profile presentation.");

            var rowsBeforeDispose = viewModel.Profiles.Count;
            viewModel.Dispose();
            Assert(SubscriptionCount(runtime, viewModel) == 0, "Dispose did not detach the profile event subscription.");
            runtime.AddProfile("After dispose");
            DrainDispatcher();
            Assert(viewModel.Profiles.Count == rowsBeforeDispose, "Disposed presentation still received profile events.");

            Console.WriteLine(
                "PASS: real profile initialization, ID-based switch/rename/remove, external switch dispatch, " +
                "runtime snapshot and hotkey reload, add defaults, natural-sort safety, persistence round-trip, " +
                "show/hide subscription stability, and clean disposal.");
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
            catch (Exception)
            {
            }
        }
    }

    private static ProfileRowViewModel Find(ProfilesViewModel viewModel, string profileId) =>
        viewModel.Profiles.Single(profile => profile.ProfileId == profileId);

    private static void TestTransactionalRename(
        WinResizerRuntime runtime, ProfilesViewModel viewModel, string configPath,
        string inactiveId, string activeId)
    {
        var renameEvents = 0;
        var refreshEvents = 0;
        ProfileEvents.ProfileRenameEvent renamed = (id, name) =>
        {
            renameEvents++;
            Assert(ConfigFactory.Profiles.Configs.Single(item => item.ProfileId == id).ProfileName == name,
                "Rename event did not observe the committed live name.");
            Assert(File.ReadAllText(configPath).Contains(name),
                "Rename event was emitted before persistence.");
        };
        EventHandler refreshed = (_, _) => refreshEvents++;
        ConfigFactory.Profiles.ProfileEvents.ProfileRename += renamed;
        runtime.ProfilesChanged += refreshed;
        try
        {
            foreach (var id in new[] { inactiveId, activeId })
            {
                var profile = ConfigFactory.Profiles.Configs.Single(item => item.ProfileId == id);
                var oldName = profile.ProfileName;
                var newName = oldName + " transactional";
                var other = ConfigFactory.Profiles.Configs.Single(item => item.ProfileId != id);
                var otherName = other.ProfileName;
                Assert(runtime.RenameProfile(id, newName), "Transactional rename by ID failed.");
                DrainDispatcher();
                Assert(Find(viewModel, id).Name == newName, "Success did not refresh presentation.");
                Assert(profile.ProfileId == id && other.ProfileName == otherName,
                    "Rename changed identity or another profile.");
                Assert(ConfigFactory.Profiles.CurrentProfileId == activeId,
                    "Rename changed the active profile.");
                Assert(runtime.RenameProfile(id, oldName), "Rename reset failed.");
                DrainDispatcher();

                var diskBefore = File.ReadAllText(configPath);
                var renameBefore = renameEvents;
                var refreshBefore = refreshEvents;
                using (var lockedFile = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failed = false;
                    try
                    {
                        // Cover both Runtime and direct ConfigFactory callers.
                        if (id == activeId)
                            ConfigFactory.ProfileRename(id, newName);
                        else
                            runtime.RenameProfile(id, newName);
                    }
                    catch (ConfigurationPersistenceException)
                    {
                        failed = true;
                    }
                    Assert(failed, "Locked config did not produce a persistence failure.");
                    Assert(profile.ProfileName == oldName, "Failure left the live profile renamed.");
                    Assert(File.ReadAllText(configPath) == diskBefore, "Failure changed persisted config.");
                }
                viewModel.Refresh();
                DrainDispatcher();
                Assert(Find(viewModel, id).Name == oldName, "Refresh after failure did not restore old name.");
                Assert(renameEvents == renameBefore && refreshEvents == refreshBefore,
                    "Failure emitted a false rename/ProfilesChanged success event.");
            }
            Assert(renameEvents == 4 && refreshEvents == 4,
                "Successful renames did not emit exactly one rename and refresh event each.");
            Assert(!runtime.RenameProfile("missing-profile-id", "Unused"), "Unknown ID was accepted.");
            var rejected = false;
            try { runtime.RenameProfile(activeId, "   "); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Existing empty-name validation changed.");
            Console.WriteLine("PASS: active/inactive transactional rename, stable ID, persisted-before-event, live/disk rollback and no false success events.");
        }
        finally
        {
            ConfigFactory.Profiles.ProfileEvents.ProfileRename -= renamed;
            runtime.ProfilesChanged -= refreshed;
        }
    }

    private static void TestProfilesView(
        WinResizerRuntime runtime,
        ProfilesViewModel viewModel,
        ProfileRowViewModel rowA,
        ProfileRowViewModel rowB,
        Func<int> profileEventCount)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = Wpf.Ui.Appearance.ApplicationTheme.Light,
        });
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml"),
            });
        }

        var profilesView = new ProfilesView { DataContext = viewModel };
        var window = new System.Windows.Window
        {
            Content = profilesView,
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
            var grid = profilesView.FindName("ProfilesGrid") as DataGrid
                ?? throw new InvalidOperationException("Profiles DataGrid was not created.");
            var activeColumn = grid.Columns.SingleOrDefault(column => Equals(column.Header, "Active"))
                ?? throw new InvalidOperationException("Active column was not created.");
            Assert(grid.Columns.All(column => !Equals(column.Header, "Switch")),
                "Profiles UI still contains a Switch column.");
            Assert(viewModel.Profiles.Count(profile => profile.IsActive) == 1,
                "Profiles UI did not start with exactly one active profile.");

            var activeA = GetCheckBox(grid, activeColumn, rowA);
            Assert(activeA.IsChecked != true, "Inactive profile was rendered as checked.");
            activeA.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            activeA.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher();
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == rowA.ProfileId,
                "Clicking Active did not switch to the inactive profile.");
            Assert(rowA.IsActive && !rowB.IsActive && viewModel.Profiles.Count(profile => profile.IsActive) == 1,
                "Active click did not reconcile exactly one active presentation row.");

            var eventsBeforeActiveClick = profileEventCount();
            activeA = GetCheckBox(grid, activeColumn, rowA);
            activeA.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            activeA.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher();
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == rowA.ProfileId,
                "Clicking the already active profile changed the active profile.");
            Assert(activeA.IsChecked == true && rowA.IsActive,
                "The active profile could be unchecked.");
            Assert(profileEventCount() == eventsBeforeActiveClick,
                "Clicking an already active profile invoked the switch operation.");

            var nameColumn = grid.Columns.SingleOrDefault(column => Equals(column.Header, "Name"))
                ?? throw new InvalidOperationException("Profile name column was not created.");
            var nameCellContent = nameColumn.GetCellContent(rowB)
                ?? throw new InvalidOperationException("Profile name cell was not created.");
            var nameCell = FindParent<DataGridCell>(nameCellContent)
                ?? throw new InvalidOperationException("Profile name DataGrid cell was not created.");
            nameCell.RaiseEvent(new MouseButtonEventArgs(WpfMouse.PrimaryDevice, 0, WpfMouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            });
            DrainDispatcher();
            grid.CancelEdit(DataGridEditingUnit.Cell);
            grid.CancelEdit(DataGridEditingUnit.Row);
            DrainDispatcher();
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == rowA.ProfileId,
                "Clicking the profile name changed the active profile.");

            grid.SelectedItem = rowB;
            DrainDispatcher();
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == rowA.ProfileId,
                "Selecting another profile row changed the active profile.");

            var activeB = GetCheckBox(grid, activeColumn, rowB);
            activeB.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            activeB.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher();
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == rowB.ProfileId,
                "Clicking Active did not switch back to Profile B.");
            Assert(!rowA.IsActive && rowB.IsActive && viewModel.Profiles.Count(profile => profile.IsActive) == 1,
                "Switching back through Active did not leave exactly one active profile.");

            var originalAName = rowA.Name;
            var originalBName = rowB.Name;
            var rowAId = rowA.ProfileId;
            var rowBId = rowB.ProfileId;
            var view = (System.Windows.Data.ListCollectionView)CollectionViewSource.GetDefaultView(viewModel.Profiles);
            view.CustomSort = new ProfileNameComparer();
            view.Refresh();
            TextBox? editor = null;
            grid.PreparingCellForEdit += (_, args) =>
            {
                if (args.EditingElement is ContentPresenter presenter)
                {
                    presenter.ApplyTemplate();
                    editor = presenter.ContentTemplate?.FindName("ProfileNameEditor", presenter) as TextBox;
                }
            };
            void EditName(ProfileRowViewModel row, string name)
            {
                editor = null;
                var nameColumn = grid.Columns.Single(item => Equals(item.Header, "Name"));
                grid.ScrollIntoView(row, nameColumn);
                grid.SelectedItem = row;
                grid.CurrentCell = new DataGridCellInfo(row, nameColumn);
                grid.UpdateLayout();
                grid.Focus();
                Assert(grid.BeginEdit(), "BeginEdit failed for profile Name.");
                Assert(editor != null, "No inline editor was created for profile Name.");
                editor!.Text = name;
                Assert(grid.CommitEdit(DataGridEditingUnit.Cell, true), "Name cell commit failed.");
                Assert(grid.CommitEdit(DataGridEditingUnit.Row, true), "Name row commit failed.");
                DrainDispatcher();
            }

            var activeBeforeRename = runtime.GetProfiles().Single(profile => profile.IsCurrent).Id;
            EditName(rowA, "Profile 10 inline");
            Assert(rowA.Name == "Profile 10 inline" && rowA.ProfileId == rowAId,
                "Inline rename after sorting did not update the selected ProfileId row.");
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == activeBeforeRename,
                "Renaming an inactive profile changed Active.");
            EditName(rowB, "Profile 2 inline");
            Assert(rowB.Name == "Profile 2 inline" && rowB.ProfileId == rowBId,
                "Inline rename of the active profile did not update the selected ProfileId row.");
            Assert(runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == activeBeforeRename,
                "Renaming the active profile changed Active.");
            EditName(rowA, originalAName);
            EditName(rowB, originalBName);
        }
        finally
        {
            window.Close();
        }
    }

    private static CheckBox GetCheckBox(DataGrid grid, DataGridColumn column, object item)
    {
        grid.ScrollIntoView(item, column);
        grid.UpdateLayout();
        return FindChild<CheckBox>(column.GetCellContent(item))
            ?? throw new InvalidOperationException("Active checkbox was not created.");
    }

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindChild<T>(VisualTreeHelper.GetChild(root, index)) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static void AssertDefaultProfile(Config profile)
    {
        AssertHotkey(profile.GetKeys(HotkeysType.Save), "S", "Ctrl", "Win", "Alt");
        AssertHotkey(profile.GetKeys(HotkeysType.Restore), "R", "Ctrl", "Win", "Alt");
        Assert(profile.GetKeys(HotkeysType.SaveAll) is null, "New profile received a Save All hotkey.");
        Assert(profile.GetKeys(HotkeysType.RestoreAll) is null, "New profile received a Restore All hotkey.");
        Assert(profile.Presets.Count == 0, "New profile received sample Presets.");
    }

    private static void AssertHotkey(Hotkeys? hotkey, string key, params string[] modifiers)
    {
        Assert(hotkey is not null && hotkey.Key == key, $"Expected hotkey {key}.");
        Assert(hotkey!.ModifierKeys.SetEquals(modifiers), $"Unexpected modifiers for {key}.");
    }

    private static int SubscriptionCount(WinResizerRuntime runtime, ProfilesViewModel viewModel)
    {
        var handlers = GetProfilesChangedHandlers(runtime);
        return handlers?.GetInvocationList().Count(handler => ReferenceEquals(handler.Target, viewModel)) ?? 0;
    }

    private static int PresentationSubscriptionCount(WinResizerRuntime runtime) =>
        GetProfilesChangedHandlers(runtime)?.GetInvocationList()
            .Count(handler => handler.Target is ProfilesViewModel) ?? 0;

    private static Delegate? GetProfilesChangedHandlers(WinResizerRuntime runtime)
    {
        var field = typeof(WinResizerRuntime).GetField("ProfilesChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProfilesChanged backing field not found.");
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
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ProfileNameComparer : IComparer
    {
        public int Compare(object? x, object? y) => NaturalStringComparer.Instance.Compare(
            (x as ProfileRowViewModel)?.Name,
            (y as ProfileRowViewModel)?.Name);
    }

    private sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        private EventHandler<KeyPressedEventArgs>? _keyPressed;
        private int _nextId;

        public int RegisterCount { get; private set; }

        public event EventHandler<KeyPressedEventArgs>? KeyPressed
        {
            add => _keyPressed += value;
            remove => _keyPressed -= value;
        }

        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            RegisterCount++;
            return ++_nextId;
        }

        public void UnRegisterHotKey(int id)
        {
        }

        public void UnRegisterHotKey()
        {
        }

        public void Dispose()
        {
            _keyPressed = null;
        }
    }

    private sealed class FakeProfilesWindow : ISettingsWindowHandle
    {
        private readonly ProfilesViewModel _viewModel;

        public FakeProfilesWindow(WinResizerRuntime runtime)
        {
            _viewModel = new ProfilesViewModel(runtime, Dispatcher.CurrentDispatcher);
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
