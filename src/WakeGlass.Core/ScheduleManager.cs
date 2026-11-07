namespace WakeGlass.Core;

// The named lock coordinates schedule updates and sleep checks across app processes.
public sealed class ScheduleManager(IWakeScheduler scheduler, ISettingsStore store, IDiagnostics log)
{
    public const string SleepName = "Daily-Sleep", WakeName = "Daily-Wake", InstantName = "Instant-Wake";
    public static IDisposable Lock()
    {
        var m = new Mutex(false, "Local\\WakeGlass-Schedule-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        try { try { if (!m.WaitOne(15000)) throw new TimeoutException("另一个 WakeGlass 操作尚未完成。"); } catch (AbandonedMutexException) { } return new Lease(m); }
        catch { m.Dispose(); throw; }
    }
    private sealed class Lease(Mutex m) : IDisposable { public void Dispose() { m.ReleaseMutex(); m.Dispose(); } }
    public static TaskSpec[] Specs(DailySchedule s, DateTime now) => [
        new(WakeName, TaskKind.Wake, TimeMath.Next(s.Wake, s.WakeDays, now), s.WakeDays, s.Enabled, s.Id),
        new(SleepName, TaskKind.Sleep, TimeMath.Next(s.Sleep, s.Days, now), s.Days, s.Enabled, s.Id)];
    public void Save(DailySchedule schedule)
    {
        TimeMath.Validate(schedule);
        using var gate = Lock();
        var settings = store.Load();
        if (settings.Stages is { Enabled: true } stages) StageTimeline.Validate(stages, schedule);
        var originals = scheduler.List().Where(t => t.Spec.Name is SleepName or WakeName).Select(t => t.Spec).ToArray();
        var specs = Specs(schedule, DateTime.Now);
        try
        {
            // Disable the old sleep trigger before updating its matching wake trigger.
            var oldSleep = originals.FirstOrDefault(t => t.Kind == TaskKind.Sleep);
            if (oldSleep != null) scheduler.Register(oldSleep with { Enabled = false });
            foreach (var spec in specs) { scheduler.Register(spec); if (!scheduler.Verify(spec)) throw new IOException($"任务 {spec.Name} 回读验证失败。"); }
            store.Save(settings with { Schedule = schedule });
        }
        catch (Exception error)
        {
            try { foreach (var s in specs.Reverse()) scheduler.Delete(s.Name); foreach (var old in originals.OrderBy(x => x.Kind == TaskKind.Sleep)) scheduler.Register(old); }
            catch (Exception rollback) { log.Write("rollback-failed", rollback.Message); throw new IOException("计划保存失败且恢复旧计划失败，请立即在任务页检查并暂停计划。", new AggregateException(error, rollback)); }
            throw;
        }
        log.Write("schedule-saved", schedule.Enabled ? "每日计划已保存并启用。" : "每日计划已暂停。");
    }
    public void SetEnabled(bool enabled)
    {
        var schedule = store.Load().Schedule ?? throw new InvalidOperationException("尚未保存每日计划。");
        Save(schedule with { Enabled = enabled });
    }
    public void DeleteDaily()
    {
        using var gate = Lock();
        // Pause first; partial deletion can never leave an active unpaired sleep trigger.
        var tasks = scheduler.List().Where(t => t.Spec.Name is SleepName or WakeName).ToArray();
        foreach (var t in tasks.Where(t => t.Spec.Kind == TaskKind.Sleep)) scheduler.Register(t.Spec with { Enabled = false });
        scheduler.Delete(SleepName); scheduler.Delete(WakeName);
        store.Save(store.Load() with { Schedule = null });
        log.Write("schedule-deleted", "每日计划及对应系统任务已删除。");
    }
    public string? Health()
    {
        var s = store.Load().Schedule;
        var tasks = scheduler.List().Where(t => t.Spec.Name is SleepName or WakeName).ToArray();
        if (s == null) return tasks.Length == 0 ? null : "发现无配置的系统计划，请删除每日计划后重新保存。";
        if (tasks.Length != 2) return "每日计划缺少系统任务；自动睡眠会被安全检查阻止。请重新保存计划。";
        foreach (var expected in Specs(s, DateTime.Now)) if (!scheduler.Verify(expected)) return "系统任务与配置不一致，请重新保存。";
        return null;
    }
}
