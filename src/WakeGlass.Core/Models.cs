using System.Globalization;

namespace WakeGlass.Core;

public record DailySchedule(string Id, TimeSpan Sleep, TimeSpan Wake, int Days, bool Enabled = true)
{
    public static DailySchedule Default => new("daily", new(23, 30, 0), new(7, 50, 0), 127, false);
    public int WakeDays => Wake < Sleep ? TimeMath.ShiftDays(Days) : Days;
}
public record AppSettings(DailySchedule? Schedule = null, string Theme = "System", string Material = "Acrylic", bool MinimizeToTray = true, int CountdownSeconds = 15, StagePlan? Stages = null);
public enum TaskKind { Wake, Sleep, Probe, StageWake, StageWorker }
public record TaskSpec(string Name, TaskKind Kind, DateTime Start, int Days = 0, bool Enabled = true, string Id = "instant");
public record ScheduledTaskInfo(TaskSpec Spec, DateTime? NextRun, DateTime? LastRun, int LastResult, string State);
public record Capabilities(bool S3, bool S0, bool Hibernate, bool RtcWake, int TimerPolicy, bool OnAc, int BatteryPercent, bool SchedulerAccess, string Details, string? Error = null)
{
    public bool CanSleep => S3 || S0;
    public string? BlockReason => Error ?? (!CanSleep ? "系统没有可用的 S3 或 S0 睡眠状态。" : !SchedulerAccess ? "当前用户无权注册计划任务，请查看诊断。" : TimerPolicy != 1 ? (TimerPolicy == 0 ? "当前供电方式禁用了唤醒计时器。请在 Windows 电源选项中手动允许后重试。" : "无法确认普通唤醒计时器已启用（仅重要计时器或状态未知）。请检查 Windows 电源选项。") : S3 && !S0 && !RtcWake ? "固件未报告支持从 S3 通过 RTC 计时器唤醒。" : !OnAc && BatteryPercent is >= 0 and <= 20 ? "电池电量低于或等于 20%，请连接电源后再尝试。" : null);
    public string Summary => $"S3 {(S3 ? "支持" : "不可用")} · S0 {(S0 ? "支持" : "不可用")} · 休眠 {(Hibernate ? "支持" : "不可用")} · {(OnAc ? "已接通电源" : "电池供电")} · 唤醒计时器 {(TimerPolicy == 1 ? "已启用" : TimerPolicy == 0 ? "已禁用" : TimerPolicy == 2 ? "仅重要计时器" : "未知")}";
}
public static class TimeMath
{
    public static int ShiftDays(int days) => ((days << 1) & 127) | ((days >> 6) & 1);
    public static void Validate(DailySchedule s)
    {
        if (s.Id != "daily") throw new ArgumentException("计划标识无效。");
        if (s.Sleep < TimeSpan.Zero || s.Sleep >= TimeSpan.FromDays(1) || s.Wake < TimeSpan.Zero || s.Wake >= TimeSpan.FromDays(1)) throw new ArgumentException("时间必须在 00:00–23:59 之间。");
        if (s.Days is < 1 or > 127) throw new ArgumentException("请至少选择一个重复星期。");
        var duration = (s.Wake - s.Sleep + TimeSpan.FromDays(1)).TotalMinutes % 1440;
        if (duration < 5) throw new ArgumentException("睡眠与唤醒至少间隔 5 分钟，不能设置为相同时间。");
    }
    public static DateTime Next(TimeSpan time, int days, DateTime now)
    {
        for (int i = 0; i <= 7; i++)
        {
            var candidate = now.Date.AddDays(i).Add(time);
            if (candidate > now && (days & (1 << (int)candidate.DayOfWeek)) != 0 && !TimeZoneInfo.Local.IsInvalidTime(candidate)) return candidate;
        }
        throw new ArgumentException("没有有效的下一次执行时间。");
    }
    public static DateTime WakeAfter(DailySchedule s, DateTime sleep) => sleep.Date.Add(s.Wake).AddDays(s.Wake < s.Sleep ? 1 : 0);
    public static DateTime ParseTime(string value) => DateTime.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : throw new ArgumentException("请输入有效的 24 小时制时间 HH:mm，例如 23:30。");
    public static void ValidateWake(DateTime wake, DateTime now)
    {
        if (wake < now.AddMinutes(2)) throw new ArgumentException("请选择至少 2 分钟后的唤醒时间。");
        if (wake > now.AddDays(30)) throw new ArgumentException("一次性唤醒最长支持未来 30 天。");
        if (TimeZoneInfo.Local.IsInvalidTime(wake) || TimeZoneInfo.Local.IsAmbiguousTime(wake)) throw new ArgumentException("该时间处于夏令时切换区间，请选择其他时间。");
    }
}
public interface IWakeScheduler
{
    void Register(TaskSpec spec);
    bool Verify(TaskSpec spec);
    IReadOnlyList<ScheduledTaskInfo> List();
    void Delete(string name);
}
public interface IPowerService { void Sleep(); }
public interface ICapabilityService { Capabilities Detect(); }
public interface ISettingsStore { AppSettings Load(); void Save(AppSettings settings); }
public interface IDiagnostics { void Write(string code, string message); }
