namespace TickEngine;

/// <summary>
/// 主线程帧调度器抽象：引擎的 Main 亲和循环在后台节拍线程上等待节拍，
/// 然后把整帧执行投递给本调度器，由宿主搬到主/UI 线程真正执行。
/// 核心库不引用任何 UI 框架，UI 宿主实现本接口即可。
/// </summary>
/// <remarks>
/// 版本语义（v2，组级在途）：
/// 本接口只负责“把帧搬到目标线程执行”，【不做】忙检测、不做丢拍判断——
/// 在途上限（每 Main 组至多一帧排队）与丢拍计数由引擎侧的每个 Loop 自主管理，
/// 因此不同 Main 组的帧可同时在目标线程的队列中等待（UI 串行执行），
/// 低频组不再被共享忙标志饿死。
/// </remarks>
public interface IFrameDispatcher
{
    /// <summary>
    /// 请求在目标线程上执行一帧。
    /// 返回 true 表示已投递成功（frame 保证会被目标线程执行，至多延迟）；
    /// 返回 false 仅表示投递本身失败（如句柄已销毁/目标不可用）——
    /// 引擎据此计一次丢拍并释放本组在途槽，不会重试该帧。
    /// 线程安全：引擎可能从任意后台节拍线程并发调用；同一帧只会被调用一次。
    /// </summary>
    bool TryPost(Action frame);

    /// <summary>目标线程的显示名（演示表/FrameContext.ThreadName 用）。</summary>
    string ThreadDisplayName { get; }
}
