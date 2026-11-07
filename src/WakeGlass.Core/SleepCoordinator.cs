namespace WakeGlass.Core;

public sealed class SleepCoordinator(IWakeScheduler scheduler, IPowerService power, ICapabilityService capabilities, IDiagnostics log)
{
    public async Task RunInstantAsync(DateTime wake, Func<CancellationToken, Task<bool>> countdown, CancellationToken cancellationToken)
    {
        var c = await Task.Run(capabilities.Detect, cancellationToken);
        if (c.BlockReason is { } reason) throw new InvalidOperationException(reason);
        TimeMath.ValidateWake(wake, DateTime.Now);
        var spec = new TaskSpec(ScheduleManager.InstantName, TaskKind.Wake, wake);
        bool registered = false, requested = false;
        try
        {
            await Task.Run(() => { using var gate = ScheduleManager.Lock(); scheduler.Register(spec); registered = true; if (!scheduler.Verify(spec)) throw new IOException("唤醒任务回读验证失败，已阻止睡眠。"); }, cancellationToken);
            log.Write("wake-verified", $"一次性唤醒任务已回读确认：{wake:yyyy-MM-dd HH:mm:ss}。");
            if (!await countdown(cancellationToken)) throw new OperationCanceledException("用户取消了睡眠。");
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                using var gate = ScheduleManager.Lock();
                if (capabilities.Detect().BlockReason is { } blocked) throw new InvalidOperationException(blocked);
                if (wake <= DateTime.Now.AddSeconds(30) || !scheduler.Verify(spec)) throw new IOException("唤醒任务已变化或即将过期，已阻止睡眠。");
                log.Write("sleep-request", "已请求系统睡眠；实际进入睡眠与硬件唤醒结果尚待系统事件确认。");
                power.Sleep(); requested = true;
            }, cancellationToken);
        }
        finally
        {
            if (registered && !requested) await Task.Run(() => { using var gate = ScheduleManager.Lock(); scheduler.Delete(spec.Name); log.Write("sleep-cancelled", "睡眠未执行，一次性唤醒任务已清理。"); });
        }
    }
}
