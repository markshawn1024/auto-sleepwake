namespace WakeGlass.Core;

public enum StageRunPhase { Holding, Interrupted, Skipped, SleepRequested, Error }

public sealed record StageRunState(Guid Id, DateTimeOffset Start, StageRunPhase Phase, string Message, DateTimeOffset? ConsumedThrough);

public interface IStageRunStore
{
    StageRunState? Load();
    void Save(StageRunState state);
}

public sealed class StageRunStore : IStageRunStore
{
    private static string FilePath => Path.Combine(SettingsStore.DataDirectory, "stage-run.json");
    public StageRunState? Load() => SettingsStore.Read<StageRunState>(FilePath);
    public void Save(StageRunState state) => SettingsStore.Write(FilePath, state);
}

public interface IStagePower : IPowerService
{
    void KeepAwake(bool enabled);
    TimeSpan? SessionIdleTime();
}

// Tick, Interrupt and Stop run on the same thread as the Windows execution-state request.
public sealed class StageRunner(TimeProvider clock, IStageRunStore runStore, IStagePower power, IDiagnostics log)
{
    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RequiredIdle = TimeSpan.FromMinutes(2);
    private StageRunState? state = runStore.Load();
    private StageOccurrence? held;
    private DateTimeOffset previousTime;
    private long previousTick;
    private string? previousZone;

    public bool Tick(StagePlan plan, Action<StageOccurrence> finish)
    {
        var now = clock.GetUtcNow();
        var tick = clock.GetTimestamp();
        var zone = clock.LocalTimeZone;
        bool gap = previousZone != null && (previousZone != zone.Id ||
            clock.GetElapsedTime(previousTick, tick) > EndGrace ||
            ((now - previousTime) - clock.GetElapsedTime(previousTick, tick)).Duration() > TimeSpan.FromSeconds(4));
        previousZone = zone.Id;
        previousTime = now;
        previousTick = tick;
        if (!plan.Active.Any()) { Stop(); return false; }

        var current = StageTimeline.Latest(plan, now, zone);
        if (held != null && (gap || held != current)) Stop();
        if (current == null || current.Start <= state?.ConsumedThrough || current.Start < state?.Start) return true;

        if (current.End is { } end && now >= end)
        {
            bool onTime = held == current && now - end <= EndGrace;
            Stop();
            if (!onTime)
            {
                Record(current, StageRunPhase.Skipped, "短时阶段结束点已错过，跳过本次睡眠。", consume: true);
                return true;
            }

            // Persist a claim before any attempt, so a restart can never replay this sleep.
            Record(current, StageRunPhase.SleepRequested, "到达计划结束时间，正在核验自动睡眠条件。", consume: true);
            try
            {
                finish(current);
                Record(current, StageRunPhase.SleepRequested, "Windows 睡眠调用已返回，后续唤醒任务仍有效。", consume: true);
            }
            catch (OperationCanceledException error) { Record(current, StageRunPhase.Skipped, error.Message, consume: true); }
            catch (Exception error) { Record(current, StageRunPhase.Error, error.Message, consume: true); }
            return true;
        }

        if (held == current) return true;
        try
        {
            power.KeepAwake(true);
            held = current;
            var next = StageTimeline.Next(plan, now, zone);
            var until = current.End is { } deadline ? $"保持至 {TimeZoneInfo.ConvertTime(deadline, zone):MM-dd HH:mm}。" : "持续保持系统运行。";
            Record(current, StageRunPhase.Holding,
                $"计划 {current.Start:MM-dd HH:mm zzz}，实际启动 {TimeZoneInfo.ConvertTime(now, zone):MM-dd HH:mm:ss zzz}。{until}下次唤醒 {next?.Start:MM-dd HH:mm zzz}。", consume: false);
        }
        catch (Exception error)
        {
            Stop();
            Record(current, StageRunPhase.Error, $"无法保持运行，跳过该阶段的自动睡眠。{error.Message}", consume: true);
        }
        return true;
    }

    public void Interrupt()
    {
        if (held is not { } interrupted) return;
        Stop();
        Record(interrupted, StageRunPhase.Interrupted, "检测到系统睡眠或恢复，已结束这次保持运行。", consume: true);
    }

    public void Stop()
    {
        if (held == null) return;
        power.KeepAwake(false);
        held = null;
    }

    private void Record(StageOccurrence occurrence, StageRunPhase phase, string message, bool consume)
    {
        var cutoff = state?.ConsumedThrough;
        if (consume && (cutoff == null || occurrence.Start > cutoff)) cutoff = occurrence.Start;
        state = new(occurrence.Stage.Id, occurrence.Start, phase, message, cutoff);
        runStore.Save(state);
        log.Write("stage-" + phase.ToString().ToLowerInvariant(), $"{occurrence.Stage.TimeLabel} · {message}");
    }
}

public sealed class StageSleepGuard(IWakeScheduler scheduler, ISettingsStore settings, ICapabilityService capabilities,
    IStagePower power, IDiagnostics log, TimeProvider clock)
{
    public void Finish(StageOccurrence occurrence)
    {
        var config = settings.Load();
        var plan = config.Stages ?? StagePlan.Empty;
        var now = clock.GetUtcNow();
        var zone = clock.LocalTimeZone;
        StageTimeline.Validate(plan, config.Schedule);
        if (StageTimeline.Latest(plan, now, zone) != occurrence)
            throw new OperationCanceledException("计划已暂停、修改或进入下一阶段，跳过睡眠。");
        CheckEnd(occurrence, now);
        if (capabilities.Detect().BlockReason is { } blocked) throw new OperationCanceledException(blocked);

        var next = StageTimeline.Next(plan, now, zone) ?? throw new OperationCanceledException("找不到下一次阶段唤醒，跳过睡眠。");
        if (next.Start - now < StageTimeline.WakeMargin)
            throw new OperationCanceledException("距离下次唤醒不足 2 分钟，跳过睡眠。");
        var spec = StageScheduleManager.WakeSpec(next.Stage, now, zone);
        var tasks = scheduler.List();
        var actual = tasks.FirstOrDefault(task => task.Spec.Name == spec.Name);
        if (actual?.NextRun is not { } registered || Math.Abs((registered - spec.Start).TotalSeconds) >= 2 || !scheduler.Verify(spec))
            throw new OperationCanceledException("下一次阶段唤醒任务缺失或时间不符，跳过睡眠。");
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        if (tasks.Any(task => task.Spec.Enabled && task.Spec.Kind == TaskKind.Wake && task.NextRun > localNow && task.NextRun < localNow + StageTimeline.WakeMargin))
            throw new OperationCanceledException("两分钟内还有一次性或每日唤醒，跳过本次睡眠。");

        if (power.SessionIdleTime() is not { } idle)
            throw new OperationCanceledException("无法确认当前交互会话空闲，跳过睡眠。");
        if (idle < StageRunner.RequiredIdle)
            throw new OperationCanceledException("最近 2 分钟检测到用户操作，跳过本次睡眠。");
        CheckEnd(occurrence, clock.GetUtcNow());
        log.Write("stage-auto-sleep", $"已核验下次阶段唤醒 {next.Start:MM-dd HH:mm zzz}，向 Windows 请求睡眠。");
        power.Sleep();
    }

    private static void CheckEnd(StageOccurrence occurrence, DateTimeOffset now)
    {
        if (occurrence.End is not { } end || now < end || now - end > StageRunner.EndGrace)
            throw new OperationCanceledException("已超出阶段结束时段，跳过睡眠。");
    }
}
