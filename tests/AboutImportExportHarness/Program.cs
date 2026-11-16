using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using WinResizer.Configuration;
using WinResizer.Runtime;
using WinResizer.Presentation;
using WinResizer.Services;
using WinResizer.Views;
using WinResizer.Core.Shortcuts;
using WinResizer.Common.Shortcuts;

internal static class Program
{
    private static int assertions;
    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-about-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(
            "pack://application:,,,/WinResizer;component/Styles/PrototypeLayout.xaml") });
        try
        {
            var active = Path.Combine(directory, "portable config.json");
            ConfigFactory.SetPortablePath(active); ConfigFactory.UseDefault(); ConfigFactory.Save();
            using var runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            { EnableWindowMonitoring = false, EnableGlobalHotkeys = false });
            runtime.Start();
            using var service = new ThemeService();
            using var theme = new ThemeViewModel(runtime, service, Dispatcher.CurrentDispatcher);
            var settings = new SettingsView { DataContext = new { ThemePreferences = theme } };
            var window = new Window { Content = settings, Width = 980, Height = 480, Opacity = 0, ShowInTaskbar = false };
            window.Show(); Drain();
            try
            {
                var text = Descendants<TextBlock>(settings).SelectMany(block => block.Inlines.OfType<Hyperlink>())
                    .SelectMany(link => link.Inlines.OfType<Run>()).Single().Text;
                Assert(text == active && settings.ConfigPath == ConfigFactory.ConfigPath, "ConfigPath binding is not the active path.");
                Assert(!Descendants<System.Windows.Controls.Primitives.ButtonBase>(settings)
                    .Any(button => Equals(button.Content, "Open Config Directory")), "Old config directory button remains.");
                Assert(((ComboBox)settings.FindName("ThemeComboBox")).Width == 130 &&
                    ((ComboBox)settings.FindName("ThemeComboBox")).Padding.Left == 16, "Theme layout/padding changed.");
                var link = Descendants<TextBlock>(settings).SelectMany(block => block.Inlines.OfType<Hyperlink>()).Single();
                Assert(link.Cursor == System.Windows.Input.Cursors.Hand &&
                    link.ReadLocalValue(Hyperlink.ForegroundProperty) != DependencyProperty.UnsetValue,
                    "Config link must keep a local foreground overriding hover triggers and a Hand cursor.");
                var info = Descendants<TextBlock>(settings).Single(block => block.Text.StartsWith("WinResizer configuration settings"));
                Assert(info.FontWeight == FontWeights.Normal && info.Foreground == app.Resources["TextFillColorPrimaryBrush"], "Config information text style invalid.");
                var themeGrid = (Grid)((ComboBox)settings.FindName("ThemeComboBox")).Parent;
                Assert(themeGrid.Parent is Grid, "Theme white panel remains.");
                Assert(!themeGrid.Children.OfType<TextBlock>().Any() &&
                    ((ComboBox)settings.FindName("ThemeComboBox")).HorizontalAlignment == HorizontalAlignment.Left,
                    "Theme label remains or ComboBox is not aligned left.");
                var startup = (CheckBox)settings.FindName("StartupCheckBox");
                var importButton = Descendants<System.Windows.Controls.Primitives.ButtonBase>(settings)
                    .Single(button => Equals(button.Content, "Import"));
                var left = ((ComboBox)settings.FindName("ThemeComboBox")).TranslatePoint(new Point(), settings).X;
                Assert(new FrameworkElement[] { startup, info, importButton }.All(element =>
                    Math.Abs(element.TranslatePoint(new Point(), settings).X - left) < 0.1),
                    "Settings controls do not share the same left edge.");
                var github = AboutActions.CreateGitHubStartInfo();
                var originalProject = AboutActions.CreateOriginalProjectStartInfo();
                Assert(github.FileName == "https://github.com/tesiek/WinResizer" && github.UseShellExecute, "GitHub target/start mode invalid.");
                Assert(originalProject.FileName == "https://github.com/caoyue/WindowResizer" && originalProject.UseShellExecute,
                    "Original project target/start mode invalid.");
                var explorer = AboutActions.CreateConfigStartInfo();
                Assert(Path.GetFileName(explorer.FileName) == "explorer.exe" && explorer.Arguments == "/select,\"" + active + "\"", "Explorer select arguments invalid.");
                var open = AboutActions.CreateImportDialog(); var save = AboutActions.CreateExportDialog();
                Assert(open.InitialDirectory == ConfigFactory.ConfigDirectory && save.InitialDirectory == ConfigFactory.ConfigDirectory,
                    "Dialog starting directories invalid.");
                Assert(open.Filter.Contains("*.json") && save.Filter.Contains("*.json") && save.FileName.EndsWith(".json"), "JSON dialogs invalid.");
                var exported = Path.Combine(directory, "export.json");
                var original = ConfigFactory.Current; var originalDisk = File.ReadAllText(active);
                AboutActions.Export(exported);
                Assert(JObject.Parse(File.ReadAllText(exported))["Configs"] != null && ReferenceEquals(original, ConfigFactory.Current)
                    && ConfigFactory.ConfigPath == active && File.ReadAllText(active) == originalDisk, "Export changed live/active configuration.");
                ExpectFailure(() => AboutActions.Export(active));
                Assert(File.ReadAllText(active) == originalDisk, "Export overwrote active config.");
                var document = JObject.Parse(File.ReadAllText(exported));
                document["Configs"]![0]!["ProfileName"] = "Imported"; document["ThemePreference"] = "Dark";
                File.WriteAllText(exported, document.ToString());
                var replaced = 0; ConfigFactory.ConfigurationReplaced += () => replaced++;
                var profilesChanged = 0; var hotkeysChanged = 0; var presetsChanged = 0;
                var processesChanged = 0; var ignoredChanged = 0; var themeChanged = 0;
                runtime.ProfilesChanged += (_, _) => profilesChanged++;
                runtime.HotkeysChanged += (_, _) => hotkeysChanged++;
                runtime.PresetsChanged += (_, _) => presetsChanged++;
                runtime.ProcessesChanged += (_, _) => processesChanged++;
                runtime.IgnoredWindowsChanged += (_, _) => ignoredChanged++;
                runtime.ThemePreferenceChanged += (_, _) => themeChanged++;
                var backupPath = AboutActions.Import(exported); Drain();
                Assert(ConfigFactory.ConfigPath == active && ConfigFactory.Current.ProfileName == "Imported"
                    && !ReferenceEquals(original, ConfigFactory.Current) && replaced == 1, "Import portable/live replacement invalid.");
                Assert(profilesChanged == 1 && hotkeysChanged == 1 && presetsChanged == 1 && processesChanged == 1
                    && ignoredChanged == 1 && themeChanged == 1, "Import did not publish all Runtime replacement events.");
                Assert(theme.SelectedTheme == "Dark" && Equals(((ComboBox)settings.FindName("ThemeComboBox")).SelectedItem, "Dark"), "Import did not refresh Theme UI.");
                Assert(File.ReadAllText(Path.Combine(directory, "WinResizer.config.pre-import.bak")) == originalDisk, "Import backup missing/invalid.");
                Assert(backupPath != null && File.Exists(backupPath) && File.ReadAllText(backupPath) == originalDisk,
                    "Import did not return its actual backup path.");
                Assert(AboutActions.ImportSuccessMessage(backupPath) == "Configuration was imported successfully." + Environment.NewLine +
                    Environment.NewLine + "The previous configuration was backed up to:" + Environment.NewLine + backupPath, "Import success message invalid.");
                var imported = ConfigFactory.Current; var importedDisk = File.ReadAllText(active);
                var invalid = Path.Combine(directory, "invalid.json"); File.WriteAllText(invalid, "not JSON");
                ExpectFailure(() => AboutActions.Import(invalid));
                Assert(ReferenceEquals(imported, ConfigFactory.Current) && File.ReadAllText(active) == importedDisk && replaced == 1,
                    "Invalid import changed live/disk state.");
                using (new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectFailure(() => AboutActions.Import(exported));
                Assert(ReferenceEquals(imported, ConfigFactory.Current) && File.ReadAllText(active) == importedDisk
                    && replaced == 1 && ConfigFactory.ConfigPath == active, "Persistence failure replaced live configuration.");
                ConfigFactory.Load();
                Assert(ConfigFactory.Current.ProfileName == "Imported" && runtime.GetThemePreference() == ThemePreference.Dark, "Imported state did not persist.");
                TestNativeReplacement(directory);
                File.Delete(active); ExpectFailure(() => AboutActions.CreateConfigStartInfo());
                TestAboutLayout();
                Console.WriteLine($"PASS: real Settings/About binding, launch targets, dialogs, portable Import/Export, backup, replacement and failure safety ({assertions} assertions).");
            }
            finally { window.Close(); }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { app.Shutdown(); Directory.Delete(directory, true); }
    }
    private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void TestAboutLayout()
    {
        var about = new AboutView();
        var window = new Window { Content = about, Width = 980, Height = 480, Opacity = 0, ShowInTaskbar = false };
        window.Show(); Drain();
        try
        {
            var icon = (Image)about.FindName("AboutIcon");
            var name = Descendants<TextBlock>(about).Single(text => text.Text == "WinResizer 1.1.0");
            var github = Descendants<System.Windows.Controls.Primitives.ButtonBase>(about)
                .Single(button => Equals(button.Content, "GitHub"));
            var originalProject = Descendants<System.Windows.Controls.Primitives.ButtonBase>(about)
                .Single(button => Equals(button.Content, "Original project"));
            var description = (TextBlock)about.FindName("AboutDescription");
            var attribution = (TextBlock)about.FindName("AboutAttribution");
            var platform = (TextBlock)about.FindName("AboutPlatform");
            Assert(name.Text == "WinResizer 1.1.0" && name.FontSize == 22 &&
                name.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue,
                "About product name/version styling changed.");
            Assert(icon.Source != null && icon.Width == 32 && icon.Height == 32,
                "About application icon is missing or incorrectly sized.");
            Assert(Math.Abs(icon.TranslatePoint(new Point(), about).X -
                            github.TranslatePoint(new Point(), about).X) < 0.1,
                "About icon and GitHub do not share the same left edge.");
            Assert(ReferenceEquals(github.Parent, originalProject.Parent) &&
                Math.Abs(github.TranslatePoint(new Point(), about).Y -
                         originalProject.TranslatePoint(new Point(), about).Y) < 0.1,
                "About links are not in one row.");
            Assert(description.Text == "Lightweight Windows utility for saving and restoring\nwindow positions and sizes." &&
                attribution.Text == "Based on WindowResizer by caoyue\nEnhanced with a redesigned UI and additional features." &&
                platform.Text == "Windows 10 / 11 · x64 · Portable\n.NET Framework 4.8 · WPF-UI 4.3.0\nMIT License",
                "About information text differs from the approved copy.");
            Assert(!Descendants<ComboBox>(about).Any() && !Descendants<CheckBox>(about).Any() &&
                   Descendants<System.Windows.Controls.Primitives.ButtonBase>(about).Count() == 2,
                "About controls differ from the two approved links.");
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.None,
                updateAccent: false);
            Drain();
            var lightThemeIcon = icon.Source;
            Assert(Equals(description.Foreground, Application.Current.Resources["TextFillColorPrimaryBrush"]) &&
                Equals(attribution.Foreground, Application.Current.Resources["TextFillColorSecondaryBrush"]) &&
                Equals(platform.Foreground, Application.Current.Resources["TextFillColorSecondaryBrush"]),
                "About text brushes do not match Light theme resources.");
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                Wpf.Ui.Appearance.ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.None,
                updateAccent: false);
            Drain();
            Assert(!ReferenceEquals(lightThemeIcon, icon.Source),
                "About icon did not follow the effective application theme.");
            Assert(Equals(description.Foreground, Application.Current.Resources["TextFillColorPrimaryBrush"]) &&
                Equals(attribution.Foreground, Application.Current.Resources["TextFillColorSecondaryBrush"]) &&
                Equals(platform.Foreground, Application.Current.Resources["TextFillColorSecondaryBrush"]),
                "About text brushes do not match Dark theme resources.");
        }
        finally { window.Close(); }
    }
    private static void TestNativeReplacement(string directory)
    {
        using (var exactProbe = new KeyboardHook())
        {
            try
            {
                exactProbe.RegisterHotKey(ModifierKeys.Ctrl, System.Windows.Forms.Keys.D1);
                Console.WriteLine("INFO: Ctrl+D1 is currently available before Import.");
            }
            catch (WinResizer.Common.Exceptions.HotkeyHookException)
            {
                var registered = RegisterHotKey(IntPtr.Zero, 0x6A01, 2, 0x31);
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (registered) UnregisterHotKey(IntPtr.Zero, 0x6A01);
                Console.WriteLine($"INFO: isolated Ctrl+D1 registration: success={registered}, Win32 error={error} (1409 = already registered).");
            }
        }
        var modifier = new System.Collections.Generic.HashSet<string> { "Ctrl", "Alt", "Shift" };
        ConfigFactory.Current.Keys.Clear();
        ConfigFactory.Current.Keys[HotkeysType.Save] = new Hotkeys { ModifierKeys = modifier, Key = "F23" };
        ConfigFactory.Current.Presets.Clear();
        ConfigFactory.Current.Presets.Add(new WindowPreset { Id = Guid.NewGuid().ToString("N"), Name = "Native",
            Enabled = true, Hotkey = new Hotkeys { ModifierKeys = modifier, Key = "F24" } });
        ConfigFactory.Save();
        var path = Path.Combine(directory, "native-identical.json"); ConfigFactory.Export(path);
        var errors = new System.Collections.Generic.List<string>();
        using (var native = new WinResizerRuntime(new WinResizerRuntimeOptions { EnableWindowMonitoring = false, Log = errors.Add }))
        {
            native.Start(); Assert(errors.Count == 0, "Native startup failed: " + string.Join(";", errors));
            using var competitor = new KeyboardHook();
            ExpectFailure(() => competitor.RegisterHotKey(ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift, System.Windows.Forms.Keys.F24));
            AboutActions.Import(path); AboutActions.Import(path);
            Assert(errors.Count == 0, "Identical native import hotkeys failed: " + string.Join(";", errors));
            ExpectFailure(() => competitor.RegisterHotKey(ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift, System.Windows.Forms.Keys.F24));
        }
        using var probe = new KeyboardHook();
        Assert(probe.RegisterHotKey(ModifierKeys.Ctrl | ModifierKeys.Alt | ModifierKeys.Shift, System.Windows.Forms.Keys.F24) > 0,
            "Native runtime left a preset hotkey registered after disposal.");
    }
    private static void ExpectFailure(Action action)
    {
        var failed = false; try { action(); } catch (Exception) { failed = true; }
        Assert(failed, "Expected operation to fail.");
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
