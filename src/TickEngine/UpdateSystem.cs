namespace TickEngine;

/// <summary>
/// 系统基类（命名致敬 Unity ECS 的 System，但保持"有状态单实例 + 注册即调度"语义：
/// 不引入 Entity/Component 数据与查询——每拍回调体自身持有状态）。
/// 生命周期：Start() 在所属循环的首拍执行前调用一次（该循环的执行线程上）；
/// Update() 每拍调用；Stop() 在引擎停止或系统被卸载时调用一次。
/// 调度档案（组名 + 线程亲和 + FPS）在注册时经 <see cref="UpdateEngine.Register"/> 给定，不由系统自身声明。
/// 派生类跨程序集覆盖时用 protected override（Start/Update/Stop）。
/// </summary>
public abstract class UpdateSystem
{
    private volatile bool _enabled = true;

    /// <summary>显示名（演示表/日志用）。</summary>
    public virtual string Name => GetType().Name;

    /// <summary>
    /// 启停开关：置 false 后，所属循环<b>从下一个成员起</b>跳过本系统（同一拍内已经跑过的成员不受影响；
    /// 执行中的帧不会被撕裂）。任意线程可安全读写。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>所属引擎（注册后可用；未注册为 null）。</summary>
    public UpdateEngine? Engine { get; internal set; }

    /// <summary>调度档案（注册后可用；未注册为 null）。</summary>
    public Schedule? Schedule { get; internal set; }

    /// <summary>启动回调：在所属循环执行线程上、会话内首拍 Update 前调用一次。</summary>
    protected virtual void Start() { }

    /// <summary>每拍更新回调。</summary>
    /// <param name="frame">本拍帧上下文（同循环同拍共享同一语义）。</param>
    protected virtual void Update(in FrameContext frame) { }

    /// <summary>停止回调：引擎停止或本系统被卸载时调用一次。</summary>
    protected virtual void Stop() { }

    // ---- 引擎桥接（internal：仅 TickEngine 程序集可调用；不在公共 API 表面）----

    internal void InvokeStart() => Start();

    internal void InvokeUpdate(in FrameContext frame) => Update(frame);

    internal void InvokeStop() => Stop();
}
