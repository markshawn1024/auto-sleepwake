namespace WakeGlass.Core;

public enum StageMode { Timed, Persistent }

public sealed record WakeStage(Guid Id, bool Enabled, TimeSpan Wake, StageMode Mode, int Minutes, int Days)
{
    public string TimeLabel => Wake.ToString(@"hh\:mm");
}

public sealed record StagePlan(bool Enabled, WakeStage[] Items)
{
    public static StagePlan Empty => new(false, []);
    public IEnumerable<WakeStage> Active => Enabled ? Items.Where(stage => stage.Enabled) : [];
}

public sealed record StageOccurrence(WakeStage Stage, DateTimeOffset Start)
{
    public DateTimeOffset? End => Stage.Mode == StageMode.Timed ? Start.AddMinutes(Stage.Minutes) : null;
}

public static class StageTimeline
{
    public const int MaxStages = 48;
    public const int MaxMinutes = 720;
    public static readonly TimeSpan WakeMargin = TimeSpan.FromMinutes(2);
    private const int Week = 7 * 1440;

    public static void Validate(StagePlan plan, DailySchedule? daily)
    {
        if (plan.Items is null || plan.Items.Length > MaxStages)
            throw new ArgumentException($"最多可保存 {MaxStages} 个唤醒阶段。");
        var ids = new HashSet<Guid>();
        foreach (var stage in plan.Items)
        {
            if (stage.Id == Guid.Empty || !ids.Add(stage.Id)) throw new ArgumentException("阶段标识重复或无效。");
            if (stage.Days is < 1 or > 127) throw new ArgumentException($"{stage.TimeLabel} 阶段至少需要一个重复日。");
            if (stage.Wake < TimeSpan.Zero || stage.Wake >= TimeSpan.FromDays(1) || stage.Wake.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new ArgumentException("阶段时间必须精确到分钟，范围为 00:00–23:59。");
            if (!Enum.IsDefined(stage.Mode) || stage.Mode == StageMode.Timed && stage.Minutes is < 1 or > MaxMinutes)
                throw new ArgumentException($"{stage.TimeLabel} 短时阶段须保持运行 1–{MaxMinutes} 分钟。");
        }

        // Validate enabled rows even while the whole plan is paused, so it can be enabled later.
        var events = plan.Items.Where(stage => stage.Enabled)
            .SelectMany(stage => WeekTimes(stage.Wake, stage.Days).Select(minute => (Stage: stage, Minute: minute)))
            .OrderBy(item => item.Minute).ToArray();
        for (int index = 0; index < events.Length; index++)
        {
            var current = events[index];
            var next = events[(index + 1) % events.Length];
            int gap = (next.Minute - current.Minute + Week) % Week;
            if (events.Length == 1) gap = Week;
            if (gap == 0) throw new ArgumentException($"{DayLabel(current.Minute)} {current.Stage.TimeLabel} 存在重复唤醒时间。");
            if (current.Stage.Mode == StageMode.Timed && gap < current.Stage.Minutes + WakeMargin.TotalMinutes)
                throw new ArgumentException($"{DayLabel(current.Minute)} {current.Stage.TimeLabel} 的运行时段与 {DayLabel(next.Minute)} {next.Stage.TimeLabel} 重叠，或结束后不足 2 分钟即再次唤醒。");

            if (!plan.Enabled || daily is not { Enabled: true }) continue;
            int length = current.Stage.Mode == StageMode.Timed ? current.Stage.Minutes : 0;
            if (WeekTimes(daily.Sleep, daily.Days).Any(minute => Distance(current.Minute, minute) <= length))
                throw new ArgumentException($"每日睡眠 {daily.Sleep:hh\\:mm} 与 {DayLabel(current.Minute)} {current.Stage.TimeLabel} 阶段冲突。请调整时间或暂停其中一个计划。");
            int wakeWindow = length == 0 ? 1 : length + (int)WakeMargin.TotalMinutes;
            if (WeekTimes(daily.Wake, daily.WakeDays).Any(minute => Distance(current.Minute, minute) < wakeWindow))
                throw new ArgumentException($"每日唤醒 {daily.Wake:hh\\:mm} 与 {DayLabel(current.Minute)} {current.Stage.TimeLabel} 阶段冲突。请调整时间或暂停其中一个计划。");
        }
    }

    private static int Distance(int start, int end) => (end - start + Week) % Week;
    private static string DayLabel(int minute) => "周" + "日一二三四五六"[minute / 1440];

    private static IEnumerable<int> WeekTimes(TimeSpan time, int days) => Enumerable.Range(0, 7)
        .Where(day => (days & (1 << day)) != 0).Select(day => day * 1440 + (int)time.TotalMinutes);

    public static StageOccurrence? Latest(StagePlan plan, DateTimeOffset now, TimeZoneInfo zone) => plan.Active
        .Select(stage => Find(stage, now, zone, future: false)).Where(item => item != null).MaxBy(item => item!.Start);

    public static StageOccurrence? Next(StagePlan plan, DateTimeOffset now, TimeZoneInfo zone) => plan.Active
        .Select(stage => Find(stage, now, zone, future: true)).Where(item => item != null).MinBy(item => item!.Start);

    public static StageOccurrence? Find(WakeStage stage, DateTimeOffset now, TimeZoneInfo zone, bool future)
    {
        var localDate = TimeZoneInfo.ConvertTime(now, zone).Date;
        // A weekly occurrence can be absent during a daylight-saving transition.
        for (int offset = 0; offset <= 14; offset++)
        {
            var local = localDate.AddDays(future ? offset : -offset).Add(stage.Wake);
            if ((stage.Days & (1 << (int)local.DayOfWeek)) == 0 || zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) continue;
            var start = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (future ? start > now : start <= now) return new(stage, start);
        }
        return null;
    }
}
