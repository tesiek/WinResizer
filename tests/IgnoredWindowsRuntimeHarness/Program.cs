using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using WinResizer.Base;
using WinResizer.Common.Windows;
using WinResizer.Configuration;
using WinResizer.Core.WindowControl;
using WinResizer.Runtime;

internal static class Program
{
    private static int _assertions;
    private static void Assert(bool value, string message)
    {
        _assertions++;
        if (!value) throw new InvalidOperationException(message);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static object Private(object owner, string field) => owner.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static AutoResizeCoordinator Coordinator(WinResizerRuntime runtime) =>
        (AutoResizeCoordinator)Private(runtime, "_autoResizeCoordinator");
    private static long Generation(WinResizerRuntime runtime) => (long)Private(Coordinator(runtime), "_generation");
    private static int Pending(WinResizerRuntime runtime) => ((IDictionary)Private(Coordinator(runtime), "_pending")).Count;
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinResizer-Ignored-" + Guid.NewGuid().ToString("N"));
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
            ConfigFactory.Save();
            var fail = false;
            var saves = 0;
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
            {
                EnableGlobalHotkeys = false, EnableWindowMonitoring = false,
                SaveConfiguration = () => { saves++; if (fail) throw new IOException("Injected failure"); ConfigFactory.Save(); },
            });
            runtime.Start();
            var events = 0;
            runtime.IgnoredWindowsChanged += (_, _) =>
            {
                events++;
                Assert(JsonConvert.SerializeObject(runtime.CurrentAutoResizeSnapshot!.IgnoredWindows) ==
                    JsonConvert.SerializeObject(ConfigFactory.Profiles.GetIgnoredWindowsSnapshot()), "Event before snapshot publication");
            };
            Assert(runtime.GetIgnoredWindows().Count == 0, "Initial list not empty");
            Assert(typeof(RuntimeIgnoredWindowInfo).GetProperties().All(p => p.SetMethod is null), "Writable DTO");
            var draft = runtime.AddIgnoredWindow();
            var token = draft.IgnoredWindowToken;
            Assert(events == 1 && draft.Active && draft.Process == "" && draft.Class == "" && draft.Title == "" &&
                draft.TitleMatch == IgnoredWindowTitleMatch.Any, "Draft defaults/event");
            bool Match(string process = "APP.EXE", string cls = "EDITOR", string title = "REPORT") =>
                IgnoredWindowMatcher.IsIgnoredForAutoResize(ConfigFactory.Profiles.GetIgnoredWindowsSnapshot(), process, () => cls, title);
            Assert(!Match(), "Empty draft matches");
            runtime.UpdateIgnoredWindowProcess(token, "app.exe");
            runtime.UpdateIgnoredWindowClass(token, "Editor");
            runtime.UpdateIgnoredWindowTitle(token, "Report");
            foreach (IgnoredWindowTitleMatch mode in Enum.GetValues(typeof(IgnoredWindowTitleMatch)))
            {
                runtime.SetIgnoredWindowTitleMatch(token, mode);
                var title = mode == IgnoredWindowTitleMatch.Contains ? "my REPORT today" :
                    mode == IgnoredWindowTitleMatch.StartsWith ? "REPORT today" :
                    mode == IgnoredWindowTitleMatch.EndsWith ? "my REPORT" : "REPORT";
                Assert(Match(title: title), "Case-insensitive title mode " + mode);
                Assert(!Match(process: "other.exe", title: title) && !Match(cls: "other", title: title), "AND " + mode);
                if (mode != IgnoredWindowTitleMatch.Any) Assert(!Match(title: "unrelated"), "Title condition " + mode);
                Assert(IgnoredWindowMatcher.IsIgnoredBeforeTitle(ConfigFactory.Profiles.GetIgnoredWindowsSnapshot(), "APP.EXE", () => "EDITOR") ==
                    (mode == IgnoredWindowTitleMatch.Any), "Before-delay mode " + mode);
            }
            runtime.SetIgnoredWindowTitleMatch(token, IgnoredWindowTitleMatch.Any);
            Assert(Match(title: "unrelated") && runtime.GetIgnoredWindows()[0].Title == "Report", "Any erased/did not ignore Title");
            runtime.SetIgnoredWindowActive(token, false);
            Assert(!Match(), "Inactive matches");
            runtime.SetIgnoredWindowActive(token, true);
            runtime.SetIgnoredWindowTitleMatch(token, IgnoredWindowTitleMatch.Exact);
            runtime.UpdateIgnoredWindowTitle(token, "");
            Assert(!Match() && runtime.GetIgnoredWindows().Count == 1, "Incomplete draft");
            runtime.UpdateIgnoredWindowTitle(token, "Report");
            var duplicate = runtime.AddIgnoredWindow("app.exe", "Editor", "Report", IgnoredWindowTitleMatch.Exact);
            Assert(duplicate.IgnoredWindowToken != token, "Duplicate content identity");
            var or = runtime.AddIgnoredWindow("other.exe");
            Assert(Match("OTHER.EXE", "anything", "anything"), "OR / optional Class");
            var backendOrder = runtime.GetIgnoredWindows().Select(row => row.IgnoredWindowToken).ToArray();
            runtime.GetIgnoredWindows().OrderByDescending(row => row.Process).ToArray();
            Assert(runtime.GetIgnoredWindows().Select(row => row.IgnoredWindowToken).SequenceEqual(backendOrder), "View sorting mutated order");
            runtime.RemoveIgnoredWindow(token);
            runtime.UpdateIgnoredWindowTitle(duplicate.IgnoredWindowToken, "changed");
            Assert(runtime.GetIgnoredWindows()[0].Title == "changed", "Identity used index");
            Expect<ArgumentException>(() => runtime.RemoveIgnoredWindow(token));
            token = duplicate.IgnoredWindowToken;
            Assert(draft.Process == "" && draft.Title == "", "DTO not detached");

