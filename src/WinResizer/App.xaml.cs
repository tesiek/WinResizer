using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Core.Startup;
using WinResizer.Services;

namespace WinResizer;

public partial class App : Application
{
    private readonly object _reportedExceptionsSync = new object();
    private readonly HashSet<Exception> _reportedExceptions = new HashSet<Exception>();
    private SingleInstanceGuard? _singleInstance;
    private WinResizerRuntime? _runtime;
    private SettingsWindowController? _settings;
    private TrayService? _tray;
    private bool _errorHandlersAttached;
    private bool _isExiting;
    internal SystemStartupService? StartupService { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstance = SingleInstanceGuard.TryAcquire();
        if (_singleInstance is null)
        {
            MessageBox.Show(
                "WinResizer already running.",
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AttachErrorHandlers();
        ConfigFactory.InitializePortablePath(AppContext.BaseDirectory);
        PortableLog.SetDirectory(ConfigFactory.ConfigDirectory);
        ConfigFactory.SetCallbackFailureLogger(PortableLog.Append);

        try
        {
            StartupService = SystemStartupService.CreateForCurrentHost();
            StartupService.RepairPathIfEnabled();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Windows startup path repair failed: {exception}");
        }

        if (!TryLoadPortableConfiguration())
        {
            ShutdownAfterStartupFailure();
            return;
        }

        try
        {
            var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                Log = PortableLog.Append,
            });
            _runtime = runtime;
            runtime.Start();
            _settings = new SettingsWindowController(() => new SettingsPrototypeWindow(runtime));
            _tray = new TrayService(runtime, ShowSettings, RequestExit, PortableLog.Append);
        }
        catch (Exception exception)
        {
            PortableLog.Append($"WPF host startup failed: {exception}");
            MessageBox.Show(
                "WinResizer could not start its runtime. Check WinResizer.error.log for details.",
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShutdownAfterStartupFailure();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private bool TryLoadPortableConfiguration()
    {
        try
        {
            ConfigFactory.Load();
            return true;
        }
        catch (ConfigurationPersistenceException exception)
        {
            ShowConfigurationWriteFailure(exception);
            return false;
        }
        catch (ConfigurationReadException exception)
        {
            return TryRecoverConfiguration(
                $"Configuration could not be read from {ConfigFactory.ConfigPath}. Defaults were restored.",
                exception);
        }
        catch (JsonReaderException exception)
        {
            return TryRecoverConfiguration(
                $"Configuration at {ConfigFactory.ConfigPath} contains invalid JSON. Defaults were restored.",
                exception);
        }
        catch (JsonSerializationException exception)
        {
            return TryRecoverConfiguration(
                $"Configuration at {ConfigFactory.ConfigPath} contains invalid data. Defaults were restored.",
                exception);
        }
        catch (Exception exception)
        {
            return TryRecoverConfiguration(
                $"Configuration at {ConfigFactory.ConfigPath} could not be loaded. Defaults were restored.",
                exception);
        }
    }

    private bool TryRecoverConfiguration(string message, Exception originalException)
    {
        try
        {
            var backupPath = ConfigFactory.RecoverFromLoadFailure();
            ConfigFactory.Save();
            if (backupPath is not null)
            {
                message += $" Original configuration backed up to: {backupPath}";
            }

            PortableLog.Append($"{message}{Environment.NewLine}Exception: {originalException}");
            MessageBox.Show(message, "WinResizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
        catch (ConfigurationPersistenceException recoveryException)
        {
            PortableLog.Append(
                $"Configuration recovery failed.{Environment.NewLine}" +
                $"Original exception: {originalException}{Environment.NewLine}" +
                $"Recovery exception: {recoveryException}");
            ShowConfigurationWriteFailure(recoveryException);
            return false;
        }
    }

    private static void ShowConfigurationWriteFailure(Exception exception)
    {
        var message =
            $"WinResizer cannot write its configuration beside the executable:{Environment.NewLine}" +
            $"{ConfigFactory.ConfigPath}{Environment.NewLine}{Environment.NewLine}" +
            "Move the program to a writable folder or fix the folder permissions.";
        PortableLog.Append($"{message}{Environment.NewLine}Exception: {exception}");
        MessageBox.Show(message, "WinResizer", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowSettings()
    {
        if (_isExiting)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowSettings));
            return;
        }

        _settings?.Show();
    }

    private void RequestExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(RequestExit));
            return;
        }

        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _settings?.BeginExit();
        Cleanup();
        Shutdown();
    }

    private void ShutdownAfterStartupFailure()
    {
        _isExiting = true;
        _settings?.BeginExit();
        Cleanup();
        Shutdown(-1);
    }

    private void Cleanup()
    {
        _settings?.BeginExit();

        var tray = _tray;
        _tray = null;
        try
        {
            tray?.Dispose();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Tray cleanup failed: {exception}");
        }

        var runtime = _runtime;
        _runtime = null;
        try
        {
            runtime?.Dispose();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Runtime cleanup failed: {exception}");
        }

        var settings = _settings;
        _settings = null;
        try
        {
            settings?.CloseForApplicationExit();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Settings cleanup failed: {exception}");
        }

        DetachErrorHandlers();
        var singleInstance = _singleInstance;
        _singleInstance = null;
        try
        {
            singleInstance?.Dispose();
        }
        catch (Exception exception)
        {
            PortableLog.Append($"Single-instance cleanup failed: {exception}");
        }
    }

    private void AttachErrorHandlers()
    {
        if (_errorHandlersAttached)
        {
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _errorHandlersAttached = true;
    }

    private void DetachErrorHandlers()
    {
        if (!_errorHandlersAttached)
        {
            return;
        }

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _errorHandlersAttached = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportUnhandledException(e.Exception, showMessage: true);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
        ReportUnhandledException(exception, showMessage: true);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportUnhandledException(e.Exception, showMessage: false);
        e.SetObserved();
    }

    private void ReportUnhandledException(Exception exception, bool showMessage)
    {
        var firstReport = false;
        lock (_reportedExceptionsSync)
        {
            firstReport = _reportedExceptions.Add(exception);
        }

        if (!firstReport)
        {
            return;
        }

        PortableLog.Append(exception.ToString());
        if (showMessage)
        {
            MessageBox.Show(
                "An error occurred. Check WinResizer.error.log for more details.",
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
