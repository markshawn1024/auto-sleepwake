using System.Diagnostics;
using System.Xml.Linq;
using WakeGlass.Core;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
if (args is ["--child-register", var childExe, var childName])
{
    new WakeScheduler(childExe).Register(new(childName, TaskKind.Probe, DateTime.Now.AddDays(1), Enabled: false));
    return 0;
}
if (args is ["--dump", var dumpExe])
{
    var dump = new WakeScheduler(dumpExe); var name = "Probe-" + Guid.NewGuid().ToString("N");
    var spec = new TaskSpec(name, TaskKind.Probe, DateTime.Now.AddDays(1), Enabled: false);
    dump.Register(spec);
    var svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
    dynamic service = svc; service.Connect(); dynamic folder = service.GetFolder(WakeScheduler.FolderPath); dynamic t = folder.GetTask(name);
    Console.WriteLine("EXPECTED: " + WakeScheduler.BuildXml(spec, dumpExe, System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value)); Console.WriteLine("ACTUAL: " + (string)t.Xml);
    dump.Delete(name); return 0;
}
var results = new List<string>();
int failed = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run(); results.Add("PASS " + name); Console.WriteLine(results[^1]); }
    catch (Exception ex) { failed++; results.Add("FAIL " + name + ": " + ex); Console.WriteLine(results[^1]); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
Task Sync(Action a) { a(); return Task.CompletedTask; }
async Task Throws(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected failure"); }
var now = new DateTime(2026, 11, 6, 12, 0, 0); // Friday
await Test("Daily next time today", () => Sync(() => Check(TimeMath.Next(new(23, 30, 0), 127, now) == now.Date.AddHours(23.5))));
await Test("Past daily event rolls to tomorrow", () => Sync(() => Check(TimeMath.Next(new(7, 50, 0), 127, now) == now.Date.AddDays(1).AddHours(7).AddMinutes(50))));
await Test("Weekdays skip weekend", () => Sync(() => Check(TimeMath.Next(new(7, 50, 0), 62, now).DayOfWeek == DayOfWeek.Monday)));
await Test("Friday sleep wakes on Saturday", () => Sync(() => { var s = DailySchedule.Default with { Days = 32 }; Check(s.WakeDays == 64); Check(TimeMath.WakeAfter(s, now).DayOfWeek == DayOfWeek.Saturday); }));
await Test("Saturday mask wraps to Sunday", () => Sync(() => Check(TimeMath.ShiftDays(64) == 1)));
await Test("All repeat masks rotate without loss", () => Sync(() => { for (int mask = 1; mask < 128; mask++) { int rotated = mask; for (int i = 0; i < 7; i++) rotated = TimeMath.ShiftDays(rotated); Check(rotated == mask); } }));
await Test("Daytime wake keeps selected weekday", () => Sync(() => { var s = new DailySchedule("daily", new(9, 0, 0), new(12, 0, 0), 62); Check(s.WakeDays == 62); Check(TimeMath.WakeAfter(s, now).Date == now.Date); }));
await Test("Year boundary crosses midnight", () => Sync(() => Check(TimeMath.WakeAfter(DailySchedule.Default, new DateTime(2026, 12, 31, 23, 30, 0)) == new DateTime(2027, 1, 1, 7, 50, 0))));
await Test("Leap day next event", () => Sync(() => Check(TimeMath.Next(new(1, 0, 0), 127, new DateTime(2028, 2, 28, 22, 0, 0)).Day == 29)));
await Test("Reject equal sleep and wake", () => Throws(() => Sync(() => TimeMath.Validate(DailySchedule.Default with { Wake = new(23, 30, 0) }))));
await Test("Reject no selected days", () => Throws(() => Sync(() => TimeMath.Validate(DailySchedule.Default with { Days = 0 }))));
await Test("Reject short overnight sleep", () => Throws(() => Sync(() => TimeMath.Validate(new("daily", new(23, 59, 0), new(0, 1, 0), 127)))));
await Test("Reject out-of-range times", () => Throws(() => Sync(() => TimeMath.Validate(DailySchedule.Default with { Sleep = TimeSpan.FromHours(24) }))));
await Test("Reject past and too near instant wake", () => Throws(() => Sync(() => TimeMath.ValidateWake(now.AddSeconds(90), now))));
await Test("Reject wake beyond 30 days", () => Throws(() => Sync(() => TimeMath.ValidateWake(now.AddDays(31), now))));
await Test("Accept future wake", () => Sync(() => TimeMath.ValidateWake(now.AddHours(1), now)));
await Test("Missing RTC rejects S3 wake", () => Sync(() => Check((new FakeCapabilities().Value with { S0 = false, RtcWake = false }).BlockReason != null)));
await Test("S0 reported separately from S3", () => Sync(() => Check((new FakeCapabilities().Value with { S3 = false, S0 = true, RtcWake = false }).BlockReason == null)));
await Test("Battery disabled timers block sleep", () => Sync(() => Check((new FakeCapabilities().Value with { OnAc = false, TimerPolicy = 0 }).BlockReason != null)));
await Test("Important-only timer policy blocks sleep", () => Sync(() => Check((new FakeCapabilities().Value with { TimerPolicy = 2 }).BlockReason != null)));
await Test("Low battery blocks sleep", () => Sync(() => Check((new FakeCapabilities().Value with { OnAc = false, BatteryPercent = 15 }).BlockReason != null)));
await Test("XML has safe power flags and interactive least privilege", () => Sync(() => { var xml = WakeScheduler.BuildXml(new("Instant-Wake", TaskKind.Wake, now), @"C:\Apps\A & B\WakeGlass.exe", "test-user"); var doc = XDocument.Parse(xml); XNamespace ns = doc.Root!.Name.Namespace; string Get(string n) => doc.Descendants(ns + n).Single().Value; Check(Get("WakeToRun") == "true"); Check(Get("RunLevel") == "LeastPrivilege"); Check(Get("LogonType") == "InteractiveToken"); Check(Get("Command").Contains("A & B")); Check(Get("AllowHardTerminate") == "false"); }));
await Test("Sleep task never wakes or catches up missed triggers", () => Sync(() => { var xml = WakeScheduler.BuildXml(new("Daily-Sleep", TaskKind.Sleep, now, 62), "C:\\WakeGlass.exe", "u"); var d = XDocument.Parse(xml); XNamespace n = d.Root!.Name.Namespace; Check(d.Descendants(n + "WakeToRun").Single().Value == "false"); Check(d.Descendants(n + "StartWhenAvailable").Single().Value == "false"); }));
await Test("Repeat save is idempotent", () => Sync(() => { var scheduler = new FakeScheduler(); var store = new FakeStore(); var manager = new ScheduleManager(scheduler, store, new FakeLog()); manager.Save(DailySchedule.Default with { Enabled = true }); manager.Save(DailySchedule.Default with { Enabled = true }); Check(scheduler.Tasks.Count == 2); }));
await Test("Pause updates both tasks and persistent state", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore(); var m = new ScheduleManager(sc, st, new FakeLog()); m.Save(DailySchedule.Default with { Enabled = true }); m.SetEnabled(false); Check(sc.Tasks.Values.All(x => !x.Enabled)); Check(st.Value.Schedule?.Enabled == false); }));
await Test("Delete removes pair without orphan tasks", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore(); var m = new ScheduleManager(sc, st, new FakeLog()); m.Save(DailySchedule.Default); m.DeleteDaily(); Check(sc.Tasks.Count == 0 && st.Value.Schedule == null); }));
await Test("Failed pair registration restores previous schedule", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore(); var m = new ScheduleManager(sc, st, new FakeLog()); m.Save(DailySchedule.Default); sc.FailName = "Daily-Sleep"; try { m.Save(DailySchedule.Default with { Enabled = true, Sleep = new(22, 0, 0) }); throw new Exception("Expected registration failure"); } catch (IOException) { } Check(sc.Tasks.Count == 2 && sc.Tasks.Values.All(x => !x.Enabled)); Check(st.Value.Schedule == DailySchedule.Default); }));
await Test("Settings failure rolls back task definitions", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore(); var m = new ScheduleManager(sc, st, new FakeLog()); m.Save(DailySchedule.Default); st.FailSave = true; try { m.Save(DailySchedule.Default with { Enabled = true }); } catch (IOException) { } Check(sc.Tasks.Count == 2 && sc.Tasks.Values.All(x => !x.Enabled)); }));
await Test("Closed GUI does not own or remove persisted schedules", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore(); new ScheduleManager(sc, st, new FakeLog()).Save(DailySchedule.Default); var reopened = new ScheduleManager(sc, st, new FakeLog()); Check(sc.Tasks.Count == 2 && reopened.Health() == null); }));
await Test("Missing wake task is detected", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore(); var m = new ScheduleManager(sc, st, new FakeLog()); m.Save(DailySchedule.Default); sc.Delete("Daily-Wake"); Check(m.Health() != null); }));
async Task InstantCase(string mode)
{
    var sc = new FakeScheduler { FailName = mode == "registration" ? "Instant-Wake" : null, BadVerify = mode == "verify" }; var power = new FakePower { Fail = mode == "power" }; var cap = new FakeCapabilities(); var log = new FakeLog(); var engine = new SleepCoordinator(sc, power, cap, log); bool countdownCalled = false;
    var work = engine.RunInstantAsync(DateTime.Now.AddHours(1), _ => { countdownCalled = true; if (mode == "tamper") sc.Delete("Instant-Wake"); if (mode == "powerchange") cap.Value = cap.Value with { TimerPolicy = 0 }; return Task.FromResult(mode != "cancel"); }, CancellationToken.None);
    if (mode == "success") { await work; Check(power.Calls == 1 && sc.Tasks.Count == 1); }
    else { await Throws(() => work); Check(power.Calls == (mode == "power" ? 1 : 0)); Check(sc.Tasks.Count == 0); if (mode is "registration" or "verify") Check(!countdownCalled); }
}
foreach (var mode in new[] { "success", "registration", "verify", "power", "cancel", "tamper", "powerchange" }) await Test("Instant safety path: " + mode, () => InstantCase(mode));
await Test("Capability rejection occurs before registration", async () => { var sc = new FakeScheduler(); var p = new FakePower(); var cap = new FakeCapabilities { Value = new FakeCapabilities().Value with { S3 = false, S0 = false } }; await Throws(() => new SleepCoordinator(sc, p, cap, new FakeLog()).RunInstantAsync(DateTime.Now.AddHours(1), _ => Task.FromResult(true), CancellationToken.None)); Check(sc.Tasks.Count == 0 && p.Calls == 0); });
await Test("Scheduled guard rejects missing wake and off-time/manual execution", async () => { var sc = new FakeScheduler(); var st = new FakeStore { Value = new(Schedule: DailySchedule.Default with { Enabled = true }) }; await Throws(() => Sync(() => ScheduledSleepGuard.Validate(sc, st, new FakeCapabilities(), now))); Check(sc.Tasks.Count == 0); });
await Test("Scheduled guard allows paired wake at expected next day", () => Sync(() => { var sc = new FakeScheduler(); var st = new FakeStore { Value = new(Schedule: DailySchedule.Default with { Enabled = true }) }; var at = now.Date.AddHours(23.5); foreach (var spec in ScheduleManager.Specs(st.Value.Schedule!, at)) sc.Register(spec); sc.OverrideNextWake = TimeMath.WakeAfter(st.Value.Schedule!, at); Check(ScheduledSleepGuard.Validate(sc, st, new FakeCapabilities(), at) == sc.OverrideNextWake); }));
await Test("Scheduled guard rejects tampered wake run time", async () => { var sc = new FakeScheduler(); var st = new FakeStore { Value = new(Schedule: DailySchedule.Default with { Enabled = true }) }; var at = now.Date.AddHours(23.5); foreach (var spec in ScheduleManager.Specs(st.Value.Schedule!, at)) sc.Register(spec); sc.OverrideNextWake = at.AddMinutes(1); await Throws(() => Sync(() => ScheduledSleepGuard.Validate(sc, st, new FakeCapabilities(), at))); });
await Test("Countdown crossing midnight keeps original sleep weekday", () => Sync(() => { var sc = new FakeScheduler(); var s = DailySchedule.Default with { Sleep = new(23, 59, 0), Days = 32, Enabled = true }; var st = new FakeStore { Value = new(Schedule: s) }; var at = now.Date.AddDays(1).AddSeconds(10); foreach (var spec in ScheduleManager.Specs(s, at)) sc.Register(spec); sc.OverrideNextWake = at.Date.Add(s.Wake); Check(ScheduledSleepGuard.Validate(sc, st, new FakeCapabilities(), at) == sc.OverrideNextWake); }));
await Test("First creation failure on sleep trigger leaves no wake orphan", async () => { var sc = new FakeScheduler { FailName = "Daily-Sleep" }; var st = new FakeStore(); await Throws(() => Sync(() => new ScheduleManager(sc, st, new FakeLog()).Save(DailySchedule.Default))); Check(sc.Tasks.Count == 0 && st.Value.Schedule == null); });
await Test("Settings atomically survive new store instance", () => Sync(() => { string dir = Path.Combine(Path.GetTempPath(), "WakeGlass-Test-" + Guid.NewGuid().ToString("N")); try { var st = new SettingsStore(dir); st.Save(new(Schedule: DailySchedule.Default)); Check(new SettingsStore(dir).Load().Schedule == DailySchedule.Default); Check(Directory.GetFiles(dir).Length == 1); } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); } }));
await Test("Diagnostics redact profile, user, device and SID", () => Sync(() => { var redacted = Diagnostics.Redact(Environment.UserName + " " + Environment.MachineName + " " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + " S-1-5-21-123-456-789"); Check(!redacted.Contains(Environment.UserName)); Check(!redacted.Contains("S-1-5-21")); }));

