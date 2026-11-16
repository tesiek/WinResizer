using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;

internal static class Program
{
    private static int _assertions;
    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static object? Private(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static object? Monitor(WinResizerRuntime runtime) => Private(runtime, "_windowEventHandler");
    private static AutoResizeCoordinator Coordinator(WinResizerRuntime runtime) =>
        (AutoResizeCoordinator)Private(runtime, "_autoResizeCoordinator")!;
    private static long Generation(WinResizerRuntime runtime) => (long)Private(Coordinator(runtime), "_generation")!;
    private static int Pending(WinResizerRuntime runtime) => ((IDictionary)Private(Coordinator(runtime), "_pending")!).Count;

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static RuntimeProcessInfo Find(WinResizerRuntime runtime, string profile, string token) =>
        runtime.GetProcesses(profile).Single(row => row.ProcessToken == token);

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-Processes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WinResizerRuntime? runtime = null;
        Form? window = null;
        try
        {
            var path = Path.Combine(directory, "WinResizer.config.json");
            ConfigFactory.SetPortablePath(path);
            ConfigFactory.UseDefault();
            var a = ConfigFactory.Current;
            var b = ConfigFactory.Profiles.Add("B");
            var legacy = new WindowSize
            {
                Name = "Firefox.exe", Title = "duplicate", Rect = new Rect(-20, -10, 900, 700),
                State = WindowState.Maximized, MaximizedPosition = new Point(17, 29), AutoResizeDelay = 400,
            };
            var duplicate = new WindowSize
            {
                Name = "firefox.EXE", Title = "duplicate", Rect = new Rect(30, 40, 1000, 800), AutoResizeDelay = 400,
            };
            var destination = new WindowSize { Name = "other.exe", Title = "*", AutoResizeDelay = 900 };
            a.WindowSizes.Add(legacy);
            a.WindowSizes.Add(duplicate);
            a.WindowSizes.Add(destination);
            ConfigFactory.Save();
            var list = a.WindowSizes;
            var failSave = false;
            var saves = 0;
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableGlobalHotkeys = false,
                SaveConfiguration = () =>
                {
                    saves++;
                    if (failSave) throw new IOException("Injected persistence failure.");
                    ConfigFactory.Save();
                },
            });
            runtime.Start();
            var events = 0;
            runtime.ProcessesChanged += (_, _) =>
            {
                events++;
                Assert(runtime.CurrentAutoResizeSnapshot!.WindowSizes.Count == ConfigFactory.Current.WindowSizes.Count,
                    "ProcessesChanged preceded snapshot publication.");
            };
            Assert(Monitor(runtime) is null, "Auto OFF baseline started monitoring.");
            var initial = runtime.GetProcesses(a.ProfileId);
            var token = initial[0].ProcessToken;
            var duplicateToken = initial[1].ProcessToken;
            Assert(token != duplicateToken, "Duplicate Process/Title rows share identity.");
            Assert(typeof(RuntimeProcessInfo).GetProperties().All(property => property.SetMethod is null), "DTO has writable properties.");
            var detachedPosition = initial[0].MaximizedPosition;
            detachedPosition.X = 1234;
            Assert(legacy.MaximizedPosition.X == 17, "DTO exposes mutable model state.");
            var order = a.WindowSizes.ToArray();
            var sorted = runtime.GetProcesses(a.ProfileId).OrderByDescending(row => row.Process).ToList();
            Assert(a.WindowSizes.SequenceEqual(order), "DTO sorting reordered backend rules.");
            Assert(runtime.GetProcesses(a.ProfileId)[0].ProcessToken == token, "Read/sort changed token.");

            var beforeEvents = events;
            var added = runtime.AddProcess(a.ProfileId, "FIREFOX.exe", "new", -70, -80, 1100, 900);
            Assert(events == beforeEvents + 1, "Add did not emit exactly one success event.");
            Assert(!added.AutoResize && added.Delay == 400 && added.State == WindowState.Normal,
                "Add did not use Auto OFF / inherited delay / new-rule defaults.");
            Assert(added.Top == -70 && added.Left == -80 && added.Right == 1100 && added.Bottom == 900,
                "Add confused RECT edges with sizes or rejected negative coordinates.");
            Assert(a.WindowSizes.Take(order.Length).SequenceEqual(order), "Add reordered existing rules.");
            var fresh = runtime.AddProcess(a.ProfileId, "fresh.exe", "*", 0, 0, 640, 480);
            Assert(fresh.Delay == 0 && !fresh.AutoResize, "New process group has unsafe defaults.");

