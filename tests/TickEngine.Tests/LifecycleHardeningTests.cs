using Microsoft.Extensions.Time.Testing;
using TickEngine;
using Xunit;

namespace TickEngine.Tests;

/// <summary>
/// 生命周期与在途门控加固回归（对应代码审查 F1–F12）：
/// 会话令牌、迟到帧作废、投递异常/吞帧不再钉死组、Stop 回调可安全改注册表、
/// 空组回收、陈旧卸载条目不再误停新注册、未启动时流逝秒为 0。
/// </summary>
public class LifecycleHardeningTests
{
    private static FakeTimeProvider NewFake() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static long FirstTick(UpdateEngine engine) => engine.Systems.Count == 0 ? 0 : engine.Systems[0].TickNumber;

    /// <summary>边推进假时钟边等条件成立（PeriodicTimer 只在时钟推进后才触发，纯 WaitUntil 永远等不到）。</summary>
    private static bool AdvanceUntil(FakeTimeProvider fake, Func<bool> condition,
                                    int maxSteps = 300, int stepMs = 10, int timeoutMs = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < maxSteps && sw.ElapsedMilliseconds < timeoutMs; i++)
        {
            if (condition()) { return true; }
            fake.Advance(TimeSpan.FromMilliseconds(stepMs));
            Thread.Sleep(3);
        }
        return condition();
    }

    // ---------------- 测试用调度器 ----------------

    /// <summary>手动排空的调度器：收下帧但不执行，由测试决定何时 Drain（模拟宿主队列）。</summary>
    private sealed class ManualDispatcher : IFrameDispatcher
    {
        private readonly Queue<Action> _pending = new();
        public string ThreadDisplayName => "ManualMain";
        public int PendingCount { get { lock (_pending) { return _pending.Count; } } }

        public bool TryPost(Action frame)
        {
            lock (_pending) { _pending.Enqueue(frame); }
            return true;
        }

        /// <summary>执行所有已排队帧（模拟宿主消息泵）。</summary>
        public int Drain()
        {
            int n = 0;
            while (true)
            {
                Action? frame;
                lock (_pending)
                {
                    if (_pending.Count == 0) { return n; }
                    frame = _pending.Dequeue();
                }
                frame();
                n++;
            }
        }
    }

    /// <summary>TryPost 直接抛异常的调度器（模拟句柄销毁时 BeginInvoke 抛错）。</summary>
    private sealed class ThrowingDispatcher : IFrameDispatcher
    {
        public string ThreadDisplayName => "ThrowingMain";
        public bool TryPost(Action frame) => throw new InvalidOperationException("句柄已销毁（测试注入）");
    }

    /// <summary>收下帧却永不执行的调度器（模拟被吞掉的回调）。</summary>
    private sealed class SwallowingDispatcher : IFrameDispatcher
    {
        public string ThreadDisplayName => "SwallowMain";
        public bool TryPost(Action frame) => true;   // 永不执行
    }

    // ---------------- F2：迟到帧不得跑进新会话 ----------------

    [Fact]
    public void StaleMainFrame_FromPreviousSession_DoesNotRunInNewSession()
    {
        var fake = NewFake();
        var dispatcher = new ManualDispatcher();
        using var engine = new UpdateEngine(fake) { Dispatcher = dispatcher };
        var recorder = new TickRecorder("Main");
        engine.Register(recorder, Schedule.Main(100));
        engine.Start();

        // 让本组投出一帧但不排空（帧滞留宿主队列）
        Assert.True(AdvanceUntil(fake, () => dispatcher.PendingCount > 0), "应已投出一帧");

        engine.Stop();
        engine.Start();                       // 换代
        long tickAfterRestart = recorder.Ticks;

        dispatcher.Drain();                   // 迟到帧在此执行——必须整帧作废

        Assert.Equal(tickAfterRestart, recorder.Ticks);   // 迟到帧没有跑进新会话
        engine.Stop();
    }

    [Fact]
    public void MainGate_NotDefeated_AfterStaleFrameRelease()
    {
        var fake = NewFake();
        var dispatcher = new ManualDispatcher();
        using var engine = new UpdateEngine(fake) { Dispatcher = dispatcher };
        var recorder = new TickRecorder("Main");
        engine.Register(recorder, Schedule.Main(100));
        engine.Start();

        Assert.True(AdvanceUntil(fake, () => dispatcher.PendingCount > 0));
        engine.Stop();
        engine.Start();
        dispatcher.Drain();                   // 释放迟到帧（其槽位归属已作废）

        // 新会话照常投递并可执行：从"整帧作废"到"正常跑一帧"不超过一次投递
        long before = recorder.Ticks;
        Assert.True(AdvanceUntil(fake, () => dispatcher.PendingCount > 0), "新会话应继续投递");
        dispatcher.Drain();
        Assert.True(recorder.Ticks > before, "新会话的帧应正常执行（门控未被迟到帧打乱）");
        engine.Stop();
    }

    // ---------------- F3：TryPost 抛异常不再杀死组线程 ----------------

    [Fact]
    public void ThrowingDispatcher_CountedAsDrop_AndLoopSurvives()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new ThrowingDispatcher() };
        int dispatcherFaults = 0;
        engine.DispatcherFaulted += _ => Interlocked.Increment(ref dispatcherFaults);
        var recorder = new TickRecorder("Main");
        engine.Register(recorder, Schedule.Main(100));
        engine.Start();

        // 递进若干拍：每拍 TryPost 抛异常 → 计丢拍，但引擎/组必须继续活着（不再被判死）
        Assert.True(AdvanceUntil(fake, () => engine.Systems.Single().Dropped >= 3),
                    $"投递失败应持续计丢拍, got {engine.Systems.Single().Dropped}");

        var view = engine.Systems.Single();
        Assert.True(engine.IsRunning, "引擎应仍在运行");
        Assert.True(Volatile.Read(ref dispatcherFaults) >= 1, "应上报 DispatcherFaulted 供宿主感知");
        engine.Stop();
    }

    // ---------------- F4：吞帧的宿主不再把组永久钉死 ----------------

    [Fact]
    public void SwallowingDispatcher_SlotReclaimedByWatchdog()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new SwallowingDispatcher() };
        Exception? fault = null;
        engine.DispatcherFaulted += ex => fault = ex;
        var recorder = new TickRecorder("Main");
        engine.Register(recorder, Schedule.Main(100));   // 周期 10ms → 看门狗阈值 max(1s, 10×周期) = 1s
        engine.Start();

        // 持续递进节拍（看门狗用真实 Stopwatch 时长判定，故此处等真实时间）
        Assert.True(AdvanceUntil(fake, () => Volatile.Read(ref fault) is not null,
                                maxSteps: 600, stepMs: 10, timeoutMs: 8000),
                    "看门狗应在有界时间内回收被吞掉的在途帧");

        Assert.NotNull(fault);                                            // 看门狗出手
        Assert.Contains("在途帧", fault!.Message);
        Assert.True(engine.Systems.Single().Dropped >= 2, "回收应计丢拍");
        engine.Stop();
    }

    // ---------------- F5：流逝秒的三种状态 ----------------

    [Fact]
    public void ElapsedSeconds_ZeroBeforeStart_AndFrozenAfterStop()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new ImmediateDispatcher() };
        var recorder = new TickRecorder("W");
        engine.Register(recorder, Schedule.Worker(100));

        Assert.Equal(0, engine.Systems.Single().ElapsedSeconds);   // 未启动 = 0（不是"开机时长"）

        engine.Start();
        TestUtil.AdvanceSteps(fake, 4, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        double running = engine.Systems.Single().ElapsedSeconds;
        Assert.True(running > 0);

        engine.Stop();
        double frozen = engine.Systems.Single().ElapsedSeconds;
        Thread.Sleep(60);
        Assert.Equal(frozen, engine.Systems.Single().ElapsedSeconds, precision: 9);   // 停后冻结，不再增长
        Assert.True(frozen > 0);
    }

    // ---------------- F6：成员内 Stop 后，同帧后续成员不得再收到 Update ----------------

    [Fact]
    public void StopFromInsideUpdate_LaterMembersAreNotUpdatedAfterTheirStop()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new ImmediateDispatcher() };
        var stopper = new StopFromUpdateSystem(engine);
        var later = new StopOrderRecorder("Later");
        engine.Register(stopper, Schedule.Worker(100));
        engine.Register(later, Schedule.Worker(100));
        engine.Start();

        for (int i = 0; i < 3; i++) { fake.Advance(TimeSpan.FromMilliseconds(10)); Thread.Sleep(5); }

        Assert.True(stopper.StopCalls >= 1, "引擎应已停止");
        Assert.Equal(0, later.UpdatesAfterStop);   // 关键断言：Stop 之后不再 Update
        engine.Stop();
    }

    // ---------------- F7：用户 Stop 回调里改注册表不再炸引擎 ----------------

    [Fact]
    public void UserStopCallback_MayRegisterAndUnregister_WithoutBreakingStop()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new ImmediateDispatcher() };
        var added = new TickRecorder("Added");
        var victim = new TickRecorder("Victim");
        var mutator = new RegistryMutatingSystem(engine, added, victim);
        engine.Register(mutator, Schedule.Worker(100));
        engine.Register(victim, Schedule.Worker(100));
        engine.Start();
        TestUtil.AdvanceSteps(fake, 2, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        engine.Stop();     // 旧实现：用户 Stop 里的 Register/Unregister 触发"集合已修改"异常逃逸

        Assert.False(engine.IsRunning);
        engine.Start();    // 半停状态的引擎无法再次启动（旧实现会走到这里就出问题）
        engine.Stop();
    }

    // ---------------- F8：成员全迁走后空组被回收 ----------------

    [Fact]
    public void EmptyWorkerGroup_IsPrunedAfterMigration()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new ImmediateDispatcher() };
        var mover = new TickRecorder("Mover");
        engine.Register(mover, Schedule.Worker(100, "Old"));
        engine.Start();
        TestUtil.AdvanceSteps(fake, 2, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.Equal(1, engine.GroupCount);

        engine.ChangeSchedule(mover, Schedule.Worker(100, "New"));
        Assert.Equal(2, engine.GroupCount);                        // 新组成立（旧组尚未回收）

        Assert.True(AdvanceUntil(fake, () => engine.GroupCount == 1),
                    $"空组应被回收, got {engine.GroupCount}");
        engine.Stop();
    }

    // ---------------- F12：陈旧卸载条目不得停掉重新注册的实例 ----------------

    [Fact]
    public void StaleUnregisterEntry_DoesNotStopReRegisteredInstance()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake) { Dispatcher = new ImmediateDispatcher() };
        var sys = new StopCountingSystem("ReReg");
        engine.Register(sys, Schedule.Worker(1, "Slow"));          // 慢组：卸载队列要等一拍（1s）才消费
        engine.Start();
        fake.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(20);

        engine.Unregister(sys);                                    // 进入慢组的卸载队列
        engine.Register(sys, Schedule.Worker(100, "Fast"));        // 立刻重新注册到快组

        // 新组正常跑
        Assert.True(AdvanceUntil(fake, () => sys.Ticks >= 2), "重新注册的实例应被调度");
        // 关键：推进足够时间让**慢组**消费它的卸载队列（旧实现会在此处停掉已重新注册的实例）
        fake.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(50);

        Assert.Equal(0, sys.StopCalls);
        engine.Stop();
    }

    // ============ 测试行为 ============

    private sealed class TickRecorder : UpdateSystem
    {
        private long _ticks;
        public TickRecorder(string name) => Name = name;
        public override string Name { get; }
        public long Ticks => Interlocked.Read(ref _ticks);
        protected override void Update(in FrameContext frame) => Interlocked.Increment(ref _ticks);
    }

    /// <summary>在 Update 里调用 engine.Stop() 的系统（文档标注为"容忍但不推荐"的用法）。</summary>
    private sealed class StopFromUpdateSystem : UpdateSystem
    {
        private readonly UpdateEngine _engine;
        private long _stopCalls;
        public StopFromUpdateSystem(UpdateEngine engine) => _engine = engine;
        public override string Name => "Stopper";
        public long StopCalls => Interlocked.Read(ref _stopCalls);

        protected override void Update(in FrameContext frame)
        {
            _engine.Stop();
        }

        protected override void Stop() => Interlocked.Increment(ref _stopCalls);
    }

    /// <summary>记录"自己的 Stop 之后是否又被 Update 过"（F6 的核心断言载体）。</summary>
    private sealed class StopOrderRecorder : UpdateSystem
    {
        private int _stopped;
        private int _updatesAfterStop;
        public StopOrderRecorder(string name) => Name = name;
        public override string Name { get; }
        public int UpdatesAfterStop => Volatile.Read(ref _updatesAfterStop);

        protected override void Update(in FrameContext frame)
        {
            if (Volatile.Read(ref _stopped) == 1) { Interlocked.Increment(ref _updatesAfterStop); }
        }

        protected override void Stop() => Volatile.Write(ref _stopped, 1);
    }

    /// <summary>Stop 回调里改注册表的系统（F7）。</summary>
    private sealed class RegistryMutatingSystem : UpdateSystem
    {
        private readonly UpdateEngine _engine;
        private readonly UpdateSystem _added;
        private readonly UpdateSystem _victim;
        private int _done;

        public RegistryMutatingSystem(UpdateEngine engine, UpdateSystem added, UpdateSystem victim)
        {
            _engine = engine;
            _added = added;
            _victim = victim;
        }

        public override string Name => "Mutator";

        protected override void Stop()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) { return; }
            _engine.Register(_added, Schedule.Worker(50, "AddedGroup"));
            _engine.Unregister(_victim);
        }
    }

    private sealed class StopCountingSystem : UpdateSystem
    {
        private long _ticks;
        private long _stopCalls;
        public StopCountingSystem(string name) => Name = name;
        public override string Name { get; }
        public long Ticks => Interlocked.Read(ref _ticks);
        public long StopCalls => Interlocked.Read(ref _stopCalls);

        protected override void Update(in FrameContext frame) => Interlocked.Increment(ref _ticks);
        protected override void Stop() => Interlocked.Increment(ref _stopCalls);
    }
}
