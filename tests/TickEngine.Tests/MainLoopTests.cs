using Microsoft.Extensions.Time.Testing;

namespace TickEngine.Tests;

/// <summary>Main（主/UI 线程）组测试：经 IFrameDispatcher 投递、忙则丢拍计数。</summary>
public sealed class MainLoopTests
{
    private static FakeTimeProvider NewFake() =>
        new(DateTimeOffset.Parse("2026-01-01T00:00:00Z").UtcDateTime);

    private static long FirstTick(UpdateEngine e) => e.Systems.First().TickNumber;

    [Fact]
    public void Register_MainUpdateSystem_RequiresDispatcher()
    {
        using var engine = new UpdateEngine(NewFake());
        Assert.Throws<InvalidOperationException>(
            () => engine.Register(new TickRecorder("m"), Schedule.Main(30)));
    }

    [Fact]
    public void MainGroup_ExecutesOnDispatcherThread_AtConfiguredFps()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = new ImmediateDispatcher(),
        };
        var bhv = new ThreadIdRecorder("ui");
        engine.Register(bhv, Schedule.Main(50));   // 20ms/拍

        engine.Start();
        TestUtil.AdvanceSteps(fake, 10, TimeSpan.FromMilliseconds(20), () => FirstTick(engine));

        var view = engine.Systems.First();
        Assert.Equal(ThreadKind.Main, view.ThreadKind);
        Assert.Equal(0, view.Dropped);
        Assert.InRange(view.TickNumber, 9, 11);
        Assert.Equal("TestMain", view.ThreadName);

        var calls = bhv.Calls;
        Assert.NotEmpty(calls);
        foreach (var c in calls)
        {
            Assert.Equal("TestMain", c.ThreadName);            // FrameContext 线程名 = dispatcher 名
            Assert.InRange(c.DeltaTime, 0.018, 0.022);         // 实测 dt
            Assert.True(c.TickNumber >= 1);
        }
        engine.Stop();
    }

    [Fact]
    public void MainGroup_DispatcherBusy_CountsDrops_AndSelfHealsAfterFree()
    {
        var fake = NewFake();
        var dispatcher = new BusyDispatcher { Busy = true };
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = dispatcher,
        };
        var bhv = new TickRecorder("ui");
        engine.Register(bhv, Schedule.Main(50));
        engine.Start();

        // 忙 8 拍：全部拒投 → 丢拍 8、零执行
        for (int i = 0; i < 8; i++)
        {
            fake.Advance(TimeSpan.FromMilliseconds(20));
            Thread.Sleep(2);
        }
        var busyView = engine.Systems.First();
        Assert.Equal(0, busyView.TickNumber);
        Assert.True(busyView.Dropped >= 7, $"忙期应大量丢拍, got {busyView.Dropped}");

        // 释放：下一拍恢复执行，丢拍不再增长
        dispatcher.Busy = false;
        TestUtil.AdvanceSteps(fake, 6, TimeSpan.FromMilliseconds(20), () => FirstTick(engine));
        var freeView = engine.Systems.First();
        Assert.True(freeView.TickNumber >= 4, $"释放后应恢复执行, got {freeView.TickNumber}");
        Assert.True(freeView.Dropped >= busyView.Dropped);

        engine.Stop();
    }

    [Fact]
    public void Systems_Snapshot_ReportsLifecycleAndLoopStats()
    {
        var fake = NewFake();
        using var engine = new UpdateEngine(fake)
        {
            Dispatcher = new ImmediateDispatcher(),
        };
        var a = new TickRecorder("A");
        var b = new TickRecorder("B");
        engine.Register(a, Schedule.Worker(100));
        engine.Register(b, Schedule.Main(30));
        engine.Start();

        // Systems 按注册序排列
        Assert.Equal(new[] { "A", "B" }, engine.Systems.Select(v => v.Name).ToArray());

        TestUtil.AdvanceSteps(fake, 6, TimeSpan.FromMilliseconds(10), () => FirstTick(engine));

        var views = engine.Systems;
        var va = views.Single(v => v.Name == "A");
        var vb = views.Single(v => v.Name == "B");
        Assert.Equal(100, va.Fps);
        Assert.Equal(30, vb.Fps);
        Assert.Equal(ThreadKind.Worker, va.ThreadKind);
        Assert.Equal(ThreadKind.Main, vb.ThreadKind);
        Assert.Equal(va.ElapsedSeconds, vb.ElapsedSeconds, precision: 6);
        Assert.True(va.TickNumber >= 5);
        Assert.True(va.ActualFps > 0, "实测 FPS EMA 应已建立");
        Assert.True(vb.ActualFps > 0);
        engine.Stop();
    }

    // ============ 测试行为 ============

    private sealed class TickRecorder : UpdateSystem
    {
        private readonly string _id;
        public TickRecorder(string id) => _id = id;
        public override string Name => _id;

        protected override void Update(in FrameContext frame) { }
    }

    private sealed class ThreadIdRecorder : UpdateSystem
    {
        private readonly List<FrameContext> _calls = new();
        private readonly string _id;
        public ThreadIdRecorder(string id) => _id = id;
        public override string Name => _id;

        public IReadOnlyList<FrameContext> Calls
        {
            get { lock (_calls) { return _calls.ToArray(); } }
        }

        protected override void Update(in FrameContext frame)
        {
            lock (_calls) { _calls.Add(frame); }
        }
    }
}
