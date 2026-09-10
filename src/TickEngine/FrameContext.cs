namespace TickEngine;

/// <summary>
/// 一拍的帧上下文：同一 (组名, 亲和, FPS) 共享循环内、同一拍的所有系统拿到同一个上下文。
/// DeltaTime 为实测间隔（墙钟），丢拍/迟到的那一拍 DeltaTime 会明显变大。
/// </summary>
public readonly struct FrameContext
{
    /// <summary>实测间隔（秒）：本拍实际执行时刻与上一拍实际执行时刻之差。</summary>
    public double DeltaTime { get; }

    /// <summary>拍号：本循环已执行帧计数（丢拍不计入）。</summary>
    public long TickNumber { get; }

    /// <summary>累计丢拍数：本循环自启动以来因迟到/忙而跳过的拍数。</summary>
    public long Dropped { get; }

    /// <summary>循环启动以来的流逝时间（秒，TimeProvider 单调时钟）。</summary>
    public double ElapsedSeconds { get; }

    /// <summary>执行本帧的线程名（Main 亲和时为宿主调度器名）。</summary>
    public string ThreadName { get; }

    /// <summary>
    /// 引擎纪元拍号（全局时间对齐轴，所有循环共享）：
    /// 自引擎 Start 起的单调刻度，固定 1ms 一拍（= 1000 刻度/秒），由引擎纪元时钟现算，
    /// 不依赖任何特定循环是否执行/丢拍——任何一拍读到的都是同一时间轴上的时刻。
    /// 不同 FPS 组之间换算：两拍 GlobalTick 之差 ÷1000 = 真实流逝秒。
    /// </summary>
    public long GlobalTick { get; }

    /// <summary>引擎纪元单调秒：自引擎 Start 起的流逝时间，所有循环共享同一时间轴。</summary>
    public double GlobalSeconds { get; }

    /// <summary>构造帧上下文（引擎内部使用）。</summary>
    internal FrameContext(double deltaTime, long tickNumber, long dropped,
                          double elapsedSeconds, string threadName,
                          long globalTick, double globalSeconds)
    {
        DeltaTime = deltaTime;
        TickNumber = tickNumber;
        Dropped = dropped;
        ElapsedSeconds = elapsedSeconds;
        ThreadName = threadName;
        GlobalTick = globalTick;
        GlobalSeconds = globalSeconds;
    }

    /// <inheritdoc />
    /// <remarks>用 InvariantCulture 格式化：日志/CSV 解析不受宿主区域设置（逗号小数点）影响。</remarks>
    public override string ToString() =>
        System.FormattableString.Invariant(
            $"g={GlobalTick} tick={TickNumber} dt={DeltaTime * 1000:F1}ms dropped={Dropped} @{ThreadName}");
}
