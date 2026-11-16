using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using WinResizer.Base;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Views;
using WinRect = WinResizer.Common.Windows.Rect;
using WinWindowState = WinResizer.Common.Windows.WindowState;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-DwmFrame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
            { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml") });

        try
        {
            var activePath = Path.Combine(directory, "WinResizer.config.json");
            var exportPath = Path.Combine(directory, "export.json");
            var importPath = Path.Combine(directory, "import.json");
            ConfigFactory.SetPortablePath(activePath);
            ConfigFactory.UseDefault();
            ConfigFactory.Save();
            Assert(ConfigFactory.Profiles.CompensateDwmFrameEffects, "Fresh configuration did not default compensation ON.");

            var saves = 0;
            var failSave = false;
            using (var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableGlobalHotkeys = false,
                EnableWindowMonitoring = false,
                SaveConfiguration = () =>
                {
                    if (failSave) throw new IOException("Injected persistence failure");
                    saves++;
                    ConfigFactory.Save();
                },
            }))
            {
                runtime.Start();
                var events = 0;
                runtime.DwmFrameCompensationChanged += (_, _) => events++;
                using (var model = new DwmFrameCompensationViewModel(runtime, Dispatcher.CurrentDispatcher))
                {
                    var view = new SettingsView { DataContext = new { DwmFrameCompensation = model } };
                    var window = new Window { Content = view, ShowInTaskbar = false, Opacity = 0, Width = 700, Height = 300 };
                    try
                    {
                        window.Show();
                        Drain();
                        var check = (CheckBox)view.FindName("DwmFrameCompensationCheckBox");
                        Assert(check.Content?.ToString() == "Compensate for Desktop Window Manager frame effects" &&
                               check.IsChecked == true, "Settings checkbox label/default binding is incorrect.");

                        model.SetCompensateDwmFrameEffects(false);
                        Drain();
                        Assert(!runtime.GetCompensateDwmFrameEffects() && model.CompensateDwmFrameEffects == false &&
                               check.IsChecked == false &&
                               runtime.CurrentAutoResizeSnapshot?.CompensateDwmFrameEffects == false &&
                               saves == 1 && events == 1,
                            "OFF did not persist/notify/refresh exactly once.");

                        ConfigFactory.Export(exportPath);
                        Assert(ConfigFactory.ConfigPath == activePath &&
                               JObject.Parse(File.ReadAllText(exportPath)).Value<bool>(nameof(Profiles.CompensateDwmFrameEffects)) == false,
                            "Export lost compensation or changed ConfigPath.");

                        model.SetCompensateDwmFrameEffects(true);
                        Assert(runtime.CurrentAutoResizeSnapshot?.CompensateDwmFrameEffects == true,
                            "Auto Resize snapshot did not receive global compensation ON.");
                        TestProcessGeometry(runtime);
                        var profileB = ConfigFactory.ProfileAdd("B");
                        runtime.SwitchProfile(profileB.ProfileId);
                        runtime.SwitchProfile(ConfigFactory.Profiles.Configs[0].ProfileId);
                        Assert(runtime.GetCompensateDwmFrameEffects(), "Profile switching changed the global setting.");

                        File.Copy(exportPath, importPath);
                        Task.Run(() => ConfigFactory.Load(importPath)).GetAwaiter().GetResult();
                        Drain();
                        Assert(ConfigFactory.ConfigPath == activePath && !runtime.GetCompensateDwmFrameEffects() &&
                               !model.CompensateDwmFrameEffects && check.IsChecked == false,
                            "Import did not preserve ConfigPath or refresh the global setting.");

                        failSave = true;
                        try
                        {
                            model.SetCompensateDwmFrameEffects(true);
                            throw new InvalidOperationException("Expected persistence failure.");
                        }
                        catch (IOException)
                        {
                        }
                        finally
                        {
                            failSave = false;
                        }
                        Drain();
                        Assert(!runtime.GetCompensateDwmFrameEffects() && !model.CompensateDwmFrameEffects &&
                               check.IsChecked == false, "Persistence failure did not roll back Runtime/UI.");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }

            ConfigFactory.Load();
            Assert(!ConfigFactory.Profiles.CompensateDwmFrameEffects, "Restart/reload lost persisted OFF.");

            var legacy = JObject.Parse(File.ReadAllText(activePath));
            legacy.Remove(nameof(Profiles.CompensateDwmFrameEffects));
            File.WriteAllText(importPath, legacy.ToString());
            ConfigFactory.Load(importPath);
            Assert(ConfigFactory.Profiles.CompensateDwmFrameEffects,
                "Configuration without the new field did not normalize to default ON.");

            if (args.Length > 0)
            {
                var shellXaml = File.ReadAllText(Path.Combine(args[0], "src", "WinResizer", "SettingsPrototypeWindow.xaml"));
                Assert(shellXaml.Contains("Header=\"Settings\"") && shellXaml.Contains("SettingsView"),
                    "Top-level Settings tab was not restored.");
                var runtimeSource = File.ReadAllText(Path.Combine(args[0], "src", "WinResizer.Runtime", "WinResizerRuntime.cs"));
                var windowUtilsSource = File.ReadAllText(Path.Combine(args[0], "src", "WinResizer.Base", "WindowUtils.cs"));
                Assert(runtimeSource.Contains("WindowUtils.TryUpdateWindowSize(window, config, null, out _)") &&
                       runtimeSource.Contains("Resizer.GetOpenUserWindows()") &&
                       runtimeSource.Contains("Resizer.IsEligibleForUserWindow(window)") &&
                       runtimeSource.Contains("WindowUtils.ResizeAllWindow(ConfigFactory.Current, null)") &&
                       windowUtilsSource.Contains("Resizer.GetPlacement(handle, ConfigFactory.Profiles.CompensateDwmFrameEffects)") &&
                       windowUtilsSource.Contains("HasValidWindowGeometry(place.Rect)") &&
                       windowUtilsSource.Contains("ConfigFactory.Profiles.CompensateDwmFrameEffects);"),
                    "Save All/Restore All are not routed through the shared global geometry contract.");
            }

            Console.WriteLine($"PASS: global DWM frame setting default, persistence, Import/Export, rollback, UI and replacement ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            app.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void TestProcessGeometry(WinResizerRuntime runtime)
    {
        Assert(WindowUtils.HasValidWindowGeometry(new WinRect(0, 0, 100, 100)) &&
               !WindowUtils.HasValidWindowGeometry(new WinRect(960, 600, 960, 600)) &&
               !WindowUtils.HasValidWindowGeometry(new WinRect(10, 20, 5, 30)),
            "Invalid/zero-size Process geometry guard failed.");

        using var form = new System.Windows.Forms.Form
        {
            Text = "DWM process geometry " + Guid.NewGuid().ToString("N"),
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Left = 180,
            Top = 140,
            Width = 620,
            Height = 440,
            ShowInTaskbar = false,
        };
        form.Show();
        System.Windows.Forms.Application.DoEvents();

        var processName = Resizer.GetRealProcessName(form.Handle)
            ?? throw new InvalidOperationException("Could not resolve process fixture.");
        ConfigFactory.Current.EnableResizeByTitle = true;
        var visible = Resizer.GetPlacement(form.Handle, true);
        WindowUtils.UpdateOrSaveWindowSize(form.Handle, ConfigFactory.Current, null);
        var rule = ConfigFactory.Current.WindowSizes.First(row =>
            row.Name.Equals(processName, StringComparison.OrdinalIgnoreCase) && row.Title == form.Text);
        Assert(Equal(rule.Rect, visible.Rect) && rule.State == WinWindowState.Normal,
            "Process Save ON did not store visible bounds/state.");
        runtime.SaveAll();
        rule = ConfigFactory.Current.WindowSizes.First(row =>
            row.Name.Equals(processName, StringComparison.OrdinalIgnoreCase) && row.Title == form.Text);
        Assert(Equal(rule.Rect, visible.Rect),
            "Save All ON did not use the same geometry as Save Active for the same HWND.");

        Assert(Resizer.MoveWindow(form.Handle, Offset(Resizer.GetPlacement(form.Handle).Rect, 90, 70)),
            "Could not displace process fixture.");
        Assert(WindowUtils.TryResizeWindow(form.Handle, ConfigFactory.Current, null, null),
            "Process restore did not report successful native placement.");
        System.Windows.Forms.Application.DoEvents();
        Assert(Equal(Resizer.GetPlacement(form.Handle, true).Rect, visible.Rect),
            "Process visible Capture -> Restore was not an exact round-trip.");

        form.WindowState = System.Windows.Forms.FormWindowState.Maximized;
        System.Windows.Forms.Application.DoEvents();
        WindowUtils.UpdateOrSaveWindowSize(form.Handle, ConfigFactory.Current, null);
        System.Windows.Forms.Application.DoEvents();
        rule = ConfigFactory.Current.WindowSizes.First(row =>
            row.Name.Equals(processName, StringComparison.OrdinalIgnoreCase) && row.Title == form.Text);
        Assert(rule.State == WinWindowState.Maximized && Equal(rule.Rect, visible.Rect) &&
               form.WindowState == System.Windows.Forms.FormWindowState.Maximized,
            "Maximized Process Save did not preserve state and visible restore bounds.");

        form.WindowState = System.Windows.Forms.FormWindowState.Normal;
        Assert(Resizer.MoveWindow(form.Handle, Offset(Resizer.GetPlacement(form.Handle).Rect, 60, 50)),
            "Could not displace maximized restore fixture.");
        WindowUtils.ResizeWindow(form.Handle, ConfigFactory.Current, null, null);
        System.Windows.Forms.Application.DoEvents();
        var maximizedRestore = Resizer.GetPlacement(form.Handle, true);
        Assert(maximizedRestore.WindowState == WinWindowState.Maximized &&
               Equal(maximizedRestore.Rect, visible.Rect) &&
               form.WindowState == System.Windows.Forms.FormWindowState.Maximized,
            "Compensated Restore did not preserve Maximized state/visible restore bounds.");

        form.WindowState = System.Windows.Forms.FormWindowState.Normal;
        System.Windows.Forms.Application.DoEvents();
        runtime.SetCompensateDwmFrameEffects(false);
        var raw = Resizer.GetPlacement(form.Handle);
        WindowUtils.UpdateOrSaveWindowSize(form.Handle, ConfigFactory.Current, null);
        rule = ConfigFactory.Current.WindowSizes.First(row =>
            row.Name.Equals(processName, StringComparison.OrdinalIgnoreCase) && row.Title == form.Text);
        Assert(Equal(rule.Rect, raw.Rect), "Process Save OFF did not preserve raw geometry.");
        runtime.SaveAll();
        rule = ConfigFactory.Current.WindowSizes.First(row =>
            row.Name.Equals(processName, StringComparison.OrdinalIgnoreCase) && row.Title == form.Text);
        Assert(Equal(rule.Rect, raw.Rect),
            "Save All OFF did not use the same geometry as Save Active for the same HWND.");

        Assert(Resizer.MoveWindow(form.Handle, Offset(raw.Rect, 120, 80)),
            "Could not displace raw process fixture.");
        WindowUtils.ResizeWindow(form.Handle, ConfigFactory.Current, null, null);
        System.Windows.Forms.Application.DoEvents();
        Assert(Equal(Resizer.GetPlacement(form.Handle).Rect, raw.Rect),
            "Process Restore OFF did not preserve legacy raw geometry.");
        runtime.SetCompensateDwmFrameEffects(true);
    }

    private static WinRect Offset(WinRect rect, int x, int y) =>
        new(rect.Left + x, rect.Top + y, rect.Right + x, rect.Bottom + y);

    private static bool Equal(WinRect left, WinRect right) =>
        left.Left == right.Left && left.Top == right.Top &&
        left.Right == right.Right && left.Bottom == right.Bottom;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
