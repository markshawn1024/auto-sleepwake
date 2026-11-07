using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using WakeGlass.Core;

namespace WakeGlass.App;

internal static class StageWorker
{
    public static int Run(Services services)
    {
        using var instance = new Mutex(true, "Global\\WakeGlass-Stages-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out var first);
        if (!first) return 0;
        if (services.Settings.Load().Stages?.Active.Any() != true) return 0;

        var clock = TimeProvider.System;
        var power = new PowerService();
        var runner = new StageRunner(clock, services.StageRuns, power, services.Log);
        var guard = new StageSleepGuard(services.Scheduler, services.Settings, services.Capabilities, power, services.Log, clock);
        using var notifications = new SuspendNotifications();
        services.Log.Write("stage-worker-start", "多阶段后台进程已启动，按当前系统时间恢复阶段状态。");
        long nextRegistrationCheck = 0;
        try
        {
            while (true)
            {
                if (notifications.Interrupted) runner.Interrupt();
                TimeZoneInfo.ClearCachedData();
                var config = services.Settings.Load();
                var plan = config.Stages ?? StagePlan.Empty;
                if (!plan.Active.Any()) return 0;
                StageTimeline.Validate(plan, config.Schedule);
                if (Environment.TickCount64 >= nextRegistrationCheck)
                {
                    var worker = new TaskSpec(StageScheduleManager.WorkerName, TaskKind.StageWorker, DateTime.Today, Id: "stages");
                    if (!services.Scheduler.Verify(worker)) throw new InvalidOperationException("Windows 后台恢复任务已禁用或与当前程序不匹配。");
                    nextRegistrationCheck = Environment.TickCount64 + 30_000;
                }
                if (!runner.Tick(plan, occurrence =>
                {
                    using var sleepLock = services.TrySleepLock();
                    if (sleepLock == null) throw new OperationCanceledException("另一项睡眠操作正在处理，跳过本次自动睡眠。");
                    using var scheduleLock = ScheduleManager.Lock();
                    if (notifications.Interrupted) throw new OperationCanceledException("期间检测到系统睡眠或恢复，跳过自动睡眠。");
                    guard.Finish(occurrence);
                })) return 0;
                Thread.Sleep(1000);
            }
        }
        finally
        {
            runner.Stop();
            services.Log.Write("stage-worker-stop", "多阶段后台进程已停止，已解除保持运行请求。");
        }
    }

    private sealed class SuspendNotifications : IDisposable
    {
        private readonly Callback callback;
        private readonly IntPtr handle;
        private int interrupted;

        public SuspendNotifications()
        {
            callback = OnPowerEvent;
            var subscription = new Subscription { Routine = callback };
            uint result = PowerRegisterSuspendResumeNotification(2, ref subscription, out handle);
            if (result != 0) throw new Win32Exception((int)result, "无法监听 Windows 睡眠与恢复事件。");
        }

        public bool Interrupted => Interlocked.Exchange(ref interrupted, 0) != 0;

        private uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
        {
            if (type is 4 or 18) Interlocked.Exchange(ref interrupted, 1);
            return 0;
        }

        public void Dispose()
        {
            PowerUnregisterSuspendResumeNotification(handle);
            GC.KeepAlive(callback);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Callback(IntPtr context, uint type, IntPtr setting);
        [StructLayout(LayoutKind.Sequential)] private struct Subscription { public Callback Routine; public IntPtr Context; }
        [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription recipient, out IntPtr registration);
        [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
    }
}
