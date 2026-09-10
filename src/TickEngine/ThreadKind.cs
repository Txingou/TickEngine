namespace TickEngine;

/// <summary>系统的线程亲和：决定该系统由哪条循环调度执行。</summary>
public enum ThreadKind
{
    /// <summary>后台工作线程：引擎为每个 (组名, Worker, Fps) 档位自建一条后台循环线程。</summary>
    Worker = 0,

    /// <summary>主线程（UI 线程）：由宿主经 <see cref="IFrameDispatcher"/> 投递到主/UI 线程执行。</summary>
    Main = 1,
}
