using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Services;
using Wpf.Ui.Appearance;

namespace WinResizer;

public partial class SettingsPrototypeWindow : Wpf.Ui.Controls.FluentWindow, ISettingsWindowHandle
{
    private const int WmDpiChanged = 0x02E0;
    private HwndSource? _source;
    private bool _allowApplicationClose;
    private bool _themeAttached;
    private bool _titleBarThemeSubscribed;
    private bool _systemThemeSubscribed;
    private int _lastSystemUsesLightTheme;
    private readonly HotkeysViewModel _hotkeysViewModel;
    private readonly ProfilesViewModel _profilesViewModel;
    private readonly PresetsViewModel _presetsViewModel;
    private readonly ProcessesViewModel _processesViewModel;
    private readonly IgnoredWindowsViewModel _ignoredWindowsViewModel;
    private readonly DwmFrameCompensationViewModel _dwmFrameCompensationViewModel;
    private readonly WinResizerRuntime _runtime;
    private string _activeProfileName = string.Empty;
    private string _dpiStatus = "DPI: detecting…";

    public ThemeService ThemeService { get; } = new ThemeService();
    public ThemeViewModel ThemePreferences { get; }
    public DwmFrameCompensationViewModel DwmFrameCompensation => _dwmFrameCompensationViewModel;
    public HotkeysViewModel ProfileOptions => _hotkeysViewModel;

    public SettingsPrototypeWindow(WinResizerRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        InitializeComponent();
        MainTitleBarIcon.Source = ApplicationIconProvider.ForTheme(ApplicationThemeManager.GetAppTheme());
        _lastSystemUsesLightTheme = SystemThemeIconProvider.ReadSystemUsesLightThemeOrDefault();
        ApplyTaskbarIcon(_lastSystemUsesLightTheme);
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
        _systemThemeSubscribed = true;
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        _titleBarThemeSubscribed = true;
        _hotkeysViewModel = new HotkeysViewModel(runtime, Dispatcher);
        ThemePreferences = new ThemeViewModel(runtime, ThemeService, Dispatcher);
        HotkeysPage.DataContext = _hotkeysViewModel;
        _profilesViewModel = new ProfilesViewModel(runtime, Dispatcher);
        ProfilesPage.DataContext = _profilesViewModel;
        _presetsViewModel = new PresetsViewModel(runtime, Dispatcher);
        PresetsPage.DataContext = _presetsViewModel;
        _processesViewModel = new ProcessesViewModel(runtime, Dispatcher);
        ProcessesPage.DataContext = _processesViewModel;
        _ignoredWindowsViewModel = new IgnoredWindowsViewModel(runtime, Dispatcher);
        IgnoredWindowsPage.DataContext = _ignoredWindowsViewModel;
        _dwmFrameCompensationViewModel = new DwmFrameCompensationViewModel(runtime, Dispatcher);
        DataContext = this;
        _runtime.ProfilesChanged += OnProfilesChanged;
        UpdateProfileStatus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateDpiStatus();
        if (!_themeAttached)
        {
            ThemeService.Attach(this);
            _themeAttached = true;
            ApplyTitleBarIcon(ApplicationThemeManager.GetAppTheme());
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowApplicationClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        ThemePreferences.Dispose();
        ThemeService.Dispose();
        if (_titleBarThemeSubscribed)
        {
            ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
            _titleBarThemeSubscribed = false;
        }
        if (_systemThemeSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
            _systemThemeSubscribed = false;
        }
        _hotkeysViewModel.Dispose();
        _profilesViewModel.Dispose();
        _presetsViewModel.Dispose();
        _processesViewModel.Dispose();
        _ignoredWindowsViewModel.Dispose();
        _dwmFrameCompensationViewModel.Dispose();
        _runtime.ProfilesChanged -= OnProfilesChanged;
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    bool ISettingsWindowHandle.IsMinimized => WindowState == WindowState.Minimized;

    void ISettingsWindowHandle.ShowWindow()
    {
        RefreshTaskbarIcon();
        Show();
    }

    void ISettingsWindowHandle.RestoreWindow() => WindowState = WindowState.Normal;

    void ISettingsWindowHandle.ActivateWindow() => Activate();

    void ISettingsWindowHandle.HideWindow() => Hide();

    void ISettingsWindowHandle.CloseForApplicationExit()
    {
        _allowApplicationClose = true;
        Close();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var grid = FindAncestor<DataGrid>(source);
        if (grid != null)
        {
            if (IsGridRecordOrTechnicalSurface(grid, source))
            {
                return;
            }

            ClearDataGridInteractionState();
            RootLayout.Focus();
            return;
        }

        ClearDataGridInteractionState();

        if (!HasFocusableControl(source))
        {
            RootLayout.Focus();
        }
    }

    private static bool IsGridRecordOrTechnicalSurface(DataGrid grid, DependencyObject? source)
    {
        if (source == null)
        {
            return false;
        }

        if (FindAncestor<DataGridColumnHeader>(source) != null ||
            FindAncestor<ScrollBar>(source) != null ||
            FindAncestor<Thumb>(source) != null ||
            FindAncestor<RepeatButton>(source) != null)
        {
            return true;
        }

        if (ItemsControl.ContainerFromElement(grid, source) is DataGridRow)
        {
            return true;
        }

        return FindAncestor<ButtonBase>(source) != null ||
               FindAncestor<ComboBox>(source) != null ||
               FindAncestor<CheckBox>(source) != null ||
               FindAncestor<TextBox>(source) != null;
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == SettingsTabs)
        {
            ClearDataGridInteractionState();
        }
    }

    private void ClearDataGridInteractionState()
    {
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            if (!(tab.Content is DependencyObject content))
            {
                continue;
            }

            foreach (var grid in FindDescendants<DataGrid>(content))
            {
                grid.UnselectAllCells();
                grid.UnselectAll();
                grid.CurrentCell = new DataGridCellInfo();
            }
        }
    }

