using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Views;
using WinResizer.Sorting;
using Rect = WinResizer.Common.Windows.Rect;

namespace ProcessesWpfReadOnlyHarness;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-processes-wpf-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        WinResizerRuntime? runtime = null;
        ProcessesViewModel? viewModel = null;
        try
        {
            var configPath = Path.Combine(temporaryRoot, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(configPath);
            ConfigFactory.UseDefault();
            var profileA = ConfigFactory.Current;
            profileA.EnableAutoResizeDelay = false;
            var profileB = ConfigFactory.ProfileAdd("Profile B");

            var first = new WindowSize
            {
                Name = "alpha10.exe",
                Title = "Alpha ten",
                Rect = new Rect(-10, -20, 801, 602),
                AutoResize = true,
                AutoResizeDelay = 400,
                State = WinResizer.Common.Windows.WindowState.Maximized,
                MaximizedPosition = new WinResizer.Common.Windows.Point(17, 29),
            };
            var second = new WindowSize
            {
                Name = "alpha2.exe",
                Title = "Alpha two",
                Rect = new Rect(30, 40, 1000, 800),
                AutoResize = false,
                AutoResizeDelay = 0,
            };
            profileA.WindowSizes.Add(first);
            profileA.WindowSizes.Add(second);
            profileB.WindowSizes.Add(new WindowSize
            {
                Name = "profile-b.exe",
                Title = "Profile B",
                Rect = new Rect(1, 2, 3, 4),
                AutoResize = true,
                AutoResizeDelay = 900,
            });
            ConfigFactory.Save();

            var failSave = false;
            var saves = 0;

            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableGlobalHotkeys = false,
                EnableWindowMonitoring = false,
                SaveConfiguration = () =>
                {
                    saves++;
                    if (failSave) throw new IOException("Injected save failure.");
                    ConfigFactory.Save();
                },
            });
            runtime.Start();
            viewModel = new ProcessesViewModel(runtime, Dispatcher.CurrentDispatcher);

            Assert(viewModel.Processes.Count == 2, "The ViewModel did not load the active profile rules.");
            var firstInfo = runtime.GetProcesses(profileA.ProfileId).Single(info => info.Process == first.Name);
            var firstRow = viewModel.Processes.Single(row => row.ProcessToken == firstInfo.ProcessToken);
            Assert(firstRow.ProfileId == profileA.ProfileId && firstRow.ProcessToken == firstInfo.ProcessToken,
                "Runtime profile/token identity did not reach the WPF row.");
            Assert(firstRow.Process == "alpha10.exe" && firstRow.Title == "Alpha ten" &&
                   firstRow.Top == -20 && firstRow.Left == -10 &&
                   firstRow.Right == 801 && firstRow.Bottom == 602 &&
                   firstRow.Auto && firstRow.Delay == 400,
                "One or more of the eight Processes columns was mapped incorrectly.");
            Assert(!viewModel.Processes.Any(row => row.Process.Contains("test", StringComparison.OrdinalIgnoreCase)),
                "Sample Processes data remained in the ViewModel.");

            var backendOrder = profileA.WindowSizes.ToArray();
            var presentationSourceOrder = viewModel.Processes.Select(row => row.ProcessToken).ToArray();
            var view = CollectionViewSource.GetDefaultView(viewModel.Processes);
            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                nameof(ProcessRowViewModel.Process),
                System.ComponentModel.ListSortDirection.Ascending));
            DrainDispatcher();
            Assert(viewModel.Processes.Select(row => row.ProcessToken).SequenceEqual(presentationSourceOrder),
                "View sorting changed the source collection order.");
            Assert(profileA.WindowSizes.SequenceEqual(backendOrder),
                "View sorting changed backend WindowSizes order.");

            runtime.UpdateProcessTitle(profileA.ProfileId, firstInfo.ProcessToken, "Updated title");
            DrainDispatcher();
            Assert(viewModel.Processes.Single(row => row.ProcessToken == firstInfo.ProcessToken).Title == "Updated title",
                "ProcessesChanged did not refresh the existing row.");

            var countBeforeAdd = viewModel.Processes.Count;
            var added = runtime.AddProcess(profileA.ProfileId, "new.exe", "New rule", 5, 6, 700, 500);
            DrainDispatcher();
            Assert(viewModel.Processes.Count == countBeforeAdd + 1 &&
                   viewModel.Processes.Any(row => row.ProcessToken == added.ProcessToken && row.Process == "new.exe"),
                "ProcessesChanged did not add the real Runtime row.");

            Assert(runtime.SwitchProfile(profileB.ProfileId), "Switch to Profile B failed.");
            DrainDispatcher();
            Assert(viewModel.Processes.Count == 1 &&
                   viewModel.Processes[0].ProfileId == profileB.ProfileId &&
                   viewModel.Processes[0].Process == "profile-b.exe",
                "Active profile change did not refresh the Processes list.");

            Assert(runtime.SwitchProfile(profileA.ProfileId), "Switch back to Profile A failed.");
            DrainDispatcher();
            Assert(viewModel.Processes.Count == countBeforeAdd + 1 &&
                   viewModel.Processes.All(row => row.ProfileId == profileA.ProfileId) &&
                   viewModel.Processes.Any(row => row.ProcessToken == firstInfo.ProcessToken),
                "Returning to Profile A did not restore its runtime rows and identity.");

            var currentRow = viewModel.Processes.Single(row => row.ProcessToken == firstInfo.ProcessToken);
            var savedTitle = currentRow.Title;
            failSave = true;
            try
            {
                viewModel.TryCommitCell(currentRow, nameof(ProcessRowViewModel.Title), "Failed title");
                throw new InvalidOperationException("Save failure was not propagated.");
            }
            catch (IOException) { }
            finally { failSave = false; }
            viewModel.Refresh();
            DrainDispatcher();
            Assert(currentRow.Title == savedTitle && first.Title == savedTitle,
                "Failed persistence changed backend or displayed Title.");
            var beforeInvalid = saves;
            Assert(!viewModel.TryCommitCell(currentRow, nameof(ProcessRowViewModel.Top), "abc") &&
                   !viewModel.TryCommitCell(currentRow, nameof(ProcessRowViewModel.Right), "2147483648") &&
                   !viewModel.TryCommitCell(currentRow, nameof(ProcessRowViewModel.Process), "   ") &&
                   !viewModel.TryCommitCell(currentRow, nameof(ProcessRowViewModel.Delay), "1.5") &&
                   saves == beforeInvalid, "Invalid/overflow numeric values were saved.");
            foreach (var field in new[] { nameof(ProcessRowViewModel.Process), nameof(ProcessRowViewModel.Delay) })
            {
                var beforeFailure = runtime.GetProcesses(profileA.ProfileId).ToArray();
                failSave = true;
                try
                {
                    viewModel.TryCommitCell(currentRow, field, field == nameof(ProcessRowViewModel.Process) ? "failed.exe" : "1234");
                    throw new InvalidOperationException("Save failure was not propagated for " + field);
                }
                catch (IOException) { }
                finally { failSave = false; }
                viewModel.Refresh();
                DrainDispatcher();
                Assert(currentRow.Process == beforeFailure[0].Process && currentRow.Delay == beforeFailure[0].Delay &&
                       runtime.GetProcesses(profileA.ProfileId).Select(info => info.Delay)
                           .SequenceEqual(beforeFailure.Select(info => info.Delay)),
                    "Failed persistence changed displayed/backend Process or Delay.");
            }
            TestInlineEditors(viewModel, runtime, currentRow, profileB.ProfileId, enabled => failSave = enabled);

            viewModel.Dispose();
            runtime.Dispose();
            Console.WriteLine($"PASS: Processes WPF data mapping, editing/rollback, Auto, profile/event refresh, sorting and runtime identity ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
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

    private static void TestInlineEditors(ProcessesViewModel viewModel, WinResizerRuntime runtime,
        ProcessRowViewModel row, string otherProfileId, Action<bool> setFailSave)
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = Wpf.Ui.Appearance.ApplicationTheme.Light,
        });
        application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml"),
            });

        var processesView = new ProcessesView { DataContext = viewModel };
        var window = new System.Windows.Window
        {
            Content = processesView, Width = 980, Height = 500,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
        };
        try
        {
            window.Show();
            DrainDispatcher();
            var grid = (DataGrid)processesView.FindName("ProcessesGrid");
            Assert(grid.Columns.Count == 9 && grid.Columns.Count(column => !Equals(column.Header, "")) == 8 && grid.RowHeight == 42 &&
                   ScrollViewer.GetHorizontalScrollBarVisibility(grid) == ScrollBarVisibility.Disabled,
                "Processes layout/eight-column contract changed.");
            var backendOrder = ConfigFactory.Current.WindowSizes.ToArray();
            var collectionView = (ListCollectionView)CollectionViewSource.GetDefaultView(viewModel.Processes);
            collectionView.SortDescriptions.Clear();
            collectionView.CustomSort = new ProcessComparer();
            TextBox? editor = null;
            grid.PreparingCellForEdit += (_, args) =>
            {
                var presenter = (ContentPresenter)args.EditingElement;
                presenter.ApplyTemplate();
                editor = presenter.ContentTemplate.FindName("ProcessEditor", presenter) as TextBox;
            };
            void Edit(string header, string text, bool commitRow = true, bool cancel = false)
            {
                editor = null;
                var column = grid.Columns.Single(item => Equals(item.Header, header));
                grid.ScrollIntoView(row, column);
                grid.CurrentCell = new DataGridCellInfo(row, column);
                grid.UpdateLayout();
                Assert(grid.BeginEdit(), $"BeginEdit failed for '{header}'.");
                Assert(editor != null, $"No editor for '{header}'.");
                editor!.Text = text;
                Assert(cancel ? grid.CancelEdit(DataGridEditingUnit.Cell)
                    : grid.CommitEdit(DataGridEditingUnit.Cell, true), $"Cell finish failed for '{header}'.");
                DrainDispatcher();
                if (commitRow)
                {
                    Assert(grid.CommitEdit(DataGridEditingUnit.Row, true), "Row commit failed.");
                    DrainDispatcher();
                }
            }
            RuntimeProcessInfo Current() => runtime.GetProcesses(row.ProfileId)
                .Single(item => item.ProcessToken == row.ProcessToken);

            Edit("Title", "Inline title", commitRow: false);
            Assert(collectionView.IsEditingItem && row.Title == "Inline title" && Current().Title == "Inline title",
                "Inline Title was not applied safely during EditItem.");
            Edit("Top", "123");
            Edit("Left", "-234");
            Edit("Right", "1300");
            Edit("Bottom", "850");
            Assert(Current().Title == "Inline title" && Current().Top == 123 && Current().Left == -234 &&
                   Current().Right == 1300 && Current().Bottom == 850,
                "Sorted single-cell edits overwrote another field or targeted another row.");
            Edit("Top", "abc");
            Edit("Right", "2147483648");
            Assert(Current().Top == 123 && Current().Right == 1300 && row.Top == 123 && row.Right == 1300,
                "Invalid numeric/overflow edit did not restore display/backend.");
            Edit("Title", "Cancelled title", cancel: true);
            Assert(Current().Title == "Inline title", "Cancelled cell was persisted.");
            Assert(Current().Process == "alpha10.exe" && Current().Delay == 400 &&
                   Current().State == WinResizer.Common.Windows.WindowState.Maximized &&
                   Current().MaximizedPosition.X == 17 && Current().MaximizedPosition.Y == 29,
                "Single-field editing changed unrelated/hidden fields.");
            Assert(ConfigFactory.Current.WindowSizes.SequenceEqual(backendOrder), "Editing/sorting changed backend order.");

            var autoColumn = grid.Columns.Single(item => Equals(item.Header, "Auto"));
            grid.ScrollIntoView(row, autoColumn);
            grid.UpdateLayout();
            foreach (var enabled in new[] { false, true })
            {
                grid.ScrollIntoView(row, autoColumn);
                grid.UpdateLayout();
                var checkBox = FindChild<CheckBox>(autoColumn.GetCellContent(row))
                    ?? throw new InvalidOperationException("Auto checkbox not found.");
                checkBox.SetCurrentValue(ToggleButton.IsCheckedProperty, enabled);
                checkBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                DrainDispatcher();
                Assert(Current().AutoResize == enabled && row.Auto == enabled &&
                       Current().Title == "Inline title" && Current().Right == 1300 && Current().Delay == 400,
                    $"Real Auto click did not persist or overwrote another field: expected={enabled}, runtime={Current().AutoResize}, row={row.Auto}.");
            }

            var delayEnabled = ConfigFactory.Current.EnableAutoResizeDelay;
            var otherRow = viewModel.Processes.Single(item => item.Process == "alpha2.exe");
            var unrelatedRow = viewModel.Processes.Single(item => item.Process == "new.exe");
            var otherToken = otherRow.ProcessToken;
            Edit("Process", "fresh-process.exe");
            Assert(Current().Process == "fresh-process.exe" && row.Process == "fresh-process.exe" && row.Delay == 400,
                "Process edit to a new group did not retain the Runtime delay.");
            foreach (var invalid in new[] { "", "   " })
            {
                Edit("Process", invalid);
                Assert(Current().Process == "fresh-process.exe" && row.Process == "fresh-process.exe",
                    "Empty/whitespace Process did not roll back.");
            }
            Edit("Process", "ALPHA2.exe", commitRow: false);
            Assert(collectionView.IsEditingItem && Current().Process == "ALPHA2.exe" && row.Delay == 0,
                "Joining an existing case-insensitive group did not adopt Runtime delay during the edit.");
            Assert(grid.CommitEdit(DataGridEditingUnit.Row, true), "Process row commit failed.");
            DrainDispatcher();
            Assert(otherRow.Delay == 0 && otherRow.ProcessToken == otherToken && unrelatedRow.Delay == 0,
                "Process edit overwrote another group or changed row identity.");

            Edit("Delay", "1234", commitRow: false);
            Assert(collectionView.IsEditingItem && Current().Delay == 1234 && row.Delay == 1234,
                "Delay was not applied safely during DataGrid editing.");
            Assert(otherRow.Delay == 0,
                "Collection reconciliation was not deferred while the row was being edited.");
            Assert(grid.CommitEdit(DataGridEditingUnit.Row, true), "Delay row commit failed.");
            DrainDispatcher();
            Assert(otherRow.Delay == 1234 && runtime.GetProcesses(row.ProfileId)
                       .Where(info => info.Process.Equals("alpha2.exe", StringComparison.OrdinalIgnoreCase))
                       .All(info => info.Delay == 1234),
                "Group Delay did not refresh all case-insensitive affected rows.");

            setFailSave(true);
            try
            {
                viewModel.TryCommitCell(row, nameof(ProcessRowViewModel.Delay), "2345");
                throw new InvalidOperationException("Grouped Delay save failure was not propagated.");
            }
            catch (IOException) { }
            finally { setFailSave(false); }
            viewModel.Refresh();
            DrainDispatcher();
            Assert(row.Delay == 1234 && otherRow.Delay == 1234 && runtime.GetProcesses(row.ProfileId)
                       .Where(info => info.Process.Equals("alpha2.exe", StringComparison.OrdinalIgnoreCase))
                       .All(info => info.Delay == 1234),
                "Persistence failure did not restore the entire Delay group.");

            foreach (var invalid in new[] { "abc", "1.5", "2147483648" })
            {
                Edit("Delay", invalid);
                Assert(row.Delay == 1234 && otherRow.Delay == 1234 && Current().Delay == 1234,
                    "Invalid integer Delay was persisted.");
            }
            Edit("Delay", "-1");
            Assert(row.Delay == 0 && otherRow.Delay == 0, "Backend lower Delay clamp did not reach all rows.");
            Edit("Delay", "2147483647");
            Assert(row.Delay == 10000 && otherRow.Delay == 10000,
                "Backend upper Delay clamp did not reach all rows.");
            Assert(row.Auto && !otherRow.Auto && unrelatedRow.Delay == 0 &&
                   ConfigFactory.Current.EnableAutoResizeDelay == delayEnabled &&
                   Current().Title == "Inline title" && Current().Top == 123 && Current().Left == -234 &&
                   Current().Right == 1300 && Current().Bottom == 850 &&
                   Current().State == WinResizer.Common.Windows.WindowState.Maximized &&
                   Current().MaximizedPosition.X == 17 && Current().MaximizedPosition.Y == 29 &&
                   ConfigFactory.Current.WindowSizes.SequenceEqual(backendOrder),
                "Process/Delay edits changed Auto, delay enablement, other fields or backend order.");

            Edit("Title", "Before profile switch", commitRow: false);
            runtime.SwitchProfile(otherProfileId);
            DrainDispatcher();
            Assert(collectionView.IsEditingItem && viewModel.Processes.All(item => item.ProfileId == row.ProfileId),
                "Profile refresh modified the collection during an active edit.");
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            DrainDispatcher();
            Assert(viewModel.Processes.Count == 1 && viewModel.Processes[0].ProfileId == otherProfileId,
                "Deferred profile refresh was lost after RowEditEnding.");
            runtime.SwitchProfile(row.ProfileId);
            DrainDispatcher();
            TestAddRemove(processesView, grid, viewModel, runtime, row.ProfileId, otherProfileId, setFailSave);
            Console.WriteLine("PASS: styled Processes WPF editors, Process group changes, grouped Delay/clamp/rollback, sorted identity, Auto and deferred profile refresh.");
        }
        finally { window.Close(); }
    }

    private static void TestAddRemove(ProcessesView processesView, DataGrid grid, ProcessesViewModel viewModel,
        WinResizerRuntime runtime, string profileAId, string profileBId, Action<bool> setFailSave)
    {
        string[] Tokens(string profileId) => runtime.GetProcesses(profileId).Select(info => info.ProcessToken).ToArray();
        var originalA = Tokens(profileAId);
        var originalB = Tokens(profileBId);
        var addButton = (Button)processesView.FindName("AddProcessButton");
        var removeColumn = grid.Columns.Single(column => Equals(column.Header, ""));
        RuntimeProcessInfo Add(string profileId)
        {
            var before = Tokens(profileId);
            addButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher();
            var after = runtime.GetProcesses(profileId);
            Assert(after.Count == before.Length + 1 && after.Take(before.Length).Select(info => info.ProcessToken).SequenceEqual(before),
                "Add did not append exactly one backend rule.");
            var added = after.Last();
            Assert(added.ProfileId == profileId && added.Process == "process.exe" && added.Title == "" &&
                   added.Top == 0 && added.Left == 0 && added.Right == 1 && added.Bottom == 1 && !added.AutoResize &&
                   added.State == WinResizer.Common.Windows.WindowState.Normal &&
                   viewModel.Processes.Any(item => item.ProfileId == profileId && item.ProcessToken == added.ProcessToken),
                "Add defaults/active profile/event refresh were incorrect.");
            return added;
        }
        void Remove(string profileId, string token)
        {
            var before = Tokens(profileId);
            var target = viewModel.Processes.Single(item => item.ProfileId == profileId && item.ProcessToken == token);
            grid.ScrollIntoView(target, removeColumn);
            grid.UpdateLayout();
            var removeButton = FindChild<Button>(removeColumn.GetCellContent(target))
                ?? throw new InvalidOperationException("Remove button was not generated.");
            removeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainDispatcher();
            Assert(Tokens(profileId).SequenceEqual(before.Where(item => item != token)) &&
                   viewModel.Processes.All(item => item.ProcessToken != token),
                "Sorted Remove targeted the wrong token or changed backend order.");
        }

        // Leave a real DataGrid transaction open: Add must finish it before reconciling the list.
        var editingRow = viewModel.Processes.First();
        var titleColumn = grid.Columns.Single(column => Equals(column.Header, "Title"));
        grid.ScrollIntoView(editingRow, titleColumn);
        grid.CurrentCell = new DataGridCellInfo(editingRow, titleColumn);
        grid.UpdateLayout();
        Assert(grid.BeginEdit(), "Could not begin editing before Add.");
        var first = Add(profileAId);
        Assert(!((IEditableCollectionView)CollectionViewSource.GetDefaultView(viewModel.Processes)).IsEditingItem,
            "Add did not finish the active DataGrid edit.");
        var second = Add(profileAId);
        Assert(first.ProcessToken != second.ProcessToken && first.Process == second.Process && first.Title == second.Title,
            "Duplicate Process/Title rules did not retain distinct Runtime identity.");
        runtime.SetProcessDelay(profileAId, first.ProcessToken, 432);
        DrainDispatcher();
        var third = Add(profileAId);
        Assert(third.Delay == 432, "Add did not inherit the Runtime group delay.");
        Assert(Tokens(profileBId).SequenceEqual(originalB), "Add affected another profile.");
        Remove(profileAId, second.ProcessToken);
        Assert(Tokens(profileAId).Contains(first.ProcessToken) && Tokens(profileAId).Contains(third.ProcessToken),
            "Remove confused duplicate Process/Title records.");

        foreach (var remove in new[] { false, true })
        {
            var before = Tokens(profileAId);
            setFailSave(true);
            try
            {
                if (remove)
                    viewModel.RemoveProcess(viewModel.Processes.Single(item => item.ProcessToken == first.ProcessToken));
                else
                    viewModel.AddProcess();
                throw new InvalidOperationException("Add/Remove persistence failure was not propagated.");
            }
            catch (IOException) { }
            finally { setFailSave(false); }
            DrainDispatcher();
            Assert(Tokens(profileAId).SequenceEqual(before) &&
                   viewModel.Processes.Select(item => item.ProcessToken).SequenceEqual(before),
                "Failed Add/Remove left UI/runtime inconsistent.");
        }

        var aBeforeSwitch = Tokens(profileAId);
        runtime.SwitchProfile(profileBId);
        DrainDispatcher();
        var addedB = Add(profileBId);
        Assert(Tokens(profileAId).SequenceEqual(aBeforeSwitch), "Add in Profile B affected Profile A.");
        Remove(profileBId, addedB.ProcessToken);
        Assert(Tokens(profileBId).SequenceEqual(originalB), "Profile B cleanup changed existing records.");
        runtime.SwitchProfile(profileAId);
        DrainDispatcher();
        Assert(viewModel.Processes.Select(item => item.ProcessToken).SequenceEqual(aBeforeSwitch),
            "A -> B -> A lost data/order/identity.");
        Remove(profileAId, first.ProcessToken);
        Remove(profileAId, third.ProcessToken);
        Assert(Tokens(profileAId).SequenceEqual(originalA), "Add/Remove did not preserve original backend order.");
        Console.WriteLine("PASS: real Add/Remove clicks, duplicate-token identity, sorted order, defaults/group delay, persistence rollback and A -> B -> A.");
    }

    private static T? FindChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is T match) return match;
        if (root is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindChild<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }

    private sealed class ProcessComparer : IComparer
    {
        public int Compare(object? x, object? y) => NaturalStringComparer.Instance.Compare(
            (x as ProcessRowViewModel)?.Process, (y as ProcessRowViewModel)?.Process);
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
}
