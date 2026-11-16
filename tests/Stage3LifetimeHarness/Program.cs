using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;
using WinResizer.Services;

namespace Stage3LifetimeHarness;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Repository root argument is required.");
            return 2;
        }

        var repositoryRoot = Path.GetFullPath(args[0]);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-stage3-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        var originalDirectory = Environment.CurrentDirectory;
        var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            TestSettingsWindowController();
            TestSingleInstanceGuard();
            TestPerMonitorV2();
            TestWindowEventHandlerAndWin32Abi();
            TestPortableLogConcurrency(temporaryRoot);
            TestPortableFirstRun(temporaryRoot);
            TestRuntimeLifecycle(temporaryRoot, repositoryRoot);
            TestHostSourceContracts(repositoryRoot);
            TestAdaptiveSystemIconPolicy(repositoryRoot);
            TestOsdIcons(repositoryRoot);
            TestOsdShowAndLifetime();
            TestPendingDelayDisposal();

            Console.WriteLine(
                "PASS: runtime Start/Dispose and event cleanup, hotkey reload lifecycle, initial snapshot without Settings, " +
                "shared mutex exclusion, lazy/reused Settings, hide/final close, explicit WPF shutdown, config-before-runtime, " +
                "portable first run, CWD independence, no AppData fallback, PMv2 window context, tray click routing, " +
                "independent window-event subscriptions, Win32 ABI signatures, synchronized portable logging, pending-delay cancellation, " +
                "tray disposal ordering, and embedded legacy OSD icon mapping.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            try
            {
                Directory.Delete(temporaryRoot, true);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void TestSettingsWindowController()
    {
        var created = 0;
        FakeSettingsWindow? window = null;
        var controller = new SettingsWindowController(() =>
        {
            created++;
            window = new FakeSettingsWindow();
            return window;
        });

        Assert(!controller.HasWindow && created == 0, "Settings was not lazy.");
        controller.Show();
        Assert(
            controller.HasWindow && created == 1 && window!.ShowCount == 1 && window.ActivateCount == 1,
            "First Settings show failed.");
        var currentWindow = window ?? throw new InvalidOperationException("Settings factory returned null.");

        controller.Hide();
        Assert(!currentWindow.IsVisible && currentWindow.HideCount == 1, "Settings hide failed.");
        currentWindow.IsMinimized = true;
        controller.Show();
        Assert(
            created == 1 && currentWindow.ShowCount == 2 && currentWindow.RestoreCount == 1 && currentWindow.ActivateCount == 2,
            "Settings instance was not reused and restored.");

        controller.BeginExit();
        controller.Show();
        Assert(created == 1 && currentWindow.ActivateCount == 2, "Settings reopened during Exit.");
        controller.CloseForApplicationExit();
        Assert(currentWindow.CloseCount == 1 && !controller.HasWindow, "Final Settings close failed.");
    }

    private static void TestSingleInstanceGuard()
    {
        var mutexName = "Local\\WinResizer-Stage3Test-" + Guid.NewGuid().ToString("N");
        using (var first = SingleInstanceGuard.TryAcquire(mutexName))
        {
            Assert(first is not null, "Normal first single-instance acquisition failed.");
            var second = Task.Run(() => SingleInstanceGuard.TryAcquire(mutexName)).GetAwaiter().GetResult();
            Assert(second is null, "A second host acquired the same mutex.");
        }

        var later = SingleInstanceGuard.TryAcquire(mutexName);
        Assert(later is not null, "Mutex was not released for a later clean start.");
        later!.Dispose();
        later.Dispose();
    }

    private static void TestWindowEventHandlerAndWin32Abi()
    {
        var callbackField = typeof(WindowEventHandler).GetField(
            "_eventHandler", BindingFlags.Instance | BindingFlags.NonPublic);
        var registeredField = typeof(WindowEventHandler).GetField(
            "_isRegistered", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(callbackField is not null && !callbackField.IsStatic,
            "WindowEventHandler callback storage is not per-instance.");
        Assert(registeredField is not null && !registeredField.IsStatic,
            "WindowEventHandler registration state is not per-instance.");

        var first = new WindowEventHandler(_ => { });
        var second = new WindowEventHandler(_ => { });
        Assert(!ReferenceEquals(callbackField!.GetValue(first), callbackField.GetValue(second)),
            "Two WindowEventHandler instances share the same callback delegate.");
        try
        {
            first.AddWindowCreateHandle();
            first.AddWindowCreateHandle();
            second.AddWindowCreateHandle();
            Assert((bool)registeredField!.GetValue(first)! && (bool)registeredField.GetValue(second)!,
                "Independent WindowEventHandler registrations were not retained.");

            first.Dispose();
            first.Dispose();
            Assert(!(bool)registeredField.GetValue(first)! && (bool)registeredField.GetValue(second)!,
                "Disposing one WindowEventHandler damaged another registration.");

            second.RemoveWindowCreateHandle();
            second.RemoveWindowCreateHandle();
            Assert(!(bool)registeredField.GetValue(second)!,
                "WindowEventHandler unregister was not idempotent.");
            second.Dispose();
            second.Dispose();
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        var nativeMethods = typeof(Resizer).Assembly.GetType(
            "WinResizer.Core.WindowControl.NativeMethods", true)!;
        var enumWindows = nativeMethods.GetMethod("EnumWindows", BindingFlags.Static | BindingFlags.NonPublic)!;
        var enumParameters = enumWindows.GetParameters();
        var callbackParameters = enumParameters[0].ParameterType.GetMethod("Invoke")!.GetParameters();
        Assert(enumParameters[1].ParameterType == typeof(IntPtr) &&
               callbackParameters[1].ParameterType == typeof(IntPtr),
            "EnumWindows lParam is not pointer-sized in both the P/Invoke and callback.");
        Assert(Resizer.GetOpenWindows().All(handle => handle != IntPtr.Zero),
            "Window enumeration returned an invalid handle after the ABI correction.");

        var setWindowPos = nativeMethods.GetMethod("SetWindowPos", BindingFlags.Static | BindingFlags.NonPublic)!;
        var marshalAs = setWindowPos.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>();
        var dllImport = setWindowPos.GetCustomAttribute<DllImportAttribute>();
        Assert(setWindowPos.ReturnType == typeof(bool) && marshalAs?.Value == UnmanagedType.Bool &&
               dllImport?.SetLastError == true,
            "SetWindowPos does not expose a correctly marshaled Win32 BOOL result.");
    }

    private static void TestPortableLogConcurrency(string temporaryRoot)
    {
        const int workerCount = 12;
        const int entriesPerWorker = 200;
        var logDirectory = Path.Combine(temporaryRoot, "portable-log");
        PortableLog.SetDirectory(logDirectory);

        var tasks = Enumerable.Range(0, workerCount)
            .Select(worker => Task.Run(() =>
            {
                for (var entry = 0; entry < entriesPerWorker; entry++)
                {
                    PortableLog.Append($"C5:{worker}:{entry}");
                }
            }))
            .ToArray();
        Task.WaitAll(tasks);

        var lines = File.ReadAllLines(Path.Combine(logDirectory, "WinResizer.error.log"));
        var messages = lines.Select(line =>
        {
            var separator = line.IndexOf("] Error: ", StringComparison.Ordinal);
            Assert(separator >= 0, "PortableLog produced a malformed or interleaved entry.");
            return line.Substring(separator + "] Error: ".Length);
        }).ToArray();
        var counts = messages.GroupBy(message => message).ToDictionary(group => group.Key, group => group.Count());
        Assert(lines.Length == workerCount * entriesPerWorker,
            "Parallel PortableLog append lost or added entries.");
        for (var worker = 0; worker < workerCount; worker++)
        {
            for (var entry = 0; entry < entriesPerWorker; entry++)
            {
                Assert(counts.TryGetValue($"C5:{worker}:{entry}", out var count) && count == 1,
                    "Parallel PortableLog append did not preserve every unique entry exactly once.");
            }
        }

        var invalidDirectory = Path.Combine(temporaryRoot, "portable-log-blocker");
        File.WriteAllText(invalidDirectory, "file blocks directory creation");
        PortableLog.SetDirectory(invalidDirectory);
        PortableLog.Append("logger failure must not escape");
        PortableLog.SetDirectory(logDirectory);
    }

    private static void TestPortableFirstRun(string temporaryRoot)
    {
        var portableBase = Path.Combine(temporaryRoot, "WPF output with spaces");
        var unrelatedDirectory = Path.Combine(temporaryRoot, "Unrelated CWD");
        var appDataDecoy = Path.Combine(temporaryRoot, "AppData-decoy");
        var localAppDataDecoy = Path.Combine(temporaryRoot, "LocalAppData-decoy");
        Directory.CreateDirectory(portableBase);
        Directory.CreateDirectory(unrelatedDirectory);
        Environment.SetEnvironmentVariable("APPDATA", appDataDecoy);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppDataDecoy);
        Environment.CurrentDirectory = unrelatedDirectory;

        ConfigFactory.InitializePortablePath(portableBase);
        ConfigFactory.Load();
        var expectedPath = Path.Combine(portableBase, "WinResizer.config.json");
        Assert(ConfigFactory.ConfigPath == expectedPath, "Portable path did not resolve beside the WPF executable base.");
        Assert(File.Exists(expectedPath), "WPF first run did not create the portable config.");
        Assert(!Directory.Exists(appDataDecoy) && !Directory.Exists(localAppDataDecoy), "WPF first run used AppData fallback.");
    }

    private static void TestPerMonitorV2()
    {
        using (var form = new Form())
        {
            var context = GetWindowDpiAwarenessContext(form.Handle);
            Assert(
                AreDpiAwarenessContextsEqual(context, new IntPtr(-4)),
                "The WPF host manifest did not produce a Per-Monitor V2 window context.");
        }
    }

    private static void TestRuntimeLifecycle(string temporaryRoot, string repositoryRoot)
    {
        var portablePath = Path.Combine(temporaryRoot, "runtime", "WinResizer.config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(portablePath)!);
        var externalPath = Path.Combine(temporaryRoot, "runtime-import.json");
        ConfigFactory.SetPortablePath(portablePath);
        ConfigFactory.UseDefault();
        var profileB = ConfigFactory.Profiles.Add("Profile B");
        ConfigFactory.Save();
        ConfigFactory.Export(externalPath);

        var fake = new FakeHotkeyRegistrar();
        var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
        {
            HotkeyRegistrar = fake,
            OwnsHotkeyRegistrar = true,
            EnableWindowMonitoring = false,
        });
        var profileNotifications = 0;
        var runtimeOsdNotifications = 0;
        var lastRuntimeOsdTitle = string.Empty;
        var lastRuntimeOsdMessage = string.Empty;
        runtime.ProfilesChanged += (_, _) => profileNotifications++;
        runtime.NotificationRaised += (_, notification) =>
        {
            runtimeOsdNotifications++;
            lastRuntimeOsdTitle = notification.Title;
            lastRuntimeOsdMessage = notification.Message;
        };

        runtime.Start();
        Assert(runtime.IsStarted, "Runtime did not enter the started state.");
        Assert(runtime.CurrentAutoResizeSnapshot is not null, "Initial Auto Resize snapshot was not published.");
        Assert(fake.RegisterCount == 2 && fake.EventAddCount == 1, "Initial global hotkeys were not registered exactly once.");

        runtime.Start();
        Assert(fake.RegisterCount == 2 && fake.EventAddCount == 1, "Repeated Start duplicated hotkey registration.");

        Assert(runtime.SwitchProfile(profileB.ProfileId), "Runtime profile switch failed.");
        Assert(runtimeOsdNotifications == 1 &&
               lastRuntimeOsdTitle == "Config Reloaded" &&
               lastRuntimeOsdMessage == "Profile switched to <Profile B>.",
            "One synchronous profile switch did not emit exactly one profile OSD.");
        Assert(fake.UnregisterAllCount == 1 && fake.RegisterCount == 4, "Profile switch did not reload hotkeys exactly once.");
        ConfigFactory.Load(externalPath);
        Assert(fake.UnregisterAllCount == 2 && fake.RegisterCount == 6, "Configuration replacement did not reload hotkeys exactly once.");
        Assert(profileNotifications == 2, "Profile/config events did not reach the runtime exactly once each.");

        runtime.Dispose();
        runtime.Dispose();
        Assert(runtime.IsDisposed && fake.DisposeCount == 1, "Double Dispose was not idempotent.");
        Assert(fake.EventRemoveCount == 1, "Dispose did not unsubscribe KeyPressed exactly once.");
        var registrationsAfterDispose = fake.RegisterCount;
        ConfigFactory.Load(externalPath);
        Assert(fake.RegisterCount == registrationsAfterDispose, "Disposed runtime remained subscribed to configuration events.");

        var fake2 = new FakeHotkeyRegistrar();
        using (var runtime2 = new WinResizerRuntime(new WinResizerRuntimeOptions
               {
                   HotkeyRegistrar = fake2,
                   OwnsHotkeyRegistrar = true,
                   EnableWindowMonitoring = false,
               }))
        {
            runtime2.Start();
            Assert(fake2.RegisterCount == 2, "A new runtime could not register after the previous runtime was disposed.");
        }

        var runtimeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WinResizer.Runtime",
            "WinResizerRuntime.cs"));
        Assert(
            !runtimeSource.Contains("SettingForm") && !runtimeSource.Contains("SettingsPrototypeWindow"),
            "Shared runtime depends on a Settings window.");
    }

    private static void TestHostSourceContracts(string repositoryRoot)
    {
        var appXaml = Read(repositoryRoot, "src", "WinResizer", "App.xaml");
        var appSource = Read(repositoryRoot, "src", "WinResizer", "App.xaml.cs");
        var windowXaml = Read(repositoryRoot, "src", "WinResizer", "SettingsPrototypeWindow.xaml");
        var windowSource = Read(repositoryRoot, "src", "WinResizer", "SettingsPrototypeWindow.xaml.cs");
        var iconProviderSource = Read(repositoryRoot, "src", "WinResizer", "Services", "ApplicationIconProvider.cs");
        var systemIconProviderSource = Read(repositoryRoot, "src", "WinResizer", "Services", "SystemThemeIconProvider.cs");
        var traySource = Read(repositoryRoot, "src", "WinResizer", "Services", "TrayService.cs");
        var singleInstanceSource = Read(repositoryRoot, "src", "WinResizer.Runtime", "SingleInstanceGuard.cs");

        Assert(!appXaml.Contains("StartupUri=") && appXaml.Contains("ShutdownMode=\"OnExplicitShutdown\""),
            "WPF Application still depends on StartupUri or lacks explicit shutdown.");
        Assert(appSource.IndexOf("TryLoadPortableConfiguration()", StringComparison.Ordinal) <
               appSource.IndexOf("runtime.Start()", StringComparison.Ordinal),
            "WPF runtime starts before portable configuration is loaded.");
        Assert(appSource.Contains("new SettingsWindowController(() => new SettingsPrototypeWindow(runtime))"),
            "WPF Settings is not created lazily with the shared runtime.");
        Assert(!appSource.Contains("Environment.Exit"), "WPF host uses Environment.Exit instead of clean shutdown.");
        Assert(windowSource.Contains("e.Cancel = true;") && windowSource.Contains("Hide();") &&
               windowSource.Contains("_allowApplicationClose = true;"),
            "Settings X/hide/final-close semantics are missing.");
        Assert(windowXaml.Contains("<ui:FluentWindow") &&
               windowXaml.Contains("ExtendsContentIntoTitleBar=\"True\"") &&
               windowXaml.Contains("<ui:TitleBar") &&
               windowXaml.Contains("Title=\"WinResizer\"") &&
               !windowXaml.Contains("Title=\"WindowResizer\"") &&
               windowXaml.Contains("<ui:ImageIcon x:Name=\"MainTitleBarIcon\""),
            "Main WPF window does not use the visible WPF-UI WinResizer title bar.");
        Assert(windowSource.Contains(": Wpf.Ui.Controls.FluentWindow") &&
               windowSource.Contains("ApplicationThemeManager.Changed += OnApplicationThemeChanged") &&
               windowSource.Contains("ApplicationThemeManager.GetAppTheme()") &&
               windowSource.Contains("ApplicationThemeManager.Changed -= OnApplicationThemeChanged") &&
               windowSource.Contains("ApplicationIconProvider.ForTheme"),
            "Main title bar is not synchronized with the effective WPF-UI theme.");
        Assert(iconProviderSource.Contains("DarkIconResource = \"WinResizer.Resources.AppIcon-light.ico\"") &&
               iconProviderSource.Contains("LightIconResource = \"WinResizer.Resources.AppIcon-dark.ico\"") &&
               iconProviderSource.Contains("theme == ApplicationTheme.Dark") &&
               iconProviderSource.Contains("SystemThemeIconProvider.SelectIconVariant"),
            "Theme-aware application icon mapping is incomplete.");
        Assert(traySource.Contains("LoadTrayIcon(\"WinResizer.Resources.AppIcon-light.ico\")") &&
               traySource.Contains("LoadTrayIcon(\"WinResizer.Resources.AppIcon-dark.ico\")"),
            "Tray theme icon resource mapping is incomplete.");
        Assert(windowSource.Contains("Icon = ApplicationIconProvider.ForSystemMode") &&
               windowSource.Contains("SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged") &&
               windowSource.Contains("SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged") &&
               windowSource.Contains("RefreshTaskbarIcon();") &&
               !windowSource.Contains("WM_SETICON"),
            "Taskbar icon does not use the standard Window.Icon system-theme lifecycle.");
        Assert(systemIconProviderSource.Contains("SystemUsesLightTheme") &&
               traySource.Contains("SystemThemeIconProvider.SelectIconVariant") &&
               windowSource.Contains("SystemThemeIconProvider.TryReadSystemUsesLightTheme"),
            "Tray and taskbar do not share the SystemUsesLightTheme policy.");
        Assert(traySource.IndexOf("_notifyIcon.Visible = false;", StringComparison.Ordinal) <
               traySource.IndexOf("_notifyIcon.Dispose();", StringComparison.Ordinal),
            "Tray icon is not hidden before disposal.");
        Assert(singleInstanceSource.Contains("ApplicationMutexName") &&
               appSource.Contains("SingleInstanceGuard.TryAcquire()"),
            "WPF startup does not use the shared single-instance guard.");
        Assert(!appSource.Contains("SpecialFolder") && !appSource.Contains("CurrentDirectory"),
            "WPF host contains a non-portable config fallback.");
        Assert(traySource.Contains("_notifyIcon.MouseClick += OnMouseClick"),
            "Tray does not subscribe to the single-click event.");
        Assert(traySource.Contains("e.Button == Forms.MouseButtons.Left"),
            "Tray single-click handler does not restrict activation to the left button.");
        Assert(!traySource.Contains("_notifyIcon.DoubleClick"),
            "Tray still requires or subscribes to DoubleClick for activation.");
        Assert(traySource.Contains("new Forms.ToolStripMenuItem(\"WinResizer\", null, OnSettings)"),
            "Tray menu does not expose the exact WinResizer command.");
        Assert(!traySource.Contains("new Forms.ToolStripMenuItem(\"WindowResizer\", null, OnSettings)"),
            "Tray menu still exposes the old product name.");
        Assert(traySource.Contains("private static string BuildTooltip() => \"WinResizer\";"),
            "Tray tooltip is not exactly WinResizer.");
        Assert(traySource.Contains("new Forms.ToolStripMenuItem($\"Profile: {profile.Name}\")"),
            "Tray profile menu items do not use the Profile: name format.");
        Assert(
            traySource.IndexOf("new Forms.ToolStripMenuItem(\"WinResizer\", null, OnSettings)", StringComparison.Ordinal) <
            traySource.IndexOf("new Forms.ToolStripSeparator()", StringComparison.Ordinal) &&
            traySource.IndexOf("new Forms.ToolStripSeparator()", StringComparison.Ordinal) <
            traySource.IndexOf("foreach (var profile in _runtime.GetProfiles())", StringComparison.Ordinal),
            "Tray WinResizer command is not the first menu item followed by a separator.");
        Assert(
            traySource.IndexOf("new Forms.ToolStripMenuItem(\"Save All Windows\"", StringComparison.Ordinal) <
            traySource.IndexOf("new Forms.ToolStripMenuItem(\"Restore All Windows\"", StringComparison.Ordinal) &&
            traySource.IndexOf("new Forms.ToolStripMenuItem(\"Restore All Windows\"", StringComparison.Ordinal) <
            traySource.IndexOf("new Forms.ToolStripMenuItem(\"Exit\"", StringComparison.Ordinal),
            "Tray menu Save All Windows/Restore All Windows/Exit order is incorrect.");
        Assert(traySource.Contains("OnSettings(sender, e);"),
            "Tray left click does not use the shared Main window command path.");
        Assert(traySource.IndexOf("tray?.Dispose();", StringComparison.Ordinal) <
               appSource.IndexOf("runtime?.Dispose();", StringComparison.Ordinal),
            "Tray cleanup is not performed before runtime cleanup.");
        Assert(traySource.Contains("SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged") &&
               traySource.Contains("SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged"),
            "Tray system-theme subscription lifecycle is incomplete.");
        Assert(traySource.Contains("systemUsesLightTheme == _lastSystemUsesLightTheme"),
            "Duplicate system-theme notifications are not guarded before replacing the tray icon.");
        Assert(traySource.Contains("_darkSymbolIcon.Dispose();") &&
               traySource.Contains("_lightSymbolIcon.Dispose();"),
            "Tray icon variants are not disposed explicitly.");
        Assert(!traySource.Contains("ExtractAssociatedIcon") &&
               !traySource.Contains("AppsUseLightTheme") &&
               !traySource.Contains("ThemePreference") &&
               !traySource.Contains("ShouldSystemUseDarkMode"),
            "Tray icon selection still depends on EXE extraction, app Theme, or undocumented dark-mode APIs.");
    }

    private static void TestAdaptiveSystemIconPolicy(string repositoryRoot)
    {
        Assert(
            SystemThemeIconProvider.SelectIconVariant(0) == SystemThemeIconVariant.LightSymbol,
            "SystemUsesLightTheme=0 did not select the light tray/taskbar symbol.");
        Assert(
            SystemThemeIconProvider.SelectIconVariant(1) == SystemThemeIconVariant.DarkSymbol,
            "SystemUsesLightTheme=1 did not select the dark tray/taskbar symbol.");
        Assert(
            SystemThemeIconProvider.SelectIconVariant(null) == SystemThemeIconVariant.DarkSymbol &&
            SystemThemeIconProvider.SelectIconVariant(2) == SystemThemeIconVariant.DarkSymbol,
            "Missing or invalid system theme did not use the dark-symbol fallback.");
        Assert(ReferenceEquals(ApplicationIconProvider.ForSystemMode(1),
                   ApplicationIconProvider.ForTheme(Wpf.Ui.Appearance.ApplicationTheme.Light)) &&
               ReferenceEquals(ApplicationIconProvider.ForSystemMode(0),
                   ApplicationIconProvider.ForTheme(Wpf.Ui.Appearance.ApplicationTheme.Dark)),
            "Taskbar system mode and TitleBar/About app theme do not map to the same embedded icon variants.");

        var projectSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WinResizer",
            "WinResizer.csproj"));
        Assert(projectSource.Contains("<ApplicationIcon>Resources\\AppIcon-light.ico</ApplicationIcon>") &&
               projectSource.Contains("<EmbeddedResource Include=\"Resources\\AppIcon-light.ico\">") &&
               projectSource.Contains("<EmbeddedResource Include=\"Resources\\AppIcon-dark.ico\">") &&
               projectSource.Contains("LogicalName"),
            "Dedicated embedded tray icon resources are not declared.");
    }

    private static void TestOsdIcons(string repositoryRoot)
    {
        Assert(
            OsdIconResources.ForLevel(RuntimeNotificationLevel.Success) == OsdIconKind.Success &&
            OsdIconResources.ForLevel(RuntimeNotificationLevel.Info) == OsdIconKind.Info &&
            OsdIconResources.ForLevel(RuntimeNotificationLevel.Warning) == OsdIconKind.Warning &&
            OsdIconResources.ForLevel(RuntimeNotificationLevel.Error) == OsdIconKind.Error &&
            OsdIconResources.ForLevel((RuntimeNotificationLevel)int.MaxValue) == OsdIconKind.Info,
            "Runtime notification levels do not map to the four OSD icons with Info fallback.");

        foreach (OsdIconKind kind in Enum.GetValues(typeof(OsdIconKind)))
        {
            var icon = OsdIconResources.LoadImage(kind);
            Assert(icon.Width > 0 && icon.Height > 0,
                $"Embedded OSD resource {kind} did not load as an image.");

            var fileName = OsdIconResources.GetAssetFileName(kind);
            var resource = System.Windows.Application.GetResourceStream(OsdIconResources.GetResourceUri(kind));
            Assert(resource is not null, $"Embedded OSD resource {fileName} is missing.");
            using var stream = resource!.Stream;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var productionBytes = File.ReadAllBytes(Path.Combine(
                repositoryRoot,
                "src",
                "WinResizer",
                "Resources",
                "Osd",
                fileName));
            Assert(buffer.ToArray().SequenceEqual(productionBytes),
                $"Embedded OSD asset {fileName} differs from the current source file.");
        }

        var projectSource = Read(repositoryRoot, "src", "WinResizer", "WinResizer.csproj");
        var osdSource = Read(repositoryRoot, "src", "WinResizer", "Services", "OsdNotificationService.cs");
        var osdWindowSource = Read(repositoryRoot, "src", "WinResizer", "Services", "OsdWindow.cs");
        var traySource = Read(repositoryRoot, "src", "WinResizer", "Services", "TrayService.cs");
        var runtimeSource = Read(repositoryRoot, "src", "WinResizer.Runtime", "WinResizerRuntime.cs");

        Assert(projectSource.Contains("<Resource Include=\"Resources\\Osd\\*.png\" />") &&
               !projectSource.Contains("Resources\\Osd\\*.png\">\r\n      <CopyToOutputDirectory>"),
            "OSD PNG files are not declared as embedded WPF Resources.");
        Assert(osdSource.Contains("TimeSpan.FromMilliseconds(2500)") &&
               osdSource.Contains("new OsdWindow()") &&
               osdSource.Contains("DispatcherOsdTimer") &&
               !osdSource.Contains("Queue<") &&
               !osdSource.Contains("ShellNotifyIcon") &&
               !osdSource.Contains("ShowBalloonTip") &&
               !osdSource.Contains("hBalloonIcon") &&
               !osdSource.Contains("BalloonTipClosed"),
            "OSD does not use one deterministic WPF window/timer or retains the Shell balloon backend.");
        Assert(osdWindowSource.Contains("ShowActivated = false") &&
               osdWindowSource.Contains("ShowInTaskbar = false") &&
               osdWindowSource.Contains("Topmost = true") &&
               osdWindowSource.Contains("WsExNoActivate") &&
               osdWindowSource.Contains("WsExToolWindow") &&
               osdWindowSource.Contains("WindowStyle.None") &&
               osdWindowSource.Contains("SystemParameters.WorkArea"),
            "WPF OSD focus, taskbar, chrome, topmost, or work-area contracts are missing.");
        Assert(traySource.Contains("new OsdNotificationService(_dispatcher)") &&
               traySource.Contains("_osdNotifications.Show(e.Title, e.Message, e.Level)") &&
               traySource.Contains("_osdNotifications.Dispose()"),
            "Tray does not reuse and dispose one shared OSD service.");
        AssertNotificationLevelNear(runtimeSource, "$\"Process <{processName}> saved!\"",
            "RuntimeNotificationLevel.Success", "Save Active Window");
        AssertNotificationLevelNear(runtimeSource, "Current processes saved.",
            "RuntimeNotificationLevel.Success", "Save All Windows");
        AssertNotificationLevelNear(runtimeSource, "Profile switched to",
            "RuntimeNotificationLevel.Success", "profile switch");
        AssertNotificationLevelNear(runtimeSource, "No Operations Available",
            "RuntimeNotificationLevel.Info", "Restore Active Window no-match");
        AssertNotificationLevelNear(runtimeSource, "Unable to resize process",
            "RuntimeNotificationLevel.Warning", "Restore Active Window process failure");
        AssertNotificationLevelNear(runtimeSource, "Register preset hotkey",
            "RuntimeNotificationLevel.Error", "preset hotkey registration failure");

        var restoreAllBody = SourceSection(runtimeSource, "public void RestoreAll()", "public void Dispose()");
        var applyPresetBody = SourceSection(runtimeSource, "private static void ApplyPreset", "private void OnConfigNoMatch");
        Assert(!restoreAllBody.Contains("Notify("), "Normal Restore All unexpectedly emits an OSD notification.");
        Assert(!applyPresetBody.Contains("Notify("), "Normal preset application unexpectedly emits an OSD notification.");
    }

    private static void TestOsdShowAndLifetime()
    {
        Assert(OsdNotificationService.DisplayDuration == TimeSpan.FromMilliseconds(2500),
            "Production OSD display duration is not configured for 2.5 seconds.");

        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var timer = new FakeOsdTimer();
        var window = new FakeOsdWindow();
        var windowCreateCount = 0;
        using (var service = new OsdNotificationService(
                   System.Windows.Threading.Dispatcher.CurrentDispatcher,
                   () =>
                   {
                       windowCreateCount++;
                       return window;
                   },
                   timer,
                   () => now,
                   OsdNotificationService.DisplayDuration))
        {
            service.Show("Save", "First", RuntimeNotificationLevel.Success);
            Assert(windowCreateCount == 1 && window.ShowCount == 1 &&
                   window.LastTitle == "Save" && window.LastMessage == "First" &&
                   timer.IsEnabled && timer.Interval == TimeSpan.FromMilliseconds(2500),
                "The first WPF OSD did not show with one window and one 2.5-second timer.");

            now = now.AddSeconds(1);
            service.Show("Save All", "Second", RuntimeNotificationLevel.Info);
            Assert(windowCreateCount == 1 && window.ShowCount == 2 &&
                   window.LastTitle == "Save All" && window.LastMessage == "Second" &&
                   timer.IsEnabled && timer.Interval == TimeSpan.FromMilliseconds(2500),
                "The second OSD did not immediately replace the first and restart the timer.");

            now = now.AddSeconds(1.5);
            timer.Fire();
            Assert(window.HideCount == 0 && timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(1),
                "The stale first deadline hid the newer OSD.");

            now = now.AddSeconds(1);
            timer.Fire();
            Assert(window.HideCount == 1 && !timer.IsEnabled,
                "The latest OSD was not hidden 2.5 seconds after its request.");

            for (var index = 0; index < 20; index++)
            {
                now = now.AddMilliseconds(1);
                service.Show("Config Reloaded", $"Profile switched to <Profile {index + 1}>.", RuntimeNotificationLevel.Success);
            }

            Assert(windowCreateCount == 1 && window.ShowCount == 22 &&
                   window.LastMessage == "Profile switched to <Profile 20>." &&
                   timer.IsEnabled && timer.StartCount == 23,
                "Twenty rapid notifications were queued or did not leave the latest message current.");

            var showsBeforeFinalHide = window.ShowCount;
            now = now.AddMilliseconds(2500);
            timer.Fire();
            Assert(window.HideCount == 2 && window.ShowCount == showsBeforeFinalHide && !timer.IsEnabled,
                "The stress-test OSD did not hide once or replayed an older notification.");
        }

        Assert(window.DisposeCount == 1 && !timer.IsEnabled,
            "The single WPF OSD window/timer was not disposed cleanly.");
    }

    private static void AssertNotificationLevelNear(
        string source,
        string marker,
        string expectedLevel,
        string operation)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert(markerIndex >= 0, $"Could not locate the {operation} notification.");
        var snippetLength = Math.Min(500, source.Length - markerIndex);
        var snippet = source.Substring(markerIndex, snippetLength);
        Assert(snippet.Contains(expectedLevel), $"{operation} does not use {expectedLevel}.");
    }

    private static string SourceSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert(start >= 0 && end > start, $"Could not read source section {startMarker}.");
        return source.Substring(start, end - start);
    }

    private static void TestPendingDelayDisposal()
    {
        var config = Config.NewConfig("stage3b");
        config.EnableAutoResizeDelay = true;
        config.WindowSizes.Add(new WindowSize
        {
            Name = Process.GetCurrentProcess().ProcessName + ".exe",
            Title = string.Empty,
            AutoResize = true,
            AutoResizeDelay = 5000,
        });
        var snapshot = AutoResizeConfigurationSnapshot.Capture(
            config,
            Array.Empty<IgnoredWindowRule>());
        using (var coordinator = new AutoResizeCoordinator(() => snapshot))
        using (var probeWindow = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(200, 100),
        })
        {
            probeWindow.Show();
            coordinator.Schedule(probeWindow.Handle);
            Task.Delay(100).GetAwaiter().GetResult();

            var stopwatch = Stopwatch.StartNew();
            coordinator.Dispose();
            stopwatch.Stop();
            Assert(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Disposing a pending five-second Auto Resize delay took {stopwatch.Elapsed.TotalMilliseconds:0.###} ms.");
        }
    }

    private static string Read(string root, params string[] parts)
    {
        var path = root;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
        }

        return File.ReadAllText(path);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        private EventHandler<KeyPressedEventArgs>? _keyPressed;
        private bool _disposed;
        private int _nextId;

        public int EventAddCount { get; private set; }
        public int EventRemoveCount { get; private set; }
        public int RegisterCount { get; private set; }
        public int UnregisterAllCount { get; private set; }
        public int DisposeCount { get; private set; }

        public event EventHandler<KeyPressedEventArgs>? KeyPressed
        {
            add
            {
                EventAddCount++;
                _keyPressed += value;
            }
            remove
            {
                EventRemoveCount++;
                _keyPressed -= value;
            }
        }

        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            RegisterCount++;
            return ++_nextId;
        }

        public void UnRegisterHotKey(int id)
        {
        }

        public void UnRegisterHotKey()
        {
            UnregisterAllCount++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCount++;
            UnRegisterHotKey();
            _keyPressed = null;
        }
    }

    private sealed class FakeOsdWindow : IOsdWindow
    {
        public bool IsVisible { get; private set; }
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string LastTitle { get; private set; } = string.Empty;
        public string LastMessage { get; private set; } = string.Empty;

        public void ShowNotification(string title, string message, System.Windows.Media.ImageSource icon)
        {
            IsVisible = true;
            ShowCount++;
            LastTitle = title;
            LastMessage = message;
        }

        public void HideNotification()
        {
            IsVisible = false;
            HideCount++;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeOsdTimer : IOsdTimer
    {
        public event EventHandler? Tick;

        public TimeSpan Interval { get; set; }

        public bool IsEnabled { get; private set; }

        public int StartCount { get; private set; }

        public void Start()
        {
            IsEnabled = true;
            StartCount++;
        }

        public void Stop() => IsEnabled = false;

        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSettingsWindow : ISettingsWindowHandle
    {
        public bool IsVisible { get; private set; }
        public bool IsMinimized { get; set; }
        public int ShowCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int ActivateCount { get; private set; }
        public int HideCount { get; private set; }
        public int CloseCount { get; private set; }

        public void ShowWindow()
        {
            IsVisible = true;
            ShowCount++;
        }

        public void RestoreWindow()
        {
            IsMinimized = false;
            RestoreCount++;
        }

        public void ActivateWindow()
        {
            ActivateCount++;
        }

        public void HideWindow()
        {
            IsVisible = false;
            HideCount++;
        }

        public void CloseForApplicationExit()
        {
            IsVisible = false;
            CloseCount++;
        }
    }
}
