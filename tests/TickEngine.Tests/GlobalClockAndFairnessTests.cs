using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace TickEngine.Tests;

/// <summary>
/// v2 语义测试：
///  A) 引擎纪元 GlobalTick/GlobalSeconds（所有循环共享同一时间轴，确定性推进）；
///  B) 组级在途（每 Main 组至多一帧排队）：低频组不再被高频组共享忙标志饿死。
/// </summary>
public sealed class GlobalClockAndFairnessTests
{
    private static FakeTimeProvider NewFake() =>
        new(DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcDateTime);

    // ---------- 引擎纪元：GlobalTick / GlobalSeconds ----------

    [Fact]
    public void GlobalClock_AllGroupsShareSameMonotonicTimeAxis()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = new ImmediateDispatcher(),
        };
        var w = new CtxRecorder("w");   // Worker 100Hz
        var m = new CtxRecorder("m");   // Main 50Hz
        engine.Register(w, Schedule.Worker(100, "W"));
        engine.Register(m, Schedule.Main(50, "M"));
        engine.Start();

        // 推进 500ms（Worker 步进 10ms × 50）
        TestUtil.AdvanceSteps(fake, 50, TimeSpan.FromMilliseconds(10), () => engine.GlobalTick);

        var wCalls = w.Calls;
        var mCalls = m.Calls;
        Assert.NotEmpty(wCalls);
        Assert.NotEmpty(mCalls);

        // 全局秒 ≈ 0.5s；拍号 = 秒×1000（1ms 刻度）
        Assert.InRange(engine.GlobalSeconds, 0.45, 0.6);
        Assert.InRange(engine.GlobalTick, 450, 600);

        // 各循环内部：GlobalTick/GlobalSeconds 随推进单调递增（同一时间轴）
        AssertMonotonic(wCalls.Select(c => c.GlobalTick).ToArray(), "Worker 组 GlobalTick");
        AssertMonotonic(mCalls.Select(c => c.GlobalTick).ToArray(), "Main 组 GlobalTick");
        AssertMonotonic(wCalls.Select(c => c.GlobalSeconds).ToArray(), "Worker 组 GlobalSeconds");

        // 不同 FPS 组同刻读到的全局秒应接近（同一时间轴）
        double wEnd = wCalls[^1].GlobalSeconds;
        double mEnd = mCalls[^1].GlobalSeconds;
        Assert.True(Math.Abs(wEnd - mEnd) < 0.05, $"两组末尾全局秒应同轴: w={wEnd:F3} m={mEnd:F3}");
        engine.Stop();
    }

    private static void AssertMonotonic<T>(IReadOnlyList<T> values, string label)
        where T : IComparable<T>
    {
        for (int i = 1; i < values.Count; i++)
        {
            Assert.True(values[i].CompareTo(values[i - 1]) >= 0, $"{label} 必须单调不降");
        }
    }

    [Fact]
    public void GlobalClock_Restart_ResetsEpoch()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var w = new CtxRecorder("w");
        engine.Register(w, Schedule.Worker(100));
        engine.Start();
        TestUtil.AdvanceSteps(fake, 20, TimeSpan.FromMilliseconds(10), () => engine.GlobalTick);
        Assert.True(engine.GlobalSeconds > 0.15);

        engine.Stop();
        Assert.Equal(0, engine.GlobalTick);      // 未运行 = 0
        Assert.Equal(0, engine.GlobalSeconds);

        engine.Start();                          // 重启：纪元重置
        Assert.True(engine.GlobalSeconds < 0.05);
        engine.Stop();
    }

    // ---------- 组级在途：低频组不被饿死 ----------

    [Fact]
    public void TwoMainGroups_DifferentRates_BothProgress_NoStarvation()
    {
        var fake = NewFake();
        // 可排队的宿主：TryPost 总接受并记录（模拟 UI 消息队列），慢速串行消费由测试线程做
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = new QueueDrainDispatcher(),
        };
        var slow = new CtxRecorder("slow");    // 10Hz
        var fast = new CtxRecorder("fast");    // 100Hz
        engine.Register(slow, Schedule.Main(10, "Slow"));
        engine.Register(fast, Schedule.Main(100, "Fast"));
        engine.Start();

        var disp = (QueueDrainDispatcher)engine.Dispatcher!;
        // 跑约 1.1s 假时钟；一边推进一边由宿主消费队列（模拟 UI 消息泵串行执行）
        for (int i = 0; i < 110; i++)
        {
            fake.Advance(TimeSpan.FromMilliseconds(10));
            disp.DrainAll();
            Thread.Sleep(1);
            disp.DrainAll();   // 节拍线程可能稍晚才入队，补一次消费
        }
        Assert.True(TestUtil.WaitUntil(() => slow.Calls.Length >= 8, 5000),
            "10Hz 低频组应持续获得执行（v2 组级在途：不再被高频组饿死）");

        // 低频 10Hz 在 ~1.1s 内应得到 ~11 次执行而非饿死为 0
        Assert.InRange(slow.Calls.Length, 8, 15);
        // 高频 100Hz 应比低频执行更多（即便宿主消费节奏略有丢拍）
        Assert.True(fast.Calls.Length > slow.Calls.Length * 3,
            $"100Hz 应显著多于 10Hz: fast={fast.Calls.Length} slow={slow.Calls.Length}");

        engine.Stop();
    }

    // ============ 测试装置 ============

    /// <summary>记录每拍 FrameContext（含 GlobalTick/GlobalSeconds）。</summary>
    private sealed class CtxRecorder : UpdateSystem
    {
        private readonly List<FrameContext> _calls = new();
        private readonly object _gate = new();
        private readonly string _id;
        public CtxRecorder(string id) => _id = id;
        public override string Name => _id;

        public FrameContext[] Calls
        {
            get { lock (_gate) { return _calls.ToArray(); } }
        }

        protected override void Update(in FrameContext frame)
        {
            lock (_gate) { _calls.Add(frame); }
        }
    }

    /// <summary>模拟 UI 消息队列：TryPost 总接受并排队；DrainAll 串行执行（模拟消息泵空闲时消费）。</summary>
    private sealed class QueueDrainDispatcher : IFrameDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public string ThreadDisplayName => "QueueUI";

        public bool TryPost(Action frame)
        {
            _queue.Enqueue(frame);
            return true;   // 纯投递：永远接受（v2 语义：忙检测已上移到 Loop）
        }

        public int Pending => _queue.Count;

        public void DrainAll()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