await StageTests.Run(Test);
if (args.Length == 2 && args[0] == "--integration")
{
    var exe = Path.GetFullPath(args[1]); var scheduler = new WakeScheduler(exe);
    await Test("Real Windows multi-stage registrations, update, worker launch and cleanup", () => StageTests.Integration(exe));
    await Test("Real Task Scheduler registration permission", () => Sync(() => Check(scheduler.ProbeAccess())));
    await Test("Real Task Scheduler repeated registration, readback, pause, delete", () => Sync(() =>
    {
        string name = "Probe-" + Guid.NewGuid().ToString("N"); var spec = new TaskSpec(name, TaskKind.Probe, DateTime.Now.AddDays(1));
        try { scheduler.Register(spec); scheduler.Register(spec); Check(scheduler.Verify(spec)); Check(scheduler.List().Count(x => x.Spec.Name == name) == 1); var paused = spec with { Enabled = false }; scheduler.Register(paused); Check(scheduler.Verify(paused)); }
        finally { scheduler.Delete(name); }
        Check(scheduler.List().All(x => x.Spec.Name != name));
    }));
    await Test("Real wake-to-run readback, no sleep action, immediate cleanup", () => Sync(() =>
    {
        string name = "Probe-" + Guid.NewGuid().ToString("N"); var spec = new TaskSpec(name, TaskKind.Wake, DateTime.Now.AddDays(1), Id: "instant");
        try { scheduler.Register(spec); Check(scheduler.Verify(spec)); Check(scheduler.List().Single(x => x.Spec.Name == name).NextRun != null); }
        finally { scheduler.Delete(name); }
    }));
    await Test("Real weekly cross-midnight day mask and next execution", () => Sync(() =>
    {
        string name = "Probe-" + Guid.NewGuid().ToString("N"); var spec = new TaskSpec(name, TaskKind.Probe, DateTime.Now.AddDays(1), 64, false);
        try { scheduler.Register(spec); Check(scheduler.Verify(spec)); Check(scheduler.List().Single(x => x.Spec.Name == name).Spec.Days == 64); }
        finally { scheduler.Delete(name); }
    }));
    await Test("Real tasks survive owner process exit", async () =>
    {
        string name = "Probe-" + Guid.NewGuid().ToString("N");
        // Registration performed in a separate process that exits; parent reads the service state.
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--child-register"); start.ArgumentList.Add(exe); start.ArgumentList.Add(name);
        try { using var process = Process.Start(start)!; await process.WaitForExitAsync(); Check(process.ExitCode == 0); Check(scheduler.List().Any(x => x.Spec.Name == name)); }
        finally { scheduler.Delete(name); }
    });
    await Test("Real capability detection and diagnostic commands", async () => { var c = new SystemCapabilityService(scheduler).Detect(); Check(c.CanSleep); var report = await new Diagnostics().ReportAsync(c); Check(report.Contains("powercfg /lastwake")); Console.WriteLine(c.Summary); });
    await Test("Real timed task executes packaged probe without GUI or sleep", async () =>
    {
        string name = "Probe-" + Guid.NewGuid().ToString("N"); var spec = new TaskSpec(name, TaskKind.Probe, DateTime.Now.AddSeconds(8));
        try
        {
            scheduler.Register(spec); Check(scheduler.Verify(spec));
            var deadline = DateTime.Now.AddSeconds(45);
            while (DateTime.Now < deadline)
            {
                var info = scheduler.List().Single(x => x.Spec.Name == name);
                if (info.LastRun is not null && info.LastResult == 0) return;
                await Task.Delay(1000);
            }
            throw new Exception("Packaged probe did not finish successfully within 45 seconds");
        }
        finally { scheduler.Delete(name); }
    });
}
Directory.CreateDirectory("TestResults");
await File.WriteAllLinesAsync("TestResults/results.txt", results.Append($"Total: {results.Count}; Passed: {results.Count - failed}; Failed: {failed}"));
Console.WriteLine($"Total: {results.Count}; Passed: {results.Count - failed}; Failed: {failed}");
return failed == 0 ? 0 : 1;

