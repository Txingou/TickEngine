using TickEngine;

namespace TickEngine.Probe;

/// <summary>
/// 系统统计发布器（性能探测器核心，引擎无关）：
/// <b>Worker 亲和</b>系统（10Hz），每拍读 engine.Systems 并把快照发布给订阅者（通常是 UI 表格）。
/// 只做“读快照 → 回调”，不碰任何 UI 框架——订阅端负责在自己线程渲染（D9 教训：别在这里做重活）。
/// 用法：engine.Register(publisher, Schedule.Worker(10, "UiStats"))；
///       publisher.SnapshotPublished += views => ui.Invoke(...) 或由 UI 侧轮询 Latest。
/// </summary>
public sealed class UiStatsPublisher : UpdateSystem
{
    private readonly Func<UpdateEngine> _engineGetter;
    private IReadOnlyList<SystemView> _latest = Array.Empty<SystemView>();   // 单写者(执行线程)，Volatile 读写

    public UiStatsPublisher(Func<UpdateEngine> engineGetter)
    {
        _engineGetter = engineGetter ?? throw new ArgumentNullException(nameof(engineGetter));
    }

    public override string Name => "UiStats";

    /// <summary>最新系统快照（引用发布；任意线程可读）。</summary>
    public IReadOnlyList<SystemView> Latest => Volatile.Read(ref _latest);

    /// <summary>快照更新时触发（在发布器的执行线程上——Main 亲和 = UI 线程）。</summary>
    public event Action<IReadOnlyList<SystemView>>? SnapshotPublished;

    protected override void Update(in FrameContext frame)
    {
        var engine = _engineGetter();
        if (engine is null) { return; }

        var views = engine.Systems;   // 注册序快照
        Volatile.Write(ref _latest, views);
        SnapshotPublished?.Invoke(views);
    }
}
