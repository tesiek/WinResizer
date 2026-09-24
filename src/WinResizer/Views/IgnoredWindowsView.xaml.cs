using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinResizer.Common.Exceptions;
using WinResizer.Configuration;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class IgnoredWindowsView : UserControl
{
    public IgnoredWindowsView() => InitializeComponent();
    private IgnoredWindowsViewModel? ViewModel => DataContext as IgnoredWindowsViewModel;

    private bool FinishEditing() =>
        IgnoredWindowsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
        IgnoredWindowsGrid.CommitEdit(DataGridEditingUnit.Row, true);

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || !FinishEditing()) return;
        var dialog = new IgnoredWindowCaptureDialog(capture => model.AddCapturedIgnoredWindow(capture))
            { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !FinishEditing()) return;
        RunOperation(() => ViewModel.AddIgnoredWindow(), () => { });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IgnoredWindowRowViewModel row ||
            ViewModel is null || !FinishEditing()) return;
        RunOperation(() => ViewModel.RemoveIgnoredWindow(row), () => { });
    }

    private static TextBox? GetEditor(FrameworkElement element)
    {
        if (element is not ContentPresenter presenter) return null;
        presenter.ApplyTemplate();
        return presenter.ContentTemplate?.FindName("IgnoredEditor", presenter) as TextBox;
    }

    private void Grid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        var editor = GetEditor(e.EditingElement);
        editor?.Focus();
        editor?.SelectAll();
    }

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not IgnoredWindowRowViewModel row ||
            GetEditor(e.EditingElement) is not { } editor || ViewModel is null) return;
        RunOperation(() => ViewModel.CommitCell(row, e.Column.SortMemberPath, editor.Text),
            () => editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget());
    }

    private void Active_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not IgnoredWindowRowViewModel row ||
            checkBox.IsChecked is not bool active || ViewModel is null) return;
        RunOperation(() => ViewModel.SetActive(row, active), () => checkBox.IsChecked = row.Active);
    }

    private void TitleMatch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || !combo.IsLoaded || (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen) ||
            combo.DataContext is not IgnoredWindowRowViewModel row ||
            combo.SelectedItem is not IgnoredWindowTitleMatch mode || mode == row.TitleMatch || ViewModel is null) return;
        RunOperation(() => ViewModel.SetTitleMatch(row, mode), () => combo.SelectedItem = row.TitleMatch);
    }

    private void Grid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ViewModel?.Refresh()));

    private void RunOperation(Action operation, Action restoreEditor)
    {
        try { operation(); }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException ||
            exception is WinResizerException || exception is IOException)
        {
            restoreEditor();
            ViewModel?.Refresh();
            if (exception is IOException) PortableLog.Append($"Ignored Windows edit failed: {exception}");
            MessageBox.Show(Window.GetWindow(this), exception is IOException
                ? "The ignored-window rule was not changed because the portable configuration could not be saved."
                : exception.Message, "WinResizer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
