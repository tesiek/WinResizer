using System;
using System.Drawing;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using WinResizer.Runtime;

namespace WinResizer.Services;

internal sealed class TrayService : IDisposable
{
    private readonly WinResizerRuntime _runtime;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly Action<string> _log;
    private readonly Forms.ContextMenuStrip _menu = new Forms.ContextMenuStrip();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly OsdNotificationService _osdNotifications;
    private readonly Icon _darkSymbolIcon;
    private readonly Icon _lightSymbolIcon;
    private readonly Dispatcher _dispatcher;
    private int _lastSystemUsesLightTheme;
    private bool _disposed;

    public TrayService(
        WinResizerRuntime runtime,
        Action showSettings,
        Action exit,
        Action<string>? log = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _log = log ?? (_ => { });
        _dispatcher = Dispatcher.CurrentDispatcher;

        var darkSymbolIcon = LoadTrayIcon("WinResizer.Resources.AppIcon-light.ico");
        try
        {
            _darkSymbolIcon = darkSymbolIcon;
            _lightSymbolIcon = LoadTrayIcon("WinResizer.Resources.AppIcon-dark.ico");
        }
        catch
        {
            darkSymbolIcon.Dispose();
            throw;
        }
        _lastSystemUsesLightTheme = SystemThemeIconProvider.ReadSystemUsesLightThemeOrDefault();
        _notifyIcon = new Forms.NotifyIcon();
        _osdNotifications = new OsdNotificationService(_dispatcher);
        try
        {
            _menu.RenderMode = Forms.ToolStripRenderMode.System;
            BuildMenu();
            _notifyIcon.Icon = GetIconForSystemMode(_lastSystemUsesLightTheme);
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Text = BuildTooltip();
            _notifyIcon.MouseClick += OnMouseClick;
            _runtime.ProfilesChanged += OnProfilesChanged;
            _runtime.NotificationRaised += OnNotificationRaised;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _notifyIcon.Visible = true;
        }
        catch
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _osdNotifications.Dispose();
            _menu.Dispose();
            _darkSymbolIcon.Dispose();
            _lightSymbolIcon.Dispose();

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.ProfilesChanged -= OnProfilesChanged;
        _runtime.NotificationRaised -= OnNotificationRaised;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _osdNotifications.Dispose();
        _menu.Dispose();
        _darkSymbolIcon.Dispose();
        _lightSymbolIcon.Dispose();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(UpdateIconForSystemTheme));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown races with the static SystemEvents callback.
        }
    }

    private void UpdateIconForSystemTheme()
    {
        if (_disposed || !SystemThemeIconProvider.TryReadSystemUsesLightTheme(out var systemUsesLightTheme) ||
            systemUsesLightTheme == _lastSystemUsesLightTheme)
        {
            return;
        }

        _lastSystemUsesLightTheme = systemUsesLightTheme;
        _notifyIcon.Icon = GetIconForSystemMode(systemUsesLightTheme);
    }

    private Icon GetIconForSystemMode(int systemUsesLightTheme) =>
        SystemThemeIconProvider.SelectIconVariant(systemUsesLightTheme) == SystemThemeIconVariant.LightSymbol
            ? _lightSymbolIcon
            : _darkSymbolIcon;

    private static Icon LoadTrayIcon(string resourceName)
    {
        var assembly = typeof(TrayService).Assembly;
        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream is null)
            {
                throw new InvalidOperationException($"Tray icon resource was not found: {resourceName}");
            }

            using (var loaded = new Icon(stream))
            {
                return (Icon)loaded.Clone();
            }
        }
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new Forms.ToolStripMenuItem("WinResizer", null, OnSettings));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        foreach (var profile in _runtime.GetProfiles())
        {
            var item = new Forms.ToolStripMenuItem($"Profile: {profile.Name}")
            {
                Checked = profile.IsCurrent,
                CheckOnClick = false,
            };
            item.Click += (sender, args) => RunCommand(() => _runtime.SwitchProfile(profile.Id));
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Save All Windows", null, (sender, args) => RunCommand(_runtime.SaveAll)));
        _menu.Items.Add(new Forms.ToolStripMenuItem("Restore All Windows", null, (sender, args) => RunCommand(_runtime.RestoreAll)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (sender, args) => _exit()));
    }

    private static string BuildTooltip() => "WinResizer";

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        BuildMenu();
        _notifyIcon.Text = BuildTooltip();
    }

    private void OnNotificationRaised(object? sender, RuntimeNotificationEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _osdNotifications.Show(e.Title, e.Message, e.Level);
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _showSettings();
        }
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            OnSettings(sender, e);
        }
    }

    private void RunCommand(Action command)
    {
        try
        {
            command();
        }
        catch (Exception exception)
        {
            _log($"Tray command failed: {exception}");
            _osdNotifications.Show(
                "Operation Failed",
                "An error occurred. Check WinResizer.error.log for details.",
                RuntimeNotificationLevel.Error);
        }
    }

    private void RunCommand(Func<bool> command) => RunCommand(() => { command(); });

}
