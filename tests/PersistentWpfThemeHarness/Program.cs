using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Services;
using WinResizer.Views;

internal static class Program
{
    private static int _assertions;
    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-theme-" + Guid.NewGuid().ToString("N"));
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
            var path = Path.Combine(directory, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(path);
            ConfigFactory.UseDefault();
            var aId = ConfigFactory.Current.ProfileId;
            var bId = ConfigFactory.ProfileAdd("B").ProfileId;
            foreach (var initial in new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark })
            {
                ConfigFactory.Profiles.ThemePreference = initial;
                ConfigFactory.Save();
                ConfigFactory.Load();
                var saves = 0;
                var fail = false;
                using (var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
                {
                    EnableWindowMonitoring = false, EnableGlobalHotkeys = false,
                    SaveConfiguration = () => { if (fail) throw new IOException("Injected Save failure"); saves++; ConfigFactory.Save(); },
                }))
                using (var service = new ThemeService())
                {
                    runtime.Start();
                    using (var model = new ThemeViewModel(runtime, service, Dispatcher.CurrentDispatcher))
                    {
                        Assert(model.SelectedTheme == initial.ToString() && service.SelectedTheme == initial.ToString(), "Startup ignored persisted Theme.");
                        var settings = new SettingsView { DataContext = new { ThemePreferences = model } };
                        var about = new AboutView();
                        var content = new StackPanel();
                        content.Children.Add(settings);
                        content.Children.Add(about);
                        var window = new Window { Content = content, Width = 800, Height = 500, ShowInTaskbar = false, Opacity = 0 };
                        try
                        {
                            window.Show();
                            service.Attach(window);
                            Drain();
                            var combo = (ComboBox)settings.FindName("ThemeComboBox");
                            var aboutIcon = (Image)about.FindName("AboutIcon");
                            Assert(Equals(combo.SelectedItem, initial.ToString()) && combo.Width == 130 && saves == 0, "Startup binding saved or changed layout.");
                            Assert(aboutIcon.Source != null, "About icon was not initialized from the effective application theme.");
                            object? lightThemeIcon = null;
                            foreach (var selected in new[] { "System", "Light", "Dark" })
                            {
                                var before = saves;
                                var changed = model.SelectedTheme != selected;
                                combo.SetCurrentValue(ComboBox.SelectedItemProperty, selected);
                                Drain();
                                Assert(model.SelectedTheme == selected && service.SelectedTheme == selected && runtime.GetThemePreference().ToString() == selected, "ComboBox did not apply Runtime Theme.");
                                Assert(saves == before + (changed ? 1 : 0), "Theme refresh caused extra Save.");
                                if (selected != "System")
                                    Assert(Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() ==
                                        (selected == "Dark" ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light), "ThemeService applied wrong explicit theme.");
                                if (selected == "Light") lightThemeIcon = aboutIcon.Source;
                                if (selected == "Dark") Assert(lightThemeIcon != null && !ReferenceEquals(lightThemeIcon, aboutIcon.Source),
                                    "About icon did not switch with the effective application theme.");
                            }
                            var beforeRefresh = saves;
                            runtime.SwitchProfile(bId); runtime.SwitchProfile(aId); Drain();
                            Assert(model.SelectedTheme == "Dark" && service.SelectedTheme == "Dark", "Profile switch changed global Theme.");
                            ConfigFactory.Load(); Drain();
                            Assert(model.SelectedTheme == "Dark" && Equals(combo.SelectedItem, "Dark"), "Reload lost Theme.");
                            model.Refresh(); Drain();
                            Assert(saves == beforeRefresh, "Reload/refresh saved configuration.");
                            var imported = JObject.Parse(File.ReadAllText(path));
                            imported["ThemePreference"] = "Light";
                            var importPath = Path.Combine(directory, "import.json");
                            File.WriteAllText(importPath, imported.ToString());
                            Task.Run(() => ConfigFactory.Load(importPath)).GetAwaiter().GetResult(); Drain();
                            Assert(model.SelectedTheme == "Light" && service.SelectedTheme == "Light" && Equals(combo.SelectedItem, "Light") && saves == beforeRefresh, $"Import sync: runtime={runtime.GetThemePreference()}, model={model.SelectedTheme}, service={service.SelectedTheme}, combo={combo.SelectedItem}, saves={saves}/{beforeRefresh}.");
                            var rollbackCombo = new ComboBox { ItemsSource = model.ThemeOptions };
                            System.Windows.Data.BindingOperations.SetBinding(rollbackCombo, ComboBox.SelectedItemProperty,
                                new System.Windows.Data.Binding(nameof(ThemeViewModel.SelectedTheme))
                                { Source = model, Mode = System.Windows.Data.BindingMode.OneWay });
                            rollbackCombo.SetCurrentValue(ComboBox.SelectedItemProperty, "Dark");
                            fail = true;
                            try { model.SetTheme("Dark"); throw new Exception("Expected persistence failure."); }
                            catch (IOException) { }
                            finally { fail = false; }
                            Drain();
                            Assert(runtime.GetThemePreference() == ThemePreference.Light && model.SelectedTheme == "Light" && service.SelectedTheme == "Light" && Equals(combo.SelectedItem, "Light") && Equals(rollbackCombo.SelectedItem, "Light"), "Failure left inconsistent Runtime/UI/service.");
                            model.SetTheme("Dark"); Drain();
                        }
                        finally { window.Close(); }
                    }
                }
                ConfigFactory.Load();
                using (var restarted = new WinResizerRuntime(new WinResizerRuntimeOptions { EnableWindowMonitoring = false, EnableGlobalHotkeys = false }))
                using (var service = new ThemeService())
                {
                    restarted.Start();
                    using (var model = new ThemeViewModel(restarted, service, Dispatcher.CurrentDispatcher))
                        Assert(model.SelectedTheme == "Dark" && service.SelectedTheme == "Dark", "Restart did not restore last selection.");
                }
            }
            Console.WriteLine($"PASS: persistent WPF Theme startup, selection, scope, replacement, rollback and restart ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { app.Shutdown(); Directory.Delete(directory, true); }
    }
    private static void Assert(bool condition, string message)
    { _assertions++; if (!condition) throw new Exception(message); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
