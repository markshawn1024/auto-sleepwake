namespace WakeGlass.Core;

public sealed class StageScheduleManager(IWakeScheduler scheduler, ISettingsStore store, IDiagnostics log, TimeProvider clock)
{
    public const string WorkerName = "Stage-Worker";
    public const string Prefix = "Stage-Wake-";
    public static string WakeName(Guid id) => Prefix + id.ToString("N");
    public static bool IsStageTask(string name) => name == WorkerName ||
        name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _);

    public static TaskSpec WakeSpec(WakeStage stage, DateTimeOffset now, TimeZoneInfo zone)
    {
        var next = StageTimeline.Find(stage, now, zone, future: true) ?? throw new InvalidOperationException("无法计算阶段的下次执行时间。");
        return new(WakeName(stage.Id), TaskKind.StageWake, next.Start.DateTime, stage.Days, Id: stage.Id.ToString("N"));
    }

    public static TaskSpec[] Specs(StagePlan plan, DateTimeOffset now, TimeZoneInfo zone)
    {
        var wakes = plan.Active.Select(stage => WakeSpec(stage, now, zone)).ToArray();
        if (wakes.Length == 0) return [];
        return [.. wakes, new(WorkerName, TaskKind.StageWorker, TimeZoneInfo.ConvertTime(now, zone).Date, Id: "stages")];
    }

    public void Save(StagePlan plan)
    {
        using var gate = ScheduleManager.Lock();
        var settings = store.Load();
        try
        {
            StageTimeline.Validate(plan, settings.Schedule);
            var desired = Specs(plan, clock.GetUtcNow(), clock.LocalTimeZone);
            var originals = scheduler.List().Where(task => IsStageTask(task.Spec.Name)).Select(task => task.Spec).ToArray();
            var touched = new HashSet<string>();
            try
            {
                foreach (var spec in desired)
                {
                    if (scheduler.Verify(spec)) continue;
                    touched.Add(spec.Name);
                    scheduler.Register(spec);
                    if (!scheduler.Verify(spec)) throw new IOException($"Windows 任务 {spec.Name} 回读失败。");
                }
                foreach (var old in originals.Where(old => !desired.Any(spec => spec.Name == old.Name)))
                {
                    touched.Add(old.Name);
                    scheduler.Delete(old.Name);
                }
                var sorted = plan with { Items = plan.Items.OrderBy(stage => stage.Wake).ToArray() };
                store.Save(settings with { Stages = sorted });
            }
            catch (Exception error)
            {
                try
                {
                    foreach (var name in touched)
                    {
                        var original = originals.FirstOrDefault(old => old.Name == name);
                        if (original == null) scheduler.Delete(name);
                        else scheduler.Register(original);
                    }
                }
                catch (Exception rollback)
                {
                    throw new IOException("计划保存失败，部分 Windows 任务无法恢复。请在多阶段页面重新保存或暂停。", new AggregateException(error, rollback));
                }
                throw;
            }
            var next = StageTimeline.Next(plan, clock.GetUtcNow(), clock.LocalTimeZone);
            log.Write("stage-plan-saved", next == null ? "多阶段计划已保存，当前没有启用的阶段。" : $"已核验 {desired.Length - 1} 个阶段唤醒任务。下次执行 {next.Start:MM-dd HH:mm zzz}。");
        }
        catch (Exception error)
        {
            log.Write("stage-plan-error", error.Message);
            throw;
        }
    }

    public string? Health(StagePlan plan, IReadOnlyList<ScheduledTaskInfo> tasks)
    {
        var expected = Specs(plan, clock.GetUtcNow(), clock.LocalTimeZone);
        if (tasks.Count(task => IsStageTask(task.Spec.Name)) != expected.Length || expected.Any(spec => !scheduler.Verify(spec)))
            return "多阶段计划与 Windows 任务不一致，请重新保存。";
        return null;
    }
}