            runtime.RemoveProcess(a.ProfileId, token);
            runtime.UpdateProcessTitle(a.ProfileId, duplicateToken, "changed after preceding row removal");
            Assert(duplicate.Title == "changed after preceding row removal", "Update used the old row index.");
            Expect<ArgumentException>(() => runtime.UpdateProcessTitle(a.ProfileId, token, "stale"));
            // Legacy Save can add rows too; reacquire identity for the restored object.
            a.WindowSizes.Insert(0, legacy);
            beforeEvents = events;
            ConfigFactory.Save();
            Assert(events == beforeEvents + 1, "External Save did not notify future WPF.");
            token = runtime.GetProcesses(a.ProfileId)[0].ProcessToken;
            Assert(token != initial[0].ProcessToken, "Removed identity became valid again.");
            runtime.UpdateProcessLeft(a.ProfileId, token, -220);
            runtime.UpdateProcessTop(a.ProfileId, token, -110);
            runtime.UpdateProcessRight(a.ProfileId, token, 1200);
            runtime.UpdateProcessBottom(a.ProfileId, token, 950);
            runtime.UpdateProcessTitle(a.ProfileId, token, "*report");
            Assert(legacy.Left == -220 && legacy.Top == -110 && legacy.Right == 1200 && legacy.Bottom == 950,
                "Single-field update overwrote other fields.");
            Assert(legacy.State == WindowState.Maximized && legacy.MaximizedPosition.X == 17 && legacy.MaximizedPosition.Y == 29,
                "Ordinary editing reset legacy State/MaximizedPosition.");
            Assert(initial[0].Left == -20 && initial[0].Title == "duplicate", "Published DTO changed with live model.");

            runtime.UpdateProcessName(a.ProfileId, token, "OTHER.EXE");
            Assert(legacy.AutoResizeDelay == 900 && destination.AutoResizeDelay == 900 && duplicate.AutoResizeDelay == 400,
                "Process rename overwrote destination/source group delay.");
            runtime.UpdateProcessName(a.ProfileId, token, "new-group.exe");
            Assert(legacy.AutoResizeDelay == 900, "Rename to new group lost previous delay.");
            runtime.UpdateProcessName(a.ProfileId, token, "other.exe");
            runtime.SetProcessDelay(a.ProfileId, token, int.MaxValue);
            Assert(legacy.AutoResizeDelay == 10000 && destination.AutoResizeDelay == 10000,
                "Delay was not clamped/applied to the entire case-insensitive group.");
            runtime.SetProcessDelay(a.ProfileId, token, -1);
            Assert(legacy.AutoResizeDelay == 0 && destination.AutoResizeDelay == 0 && !a.EnableAutoResizeDelay,
                "Negative delay/group semantics changed EnableAutoResizeDelay.");

            void CheckRollback(Action operation, bool injectFailure = true)
            {
                var json = File.ReadAllText(path);
                var model = JsonConvert.SerializeObject(ConfigFactory.Profiles);
                var snapshot = runtime.CurrentAutoResizeSnapshot;
                var monitor = Monitor(runtime);
                var generation = Generation(runtime);
                var tokens = runtime.GetProcesses(a.ProfileId).Select(row => row.ProcessToken).ToArray();
                var count = events;
                failSave = injectFailure;
                try { Expect<IOException>(operation); }
                finally { failSave = false; }
                Assert(File.ReadAllText(path) == json && JsonConvert.SerializeObject(ConfigFactory.Profiles) == model,
                    "Save failure did not restore the model/disk.");
                Assert(ReferenceEquals(snapshot, runtime.CurrentAutoResizeSnapshot), "Save failure published a new snapshot.");
                Assert(ReferenceEquals(monitor, Monitor(runtime)), "Save failure changed monitoring.");
                Assert(events == count && Generation(runtime) == generation, "Save failure emitted an event/cancelled pending.");
                Assert(runtime.GetProcesses(a.ProfileId).Select(row => row.ProcessToken).SequenceEqual(tokens),
                    "Rollback changed identity/order.");
                Assert(ReferenceEquals(list, a.WindowSizes), "API replaced the entire list.");
            }

