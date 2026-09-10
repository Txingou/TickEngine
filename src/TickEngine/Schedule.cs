namespace TickEngine;

/// <summary>
/// 系统的调度档案：组名 + 线程亲和 + 目标 FPS。
/// 引擎按 (GroupName, ThreadKind, Fps) 归组：同组共享一条循环节拍流（注册序内逐帧顺序调用）；
/// 不同组名可让多条同频 Worker 循环独立共存（独立线程、独立丢拍统计）。
/// </summary>
public sealed record Schedule
{
    /// <summary>FPS 上限（PeriodicTimer 单周期下限 1ms）。</summary>
    public const int MaxFps = 1000;

    /// <summary>默认组名：未显式指定组名时归入该组。</summary>
    public const string DefaultGroupName = "Default";

    /// <summary>组名长度上限（线程名/演示表可读性）。</summary>
    public const int MaxGroupNameLength = 32;

    /// <summary>归组组名（已 Trim；空/空白归一化为 <see cref="DefaultGroupName"/>；大小写敏感）。</summary>
    public string GroupName { get; }

    /// <summary>线程亲和：Worker = 引擎自建后台线程；Main = 宿主投递到主/UI 线程。</summary>
    public ThreadKind ThreadKind { get; }

    /// <summary>
    /// 目标 FPS。Worker 允许 0 = 不限速跑满（慎用：忙循环）；Main 必须 ≥ 1（固定节拍）。
    /// </summary>
    public int Fps { get; }

    /// <summary>是否不限速（Fps == 0，仅 Worker 合法）。</summary>
    public bool IsUnthrottled => Fps == 0;

    /// <summary>单周期时长（秒）；不限速时为 0。</summary>
    internal double PeriodSeconds => Fps > 0 ? 1.0 / Fps : 0.0;

    /// <summary>构造调度档案。</summary>
    /// <param name="threadKind">线程亲和。</param>
    /// <param name="fps">目标 FPS：Worker ≥ 0，Main ≥ 1，且 ≤ <see cref="MaxFps"/>。</param>
    /// <param name="groupName">归组组名（默认 <see cref="DefaultGroupName"/>）。</param>
    /// <exception cref="ArgumentOutOfRangeException">fps 越界，或 Main 亲和但 fps == 0。</exception>
    /// <exception cref="ArgumentException">组名超过 <see cref="MaxGroupNameLength"/>（空/空白会归一化为 <see cref="DefaultGroupName"/>，不抛）。</exception>
    public Schedule(ThreadKind threadKind, int fps, string? groupName = null)
    {
        if (fps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "FPS 不能为负");
        }
        if (fps > MaxFps)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), $"FPS 超过上限 {MaxFps}（周期低于 1ms）");
        }
        if (threadKind == ThreadKind.Main && fps == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "Main 亲和要求固定 FPS（≥ 1）：主线程没有不限速语义");
        }
        ThreadKind = threadKind;
        Fps = fps;
        GroupName = NormalizeGroupName(groupName);
    }

    /// <summary>Worker 后台线程调度档案。</summary>
    public static Schedule Worker(int fps, string? groupName = null)
        => new(ThreadKind.Worker, fps, groupName);

    /// <summary>主/UI 线程固定节拍调度档案。</summary>
    public static Schedule Main(int fps, string? groupName = null)
        => new(ThreadKind.Main, fps, groupName);

    /// <summary>组名归一化：Trim；空/空白 → Default；校验长度。</summary>
    internal static string NormalizeGroupName(string? groupName)
    {
        string name = (groupName ?? string.Empty).Trim();
        if (name.Length == 0) { return DefaultGroupName; }
        if (name.Length > MaxGroupNameLength)
        {
            throw new ArgumentException($"组名超过上限 {MaxGroupNameLength} 字符", nameof(groupName));
        }
        return name;
    }

    /// <inheritdoc />
    public override string ToString() =>
        GroupName == DefaultGroupName
            ? $"{ThreadKind}@{Fps}fps"
            : $"{GroupName}/{ThreadKind}@{Fps}fps";
}
