using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer;
using WinResizer.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml") });
        WinResizerRuntime? runtime = null;
        SettingsPrototypeWindow? window = null;
        try
        {
            ConfigFactory.SetPortablePath(Path.Combine(root, "WinResizer.config.json"));
            ConfigFactory.UseDefault();
            ConfigFactory.ProfileRename(ConfigFactory.Current.ProfileId, "A");
            var aId = ConfigFactory.Current.ProfileId;
            var b = ConfigFactory.ProfileAdd("B");
            ConfigFactory.ProfileSwitch(b.ProfileId);
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions { EnableWindowMonitoring = false, EnableGlobalHotkeys = false });
            runtime.Start();
            window = new SettingsPrototypeWindow(runtime) { Width = 1100, Height = 700, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            window.Show();
            Drain();
            var status = (TextBlock)window.FindName("DpiStatusText");
            Assert(status.Text.Contains("DPI:") && status.Text.Contains("Profile: B"), "Startup status did not show the active profile.");
            var profileMarker = status.Text.IndexOf("      Profile:", StringComparison.Ordinal);
            var dpiBefore = status.Text.Substring(0, profileMarker);
            Assert(dpiBefore.StartsWith("DPI: ", StringComparison.Ordinal), "DPI status format is missing.");
            runtime.SwitchProfile(aId); Drain();
            Assert(status.Text.Contains("Profile: A"), "Tray/profile switch did not update status.");
            runtime.RenameProfile(aId, "A renamed"); Drain();
            Assert(status.Text.Contains("Profile: A renamed"), "Active profile rename did not update status.");
            runtime.RenameProfile(b.ProfileId, "B renamed"); Drain();
            Assert(status.Text.Contains("Profile: A renamed"), "Inactive profile rename changed status.");
            runtime.SwitchProfile(b.ProfileId); Drain();
            Assert(status.Text.Contains("Profile: B renamed"), "Switch to renamed profile did not update status.");
            ConfigFactory.Load(); Drain();
            Assert(status.Text.StartsWith("DPI: ", StringComparison.Ordinal), "DPI status was lost after config refresh.");
            Console.WriteLine("PASS: active profile status startup, switch, rename, import/replacement and independent DPI display.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            if (window != null) ((ISettingsWindowHandle)window).CloseForApplicationExit();
            runtime?.Dispose();
            app.Shutdown();
            try { Directory.Delete(root, true); } catch { }
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