sealed class FakeScheduler : IWakeScheduler
{
    public Dictionary<string, TaskSpec> Tasks = []; public string? FailName; public bool BadVerify; public DateTime? OverrideNextWake;
    public void Register(TaskSpec s) { if (s.Name == FailName) { FailName = null; throw new IOException("Injected scheduler failure"); } Tasks[s.Name] = s; }
    public bool Verify(TaskSpec s) => !BadVerify && Tasks.TryGetValue(s.Name, out var found) && found.Kind == s.Kind && found.Enabled == s.Enabled && found.Days == s.Days && found.Start.TimeOfDay == s.Start.TimeOfDay;
    public IReadOnlyList<ScheduledTaskInfo> List() => Tasks.Values.Select(t => new ScheduledTaskInfo(t, t.Kind == TaskKind.Wake && OverrideNextWake != null ? OverrideNextWake : t.Start, null, 267011, t.Enabled ? "就绪" : "已暂停")).ToArray();
    public void Delete(string name) => Tasks.Remove(name);
}
sealed class FakePower : IPowerService { public int Calls; public bool Fail; public void Sleep() { Calls++; if (Fail) throw new IOException("Injected power failure"); } }
sealed class FakeCapabilities : ICapabilityService { public Capabilities Value = new(true, false, true, true, 1, true, 80, true, ""); public Capabilities Detect() => Value; }
sealed class FakeStore : ISettingsStore { public AppSettings Value = new(); public bool FailSave; public AppSettings Load() => Value; public void Save(AppSettings s) { if (FailSave) throw new IOException("Injected settings failure"); Value = s; } }
sealed class FakeLog : IDiagnostics { public void Write(string code, string message) { } }
