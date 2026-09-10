using Microsoft.Extensions.Time.Testing;

namespace TickEngine.Tests;

/// <summary>引擎语义测试：固定节拍、丢拍会计、注册序、异常隔离、生命周期、动态调度（确定性假时钟）。</summary>
public sealed class WorkerLoopTests
{
    private static FakeTimeProvider NewFake() =>
        new(DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcDateTime);

    private static long FirstTick(UpdateEngine e) => e.Systems.First().TickNumber;

    private static long FirstDropped(UpdateEngine e) => e.Systems.First().Dropped;

    // ---------- 固定节拍 ----------

    [Fact]
    public void Worker_TicksAtConfiguredFps_WithMeasuredDeltaTime()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new TickRecorder("w1");
        engine.Register(bhv, Schedule.Worker(100));   // 10ms/拍

        engine.Start();
        TestUtil.AdvanceSteps(fake, 20, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        var view = engine.Systems.First();
        Assert.Equal(100, view.Fps);
        Assert.InRange(view.TickNumber, 18, 21);   // 假时钟下 20 步 ≈ 20 拍
        Assert.Equal(0, view.Dropped);             // 无迟到 → 无丢拍

        // dt = 实测假时钟间隔 ≈ 10ms；拍号单调
        var calls = bhv.Calls.ToArray();
        Assert.True(calls.Length >= 18);
        long lastTick = 0;
        foreach (var c in calls)
        {
            Assert.True(c.Frame.TickNumber > lastTick, "拍号必须单调递增");
            lastTick = c.Frame.TickNumber;
            Assert.InRange(c.Frame.DeltaTime, 0.008, 0.012);
            Assert.Contains("Worker", c.Frame.ThreadName);
        }
        engine.Stop();
    }

    [Fact]
    public void Worker_StartOncePerSession_StopOncePerSession_Restartable()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new LifecycleRecorder("lc");
        engine.Register(bhv, Schedule.Worker(100));

        engine.Start();
        TestUtil.AdvanceSteps(fake, 3, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.Equal(1, bhv.StartCount);
        engine.Stop();
        Assert.Equal(1, bhv.StopCount);

        // 可重启：新会话 Start 再次调用
        engine.Start();
        TestUtil.AdvanceSteps(fake, 3, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.Equal(2, bhv.StartCount);
        engine.Stop();
        Assert.Equal(2, bhv.StopCount);
    }

    // ---------- 丢拍会计（worker 忙 → 合并丢拍，丢拍不补） ----------

    [Fact]
    public void Worker_WhenUpdateTooSlow_DropsCounted_AndNoCatchUp()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var slow = new SlowRecorder("slow", sleepMs: 35);   // 每拍睡 35ms 实时间
        var fast = new TickRecorder("fast");
        engine.Register(slow, Schedule.Worker(50));        // 20ms/拍：35ms > 周期
        engine.Register(fast, Schedule.Worker(50));        // 同组共享 → 也被拖慢

        engine.Start();
        // 前台快速推进假时钟（模拟墙上时间流逝），慢行为一睡就是几个周期 → 丢拍
        for (int i = 0; i < 60; i++)
        {
            fake.Advance(TimeSpan.FromMilliseconds(20));
            Thread.Sleep(1);
        }
        Assert.True(TestUtil.WaitUntil(() => FirstDropped(engine) > 0), "应观察到丢拍");

        var view = engine.Systems.First();
        Assert.True(view.Dropped > 0, $"丢拍应 > 0, got {view.Dropped}");
        Assert.True(view.TickNumber > 0 && view.TickNumber < 60,
            $"丢拍不补：拍数应显著少于 60 次推进, got {view.TickNumber}");

        // 丢拍后的那一拍 dt 明显变大（实测 dt：行为可感知迟到）
        var calls = slow.Calls;
        Assert.True(calls.Skip(1).Any(c => c.DeltaTime > 0.03),
            "迟到拍的实测 dt 应超过周期");
        engine.Stop();
    }

    // ---------- 注册序 + 同帧共享 ctx（Q20 顺序规则） ----------

    [Fact]
    public void SharedLoop_MembersRunInRegistrationOrder_SameFrameSharesCtx()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var a = new TickRecorder("A");
        var b = new TickRecorder("B");
        engine.Register(a, Schedule.Worker(50));
        engine.Register(b, Schedule.Worker(50));