            void Rollback(Action operation, bool injected = true)
            {
                var disk = File.ReadAllText(path);
                var model = JsonConvert.SerializeObject(ConfigFactory.Profiles);
                var snapshot = runtime.CurrentAutoResizeSnapshot;
                var ids = runtime.GetIgnoredWindows().Select(row => row.IgnoredWindowToken).ToArray();
                var count = events;
                var generation = Generation(runtime);
                fail = injected;
                try { Expect<IOException>(operation); }
                finally { fail = false; }
                Assert(disk == File.ReadAllText(path) && model == JsonConvert.SerializeObject(ConfigFactory.Profiles), "Rollback model/disk");
                Assert(ReferenceEquals(snapshot, runtime.CurrentAutoResizeSnapshot) && events == count && Generation(runtime) == generation,
                    "Failure published snapshot/event or cancelled pending");
                Assert(runtime.GetIgnoredWindows().Select(row => row.IgnoredWindowToken).SequenceEqual(ids), "Rollback order/identity");
            }
            Rollback(() => runtime.AddIgnoredWindow("failed.exe"));
            Rollback(() => runtime.RemoveIgnoredWindow(token));
            Rollback(() => runtime.UpdateIgnoredWindowProcess(token, "failed.exe"));
            Rollback(() => runtime.UpdateIgnoredWindowClass(token, "failed"));
            Rollback(() => runtime.UpdateIgnoredWindowTitle(token, "failed"));
            Rollback(() => runtime.SetIgnoredWindowActive(token, false));
            Rollback(() => runtime.SetIgnoredWindowTitleMatch(token, IgnoredWindowTitleMatch.Contains));
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Rollback(() => runtime.RemoveIgnoredWindow(token), false);
            var before = events;
            var beforeSaves = saves;
            Expect<ArgumentNullException>(() => runtime.UpdateIgnoredWindowProcess(token, null!));
            Expect<ArgumentNullException>(() => runtime.UpdateIgnoredWindowClass(token, null!));
            Expect<ArgumentNullException>(() => runtime.UpdateIgnoredWindowTitle(token, null!));
            Expect<ArgumentOutOfRangeException>(() => runtime.SetIgnoredWindowTitleMatch(token, (IgnoredWindowTitleMatch)99));
            Expect<ArgumentOutOfRangeException>(() => runtime.AddIgnoredWindow(titleMatch: (IgnoredWindowTitleMatch)99));
            runtime.SetIgnoredWindowActive(token, true);
            Assert(events == before && saves == beforeSaves, "Validation/no-op publishes success");
            runtime.SwitchProfile(b.ProfileId);
            runtime.SwitchProfile(a.ProfileId);
            Assert(runtime.GetIgnoredWindows()[0].IgnoredWindowToken == token, "A-B-A invalidated global identity");

            var oldObject = ConfigFactory.Profiles.IgnoredWindows[0];
            ConfigFactory.Profiles.IgnoredWindows[0] = new IgnoredWindowRule { Process = oldObject.Process };
            Expect<ArgumentException>(() => runtime.RemoveIgnoredWindow(token));
            var listToken = runtime.GetIgnoredWindows()[1].IgnoredWindowToken;
            ConfigFactory.Profiles.ReplaceIgnoredWindows(ConfigFactory.Profiles.IgnoredWindows.ToArray());
            Expect<ArgumentException>(() => runtime.RemoveIgnoredWindow(listToken));
            ConfigFactory.Save();
            foreach (var row in runtime.GetIgnoredWindows().ToArray()) runtime.RemoveIgnoredWindow(row.IgnoredWindowToken);

