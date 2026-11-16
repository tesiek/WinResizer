using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WinResizer.Core.Startup;
using WinResizer.Configuration;
using WinResizer.Presentation;
using WinResizer.Services;
using WinResizer.Views;

internal static class Program
{
    private static int _assertions;
    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Fail(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new InvalidOperationException("Expected registry failure.");
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Store();
            store.Values["AnotherApplication"] = "untouched";
            var exe = @"C:\Portable Apps\Żółć\WinResizer.exe";
            var service = new SystemStartupService(store, exe);
            foreach (var invalid in new[] { "WinResizer.exe", @"C:WinResizer.exe", @"\WinResizer.exe", "C:\\bad\"path.exe" })
            {
                try { _ = new SystemStartupService(store, invalid); throw new InvalidOperationException("Relative/invalid EXE accepted."); }
                catch (ArgumentException) { Assert(store.Writes == 0, "Path validation wrote registry."); }
            }
            Assert(!service.IsEnabled, "Missing value should be disabled.");
            service.RepairPathIfEnabled();
            Assert(store.Writes == 0 && !service.IsEnabled, "Repair created an absent entry.");
            service.Disable();
            Assert(store.Values["AnotherApplication"] == "untouched", "Disable changed another entry.");
            service.Enable();
            Assert(store.Read() == "\"" + exe + "\"" && service.IsEnabled, "Enable did not quote the actual executable.");
            var writes = store.Writes;
            service.RepairPathIfEnabled();
            Assert(store.Writes == writes, "Correct path was rewritten.");
            store.Values[RegistryStartupStore.ValueName] = "\"" + exe.ToUpperInvariant() + "\"";
            service.RepairPathIfEnabled();
            Assert(store.Writes == writes, "Case-only path change caused a write.");
            store.Values[RegistryStartupStore.ValueName] = @"C:\Old\WinResizer.exe";
            service.RepairPathIfEnabled();
            Assert(store.Read() == "\"" + exe + "\"" && store.Writes == writes + 1, "Stale path was not repaired.");
            service.Disable();
            service.Disable();
            Assert(!service.IsEnabled && store.Values.Count == 1, "Disable did not remove only WinResizer.");
            store.Values[RegistryStartupStore.ValueName] = "";
            Assert(service.IsEnabled, "Present empty legacy entry must remain logically enabled.");
            service.RepairPathIfEnabled();
            Assert(store.Read() == "\"" + exe + "\"", "Empty legacy entry was not repaired.");
            service.Disable();
            var model = new StartupViewModel(service);
            model.Refresh();
            Assert(!model.IsEnabled && model.IsAvailable, "Initial ViewModel state wrong.");
            var checkbox = new CheckBox { DataContext = model };
            checkbox.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(StartupViewModel.IsEnabled)) { Mode = BindingMode.OneWay });
            model.SetEnabled(true); Drain();
            Assert(model.IsEnabled && checkbox.IsChecked == true && service.IsEnabled, "Enable not reflected in WPF.");
            store.FailWrite = true;
            Fail(() => model.SetEnabled(true)); Drain();
            Assert(model.IsEnabled && checkbox.IsChecked == true, "Write failure lost old enabled state.");
            store.FailWrite = false;
            store.FailDelete = true;
            checkbox.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            Fail(() => model.SetEnabled(false)); Drain();
            Assert(model.IsEnabled && checkbox.IsChecked == true && service.IsEnabled, "Delete failure left false WPF state.");
            store.FailDelete = false;
            model.SetEnabled(false);
            store.FailWrite = true;
            checkbox.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            Fail(() => model.SetEnabled(true)); Drain();
            Assert(!model.IsEnabled && checkbox.IsChecked == false && !service.IsEnabled, "Enable failure left false WPF state.");
            store.FailRead = true;
            Fail(model.Refresh);
            Assert(!model.IsAvailable && !model.IsEnabled && !string.IsNullOrEmpty(model.ErrorMessage), "Read failure should disable an unavailable control.");
            store.FailRead = false; store.FailWrite = false;
            model.Refresh();
            Assert(model.IsAvailable && model.ErrorMessage is null, "Read recovery failed.");
            store.Values[RegistryStartupStore.ValueName] = "old";
            store.FailWrite = true;
            Fail(service.RepairPathIfEnabled);
            Assert(store.Read() == "old", "Repair failure changed old entry.");
            store.FailWrite = false;

            ConfigFactory.SetPortablePath(Path.Combine(root, "WinResizer.config.json"));
            ConfigFactory.UseDefault(); ConfigFactory.Save();
            var config = File.ReadAllText(ConfigFactory.ConfigPath);
            var saved = 0;
            ConfigFactory.ConfigurationSavedEvent onSave = () => saved++;
            ConfigFactory.ConfigurationSaved += onSave;
            try
            {
                model.SetEnabled(true); model.SetEnabled(false);
                Assert(saved == 0 && File.ReadAllText(ConfigFactory.ConfigPath) == config, "Startup wrote portable configuration.");
                var export = Path.Combine(root, "export.json");
                writes = store.Writes;
                var deletes = store.Deletes;
                AboutActions.Export(export);
                AboutActions.Import(export);
                Assert(store.Writes == writes && store.Deletes == deletes && !service.IsEnabled, "Import/Export changed system startup.");
                Assert(File.ReadAllText(export) == config && !config.Contains("RunOnSystemStartup"), "Export includes startup state.");
            }
            finally { ConfigFactory.ConfigurationSaved -= onSave; }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(
                "pack://application:,,,/WinResizer;component/Styles/PrototypeLayout.xaml") });
            var settings = new SettingsView(service);
            var window = new Window { Content = settings, Width = 980, Height = 650, Opacity = 0, ShowInTaskbar = false };
            window.Show(); Drain();
            try
            {
                var combo = (ComboBox)settings.FindName("ThemeComboBox");
                var startup = (CheckBox)settings.FindName("StartupCheckBox");
                Assert(combo.Width == 130 && combo.HorizontalAlignment == HorizontalAlignment.Left, "Theme sizing/alignment wrong.");
                Assert(!Descendants<TextBlock>(settings).Any(text => text.Text == "Theme" || text.Text == "Startup"), "Unwanted label remains.");
                var import = Descendants<System.Windows.Controls.Primitives.ButtonBase>(settings).Single(button => Equals(button.Content, "Import"));
                var info = Descendants<TextBlock>(settings).Single(text => text.Text.StartsWith("WinResizer configuration settings"));
                var left = combo.TranslatePoint(new Point(), settings).X;
                Assert(new FrameworkElement[] { combo, startup, import, info }.All(element => Math.Abs(element.TranslatePoint(new Point(), settings).X - left) < 0.1), "Settings controls do not share the same left edge.");
                Assert(Equals(startup.Content, "Run on system startup") && startup.IsChecked == false && startup.IsEnabled, "Startup control binding wrong.");
                Assert(Descendants<System.Windows.Controls.Primitives.ButtonBase>(settings).Any(button => Equals(button.Content, "Export")) && settings.ConfigPath == ConfigFactory.ConfigPath, "Settings actions regressed.");
                startup.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                startup.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain();
                Assert(service.IsEnabled && startup.IsChecked == true, "Real Settings click did not enable startup.");
                startup.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                startup.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain();
                Assert(!service.IsEnabled && startup.IsChecked == false, "Real Settings click did not disable startup.");
            }
            finally { window.Close(); app.Shutdown(); }
            Console.WriteLine($"PASS: {_assertions} assertions; isolated startup backend, stale path, WPF rollback/layout/clicks and config isolation. No user Run writes.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Store : IStartupStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public int Writes { get; private set; }
        public int Deletes { get; private set; }
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }
        public string? Read()
        {
            if (FailRead) throw new IOException("Simulated registry read failure.");
            return Values.TryGetValue(RegistryStartupStore.ValueName, out var value) ? value : null;
        }
        public void Write(string command)
        {
            if (FailWrite) throw new IOException("Simulated registry write failure.");
            Writes++; Values[RegistryStartupStore.ValueName] = command;
        }
        public void Delete()
        {
            if (FailDelete) throw new IOException("Simulated registry delete failure.");
            Deletes++; Values.Remove(RegistryStartupStore.ValueName);
        }
    }
}
