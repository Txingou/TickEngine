namespace TickEngine;

/// <summary>引擎公开的系统运行时快照（演示表/监控轮询用，取自某个瞬间，不做一致性保证）。</summary>
public readonly record struct SystemView
{
    /// <summary>系统实例（用于启停/卸载等操作）。</summary>
    public required UpdateSystem UpdateSystem { get; init; }

    /// <summary>系统显示名。</summary>
    public required string Name { get; init; }

    /// <summary>归组组名（默认 "Default"）。同组名同频共享循环；异组名可独立共存。</summary>
    public required string GroupName { get; init; }

    /// <summary>线程亲和。</summary>
    public required ThreadKind ThreadKind { get; init; }

    /// <summary>目标 FPS。</summary>
    public required int Fps { get; init; }

    /// <summary>当前是否启用。</summary>
    public required bool Enabled { get; init; }

    /// <summary>本循环已执行帧数。</summary>
    public required long TickNumber { get; init; }

    /// <summary>本循环累计丢拍。</summary>
    public required long Dropped { get; init; }

    /// <summary>本系统累计异常次数。</summary>
    public required long Faults { get; init; }

    /// <summary>本循环实测 FPS（EMA 平滑）。</summary>
    public required double ActualFps { get; init; }

    /// <summary>本循环最近一帧实测间隔（秒）。</summary>
    public required double LastDeltaSeconds { get; init; }

    /// <summary>执行线程显示名。</summary>
    public required string ThreadName { get; init; }

    /// <summary>循环启动以来的流逝秒数。</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>
    /// 最近一拍 Update 的实际耗时（秒，真实墙钟 Stopwatch 实测，与调度时间轴解耦）。
    /// 数值 0 = 该系统尚未执行过 Update。
    /// </summary>
    public required double LastUpdateSeconds { get; init; }

    /// <summary>
    /// 本会话内单拍 Update 耗时的最大值（秒，真实墙钟）。0 = 尚未执行过 Update。
    /// 尖峰卡顿（如系统内 Sleep）靠它一锤定音。
    /// </summary>
    public required double MaxUpdateSeconds { get; init; }

    /// <summary>
    /// 最近一次异常的异常对象（溯源用：可直接读 Message / StackTrace / 类型；null = 本会话尚未发生异常）。
    /// 语义：新会话（引擎 Start 时）清空；系统恢复正常后<b>保留</b>现场，直到下一次异常覆盖——
    /// 与累计计数 <see cref="Faults"/> 互补（计数回答"炸过几次"，本字段回答"最近炸的是什么、炸在哪"）。
    /// 注意：异常对象的 StackTrace 首次访问才物化，物化后会钉住栈帧；本字段只保留"最近一次"，代价可忽略。
    /// </summary>
    public Exception? LastException { get; init; }
}
