using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinResizer.Common.Exceptions;
using WinResizer.Common.Shortcuts;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();
    }

    private HotkeysViewModel? ViewModel => DataContext as HotkeysViewModel;

    private void SetKey_Click(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row || ViewModel is null)
        {
            return;
        }

        var dialog = new HotkeyCaptureDialog(row.Action, row.CurrentKey, allowClear: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.Result == HotkeyCaptureResult.Clear)
        {
            RunOperation(() => { ViewModel.SetHotkey(row, null); return true; }, "Hotkey could not be cleared.");
            return;
        }
        if (dialog.Result != HotkeyCaptureResult.Set || dialog.ResultHotkey is not { } hotkey) return;

        RunOperation(
            () =>
            {
                ViewModel.SetHotkey(row, hotkey);
                return true;
            },
            "Hotkey could not be changed.");
    }

    private static HotkeyRowViewModel? GetRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as HotkeyRowViewModel;

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ProfileOption option } check && ViewModel is { } model)
            RunOperation(() => { model.SetProfileOption(option, check.IsChecked == true); return true; }, "Option could not be changed.");
    }

    private bool RunOperation(Func<bool> operation, string failureMessage)
    {
        if (ViewModel is null)
        {
            return false;
        }

        try
        {
            if (operation())
            {
                return true;
            }

            ViewModel.Refresh();
            ShowError(failureMessage);
            return false;
        }
        catch (ArgumentException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
            return false;
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
            return false;
        }
        catch (HotkeyNotAllowedException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message, HotkeyNotAllowedException.Title);
            return false;
        }
        catch (WinResizerException exception)
        {
            ViewModel.Refresh();
            ShowError(exception.Message);
            return false;
        }
        catch (IOException exception)
        {
            PortableLog.Append($"Hotkey operation failed: {exception}");
            ViewModel.Refresh();
            ShowError(failureMessage + " The portable configuration could not be saved.");
            return false;
        }
    }

    private void ShowError(string message, string title = "WinResizer")
    {
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
