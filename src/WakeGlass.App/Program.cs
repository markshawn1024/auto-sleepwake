using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using WakeGlass.Core;

namespace WakeGlass.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        bool simulate = args is ["--simulate"];
        if (simulate) args = [];
        var services = new Services(simulate);
        if (args is ["--stage-worker"] or ["--stage-wake", _])
        {
            try
            {
                if (args.Length == 1) return StageWorker.Run(services);
                if (!Guid.TryParseExact(args[1], "N", out var id)) return 2;
                var stage = services.Settings.Load().Stages?.Active.FirstOrDefault(item => item.Id == id);
                if (stage == null) return 0;
                services.Log.Write("stage-wake-trigger", $"Windows 已启动 {stage.TimeLabel} 阶段任务，实际执行时间 {DateTimeOffset.Now:MM-dd HH:mm:ss zzz}。");
                services.Scheduler.StartStageWorker();
                return 0;
            }
            catch (Exception error) { services.Log.Write("stage-worker-error", error.Message); return 1; }
        }
        try
        {
            if (args.Length > 0)
            {
                if (args is ["--probe"]) { services.Log.Write("probe-executed", "任务调度测试已执行（未调用睡眠 API）。"); return 0; }
                if (args is ["--wake", "daily" or "instant"])
                {
                    services.Log.Write("wake-action", "唤醒任务动作已执行；这仅表示 Windows 运行了任务，不能证明此前发生睡眠或由此任务唤醒。");
                    var result = Diagnostics.RunPowerCfgAsync("/lastwake").GetAwaiter().GetResult();
                    services.Log.Write("lastwake", result);
                    if (args[1] == "instant") { using var gate = ScheduleManager.Lock(); var t = services.Scheduler.List().FirstOrDefault(x => x.Spec.Name == ScheduleManager.InstantName); if (t != null && t.Spec.Start <= DateTime.Now) services.Scheduler.Delete(t.Spec.Name); }
                    return 0;
                }
                if (args is ["--scheduled-sleep", "daily"])
                {
                    using var sleepMutex = services.TrySleepLock();
                    if (sleepMutex == null) return 3;
                    var wake = ScheduledSleepGuard.Validate(services.Scheduler, services.Settings, services.Capabilities, DateTime.Now);
                    var worker = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    Theme.Apply(services.Settings.Load());
                    worker.Startup += async (_, _) =>
                    {
                        try
                        {
                            var dialog = new CountdownWindow(wake, services.Settings.Load().CountdownSeconds);
                            if (dialog.ShowDialog() == true)
                            {
                                await System.Threading.Tasks.Task.Run(() =>
                                {
                                    using var gate = ScheduleManager.Lock();
                                    ScheduledSleepGuard.Validate(services.Scheduler, services.Settings, services.Capabilities, DateTime.Now);
                                    services.Log.Write("scheduled-sleep-request", "每日计划已验证，正在请求系统睡眠。"); services.Power.Sleep();
                                });
                            }
                            else services.Log.Write("scheduled-sleep-cancelled", "用户取消了本次自动睡眠；后续每日计划保持有效。");
                        }
                        catch (Exception ex) { services.Log.Write("scheduled-sleep-blocked", ex.Message); MessageBox.Show(Diagnostics.Redact(ex.Message), "WakeGlass · 已阻止睡眠", MessageBoxButton.OK, MessageBoxImage.Warning); }
                        finally { worker.Shutdown(); }
                    };
                    return worker.Run();
                }
                if (args is not ["--show"]) throw new ArgumentException("不支持的启动参数。");
            }
            using var instance = new Mutex(true, "Local\\WakeGlass-UI-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out bool first);
            if (!first)
            {
                try { using var signal = EventWaitHandle.OpenExisting(Services.OpenSignalName); signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { var handle = FindWindow(null, "WakeGlass"); if (handle != IntPtr.Zero) { ShowWindow(handle, 9); SetForegroundWindow(handle); } }
                return 0;
            }
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.DispatcherUnhandledException += (_, e) => { services.Log.Write("ui-error", e.Exception.Message); MessageBox.Show(Diagnostics.Redact(e.Exception.Message), "WakeGlass", MessageBoxButton.OK, MessageBoxImage.Warning); e.Handled = true; };
            Theme.Apply(services.Settings.Load());
            var window = new MainWindow(services); application.MainWindow = window; window.Show();
            return application.Run();
        }
        catch (Exception ex) { services.Log.Write("startup-error", ex.Message); MessageBox.Show(Diagnostics.Redact(ex.Message), "WakeGlass · 无法完成操作", MessageBoxButton.OK, MessageBoxImage.Warning); return 1; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string name);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
public sealed class Services
{
    public static string OpenSignalName => "Local\\WakeGlass-Open-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    public SettingsStore Settings { get; } = new();
    public Diagnostics Log { get; } = new();
    public WakeScheduler Scheduler { get; } = new(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WakeGlass.exe"));
    public IPowerService Power { get; }
    public bool Simulate { get; }
    public SystemCapabilityService Capabilities { get; }
    public ScheduleManager Schedules { get; }
    public StageScheduleManager StageSchedules { get; }
    public StageRunStore StageRuns { get; } = new();
    public SleepCoordinator Sleep { get; }
    public Services(bool simulate = false)
    {
        Simulate = simulate;
        Power = simulate ? new SimulatedPower(Log) : new PowerService();
        Capabilities = new(Scheduler);
        Schedules = new(Scheduler, Settings, Log);
        StageSchedules = new(Scheduler, Settings, Log, TimeProvider.System);
        Sleep = new(Scheduler, Power, Capabilities, Log);
    }
    private sealed class SimulatedPower(Diagnostics log) : IPowerService { public void Sleep() => log.Write("simulated-sleep", "模拟电源服务已接收请求；没有调用任何系统睡眠 API。"); }
    public IDisposable? TrySleepLock()
    {
        var m = new Mutex(false, "Local\\WakeGlass-Sleep-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        try { if (!m.WaitOne(0)) { m.Dispose(); return null; } } catch (AbandonedMutexException) { }
        return new SleepLease(m);
    }
    private sealed class SleepLease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
}
