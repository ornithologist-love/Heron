namespace Heron.Core.Scheduling;

/// <summary>
///     每日定点任务运行核心（纯 BCL、无 UI / 无 ASP.NET 依赖）：
///     - 计算下一次运行时刻（当天未到 → 今天；已过 → 明天）
///     - 循环：休眠到点 → 安全执行任务（异常被捕获并回报）→ 计算下一次；启动时不立即执行
/// </summary>
public static class DailyTaskRunner
{
    /// <summary>计算每日定点任务的下次运行时刻（本地时间）。</summary>
    public static DateTime ComputeNextRun(DateTime now, int hour, int minute)
    {
        var today = now.Date.AddHours(hour).AddMinutes(minute);
        return now >= today ? today.AddDays(1) : today;
    }

    public static Task RunDailyAsync(
        string taskName, int hour, int minute,
        Func<CancellationToken, Task> action,
        Action<string>? log = null,
        CancellationToken ct = default) =>
        RunDailyAsync(taskName, hour, minute, action, log, ct, null);

    /// <summary>每日定点循环，同时向宿主回报当前实际等待的计划时刻。</summary>
    public static async Task RunDailyAsync(
        string taskName, int hour, int minute,
        Func<CancellationToken, Task> action,
        Action<string>? log,
        CancellationToken ct,
        Action<DateTime>? scheduled)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextRun = ComputeNextRun(now, hour, minute);
            var delay = nextRun - now;

            log?.Invoke($"[{taskName}] 下次运行 {nextRun:yyyy-MM-dd HH:mm}（{delay:c} 后）");
            scheduled?.Invoke(nextRun);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                // A suspend/reschedule can cancel a delay that has just completed.
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            log?.Invoke($"[{taskName}] 开始执行");

            try
            {
                await action(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                log?.Invoke($"[{taskName}] 已取消（宿主关闭）");
                break;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[{taskName}] 执行失败: {ex.Message}");
            }
        }

        log?.Invoke($"[{taskName}] 调度循环已停止");
    }
}
