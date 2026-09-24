using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinResizer.Services;
using Wpf.Ui.Appearance;

namespace WinResizer.Views;

public partial class AboutView : UserControl
{
    private bool _themeSubscribed;

    public AboutView()
    {
        InitializeComponent();
        var version = typeof(AboutView).Assembly.GetName().Version;
        AboutTitle.Text = $"WinResizer {version?.Major ?? 1}.{version?.Minor ?? 0}";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_themeSubscribed)
        {
            ApplicationThemeManager.Changed += OnApplicationThemeChanged;
            _themeSubscribed = true;
        }

        ApplyThemeIcon(ApplicationThemeManager.GetAppTheme());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_themeSubscribed)
        {
            return;
        }

        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        _themeSubscribed = false;
    }

    private void OnApplicationThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => ApplyThemeIcon(theme)));
    }

    private void ApplyThemeIcon(ApplicationTheme theme) =>
        AboutIcon.Source = ApplicationIconProvider.ForTheme(theme);

    private void GitHub_Click(object sender, RoutedEventArgs e) => RunAction(
        () =>
        {
            using var process = System.Diagnostics.Process.Start(AboutActions.CreateGitHubStartInfo());
        }, "open GitHub");

    private void OriginalProject_Click(object sender, RoutedEventArgs e) => RunAction(
        () =>
        {
            using var process = System.Diagnostics.Process.Start(AboutActions.CreateOriginalProjectStartInfo());
        }, "open the original project");

    private void RunAction(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"About could not {description}: {exception}");
            MessageBox.Show(
                Window.GetWindow(this),
                $"Could not {description}.\n\n{exception.Message}",
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

}
