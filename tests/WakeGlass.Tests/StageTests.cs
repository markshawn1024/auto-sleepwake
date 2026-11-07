using WakeGlass.Core;

internal static class StageTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await Case("07:25 holds until 07:32, then sleeps once with 08:25 intact", () =>
        {
            var scenario = new Scenario();
            scenario.Tick();
            scenario.AdvanceMinutes(7);
            Check(scenario.Power.Sleeps == 1 && !scenario.Power.Holding);
            Check(scenario.RunStore.Value?.Phase == StageRunPhase.SleepRequested);
            Check(scenario.Scheduler.Tasks.ContainsKey(StageScheduleManager.WakeName(scenario.Persistent.Id)));
            scenario.AdvanceMinutes(1);
            Check(scenario.Power.Sleeps == 1);
        });

        await Case("08:25 stage holds indefinitely and resumes after worker restart", () =>
        {
            var scenario = new Scenario();
            scenario.Clock.Move(TimeSpan.FromHours(1));
            scenario.Tick();
            Check(scenario.Power.Holding && scenario.RunStore.Value?.Id == scenario.Persistent.Id);
            scenario.Restart();
            scenario.Clock.Move(TimeSpan.FromHours(3));
            scenario.Tick();
            Check(scenario.Power.Holding && scenario.Power.Sleeps == 0);
            scenario.Tick(scenario.Plan with { Enabled = false });
            Check(!scenario.Power.Holding);
        });

        await Case("Manual sleep consumes persistent occurrence until next scheduled stage", () =>
        {
            var scenario = new Scenario();
            scenario.Clock.Move(TimeSpan.FromHours(1));
            scenario.Tick();
            scenario.Runner.Interrupt();
            scenario.Restart();
            scenario.Clock.Move(TimeSpan.FromMinutes(1));
            scenario.Tick();
            Check(!scenario.Power.Holding && scenario.RunStore.Value?.Phase == StageRunPhase.Interrupted);
            scenario.Clock.Move(TimeSpan.FromHours(23));
            scenario.Tick();
            Check(scenario.Power.Holding);
        });

        await Case("Worker restart during timed stage restores only the remaining time", () =>
        {
            var scenario = new Scenario();
            scenario.Tick();
            scenario.AdvanceMinutes(5);
            scenario.Restart();
            scenario.Tick();
            Check(scenario.Power.Holding);
            scenario.AdvanceMinutes(2);
            Check(scenario.Power.Sleeps == 1);
        });

        await Case("Deleting the current stage cannot reactivate an older occurrence", () =>
        {
            var scenario = new Scenario();
            scenario.Clock.Move(TimeSpan.FromHours(1));
            scenario.Tick();
            var changed = scenario.Plan with { Items = [scenario.Timed] };
            scenario.Tick(changed);
            scenario.Restart();
            scenario.Tick(changed);
            Check(!scenario.Power.Holding && scenario.Power.Sleeps == 0);
        });

        await Case("Worker restart after a missed deadline never backfills sleep", () =>
        {
            var scenario = new Scenario();
            scenario.Tick();
            scenario.Restart();
            scenario.Clock.Move(TimeSpan.FromMinutes(7));
            scenario.Tick();
            Check(scenario.Power.Sleeps == 0 && scenario.RunStore.Value?.Phase == StageRunPhase.Skipped);
        });

        await Case("A clock correction at the deadline cannot trigger sleep", () =>
        {
            var scenario = new Scenario();
            scenario.Tick();
            scenario.Clock.Jump(TimeSpan.FromMinutes(7));
            scenario.Tick();
            Check(scenario.Power.Sleeps == 0 && !scenario.Power.Holding);
        });

        await Case("A timezone change cannot replay a held stage's sleep", () =>
        {
            var scenario = new Scenario();
            scenario.Tick();
            scenario.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("new-zone", TimeSpan.FromMinutes(7), "new-zone", "new-zone");
            scenario.Tick();
            Check(scenario.Power.Sleeps == 0 && !scenario.Power.Holding);
        });

        await Case("User activity at 07:32 skips sleep and leaves the next stage", () =>
        {
            var scenario = new Scenario();
            scenario.Power.Idle = TimeSpan.FromSeconds(10);
            scenario.Tick();
            scenario.AdvanceMinutes(7);
            Check(scenario.Power.Sleeps == 0 && scenario.RunStore.Value?.Message.Contains("用户操作") == true);
            Check(scenario.Scheduler.Tasks.ContainsKey(StageScheduleManager.WakeName(scenario.Persistent.Id)));
        });

        await Case("Unknown session activity blocks sleep", () =>
        {
            var scenario = new Scenario();
            scenario.Power.Idle = null;
            scenario.Tick();
            scenario.AdvanceMinutes(7);
            Check(scenario.Power.Sleeps == 0 && scenario.RunStore.Value?.Phase == StageRunPhase.Skipped);
        });

        await Case("Missing future wake and disabled wake timers each block sleep", () =>
        {
            var missing = new Scenario();
            missing.Scheduler.Delete(StageScheduleManager.WakeName(missing.Persistent.Id));
            missing.Tick();
            missing.AdvanceMinutes(7);
            Check(missing.Power.Sleeps == 0 && missing.RunStore.Value?.Message.Contains("唤醒任务缺失") == true);
            var disabled = new Scenario();
            disabled.Capabilities.Value = disabled.Capabilities.Value with { TimerPolicy = 0 };
            disabled.Tick();
            disabled.AdvanceMinutes(7);
            Check(disabled.Power.Sleeps == 0 && disabled.RunStore.Value?.Message.Contains("唤醒计时器") == true);
        });

        await Case("Hold failure prevents later automatic sleep", () =>
        {
            var scenario = new Scenario();
            scenario.Power.FailHold = true;
            scenario.Tick();
            scenario.AdvanceMinutes(7);
            Check(scenario.Power.Sleeps == 0 && scenario.RunStore.Value?.Phase == StageRunPhase.Error);
        });

        await Case("Weekend and weekday masks select different stages", () =>
        {
            var scenario = new Scenario();
            var plan = new StagePlan(true, [scenario.Timed with { Days = 62 }, scenario.Persistent with { Days = 65 }]);
            var saturday = scenario.Clock.GetUtcNow().AddDays(1).AddHours(2);
            Check(StageTimeline.Latest(plan, saturday, TimeZoneInfo.Utc)?.Stage.Id == scenario.Persistent.Id);
            Check(StageTimeline.Next(plan, saturday.AddDays(1), TimeZoneInfo.Utc)?.Stage.Id == scenario.Timed.Id);
        });

        await Case("Friday 23:58 timed stage ends on Saturday with correct elapsed time", () =>
        {
            var scenario = new Scenario();
            var friday = scenario.Clock.GetUtcNow().Date.AddHours(23).AddMinutes(58);
            var stage = scenario.Timed with { Wake = friday.TimeOfDay, Days = 32 };
            var occurrence = StageTimeline.Find(stage, new DateTimeOffset(friday.AddMinutes(5), TimeSpan.Zero), TimeZoneInfo.Utc, false);
            Check(occurrence?.End == new DateTimeOffset(friday.AddMinutes(7), TimeSpan.Zero));
        });

        await Case("Reject duplicate and overlapping stages across Sunday week boundary", () =>
        {
            var scenario = new Scenario();
            Reject(() => StageTimeline.Validate(new(true, [scenario.Timed, scenario.Persistent with { Wake = scenario.Timed.Wake }]), null));
            var saturday = scenario.Timed with { Wake = new(23, 58, 0), Days = 64 };
            var sunday = scenario.Persistent with { Wake = new(0, 3, 0), Days = 1 };
            Reject(() => StageTimeline.Validate(new(true, [saturday, sunday]), null));
            StageTimeline.Validate(new(true, [saturday, sunday with { Enabled = false }]), null);
        });

        await Case("DST spring gaps and repeated fall times are skipped", () =>
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var scenario = new Scenario();
            var stage = scenario.Timed with { Wake = new(2, 30, 0), Days = 1 };
            var spring = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5));
            Check(StageTimeline.Find(stage, spring, zone, true)?.Start.Day == 15);
            var fall = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
            Check(StageTimeline.Find(stage with { Wake = new(1, 30, 0) }, fall, zone, true)?.Start.Day == 8);
        });

        await Case("Saving either schedule rejects legacy sleep and wake collisions", () =>
        {
            var scenario = new Scenario();
            var old = DailySchedule.Default with { Enabled = true, Sleep = new(7, 30, 0), Wake = new(9, 0, 0) };
            Reject(() => StageTimeline.Validate(scenario.Plan, old));
            Reject(() => new ScheduleManager(scenario.Scheduler, scenario.Settings, scenario.Log).Save(old));
            Reject(() => StageTimeline.Validate(scenario.Plan, old with { Sleep = new(23, 0, 0), Wake = new(8, 25, 0) }));
        });

        await Case("Register, edit, disable, delete and pause use stable task IDs", () =>
        {
            var scenario = new Scenario();
            scenario.Manager.Save(scenario.Plan);
            scenario.Manager.Save(scenario.Plan);
            Check(scenario.Scheduler.Tasks.Count == 3);
            var edited = scenario.Persistent with { Wake = new(9, 0, 0) };
            var changed = new StagePlan(true, [edited, scenario.Timed with { Enabled = false }]);
            scenario.Manager.Save(changed);
            Check(scenario.Scheduler.Tasks.Count == 2 && scenario.Scheduler.Tasks[StageScheduleManager.WakeName(edited.Id)].Start.Hour == 9);
            scenario.Manager.Save(changed with { Items = [edited] });
            Check(scenario.Settings.Value.Stages?.Items.Length == 1);
            scenario.Manager.Save(changed with { Enabled = false });
            Check(scenario.Scheduler.Tasks.Count == 0);
        });

        await Case("Failed stage registration restores old tasks and saved configuration", () =>
        {
            var scenario = new Scenario();
            var snapshot = scenario.Scheduler.Tasks.ToDictionary();
            var original = scenario.Settings.Value;
            scenario.Scheduler.FailName = StageScheduleManager.WakeName(scenario.Persistent.Id);
            var changed = new StagePlan(true, [scenario.Timed with { Wake = new(7, 15, 0) }, scenario.Persistent with { Wake = new(9, 0, 0) }]);
            try { scenario.Manager.Save(changed); }
            catch (IOException)
            {
                Check(scenario.Settings.Value == original && snapshot.All(pair => scenario.Scheduler.Tasks[pair.Key] == pair.Value));
                return;
            }
            throw new Exception("Expected registration failure");
        });

        await Case("Old JSON with BOM and no stages retains single-stage settings", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "WakeGlass-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var file = Path.Combine(path, "settings.json");
            try
            {
                File.WriteAllText(file, "{\"Theme\":\"Dark\",\"CountdownSeconds\":23}", System.Text.Encoding.UTF8);
                var store = new SettingsStore(path);
                var original = store.Load();
                Check(original.Theme == "Dark" && original.CountdownSeconds == 23 && original.Stages == null);
                store.Save(original with { Stages = StagePlan.Empty });
                Check(store.Load().CountdownSeconds == 23 && store.Load().Stages?.Enabled == false);
            }
            finally { File.Delete(file); Directory.Delete(path); }
        });

        await Case("Configuration write failure rolls back stage task removals", () =>
        {
            var scenario = new Scenario();
            var saved = scenario.Settings.Value;
            scenario.Settings.FailSave = true;
            try { scenario.Manager.Save(scenario.Plan with { Enabled = false }); }
            catch (IOException)
            {
                Check(scenario.Settings.Value == saved && scenario.Scheduler.Tasks.Count == 3);
                return;
            }
            throw new Exception("Expected settings write failure");
        });

        Task Case(string name, Action action) => test("Stages: " + name, () => { action(); return Task.CompletedTask; });
    }

    public static async Task Integration(string exe)
    {
        var scheduler = new WakeScheduler(exe);
        Check(new SettingsStore().Load().Stages?.Active.Any() != true, "Pause the live stage plan before running this integration test.");
        Check(!scheduler.List().Any(task => StageScheduleManager.IsStageTask(task.Spec.Name)), "Existing stage tasks must be preserved.");
        var store = new FakeStore();
        var manager = new StageScheduleManager(scheduler, store, new FakeLog(), TimeProvider.System);
        var later = DateTime.Now.AddDays(1);
        var first = new WakeStage(Guid.NewGuid(), true, new(7, 25, 0), StageMode.Timed, 7, 1 << (int)later.DayOfWeek);
        var second = new WakeStage(Guid.NewGuid(), true, new(8, 25, 0), StageMode.Persistent, 0, first.Days);
        var plan = new StagePlan(true, [first, second]);
        try
        {
            manager.Save(plan);
            manager.Save(plan);
            var tasks = scheduler.List().Where(task => StageScheduleManager.IsStageTask(task.Spec.Name)).ToArray();
            Check(tasks.Length == 3 && manager.Health(plan, tasks) == null);
            Check(tasks.Count(task => task.Spec.Kind == TaskKind.StageWake && task.NextRun.HasValue) == 2);
            scheduler.StartStageWorker();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            DateTime? initialRun = null;
            while (DateTime.UtcNow < deadline)
            {
                var worker = scheduler.List().Single(task => task.Spec.Kind == TaskKind.StageWorker);
                if (worker.LastRun.HasValue && worker.LastResult == 0) { initialRun = worker.LastRun; break; }
                await Task.Delay(500);
            }
            Check(initialRun.HasValue, "Windows did not launch the packaged worker successfully.");
            deadline = DateTime.UtcNow.AddSeconds(75);
            bool recovered = false;
            while (DateTime.UtcNow < deadline)
            {
                var worker = scheduler.List().Single(task => task.Spec.Kind == TaskKind.StageWorker);
                if (worker.LastRun > initialRun && worker.LastResult == 0) { recovered = true; break; }
                await Task.Delay(1000);
            }
            Check(recovered, "The scheduled recovery trigger did not relaunch the packaged worker.");
            var changed = new StagePlan(true, [first with { Enabled = false }, second with { Wake = new(8, 40, 0) }]);
            manager.Save(changed);
            var updated = scheduler.List().Where(task => StageScheduleManager.IsStageTask(task.Spec.Name)).ToArray();
            Check(updated.Length == 2 && manager.Health(changed, updated) == null);
            Check(updated.Single(task => task.Spec.Kind == TaskKind.StageWake).NextRun?.TimeOfDay == new TimeSpan(8, 40, 0));
        }
        finally { manager.Save(StagePlan.Empty); }
        Check(!scheduler.List().Any(task => StageScheduleManager.IsStageTask(task.Spec.Name)));
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected validation to reject the schedule");
    }

    private static void Check(bool pass, string message = "Stage assertion failed")
    {
        if (!pass) throw new Exception(message);
    }

    private sealed class Scenario
    {
        public readonly TestClock Clock = new();
        public readonly WakeStage Timed = new(Guid.NewGuid(), true, new(7, 25, 0), StageMode.Timed, 7, 127);
        public readonly WakeStage Persistent = new(Guid.NewGuid(), true, new(8, 25, 0), StageMode.Persistent, 0, 127);
        public readonly FakeScheduler Scheduler = new();
        public readonly FakeStore Settings = new();
        public readonly FakeCapabilities Capabilities = new();
        public readonly FakeLog Log = new();
        public readonly TestPower Power = new();
        public readonly MemoryRunStore RunStore = new();
        public readonly StagePlan Plan;
        public readonly StageScheduleManager Manager;
        public StageRunner Runner;
        private readonly StageSleepGuard guard;

        public Scenario()
        {
            Plan = new(true, [Timed, Persistent]);
            Manager = new(Scheduler, Settings, Log, Clock);
            Manager.Save(Plan);
            Runner = new(Clock, RunStore, Power, Log);
            guard = new(Scheduler, Settings, Capabilities, Power, Log, Clock);
        }

        public void Restart()
        {
            Runner.Stop();
            Runner = new(Clock, RunStore, Power, Log);
        }

        public void Tick(StagePlan? plan = null) => Runner.Tick(plan ?? Plan, guard.Finish);

        public void AdvanceMinutes(int minutes)
        {
            for (int index = 0; index < minutes * 60; index++)
            {
                Clock.Move(TimeSpan.FromSeconds(1));
                Tick();
            }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 6, 7, 25, 0, TimeSpan.Zero);
        private long timestamp;
        public TimeZoneInfo Zone = TimeZoneInfo.Utc;
        public override TimeZoneInfo LocalTimeZone => Zone;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Move(TimeSpan elapsed) { now += elapsed; timestamp += elapsed.Ticks; }
        public void Jump(TimeSpan elapsed) => now += elapsed;
    }

    private sealed class TestPower : IStagePower
    {
        public bool Holding;
        public bool FailHold;
        public int Sleeps;
        public TimeSpan? Idle = TimeSpan.FromHours(1);
        public void KeepAwake(bool enabled)
        {
            if (enabled && FailHold) throw new IOException("Injected hold failure");
            Holding = enabled;
        }
        public TimeSpan? SessionIdleTime() => Idle;
        public void Sleep() { Check(!Holding, "Hold must be released before requesting sleep"); Sleeps++; }
    }

    private sealed class MemoryRunStore : IStageRunStore
    {
        public StageRunState? Value;
        public StageRunState? Load() => Value;
        public void Save(StageRunState state) => Value = state;
    }
}
