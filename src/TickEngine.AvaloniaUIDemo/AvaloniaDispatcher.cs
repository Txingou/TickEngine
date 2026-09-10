using Avalonia;
using TickEngine;

namespace TickEngine.AvaloniaUIDemo;

/// <summary>
/// Avalonia 版主线程调度器：把引擎 Main 组整帧投递到 UI 线程执行。
/// Avalonia 等价于 WinForms BeginInvoke = Dispatcher.UIThread.Post；
/// 忙检测/在途上限已上移引擎 Loop（组级在途），此处只做纯投递。
/// <para>
/// 注意：<c>Post</c> 在 UI 线程已关闭时可能"收下但不执行"——这种情况由引擎的在途看门狗兜底
/// （超过 max(1s, 10×周期) 未回收则强制释放槽并上报 <c>DispatcherFaulted</c>），因此本类
/// 只需把可捕获的失败如实返回 false。
/// </para>
/// </summary>
public sealed class AvaloniaDispatcher : IFrameDispatcher
{
    public string ThreadDisplayName => "UI(Avalonia)";

    public bool TryPost(Action frame)
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => frame());
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // Dispatcher 不可用/平台已关闭 → 引擎计丢拍
        }
        catch (TaskCanceledException)
        {
            return false;   // 关闭中的 UI 线程拒绝投递 → 引擎计丢拍
        }
    }
}
