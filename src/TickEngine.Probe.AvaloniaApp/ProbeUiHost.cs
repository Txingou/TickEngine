using Avalonia.Threading;
using TickEngine;

namespace TickEngine.Probe;

/// <summary>
/// 探针窗口宿主：把"开/关窗"统一 marshal 到 Avalonia UI 线程执行，两条宿主路径合一——
/// <list type="bullet">
/// <item>宿主自身是 Avalonia 应用 → 直接用它的 UI 线程（不启动第二个应用，实测第二个必失败）；</item>
/// <item>非 Avalonia 宿主（控制台/WinForms）→ 由 <see cref="ProbeAppLauncher"/> 启动常驻探针应用，再用它的 UI 线程。</item>
/// </list>
/// 失败（应用起不来/已退出/建窗抛异常）不抛给宿主：记 Error 到探针日志并让 <see cref="IsOpen"/> 保持 false（Q9=A）。
/// </summary>
internal sealed class ProbeUiHost
{
    private readonly ProbeSession _session;
    private readonly UpdateEngine _engine;
    private readonly object _gate = new();
    private ProbeWindow? _window;      // 仅 UI 线程写；其他线程经 Volatile 读
    private volatile bool _open;

    public ProbeUiHost(ProbeSession session, UpdateEngine engine)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public bool IsOpen => _open;

    public string? LastError => ProbeAppLauncher.LastError;

    /// <summary>当前窗体的刷新计数（无窗为 0）。</summary>
    public long RefreshCount => Volatile.Read(ref _window)?.RefreshCount ?? 0;

    /// <summary>当前窗体日志清单的显示条数（无窗为 0）。</summary>
    public long LogItemCount => Volatile.Read(ref _window)?.LogItemCount ?? 0;

    public void Open()
    {
        if (!ProbeAppLauncher.EnsureUiThread())
        {
            _session.Log.Error($"[探针] 无法打开窗口：{ProbeAppLauncher.LastError ?? "Avalonia UI 线程不可用"}");
            return;
        }
        try
        {
            Dispatcher.UIThread.Post(OpenOnUiThread);
        }
        catch (Exception ex)
        {
            // Post 本身失败（UI 线程关闭中）：如实记录，不让宿主看到异常
            _session.Log.Error($"[探针] 投递开窗请求失败: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private void OpenOnUiThread()
    {
        try
        {
            lock (_gate)
            {
                if (_open && _window is { IsVisible: true })
                {
                    _window.Activate();
                    return;
                }
                var window = new ProbeWindow(_session, _engine);
                window.Closed += (_, _) =>
                {
                    // 只有当前窗体才允许清状态（避免旧窗的迟到 Closed 把新窗状态清掉）
                    if (ReferenceEquals(Volatile.Read(ref _window), window))
                    {
                        _window = null;
                        _open = false;
                    }
                };
                _window = window;
                _open = true;
                window.Show();
            }
        }
        catch (Exception ex)
        {
            // 建窗/显示失败（主题资源、平台问题、窗口异常…）：绝不外抛——
            // 抛到 Avalonia 派发器上会让该应用从此"活着但什么都不执行"（A2），
            // 这里改为记录到探针日志，并把状态复位以便下次重试。
            _open = false;
            _window = null;
            _session.Log.Error($"[探针] 创建/显示探针窗口失败: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    public void Close()
    {
        if (!ProbeAppLauncher.EnsureUiThread()) { return; }
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_gate)
                {
                    _window?.Close();
                }
            });
        }
        catch (Exception ex)
        {
            _session.Log.Error($"[探针] 投递关窗请求失败: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        Close();
        ProbeAppLauncher.ShutdownOwnedApp();   // 只关我们自己启动的常驻应用；宿主自带的绝不动
    }
}
