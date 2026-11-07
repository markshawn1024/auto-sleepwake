namespace WakeGlass.Core;

public static class ScheduledSleepGuard
{
    public static DateTime Validate(IWakeScheduler scheduler, ISettingsStore store, ICapabilityService capabilities, DateTime now)
    {
        var settings = store.Load();
        var s = settings.Schedule;
        if (s is not { Enabled: true }) throw new InvalidOperationException("每日计划已暂停或删除，跳过睡眠。");
        TimeMath.Validate(s);
        if (settings.Stages is { Enabled: true } stages) StageTimeline.Validate(stages, s);
        var occurrence = now.Date.Add(s.Sleep);
        if (occurrence > now) occurrence = occurrence.AddDays(-1);
        if ((s.Days & (1 << (int)occurrence.DayOfWeek)) == 0 || (now - occurrence).TotalSeconds > 120) throw new InvalidOperationException("不是计划的睡眠时间，拒绝补执行过期或手动触发的睡眠任务。");
        if (capabilities.Detect().BlockReason is { } blocked) throw new InvalidOperationException(blocked);
        var wake = TimeMath.WakeAfter(s, occurrence);
        if (TimeZoneInfo.Local.IsInvalidTime(wake) || TimeZoneInfo.Local.IsAmbiguousTime(wake)) throw new InvalidOperationException("唤醒时间处于夏令时切换区间，跳过本次睡眠。");
        foreach (var spec in ScheduleManager.Specs(s, now)) if (!scheduler.Verify(spec)) throw new IOException("每日计划的系统任务验证失败，已阻止睡眠。");
        var task = scheduler.List().Single(x => x.Spec.Name == ScheduleManager.WakeName);
        if (task.NextRun is not { } next || Math.Abs((next - wake).TotalSeconds) > 60) throw new IOException("系统报告的下一次唤醒时间与计划不一致，已阻止睡眠。");
        return wake;
    }
}
