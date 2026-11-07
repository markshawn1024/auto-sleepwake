using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WakeGlass.Core;
using Forms = System.Windows.Forms;

namespace WakeGlass.App;

public partial class MainWindow : Window
{
    private readonly Services services;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon tray;
    private readonly EventWaitHandle openSignal;
    private readonly RegisteredWaitHandle openWait;
    private IReadOnlyList<ScheduledTaskInfo> tasks = [];
    private Capabilities? capabilities;
    private bool busy, exiting, refreshing;
    private int ticks;
    private CountdownWindow? activeCountdown;
    private readonly CancellationTokenSource lifetime = new();
    public MainWindow(Services services)
    {
        this.services = services;
        InitializeComponent();
        openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Services.OpenSignalName);
        openWait = ThreadPool.RegisterWaitForSingleObject(openSignal, (_, _) => Dispatcher.BeginInvoke(Open), null, Timeout.Infinite, false);
        if (services.Simulate) Title = "WakeGlass · 模拟测试";
        var settings = services.Settings.Load();
        LoadSettings(settings); LoadSchedule(settings.Schedule ?? DailySchedule.Default);
        StageCards.ItemsSource = stageDrafts;
        LoadStageDrafts(settings.Stages);
        SetWake(DateTime.Now.AddHours(1));
        tray = new Forms.NotifyIcon { Text = "WakeGlass · 电源调度", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 WakeGlass", null, (_, _) => Dispatcher.Invoke(Open));
        menu.Items.Add("立即睡眠（使用界面唤醒时间）", null, (_, _) => Dispatcher.Invoke(() => { Open(); Tabs.SelectedIndex = 0; SleepClick(this, new RoutedEventArgs()); }));
        menu.Items.Add("查看下一次唤醒", null, (_, _) => Dispatcher.Invoke(() => { Open(); Tabs.SelectedIndex = 0; }));
        menu.Items.Add("暂停／恢复每日计划", null, (_, _) => Dispatcher.Invoke(() => ToggleDailyClick(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出（保留计划）", null, (_, _) => Dispatcher.Invoke(Exit));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Open);
        SourceInitialized += (_, _) => { Theme.Backdrop(this, settings.Material); HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(PowerHook); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && services.Settings.Load().MinimizeToTray && !busy) Hide(); };
        Closing += OnClosing;
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); openWait.Unregister(null); openSignal.Dispose(); tray.Dispose(); SystemEvents.UserPreferenceChanged -= SystemThemeChanged; };
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Loaded += async (_, _) => { services.Log.Write("app-open", "WakeGlass 已启动。已配置的系统任务不依赖界面运行。"); await RefreshAsync(); timer.Start(); };
        timer.Tick += async (_, _) => { UpdateClock(); ticks++; if (ticks % 30 == 0 && !busy) await RefreshAsync(); };
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => Theme.Apply(services.Settings.Load()));
    private IntPtr PowerHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x218)
        {
            if (wParam.ToInt32() == 4) services.Log.Write("system-suspend", "收到 Windows 即将睡眠的系统事件。");
            if (wParam.ToInt32() is 7 or 18) { services.Log.Write("system-resume", "收到 Windows 恢复运行的系统事件；唤醒原因请查看 lastwake。"); Dispatcher.BeginInvoke(async () => { await RefreshAsync(); }); }
        }
        return IntPtr.Zero;
    }
    private void Open() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void SetWake(DateTime date) { WakeDate.SelectedDate = date.Date; WakeTime.Text = date.ToString("HH:mm"); UpdateClock(); }
    private DateTime SelectedWake()
    {
        if (WakeDate.SelectedDate is not { } date) throw new ArgumentException("请选择有效的唤醒日期。");
        if (!DateTime.TryParse(WakeDate.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var entered) || entered.Date != date.Date) throw new ArgumentException("唤醒日期无效，请使用日历重新选择。");
        if (!DateTime.TryParseExact(WakeTime.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new ArgumentException("唤醒时间格式应为 HH:mm，例如 07:50。");
        return date.Date + time.TimeOfDay;
    }
    private void UpdateClock()
    {
        if (Clock == null || WakeTime == null || WakeRemaining == null) return;
        Clock.Text = DateTime.Now.ToString("MM月dd日  HH:mm:ss");
        try { var remaining = SelectedWake() - DateTime.Now; WakeRemaining.Text = remaining.TotalSeconds > 0 ? $"将在 {Duration(remaining)} 后唤醒" : "请选择未来的唤醒时间"; } catch (ArgumentException) { WakeRemaining.Text = "日期和时间格式：HH:mm"; }
        var future = tasks.Where(x => x.Spec.Enabled && x.Spec.Kind is not (TaskKind.StageWorker or TaskKind.Probe))
            .Select(x => x.NextRun).Append(stageSleepLocal).Where(time => time > DateTime.Now).Min();
        EventCountdown.Text = future is { } at ? Duration(at - DateTime.Now) : "—";
    }
    private static string Duration(TimeSpan time) => time.TotalDays >= 1 ? $"{(int)time.TotalDays} 天 {time.Hours} 小时" : time.TotalHours >= 1 ? $"{(int)time.TotalHours} 小时 {time.Minutes} 分" : $"{Math.Max(0, time.Minutes)} 分 {Math.Max(0, time.Seconds)} 秒";
    private void WakeChanged(object sender, RoutedEventArgs e) => UpdateClock();
    private void QuickClick(object sender, RoutedEventArgs e) => SetWake(DateTime.Now.AddMinutes(int.Parse((string)((Button)sender).Tag)));
    private void TimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        if (sender is TextBox box && DateTime.TryParseExact(box.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) { box.Text = time.AddMinutes(e.Key == Key.Up ? 1 : -1).ToString("HH:mm"); e.Handled = true; }
    }
    private void RepeatChanged(object sender, SelectionChangedEventArgs e) { if (WeekDays != null) WeekDays.Visibility = Repeat.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
    private void LoadSchedule(DailySchedule s)
    {
        DailySleep.Text = s.Sleep.ToString(@"hh\:mm"); DailyWake.Text = s.Wake.ToString(@"hh\:mm"); DailyEnabled.IsChecked = s.Enabled;
        Repeat.SelectedIndex = s.Days == 127 ? 0 : s.Days == 62 ? 1 : 2;
        foreach (CheckBox check in WeekDays.Children) check.IsChecked = (s.Days & (1 << int.Parse((string)check.Tag))) != 0;
    }
    private void LoadSettings(AppSettings settings)
    {
        ThemeChoice.SelectedIndex = settings.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };
        MaterialChoice.SelectedIndex = settings.Material switch { "Mica" => 1, "Solid" => 2, _ => 0 };
        TrayChoice.IsChecked = settings.MinimizeToTray; CountdownChoice.Text = settings.CountdownSeconds.ToString(CultureInfo.InvariantCulture);
    }
    private async Task RefreshAsync()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            var data = await Task.Run(() =>
            {
                TimeZoneInfo.ClearCachedData();
                var registered = services.Scheduler.List();
                var plan = services.Settings.Load().Stages ?? StagePlan.Empty;
                return (Tasks: registered, Caps: services.Capabilities.Detect(), Health: services.Schedules.Health(),
                    Plan: plan, Run: services.StageRuns.Load(), StageHealth: services.StageSchedules.Health(plan, registered));
            });
            tasks = data.Tasks; capabilities = data.Caps;
            RefreshStages(data.Plan, data.Run, data.StageHealth, tasks);
            CapabilityText.Text = $"Windows {Environment.OSVersion.Version}\n{capabilities.Summary}\n任务注册权限：{(capabilities.SchedulerAccess ? "已实测可用" : "受限")}";
            CapabilityWarning.Text = capabilities.BlockReason ?? (capabilities.S0 ? "S0 静态能力检查通过；Modern Standby 唤醒仍需本机实际测试。" : "静态能力检查通过；尚未进行硬件睡眠／唤醒测试。");
            PowerLabel.Text = capabilities.OnAc ? "● 正在运行 · 已接通电源" : "● 正在运行 · 电池供电";
            var wake = tasks.Where(x => x.Spec.Enabled && x.Spec.Kind is TaskKind.Wake or TaskKind.StageWake && x.NextRun > DateTime.Now).OrderBy(x => x.NextRun).FirstOrDefault();
            NextWake.Text = wake?.NextRun?.ToString("HH:mm") ?? "尚未安排"; NextWakeDay.Text = wake?.NextRun?.ToString("MM月dd日 dddd") ?? "设置一次唤醒，放心休息";
            var sleep = tasks.FirstOrDefault(x => x.Spec.Kind == TaskKind.Sleep && x.Spec.Enabled);
            var nextSleep = new[] { sleep?.NextRun, stageSleepLocal }.Min();
            NextSleep.Text = nextSleep?.ToString("MM/dd  HH:mm") ?? "—";
            ScheduleSummary.Text = data.Health ?? data.StageHealth ?? (nextSleep != null && nextSleep == stageSleepLocal ? "多阶段计划 · 条件满足时" : sleep != null ? "每日计划已启用" : "每日计划未启用");
            var selectedName = (TasksList.SelectedItem as TaskRow)?.Task.Spec.Name;
            var rows = tasks.Select(x => new TaskRow(x)).ToArray(); TasksList.ItemsSource = rows;
            TasksList.SelectedItem = rows.FirstOrDefault(r => r.Task.Spec.Name == selectedName); EmptyTasks.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var entries = services.Log.Read(); RecentActivity.Text = entries.FirstOrDefault() is { } latest ? $"{latest.Time:HH:mm}  {latest.Message}" : "还没有执行记录。";
            HistoryText.Text = string.Join("\n\n", entries.Select(x => $"{x.Time:MM-dd HH:mm:ss}  [{x.Code}]\n{x.Message}"));
            tray.Text = wake?.NextRun is { } next ? $"WakeGlass · 下次唤醒 {next:MM/dd HH:mm}" : "WakeGlass · 尚未安排唤醒";
            if (!busy) StatusText.Text = services.Simulate ? "模拟测试模式 · 禁止真实睡眠 · 请勿将模拟结果视为硬件测试" : data.Health ?? data.StageHealth ?? capabilities.BlockReason ?? "就绪 · 系统任务已同步 · 硬件唤醒结果需实测确认";
            UpdateClock();
        }
        catch (Exception ex) { Error(ex); }
        finally { refreshing = false; }
    }
    private async Task ExecuteAsync(Func<Task> action, string success)
    {
        if (busy) return; busy = true; SleepButton.IsEnabled = SaveButton.IsEnabled = false;
        try { await action(); StatusText.Text = success; }
        catch (OperationCanceledException) { StatusText.Text = "当前操作已取消；本次新增的一次性唤醒任务已清理。"; }
        catch (Exception ex) { Error(ex); }
        finally { await RefreshAsync(); busy = false; SleepButton.IsEnabled = SaveButton.IsEnabled = true; }
    }
    private void Error(Exception error) { var message = Diagnostics.Redact(error.Message); StatusText.Text = message; services.Log.Write("operation-error", message); MessageBox.Show(this, message, "WakeGlass · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private async void SleepClick(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(async () =>
        {
            using var lease = services.TrySleepLock(); if (lease == null) throw new InvalidOperationException("另一项睡眠操作正在进行。");
            var wake = SelectedWake();
            var config = services.Settings.Load();
            var daily = config.Schedule;
            if (daily is { Enabled: true })
            {
                var planned = TimeMath.Next(daily.Sleep, daily.Days, DateTime.Now);
                var otherWake = TimeMath.Next(daily.Wake, daily.WakeDays, DateTime.Now);
                if ((planned < wake || otherWake < wake) && MessageBox.Show(this, "本次休息期间还有每日睡眠或唤醒事件，电脑可能提前醒来或再次睡眠。是否仍按所选时间继续？", "计划时间冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) throw new OperationCanceledException();
            }
            if (config.Stages is { Enabled: true } stages)
            {
                var now = DateTimeOffset.Now;
                var nextStage = StageTimeline.Next(stages, now, TimeZoneInfo.Local);
                if (nextStage != null && nextStage.Start.LocalDateTime <= wake && MessageBox.Show(this,
                    $"{nextStage.Start.LocalDateTime:MM-dd HH:mm} 有多阶段唤醒，电脑可能早于本次选择的时间醒来。继续安排这次睡眠吗？",
                    "阶段唤醒时间冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    throw new OperationCanceledException();
            }
            StatusText.Text = "正在检查能力并注册唤醒任务…";
            await services.Sleep.RunInstantAsync(wake, _ =>
            {
                activeCountdown = new CountdownWindow(wake, services.Settings.Load().CountdownSeconds) { Owner = this };
                bool proceed = activeCountdown.ShowDialog() == true; activeCountdown = null; return Task.FromResult(proceed);
            }, lifetime.Token);
            if (services.Simulate) { await Task.Run(() => { using var gate = ScheduleManager.Lock(); services.Scheduler.Delete(ScheduleManager.InstantName); }); StatusText.Text = "模拟睡眠已完成；没有调用系统电源 API。"; }
        }, "睡眠请求已提交给 Windows；请通过系统恢复事件和诊断核对结果。");
    }
    private async void SaveClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var sleep = TimeMath.ParseTime(DailySleep.Text).TimeOfDay; var wake = TimeMath.ParseTime(DailyWake.Text).TimeOfDay;
        int days = Repeat.SelectedIndex switch { 0 => 127, 1 => 62, _ => WeekDays.Children.Cast<CheckBox>().Where(c => c.IsChecked == true).Aggregate(0, (mask, c) => mask | (1 << int.Parse((string)c.Tag))) };
        var s = new DailySchedule("daily", sleep, wake, days, DailyEnabled.IsChecked == true); TimeMath.Validate(s);
        if (services.Simulate && s.Enabled) throw new InvalidOperationException("模拟模式只能保存暂停的每日计划，不能创建真实自动睡眠计划。");
        if ((wake - sleep + TimeSpan.FromDays(1)).TotalHours % 24 > 18 && MessageBox.Show(this, "此计划的睡眠时长超过 18 小时，请确认时间是否正确。继续保存？", "检查计划", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) throw new OperationCanceledException();
        await Task.Run(() => services.Schedules.Save(s));
    }, "计划已保存，系统任务已注册并回读验证。关闭界面后继续有效。");
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void ToggleDailyClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        bool enabled = !(services.Settings.Load().Schedule?.Enabled ?? throw new InvalidOperationException("请先保存每日计划。"));
        if (services.Simulate && enabled) throw new InvalidOperationException("模拟模式禁止启用真实每日计划。");
        await Task.Run(() => services.Schedules.SetEnabled(enabled)); LoadSchedule(services.Settings.Load().Schedule!);
    }, "每日计划状态已同步到 Windows。");
    private async void ToggleTaskClick(object sender, RoutedEventArgs e)
    {
        if (TasksList.SelectedItem is not TaskRow row) { StatusText.Text = "请先选择一项系统任务。"; return; }
        if (GoToStageTask(row)) return;
        if (row.Task.Spec.Name is ScheduleManager.SleepName or ScheduleManager.WakeName) { ToggleDailyClick(sender, e); return; }
        await ExecuteAsync(() => Task.Run(() => { using var gate = ScheduleManager.Lock(); var s = row.Task.Spec with { Enabled = !row.Task.Spec.Enabled }; if (s.Enabled) TimeMath.ValidateWake(s.Start, DateTime.Now); services.Scheduler.Register(s); if (!services.Scheduler.Verify(s)) throw new IOException("任务状态回读验证失败。"); }), "任务状态已更新。");
    }
    private async void DeleteTaskClick(object sender, RoutedEventArgs e)
    {
        if (TasksList.SelectedItem is not TaskRow row) { StatusText.Text = "请先选择一项系统任务。"; return; }
        if (GoToStageTask(row)) return;
        bool daily = row.Task.Spec.Name is ScheduleManager.SleepName or ScheduleManager.WakeName;
        if (MessageBox.Show(this, daily ? "删除整个每日计划及对应的睡眠、唤醒系统任务？" : "删除所选一次性任务？", "删除计划", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ExecuteAsync(() => Task.Run(() => { if (daily) services.Schedules.DeleteDaily(); else { using var gate = ScheduleManager.Lock(); services.Scheduler.Delete(row.Task.Spec.Name); } }), "计划已删除。");
        LoadSchedule(services.Settings.Load().Schedule ?? DailySchedule.Default);
    }
    private bool GoToStageTask(TaskRow row)
    {
        if (!StageScheduleManager.IsStageTask(row.Task.Spec.Name)) return false;
        Tabs.SelectedItem = StagesTab;
        StatusText.Text = "请在阶段卡片中修改或删除阶段，再保存计划。";
        return true;
    }
    private async void DetectClick(object sender, RoutedEventArgs e) { services.Capabilities.InvalidateAccess(); await RefreshAsync(); }
    private async Task<string> DiagnosticReportAsync()
    {
        var data = await Task.Run(() => (services.Capabilities.Detect(), services.Scheduler.List()));
        return await services.Log.ReportAsync(data.Item1, data.Item2);
    }
    private async void DiagnosticsClick(object sender, RoutedEventArgs e) => await ExecuteAsync(async () => { DiagnosticOutput.Text = await DiagnosticReportAsync(); }, "诊断已更新。输出已屏蔽用户、设备名称和 SID。");
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"WakeGlass-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "诊断文本|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        await ExecuteAsync(async () => { var report = await DiagnosticReportAsync(); await File.WriteAllTextAsync(dialog.FileName, report); DiagnosticOutput.Text = report; }, "诊断日志已导出。");
    }
    private void TestClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "这是真实硬件测试：将安排约 3 分钟后的唤醒，并在可取消倒计时结束后让电脑实际睡眠。\n\n请保存工作并接通电源。若硬件未唤醒，请手动按电源键。现在开始测试？", "明确确认 · 真实睡眠测试", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetWake(DateTime.Now.AddMinutes(3)); Tabs.SelectedIndex = 0; SleepClick(sender, e);
    }
    private async void SaveSettingsClick(object sender, RoutedEventArgs e) => await ExecuteAsync(() =>
    {
        if (!int.TryParse(CountdownChoice.Text, out int seconds) || seconds is < 5 or > 60) throw new ArgumentException("倒计时应为 5–60 秒的整数。");
        using var gate = ScheduleManager.Lock();
        var settings = services.Settings.Load() with { Theme = ThemeChoice.SelectedIndex switch { 1 => "Dark", 2 => "Light", _ => "System" }, Material = MaterialChoice.SelectedIndex switch { 1 => "Mica", 2 => "Solid", _ => "Acrylic" }, MinimizeToTray = TrayChoice.IsChecked == true, CountdownSeconds = seconds };
        services.Settings.Save(settings); Theme.Apply(settings); return Task.CompletedTask;
    }, "设置已保存。");
    private void SettingsClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 3;
    private void HistoryClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; activeCountdown?.Close(); StatusText.Text = "正在完成或取消当前操作，请稍候。"; return; }
        if (!exiting && services.Settings.Load().MinimizeToTray) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2000, "WakeGlass 已在托盘中运行", "系统计划保持有效。可从托盘菜单退出。", Forms.ToolTipIcon.Info); }
        else if (!exiting) { e.Cancel = true; Exit(); }
    }
    private void Exit() { if (busy) { activeCountdown?.Close(); StatusText.Text = "当前操作结束后即可退出。"; return; } exiting = true; services.Log.Write("app-exit", "用户退出了界面，系统计划已保留。"); Close(); Application.Current.Shutdown(); }
    private void ExitClick(object sender, RoutedEventArgs e) => Exit();
    public sealed record TaskRow(ScheduledTaskInfo Task)
    {
        public string Title => (Task.Spec.Kind switch
        {
            TaskKind.StageWorker => "多阶段 · 后台恢复",
            TaskKind.StageWake => $"多阶段 · {Task.Spec.Start:HH:mm} 唤醒",
            _ => Task.Spec.Name switch { ScheduleManager.SleepName => "每日 · 睡眠", ScheduleManager.WakeName => "每日 · 唤醒", ScheduleManager.InstantName => "一次性 · 唤醒", _ => "安全调度测试" }
        }) + "  /  " + Task.State;
        public string Detail => $"下次：{Task.NextRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}     最近运行：{Task.LastRun?.ToString("MM-dd HH:mm:ss") ?? "尚未运行"}\n结果：{(Task.LastResult == 0 ? "动作返回成功（不代表硬件唤醒已验证）" : Task.LastResult == 267011 ? "尚未运行" : $"0x{Task.LastResult:X8}")}     {(Task.Spec.Kind is TaskKind.Wake or TaskKind.StageWake ? "计划唤醒任务" : Task.Spec.Kind == TaskKind.StageWorker ? "登录及定期恢复后台" : "运行前再次校验唤醒任务")}";
    }
}