            CheckRollback(() => runtime.AddProcess(a.ProfileId, "failed.exe", "*", 0, 0, 500, 400));
            CheckRollback(() => runtime.RemoveProcess(a.ProfileId, token));
            CheckRollback(() => runtime.UpdateProcessName(a.ProfileId, token, "firefox.exe"));
            CheckRollback(() => runtime.UpdateProcessTitle(a.ProfileId, token, "failed"));
            CheckRollback(() => runtime.UpdateProcessTop(a.ProfileId, token, 100));
            CheckRollback(() => runtime.UpdateProcessLeft(a.ProfileId, token, 100));
            CheckRollback(() => runtime.UpdateProcessRight(a.ProfileId, token, 100));
            CheckRollback(() => runtime.UpdateProcessBottom(a.ProfileId, token, 100));
            CheckRollback(() => runtime.SetProcessDelay(a.ProfileId, token, 800));
            // Denying delete sharing exercises the real atomic File.Replace failure,
            // not just the injected persistence seam. Reads remain available to assertions.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                CheckRollback(() => runtime.SetProcessDelay(a.ProfileId, token, 800), injectFailure: false);
            CheckRollback(() => runtime.SetProcessAutoResize(a.ProfileId, token, true));
            Assert(!a.WindowSizes.Any(row => row.AutoResize) && Monitor(runtime) is null, "Failed first Auto ON started monitoring.");
            runtime.SetProcessAutoResize(a.ProfileId, token, true);
            var firstMonitor = Monitor(runtime);
            Assert(firstMonitor is not null, "First Auto ON did not start monitoring.");
            CheckRollback(() => runtime.SetProcessAutoResize(a.ProfileId, token, false));
            runtime.SetProcessAutoResize(a.ProfileId, duplicateToken, true);
            Assert(ReferenceEquals(firstMonitor, Monitor(runtime)), "Second Auto ON duplicated monitoring.");
            runtime.SetProcessAutoResize(a.ProfileId, token, false);
            Assert(Monitor(runtime) is not null, "Auto OFF stopped monitoring with another active rule.");
            runtime.SetProcessAutoResize(a.ProfileId, duplicateToken, false);
            Assert(Monitor(runtime) is null, "Last Auto OFF did not stop monitoring.");

            var validationEvents = events;
            var validationSaves = saves;
            Expect<ArgumentException>(() => runtime.AddProcess(a.ProfileId, " ", "*", 0, 0, 1, 1));
            Expect<ArgumentException>(() => runtime.UpdateProcessName(a.ProfileId, token, ""));
            Expect<ArgumentNullException>(() => runtime.UpdateProcessTitle(a.ProfileId, token, null!));
            Expect<ArgumentException>(() => runtime.UpdateProcessTop(b.ProfileId, token, 1));
            Expect<ArgumentException>(() => runtime.RemoveProcess(a.ProfileId, "unknown"));
            runtime.UpdateProcessTitle(a.ProfileId, token, legacy.Title);
            runtime.SetProcessDelay(a.ProfileId, token, 0);
            runtime.SetProcessAutoResize(a.ProfileId, token, false);
            Assert(events == validationEvents && saves == validationSaves, "Validation/no-op emitted success or persisted.");

            var bRow = runtime.AddProcess(b.ProfileId, "b.exe", "*", 0, 0, 600, 500);
            runtime.SetProcessAutoResize(b.ProfileId, bRow.ProcessToken, true);
            Assert(Monitor(runtime) is null, "Inactive profile Auto ON started active monitoring.");
            Assert(runtime.SwitchProfile(b.ProfileId) && Monitor(runtime) is not null, "Profile B did not start its monitoring.");
            Assert(runtime.SwitchProfile(a.ProfileId) && Monitor(runtime) is null, "Profile A did not stop B monitoring.");
            Assert(Find(runtime, a.ProfileId, token).ProcessToken == token &&
                Find(runtime, b.ProfileId, bRow.ProcessToken).ProcessToken == bRow.ProcessToken,
                "A-B-A changed runtime identities.");

