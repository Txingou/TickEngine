using TickEngine;

namespace TickEngine.Probe;

/// <summary>
/// 探针表格行 VM（框架无关，供 Avalonia DataGrid 绑定）：把 SystemView 原始值格式化为显示文本。
/// </summary>
public sealed class SystemViewRow
{
    public required string Name { get; init; }
    public required string GroupName { get; init; }
    public required string ThreadKind { get; init; }
    public required string Fps { get; init; }
    public required string ActualFpsText { get; init; }
    public required string TickNumber { get; init; }
    public required string Dropped { get; init; }
    public required string Faults { get; init; }
    public required string FrameIntervalText { get; init; }
    public required string UpdateLastText { get; init; }
    public required string UpdateMaxText { get; init; }
    public required string EnabledText { get; init; }

    /// <summary>最近一次异常的 Message（无异常为空串；表格「异常消息」列绑定）。</summary>
    public required string ExceptionMessage { get; init; }

    /// <summary>最近一次异常对象（不绑定列；双击跳转/复制堆栈用）。</summary>
    public Exception? Exception { get; init; }

    /// <summary>本行是否有异常（着色判断用）。</summary>
    public bool HasException => Exception != null;

    public static SystemViewRow From(SystemView v) => new()
    {
        Name = v.Name,
        GroupName = v.GroupName,
        ThreadKind = v.ThreadKind == TickEngine.ThreadKind.Main ? "Main(UI)" : "Worker",
        Fps = v.Fps.ToString(),
        ActualFpsText = v.ActualFps > 0 ? v.ActualFps.ToString("F1") : "-",
        TickNumber = v.TickNumber.ToString(),
        Dropped = v.Dropped.ToString(),
        Faults = v.Faults.ToString(),
        FrameIntervalText = v.LastDeltaSeconds > 0 ? (v.LastDeltaSeconds * 1000).ToString("F2") : "-",
        UpdateLastText = v.LastUpdateSeconds > 0 ? (v.LastUpdateSeconds * 1000).ToString("F3") : "-",
        UpdateMaxText = v.MaxUpdateSeconds > 0 ? (v.MaxUpdateSeconds * 1000).ToString("F3") : "-",
        EnabledText = v.Enabled ? "是" : "否",
        ExceptionMessage = FormatExceptionMessage(v.LastException),
        Exception = v.LastException,
    };

    /// <summary>异常消息显示文本：单行化（多行消息压成一行 + 首个源码位置，便于表格阅读）。</summary>
    private static string FormatExceptionMessage(Exception? ex)
    {
        if (ex is null) { return string.Empty; }
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        string? where = ProbeCodeJump.DescribeLocation(ex);
        return where is null ? message : $"{message} @ {where}";
    }
}
