using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WakeGlass.Core;

namespace WakeGlass.App;

public partial class MainWindow
{
    private readonly ObservableCollection<StageDraft> stageDrafts = [];
    private DateTime? stageSleepLocal;

    private void LoadStageDrafts(StagePlan? plan)
    {
        stageDrafts.Clear();
        foreach (var stage in (plan ?? StagePlan.Empty).Items.OrderBy(stage => stage.Wake))
            stageDrafts.Add(new StageDraft(stage));
        NoStages.Visibility = stageDrafts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddStageClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (stageDrafts.Count >= StageTimeline.MaxStages)
        {
            StatusText.Text = $"最多可添加 {StageTimeline.MaxStages} 个阶段。";
            return;
        }
        var minutes = (445 + stageDrafts.Count * 60) % 1440;
        var mode = stageDrafts.Count == 0 ? StageMode.Timed : StageMode.Persistent;
        stageDrafts.Add(new StageDraft(new(Guid.NewGuid(), true, TimeSpan.FromMinutes(minutes), mode, 7, 127)));
        NoStages.Visibility = Visibility.Collapsed;
    }

    private void RemoveStageClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { DataContext: StageDraft stage }) return;
        stageDrafts.Remove(stage);
        NoStages.Visibility = stageDrafts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = "已从草稿移除阶段。保存后清理对应的 Windows 任务。";
    }

    private async void SaveStagesClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var stages = stageDrafts.Select(draft => draft.ToStage()).ToArray();
        var saved = await Task.Run(() =>
        {
            var current = services.Settings.Load().Stages ?? StagePlan.Empty;
            var plan = new StagePlan(current.Enabled && stages.Any(stage => stage.Enabled), stages);
            if (services.Simulate && plan.Enabled) throw new InvalidOperationException("模拟模式只能保存暂停的阶段计划。");
            SaveStages(plan);
            return services.Settings.Load().Stages;
        });
        LoadStageDrafts(saved);
    }, "阶段计划已保存，Windows 任务已同步。");

    private async void ToggleStagesClick(object sender, RoutedEventArgs e) => await ExecuteAsync(() => Task.Run(() =>
    {
        var plan = services.Settings.Load().Stages ?? StagePlan.Empty;
        if (!plan.Enabled && !plan.Items.Any(stage => stage.Enabled))
            throw new InvalidOperationException("请先保存至少一个启用的阶段。");
        if (services.Simulate && !plan.Enabled) throw new InvalidOperationException("模拟模式禁止启用真实阶段计划。");
        SaveStages(plan with { Enabled = !plan.Enabled });
    }), "多阶段计划状态已更新。");

    private void SaveStages(StagePlan plan)
    {
        services.StageSchedules.Save(plan);
        if (!plan.Active.Any()) return;
        try { services.Scheduler.StartStageWorker(); }
        catch (Exception error)
        {
            services.Log.Write("stage-worker-start-error", $"立即启动失败，等待 Windows 恢复任务重试。{error.Message}");
        }
    }

    private void RefreshStages(StagePlan plan, StageRunState? run, string? health, IReadOnlyList<ScheduledTaskInfo> registered)
    {
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        var current = StageTimeline.Latest(plan, now, zone);
        var next = StageTimeline.Next(plan, now, zone);
        bool currentRun = current != null && run != null && run.Id == current.Stage.Id && run.Start == current.Start;
        bool workerRunning = registered.Any(task => task.Spec.Kind == TaskKind.StageWorker && task.State == "运行中");
        bool holding = workerRunning && currentRun && run?.Phase == StageRunPhase.Holding && (current?.End == null || current.End > now);
        var timedEnds = plan.Active.Where(stage => stage.Mode == StageMode.Timed)
            .Select(stage => StageTimeline.Find(stage, now, zone, future: true)?.End);
        var nextEnd = holding && current?.End != null ? current.End : timedEnds.Min();
        stageSleepLocal = nextEnd?.LocalDateTime;
        StageStatus.Text = health != null || currentRun && run?.Phase == StageRunPhase.Error ? "异常"
            : !plan.Active.Any() ? "已暂停"
            : holding && current?.End != null ? $"睡眠待执行 · {current.Stage.TimeLabel}"
            : holding ? $"运行中 · {current!.Stage.TimeLabel} 持续唤醒"
            : "等待下一阶段";
        StageRegistration.Text = health ?? (!plan.Active.Any() ? "启用后由 Windows 注册阶段任务。"
            : workerRunning ? "Windows 任务已核验 · 后台已启动" : "Windows 任务已核验 · 等待后台启动");
        StageNextWake.Text = Stamp(next?.Start);
        StageNextSleep.Text = Stamp(nextEnd);
        StageToggle.Content = plan.Enabled ? "暂停多阶段计划" : "启用已保存计划";

        foreach (var draft in stageDrafts)
        {
            var saved = plan.Items.FirstOrDefault(item => item.Id == draft.Id);
            if (saved == null) { draft.Status = "尚未保存"; continue; }
            var task = registered.FirstOrDefault(item => item.Spec.Name == StageScheduleManager.WakeName(saved.Id));
            var state = holding && current?.Stage.Id == saved.Id ? "正在保持运行"
                : run?.Id == saved.Id && run.Phase != StageRunPhase.Holding ? run.Message
                : "等待执行";
            draft.Status = !plan.Enabled || !saved.Enabled ? "已保存 · 暂停"
                : task == null ? "缺少 Windows 唤醒任务"
                : $"下次 {task.NextRun:MM-dd HH:mm} · {state}";
        }
    }

    private static string Stamp(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM/dd  HH:mm") ?? "—";

    public sealed class StageDraft : INotifyPropertyChanged
    {
        public StageDraft(WakeStage stage)
        {
            Id = stage.Id;
            Enabled = stage.Enabled;
            WakeText = stage.TimeLabel;
            modeIndex = (int)stage.Mode;
            MinutesText = (stage.Minutes > 0 ? stage.Minutes : 7).ToString();
            Days = [.. Enumerable.Range(1, 7).Select(index => new DayDraft(index % 7, "周" + "日一二三四五六"[index % 7], (stage.Days & (1 << (index % 7))) != 0))];
        }

        public Guid Id { get; }
        public string Heading => $"阶段 · {Id:N}"[..13];
        public bool Enabled { get; set; }
        public string WakeText { get; set; }
        public string MinutesText { get; set; }
        public DayDraft[] Days { get; }
        private int modeIndex;
        public int ModeIndex
        {
            get => modeIndex;
            set
            {
                modeIndex = value;
                PropertyChanged?.Invoke(this, new(nameof(ModeIndex)));
                PropertyChanged?.Invoke(this, new(nameof(Outcome)));
            }
        }
        public string Outcome => ModeIndex == 1 ? "保持系统运行，直至主动睡眠或切换阶段。" : "结束操作 · 核验条件后自动睡眠";
        private string status = "尚未保存";
        public string Status
        {
            get => status;
            set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;

        public WakeStage ToStage()
        {
            var wake = TimeMath.ParseTime(WakeText).TimeOfDay;
            int minutes = 0;
            if (ModeIndex == 0 && !int.TryParse(MinutesText, out minutes))
                throw new ArgumentException($"{WakeText} 阶段的运行时长须填写整数分钟。");
            var days = Days.Where(day => day.Selected).Sum(day => 1 << day.Index);
            return new(Id, Enabled, wake, (StageMode)ModeIndex, minutes, days);
        }
    }

    public sealed class DayDraft(int index, string label, bool selected)
    {
        public int Index { get; } = index;
        public string Label { get; } = label;
        public bool Selected { get; set; } = selected;
    }
}
