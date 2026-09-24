using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinResizer.Common.Exceptions;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();
    private ProcessesViewModel? ViewModel => DataContext as ProcessesViewModel;

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ProfileOption option, DataContext: HotkeysViewModel model } check) return;
        try { model.SetProfileOption(option, check.IsChecked == true); }
        catch (Exception exception) when (exception is IOException || exception is WinResizerException ||
            exception is ArgumentException || exception is InvalidOperationException)
        {
            model.Refresh();
            PortableLog.Append($"Processes option failed: {exception}");
            MessageBox.Show(Window.GetWindow(this), exception is IOException ?
                "The option was not changed because the portable configuration could not be saved." : exception.Message,
                "WinResizer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool FinishEditing() =>
        ProcessesGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
        ProcessesGrid.CommitEdit(DataGridEditingUnit.Row, true);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !FinishEditing()) return;
        RunOperation(() => ViewModel.AddProcess(), () => { });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ProcessRowViewModel row ||
            ViewModel is null || !FinishEditing()) return;
        RunOperation(() => ViewModel.RemoveProcess(row), () => { });
    }

    private static TextBox? GetEditor(FrameworkElement element)
    {
        if (element is not ContentPresenter presenter) return null;
        presenter.ApplyTemplate();
        return presenter.ContentTemplate?.FindName("ProcessEditor", presenter) as TextBox;
    }

    private void ProcessesGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        var editor = GetEditor(e.EditingElement);
        editor?.Focus();
        editor?.SelectAll();
    }

    private void ProcessesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ProcessRowViewModel row ||
            GetEditor(e.EditingElement) is not { } editor || ViewModel is null) return;
        RunOperation(() =>
        {
            if (!ViewModel.TryCommitCell(row, e.Column.SortMemberPath, editor.Text))
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }, () => editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget());
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not ProcessRowViewModel row ||
            checkBox.IsChecked is not bool enabled || ViewModel is null) return;
        RunOperation(() => ViewModel.SetAutoResize(row, enabled), () => checkBox.IsChecked = row.Auto);
    }

    private void ProcessesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ViewModel?.Refresh()));
    }

    private void RunOperation(Action operation, Action restoreEditor)
    {
        try { operation(); }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException ||
            exception is WinResizerException || exception is IOException)
        {
            restoreEditor();
            ViewModel?.Refresh();
            if (exception is IOException) PortableLog.Append($"Processes edit failed: {exception}");
            MessageBox.Show(Window.GetWindow(this), exception is IOException
                ? "The process rule was not changed because the portable configuration could not be saved."
                : exception.Message, "WinResizer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
