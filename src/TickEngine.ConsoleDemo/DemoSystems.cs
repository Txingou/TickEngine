using System.Text;
using TickEngine;

namespace TickEngine.ConsoleDemo;

/// <summary>控制台演示共享世界：Compute worker 写、Console UI 线程读（volatile 发布标量/整块快照）。</summary>
public sealed class DemoWorld
{
    /// <summary>计算迭代计数（Compute worker 每拍 +1）。</summary>
    public long ComputeIterations { get => Volatile.Read(ref _iter); set => Volatile.Write(ref _iter, value); }
    private long _iter;

    /// <summary>仿真“信号输出”标量（每拍按正弦演化，让文本屏有可见变化）。</summary>
    public double Signal { get => Volatile.Read(ref _signal); set => Volatile.Write(ref _signal, value); }
    private double _signal;

    /// <summary>心跳拍数（Heartbeat worker 每拍 +1）。</summary>
    public long Heartbeats { get => Volatile.Read(ref _beats); set => Volatile.Write(ref _beats, value); }
    private long _beats;
}

/// <summary>
/// 后台计算系统（Worker 亲和，演示“各自独立速率后台线程”）：
/// 每拍跑一段真实 CPU 负载（正弦积分），按 Schedule 频率节拍；频率可运行中经引擎改速。
/// 注意与 Main 组隔离：本组算得再慢也只丢自己的拍，不会挤压文本屏。
/// </summary>
public sealed class ComputeLoadSystem : UpdateSystem
{
    private readonly DemoWorld _world;
    private double _phase;

    public ComputeLoadSystem(DemoWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public override string Name => "ComputeLoad";

    protected override void Update(in FrameContext frame)
    {
        // 真实负载：积一段正弦，制造可观测的 Update 耗时（引擎 Stopwatch 实测）
        double acc = 0;
        const int samples = 24_000;
        for (int i = 0; i < samples; i++)
        {
            acc += Math.Sin(_phase + i * 0.001);
        }
        _phase += frame.DeltaTime * 6.0;

        _world.ComputeIterations++;
        _world.Signal = acc / samples;
    }
}

/// <summary>心跳系统：独立 Worker 组（与 Compute 不同频），文本屏据此显示两条后台循环并行。</summary>
public sealed class HeartbeatSystem : UpdateSystem
{
    private readonly DemoWorld _world;

    public HeartbeatSystem(DemoWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public override string Name => "Heartbeat";

    protected override void Update(in FrameContext frame)
    {
        _world.Heartbeats++;
    }
}

/// <summary>
/// 异常演示系统（Worker 2Hz，独立组）：按节奏抛出真实异常，用来演示
/// 「异常隔离 + SystemView.LastException（探针表格异常消息列）+ 探针日志 Error + 双击跳转源码」。
/// 引擎照常继续运行（Faults 累计、LastException 保留最近现场）。
/// 冒烟模式首拍必抛（确定性），交互模式每 ~3s 抛一次（不刷屏）。
/// </summary>
public sealed class FaultDemoSystem : UpdateSystem
{
    private readonly bool _throwOnFirstTick;
    private long _ticks;

    public FaultDemoSystem(bool throwOnFirstTick)
    {
        _throwOnFirstTick = throwOnFirstTick;
    }

    public override string Name => "FaultDemo";

