using System;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WinResizer.Common.Shortcuts;
using WinResizer.Views;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach (var resource in new[] { "PrototypeLayout", "DataGridStyles", "TabStyles" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/WinResizer;component/Styles/{resource}.xaml"),
                });

            var first = Record(
                new[] { (true, Forms.Keys.LControlKey), (true, Forms.Keys.A), (true, Forms.Keys.A),
                    (false, Forms.Keys.A), (false, Forms.Keys.LControlKey) });
            Assert(first.ToKeysString() == "Ctrl+A", "Auto-repeat or normal release produced the wrong hotkey.");
            var second = Record(
                new[] { (true, Forms.Keys.LMenu), (true, Forms.Keys.B),
                    (false, Forms.Keys.LMenu), (false, Forms.Keys.B) });
            Assert(second.ToKeysString() == "Alt+B", "Modifier-first release produced the wrong hotkey.");
            Assert(first.ToKeysString() == "Ctrl+A", "A later recording mutated the previous result.");
            var third = Record(
                new[] { (true, Forms.Keys.LShiftKey), (false, Forms.Keys.LShiftKey),
                    (true, Forms.Keys.C), (false, Forms.Keys.C),
                    (true, Forms.Keys.LControlKey), (true, Forms.Keys.LShiftKey), (true, Forms.Keys.D),
                    (false, Forms.Keys.D), (false, Forms.Keys.LShiftKey), (false, Forms.Keys.LControlKey) });
            Assert(third.Key == "D" && third.ModifierKeys.Count == 2 &&
                third.ModifierKeys.Contains("Ctrl") && third.ModifierKeys.Contains("Shift"),
                "Invalid modifier-only/key-only input or multiple modifiers failed.");
            app.Shutdown();
            Console.WriteLine($"PASS: current WPF hotkey recorder ({_assertions} assertions): repeat, release order, invalid attempts, and independent results.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Hotkeys Record((bool Down, Forms.Keys Key)[] sequence)
    {
        var dialog = new HotkeyCaptureDialog("Regression", "-") { Opacity = 0, ShowInTaskbar = false };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = dialog.GetType();
        var loaded = type.GetMethod("Dialog_Loaded", flags)!;
        dialog.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), dialog, loaded);
        var capturing = type.GetField("_capturing", flags)!;
        var down = type.GetMethod("HandleKeyDown", flags)!;
        var up = type.GetMethod("HandleKeyUp", flags)!;
        Exception? callbackFailure = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                capturing.SetValue(dialog, true);
                foreach (var input in sequence)
                {
                    (input.Down ? down : up).Invoke(dialog, new object[] { input.Key });
                    if (dialog.Result == HotkeyCaptureResult.Set) break;
                }
                Assert(dialog.Result == HotkeyCaptureResult.Set, "Recorder accepted no valid combination.");
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
                dialog.Close();
            }
        }));
        var accepted = dialog.ShowDialog();
        if (callbackFailure is not null) throw callbackFailure;
        Assert(accepted == true && dialog.ResultHotkey is not null, "Recorder did not commit its combination.");
        return dialog.ResultHotkey!;
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