            window = new Form { Text = "before delay", StartPosition = FormStartPosition.Manual,
                Left = 40, Top = 50, Width = 260, Height = 180 };
            window.Show();
            Pump(100);
            var originalRect = Resizer.GetPlacement(window.Handle).Rect;
            var realProcess = Resizer.GetRealProcessName(window.Handle)!;
            Assert(!string.IsNullOrWhiteSpace(realProcess), "Cannot resolve test window process.");
            var real = runtime.AddProcess(a.ProfileId, realProcess, "*", 220, 420, 680, 400);
            a.EnableAutoResizeDelay = true;
            ConfigFactory.Save();
            runtime.SetProcessDelay(a.ProfileId, real.ProcessToken, 1200);
            runtime.SetProcessAutoResize(a.ProfileId, real.ProcessToken, true);
            Pump(100);
            Assert(Resizer.GetPlacement(window.Handle).Rect.Left == originalRect.Left,
                "Auto ON resized an already-open window.");
            Coordinator(runtime).Schedule(window.Handle);
            Pump(150);
            var generationBeforeEdit = Generation(runtime);
            Assert(Pending(runtime) == 1, "Test did not start one pending operation.");
            window.Text = "fresh title after delay";
            runtime.UpdateProcessTitle(a.ProfileId, real.ProcessToken, window.Text);
            runtime.UpdateProcessLeft(a.ProfileId, real.ProcessToken, 460);
            runtime.SetProcessDelay(a.ProfileId, real.ProcessToken, 0);
            ConfigFactory.Save();
            Assert(Generation(runtime) == generationBeforeEdit && Pending(runtime) == 1,
                "Ordinary Update/Delay/Save cancelled pending.");
            Pump(200);
            Assert(Resizer.GetPlacement(window.Handle).Rect.Left == originalRect.Left,
                "Editing Delay restarted/shortened the old wait.");
            Pump(1100);
            Assert(Resizer.GetPlacement(window.Handle, true).Rect.Left == 460 && Pending(runtime) == 0,
                "Pending did not use fresh snapshot/Title/geometry after its original wait.");

            runtime.SetProcessDelay(a.ProfileId, real.ProcessToken, 1200);
            Coordinator(runtime).Schedule(window.Handle);
            Pump(150);
            var generationBeforeOff = Generation(runtime);
            var rectBeforeOff = Resizer.GetPlacement(window.Handle).Rect;
            runtime.SetProcessAutoResize(a.ProfileId, real.ProcessToken, false);
            Assert(Monitor(runtime) is null && Pending(runtime) == 1 && Generation(runtime) == generationBeforeOff,
                "Last Auto OFF cancelled pending instead of only stopping monitoring.");
            Pump(1300);
            Assert(Pending(runtime) == 0 && Resizer.GetPlacement(window.Handle).Rect.Left == rectBeforeOff.Left,
                "Pending resized through a disabled rule or did not clean up.");
            runtime.SetProcessAutoResize(a.ProfileId, real.ProcessToken, true);
            Coordinator(runtime).Schedule(window.Handle);
            Pump(150);
            var generationBeforeSwitch = Generation(runtime);
            runtime.SwitchProfile(b.ProfileId);
            runtime.SwitchProfile(a.ProfileId);
            Assert(Generation(runtime) == generationBeforeSwitch + 2 && Pending(runtime) == 0,
                "A-B-A did not cancel old pending work.");
            Assert(Find(runtime, a.ProfileId, real.ProcessToken).ProcessToken == real.ProcessToken,
                "Profile switch invalidated a surviving rule token.");

            Coordinator(runtime).Schedule(window.Handle);
            Pump(150);
            var importPath = Path.Combine(directory, "same-id-import.json");
            File.WriteAllText(importPath, JsonConvert.SerializeObject(ConfigFactory.Profiles));
            var generationBeforeImport = Generation(runtime);
            var eventsBeforeImport = events;
            ConfigFactory.Load(importPath);
            Assert(ConfigFactory.Current.ProfileId == a.ProfileId && events == eventsBeforeImport + 1,
                "Same-ID import did not publish exactly one ProcessesChanged.");
            Assert(Generation(runtime) == generationBeforeImport + 1 && Pending(runtime) == 0,
                "Import did not invalidate pending work.");
            validationEvents = events;
            Expect<ArgumentException>(() => runtime.UpdateProcessTitle(a.ProfileId, token, "stale imported token"));
            Expect<ArgumentException>(() => runtime.SetProcessAutoResize(a.ProfileId, real.ProcessToken, false));
            Expect<ArgumentException>(() => runtime.RemoveProcess(b.ProfileId, bRow.ProcessToken));
            Assert(events == validationEvents && runtime.GetProcesses(a.ProfileId).All(row => row.ProcessToken != token),
                "Configuration replacement reused/accepted stale identity.");
            Assert(!File.ReadAllText(path).Contains("ProcessToken"), "Runtime identity leaked into JSON.");
            runtime.Dispose();
            validationEvents = events;
            ConfigFactory.Save();
            Assert(events == validationEvents, "Disposed runtime kept ConfigurationSaved subscription.");
            Expect<InvalidOperationException>(() => runtime.GetProcesses(a.ProfileId));
            Console.WriteLine($"PASS: Processes identity, CRUD/rollback, grouped Delay, monitoring, profile/import, pending semantics ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            window?.Dispose();
            runtime?.Dispose();
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
