using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinResizer.Common.Exceptions;
using WinResizer.Configuration;
using WinResizer.Core.Startup;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
        : this((Application.Current as App)?.StartupService ?? SystemStartupService.CreateForCurrentHost())
    {
    }

    public SettingsView(SystemStartupService startup)
    {
        InitializeComponent();
        StartupCheckBox.DataContext = new StartupViewModel(startup);
        RefreshStartup();
        Loaded += (_, _) => RefreshStartup();
    }

    public string ConfigPath => ConfigFactory.ConfigPath;

    private void RefreshStartup()
    {
        try
        {
            ((StartupViewModel)StartupCheckBox.DataContext).Refresh();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Windows startup settings could not be read: {exception}");
        }
    }

    private void Startup_Click(object sender, RoutedEventArgs e) => RunAction(
        () => ((StartupViewModel)StartupCheckBox.DataContext).SetEnabled(StartupCheckBox.IsChecked == true),
        "change Windows startup settings");

    private void ConfigPath_Click(object sender, RoutedEventArgs e) => RunAction(
        () =>
        {
            using var process = Process.Start(AboutActions.CreateConfigStartInfo());
        }, "show the configuration file in Explorer");

    private void Import_Click(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        var dialog = AboutActions.CreateImportDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var backupPath = AboutActions.Import(dialog.FileName);
        MessageBox.Show(
            Window.GetWindow(this),
            AboutActions.ImportSuccessMessage(backupPath),
            "Configuration Imported",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }, "import configuration");

    private void Export_Click(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        var dialog = AboutActions.CreateExportDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AboutActions.Export(dialog.FileName);
        }
    }, "export configuration");

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: ThemeViewModel model, SelectedItem: string theme } ||
            model.IsRefreshing || theme == model.SelectedTheme)
        {
            return;
        }

        try
        {
            model.SetTheme(theme);
        }
        catch (Exception exception) when (exception is IOException || exception is ArgumentException ||
                                          exception is InvalidOperationException)
        {
            PortableLog.Append($"Theme change failed: {exception}");
            MessageBox.Show(
                Window.GetWindow(this),
                exception is IOException
                    ? "The theme was not changed because the portable configuration could not be saved."
                    : exception.Message,
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DwmFrameCompensation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DwmFrameCompensationViewModel model } check)
        {
            return;
        }

        try
        {
            model.SetCompensateDwmFrameEffects(check.IsChecked == true);
        }
        catch (Exception exception) when (exception is IOException || exception is WinResizerException ||
                                          exception is ArgumentException || exception is InvalidOperationException)
        {
            model.Refresh();
            PortableLog.Append($"DWM frame compensation setting failed: {exception}");
            MessageBox.Show(
                Window.GetWindow(this),
                exception is IOException
                    ? "The setting was not changed because the portable configuration could not be saved."
                    : exception.Message,
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RunAction(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Settings could not {description}: {exception}");
            MessageBox.Show(
                Window.GetWindow(this),
                $"Could not {description}.\n\n{exception.Message}",
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