    protected override void Update(in FrameContext frame)
    {
        long n = Interlocked.Increment(ref _ticks);
        bool shouldThrow = (_throwOnFirstTick && n == 1) || n % 6 == 0;
        if (!shouldThrow) { return; }

        throw new InvalidOperationException(
            $"演示异常：FaultDemo 第 {n} 拍故意抛出（异常隔离演示；双击本行「异常消息」可跳到这行源码）");
    }
}

/// <summary>
/// 文本屏系统（Main 亲和，经 ConsoleFrameDispatcher 在控制台主线程执行）：
/// 顶部整块重绘：引擎纪元 + 每系统一行迷你统计（源自引擎 Systems 快照，即控制台版探针表）
/// + Compute 世界读数 + 按键帮助。重绘频率 = 注册时的 Main FPS。
/// 输出被重定向（自动化/管道）时退回低频整行打印，避免光标 API 不可用。
/// <para>
/// 渲染与日志分道（Q4=A）：本系统写入的是<b>探针安装 tee 之前的原始 stdout</b>——
/// 屏幕重绘不是日志，不能把 15Hz 的整块重绘灌进探针日志面板（会淹没真正的日志）。
/// </para>
/// </summary>
public sealed class ConsoleScreenSystem : UpdateSystem
{
    private readonly Func<UpdateEngine> _engineGetter;
    private readonly DemoWorld _world;
    private readonly TextWriter _rawOut;
    private long _renderedFrames;
    private readonly bool _redirected;

    public ConsoleScreenSystem(Func<UpdateEngine> engineGetter, DemoWorld world, TextWriter rawOut)
    {
        _engineGetter = engineGetter ?? throw new ArgumentNullException(nameof(engineGetter));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _rawOut = rawOut ?? throw new ArgumentNullException(nameof(rawOut));
        _redirected = Console.IsOutputRedirected;
    }

    public override string Name => "ConsoleScreen";

    protected override void Update(in FrameContext frame)
    {
        long n = Interlocked.Increment(ref _renderedFrames);
        if (_redirected)
        {
            // 重定向输出：每 ~1.25s 落一行整段摘要（避免刷屏；光标不可用）
            if (n % 25 != 1) { return; }
            _rawOut.WriteLine(BuildScreen().Replace('\n', '|'));
            _rawOut.Flush();
            return;
        }
        DrawBlock(BuildScreen());
    }

    /// <summary>组屏：纪元 + 迷你系统表 + Compute 世界 + 帮助（文本屏内容唯一出处）。</summary>
    private string BuildScreen()
    {
        var engine = _engineGetter();
        var sb = new StringBuilder();
        sb.AppendLine($"TickEngine 控制台演示 — 纪元 {engine.GlobalSeconds,6:F1}s (global tick {engine.GlobalTick})");
        sb.AppendLine(new string('-', 78));

        foreach (var v in engine.Systems)
        {
            string kind = v.ThreadKind == ThreadKind.Main ? "Main" : "Wrk ";
            string group = v.GroupName;
            sb.AppendLine(
                $"  {v.Name,-14} {group,-10} {kind} {v.Fps,4}Hz | 实测{v.ActualFps,6:F1} | 拍{v.TickNumber,8} | 丢{v.Dropped,4} | 错{v.Faults,2} | upd{(v.LastUpdateSeconds * 1000),6:F2}ms");
        }

        sb.AppendLine(new string('-', 78));
        sb.AppendLine($"  迭代 {_world.ComputeIterations,9} | 信号 {_world.Signal,7:F4} | 心跳 {_world.Heartbeats,6}");
        sb.AppendLine("  [P] 探针窗口  [+/-] 计算Hz(10-400)  [Q/Esc] 退出");
        return sb.ToString();
    }

    /// <summary>光标固定到 (0,0) 重绘整块（行数固定 → 无需清屏；末尾留白覆盖旧残影）。</summary>
    private void DrawBlock(string block)
    {
        try
        {
            Console.SetCursorPosition(0, 0);
            _rawOut.Write(block);           // 原始 stdout：控制台照常显示，但不进探针日志
            for (int i = 0; i < 2; i++) { _rawOut.Write(new string(' ', 78)); }
            _rawOut.Flush();
        }
        catch (IOException)
        {
            // 极少数宿主输出句柄异常：直接整行打，避免演示崩
            _rawOut.WriteLine(block.Replace('\n', '|'));
            _rawOut.Flush();
        }
    }
}
