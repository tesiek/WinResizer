using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using WinResizer.Base;
using WinResizer.Common.Shortcuts;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;
using WinRect = WinResizer.Common.Windows.Rect;
using WinWindowState = WinResizer.Common.Windows.WindowState;

namespace ConfigCallbackFailureCharacterizationHarness;

internal static class Program
{
    private static int _assertions;
    private static readonly List<string> CallbackFailures = new();

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-ConfigCallbacks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ConfigFactory.SetCallbackFailureLogger(CallbackFailures.Add);
        try
        {
            VerifyPersistenceFailureIsDistinct(Path.Combine(root, "persistence"));
            VerifyResetBindingsFailureIsolation(Path.Combine(root, "reset-bindings"));
            VerifyConfigurationSavedFailureIsolation(Path.Combine(root, "configuration-saved"));
            VerifyReplacingFailureIsolation(Path.Combine(root, "replacing"));
            VerifyReplacedFailureIsolation(Path.Combine(root, "replaced"));
            VerifySaveActiveFailureIsolation(Path.Combine(root, "save-active"));
            VerifySaveAllFailureIsolation(Path.Combine(root, "save-all"));
            VerifyProfileAddFailureIsolation(Path.Combine(root, "profile-add"));
            VerifyProfileRemoveFailureIsolation(Path.Combine(root, "profile-remove"));
            VerifyProfileSwitchFailureIsolation(Path.Combine(root, "profile-switch"));
            VerifyLoggerFailureIsIsolated(Path.Combine(root, "logger-failure"));

            Console.WriteLine("PASS: persistence failure is a typed pre-commit failure and emits no post-save callback.");
            Console.WriteLine("PASS: ResetBindings and configuration event failures are isolated after commit.");
            Console.WriteLine("PASS: Save Active, Save All and profile operations remain successful after callback failures.");
            Console.WriteLine("PASS: import continues through replacing/replaced callback failures.");
            Console.WriteLine("PASS: callback logging is observable and logger failures are best effort.");
            Console.WriteLine($"PASS: config callback failure isolation ({_assertions} assertions; BUG CONFIRMED = 0).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            ConfigFactory.SetCallbackFailureLogger(null);
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static void VerifyPersistenceFailureIsDistinct(string directory)
    {
        var configPath = PrepareDefault(directory);
        CallbackFailures.Clear();
        var diskBefore = File.ReadAllText(configPath);
        var callbackCount = 0;
        ConfigFactory.ConfigurationSavedEvent callback = () => callbackCount++;
        ConfigFactory.ConfigurationSaved += callback;
        Exception? failure;
        try
        {
            ConfigFactory.Current.NotifyOnSaved = true;
            using var locked = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            failure = Capture(ConfigFactory.Save);
            Assert(File.ReadAllText(configPath) == diskBefore,
                "Persistence failure changed the disk file.");
        }
        finally
        {
            ConfigFactory.ConfigurationSaved -= callback;
        }

        Assert(failure is ConfigurationPersistenceException,
            "A true persistence failure did not return ConfigurationPersistenceException.");
        Assert(callbackCount == 0,
            "A post-save callback ran despite failed persistence.");
        Assert(CallbackFailures.Count == 0,
            "A callback failure was logged despite failed persistence and no post-commit callbacks.");
        Console.WriteLine("TRACE persistence failure: persist-start -> persistence-exception; no commit, reset or saved callback.");
    }

    private static void VerifyResetBindingsFailureIsolation(string directory)
    {
        var configPath = PrepareDefault(directory);
        CallbackFailures.Clear();
        var profile = ConfigFactory.Current;
        var trace = new List<string>();
        var presetsReset = 0;
        var configurationSaved = 0;
        ListChangedEventHandler first = (_, args) =>
        {
            if (args.ListChangedType == ListChangedType.Reset) trace.Add("window-reset-pass");
        };
        ListChangedEventHandler throwing = (_, args) =>
        {
            if (args.ListChangedType != ListChangedType.Reset) return;
            trace.Add("window-reset-throw");
            throw new ControlledCallbackException("WindowSizes ResetBindings");
        };
        ListChangedEventHandler tail = (_, args) =>
        {
            if (args.ListChangedType == ListChangedType.Reset) trace.Add("window-reset-tail");
        };
        ListChangedEventHandler preset = (_, args) =>
        {
            if (args.ListChangedType == ListChangedType.Reset) presetsReset++;
        };
        ConfigFactory.ConfigurationSavedEvent saved = () => configurationSaved++;

        profile.WindowSizes.ListChanged += first;
        profile.WindowSizes.ListChanged += throwing;
        profile.WindowSizes.ListChanged += tail;
        profile.Presets.ListChanged += preset;
        ConfigFactory.ConfigurationSaved += saved;
        Exception? failure;
        try
        {
            profile.NotifyOnSaved = true;
            failure = Capture(ConfigFactory.Save);
            trace.Add("call-returned");
        }
        finally
        {
            profile.WindowSizes.ListChanged -= first;
            profile.WindowSizes.ListChanged -= throwing;
            profile.WindowSizes.ListChanged -= tail;
            profile.Presets.ListChanged -= preset;
            ConfigFactory.ConfigurationSaved -= saved;
        }

        Assert(failure is null,
            "ResetBindings failure escaped after persistence commit.");
        Assert(ReadProfiles(configPath).Configs.Single().NotifyOnSaved && profile.NotifyOnSaved,
            "ResetBindings failure occurred before the disk/live change was committed.");
        Assert(trace.SequenceEqual(new[] { "window-reset-pass", "window-reset-throw", "call-returned" }),
            "ResetBindings isolation/order changed: " + string.Join(", ", trace));
        Assert(presetsReset == 1 && configurationSaved == 1,
            "Callbacks after the throwing WindowSizes ResetBindings handler did not continue.");
        AssertCallbackFailureLogged("WindowSizes.ResetBindings");
        Console.WriteLine("TRACE ResetBindings: persist-commit -> window-reset-pass -> window-reset-throw/log -> preset-reset -> ConfigurationSaved -> success.");
    }

    private static void VerifyConfigurationSavedFailureIsolation(string directory)
    {
        var configPath = PrepareDefault(directory);
        CallbackFailures.Clear();
        var profile = ConfigFactory.Current;
        var windowResets = 0;
        var presetResets = 0;
        ListChangedEventHandler windowReset = (_, args) =>
        {
            if (args.ListChangedType == ListChangedType.Reset) windowResets++;
        };
        ListChangedEventHandler presetReset = (_, args) =>
        {
            if (args.ListChangedType == ListChangedType.Reset) presetResets++;
        };
        profile.WindowSizes.ListChanged += windowReset;
        profile.Presets.ListChanged += presetReset;
        var callbacks = new ThrowingSavedSubscription();
        Exception? failure;
        try
        {
            profile.EnableResizeByTitle = false;
            failure = Capture(ConfigFactory.Save);
            callbacks.Trace.Add("call-returned");
        }
        finally
        {
            callbacks.Dispose();
            profile.WindowSizes.ListChanged -= windowReset;
            profile.Presets.ListChanged -= presetReset;
        }

        Assert(failure is null,
            "ConfigurationSaved failure escaped after persistence commit.");
        Assert(!ReadProfiles(configPath).Configs.Single().EnableResizeByTitle && !profile.EnableResizeByTitle,
            "ConfigurationSaved failure occurred before the disk/live change was committed.");
        Assert(windowResets == 1 && presetResets == 1,
            "ConfigurationSaved failure occurred before both ResetBindings calls.");
        Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
            "ConfigurationSaved subscribers were not isolated individually.");
        Assert(callbacks.Trace.SequenceEqual(new[] { "saved-pass", "saved-throw", "saved-tail", "call-returned" }),
            "ConfigurationSaved trace changed: " + string.Join(", ", callbacks.Trace));
        AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationSaved));
        Console.WriteLine("TRACE ConfigurationSaved: persist-commit -> resets -> saved-pass -> saved-throw/log -> saved-tail -> success.");
    }

    private static void VerifyReplacingFailureIsolation(string directory)
    {
        var configPath = PrepareDefault(directory);
        CallbackFailures.Clear();
        var oldProfile = ConfigFactory.Current;
        oldProfile.ProfileName = "Old Live";
        ConfigFactory.Save();
        var importPath = WriteImport(directory, "Imported Disk");
        var trace = new List<string>();
        var tailCount = 0;
        var replacedCount = 0;
        ConfigFactory.ConfigurationReplacingEvent pass = () => trace.Add("replacing-pass");
        ConfigFactory.ConfigurationReplacingEvent throwing = () =>
        {
            trace.Add("replacing-throw");
            throw new ControlledCallbackException("ConfigurationReplacing");
        };
        ConfigFactory.ConfigurationReplacingEvent tail = () => { tailCount++; trace.Add("replacing-tail"); };
        ConfigFactory.ConfigurationReplacedEvent replaced = () =>
        {
            replacedCount++;
            trace.Add("replaced");
        };
        ConfigFactory.ConfigurationReplacing += pass;
        ConfigFactory.ConfigurationReplacing += throwing;
        ConfigFactory.ConfigurationReplacing += tail;
        ConfigFactory.ConfigurationReplaced += replaced;
        Exception? failure;
        try
        {
            failure = Capture(() => ConfigFactory.Load(importPath));
            trace.Add("call-returned");
        }
        finally
        {
            ConfigFactory.ConfigurationReplacing -= pass;
            ConfigFactory.ConfigurationReplacing -= throwing;
            ConfigFactory.ConfigurationReplacing -= tail;
            ConfigFactory.ConfigurationReplaced -= replaced;
        }

        Assert(failure is null,
            "ConfigurationReplacing failure escaped after import persistence commit.");
        Assert(ReadProfiles(configPath).Configs.Single().ProfileName == "Imported Disk",
            "Import disk commit did not precede ConfigurationReplacing.");
        Assert(!ReferenceEquals(ConfigFactory.Current, oldProfile) && ConfigFactory.Current.ProfileName == "Imported Disk",
            "ConfigurationReplacing failure prevented live model replacement.");
        Assert(tailCount == 1 && replacedCount == 1 &&
               trace.SequenceEqual(new[]
                   { "replacing-pass", "replacing-throw", "replacing-tail", "replaced", "call-returned" }),
            "ConfigurationReplacing isolation/order changed.");
        AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationReplacing));
        Console.WriteLine("TRACE ConfigurationReplacing: import persist-commit -> replacing-pass -> replacing-throw/log -> replacing-tail -> live replace -> replaced -> success.");
    }

    private static void VerifyReplacedFailureIsolation(string directory)
    {
        var configPath = PrepareDefault(directory);
        CallbackFailures.Clear();
        ConfigFactory.Current.ProfileName = "Old";
        ConfigFactory.Save();
        var importPath = WriteImport(directory, "Imported Live");
        var trace = new List<string>();
        var tailCount = 0;
        ConfigFactory.ConfigurationReplacingEvent replacing = () => trace.Add("replacing");
        ConfigFactory.ConfigurationReplacedEvent pass = () => trace.Add("replaced-pass");
        ConfigFactory.ConfigurationReplacedEvent throwing = () =>
        {
            trace.Add("replaced-throw");
            throw new ControlledCallbackException("ConfigurationReplaced");
        };
        ConfigFactory.ConfigurationReplacedEvent tail = () => { tailCount++; trace.Add("replaced-tail"); };
        ConfigFactory.ConfigurationReplacing += replacing;
        ConfigFactory.ConfigurationReplaced += pass;
        ConfigFactory.ConfigurationReplaced += throwing;
        ConfigFactory.ConfigurationReplaced += tail;
        Exception? failure;
        try
        {
            failure = Capture(() => ConfigFactory.Load(importPath));
            trace.Add("call-returned");
        }
        finally
        {
            ConfigFactory.ConfigurationReplacing -= replacing;
            ConfigFactory.ConfigurationReplaced -= pass;
            ConfigFactory.ConfigurationReplaced -= throwing;
            ConfigFactory.ConfigurationReplaced -= tail;
        }

        Assert(failure is null,
            "ConfigurationReplaced failure escaped after import disk/live commit.");
        Assert(ReadProfiles(configPath).Configs.Single().ProfileName == "Imported Live" &&
               ConfigFactory.Current.ProfileName == "Imported Live",
            "ConfigurationReplaced failure did not occur after disk/live replacement.");
        Assert(tailCount == 1 && trace.SequenceEqual(new[]
            { "replacing", "replaced-pass", "replaced-throw", "replaced-tail", "call-returned" }),
            "ConfigurationReplaced isolation/order changed.");
        AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationReplaced));
        Console.WriteLine("TRACE ConfigurationReplaced: import persist-commit -> replacing -> live-replace -> replaced-pass -> replaced-throw/log -> replaced-tail -> success.");
    }

    private static void VerifySaveActiveFailureIsolation(string directory)
    {
        var configPath = PrepareGeometry(directory);
        CallbackFailures.Clear();
        using var form = CreateForm("Callback Save Active " + Guid.NewGuid().ToString("N"), 180, 140);
        WinResizerRuntime? runtime = null;
        ThrowingSavedSubscription? callbacks = null;
        try
        {
            RunWithForegroundForm(form, () =>
            {
                var processName = ProcessName(form);
                var original = new WinRect(20, 30, 420, 330);
                ConfigFactory.Current.WindowSizes.Add(NewRule(processName, form.Text, original));
                ConfigFactory.Current.WindowSizes.Add(NewRule(processName, "*", original));
                ConfigFactory.Save();

                runtime = NewRuntime();
                runtime.Start();
                var notifications = new List<RuntimeNotificationEventArgs>();
                runtime.NotificationRaised += (_, notification) => notifications.Add(notification);
                callbacks = new ThrowingSavedSubscription();
                var expected = Resizer.GetPlacement(form.Handle, false).Rect;
                Assert(!Equal(original, expected), "Save Active fixture did not differ from saved geometry.");

                var failure = Capture(runtime.Save);
                callbacks.Trace.Add("call-returned");
                var diskRule = ReadProfiles(configPath).Current!.WindowSizes.Single(rule => rule.Title == form.Text);
                var liveRule = ConfigFactory.Current.WindowSizes.Single(rule => rule.Title == form.Text);
                Assert(failure is null,
                    "Save Active propagated a post-commit callback failure.");
                Assert(Equal(diskRule.Rect, expected) && Equal(liveRule.Rect, expected),
                    "Save Active callback failure rolled back committed disk or live geometry.");
                Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
                    "Save Active ConfigurationSaved subscribers were not isolated.");
                Assert(notifications.Count == 1 &&
                       notifications[0].Level == RuntimeNotificationLevel.Success,
                    "Save Active did not emit success OSD after committed persistence.");
                AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationSaved));
                Console.WriteLine("TRACE Save Active: live geometry mutation -> persist-commit -> callback failure logged -> tail subscriber -> runtime reset -> success OSD.");
            });
        }
        finally
        {
            callbacks?.Dispose();
            runtime?.Dispose();
        }
    }

    private static void VerifySaveAllFailureIsolation(string directory)
    {
        var configPath = PrepareGeometry(directory);
        CallbackFailures.Clear();
        using var first = CreateForm("Callback Save All first " + Guid.NewGuid().ToString("N"), 240, 180);
        using var second = CreateForm("Callback Save All second " + Guid.NewGuid().ToString("N"), 720, 240);
        WinResizerRuntime? runtime = null;
        ThrowingSavedSubscription? callbacks = null;
        first.Show();
        second.Show();
        Application.DoEvents();
        try
        {
            var processName = ProcessName(first);
            var original = new WinRect(40, 50, 440, 350);
            ConfigFactory.Current.WindowSizes.Add(NewRule(processName, first.Text, original));
            ConfigFactory.Current.WindowSizes.Add(NewRule(processName, second.Text, original));
            ConfigFactory.Current.WindowSizes.Add(NewRule(processName, "*", original));
            ConfigFactory.Save();

            var saveAttempts = 0;
            runtime = NewRuntime(saveConfiguration: () =>
            {
                saveAttempts++;
                ConfigFactory.Save();
            });
            runtime.Start();
            var notifications = new List<RuntimeNotificationEventArgs>();
            runtime.NotificationRaised += (_, notification) => notifications.Add(notification);
            callbacks = new ThrowingSavedSubscription();

            Assert(Resizer.MoveWindow(first.Handle, new WinRect(120, 130, 540, 450), false),
                "Could not move first Save All fixture.");
            Assert(Resizer.MoveWindow(second.Handle, new WinRect(650, 300, 1070, 620), false),
                "Could not move second Save All fixture.");
            Application.DoEvents();
            var expectedFirst = Resizer.GetPlacement(first.Handle, false).Rect;
            var expectedSecond = Resizer.GetPlacement(second.Handle, false).Rect;

            var failure = Capture(runtime.SaveAll);
            callbacks.Trace.Add("call-returned");
            var disk = ReadProfiles(configPath).Current!;
            var diskFirst = disk.WindowSizes.Single(rule => rule.Title == first.Text);
            var diskSecond = disk.WindowSizes.Single(rule => rule.Title == second.Text);
            var liveFirst = ConfigFactory.Current.WindowSizes.Single(rule => rule.Title == first.Text);
            var liveSecond = ConfigFactory.Current.WindowSizes.Single(rule => rule.Title == second.Text);
            Assert(failure is null,
                "Save All propagated a post-commit callback failure.");
            Assert(saveAttempts == 1,
                "Save All callback failure did not use exactly one persistence attempt.");
            Assert(Equal(diskFirst.Rect, expectedFirst) && Equal(liveFirst.Rect, expectedFirst) &&
                   Equal(diskSecond.Rect, expectedSecond) && Equal(liveSecond.Rect, expectedSecond),
                "Save All callback failure rolled back committed disk or live geometry.");
            Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
                "Save All ConfigurationSaved subscribers were not isolated.");
            Assert(notifications.Count == 1 &&
                   notifications[0].Level == RuntimeNotificationLevel.Success,
                "Save All did not emit success OSD after committed persistence.");
            AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationSaved));
            Console.WriteLine("TRACE Save All: live batch mutation -> one persist-commit -> callback failure logged -> tail subscriber -> runtime reset -> success OSD.");
        }
        finally
        {
            callbacks?.Dispose();
            runtime?.Dispose();
            first.Close();
            second.Close();
            Application.DoEvents();
        }
    }

    private static void VerifyProfileAddFailureIsolation(string directory)
    {
        using var fixture = new ProfileFixture(directory);
        CallbackFailures.Clear();
        var originalProfiles = ConfigFactory.Profiles.Configs.ToList();
        var profilesChanged = 0;
        var addedEvents = 0;
        fixture.Runtime.ProfilesChanged += OnProfilesChanged;
        ConfigFactory.Profiles.ProfileEvents.ProfileAdd += OnAdded;
        using var callbacks = new ThrowingSavedSubscription();
        try
        {
            var failure = Capture(() => fixture.Runtime.AddProfile("Committed Add"));
            callbacks.Trace.Add("call-returned");
            var added = ConfigFactory.Profiles.Configs.Single(profile => profile.ProfileName == "Committed Add");
            Assert(failure is null,
                "Profile Add propagated a post-commit callback failure.");
            Assert(ReadProfiles(fixture.ConfigPath).Configs.Any(profile => profile.ProfileId == added.ProfileId),
                "Profile Add callback failure occurred before candidate disk commit.");
            Assert(ConfigFactory.Profiles.Configs.Count == originalProfiles.Count + 1 &&
                   ConfigFactory.Profiles.Configs.Take(originalProfiles.Count)
                       .Zip(originalProfiles, ReferenceEquals).All(equal => equal),
                "Profile Add callback failure left disk/live inconsistent or changed existing identity.");
            Assert(addedEvents == 1 && profilesChanged == 1,
                "Profile Add live success events did not run exactly once before callback failure.");
            Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
                "Profile Add ConfigurationSaved subscribers were not isolated.");
            Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
                   fixture.AutoResizeGeneration == fixture.InitialGeneration &&
                   fixture.Notifications.Count == 0,
                "Profile Add callback failure unexpectedly changed hotkeys, Auto Resize generation or OSD.");
            AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationSaved));
            Console.WriteLine("TRACE Profile Add: candidate persist-commit -> live append/events -> callback failure logged -> tail subscriber -> success.");
        }
        finally
        {
            fixture.Runtime.ProfilesChanged -= OnProfilesChanged;
            ConfigFactory.Profiles.ProfileEvents.ProfileAdd -= OnAdded;
        }

        void OnProfilesChanged(object? sender, EventArgs args) => profilesChanged++;
        void OnAdded(string id, string name) => addedEvents++;
    }

    private static void VerifyProfileRemoveFailureIsolation(string directory)
    {
        using var fixture = new ProfileFixture(directory);
        CallbackFailures.Clear();
        var profileB = fixture.B;
        var profilesChanged = 0;
        var removedEvents = 0;
        fixture.Runtime.ProfilesChanged += OnProfilesChanged;
        ConfigFactory.Profiles.ProfileEvents.ProfileRemove += OnRemoved;
        using var callbacks = new ThrowingSavedSubscription();
        try
        {
            var failure = Capture(() => fixture.Runtime.RemoveProfile(profileB.ProfileId));
            callbacks.Trace.Add("call-returned");
            Assert(failure is null,
                "Profile Remove propagated a post-commit callback failure.");
            Assert(ReadProfiles(fixture.ConfigPath).Configs.All(profile => profile.ProfileId != profileB.ProfileId) &&
                   ConfigFactory.Profiles.Configs.All(profile => !ReferenceEquals(profile, profileB)),
                "Profile Remove callback failure left the committed profile on disk or live.");
            Assert(removedEvents == 1 && profilesChanged == 1,
                "Profile Remove live success events did not run exactly once before callback failure.");
            Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
                "Profile Remove ConfigurationSaved subscribers were not isolated.");
            Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
                   fixture.AutoResizeGeneration == fixture.InitialGeneration &&
                   fixture.Notifications.Count == 0,
                "Profile Remove callback failure unexpectedly changed hotkeys, Auto Resize generation or OSD.");
            AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationSaved));
            Console.WriteLine("TRACE Profile Remove: candidate persist-commit -> live remove/events -> callback failure logged -> tail subscriber -> success.");
        }
        finally
        {
            fixture.Runtime.ProfilesChanged -= OnProfilesChanged;
            ConfigFactory.Profiles.ProfileEvents.ProfileRemove -= OnRemoved;
        }

        void OnProfilesChanged(object? sender, EventArgs args) => profilesChanged++;
        void OnRemoved(string id) => removedEvents++;
    }

    private static void VerifyProfileSwitchFailureIsolation(string directory)
    {
        using var fixture = new ProfileFixture(directory);
        CallbackFailures.Clear();
        var profilesChanged = 0;
        var hotkeysChanged = 0;
        var optionsChanged = 0;
        var presetsChanged = 0;
        var processesChanged = 0;
        var switching = 0;
        var switched = 0;
        fixture.Runtime.ProfilesChanged += (_, _) => profilesChanged++;
        fixture.Runtime.HotkeysChanged += (_, _) => hotkeysChanged++;
        fixture.Runtime.ProfileOptionsChanged += (_, _) => optionsChanged++;
        fixture.Runtime.PresetsChanged += (_, _) => presetsChanged++;
        fixture.Runtime.ProcessesChanged += (_, _) => processesChanged++;
        ConfigFactory.Profiles.ProfileEvents.ProfileSwitching += OnSwitching;
        ConfigFactory.Profiles.ProfileEvents.ProfileSwitch += OnSwitched;
        using var callbacks = new ThrowingSavedSubscription();
        try
        {
            var failure = Capture(() => fixture.Runtime.SwitchProfile(fixture.B.ProfileId));
            callbacks.Trace.Add("call-returned");
            Assert(failure is null,
                "Profile Switch propagated a post-commit callback failure.");
            Assert(ReadProfiles(fixture.ConfigPath).CurrentProfileId == fixture.B.ProfileId &&
                   ConfigFactory.Profiles.CurrentProfileId == fixture.B.ProfileId,
                "Profile Switch callback failure left disk/live active profiles inconsistent.");
            Assert(fixture.Runtime.CurrentAutoResizeSnapshot!.WindowSizes.Single().Name == "b.exe" &&
                   fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F3" &&
                   fixture.Registrar.ActiveKeys.Single() == "F3",
                "Profile Switch callback failure did not retain Profile B runtime state.");
            Assert(fixture.AutoResizeGeneration == fixture.InitialGeneration + 1,
                "Profile Switch callback failure did not invalidate Auto Resize exactly once.");
            Assert(fixture.Registrar.ReleaseAllCount == 1 && fixture.Registrar.RegisterCount == 1,
                "Profile Switch callback failure did not reload hotkeys exactly once.");
            Assert(switching == 1 && switched == 1 && profilesChanged == 1 && hotkeysChanged == 1 &&
                   optionsChanged == 1 && presetsChanged == 1 && processesChanged == 2,
                "Profile Switch success-side event counts changed before callback failure.");
            Assert(fixture.Notifications.Count == 1 &&
                   fixture.Notifications[0].Level == RuntimeNotificationLevel.Success &&
                   fixture.Notifications[0].Message.Contains("<B>"),
                "Profile Switch callback failure did not retain the already emitted success OSD.");
            Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
                "Profile Switch ConfigurationSaved subscribers were not isolated.");
            AssertCallbackFailureLogged(nameof(ConfigFactory.ConfigurationSaved));
            Console.WriteLine("TRACE Profile Switch: candidate persist-commit -> live/runtime B and side effects once -> callback failure logged -> tail subscriber -> success.");
        }
        finally
        {
            ConfigFactory.Profiles.ProfileEvents.ProfileSwitching -= OnSwitching;
            ConfigFactory.Profiles.ProfileEvents.ProfileSwitch -= OnSwitched;
        }

        void OnSwitching(string current, string next) => switching++;
        void OnSwitched(string id) => switched++;
    }

    private static void VerifyLoggerFailureIsIsolated(string directory)
    {
        PrepareDefault(directory);
        using var callbacks = new ThrowingSavedSubscription();
        ConfigFactory.SetCallbackFailureLogger(_ =>
            throw new ControlledCallbackException("Callback failure logger"));
        try
        {
            ConfigFactory.Current.NotifyOnSaved = true;
            var failure = Capture(ConfigFactory.Save);
            Assert(failure is null,
                "A callback failure logger exception escaped to the caller.");
            Assert(callbacks.PassCount == 1 && callbacks.ThrowCount == 1 && callbacks.TailCount == 1,
                "A callback failure logger exception stopped subscriber processing.");
        }
        finally
        {
            ConfigFactory.SetCallbackFailureLogger(CallbackFailures.Add);
        }

        Console.WriteLine("TRACE logger failure: callback throws -> logger throws/is isolated -> diagnostic fallback attempted -> tail subscriber -> success.");
    }

    private static void AssertCallbackFailureLogged(string stage)
    {
        Assert(CallbackFailures.Any(message =>
                message.IndexOf("during " + stage, StringComparison.Ordinal) >= 0 &&
                message.IndexOf(nameof(ControlledCallbackException), StringComparison.Ordinal) >= 0),
            $"No callback failure log identified stage {stage} and the controlled exception type.");
    }

    private static string PrepareDefault(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "WinResizer.config.json");
        ConfigFactory.SetPortablePath(path);
        ConfigFactory.UseDefault();
        ConfigFactory.Save();
        return path;
    }

    private static string PrepareGeometry(string directory)
    {
        var path = PrepareDefault(directory);
        ConfigFactory.Profiles.CompensateDwmFrameEffects = false;
        ConfigFactory.Current.EnableResizeByTitle = true;
        ConfigFactory.Current.NotifyOnSaved = true;
        return path;
    }

    private static string WriteImport(string directory, string profileName)
    {
        var profile = Config.NewConfig(profileName);
        var profiles = new Profiles(profile.ProfileId, new List<Config> { profile });
        var path = Path.Combine(directory, profileName.Replace(' ', '-') + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(profiles, Formatting.Indented));
        return path;
    }

    private static Profiles ReadProfiles(string path) =>
        JsonConvert.DeserializeObject<Profiles>(File.ReadAllText(path))
        ?? throw new InvalidOperationException("Could not read persisted profiles.");

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static WinResizerRuntime NewRuntime(Action? saveConfiguration = null) =>
        new(new WinResizerRuntimeOptions
        {
            EnableGlobalHotkeys = false,
            EnableWindowMonitoring = false,
            SaveConfiguration = saveConfiguration,
        });

    private static Form CreateForm(string title, int left, int top) => new()
    {
        Text = title,
        StartPosition = FormStartPosition.Manual,
        Location = new System.Drawing.Point(left, top),
        Size = new Size(420, 320),
        ShowInTaskbar = true,
        TopMost = true,
    };

    private static WindowSize NewRule(string processName, string title, WinRect rect) => new()
    {
        Name = processName,
        Title = title,
        Rect = rect,
        State = WinWindowState.Normal,
    };

    private static string ProcessName(Form form) =>
        Resizer.GetRealProcessName(form.Handle)
        ?? throw new InvalidOperationException("Could not resolve fixture process.");

    private static bool Equal(WinRect left, WinRect right) =>
        left.Left == right.Left && left.Top == right.Top &&
        left.Right == right.Right && left.Bottom == right.Bottom;

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
                    Assert(TryActivateForeground(form), "Fixture window did not become foreground.");
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
        if (failure is not null) throw failure;
    }

    private static bool TryActivateForeground(Form form)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread &&
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
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }

            Application.DoEvents();
            if (Resizer.GetForegroundHandle() == form.Handle) return true;
            Thread.Sleep(50);
        }

        return false;
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ThrowingSavedSubscription : IDisposable
    {
        private readonly ConfigFactory.ConfigurationSavedEvent _pass;
        private readonly ConfigFactory.ConfigurationSavedEvent _throw;
        private readonly ConfigFactory.ConfigurationSavedEvent _tail;

        public ThrowingSavedSubscription()
        {
            _pass = () => { PassCount++; Trace.Add("saved-pass"); };
            _throw = () =>
            {
                ThrowCount++;
                Trace.Add("saved-throw");
                throw new ControlledCallbackException("ConfigurationSaved");
            };
            _tail = () => { TailCount++; Trace.Add("saved-tail"); };
            ConfigFactory.ConfigurationSaved += _pass;
            ConfigFactory.ConfigurationSaved += _throw;
            ConfigFactory.ConfigurationSaved += _tail;
        }

        public List<string> Trace { get; } = new();
        public int PassCount { get; private set; }
        public int ThrowCount { get; private set; }
        public int TailCount { get; private set; }

        public void Dispose()
        {
            ConfigFactory.ConfigurationSaved -= _pass;
            ConfigFactory.ConfigurationSaved -= _throw;
            ConfigFactory.ConfigurationSaved -= _tail;
        }
    }

    private sealed class ProfileFixture : IDisposable
    {
        public ProfileFixture(string directory)
        {
            ConfigPath = PrepareDefault(directory);
            A = ConfigFactory.Current;
            Configure(A, "A", "F1", "a.exe");
            B = ConfigFactory.Profiles.Add("B");
            Configure(B, "B", "F3", "b.exe");
            ConfigFactory.Save();

            Registrar = new FakeHotkeyRegistrar();
            Runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                HotkeyRegistrar = Registrar,
                OwnsHotkeyRegistrar = false,
                EnableGlobalHotkeys = true,
                EnableWindowMonitoring = false,
            });
            Runtime.Start();
            Runtime.NotificationRaised += OnNotification;
            Registrar.ResetMetrics();
            InitialGeneration = AutoResizeGeneration;
        }

        public string ConfigPath { get; }
        public Config A { get; }
        public Config B { get; }
        public FakeHotkeyRegistrar Registrar { get; }
        public WinResizerRuntime Runtime { get; }
        public List<RuntimeNotificationEventArgs> Notifications { get; } = new();
        public long InitialGeneration { get; }

        public long AutoResizeGeneration
        {
            get
            {
                var coordinator = typeof(WinResizerRuntime)
                    .GetField("_autoResizeCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Runtime)!;
                return (long)coordinator.GetType()
                    .GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(coordinator)!;
            }
        }

        public void Dispose()
        {
            Runtime.NotificationRaised -= OnNotification;
            Runtime.Dispose();
            Registrar.Dispose();
        }

        private void OnNotification(object? sender, RuntimeNotificationEventArgs notification) =>
            Notifications.Add(notification);

        private static void Configure(Config profile, string name, string key, string process)
        {
            profile.ProfileName = name;
            profile.Keys.Clear();
            profile.SetKeys(HotkeysType.Save, new Hotkeys
            {
                Key = key,
                ModifierKeys = new HashSet<string> { "Ctrl", "Shift" },
            });
            profile.WindowSizes.Add(new WindowSize
            {
                Name = process,
                Title = "*",
                Rect = new WinRect(10, 20, 410, 320),
                AutoResize = true,
            });
        }
    }

    private sealed class FakeHotkeyRegistrar : IGlobalHotkeyRegistrar
    {
        private readonly Dictionary<int, string> _active = new();
        private EventHandler<KeyPressedEventArgs>? _keyPressed;
        private int _nextId;

        public event EventHandler<KeyPressedEventArgs>? KeyPressed
        {
            add => _keyPressed += value;
            remove => _keyPressed -= value;
        }

        public int RegisterCount { get; private set; }
        public int ReleaseAllCount { get; private set; }
        public IReadOnlyCollection<string> ActiveKeys => _active.Values.ToList().AsReadOnly();

        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            RegisterCount++;
            var id = ++_nextId;
            _active[id] = key.ToString();
            return id;
        }

        public void UnRegisterHotKey(int id) => _active.Remove(id);

        public void UnRegisterHotKey()
        {
            ReleaseAllCount++;
            _active.Clear();
        }

        public void ResetMetrics()
        {
            RegisterCount = 0;
            ReleaseAllCount = 0;
        }

        public void Dispose()
        {
            _keyPressed = null;
            _active.Clear();
        }
    }

    private sealed class ControlledCallbackException : Exception
    {
        public ControlledCallbackException(string callback) : base(callback + " callback failed.")
        {
        }
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
