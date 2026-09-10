using Microsoft.Extensions.Time.Testing;

namespace TickEngine.Tests;

/// <summary>组名归组测试：默认组、命名组并存、同频异组独立、归一化与校验、跨组迁移。</summary>
public sealed class GroupNameTests
{
    private static FakeTimeProvider NewFake() =>
        new(DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcDateTime);

    private static long TickOf(UpdateEngine e, string name) =>
        e.Systems.Single(v => v.Name == name).TickNumber;

    private static long DroppedOf(UpdateEngine e, string name) =>
        e.Systems.Single(v => v.Name == name).Dropped;

    // ---------- 默认组语义 ----------

    [Fact]
    public void Schedule_DefaultGroupName_WhenNullOrBlank()
    {
        Assert.Equal(Schedule.DefaultGroupName, Schedule.Worker(10).GroupName);
        Assert.Equal(Schedule.DefaultGroupName, Schedule.Worker(10, null).GroupName);
        Assert.Equal(Schedule.DefaultGroupName, Schedule.Worker(10, "  ").GroupName);   // 空白 → Default
        Assert.Equal(Schedule.DefaultGroupName, Schedule.Worker(10, "").GroupName);
        Assert.Equal("Servo", Schedule.Worker(10, "  Servo  ").GroupName);              // Trim
        Assert.Equal(Schedule.DefaultGroupName, Schedule.Main(30).GroupName);
    }

    [Fact]
    public void Schedule_GroupNameValidation()
    {
        Assert.Throws<ArgumentException>(() => Schedule.Worker(10, new string('x', 33)));  // 超长
        // 32 字符边界合法
        Assert.Equal(new string('x', 32), Schedule.Worker(10, new string('x', 32)).GroupName);
    }

    [Fact]
    public void Schedule_GroupName_CaseSensitive()
    {
        var a = Schedule.Worker(10, "Servo");
        var b = Schedule.Worker(10, "servo");
        Assert.NotEqual(a, b);                       // 大小写不同 = 不同组
        Assert.NotEqual(a.GroupName, b.GroupName);
    }

    [Fact]
    public void DefaultGroup_SameRate_MergesSharesCtx()
    {
        // 默认组同频 → 共享一条循环（复用共享循环语义：同帧 ctx 一致）
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var x = new Recorder("x");
        var y = new Recorder("y");
        engine.Register(x, Schedule.Worker(50));
        engine.Register(y, Schedule.Worker(50, null));   // 归一化后仍是 Default

        engine.Start();
        TestUtil.AdvanceSteps(fake, 6, TimeSpan.FromMilliseconds(20), () => TickOf(engine, "x"));

        var xv = engine.Systems.Single(v => v.Name == "x");
        var yv = engine.Systems.Single(v => v.Name == "y");
        Assert.Equal(Schedule.DefaultGroupName, xv.GroupName);
        Assert.Equal(Schedule.DefaultGroupName, yv.GroupName);
        Assert.Equal(xv.ThreadName, yv.ThreadName);      // 同一条线程
        engine.Stop();
    }

    // ---------- 命名组并存：同频异组 = 独立线程 / 独立丢拍 ----------

    [Fact]
    public void NamedGroups_SameRate_AreIndependent_IsolationVisibleViaDrops()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var fast = new Recorder("fast");
        var stutter = new SleepyRecorder("stutter", sleepMs: 20);   // 每拍睡 20ms → 必然丢拍
        engine.Register(fast, Schedule.Worker(100, "Fast"));         // 组 Fast
        engine.Register(stutter, Schedule.Worker(100, "Slow"));      // 组 Slow：同 100Hz 但异组
        engine.Start();

        // 先确认两组都各自跑起来（每拍等消费：慢组每拍睡 20ms 实时间也能推进）
        TestUtil.AdvanceSteps(fake, 4, TimeSpan.FromMilliseconds(10), () => TickOf(engine, "stutter"));
        Assert.True(TickOf(engine, "fast") >= 1, "Fast 组应已运行");
        Assert.True(TickOf(engine, "stutter") >= 1, "Slow 组应已运行");

        // 突进假时钟：慢组线程每拍实睡 → 大 dt 间隙 → 丢拍上涨；Fast 组独立线程不受影响
        for (int i = 0; i < 80; i++)
        {
            fake.Advance(TimeSpan.FromMilliseconds(10));
            Thread.Sleep(1);
        }
        Assert.True(TestUtil.WaitUntil(() => DroppedOf(engine, "stutter") > 0, 5000),
            "Slow 组应观察到丢拍");
        Assert.Equal(0, DroppedOf(engine, "fast"));     // Fast 组完全不受 Slow 组拖累 → 线程隔离

