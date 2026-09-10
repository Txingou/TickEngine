using TickEngine;

namespace TickEngine.ConsoleDemo;

/// <summary>
/// 控制台版主线程调度器：把引擎 Main 组的整帧投递到"控制台 UI 线程"执行。
/// 控制台没有系统消息泵，演示把进程主线程当泵：<see cref="DrainPosted"/> 由主循环调用，
/// 在轮询按键的间隙执行已投递的帧——语义与 WinForms BeginInvoke 队列一致（投递成功与否、
/// 在途上限与丢拍判断全部在引擎侧，本类只负责"搬到目标线程"）。
/// </summary>
public sealed class ConsoleFrameDispatcher : IFrameDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private volatile bool _closed;

    public string ThreadDisplayName => "UI(Console)";

    /// <summary>投递一帧到控制台主循环（线程安全；关闭后拒收 → 引擎计丢拍）。</summary>
    public bool TryPost(Action frame)
    {
        lock (_gate)
        {
            if (_closed) { return false; }
            _pending.Enqueue(frame);
            return true;
        }
    }

    /// <summary>主循环每轮调用：执行所有已投递帧（在控制台主线程上串行）。</summary>
    public void DrainPosted()
    {
        while (true)
        {
            Action? frame;
            lock (_gate)
            {
                if (_pending.Count == 0) { return; }
                frame = _pending.Dequeue();
            }
            frame();
        }
    }

    /// <summary>关闭：停止接收新投递（引擎停止后调用；残留帧被丢弃）。</summary>
    public void Close() => _closed = true;
}