    private static bool HasFocusableControl(DependencyObject? source)
    {
        var control = FindAncestor<Control>(source);
        return control != null && control.Focusable && control.IsTabStop;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject source)
    {
        if (source is Visual || source is Visual3D)
        {
            return VisualTreeHelper.GetParent(source);
        }

        return LogicalTreeHelper.GetParent(source);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var descendant in FindDescendants<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return descendant;
            }
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateDpiStatus));
        }

        return IntPtr.Zero;
    }

    private void UpdateDpiStatus()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiStatus = $"DPI: {dpi.DpiScaleX * 100:0}%";
        UpdateStatusText();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(UpdateProfileStatus));
    }

    private void UpdateProfileStatus()
    {
        _activeProfileName = _runtime.GetProfiles().FirstOrDefault(profile => profile.IsCurrent)?.Name ?? string.Empty;
        UpdateStatusText();
    }

    private void UpdateStatusText() =>
        DpiStatusText.Text = $"{_dpiStatus}      Profile: {_activeProfileName}";

    private void OnApplicationThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => ApplyTitleBarIcon(theme)));
    }

    private void ApplyTitleBarIcon(ApplicationTheme theme) =>
        MainTitleBarIcon.Source = ApplicationIconProvider.ForTheme(theme);

    private void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(RefreshTaskbarIcon));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown races with the static SystemEvents callback.
        }
    }

    private void RefreshTaskbarIcon()
    {
        if (!SystemThemeIconProvider.TryReadSystemUsesLightTheme(out var systemUsesLightTheme) ||
            systemUsesLightTheme == _lastSystemUsesLightTheme)
        {
            return;
        }

        _lastSystemUsesLightTheme = systemUsesLightTheme;
        ApplyTaskbarIcon(systemUsesLightTheme);
    }

    private void ApplyTaskbarIcon(int systemUsesLightTheme) =>
        Icon = ApplicationIconProvider.ForSystemMode(systemUsesLightTheme);
}
