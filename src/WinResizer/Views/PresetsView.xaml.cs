using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinResizer.Common.Exceptions;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class PresetsView : UserControl
{
    public PresetsView()
    {
        InitializeComponent();
    }

    private PresetsViewModel? ViewModel => DataContext as PresetsViewModel;

    private void CaptureWindow_Click(object sender, RoutedEventArgs e) => OpenCapture(null);

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is { } row) OpenCapture(row);
    }

    private void OpenCapture(PresetRowViewModel? row)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;
        try
        {
            // Preserve identity across sorting and profile changes while the dialog is open.
            var profileId = row?.ProfileId ?? viewModel.GetCaptureProfileId();
            var presetId = row?.PresetId;
            var dialog = new CaptureWindowDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || dialog.Result is not { } capture) return;
            if (presetId is null)
                viewModel.AddCapturedPreset(profileId, capture);
            else
                viewModel.CapturePreset(profileId, presetId, capture);
        }
        catch (Exception exception) when (exception is ArgumentException ||
            exception is InvalidOperationException || exception is WinResizerException || exception is IOException)
        {
            viewModel.Refresh();
            if (exception is IOException)
            {
                PortableLog.Append($"Preset Capture failed: {exception}");
                ShowError("Capture was not saved because the portable configuration could not be saved.");
            }
            else ShowError(exception.Message);
        }
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            ViewModel.AddPreset();
        }
        catch (ArgumentException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (WinResizerException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (IOException exception)
        {
            PortableLog.Append($"Preset Add operation failed: {exception}");
            ViewModel.Refresh();
            ShowError("The preset was not added because the portable configuration could not be saved.");
        }
    }

    private void Active_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            GetRow(sender) is not { } row ||
            ViewModel is null ||
            checkBox.IsChecked is not bool enabled)
        {
            return;
        }

        try
        {
            ViewModel.SetPresetEnabled(row, enabled);
        }
        catch (ArgumentException exception)
        {
            checkBox.IsChecked = row.Enabled;
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            checkBox.IsChecked = row.Enabled;
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (WinResizerException exception)
        {
            checkBox.IsChecked = row.Enabled;
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (IOException exception)
        {
            checkBox.IsChecked = row.Enabled;
            PortableLog.Append($"Preset Active operation failed: {exception}");
            ViewModel.Refresh();
            ShowError("The preset state was not changed because the portable configuration could not be saved.");
        }
    }

    private void SetKey_Click(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row || ViewModel is null)
        {
            return;
        }

        var dialog = new HotkeyCaptureDialog(row.Name, row.Hotkey, allowClear: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            switch (dialog.Result)
            {
                case HotkeyCaptureResult.Set when dialog.ResultHotkey is { } hotkey:
                    ViewModel.SetPresetHotkey(row, hotkey);
                    break;
                case HotkeyCaptureResult.Clear:
                    ViewModel.SetPresetHotkey(row, null);
                    break;
                default:
                    return;
            }
        }
        catch (HotkeyNotAllowedException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message, HotkeyNotAllowedException.Title);
        }
        catch (ArgumentException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (WinResizerException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (IOException exception)
        {
            PortableLog.Append($"Preset Set Key operation failed: {exception}");
            ViewModel.Refresh();
            ShowError("The preset hotkey was not changed because the portable configuration could not be saved.");
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row || ViewModel is null)
        {
            return;
        }

        try
        {
            if (ViewModel.RemovePreset(row))
            {
                return;
            }

            ViewModel.Refresh();
            ShowError("The preset could not be removed.");
        }
        catch (ArgumentException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (WinResizerException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (IOException exception)
        {
            PortableLog.Append($"Preset Remove operation failed: {exception}");
            ViewModel.Refresh();
            ShowError("The preset was not removed because the portable configuration could not be saved.");
        }
    }

    private void PresetsGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        var editor = GetEditor(e.EditingElement);
        editor?.Focus();
        editor?.SelectAll();
    }

    private void PresetsGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        DeferRefresh();
    }

    private static TextBox? GetEditor(FrameworkElement editingElement)
    {
        if (editingElement is not ContentPresenter presenter)
        {
            return null;
        }

        presenter.ApplyTemplate();
        return presenter.ContentTemplate?.FindName("PresetEditor", presenter) as TextBox;
    }

    private void PresetsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not PresetRowViewModel row ||
            GetEditor(e.EditingElement) is not { } editor ||
            ViewModel is null)
        {
            return;
        }

        var edit = new PresetEditSnapshot(
            row.ProfileId,
            row.PresetId,
            row.Name,
            row.Top,
            row.Left,
            row.Width,
            row.Height,
            e.Column.SortMemberPath);

        if (e.Column == PresetNameColumn)
        {
            edit.Name = editor.Text;
        }
        else if (e.Column == TopColumn)
        {
            if (!TryParseInteger(editor.Text, out var value))
            {
                DeferRefresh();
                return;
            }

            edit.Top = value;
        }
        else if (e.Column == LeftColumn)
        {
            if (!TryParseInteger(editor.Text, out var value))
            {
                DeferRefresh();
                return;
            }

            edit.Left = value;
        }
        else if (e.Column == WidthColumn)
        {
            if (!TryParseInteger(editor.Text, out var value))
            {
                DeferRefresh();
                return;
            }

            edit.Width = value;
        }
        else if (e.Column == HeightColumn)
        {
            if (!TryParseInteger(editor.Text, out var value))
            {
                DeferRefresh();
                return;
            }

            edit.Height = value;
        }
        else
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => CommitEdit(edit)));
    }

    private void CommitEdit(PresetEditSnapshot edit)
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            // The row may still be in EditItem, so its display values can lag behind
            // the previous cell commit. Preserve the latest runtime values of other fields.
            var current = ViewModel.GetPreset(edit.ProfileId, edit.PresetId);
            if (current is null)
            {
                ViewModel.Refresh();
                return;
            }

            ViewModel.UpdatePreset(
                edit.ProfileId,
                edit.PresetId,
                edit.Field == nameof(PresetRowViewModel.Name) ? edit.Name : current.Name,
                edit.Field == nameof(PresetRowViewModel.Top) ? edit.Top : current.Y,
                edit.Field == nameof(PresetRowViewModel.Left) ? edit.Left : current.X,
                edit.Field == nameof(PresetRowViewModel.Width) ? edit.Width : current.Width,
                edit.Field == nameof(PresetRowViewModel.Height) ? edit.Height : current.Height);
        }
        catch (ArgumentException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (WinResizerException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
        }
        catch (IOException exception)
        {
            PortableLog.Append($"Preset inline edit failed: {exception}");
            ViewModel.Refresh();
            ShowError("The preset was not changed because the portable configuration could not be saved.");
        }
    }

    private void DeferRefresh()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ViewModel?.Refresh()));
    }

    private static bool TryParseInteger(string text, out int value) =>
        int.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.CurrentCulture,
            out value);

    private static PresetRowViewModel? GetRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as PresetRowViewModel;

    private void ShowError(string message, string title = "WinResizer")
    {
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private sealed class PresetEditSnapshot
    {
        public PresetEditSnapshot(
            string profileId,
            string presetId,
            string name,
            int top,
            int left,
            int width,
            int height,
            string field)
        {
            ProfileId = profileId;
            PresetId = presetId;
            Name = name;
            Top = top;
            Left = left;
            Width = width;
            Height = height;
            Field = field;
        }

        public string ProfileId { get; }

        public string PresetId { get; }

        public string Field { get; }

        public string Name { get; set; }

        public int Top { get; set; }

        public int Left { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }
}