        var fv = engine.Systems.Single(v => v.Name == "fast");
        var sv = engine.Systems.Single(v => v.Name == "stutter");
        Assert.Equal("Fast", fv.GroupName);
        Assert.Equal("Slow", sv.GroupName);
        Assert.NotEqual(fv.ThreadName, sv.ThreadName);   // 两个不同线程
        engine.Stop();
    }

    [Fact]
    public void NamedGroups_SameRate_TwoGroupNamesMeansTwoThreads()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var a = new Recorder("a");
        var b = new Recorder("b");
        engine.Register(a, Schedule.Worker(100, "G1"));
        engine.Register(b, Schedule.Worker(100, "G2"));   // 同 100Hz 异组

        engine.Start();
        TestUtil.AdvanceSteps(fake, 5, TimeSpan.FromMilliseconds(10), () => TickOf(engine, "a"));

        var av = engine.Systems.Single(v => v.Name == "a");
        var bv = engine.Systems.Single(v => v.Name == "b");
        Assert.Equal(100, av.Fps);
        Assert.Equal(100, bv.Fps);
        Assert.NotEqual(av.ThreadName, bv.ThreadName);   // 各占一条循环线程
        engine.Stop();
    }

    [Fact]
    public void MainGroup_NamedGroups_Supported()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = new ImmediateDispatcher(),
        };
        var a = new Recorder("a");
        var b = new Recorder("b");
        engine.Register(a, Schedule.Main(50, "UiA"));
        engine.Register(b, Schedule.Main(50, "UiB"));     // Main 同频异组
        engine.Start();

        TestUtil.AdvanceSteps(fake, 6, TimeSpan.FromMilliseconds(20), () => TickOf(engine, "a"));

        var av = engine.Systems.Single(v => v.Name == "a");
        var bv = engine.Systems.Single(v => v.Name == "b");
        Assert.Equal(ThreadKind.Main, av.ThreadKind);
        Assert.Equal("UiA", av.GroupName);
        Assert.Equal("UiB", bv.GroupName);
        Assert.True(av.TickNumber >= 3);
        Assert.True(bv.TickNumber >= 3);                  // 两组各跑各的
        engine.Stop();
    }

    // ---------- 跨组迁移（ChangeSchedule 换组名） ----------

    [Fact]
    public void ChangeSchedule_ToAnotherGroup_MovesMember()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake);
        var bhv = new Recorder("m");
        engine.Register(bhv, Schedule.Worker(50, "A"));
        engine.Start();

        TestUtil.AdvanceSteps(fake, 4, TimeSpan.FromMilliseconds(20), () => TickOf(engine, "m"));
        Assert.True(engine.ChangeSchedule(bhv, Schedule.Worker(50, "B")));   // 同频迁移到组 B

        // 迁入新组后 tick 从 0 重计：4 步 × 20ms ≈ 4 拍
        TestUtil.AdvanceSteps(fake, 4, TimeSpan.FromMilliseconds(20), () => TickOf(engine, "m"));
        var v = engine.Systems.Single(x => x.Name == "m");
        Assert.Equal("B", v.GroupName);
        Assert.Contains("B", v.ThreadName);
        Assert.InRange(v.TickNumber, 3, 5);
        engine.Stop();
    }

    [Fact]
    public void ChangeSchedule_SameGroupSameRate_NoOp()
    {
        using var engine = new UpdateEngine(NewFake());
        var bhv = new Recorder("m");
        engine.Register(bhv, Schedule.Worker(50, "A"));
        // 同组同频（含组名相同）→ true 且不迁移
        Assert.True(engine.ChangeSchedule(bhv, Schedule.Worker(50, "A")));
        engine.Dispose();
    }

    // ---------- 测试行为 ----------

    private sealed class Recorder : UpdateSystem
    {
        private readonly string _id;
        public Recorder(string id) => _id = id;
        public override string Name => _id;

        protected override void Update(in FrameContext frame) { }
    }

    private sealed class SleepyRecorder : UpdateSystem
    {
        private readonly string _id;
        private readonly int _sleepMs;
        private long _n;

        public SleepyRecorder(string id, int sleepMs)
        {
            _id = id;
            _sleepMs = sleepMs;
        }

        public override string Name => _id;

        protected override void Update(in FrameContext frame)
        {
            if (Interlocked.Increment(ref _n) % 2 == 0) { Thread.Sleep(_sleepMs); }
        }
    }
}
