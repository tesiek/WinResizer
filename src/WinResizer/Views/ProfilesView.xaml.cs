using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
    }

    private ProfilesViewModel? ViewModel => DataContext as ProfilesViewModel;

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateNameDialog("New Profile", "Enter new profile name:");
        if (dialog.ShowDialog() == true)
        {
            RunOperation(
                () =>
                {
                    ViewModel!.AddProfile(dialog.ProfileName);
                    return true;
                },
                "Profile could not be added.");
        }
    }

    private void Active_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            GetRow(sender) is not { } profile ||
            ViewModel is null)
        {
            return;
        }

        if (profile.IsActive)
        {
            checkBox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            return;
        }

        if (checkBox.IsChecked != true)
        {
            checkBox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        }

        if (!RunOperation(
                () => ViewModel.SwitchProfile(profile),
                "Profile could not be switched."))
        {
            checkBox.SetCurrentValue(ToggleButton.IsCheckedProperty, profile.IsActive);
        }
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is { } profile)
        {
            RunOperation(
                () => ViewModel!.RemoveProfile(profile),
                "The active or last profile cannot be removed.");
        }
    }

    private void ProfilesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Column != ProfileNameColumn ||
            e.Row.Item is not ProfileRowViewModel profile ||
            GetProfileNameEditor(e.EditingElement) is not { } editor)
        {
            return;
        }

        var profileName = editor.Text;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => CommitProfileRename(profile, profileName)));
    }

    private void CommitProfileRename(ProfileRowViewModel profile, string profileName)
    {
        RunOperation(
            () => ViewModel!.RenameProfile(profile, profileName),
            "Profile could not be renamed.");
    }

    private void ProfilesGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        var editor = GetProfileNameEditor(e.EditingElement);
        editor?.Focus();
        editor?.SelectAll();
    }

    private static TextBox? GetProfileNameEditor(FrameworkElement editingElement)
    {
        if (editingElement is not ContentPresenter presenter)
        {
            return null;
        }

        presenter.ApplyTemplate();
        return presenter.ContentTemplate?.FindName("ProfileNameEditor", presenter) as TextBox;
    }

    private void ProfilesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e) =>
        DeferRefresh();

    private void DeferRefresh() =>
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => ViewModel?.Refresh()));

    private ProfileNameDialog CreateNameDialog(string title, string prompt, string currentName = "") =>
        new ProfileNameDialog(title, prompt, currentName)
        {
            Owner = Window.GetWindow(this),
        };

    private static ProfileRowViewModel? GetRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as ProfileRowViewModel;

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
            PortableLog.Append($"Profile operation failed: {exception}");
            ViewModel.Refresh();
            ShowError(failureMessage);
            return false;
        }
        catch (IOException exception)
        {
            PortableLog.Append($"Profile operation failed: {exception}");
            ViewModel.Refresh();
            ShowError("The profile changed in memory, but the portable configuration could not be saved.");
            return false;
        }
    }

    private void ShowError(string message)
    {
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            "WinResizer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
