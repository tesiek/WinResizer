using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WinResizer.Common.Shortcuts;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Runtime;
using CommonHotkeys = WinResizer.Common.Shortcuts.Hotkeys;

namespace Stage4B1RuntimeHarness;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "WinResizer-stage4b1-notifications-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        WinResizerRuntime? runtime = null;
        Form? form = null;
        try
        {
            ConfigFactory.SetPortablePath(Path.Combine(temporaryRoot, "WinResizer.config.json"));
            ConfigFactory.UseDefault();
            ConfigFactory.Current.NotifyOnSaved = true;
            var reloadProfile = ConfigFactory.ProfileAdd("Notification Reload");
            reloadProfile.NotifyOnSaved = true;
            ConfigFactory.Save();

            var registrar = new FakeHotkeyRegistrar();
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = registrar,
                OwnsHotkeyRegistrar = false,
                EnableWindowMonitoring = false,
            });
            runtime.Start();

            var notifications = new List<RuntimeNotificationEventArgs>();
            runtime.NotificationRaised += (_, notification) => notifications.Add(notification);

            form = new Form
            {
                Text = "WinResizer Stage 4B.1 notification test",
                StartPosition = FormStartPosition.Manual,
                Location = new Point(100, 100),
                Size = new Size(640, 480),
                ShowInTaskbar = true,
                TopMost = true,
            };
            var activeRuntime = runtime;
            RunWithForegroundForm(form, () =>
            {
                activeRuntime.Save();
                Assert(notifications.Count == 1, "NotifyOnSaved=true did not emit exactly one Save notification.");
                Assert(notifications[0].Title == "Config Saved" &&
                       notifications[0].Level == RuntimeNotificationLevel.Success &&
                       notifications[0].OpenProcessSettings,
                    "The Save notification did not preserve production metadata.");

                activeRuntime.SetHotkey(
                    HotkeysType.Save,
                    NewHotkey("F6", "Ctrl", "Shift"));
                activeRuntime.Save();
                Assert(notifications.Count == 2,
                    "Changing the Save hotkey disabled or duplicated the notification path.");

                Assert(activeRuntime.SwitchProfile(reloadProfile.ProfileId),
                    "Profile switch for the hotkey reload notification test failed.");
                notifications.Clear();
                activeRuntime.Save();
                Assert(notifications.Count == 1,
                    "Hotkey reload after a profile switch disabled or duplicated the notification path.");

                activeRuntime.SetNotifyOnSaved(false);
                Assert(!ConfigFactory.Current.NotifyOnSaved, "SetNotifyOnSaved(false) did not update the live config.");
                activeRuntime.Save();
                Assert(notifications.Count == 1, "NotifyOnSaved=false still emitted a Save notification.");
            });

            Console.WriteLine(
                "PASS: successful foreground Save emits one RuntimeNotification when enabled, none when disabled, " +
                "and survives hotkey replacement and profile hotkey reload.");
            return 0;
        }
        finally
        {
            form?.Close();
            form?.Dispose();
            runtime?.Dispose();
            try
            {
                Directory.Delete(temporaryRoot, true);
            }
            catch
            {
            }
        }
    }

    private static bool ResizerHandleMatches(Form form) =>
        WinResizer.Core.WindowControl.Resizer.GetForegroundHandle() == form.Handle;

    private static void RunWithForegroundForm(Form form, Action action)
    {
        Exception? failure = null;
        EventHandler? shown = null;
        shown = (_, _) =>
        {
            form.Shown -= shown;
            form.BeginInvoke(new Action(() =>
            {
                try
                {
                    Assert(TryActivateForeground(form), "The test window did not become the foreground window.");
                    action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    form.Close();
                }
            }));
        };

        form.Shown += shown;
        form.ShowDialog();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static bool TryActivateForeground(Form form)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 &&
                           foregroundThread != currentThread &&
                           AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                ShowWindow(form.Handle, 5);
                BringWindowToTop(form.Handle);
                SwitchToThisWindow(form.Handle, true);
                SetActiveWindow(form.Handle);
                SetFocus(form.Handle);
                SetForegroundWindow(form.Handle);
                form.BringToFront();
                form.Activate();
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            Application.DoEvents();
            if (ResizerHandleMatches(form))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static CommonHotkeys NewHotkey(string key, params string[] modifiers) => new CommonHotkeys
    {
        ModifierKeys = new HashSet<string>(modifiers),
        Key = key,
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        private int _nextId;
        private EventHandler<KeyPressedEventArgs>? _keyPressed;
        private readonly Dictionary<int, (ModifierKeys Modifier, Keys Key)> _registered =
            new Dictionary<int, (ModifierKeys Modifier, Keys Key)>();

        public event EventHandler<KeyPressedEventArgs>? KeyPressed
        {
            add => _keyPressed += value;
            remove => _keyPressed -= value;
        }

        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            if (_registered.ContainsValue((modifier, key)))
            {
                throw new InvalidOperationException("The hotkey is already registered.");
            }

            var id = ++_nextId;
            _registered.Add(id, (modifier, key));
            return id;
        }

        public void UnRegisterHotKey(int id) => _registered.Remove(id);

        public void UnRegisterHotKey() => _registered.Clear();

        public void Dispose() => _registered.Clear();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