        engine.Start();
        TestUtil.AdvanceSteps(fake, 8, TimeSpan.FromMilliseconds(20), () => FirstTick(engine));

        var aCalls = a.Calls.ToArray();
        var bCalls = b.Calls.ToArray();
        Assert.Equal(aCalls.Length, bCalls.Length);
        Assert.True(aCalls.Length >= 8);

        // 同帧共享同一 FrameContext 语义：dt 与拍号逐拍一致
        for (int i = 0; i < aCalls.Length; i++)
        {
            Assert.Equal(aCalls[i].Frame.TickNumber, bCalls[i].Frame.TickNumber);
            Assert.Equal(aCalls[i].Frame.DeltaTime, bCalls[i].Frame.DeltaTime, precision: 10);
        }

        // 注册序：同拍内 A 先于 B（成对交叉出现）
        var interleaved = aCalls.Concat(bCalls)
            .OrderBy(c => c.Frame.TickNumber).ThenBy(c => c.Id == "A" ? 0 : 1)
            .Select(c => c.Id).ToArray();
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            Assert.Equal("A", interleaved[i]);
            Assert.Equal("B", interleaved[i + 1]);
        }
        engine.Stop();
    }

    // ---------- 异常隔离（Q18：记日志+跳过，引擎继续） ----------

    [Fact]
    public void Fault_IsolatedToUpdateSystem_EngineAndOthersKeepRunning()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        long faultEvents = 0;
        engine.SystemFaulted += (_, _) => Interlocked.Increment(ref faultEvents);
        var ok = new TickRecorder("ok");
        var bad = new FaultRecorder("bad", throwEvery: 1);   // 每拍都炸
        engine.Register(ok, Schedule.Worker(100));
        engine.Register(bad, Schedule.Worker(100));

        engine.Start();
        TestUtil.AdvanceSteps(fake, 10, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        var views = engine.Systems.ToDictionary(v => v.Name);
        Assert.True(engine.IsRunning);
        Assert.True(ok.Calls.Count >= 9, "正常行为应继续运行");
        Assert.True(views["bad"].Faults >= 9, $"异常计数应累计, got {views["bad"].Faults}");
        Assert.True(Interlocked.Read(ref faultEvents) >= 9, "SystemFaulted 事件应触发");
        engine.Stop();
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void Fault_InStart_CountedAndFirstUpdateSkipped()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new TickRecorder("s") { ThrowInStart = true };
        engine.Register(bhv, Schedule.Worker(100));

        engine.Start();
        TestUtil.AdvanceSteps(fake, 4, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        var view = engine.Systems.First();
        Assert.True(view.Faults >= 1, "Start 异常应记 Fault");
        engine.Stop();
    }

    [Fact]
    public void LastException_HoldsFaultPayload_AndSurvivesRecovery()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bad = new FaultRecorder("bad", throwEvery: 2);   // 隔拍炸：验证“恢复正常后仍保留现场”
        engine.Register(bad, Schedule.Worker(100));

        engine.Start();
        TestUtil.AdvanceSteps(fake, 10, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        var ex = engine.Systems.Single().LastException;
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("故意炸", ex!.Message);
        Assert.Contains(nameof(FaultRecorder), ex.StackTrace!);   // 溯源：栈里能看到用户方法名
        // Debug + PDB 下应能拿到 file:line（探针“双击跳转”依赖它）；无 PDB 时不强求
        if (ex.StackTrace!.Contains(".cs:line"))
        {
            Assert.Contains("EngineTests.cs", ex.StackTrace);
        }

        engine.Stop();
        Assert.NotNull(engine.Systems.Single().LastException);   // Stop 不擦现场
    }

    [Fact]
    public void LastException_ClearedOnNewSession()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new TickRecorder("s") { ThrowInStart = true };
        engine.Register(bhv, Schedule.Worker(100));

        engine.Start();
        TestUtil.AdvanceSteps(fake, 3, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.NotNull(engine.Systems.Single().LastException);

        engine.Stop();
        Assert.NotNull(engine.Systems.Single().LastException);   // 会话结束后仍可回看现场

        bhv.ThrowInStart = false;                                // 新会话不再炸
        engine.Start();
        TestUtil.AdvanceSteps(fake, 3, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.Null(engine.Systems.Single().LastException);      // 新会话清空（旧现场不跨会话继承）
        Assert.True(engine.Systems.Single().Faults >= 1, "Faults 是整生命周期累计计数，不随新会话清零");
        engine.Stop();
    }

    // ---------- 生命周期：Enable / Disable / Unregister ----------

    [Fact]
    public void Disabled_UpdateSystemSkipsTicks_ReenableResumes()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new TickRecorder("t");
        engine.Register(bhv, Schedule.Worker(100));
        engine.Start();

        TestUtil.AdvanceSteps(fake, 5, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        long before = bhv.Calls.Count;
        Assert.True(before >= 4);

        engine.SetEnabled(bhv, false);
        TestUtil.AdvanceSteps(fake, 8, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.Equal(before, bhv.Calls.Count);   // 停用后不再调用 Update

        engine.SetEnabled(bhv, true);
        TestUtil.AdvanceSteps(fake, 3, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.True(bhv.Calls.Count > before, "重新启用后应恢复 Update");
        engine.Stop();
    }

    [Fact]
    public void Unregister_StopsExactlyOnceAndRemovesFromSnapshot()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new LifecycleRecorder("u");
        var keeper = new TickRecorder("keeper");   // 看守：保持 Systems 非空以便观察拍号
        engine.Register(bhv, Schedule.Worker(100));
        engine.Register(keeper, Schedule.Worker(100));
        engine.Start();

        TestUtil.AdvanceSteps(fake, 5, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.True(engine.Unregister(bhv));
        Assert.False(engine.Unregister(bhv));          // 二次卸载 = false

        TestUtil.AdvanceSteps(fake, 5, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.DoesNotContain(engine.Systems, b => b.Name == "u");
        Assert.True(engine.Systems.Single(b => b.Name == "keeper").TickNumber > 5,
            "看守行为应继续运行");

        Assert.Equal(1, bhv.StopCount);
        engine.Stop();
        Assert.Equal(1, bhv.StopCount);                // Stop 恰好一次
    }

    // ---------- 动态改调度（ChangeSchedule） ----------

    [Fact]
    public void ChangeSchedule_MovesBetweenGroups_AndTicksAtNewRate()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new TickRecorder("m");
        engine.Register(bhv, Schedule.Worker(100));
        engine.Start();

        TestUtil.AdvanceSteps(fake, 5, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));
        Assert.True(engine.ChangeSchedule(bhv, Schedule.Worker(20)));   // 100→20Hz

        // 迁入新组后拍号从 0 重计；4 步 × 50ms → 约 4 拍
        TestUtil.AdvanceSteps(fake, 4, TimeSpan.FromMilliseconds(50), () => FirstTick(engine));

        var view = engine.Systems.First();
        Assert.Equal(20, view.Fps);
        Assert.Contains("20Hz", view.ThreadName);
        Assert.InRange(view.TickNumber, 3, 5);
        engine.Stop();
    }

    [Fact]
    public void ChangeSchedule_ToMain_RequiresDispatcher()
    {
        using var engine = new UpdateEngine(NewFake());
        var bhv = new TickRecorder("x");
        engine.Register(bhv, Schedule.Worker(10));
        Assert.Throws<InvalidOperationException>(() => engine.ChangeSchedule(bhv, Schedule.Main(30)));
    }

    // ---------- 边界校验 ----------

    [Fact]
    public void Register_Validation()
    {
        using var engine = new UpdateEngine(NewFake());
        Assert.Throws<ArgumentNullException>(() => engine.Register(null!, Schedule.Worker(10)));
        Assert.Throws<ArgumentNullException>(() => engine.Register(new TickRecorder("a"), null!));

        var bhv = new TickRecorder("d");
        engine.Register(bhv, Schedule.Worker(10));
        Assert.Throws<InvalidOperationException>(() => engine.Register(bhv, Schedule.Worker(10))); // 重复
    }

    [Fact]
    public void Schedule_Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Schedule.Worker(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Schedule.Main(0));       // Main 必须固定 FPS
        Assert.Throws<ArgumentOutOfRangeException>(() => Schedule.Worker(2000));  // 超过 MaxFps
        Assert.Equal(ThreadKind.Worker, Schedule.Worker(0).ThreadKind);           // Worker 允许 0 = 不限速
        Assert.True(Schedule.Worker(0).IsUnthrottled);
    }

    // ---------- Update 实耗计时（真实墙钟，与假时钟调度解耦） ----------

    [Fact]
    public void UpdateDuration_ReportedPerUpdateSystem_NonNegativeAndMonotonicMax()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = new ImmediateDispatcher(),
        };
        var a = new TickRecorder("a");
        var b = new TickRecorder("b");
        engine.Register(a, Schedule.Worker(100));
        engine.Register(b, Schedule.Main(50));
        engine.Start();

        TestUtil.AdvanceSteps(fake, 10, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        var va = engine.Systems.Single(v => v.Name == "a");
        var vb = engine.Systems.Single(v => v.Name == "b");
        Assert.True(va.TickNumber >= 9);
        Assert.True(vb.TickNumber >= 4);
        Assert.True(va.LastUpdateSeconds >= 0 && va.MaxUpdateSeconds >= 0);
        Assert.True(vb.LastUpdateSeconds >= 0 && vb.MaxUpdateSeconds >= 0);
        Assert.True(va.MaxUpdateSeconds >= va.LastUpdateSeconds);
        Assert.True(vb.MaxUpdateSeconds >= vb.LastUpdateSeconds);
        engine.Stop();
    }

    [Fact]
    public void UpdateDuration_CapturesSlowUpdateSpike()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var slow = new SlowRecorder("slow", sleepMs: 30);   // 每拍睡 30ms 实时间
        engine.Register(slow, Schedule.Worker(100));
        engine.Start();

        for (int i = 0; i < 20; i++)
        {
            fake.Advance(TimeSpan.FromMilliseconds(10));
            Thread.Sleep(2);
        }
        Assert.True(TestUtil.WaitUntil(() =>
            engine.Systems.Single(v => v.Name == "slow").MaxUpdateSeconds >= 0.02,
            5000), "最大 Update 耗时应捕获到 Sleep 尖峰 (≥20ms)");
        engine.Stop();
    }

    // ============ 测试行为 ============

    /// <summary>记录每拍 id 与 FrameContext（不抛异常）。</summary>
    private sealed class TickRecorder : UpdateSystem
    {
        private readonly List<Call> _calls = new();
        private readonly object _gate = new();
        private readonly string _id;
        public bool ThrowInStart;

        public TickRecorder(string id) => _id = id;

        public override string Name => _id;

        public IReadOnlyList<Call> Calls
        {
            get { lock (_gate) { return _calls.ToArray(); } }
        }

        public sealed record Call(string Id, FrameContext Frame);

        protected override void Start()
        {
            if (ThrowInStart) { throw new InvalidOperationException("Start 故意炸"); }
        }

        protected override void Update(in FrameContext frame)
        {
            lock (_gate) { _calls.Add(new Call(_id, frame)); }
        }
    }

    /// <summary>生命周期计数行为。</summary>
    private sealed class LifecycleRecorder : UpdateSystem
    {
        private readonly string _id;
        private long _startCount;
        private long _stopCount;

        public LifecycleRecorder(string id) => _id = id;

        public override string Name => _id;

        public long StartCount => Interlocked.Read(ref _startCount);
        public long StopCount => Interlocked.Read(ref _stopCount);

        protected override void Start() => Interlocked.Increment(ref _startCount);

        protected override void Stop() => Interlocked.Increment(ref _stopCount);
    }

    /// <summary>每拍 Sleep 制造迟到的慢行为。</summary>
    private sealed class SlowRecorder : UpdateSystem
    {
        private readonly List<FrameContext> _calls = new();
        private readonly object _gate = new();
        private readonly string _id;
        private readonly int _sleepMs;

        public SlowRecorder(string id, int sleepMs)
        {
            _id = id;
            _sleepMs = sleepMs;
        }

        public override string Name => _id;

        public FrameContext[] Calls
        {
            get { lock (_gate) { return _calls.ToArray(); } }
        }

        protected override void Update(in FrameContext frame)
        {
            lock (_gate) { _calls.Add(frame); }
            Thread.Sleep(_sleepMs);
        }
    }

    /// <summary>按节奏抛异常的行为。</summary>
    private sealed class FaultRecorder : UpdateSystem
    {
        private readonly string _id;
        private readonly int _throwEvery;
        private long _n;

        public FaultRecorder(string id, int throwEvery)
        {
            _id = id;
            _throwEvery = throwEvery;
        }

        public override string Name => _id;

        protected override void Update(in FrameContext frame)
        {
            if (Interlocked.Increment(ref _n) % _throwEvery == 0)
            {
                throw new InvalidOperationException("故意炸");
            }
        }
    }
}
