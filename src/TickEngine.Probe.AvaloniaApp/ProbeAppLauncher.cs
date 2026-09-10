using Avalonia;
using Avalonia.Threading;

namespace TickEngine.Probe;

/// <summary>
/// 探针 Avalonia 应用的生命周期管理（核心约束，均已实测）：
/// <list type="bullet">
/// <item>Avalonia 的 <c>AppBuilder.Setup()</c> 是<b>进程级一次性闩锁</b>：同进程起第二个应用必抛
/// <c>InvalidOperationException("Setup was already called on one of AppBuilder instances")</c>，
/// 且 <c>Shutdown()</c> 不会重置——因此探针应用<b>每进程只启动一次并常驻</b>（Q3=A）。</item>
/// <item>裸线程跑 Main 若抛异常会因未处理而<b>拆掉整个宿主进程</b>（exit 0xE0434352）→
/// 本类把启动体包在 try/catch 中，失败只记录状态、不抛给宿主（Q9=A、Q10=A）。</item>
/// <item>宿主自身已是 Avalonia 应用时<b>绝不能</b>再启动本应用，而是复用其 UI 线程。</item>
/// <item><b>存活判定</b>：应用/线程死掉后 <c>Application.Current</c> 仍非空、<c>Dispatcher.Post</c>
/// 也不再抛异常（静默不执行）——所以可用性必须由“线程是否还活着”推导，不能只看 <c>Application.Current</c>，
/// 否则会把一个死应用报成可用，让开窗静默失败（A2）。</item>
/// </list>
/// 常驻形态：<c>ShutdownMode = OnExplicitShutdown</c> + <c>MainWindow = null</c>，
/// 关窗只关窗口、UI 线程继续泵，可反复开窗（已实测 3 轮开→关）。
/// </summary>
public static class ProbeAppLauncher
{
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? _thread;
    private static bool _startAttempted;
    private static volatile bool _started;
    private static volatile bool _ownsApp;      // 本应用是否由我们启动（决定 Dispose 时是否 Shutdown）
    private static volatile string? _lastError;

    /// <summary>探针应用当前是否可用（由我们启动、线程仍活着且已完成初始化）。</summary>
    public static bool IsRunning => _started && _thread is { IsAlive: true };

    /// <summary>最近一次启动/开窗失败原因（成功为 null；每次尝试会清空重记）。</summary>
    public static string? LastError => _lastError;

    /// <summary>
    /// 确保有一条可用的 Avalonia UI 线程：宿主已是 Avalonia 应用 → 直接复用；
    /// 否则启动常驻探针应用（每进程仅一次，失败后不再重试，但会持续反映真实存活状态）。
    /// </summary>
    public static bool EnsureUiThread()
    {
        // 宿主自带 Avalonia（或我们自己已启动过）：先按“线程是否活着”判定可用性
        if (Application.Current is not null)
        {
            if (_thread is null || _thread.IsAlive) { return true; }
            _started = false;
            _lastError = "探针应用线程已退出（Avalonia Setup 为进程级一次性闩锁，无法重启）";
            return false;
        }

        lock (Gate)
        {
            if (_started && _thread is { IsAlive: true }) { return true; }

            if (_startAttempted)
            {
                // 上次可能只是"启动很慢"：若此刻线程已就绪，则补认成功（A7）
                if (Ready.IsSet && _thread is { IsAlive: true } && Application.Current is not null)
                {
                    _started = true;
                    _lastError = null;
                    return true;
                }
                _lastError ??= "探针应用此前启动失败或已关闭（Avalonia Setup 为进程级一次性闩锁，无法重启）";
                return false;
            }

            _startAttempted = true;
            _lastError = null;
            Ready.Reset();

            var thread = new Thread(() =>
            {
                try
                {
                    Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    // 绝不外抛：未处理异常会拆掉宿主进程
                    _lastError = $"{ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    _started = false;                 // 应用退出后立刻反映"不可用"
                    try { Ready.Set(); } catch { /* 已释放 */ }
                }
            })
            {
                IsBackground = true,          // Q11：宿主退出不被它挂住
                Name = "TickEngine-ProbeAvalonia",
            };
            thread.SetApartmentState(ApartmentState.STA);   // A6：DTE/剪贴板等 COM 需要 STA
            _thread = thread;
            _ownsApp = true;                  // 先认领（即使后面等待超时，也还能被 Shutdown 收掉）
            thread.Start();

            if (!Ready.Wait(TimeSpan.FromSeconds(10)))
            {
                // 不判死：应用可能只是启动慢，后续调用会按线程存活情况补认
                _lastError = "探针应用启动超时（后续开窗会按线程存活情况重试判定）";
                return false;
            }
            _started = _lastError is null && Application.Current is not null;
            if (!_started && _lastError is null) { _lastError = "探针应用启动后 Application.Current 为空"; }
            return _started;
        }
    }

    /// <summary>App 初始化完成（UI 线程上）→ 唤醒等待者。</summary>
    internal static void NotifyReady()
    {
        try { Ready.Set(); } catch { /* 已释放 */ }
    }

    /// <summary>
    /// 会话 Dispose 时调用：若常驻应用是我们启动的，则显式关闭它。
    /// 宿主自带的 Avalonia 应用绝不动；幂等。
    /// </summary>
    public static void ShutdownOwnedApp()
    {
        if (!_ownsApp) { return; }
        try
        {
            if (Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
            }
        }
        catch (Exception ex)
        {
            _lastError = $"探针应用关闭失败: {ex.GetType().Name}";
        }
        finally
        {
            _ownsApp = false;
        }
    }
}
