using TickEngine;

namespace TickEngine.WinformDemo;

/// <summary>
/// WinForms 版主线程调度器：把引擎 Main 组的整帧投递到 UI 线程执行（BeginInvoke）。
/// v2（组级在途）后本类【不再持有忙标志】——在途上限与丢拍判断已上移到引擎的每个 Loop，
/// 因此多个 Main 组的帧可以在 UI 消息队列中并存，由消息泵串行执行，
/// 低频组不再被单个共享忙标志饿死。
/// <para>
/// v3 关键约定：<b>投递出去的回调必须最终执行 frame()</b>——引擎在帧闭包的 finally 里释放
/// 本组的在途槽；若像旧实现那样"控件已销毁就直接 return"，回调被吞掉，槽位会永久卡死
/// （TickNumber 冻结、Dropped 一直涨）。控件销毁后帧内的系统若访问已销毁控件，会由引擎的
/// 异常隔离按系统计数，不会影响引擎与其他系统。
/// </para>
/// </summary>
public sealed class WinFormsDispatcher : IFrameDispatcher
{
    private readonly Control _target;

    public WinFormsDispatcher(Control target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public string ThreadDisplayName => "UI(WinForms)";

    public bool TryPost(Action frame)
    {
        try
        {
            if (_target.IsDisposed || _target.Disposing)
            {
                return false;   // 目标已销毁：投递失败 → 引擎计丢拍并释放本组在途槽
            }
            _target.BeginInvoke(() =>
            {
                // 即使此刻控件已销毁也必须执行 frame()：引擎需要它来释放在途槽（见类型注释）
                frame();
            });
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // 句柄尚未创建/已销毁：无法投递 → 引擎计丢拍
            return false;
        }
    }
}
