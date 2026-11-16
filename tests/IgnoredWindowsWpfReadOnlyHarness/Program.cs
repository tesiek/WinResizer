using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinResizer.Views;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Services;
using WinResizer.Core.WindowControl;

internal static class Program
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Pump(Dispatcher dispatcher)
    {
        dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
    }

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-Ignored-Wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WinResizerRuntime? runtime = null;
        IgnoredWindowsViewModel? viewModel = null;
        try
        {
            var path = Path.Combine(directory, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(path);
            ConfigFactory.UseDefault();
            var profiles = ConfigFactory.Profiles;
            var profileA = ConfigFactory.Current;
            var profileB = profiles.Add("B");
            profiles.IgnoredWindows.Add(new IgnoredWindowRule
            {
                Active = true,
                Process = "zeta.exe",
                Class = "ZetaClass",
                Title = "Zeta title",
                TitleMatch = IgnoredWindowTitleMatch.EndsWith,
            });
            profiles.IgnoredWindows.Add(new IgnoredWindowRule
            {
                Active = false,
                Process = "alpha.exe",
                Class = "AlphaClass",
                Title = "Alpha title",
                TitleMatch = IgnoredWindowTitleMatch.Contains,
            });
            ConfigFactory.Save();

            var failSave = false;
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableGlobalHotkeys = false,
                EnableWindowMonitoring = false,
                SaveConfiguration = () => { if (failSave) throw new IOException("Injected failure"); ConfigFactory.Save(); },
            });
            runtime.Start();
            var dispatcher = Dispatcher.CurrentDispatcher;
            viewModel = new IgnoredWindowsViewModel(runtime, dispatcher);

            Assert(viewModel.IgnoredWindows.Count == 2, "Runtime ignored rules did not appear in the ViewModel.");
            var first = viewModel.IgnoredWindows[0];
            var second = viewModel.IgnoredWindows[1];
            Assert(first.Active && first.Process == "zeta.exe" && first.Class == "ZetaClass" &&
                first.Title == "Zeta title" && first.TitleMatch == IgnoredWindowTitleMatch.EndsWith,
                "First row mapping is incorrect.");
            Assert(!second.Active && second.Process == "alpha.exe" && second.Class == "AlphaClass" &&
                second.Title == "Alpha title" && second.TitleMatch == IgnoredWindowTitleMatch.Contains,
                "Second row mapping is incorrect.");
            Assert(!string.IsNullOrWhiteSpace(first.IgnoredWindowToken) &&
                !string.IsNullOrWhiteSpace(second.IgnoredWindowToken) &&
                first.IgnoredWindowToken != second.IgnoredWindowToken,
                "Runtime token did not reach the WPF rows.");
            Assert(typeof(IgnoredWindowRowViewModel).GetProperties().Where(property =>
                    property.Name != nameof(IgnoredWindowRowViewModel.IgnoredWindowToken))
                .All(property => property.SetMethod is null || !property.SetMethod.IsPublic),
                "Ignored Windows row exposes a public mutable property.");

            var backendTokens = runtime.GetIgnoredWindows().Select(rule => rule.IgnoredWindowToken).ToArray();
            var view = CollectionViewSource.GetDefaultView(viewModel.IgnoredWindows);
            view.SortDescriptions.Add(new SortDescription(nameof(IgnoredWindowRowViewModel.Process), ListSortDirection.Ascending));
            Assert(view.Cast<IgnoredWindowRowViewModel>().First().Process == "alpha.exe", "View sorting was not applied.");
            Assert(viewModel.IgnoredWindows.Select(row => row.IgnoredWindowToken).SequenceEqual(backendTokens),
                "View sorting changed backend-order collection.");
            view.SortDescriptions.Clear();

            var editorView = (IEditableCollectionView)view;
            editorView.EditItem(first);
            viewModel.CommitCell(first, nameof(first.Process), "");
            viewModel.CommitCell(first, nameof(first.Class), "");
            viewModel.CommitCell(first, nameof(first.Title), "kept title");
            Pump(dispatcher);
            Assert(editorView.IsEditingItem && first.Process == "" && first.Class == "" && first.Title == "kept title",
                "Draft editing or deferred refresh failed.");
            viewModel.SetActive(first, false);
            Assert(!first.Active, "Active OFF failed.");
            viewModel.SetActive(first, true);
            Assert(first.Active, "Active ON failed.");
            foreach (var mode in viewModel.TitleMatchOptions)
            {
                viewModel.SetTitleMatch(first, mode);
                Assert(first.TitleMatch == mode && first.Title == "kept title", "TitleMatch lost Title.");
            }
            viewModel.CommitCell(first, nameof(first.Process), "  Edited.EXE  ");
            viewModel.CommitCell(first, nameof(first.Class), "EditedClass");
            Assert(first.Process == "  Edited.EXE  " && first.Class == "EditedClass", "Unexpected normalization.");
            editorView.CommitEdit();
            viewModel.Refresh();
            Pump(dispatcher);
            Assert(!editorView.IsEditingItem && ReferenceEquals(first, viewModel.IgnoredWindows[0]), "Refresh changed identity.");
            view.SortDescriptions.Add(new SortDescription(nameof(first.Process), ListSortDirection.Descending));
            var sortedRow = view.Cast<IgnoredWindowRowViewModel>().First();
            viewModel.CommitCell(sortedRow, nameof(first.Title), "sorted edit");
            Pump(dispatcher);
            Assert(runtime.GetIgnoredWindows().Single(rule => rule.IgnoredWindowToken == sortedRow.IgnoredWindowToken).Title == "sorted edit",
                "Sorted edit used row index.");
            Assert(runtime.GetIgnoredWindows().Select(rule => rule.IgnoredWindowToken).SequenceEqual(backendTokens), "Editing reordered backend.");
            view.SortDescriptions.Clear();
            foreach (Action operation in new Action[]
            {
                () => viewModel.CommitCell(first, nameof(first.Process), "failed"),
                () => viewModel.CommitCell(first, nameof(first.Class), "failed"),
                () => viewModel.CommitCell(first, nameof(first.Title), "failed"),
                () => viewModel.SetActive(first, false),
                () => viewModel.SetTitleMatch(first, IgnoredWindowTitleMatch.Any),
            })
            {
                var beforeFailure = File.ReadAllText(path);
                var state = (first.Active, first.Process, first.Class, first.Title, first.TitleMatch);
                failSave = true;
                try { operation(); throw new InvalidOperationException("Expected persistence failure."); }
                catch (IOException) { }
                finally { failSave = false; }
                Pump(dispatcher);
                Assert(state == (first.Active, first.Process, first.Class, first.Title, first.TitleMatch) &&
                    File.ReadAllText(path) == beforeFailure, "Failure did not restore UI/runtime/disk.");
            }

            var beforeProfileSwitch = viewModel.IgnoredWindows.Select(row =>
                (row.IgnoredWindowToken, row.Process, row.Title)).ToArray();
            Assert(runtime.SwitchProfile(profileB.ProfileId), "Profile B switch failed.");
            Assert(runtime.SwitchProfile(profileA.ProfileId), "Profile A switch failed.");
            Pump(dispatcher);
            Assert(viewModel.IgnoredWindows.Select(row =>
                (row.IgnoredWindowToken, row.Process, row.Title)).SequenceEqual(beforeProfileSwitch),
                "Changing profile changed global Ignored Windows data or identity.");

            var orderBeforeAdd = runtime.GetIgnoredWindows().Select(rule => rule.IgnoredWindowToken).ToArray();
            var draft1 = viewModel.AddIgnoredWindow();
            var draft2 = viewModel.AddIgnoredWindow();
            Pump(dispatcher);
            Assert(viewModel.IgnoredWindows.Count == 4 && draft1.Active && draft1.Process == "" && draft1.Class == "" &&
                draft1.Title == "" && draft1.TitleMatch == IgnoredWindowTitleMatch.Any, "Add draft/defaults/count failed.");
            Assert(runtime.GetIgnoredWindows().Take(2).Select(rule => rule.IgnoredWindowToken).SequenceEqual(orderBeforeAdd) &&
                runtime.GetIgnoredWindows().Last().IgnoredWindowToken == draft2.IgnoredWindowToken, "Add did not append.");
            var survivor = viewModel.IgnoredWindows.Single(row => row.IgnoredWindowToken == draft2.IgnoredWindowToken);
            var removed = viewModel.IgnoredWindows.Single(row => row.IgnoredWindowToken == draft1.IgnoredWindowToken);
            foreach (Action operation in new Action[] { () => viewModel.AddIgnoredWindow(), () => viewModel.RemoveIgnoredWindow(removed) })
            {
                var ids = runtime.GetIgnoredWindows().Select(rule => rule.IgnoredWindowToken).ToArray();
                var disk = File.ReadAllText(path);
                failSave = true;
                try { operation(); throw new InvalidOperationException("Expected Add/Remove failure."); }
                catch (IOException) { }
                finally { failSave = false; }
                Pump(dispatcher);
                Assert(runtime.GetIgnoredWindows().Select(rule => rule.IgnoredWindowToken).SequenceEqual(ids) &&
                    viewModel.IgnoredWindows.Select(row => row.IgnoredWindowToken).SequenceEqual(ids) &&
                    File.ReadAllText(path) == disk, "Add/Remove rollback lost order/identity/disk/UI consistency.");
            }
            view.SortDescriptions.Add(new SortDescription(nameof(first.Process), ListSortDirection.Descending));
            viewModel.RemoveIgnoredWindow(removed);
            Pump(dispatcher);
            Assert(!runtime.GetIgnoredWindows().Any(rule => rule.IgnoredWindowToken == draft1.IgnoredWindowToken) &&
                viewModel.IgnoredWindows.Contains(survivor), "Sorted duplicate Remove used wrong identity.");
            runtime.SwitchProfile(profileB.ProfileId);
            runtime.SwitchProfile(profileA.ProfileId);
            Pump(dispatcher);
            Assert(viewModel.IgnoredWindows.Contains(survivor), "Global Add/Remove lost on A-B-A.");
            viewModel.RemoveIgnoredWindow(survivor);
            Pump(dispatcher);
            view.SortDescriptions.Clear();
            Assert(runtime.GetIgnoredWindows().Select(rule => rule.IgnoredWindowToken).SequenceEqual(orderBeforeAdd),
                "Add/Remove/sorting reordered surviving backend rules.");

            var added = runtime.AddIgnoredWindow("new.exe", "NewClass", "New title", IgnoredWindowTitleMatch.StartsWith);
            Pump(dispatcher);
            Assert(viewModel.IgnoredWindows.Count == 3 &&
                viewModel.IgnoredWindows.Single(row => row.IgnoredWindowToken == added.IgnoredWindowToken).TitleMatch ==
                IgnoredWindowTitleMatch.StartsWith, "IgnoredWindowsChanged did not refresh the ViewModel.");

            ConfigFactory.Profiles.IgnoredWindows[0].Title = "changed externally";
            ConfigFactory.Save();
            Pump(dispatcher);
            Assert(viewModel.IgnoredWindows[0].Title == "changed externally", "External Runtime save did not refresh the ViewModel.");
            Assert(viewModel.IgnoredWindows.All(row => row is not null), "Sample/null rows appeared in the ViewModel.");
            TestCapture(viewModel, runtime, dispatcher, value => failSave = value);
            TestUi(viewModel, runtime, dispatcher);

            viewModel.Dispose();
            runtime.Dispose();
            viewModel = null;
            runtime = null;
            Console.WriteLine($"PASS: Ignored Windows WPF read-only binding ({_assertions} assertions).");
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
                Directory.Delete(directory, true);
            }
            catch
            {
                // The harness result is already determined; temporary cleanup is best effort.
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void TestCapture(IgnoredWindowsViewModel model, WinResizerRuntime runtime,
        Dispatcher dispatcher, Action<bool> setFailSave)
    {
        using var form = new System.Windows.Forms.Form { Text = "preview title", Width = 300, Height = 200 };
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        var source = new IgnoredWindowCaptureSource(0); // Allow this harness's own windows for the native probes only.
        var handle = form.Handle;
        var preview = source.GetWindows().Single(item => item.Handle == handle);
        Assert(preview.Process == Resizer.GetRealProcessName(handle) && preview.Class == Resizer.GetWindowClassName(handle),
            "Native Process/Class mapping differs from matcher input.");
        Assert(!new IgnoredWindowCaptureSource().GetWindows().Any(item => item.Handle == handle), "Own windows not excluded.");
        var saves = 0;
        var capture = new IgnoredWindowCaptureViewModel(source, item => { model.AddCapturedIgnoredWindow(item); saves++; })
            { SelectedWindow = preview };
        var count = runtime.GetIgnoredWindows().Count;
        form.Text = "  fresh title  ";
        Assert(capture.TryCaptureSelected(), "Capture failed");
        Pump(dispatcher);
        var added = runtime.GetIgnoredWindows().Last();
        Assert(runtime.GetIgnoredWindows().Count == count + 1 && added.Process == preview.Process && added.Class == preview.Class &&
            added.Title == "  fresh title  " && added.Active && added.TitleMatch == IgnoredWindowTitleMatch.Exact,
            "Capture used stale preview, trimmed text, or did not create one complete rule.");

        form.Text = "";
        Assert(capture.TryCaptureSelected(), "Empty title capture failed");
        Assert(runtime.GetIgnoredWindows().Last().Title == "" && runtime.GetIgnoredWindows().Last().TitleMatch == IgnoredWindowTitleMatch.Any,
            "Empty title did not use Any.");
        form.WindowState = System.Windows.Forms.FormWindowState.Maximized;
        System.Windows.Forms.Application.DoEvents();
        Assert(source.GetWindows().Any(item => item.Handle == handle) && source.TryCapture(preview, out _, out _),
            "Maximized window rejected.");
        using var popup = new System.Windows.Forms.Form
        {
            Text = "owned popup", FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow,
            ShowInTaskbar = false, Width = 180, Height = 130,
        };
        popup.Show(form);
        System.Windows.Forms.Application.DoEvents();
        Assert(source.GetWindows().Any(item => item.Handle == popup.Handle), "Owned/tool user popup was filtered.");
        Assert(!IgnoredWindowCaptureSource.IsUserWindow(new IntPtr(1), new IntPtr(1), 0, 0, "tooltips_class32", false) &&
            !IgnoredWindowCaptureSource.IsUserWindow(new IntPtr(1), new IntPtr(1), 0x40000000, 0, "Child", false),
            "Technical/helper or child HWND not filtered.");
        Assert(!IgnoredWindowCaptureSource.IsUserWindow(new IntPtr(1), new IntPtr(1), 0x94090000, 0x80, "Helper", false) &&
            !IgnoredWindowCaptureSource.IsUserWindow(new IntPtr(1), new IntPtr(1), 0, 0x08000000, "Helper", false),
            "Captionless tool/no-activate helper was not filtered.");
        Assert(IgnoredWindowCaptureSource.IsUserWindow(new IntPtr(1), new IntPtr(1), 0x16CF0000, 0x00040100, "UserWindow", false) &&
            IgnoredWindowCaptureSource.IsUserWindow(new IntPtr(1), new IntPtr(1), 0x00C00000, 0x08000080, "UserPopup", false),
            "User-facing app window or captioned tool/no-activate popup was filtered.");
        using var helper = new CaptionlessToolWindow { Text = "technical helper", Width = 120, Height = 80 };
        helper.Show(form);
        System.Windows.Forms.Application.DoEvents();
        Assert(!source.GetWindows().Any(item => item.Handle == helper.Handle) &&
            source.GetWindows().Any(item => item.Handle == handle), "Native helper filter removed the main window or kept the helper.");
        var helperPreview = new IgnoredWindowCaptureItem(helper.Handle, preview.ProcessId, preview.Process,
            Resizer.GetWindowClassName(helper.Handle)!, helper.Text);
        Assert(!source.TryCapture(helperPreview, out _, out _), "Final revalidation allowed a technical helper.");

        var invalid = new IgnoredWindowCaptureItem(handle, preview.ProcessId + 1, preview.Process, preview.Class, preview.Title);
        capture.SelectedWindow = invalid;
        var beforeFailure = runtime.GetIgnoredWindows().Count;
        var beforeSaves = saves;
        Assert(!capture.TryCaptureSelected() && runtime.GetIgnoredWindows().Count == beforeFailure && saves == beforeSaves &&
            !string.IsNullOrWhiteSpace(capture.Status), "Replaced HWND/PID preview saved stale data.");
        capture.SelectedWindow = preview;
        setFailSave(true);
        try { Assert(!capture.TryCaptureSelected(), "Persistence failure reported success"); }
        finally { setFailSave(false); }
        Pump(dispatcher);
        Assert(runtime.GetIgnoredWindows().Count == beforeFailure && model.IgnoredWindows.Count == beforeFailure && saves == beforeSaves,
            "Persistence failure left a draft or inconsistent UI.");
        popup.Close();
        form.Close();
        System.Windows.Forms.Application.DoEvents();
        Assert(!capture.TryCaptureSelected() && runtime.GetIgnoredWindows().Count == beforeFailure && saves == beforeSaves,
            "Closed HWND saved a rule.");
    }

    private static void TestUi(IgnoredWindowsViewModel model, WinResizerRuntime runtime, Dispatcher dispatcher)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml"),
            });
        var control = new IgnoredWindowsView { DataContext = model };
        var window = new System.Windows.Window { Content = control, Width = 980, Height = 500, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            Pump(dispatcher);
            var grid = (DataGrid)control.FindName("IgnoredWindowsGrid");
            Assert(grid.Columns.Count == 6 && grid.RowHeight == 42 &&
                ScrollViewer.GetHorizontalScrollBarVisibility(grid) == ScrollBarVisibility.Disabled, "Grid layout changed.");
            var row = model.IgnoredWindows[1];
            TextBox? editor = null;
            grid.PreparingCellForEdit += (_, args) =>
            {
                var presenter = (ContentPresenter)args.EditingElement;
                presenter.ApplyTemplate();
                editor = (TextBox)presenter.ContentTemplate.FindName("IgnoredEditor", presenter);
            };
            foreach (var field in new[] { "Process", "Class", "Title" })
            {
                var column = grid.Columns.Single(item => Equals(item.Header, field));
                grid.ScrollIntoView(row, column);
                grid.CurrentCell = new DataGridCellInfo(row, column);
                grid.UpdateLayout();
                Assert(grid.BeginEdit() && editor is not null, "No inline editor for " + field);
                editor!.Text = field == "Process" ? "" : "UI " + field;
                Assert(grid.CommitEdit(DataGridEditingUnit.Cell, true), "Cell commit failed");
                Pump(dispatcher);
                Assert(((IEditableCollectionView)CollectionViewSource.GetDefaultView(model.IgnoredWindows)).IsEditingItem,
                    "Refresh interrupted row editing");
                Assert(grid.CommitEdit(DataGridEditingUnit.Row, true), "Row commit failed");
                Pump(dispatcher);
            }
            var check = Descendants<CheckBox>(grid).Single(item => ReferenceEquals(item.DataContext, row));
            check.IsChecked = true;
            check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(dispatcher);
            Assert(runtime.GetIgnoredWindows().Single(item => item.IgnoredWindowToken == row.IgnoredWindowToken).Active, "Checkbox did not save");
            foreach (var mode in model.TitleMatchOptions)
            {
                grid.UpdateLayout();
                var combo = Descendants<ComboBox>(grid).Single(item => ReferenceEquals(item.DataContext, row));
                combo.IsDropDownOpen = true;
                combo.SelectedItem = mode;
                combo.IsDropDownOpen = false;
                Pump(dispatcher);
                Assert(row.TitleMatch == mode && row.Title == "UI Title",
                    $"ComboBox expected {mode}, actual {row.TitleMatch}, title '{row.Title}', items {combo.Items.Count}, loaded {combo.IsLoaded}");
            }
            var initialCount = model.IgnoredWindows.Count;
            var addButton = Descendants<Button>(control).Single(button => Equals(button.Content, "Add"));
            addButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(dispatcher);
            grid.UpdateLayout();
            Assert(model.IgnoredWindows.Count == initialCount + 1, "Add click did not create exactly one rule.");
            var addedRow = model.IgnoredWindows.Last();
            grid.ScrollIntoView(addedRow);
            grid.UpdateLayout();
            var removeButton = Descendants<Button>(grid).Single(button =>
                Equals(button.Content, "Remove") && ReferenceEquals(button.DataContext, addedRow));
            removeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(dispatcher);
            Assert(model.IgnoredWindows.Count == initialCount && !runtime.GetIgnoredWindows().Any(rule =>
                rule.IgnoredWindowToken == addedRow.IgnoredWindowToken), "Remove click did not remove token.");
            var dialogSource = new DialogCaptureSource();
            var dialog = new IgnoredWindowCaptureDialog(dialogSource, capture => model.AddCapturedIgnoredWindow(capture)) { Owner = window };
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Capture"))
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent))));
            Assert(dialog.ShowDialog() == true, "Capture dialog did not complete.");
            Pump(dispatcher);
            Assert(model.IgnoredWindows.Count == initialCount + 1 && runtime.GetIgnoredWindows().Last().Title == "final dialog title",
                "Dialog did not persist one fresh complete rule.");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private sealed class CaptionlessToolWindow : System.Windows.Forms.Form
    {
        public CaptionlessToolWindow()
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
        }

        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x80; // WS_EX_TOOLWINDOW without caption/frame/app-window override.
                return parameters;
            }
        }
    }

    private sealed class DialogCaptureSource : IIgnoredWindowCaptureSource
    {
        private readonly IgnoredWindowCaptureItem _preview = new(new IntPtr(123), 42, "preview.exe", "PreviewClass", "preview title");
        public IReadOnlyList<IgnoredWindowCaptureItem> GetWindows() => new[] { _preview };
        public bool TryCapture(IgnoredWindowCaptureItem preview, out IgnoredWindowCaptureItem? current, out string error)
        {
            current = new IgnoredWindowCaptureItem(preview.Handle, preview.ProcessId, "final.exe", "FinalClass", "final dialog title");
            error = "";
            return true;
        }
    }
}