            window = new Form { Text = "pending ignored window", StartPosition = FormStartPosition.Manual,
                Left = 40, Top = 50, Width = 260, Height = 180 };
            window.Show();
            Pump(100);
            var original = Resizer.GetPlacement(window.Handle).Rect;
            var process = Resizer.GetRealProcessName(window.Handle)!;
            a.WindowSizes.Add(new WindowSize { Name = process, Title = "*", AutoResize = true, AutoResizeDelay = 900,
                Rect = new Rect(420, 220, 680, 400) });
            a.EnableAutoResizeDelay = true;
            ConfigFactory.Save();
            Coordinator(runtime).Schedule(window.Handle);
            Pump(120);
            Assert(Pending(runtime) == 1, "No pending work");
            var generationBefore = Generation(runtime);
            var blocker = runtime.AddIgnoredWindow(process, title: window.Text, titleMatch: IgnoredWindowTitleMatch.Exact);
            ConfigFactory.Save();
            Assert(Pending(runtime) == 1 && Generation(runtime) == generationBefore, "Ordinary mutation/Save cancelled pending");
            Pump(1000);
            Assert(Pending(runtime) == 0 && Resizer.GetPlacement(window.Handle).Rect.Left == original.Left, "Fresh ignored snapshot not used after delay");
            Assert(WindowUtils.ApplyPreset(window.Handle, new WindowPreset { X = 330, Y = 170, Width = 260, Height = 180 }), "Manual preset failed");
            Assert(Resizer.GetPlacement(window.Handle, true).Rect.Left == 330, "Ignored rule blocked manual preset");
            runtime.SetIgnoredWindowTitleMatch(blocker.IgnoredWindowToken, IgnoredWindowTitleMatch.Any);
            Coordinator(runtime).Schedule(window.Handle);
            Pump(120);
            Assert(Pending(runtime) == 0 && Resizer.GetPlacement(window.Handle, true).Rect.Left == 330, "Any not checked before delay");
            runtime.SetIgnoredWindowActive(blocker.IgnoredWindowToken, false);
            Coordinator(runtime).Schedule(window.Handle);
            Pump(120);
            generationBefore = Generation(runtime);
            runtime.RemoveIgnoredWindow(blocker.IgnoredWindowToken);
            Assert(Pending(runtime) == 1 && Generation(runtime) == generationBefore, "Remove cancelled pending");
            Pump(1000);
            Assert(Pending(runtime) == 0 && Resizer.GetPlacement(window.Handle, true).Rect.Left == 420, "Remove prevented eligible pending resize");
            blocker = runtime.AddIgnoredWindow(process, title: window.Text, titleMatch: IgnoredWindowTitleMatch.Exact, active: false);
            Coordinator(runtime).Schedule(window.Handle);
            Pump(120);
            Assert(Pending(runtime) == 1, "No pending import work");
            var import = Path.Combine(directory, "import.json");
            File.WriteAllText(import, JsonConvert.SerializeObject(ConfigFactory.Profiles));
            before = events;
            generationBefore = Generation(runtime);
            ConfigFactory.Load(import);
            Assert(events == before + 1 && Generation(runtime) == generationBefore + 1 && Pending(runtime) == 0, "Import refresh/invalidation");
            Expect<ArgumentException>(() => runtime.SetIgnoredWindowActive(blocker.IgnoredWindowToken, true));
            Assert(runtime.GetIgnoredWindows().Single().Process == process, "Imported global list not reflected");
            Assert(!File.ReadAllText(path).Contains("IgnoredWindowToken"), "Identity leaked to JSON");
            runtime.Dispose();
            before = events;
            ConfigFactory.Save();
            Assert(events == before, "Disposed event subscription");
            Expect<InvalidOperationException>(() => runtime.GetIgnoredWindows());
            runtime = new WinResizerRuntime(new WinResizerRuntimeOptions
                { EnableGlobalHotkeys = false, EnableWindowMonitoring = false, SaveConfiguration = () => { } });
            runtime.Start();
            runtime.AddIgnoredWindow("seam.exe");
            Assert(runtime.CurrentAutoResizeSnapshot!.IgnoredWindows.Any(rule => rule.Process == "seam.exe"), "Save seam did not publish snapshot");
            Console.WriteLine($"PASS: Ignored Windows runtime ({_assertions} assertions).");
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
