namespace TickEngine.Tests;

/// <summary>测试辅助：轮询直到条件满足或超时。</summary>
public static class TestUtil
{
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) { return false; }
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>以单步步长推进假时钟 N 步，每步等引擎消费一拍，保证确定性。</summary>
    public static void AdvanceSteps(FakeTimeProvider time, int steps, TimeSpan step,
                                    Func<long> observedTicks, int settleMs = 2)
    {
        for (int i = 0; i < steps; i++)
        {
            long before = observedTicks();
            time.Advance(step);
            if (!WaitUntil(() => observedTicks() > before, 2000))
            {
                throw new TimeoutException($"第 {i} 步推进后引擎未消费拍");
            }
            if (settleMs > 0) { Thread.Sleep(settleMs); }
        }
    }
}

/// <summary>同步直投调度器：立即在同一线程执行（模拟"立刻可执行"的主线程）。</summary>
public sealed class ImmediateDispatcher : IFrameDispatcher
{
    public string ThreadDisplayName => "TestMain";

    public bool TryPost(Action frame)
    {
        frame();
        return true;
    }
}

/// <summary>可控调度器：busy=true 时拒投（模拟主线程忙），否则直投。</summary>
public sealed class BusyDispatcher : IFrameDispatcher
{
    public volatile bool Busy;

    public string ThreadDisplayName => "BusyMain";

    public bool TryPost(Action frame)
    {
        if (Busy)
        {
            return false;
        }
        frame();
        return true;
    }
}

/// <summary>静默记录调度器：记录投递次数但不执行（模拟消息队列堆积被拒）。</summary>
public sealed class QueueDispatcher : IFrameDispatcher
{
    public int Posted;
    public string ThreadDisplayName => "QueueMain";

    public bool TryPost(Action frame)
    {
        Interlocked.Increment(ref Posted);
        return true;
    }
}
