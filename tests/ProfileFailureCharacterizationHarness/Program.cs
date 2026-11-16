using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using WinResizer.Common.Shortcuts;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;
using WinResizer.Presentation;
using WinResizer.Runtime;

namespace ProfileFailureCharacterizationHarness;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinResizer-ProfileFailure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyAddTransaction(Path.Combine(root, "add"));
            VerifyRemoveTransaction(Path.Combine(root, "remove"));
            VerifyThrowingProfileAddIsolated(Path.Combine(root, "add-callback"));
            VerifyThrowingProfileRemoveIsolated(Path.Combine(root, "remove-callback"));
            VerifyThrowingProfileRenameIsolated(Path.Combine(root, "rename-callback"));
            VerifyRemoveProtections(Path.Combine(root, "remove-protections"));
            VerifySwitchFailure(Path.Combine(root, "switch"));
            VerifyThrowingProfileSwitchingIsolated(Path.Combine(root, "switching-callback"));
            VerifyThrowingProfileSwitchIsolated(Path.Combine(root, "switch-callback"));
            VerifyGetCurrentConfigFallback(Path.Combine(root, "current-fallback"));
            VerifyGetCurrentConfigFallbackPersistenceFailure(Path.Combine(root, "current-fallback-failure"));
            VerifyValidCurrentRead(Path.Combine(root, "current-read"));
            VerifySuccessfulRoundTrip(Path.Combine(root, "success"));

            Console.WriteLine("PASS: Profile Add persists before live/UI mutation and emits success events exactly once.");
            Console.WriteLine("PASS: Profile Remove persists before live/UI mutation and preserves order/reference identity on failure.");
            Console.WriteLine("PASS: throwing ProfileAdd/ProfileRemove subscribers are isolated; later subscribers and ConfigurationSaved still run.");
            Console.WriteLine("PASS: throwing ProfileRename subscriber is isolated after ConfigurationSaved; later subscribers still run.");
            Console.WriteLine("PASS: Profile Switch persistence failure leaves live state, hotkeys, Auto Resize, UI and OSD unchanged.");
            Console.WriteLine("PASS: throwing ProfileSwitching subscriber is isolated; later subscribers run and disk/live/runtime switch to B.");
            Console.WriteLine("PASS: throwing ProfileSwitch subscriber is isolated; later subscribers and ConfigurationSaved still run.");
            Console.WriteLine("PASS: Current fallback persists one switch and isolates a throwing subscriber without duplicate runtime effects.");
            Console.WriteLine("PASS: Current fallback persistence failure has no live/runtime side effects; a valid Current read has no side effects.");
            Console.WriteLine("PASS: active-profile and last-profile removal protections remain effective.");
            Console.WriteLine("PASS: normal A -> B -> A switching persists first and synchronizes runtime state exactly once.");
            Console.WriteLine("NOT TESTABLE AUTOMATICALLY: a real tray menu was not instantiated; its ProfilesChanged input was observed instead.");
            Console.WriteLine($"PASS: transactional profile operations ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static void VerifyAddTransaction(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profilesList = ConfigFactory.Profiles.Configs;
        var originalProfiles = profilesList.ToList();
        var originalIds = originalProfiles.Select(profile => profile.ProfileId).ToList();
        var originalActiveId = ConfigFactory.Profiles.CurrentProfileId;
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        fixture.ResetObservation();

        var failed = false;
        using (new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                fixture.Runtime.AddProfile("Failed Add");
            }
            catch (ConfigurationPersistenceException)
            {
                failed = true;
            }

            Assert(File.ReadAllText(fixture.ConfigPath) == originalDisk,
                "Profile Add failure changed the config on disk.");
        }

        DrainDispatcher();
        Assert(failed, "Profile Add did not surface persistence failure.");
        Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs),
            "Profile Add replaced the profiles list instance.");
        Assert(profilesList.Count == originalProfiles.Count &&
               profilesList.Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal),
            "Profile Add failure changed live profile order or references.");
        Assert(profilesList.Select(profile => profile.ProfileId).SequenceEqual(originalIds) &&
               profilesList.All(profile => profile.ProfileName != "Failed Add"),
            "Profile Add failure left a ghost profile in live RAM.");
        Assert(ConfigFactory.Profiles.CurrentProfileId == originalActiveId &&
               ReferenceEquals(ConfigFactory.Current, fixture.A),
            "Profile Add failure changed the active profile.");
        Assert(fixture.Recorder.ProfileAdded == 0 && fixture.Recorder.ProfilesChanged == 0 &&
               fixture.Recorder.ConfigurationSaved == 0,
            "Profile Add failure emitted success events.");
        Assert(fixture.Recorder.Timeline.Count == 0,
            "Profile Add failure produced pre-persistence side effects.");
        Assert(fixture.ViewModel.Profiles.Select(profile => profile.ProfileId).SequenceEqual(originalIds),
            "Profile Add failure changed the profile UI state.");
        Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
               fixture.Registrar.ActiveKeys.Single() == "F1",
            "Profile Add failure unexpectedly reloaded hotkeys.");
        Assert(fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe",
            "Profile Add failure unexpectedly invalidated or switched Auto Resize state.");
        Assert(fixture.Recorder.Notifications.Count == 0,
            "Profile Add failure emitted an OSD notification.");

        fixture.ResetObservation();
        var addedInfo = fixture.Runtime.AddProfile("Added");
        DrainDispatcher();
        var added = profilesList.Single(profile => profile.ProfileId == addedInfo.Id);
        Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs),
            "Successful Profile Add replaced the profiles list instance.");
        Assert(profilesList.Count == originalProfiles.Count + 1 &&
               profilesList.Take(originalProfiles.Count)
                   .Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal) &&
               ReferenceEquals(profilesList[profilesList.Count - 1], added),
            "Successful Profile Add changed existing identity/order or did not append the new object.");
        Assert(!string.IsNullOrWhiteSpace(added.ProfileId) && added.ProfileName == "Added" &&
               originalProfiles.All(profile => profile.ProfileId != added.ProfileId),
            "Successful Profile Add did not preserve the generated profile identity.");
        Assert(fixture.ReadDiskProfileIds().SequenceEqual(profilesList.Select(profile => profile.ProfileId)),
            "Successful Profile Add did not persist the live profile order.");
        Assert(ConfigFactory.Profiles.CurrentProfileId == originalActiveId &&
               fixture.ReadDiskCurrentProfileId() == originalActiveId,
            "Successful Profile Add changed the active profile.");
        Assert(fixture.Recorder.ProfileAdded == 1 && fixture.Recorder.ProfilesChanged == 1 &&
               fixture.Recorder.ConfigurationSaved == 1,
            "Successful Profile Add did not emit each expected event exactly once.");
        AssertOrder(fixture.Recorder.Timeline, "profiles", "add", "saved");
        Assert(fixture.ViewModel.Profiles.Any(profile => profile.ProfileId == added.ProfileId),
            "Successful Profile Add did not refresh the profile UI.");
        Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
               fixture.Registrar.ActiveKeys.Single() == "F1",
            "Successful Profile Add unexpectedly reloaded hotkeys.");
        Assert(fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe",
            "Successful Profile Add unexpectedly invalidated or switched Auto Resize state.");
        Assert(fixture.Recorder.Notifications.Count == 0,
            "Successful Profile Add emitted an OSD notification.");
    }

    private static void VerifyRemoveTransaction(string directory)
    {
        using var fixture = new Fixture(directory, "B", "C");
        var profilesList = ConfigFactory.Profiles.Configs;
        var originalProfiles = profilesList.ToList();
        var profileB = fixture.Profile("B");
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        fixture.ResetObservation();

        Assert(!fixture.Runtime.RemoveProfile(fixture.A.ProfileId),
            "Runtime allowed removal of the active profile.");
        Assert(fixture.Recorder.Timeline.Count == 0,
            "Rejected active-profile removal emitted events.");

        var failed = false;
        using (new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                fixture.Runtime.RemoveProfile(profileB.ProfileId);
            }
            catch (ConfigurationPersistenceException)
            {
                failed = true;
            }

            Assert(File.ReadAllText(fixture.ConfigPath) == originalDisk,
                "Profile Remove failure changed the config on disk.");
        }

        DrainDispatcher();
        Assert(failed, "Profile Remove did not surface persistence failure.");
        Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs),
            "Profile Remove replaced the profiles list instance.");
        Assert(profilesList.Count == originalProfiles.Count &&
               profilesList.Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal),
            "Profile Remove failure changed live profile order or identity.");
        Assert(ReferenceEquals(profilesList[originalProfiles.IndexOf(profileB)], profileB),
            "Profile Remove failure did not preserve the target object at its original index.");
        Assert(ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
               ReferenceEquals(ConfigFactory.Current, fixture.A),
            "Inactive Profile Remove failure changed the active profile.");
        Assert(fixture.Recorder.ProfileRemoved == 0 && fixture.Recorder.ProfilesChanged == 0 &&
               fixture.Recorder.ConfigurationSaved == 0,
            "Profile Remove failure emitted success events.");
        Assert(fixture.Recorder.Timeline.Count == 0,
            "Profile Remove failure produced pre-persistence side effects.");
        Assert(fixture.ViewModel.Profiles.Any(profile => profile.ProfileId == profileB.ProfileId),
            "Profile Remove failure changed the profile UI state.");
        Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
               fixture.Registrar.ActiveKeys.Single() == "F1",
            "Profile Remove failure unexpectedly reloaded hotkeys.");
        Assert(fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe",
            "Profile Remove failure unexpectedly invalidated or switched Auto Resize state.");
        Assert(fixture.Recorder.Notifications.Count == 0,
            "Profile Remove failure emitted an OSD notification.");

        fixture.ResetObservation();
        Assert(fixture.Runtime.RemoveProfile(profileB.ProfileId),
            "Successful Profile Remove rejected the inactive profile.");
        DrainDispatcher();
        var expectedSurvivors = originalProfiles.Where(profile => !ReferenceEquals(profile, profileB)).ToList();
        Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs) &&
               profilesList.Count == expectedSurvivors.Count &&
               profilesList.Zip(expectedSurvivors, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal),
            "Successful Profile Remove changed remaining order/references or list identity.");
        Assert(!profilesList.Contains(profileB) &&
               fixture.ReadDiskProfileIds().SequenceEqual(expectedSurvivors.Select(profile => profile.ProfileId)),
            "Successful Profile Remove did not remove the exact profile from disk and live RAM.");
        Assert(ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
               fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId,
            "Successful inactive Profile Remove changed the active profile.");
        Assert(fixture.Recorder.ProfileRemoved == 1 && fixture.Recorder.ProfilesChanged == 1 &&
               fixture.Recorder.ConfigurationSaved == 1,
            "Successful Profile Remove did not emit each expected event exactly once.");
        AssertOrder(fixture.Recorder.Timeline, "profiles", "remove", "saved");
        Assert(fixture.ViewModel.Profiles.All(profile => profile.ProfileId != profileB.ProfileId),
            "Successful Profile Remove did not refresh the profile UI.");
        Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
               fixture.Registrar.ActiveKeys.Single() == "F1",
            "Successful Profile Remove unexpectedly reloaded hotkeys.");
        Assert(fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe",
            "Successful inactive Profile Remove unexpectedly invalidated or switched Auto Resize state.");
        Assert(fixture.Recorder.Notifications.Count == 0,
            "Successful Profile Remove emitted an OSD notification.");
    }

    private static void VerifyThrowingProfileAddIsolated(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var originalIds = fixture.ReadDiskProfileIds();
        var generation = fixture.AutoResizeGeneration;
        var trace = new List<string>();
        var injected = new InvalidOperationException("Injected ProfileAdd failure (F4F).");
        var callbackFailures = new List<string>();
        var events = ConfigFactory.Profiles.ProfileEvents;
        ProfileEvents.ProfileAddEvent first = (_, _) => trace.Add("first");
        ProfileEvents.ProfileAddEvent throwing = (_, _) =>
        {
            trace.Add("throwing");
            throw injected;
        };
        ProfileEvents.ProfileAddEvent last = (_, _) => trace.Add("last");
        ConfigFactory.ConfigurationSavedEvent saved = () => trace.Add("saved");

        fixture.ResetObservation();
        ConfigFactory.SetCallbackFailureLogger(callbackFailures.Add);
        events.ProfileAdd += first;
        events.ProfileAdd += throwing;
        events.ProfileAdd += last;
        ConfigFactory.ConfigurationSaved += saved;
        try
        {
            var addedInfo = fixture.Runtime.AddProfile("Callback Add");

            DrainDispatcher();
            var added = ConfigFactory.Profiles.Configs.Single(profile => profile.ProfileName == "Callback Add");
            Assert(addedInfo.Id == added.ProfileId &&
                   trace.SequenceEqual(new[] { "first", "throwing", "last", "saved" }),
                "F4F ProfileAdd did not invoke the tail subscriber and ConfigurationSaved before returning.");
            Assert(callbackFailures.Count == 1 &&
                   callbackFailures[0].Contains("during ProfileAdd") &&
                   callbackFailures[0].Contains(injected.Message),
                "F4F ProfileAdd did not log the isolated callback failure exactly once.");
            Assert(File.ReadAllText(fixture.ConfigPath) != originalDisk &&
                   fixture.ReadDiskProfileIds().SequenceEqual(originalIds.Concat(new[] { added.ProfileId })) &&
                   fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId,
                "F4F ProfileAdd did not commit the new profile to disk before the callback.");
            Assert(ReferenceEquals(ConfigFactory.Profiles.Configs.Last(), added) &&
                   ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
                   ReferenceEquals(ConfigFactory.Current, fixture.A),
                "F4F ProfileAdd did not leave disk and live state consistently committed.");
            Assert(fixture.Recorder.ProfileAdded == 1 && fixture.Recorder.ProfilesChanged == 1 &&
                   fixture.ViewModel.Profiles.Any(profile => profile.ProfileId == added.ProfileId),
                "F4F ProfileAdd did not refresh the runtime/UI before the injected subscriber.");
            Assert(fixture.Recorder.ConfigurationSaved == 1,
                "F4F ProfileAdd did not reach NotifyConfigurationSaved exactly once.");
            Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
                   fixture.Registrar.ActiveKeys.Single() == "F1" &&
                   fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe" &&
                   fixture.Recorder.Notifications.Count == 0,
                "F4F ProfileAdd unexpectedly changed hotkeys, Auto Resize or OSD.");
        }
        finally
        {
            events.ProfileAdd -= first;
            events.ProfileAdd -= throwing;
            events.ProfileAdd -= last;
            ConfigFactory.ConfigurationSaved -= saved;
            ConfigFactory.SetCallbackFailureLogger(null);
        }
    }

    private static void VerifyThrowingProfileRemoveIsolated(string directory)
    {
        using var fixture = new Fixture(directory, "B", "C");
        var removed = fixture.Profile("B");
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        var trace = new List<string>();
        var injected = new InvalidOperationException("Injected ProfileRemove failure (F4F).");
        var callbackFailures = new List<string>();
        var events = ConfigFactory.Profiles.ProfileEvents;
        ProfileEvents.ProfileRemoveEvent first = _ => trace.Add("first");
        ProfileEvents.ProfileRemoveEvent throwing = _ =>
        {
            trace.Add("throwing");
            throw injected;
        };
        ProfileEvents.ProfileRemoveEvent last = _ => trace.Add("last");
        ConfigFactory.ConfigurationSavedEvent saved = () => trace.Add("saved");

        fixture.ResetObservation();
        ConfigFactory.SetCallbackFailureLogger(callbackFailures.Add);
        events.ProfileRemove += first;
        events.ProfileRemove += throwing;
        events.ProfileRemove += last;
        ConfigFactory.ConfigurationSaved += saved;
        try
        {
            Assert(fixture.Runtime.RemoveProfile(removed.ProfileId),
                "F4F ProfileRemove callback failure prevented the completed removal.");

            DrainDispatcher();
            Assert(trace.SequenceEqual(new[] { "first", "throwing", "last", "saved" }),
                "F4F ProfileRemove did not invoke the tail subscriber and ConfigurationSaved before returning.");
            Assert(callbackFailures.Count == 1 &&
                   callbackFailures[0].Contains("during ProfileRemove") &&
                   callbackFailures[0].Contains(injected.Message),
                "F4F ProfileRemove did not log the isolated callback failure exactly once.");
            Assert(File.ReadAllText(fixture.ConfigPath) != originalDisk &&
                   fixture.ReadDiskProfileIds().SequenceEqual(new[] { fixture.A.ProfileId, fixture.Profile("C").ProfileId }) &&
                   fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId,
                "F4F ProfileRemove did not persist the removal before the callback.");
            Assert(ConfigFactory.Profiles.Configs.All(profile => !ReferenceEquals(profile, removed)) &&
                   ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
                   ReferenceEquals(ConfigFactory.Current, fixture.A),
                "F4F ProfileRemove did not leave disk and live state consistently committed.");
            Assert(fixture.Recorder.ProfileRemoved == 1 && fixture.Recorder.ProfilesChanged == 1 &&
                   fixture.ViewModel.Profiles.All(profile => profile.ProfileId != removed.ProfileId),
                "F4F ProfileRemove did not refresh the runtime/UI before the injected subscriber.");
            Assert(fixture.Recorder.ConfigurationSaved == 1,
                "F4F ProfileRemove did not reach NotifyConfigurationSaved exactly once.");
            Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
                   fixture.Registrar.ActiveKeys.Single() == "F1" &&
                   fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe" &&
                   fixture.Recorder.Notifications.Count == 0,
                "F4F ProfileRemove unexpectedly changed hotkeys, Auto Resize or OSD.");
        }
        finally
        {
            events.ProfileRemove -= first;
            events.ProfileRemove -= throwing;
            events.ProfileRemove -= last;
            ConfigFactory.ConfigurationSaved -= saved;
            ConfigFactory.SetCallbackFailureLogger(null);
        }
    }

    private static void VerifyThrowingProfileRenameIsolated(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        var trace = new List<string>();
        var injected = new InvalidOperationException("Injected ProfileRename failure (F4F).");
        var callbackFailures = new List<string>();
        var events = ConfigFactory.Profiles.ProfileEvents;
        ProfileEvents.ProfileRenameEvent first = (_, _) => trace.Add("first");
        ProfileEvents.ProfileRenameEvent throwing = (_, _) =>
        {
            trace.Add("throwing");
            throw injected;
        };
        ProfileEvents.ProfileRenameEvent last = (_, _) => trace.Add("last");
        ConfigFactory.ConfigurationSavedEvent saved = () => trace.Add("saved");

        fixture.ResetObservation();
        ConfigFactory.SetCallbackFailureLogger(callbackFailures.Add);
        events.ProfileRename += first;
        events.ProfileRename += throwing;
        events.ProfileRename += last;
        ConfigFactory.ConfigurationSaved += saved;
        try
        {
            Assert(fixture.Runtime.RenameProfile(fixture.A.ProfileId, "A Renamed"),
                "F4F ProfileRename callback failure prevented the completed rename.");

            DrainDispatcher();
            Assert(trace.SequenceEqual(new[] { "saved", "first", "throwing", "last" }),
                "F4F ProfileRename did not notify ConfigurationSaved first and invoke the tail subscriber.");
            Assert(callbackFailures.Count == 1 &&
                   callbackFailures[0].Contains("during ProfileRename") &&
                   callbackFailures[0].Contains(injected.Message),
                "F4F ProfileRename did not log the isolated callback failure exactly once.");
            Assert(File.ReadAllText(fixture.ConfigPath) != originalDisk &&
                   fixture.ReadDiskProfileName(fixture.A.ProfileId) == "A Renamed" &&
                   fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId,
                "F4F ProfileRename did not save the new name before the callback.");
            Assert(ReferenceEquals(ConfigFactory.Current, fixture.A) &&
                   fixture.A.ProfileName == "A Renamed" &&
                   ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId,
                "F4F ProfileRename did not leave disk and live state consistently committed.");
            Assert(fixture.Recorder.ConfigurationSaved == 1 && fixture.Recorder.ProfilesChanged == 1 &&
                   fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == fixture.A.ProfileId).Name == "A Renamed",
                "F4F ProfileRename did not notify ConfigurationSaved and refresh runtime/UI before the throw.");
            Assert(fixture.Registrar.RegisterCount == 0 && fixture.Registrar.ReleaseAllCount == 0 &&
                   fixture.Registrar.ActiveKeys.Single() == "F1" &&
                   fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe" &&
                   fixture.Recorder.Notifications.Count == 0,
                "F4F ProfileRename unexpectedly changed hotkeys, Auto Resize or OSD.");
        }
        finally
        {
            events.ProfileRename -= first;
            events.ProfileRename -= throwing;
            events.ProfileRename -= last;
            ConfigFactory.ConfigurationSaved -= saved;
            ConfigFactory.SetCallbackFailureLogger(null);
        }
    }

    private static void VerifyRemoveProtections(string directory)
    {
        using var fixture = new Fixture(directory);
        fixture.ResetObservation();
        Assert(!fixture.Runtime.RemoveProfile(fixture.A.ProfileId),
            "Runtime allowed removal of the last active profile.");
        Assert(ConfigFactory.Profiles.Configs.Count == 1 &&
               ReferenceEquals(ConfigFactory.Profiles.Configs[0], fixture.A),
            "Rejected last-profile removal changed RAM.");
        Assert(fixture.Recorder.Timeline.Count == 0 && fixture.Recorder.Notifications.Count == 0,
            "Rejected last-profile removal emitted events or OSD.");
    }

    private static void VerifySwitchFailure(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profileB = fixture.Profile("B");
        var profilesList = ConfigFactory.Profiles.Configs;
        var originalProfiles = profilesList.ToList();
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        fixture.ResetObservation();

        var failed = false;
        using (new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                fixture.Runtime.SwitchProfile(profileB.ProfileId);
            }
            catch (ConfigurationPersistenceException)
            {
                failed = true;
            }

            Assert(File.ReadAllText(fixture.ConfigPath) == originalDisk &&
                   fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId,
                "Profile Switch failure changed the active profile on disk.");
        }

        DrainDispatcher();
        Assert(failed, "Profile Switch did not surface persistence failure.");
        Assert(ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
               ReferenceEquals(ConfigFactory.Current, fixture.A),
            "Failed Profile Switch changed the live active profile.");
        Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs) &&
               profilesList.Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal),
            "Failed Profile Switch changed profile list/order/identity.");
        Assert(fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F1" &&
               fixture.Registrar.ActiveKeys.Single() == "F1",
            "Failed Profile Switch changed the active hotkeys from Profile A.");
        Assert(fixture.Registrar.ReleaseAllCount == 0 && fixture.Registrar.RegisterCount == 0,
            "Failed Profile Switch reloaded hotkeys.");
        Assert(fixture.AutoResizeGeneration == generation && fixture.SnapshotProcess == "a.exe",
            "Failed Profile Switch invalidated pending Auto Resize or changed its snapshot.");
        Assert(fixture.Recorder.ProfileSwitching == 0 && fixture.Recorder.ProfileSwitched == 0 &&
               fixture.Recorder.ProfilesChanged == 0 && fixture.Recorder.HotkeysChanged == 0 &&
               fixture.Recorder.ProfileOptionsChanged == 0 && fixture.Recorder.PresetsChanged == 0 &&
               fixture.Recorder.ProcessesChanged == 0 && fixture.Recorder.ConfigurationSaved == 0,
            "Failed Profile Switch emitted success-side events.");
        Assert(fixture.Recorder.Notifications.Count == 0,
            "Failed Profile Switch emitted a success OSD.");
        Assert(fixture.Recorder.DiskAtProfilesChanged.Count == 0 &&
               fixture.Recorder.LiveAtProfilesChanged.Count == 0,
            "Failed Profile Switch refreshed profile UI/tray subscribers.");
        Assert(fixture.Recorder.Timeline.Count == 0,
            "Failed Profile Switch produced side effects before persistence failure: " +
            string.Join(", ", fixture.Recorder.Timeline));
        Assert(fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == fixture.A.ProfileId).IsActive &&
               !fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == profileB.ProfileId).IsActive,
            "Failed Profile Switch changed the profile UI from A.");
    }

    private static void VerifyThrowingProfileSwitchingIsolated(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profileB = fixture.Profile("B");
        var profilesList = ConfigFactory.Profiles.Configs;
        var originalProfiles = profilesList.ToList();
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        var callbackTrace = new List<string>();
        var callbackFailures = new List<string>();
        var injected = new InvalidOperationException("Injected ProfileSwitching failure (F4B).");
        ProfileEvents.ProfileSwitchingEvent firstSubscriber = (current, next) => callbackTrace.Add("first");
        ProfileEvents.ProfileSwitchingEvent throwingSubscriber = (current, next) =>
        {
            callbackTrace.Add("throwing");
            throw injected;
        };
        ProfileEvents.ProfileSwitchingEvent lastSubscriber = (current, next) => callbackTrace.Add("last");
        string? diskAtPostSwitch = null;
        string? liveAtPostSwitch = null;
        ProfileEvents.ProfileSwitchEvent postSwitchSubscriber = id =>
        {
            callbackTrace.Add("post-switch");
            diskAtPostSwitch = fixture.ReadDiskCurrentProfileId();
            liveAtPostSwitch = ConfigFactory.Profiles.CurrentProfileId;
        };

        Assert(fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId &&
               ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
               ReferenceEquals(ConfigFactory.Current, fixture.A),
            "F4A baseline is not persisted and active on Profile A.");
        fixture.ResetObservation();
        ConfigFactory.SetCallbackFailureLogger(callbackFailures.Add);
        var events = ConfigFactory.Profiles.ProfileEvents;
        events.ProfileSwitching += firstSubscriber;
        events.ProfileSwitching += throwingSubscriber;
        events.ProfileSwitching += lastSubscriber;
        events.ProfileSwitch += postSwitchSubscriber;
        try
        {
            Assert(fixture.Runtime.SwitchProfile(profileB.ProfileId),
                "F4B ProfileSwitching callback failure prevented the live switch.");
            DrainDispatcher();
            Assert(File.ReadAllText(fixture.ConfigPath) != originalDisk &&
                   fixture.ReadDiskCurrentProfileId() == profileB.ProfileId,
                "F4B did not persist Profile B before the callbacks.");
            Assert(ConfigFactory.Profiles.CurrentProfileId == profileB.ProfileId &&
                   ReferenceEquals(ConfigFactory.Current, profileB),
                "F4B did not commit Profile B to the live model.");
            Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs) &&
                   profilesList.Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal) &&
                   fixture.ReadDiskProfileIds().SequenceEqual(originalProfiles.Select(profile => profile.ProfileId)),
                "F4B changed the profile list/order/identity.");
            Assert(callbackTrace.SequenceEqual(new[] { "first", "throwing", "last", "post-switch" }),
                "F4B did not invoke each ProfileSwitching subscriber and the post-switch notification in order.");
            Assert(callbackFailures.Count == 1 &&
                   callbackFailures[0].Contains("during ProfileSwitching") &&
                   callbackFailures[0].Contains(injected.Message),
                "F4B did not log the isolated ProfileSwitching callback failure exactly once.");
            Assert(diskAtPostSwitch == profileB.ProfileId && liveAtPostSwitch == profileB.ProfileId,
                "F4B ProfileSwitch subscriber did not observe committed Profile B on disk and in RAM.");
            Assert(!fixture.Runtime.GetProfiles().Single(profile => profile.Id == fixture.A.ProfileId).IsCurrent &&
                   fixture.Runtime.GetProfiles().Single(profile => profile.Id == profileB.ProfileId).IsCurrent &&
                   !fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == fixture.A.ProfileId).IsActive &&
                   fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == profileB.ProfileId).IsActive,
                "F4B runtime/UI did not switch to Profile B.");
            Assert(fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F3" &&
                   fixture.Registrar.ActiveKeys.Single() == "F3" &&
                   fixture.Registrar.ReleaseAllCount == 1 && fixture.Registrar.RegisterCount == 1,
                "F4B did not reload Profile B hotkeys exactly once.");
            Assert(fixture.SnapshotProcess == "b.exe" && fixture.AutoResizeGeneration == generation + 1,
                "F4B did not update Auto Resize snapshot or invalidate pending work exactly once.");
            Assert(fixture.Recorder.ProfileSwitching == 1 && fixture.Recorder.ProfileSwitched == 1 &&
                   fixture.Recorder.ConfigurationSaved == 1 && fixture.Recorder.ProfilesChanged == 1 &&
                   fixture.Recorder.HotkeysChanged == 1 && fixture.Recorder.ProfileOptionsChanged == 1 &&
                   fixture.Recorder.PresetsChanged == 1 && fixture.Recorder.ProcessesChanged == 2 &&
                   fixture.Recorder.Notifications.Count == 1 &&
                   fixture.Recorder.Notifications[0].Level == RuntimeNotificationLevel.Success &&
                   fixture.Recorder.DiskAtProfilesChanged.Single() == profileB.ProfileId &&
                   fixture.Recorder.LiveAtProfilesChanged.Single() == profileB.ProfileId,
                "F4B did not deliver the normal Profile B runtime/UI notifications.");
            AssertOrder(fixture.Recorder.Timeline,
                "switching", "hotkeys-release", "hotkeys-register:F3", "profiles", "hotkeys",
                "options", "presets", "processes", "osd", "switch", "saved");
        }
        finally
        {
            events.ProfileSwitching -= firstSubscriber;
            events.ProfileSwitching -= throwingSubscriber;
            events.ProfileSwitching -= lastSubscriber;
            events.ProfileSwitch -= postSwitchSubscriber;
            ConfigFactory.SetCallbackFailureLogger(null);
        }
    }

    private static void VerifyThrowingProfileSwitchIsolated(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profileB = fixture.Profile("B");
        var profilesList = ConfigFactory.Profiles.Configs;
        var originalProfiles = profilesList.ToList();
        var originalDisk = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        var callbackTrace = new List<string>();
        var callbackFailures = new List<string>();
        var injected = new InvalidOperationException("Injected ProfileSwitch failure (F4D).");
        ProfileEvents.ProfileSwitchEvent firstSubscriber = id => callbackTrace.Add("first");
        ProfileEvents.ProfileSwitchEvent throwingSubscriber = id =>
        {
            callbackTrace.Add("throwing");
            throw injected;
        };
        ProfileEvents.ProfileSwitchEvent lastSubscriber = id => callbackTrace.Add("last");
        ConfigFactory.ConfigurationSavedEvent savedSubscriber = () => callbackTrace.Add("saved");

        Assert(fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId &&
               ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
               ReferenceEquals(ConfigFactory.Current, fixture.A),
            "F4C baseline is not persisted and active on Profile A.");
        fixture.ResetObservation();
        ConfigFactory.SetCallbackFailureLogger(callbackFailures.Add);
        var events = ConfigFactory.Profiles.ProfileEvents;
        events.ProfileSwitch += firstSubscriber;
        events.ProfileSwitch += throwingSubscriber;
        events.ProfileSwitch += lastSubscriber;
        ConfigFactory.ConfigurationSaved += savedSubscriber;
        try
        {
            Assert(fixture.Runtime.SwitchProfile(profileB.ProfileId),
                "F4D ProfileSwitch callback failure prevented the completed switch.");
            DrainDispatcher();
            Assert(File.ReadAllText(fixture.ConfigPath) != originalDisk &&
                   fixture.ReadDiskCurrentProfileId() == profileB.ProfileId,
                "F4D Profile B was not persisted before the post-switch callbacks.");
            Assert(ConfigFactory.Profiles.CurrentProfileId == profileB.ProfileId &&
                   ReferenceEquals(ConfigFactory.Current, profileB),
                "F4D live configuration did not remain committed to Profile B.");
            Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs) &&
                   profilesList.Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal) &&
                   fixture.ReadDiskProfileIds().SequenceEqual(originalProfiles.Select(profile => profile.ProfileId)),
                "F4D changed profile list/order/identity.");
            Assert(callbackTrace.SequenceEqual(new[] { "first", "throwing", "last", "saved" }),
                "F4D did not isolate ProfileSwitch subscribers before ConfigurationSaved.");
            Assert(callbackFailures.Count == 1 &&
                   callbackFailures[0].Contains("during ProfileSwitch") &&
                   callbackFailures[0].Contains(injected.Message),
                "F4D did not log the isolated ProfileSwitch callback failure exactly once.");
            Assert(!fixture.Runtime.GetProfiles().Single(profile => profile.Id == fixture.A.ProfileId).IsCurrent &&
                   fixture.Runtime.GetProfiles().Single(profile => profile.Id == profileB.ProfileId).IsCurrent &&
                   !fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == fixture.A.ProfileId).IsActive &&
                   fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == profileB.ProfileId).IsActive,
                "F4D runtime/UI did not remain on Profile B.");
            Assert(fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F3" &&
                   fixture.Registrar.ActiveKeys.Single() == "F3" &&
                   fixture.Registrar.ReleaseAllCount == 1 && fixture.Registrar.RegisterCount == 1,
                "F4D Profile B hotkeys were not active exactly once after the callback failure.");
            Assert(fixture.SnapshotProcess == "b.exe" && fixture.AutoResizeGeneration == generation + 1,
                "F4D Auto Resize snapshot/invalidation did not remain on Profile B.");
            Assert(fixture.Recorder.ProfileSwitching == 1 && fixture.Recorder.ProfileSwitched == 1 &&
                   fixture.Recorder.ProfilesChanged == 1 && fixture.Recorder.HotkeysChanged == 1 &&
                   fixture.Recorder.ProfileOptionsChanged == 1 && fixture.Recorder.PresetsChanged == 1 &&
                   fixture.Recorder.ProcessesChanged == 2 && fixture.Recorder.ConfigurationSaved == 1,
                "F4D did not complete runtime effects and ConfigurationSaved exactly once.");
            Assert(fixture.Recorder.Notifications.Count == 1 &&
                   fixture.Recorder.Notifications[0].Level == RuntimeNotificationLevel.Success &&
                   fixture.Recorder.Notifications[0].Message.Contains("<B>"),
                "F4D success OSD was missing or duplicated.");
            Assert(fixture.Recorder.DiskAtProfilesChanged.Single() == profileB.ProfileId &&
                   fixture.Recorder.LiveAtProfilesChanged.Single() == profileB.ProfileId,
                "F4D runtime profile refresh did not observe committed Profile B.");
            AssertOrder(fixture.Recorder.Timeline,
                "switching", "hotkeys-release", "hotkeys-register:F3", "profiles", "hotkeys",
                "options", "presets", "processes", "osd", "switch", "saved");
        }
        finally
        {
            events.ProfileSwitch -= firstSubscriber;
            events.ProfileSwitch -= throwingSubscriber;
            events.ProfileSwitch -= lastSubscriber;
            ConfigFactory.ConfigurationSaved -= savedSubscriber;
            ConfigFactory.SetCallbackFailureLogger(null);
        }
    }

    private static void VerifyGetCurrentConfigFallback(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profileB = fixture.Profile("B");
        Assert(fixture.Runtime.SwitchProfile(profileB.ProfileId),
            "Could not establish persisted Profile B before testing Current fallback.");
        DrainDispatcher();

        var profiles = ConfigFactory.Profiles;
        var configs = profiles.Configs.ToList();
        var ignoredWindows = profiles.GetIgnoredWindowsSnapshot();
        var diskBefore = File.ReadAllText(fixture.ConfigPath);
        profiles.Replace(configs, "missing-profile", ignoredWindows,
            profiles.ThemePreference, profiles.CompensateDwmFrameEffects);
        var generation = fixture.AutoResizeGeneration;
        fixture.ResetObservation();

        var callbackTrace = new List<string>();
        ProfileEvents.ProfileSwitchEvent first = _ => callbackTrace.Add("first");
        ProfileEvents.ProfileSwitchEvent throwing = _ =>
        {
            callbackTrace.Add("throwing");
            throw new InvalidOperationException("F4H-C callback failure");
        };
        ProfileEvents.ProfileSwitchEvent last = _ => callbackTrace.Add("last");
        var reentrantReads = 0;
        string? reentrantProfileId = null;
        EventHandler readDuringRefresh = (_, _) =>
        {
            reentrantReads++;
            reentrantProfileId = ConfigFactory.Current.ProfileId;
        };
        var events = profiles.ProfileEvents;
        events.ProfileSwitch += first;
        events.ProfileSwitch += throwing;
        events.ProfileSwitch += last;
        fixture.Runtime.ProfilesChanged += readDuringRefresh;
        try
        {
            var current = ConfigFactory.Current;
            DrainDispatcher();
            Assert(ReferenceEquals(current, fixture.A),
                "Current fallback did not return the selected Profile A.");
            Assert(profiles.CurrentProfileId == fixture.A.ProfileId &&
                   ReferenceEquals(profiles.Current, fixture.A) &&
                   fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId &&
                   File.ReadAllText(fixture.ConfigPath) != diskBefore,
                "Current fallback did not persist the selected live profile.");
            Assert(fixture.Runtime.GetProfiles().Single(profile => profile.IsCurrent).Id == fixture.A.ProfileId &&
                   fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == fixture.A.ProfileId).IsActive &&
                   !fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == profileB.ProfileId).IsActive &&
                   fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F1" &&
                   fixture.Registrar.ActiveKeys.Single() == "F1" &&
                   fixture.SnapshotProcess == "a.exe",
                "Current fallback did not synchronize Runtime, UI, hotkeys and Auto Resize.");
            Assert(callbackTrace.SequenceEqual(new[] { "first", "throwing", "last" }),
                "A throwing ProfileSwitch subscriber stopped a later subscriber.");
            Assert(reentrantReads == 1 && reentrantProfileId == fixture.A.ProfileId,
                "Reading Current during ProfilesChanged started a recursive fallback.");
            Assert(fixture.Recorder.ProfileSwitching == 1 && fixture.Recorder.ProfileSwitched == 1 &&
                   fixture.Recorder.ConfigurationSaved == 1 && fixture.Recorder.ProfilesChanged == 1 &&
                   fixture.Recorder.Notifications.Count == 1 &&
                   fixture.Recorder.Notifications[0].Level == RuntimeNotificationLevel.Success &&
                   fixture.Recorder.Notifications[0].Message.Contains("<A>"),
                "Current fallback duplicated or omitted the switch lifecycle, save or success OSD.");
            Assert(fixture.Recorder.DiskAtProfilesChanged.Single() == fixture.A.ProfileId &&
                   fixture.Recorder.LiveAtProfilesChanged.Single() == fixture.A.ProfileId &&
                   fixture.Registrar.ReleaseAllCount == 1 && fixture.Registrar.RegisterCount == 1 &&
                   fixture.AutoResizeGeneration == generation + 1,
                "Current fallback did not commit before refresh or repeated hotkey/Auto Resize work.");
        }
        finally
        {
            fixture.Runtime.ProfilesChanged -= readDuringRefresh;
            events.ProfileSwitch -= first;
            events.ProfileSwitch -= throwing;
            events.ProfileSwitch -= last;
        }
    }

    private static void VerifyGetCurrentConfigFallbackPersistenceFailure(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profileB = fixture.Profile("B");
        Assert(fixture.Runtime.SwitchProfile(profileB.ProfileId),
            "Could not establish persisted Profile B before testing fallback persistence failure.");
        DrainDispatcher();

        var profiles = ConfigFactory.Profiles;
        var diskBefore = File.ReadAllText(fixture.ConfigPath);
        profiles.Replace(profiles.Configs.ToList(), "missing-profile", profiles.GetIgnoredWindowsSnapshot(),
            profiles.ThemePreference, profiles.CompensateDwmFrameEffects);
        var generation = fixture.AutoResizeGeneration;
        fixture.ResetObservation();

        var failed = false;
        using (new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                _ = ConfigFactory.Current;
            }
            catch (ConfigurationPersistenceException)
            {
                failed = true;
            }

            Assert(File.ReadAllText(fixture.ConfigPath) == diskBefore,
                "Failed Current fallback changed the config on disk.");
        }

        DrainDispatcher();
        Assert(failed && profiles.CurrentProfileId == "missing-profile" && profiles.Current is null &&
               fixture.ReadDiskCurrentProfileId() == profileB.ProfileId,
            "Failed Current fallback changed live state or hid the persistence failure.");
        Assert(fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == profileB.ProfileId).IsActive &&
               fixture.Registrar.ActiveKeys.Single() == "F3" && fixture.SnapshotProcess == "b.exe" &&
               fixture.AutoResizeGeneration == generation,
            "Failed Current fallback changed UI, hotkeys or Auto Resize state.");
        Assert(fixture.Recorder.Timeline.Count == 0 && fixture.Recorder.Notifications.Count == 0 &&
               fixture.Registrar.ReleaseAllCount == 0 && fixture.Registrar.RegisterCount == 0,
            "Failed Current fallback emitted a false success lifecycle.");
    }

    private static void VerifyValidCurrentRead(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var diskBefore = File.ReadAllText(fixture.ConfigPath);
        var generation = fixture.AutoResizeGeneration;
        fixture.ResetObservation();

        Assert(ReferenceEquals(ConfigFactory.Current, fixture.A) &&
               ReferenceEquals(ConfigFactory.Current, fixture.A),
            "A valid Current read did not return the active profile.");
        DrainDispatcher();
        Assert(File.ReadAllText(fixture.ConfigPath) == diskBefore &&
               fixture.Recorder.Timeline.Count == 0 && fixture.Recorder.Notifications.Count == 0 &&
               fixture.Registrar.ReleaseAllCount == 0 && fixture.Registrar.RegisterCount == 0 &&
               fixture.AutoResizeGeneration == generation,
            "A valid Current read performed persistence or lifecycle work.");
    }

    private static void VerifySuccessfulRoundTrip(string directory)
    {
        using var fixture = new Fixture(directory, "B");
        var profileB = fixture.Profile("B");
        var profilesList = ConfigFactory.Profiles.Configs;
        var originalProfiles = profilesList.ToList();
        var generation = fixture.AutoResizeGeneration;

        fixture.ResetObservation();
        Assert(fixture.Runtime.SwitchProfile(profileB.ProfileId), "Normal A -> B switch failed.");
        DrainDispatcher();
        Assert(ConfigFactory.Profiles.CurrentProfileId == profileB.ProfileId &&
               fixture.ReadDiskCurrentProfileId() == profileB.ProfileId &&
               fixture.SnapshotProcess == "b.exe",
            "Normal A -> B did not synchronize live, disk and Auto Resize snapshot.");
        Assert(fixture.Registrar.ActiveKeys.Single() == "F3" &&
               fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F3",
            "Normal A -> B did not load Profile B hotkeys.");
        Assert(fixture.Registrar.ReleaseAllCount == 1 && fixture.Registrar.RegisterCount == 1,
            "Normal A -> B did not reload hotkeys exactly once.");
        Assert(fixture.Recorder.ProfileSwitching == 1 && fixture.Recorder.ProfileSwitched == 1 &&
               fixture.Recorder.ProfilesChanged == 1 && fixture.Recorder.HotkeysChanged == 1 &&
               fixture.Recorder.ProfileOptionsChanged == 1 && fixture.Recorder.PresetsChanged == 1 &&
               fixture.Recorder.ProcessesChanged == 2 && fixture.Recorder.ConfigurationSaved == 1,
            "Normal A -> B did not emit the expected events exactly once.");
        Assert(fixture.Recorder.Notifications.Count == 1 &&
               fixture.Recorder.Notifications[0].Level == RuntimeNotificationLevel.Success &&
               fixture.Recorder.Notifications[0].Message.Contains("<B>"),
            "Normal A -> B did not emit exactly one success OSD.");
        Assert(fixture.Recorder.DiskAtProfilesChanged.Single() == profileB.ProfileId &&
               fixture.Recorder.LiveAtProfilesChanged.Single() == profileB.ProfileId,
            "Normal A -> B emitted ProfilesChanged before disk/live commit.");
        AssertOrder(fixture.Recorder.Timeline,
            "switching", "hotkeys-release", "hotkeys-register:F3", "profiles", "hotkeys",
            "options", "presets", "processes", "osd", "switch", "saved");
        Assert(fixture.ViewModel.Profiles.Single(profile => profile.ProfileId == profileB.ProfileId).IsActive,
            "Normal A -> B did not update profile UI state.");

        fixture.ResetObservation();
        Assert(fixture.Runtime.SwitchProfile(fixture.A.ProfileId), "Normal B -> A switch failed.");
        DrainDispatcher();
        Assert(ConfigFactory.Profiles.CurrentProfileId == fixture.A.ProfileId &&
               fixture.ReadDiskCurrentProfileId() == fixture.A.ProfileId &&
               fixture.SnapshotProcess == "a.exe",
            "Normal B -> A did not synchronize live, disk and Auto Resize snapshot.");
        Assert(fixture.Registrar.ActiveKeys.Single() == "F1" &&
               fixture.Runtime.GetHotkeys().Single(item => item.Type == HotkeysType.Save).Hotkey?.Key == "F1",
            "Normal B -> A did not restore Profile A hotkeys.");
        Assert(fixture.Registrar.ReleaseAllCount == 1 && fixture.Registrar.RegisterCount == 1,
            "Normal B -> A did not reload hotkeys exactly once.");
        Assert(fixture.Recorder.ProfileSwitching == 1 && fixture.Recorder.ProfileSwitched == 1 &&
               fixture.Recorder.ProfilesChanged == 1 && fixture.Recorder.HotkeysChanged == 1 &&
               fixture.Recorder.ProfileOptionsChanged == 1 && fixture.Recorder.PresetsChanged == 1 &&
               fixture.Recorder.ProcessesChanged == 2 && fixture.Recorder.ConfigurationSaved == 1,
            "Normal B -> A did not emit the expected events exactly once.");
        Assert(fixture.Recorder.Notifications.Count == 1 &&
               fixture.Recorder.Notifications[0].Level == RuntimeNotificationLevel.Success &&
               fixture.Recorder.Notifications[0].Message.Contains("<A>"),
            "Normal B -> A did not emit exactly one success OSD.");
        Assert(fixture.Recorder.DiskAtProfilesChanged.Single() == fixture.A.ProfileId &&
               fixture.Recorder.LiveAtProfilesChanged.Single() == fixture.A.ProfileId,
            "Normal B -> A emitted ProfilesChanged before disk/live commit.");
        Assert(fixture.AutoResizeGeneration == generation + 2,
            "Normal A -> B -> A did not invalidate Auto Resize twice.");
        Assert(ReferenceEquals(profilesList, ConfigFactory.Profiles.Configs) &&
               profilesList.Zip(originalProfiles, (actual, expected) => ReferenceEquals(actual, expected)).All(equal => equal),
            "Normal A -> B -> A changed profile list/order/identity.");
    }

    private static void AssertOrder(IReadOnlyList<string> timeline, params string[] expected)
    {
        var position = -1;
        foreach (var item in expected)
        {
            var next = -1;
            for (var index = position + 1; index < timeline.Count; index++)
            {
                if (timeline[index] == item)
                {
                    next = index;
                    break;
                }
            }

            Assert(next >= 0, $"Event '{item}' was missing or out of order. Timeline: {string.Join(", ", timeline)}");
            position = next;
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string directory, params string[] additionalProfiles)
        {
            Directory.CreateDirectory(directory);
            ConfigPath = Path.Combine(directory, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(ConfigPath);
            ConfigFactory.UseDefault();
            A = ConfigFactory.Current;
            Configure(A, "A", "F1", "a.exe");
            foreach (var name in additionalProfiles)
            {
                var profile = ConfigFactory.Profiles.Add(name);
                var key = name == "B" ? "F3" : "F5";
                Configure(profile, name, key, name.ToLowerInvariant() + ".exe");
            }

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
            ViewModel = new ProfilesViewModel(Runtime, Dispatcher.CurrentDispatcher);
            Recorder = new EventRecorder(Runtime, ConfigPath);
            Registrar.Trace = Recorder.RecordHotkey;
            Registrar.ResetMetrics();
        }

        public string ConfigPath { get; }
        public Config A { get; }
        public FakeHotkeyRegistrar Registrar { get; }
        public WinResizerRuntime Runtime { get; }
        public ProfilesViewModel ViewModel { get; }
        public EventRecorder Recorder { get; }

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

        public string SnapshotProcess => Runtime.CurrentAutoResizeSnapshot!.WindowSizes.Single().Name;

        public Config Profile(string name) =>
            ConfigFactory.Profiles.Configs.Single(profile => profile.ProfileName == name);

        public string ReadDiskCurrentProfileId() =>
            JObject.Parse(File.ReadAllText(ConfigPath)).Value<string>(nameof(Profiles.CurrentProfileId))
            ?? string.Empty;

        public IReadOnlyList<string> ReadDiskProfileIds() =>
            (JObject.Parse(File.ReadAllText(ConfigPath))[nameof(Profiles.Configs)] as JArray)?
                .Select(profile => profile.Value<string>(nameof(Config.ProfileId)) ?? string.Empty)
                .ToList()
            ?? new List<string>();

        public string ReadDiskProfileName(string profileId) =>
            (JObject.Parse(File.ReadAllText(ConfigPath))[nameof(Profiles.Configs)] as JArray)?
                .Single(profile => profile.Value<string>(nameof(Config.ProfileId)) == profileId)
                .Value<string>(nameof(Config.ProfileName))
            ?? string.Empty;

        public void ResetObservation()
        {
            Recorder.Clear();
            Registrar.ResetMetrics();
        }

        public void Dispose()
        {
            Registrar.Trace = null;
            Recorder.Dispose();
            ViewModel.Dispose();
            Runtime.Dispose();
            Registrar.Dispose();
        }

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
                Rect = new Rect(10, 20, 410, 320),
                AutoResize = true,
            });
        }
    }

    private sealed class EventRecorder : IDisposable
    {
        private readonly WinResizerRuntime _runtime;
        private readonly string _configPath;
        private readonly ProfileEvents.ProfileAddEvent _profileAdd;
        private readonly ProfileEvents.ProfileRemoveEvent _profileRemove;
        private readonly ProfileEvents.ProfileSwitchingEvent _profileSwitching;
        private readonly ProfileEvents.ProfileSwitchEvent _profileSwitch;
        private readonly ConfigFactory.ConfigurationSavedEvent _configurationSaved;
        private readonly EventHandler _profilesChanged;
        private readonly EventHandler _hotkeysChanged;
        private readonly EventHandler _profileOptionsChanged;
        private readonly EventHandler _presetsChanged;
        private readonly EventHandler _processesChanged;
        private readonly EventHandler<RuntimeNotificationEventArgs> _notification;

        public EventRecorder(WinResizerRuntime runtime, string configPath)
        {
            _runtime = runtime;
            _configPath = configPath;
            _profileAdd = (id, name) => { ProfileAdded++; Timeline.Add("add"); };
            _profileRemove = id => { ProfileRemoved++; Timeline.Add("remove"); };
            _profileSwitching = (current, next) => { ProfileSwitching++; Timeline.Add("switching"); };
            _profileSwitch = id => { ProfileSwitched++; Timeline.Add("switch"); };
            _configurationSaved = () => { ConfigurationSaved++; Timeline.Add("saved"); };
            _profilesChanged = (_, _) =>
            {
                ProfilesChanged++;
                Timeline.Add("profiles");
                DiskAtProfilesChanged.Add(ReadDiskCurrentProfileId());
                LiveAtProfilesChanged.Add(ConfigFactory.Profiles.CurrentProfileId);
            };
            _hotkeysChanged = (_, _) => { HotkeysChanged++; Timeline.Add("hotkeys"); };
            _profileOptionsChanged = (_, _) => { ProfileOptionsChanged++; Timeline.Add("options"); };
            _presetsChanged = (_, _) => { PresetsChanged++; Timeline.Add("presets"); };
            _processesChanged = (_, _) => { ProcessesChanged++; Timeline.Add("processes"); };
            _notification = (_, notification) =>
            {
                Notifications.Add(notification);
                Timeline.Add("osd");
            };

            var events = ConfigFactory.Profiles.ProfileEvents;
            events.ProfileAdd += _profileAdd;
            events.ProfileRemove += _profileRemove;
            events.ProfileSwitching += _profileSwitching;
            events.ProfileSwitch += _profileSwitch;
            ConfigFactory.ConfigurationSaved += _configurationSaved;
            runtime.ProfilesChanged += _profilesChanged;
            runtime.HotkeysChanged += _hotkeysChanged;
            runtime.ProfileOptionsChanged += _profileOptionsChanged;
            runtime.PresetsChanged += _presetsChanged;
            runtime.ProcessesChanged += _processesChanged;
            runtime.NotificationRaised += _notification;
        }

        public List<string> Timeline { get; } = new();
        public List<string> DiskAtProfilesChanged { get; } = new();
        public List<string> LiveAtProfilesChanged { get; } = new();
        public List<RuntimeNotificationEventArgs> Notifications { get; } = new();
        public int ProfileAdded { get; private set; }
        public int ProfileRemoved { get; private set; }
        public int ProfileSwitching { get; private set; }
        public int ProfileSwitched { get; private set; }
        public int ProfilesChanged { get; private set; }
        public int HotkeysChanged { get; private set; }
        public int ProfileOptionsChanged { get; private set; }
        public int PresetsChanged { get; private set; }
        public int ProcessesChanged { get; private set; }
        public int ConfigurationSaved { get; private set; }

        public void RecordHotkey(string value) => Timeline.Add(value);

        public void Clear()
        {
            Timeline.Clear();
            DiskAtProfilesChanged.Clear();
            LiveAtProfilesChanged.Clear();
            Notifications.Clear();
            ProfileAdded = 0;
            ProfileRemoved = 0;
            ProfileSwitching = 0;
            ProfileSwitched = 0;
            ProfilesChanged = 0;
            HotkeysChanged = 0;
            ProfileOptionsChanged = 0;
            PresetsChanged = 0;
            ProcessesChanged = 0;
            ConfigurationSaved = 0;
        }

        public void Dispose()
        {
            var events = ConfigFactory.Profiles.ProfileEvents;
            events.ProfileAdd -= _profileAdd;
            events.ProfileRemove -= _profileRemove;
            events.ProfileSwitching -= _profileSwitching;
            events.ProfileSwitch -= _profileSwitch;
            ConfigFactory.ConfigurationSaved -= _configurationSaved;
            _runtime.ProfilesChanged -= _profilesChanged;
            _runtime.HotkeysChanged -= _hotkeysChanged;
            _runtime.ProfileOptionsChanged -= _profileOptionsChanged;
            _runtime.PresetsChanged -= _presetsChanged;
            _runtime.ProcessesChanged -= _processesChanged;
            _runtime.NotificationRaised -= _notification;
        }

        private string ReadDiskCurrentProfileId() =>
            JObject.Parse(File.ReadAllText(_configPath)).Value<string>(nameof(Profiles.CurrentProfileId))
            ?? string.Empty;
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
        public Action<string>? Trace { get; set; }
        public int RegisterCount { get; private set; }
        public int ReleaseAllCount { get; private set; }
        public IReadOnlyCollection<string> ActiveKeys => _active.Values.ToList().AsReadOnly();

        public int RegisterHotKey(ModifierKeys modifier, Keys key)
        {
            RegisterCount++;
            var id = ++_nextId;
            _active[id] = key.ToString();
            Trace?.Invoke("hotkeys-register:" + key);
            return id;
        }

        public void UnRegisterHotKey(int id) => _active.Remove(id);

        public void UnRegisterHotKey()
        {
            ReleaseAllCount++;
            _active.Clear();
            Trace?.Invoke("hotkeys-release");
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
}
