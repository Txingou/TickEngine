using TickEngine;

namespace TickEngine.Probe;

/// <summary>装配选项：哪些采集接上（控制台可只用纯采集，不建 UI 探针）。</summary>
public sealed record ProbeSessionOptions
{
    /// <summary>节拍日志 CSV 路径；null = 不启用 BeatLogSystem。</summary>
    public string? BeatLogPath { get; init; }

    /// <summary>BeatLog 初始启用（默认 true）。</summary>
    public bool BeatLogEnabled { get; init; } = true;

    /// <summary>是否启用 UI 线程 CPU 探针（需宿主把 CaptureUiThread 在目标线程调用一次）。</summary>
    public bool TrackUiThread { get; init; } = true;

    /// <summary>是否启用绘制计时累加器（宿主画布经 IPaintTimingSink 上报）。</summary>
    public bool TrackPaint { get; init; } = true;
}

/// <summary>
/// 一次装配会话：把探测系统注册进宿主引擎并持有 UI 侧探针。
/// 纯采集部分（Stats/BeatLog）为 Worker 亲和 → 不要求 UI 线程 → 控制台宿主可挂；
/// 呈现是本程序集内的 Avalonia 探针窗：宿主自带 Avalonia 时直接用它的 UI 线程，
/// 否则由 <see cref="ProbeAppLauncher"/> 在后台线程启动常驻探针应用；Open/Close/Toggle 一律 marshal 到该 UI 线程。
/// </summary>
public sealed class ProbeSession : IDisposable
{
    /// <summary>系统快照发布器（Worker 10Hz 读 Systems → Latest/SnapshotPublished）。</summary>
    public UiStatsPublisher Stats { get; }

    /// <summary>节拍日志系统（未启用为 null）。</summary>
    public BeatLogSystem? BeatLog { get; }

    /// <summary>UI 线程 CPU 探针（options.TrackUiThread 为 false 时为 null）。</summary>
    public UiThreadProbe? ThreadProbe { get; }

    /// <summary>绘制计时累加器（options.TrackPaint 为 false 时为 null）。</summary>
    public UiPaintStats? PaintStats { get; }

    /// <summary>探针日志缓冲（Console.Out/Error + 引擎异常上报都进这里；窗口没开也照样缓冲）。</summary>
    public ProbeLog Log { get; }

    private readonly UpdateEngine _engine;
    private readonly object _windowGate = new();
    private ProbeUiHost? _windowHost;
    private readonly ProbeConsoleCapture _consoleCapture;
    private readonly Action<UpdateSystem, Exception> _faultLogger;
    private bool _disposed;

    /// <summary>每引擎一份会话（防止重复装配出重复系统/双写 CSV）。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UpdateEngine, ProbeSession> Sessions = new();
    private static readonly object SessionGate = new();

    private ProbeSession(UpdateEngine engine, ProbeSessionOptions options,
                         UiStatsPublisher stats, BeatLogSystem? beatLog,
                         UiThreadProbe? threadProbe, UiPaintStats? paintStats)
    {
        _engine = engine;
        Stats = stats;
        BeatLog = beatLog;
        ThreadProbe = threadProbe;
        PaintStats = paintStats;

        // 日志缓冲 + Console tee（Q3=A：Attach 时装、Dispose 时还原；窗口没开也缓冲，开窗回放历史）
        Log = new ProbeLog();
        _consoleCapture = new ProbeConsoleCapture(Log);
        _faultLogger = (system, ex) =>
        {
            // Q6=A：引擎异常不经 Console，主动写成 Error；带首个 file:line 便于人读，
            // 同时把异常对象挂在条目上（日志面板双击即可精确跳转源码）。
            string? where = ProbeCodeJump.DescribeLocation(ex);
            Log.Error($"[异常] {system.Name}: {ex.GetType().Name}: {ex.Message}" +
                      (where is null ? string.Empty : $" @ {where}"), ex);
        };
    }

    /// <summary>宿主引擎（内部：窗口宿主/呈现代码用）。</summary>
    internal UpdateEngine Engine => _engine;

    /// <summary>
    /// 把探测系统注册进宿主引擎（可 Start 前调用，随引擎启停）。
    /// <b>同一引擎只装配一次</b>：重复 Attach 会返回既有会话（旧实现会再注册一套 UiStats/BeatLog，
    /// 表格出现重复行、CSV 行翻倍）；若既有会话已 Dispose 则抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static ProbeSession Attach(UpdateEngine engine, ProbeSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        options ??= new ProbeSessionOptions();

        lock (SessionGate)
        {
            if (Sessions.TryGetValue(engine, out var existing))
            {
                if (existing._disposed)
                {
                    throw new InvalidOperationException(
                        "该引擎的探针会话已 Dispose：探针每引擎只装配一次（请为新引擎新建会话）");
                }
                return existing;
            }

            var stats = new UiStatsPublisher(() => engine);
            engine.Register(stats, Schedule.Worker(10, "UiStats"));

            BeatLogSystem? beatLog = null;
            if (!string.IsNullOrEmpty(options.BeatLogPath))
            {
                beatLog = new BeatLogSystem(engine, options.BeatLogPath);
                beatLog.Enabled = options.BeatLogEnabled;
                engine.Register(beatLog, Schedule.Worker(10, "BeatLog"));
            }

            var session = new ProbeSession(engine, options, stats, beatLog,
                options.TrackUiThread ? new UiThreadProbe() : null,
                options.TrackPaint ? new UiPaintStats() : null);
            session.StartCapture();
            Sessions.Add(engine, session);
            return session;
        }
    }

    /// <summary>
    /// 装配完成后的收尾（<see cref="Attach"/> 内部调用）：安装 Console tee + 订阅引擎异常上报。
    /// 之所以不在构造器里做，是为了让 tee 捕获不到装配期间探针自己的输出。
    /// </summary>
    private void StartCapture()
    {
        _engine.SystemFaulted += _faultLogger;
        _consoleCapture.Install();
    }

    /// <summary>宿主在目标线程调用：记录该线程 OS id 供 CPU 采样（须在目标线程执行一次）。</summary>
    public void CaptureUiThread() => ThreadProbe?.CaptureUiThread();

    /// <summary>开始后台采样（启动引擎后调用）。</summary>
    public void StartSampling() => ThreadProbe?.StartSampling();

    /// <summary>启停节拍日志（BeatLog 未启用时 no-op）。</summary>
    public void SetBeatLogEnabled(bool enabled)
    {
        if (BeatLog != null) { BeatLog.Enabled = enabled; }
    }

    /// <summary>确保节拍日志尾数据落盘（宿主关闭前调用；幂等）。</summary>
    public void FlushBeatLog() => BeatLog?.FlushNow();

    // ---- 统一探针窗口入口（Avalonia 呈现；宿主自带 UI 线程或由常驻探针应用提供）----

    /// <summary>探针窗口当前是否已打开（未装配窗口宿主为 false）。</summary>
    public bool IsProbeWindowOpen
    {
        get { lock (_windowGate) { return _windowHost?.IsOpen ?? false; } }
    }

    /// <summary>
    /// 探针窗口自上次开窗以来已执行的刷新次数（每次定时器触发表格/状态行重绘 +1）。
    /// 冒烟/自检用：验证"窗口真的在自驱刷新"，而非只是静态打开。
    /// </summary>
    public long ProbeRefreshCount
    {
        get { lock (_windowGate) { return _windowHost?.RefreshCount ?? 0; } }
    }

    /// <summary>探针窗口日志面板当前显示的条数（冒烟/自检用：验证日志确实进了面板）。</summary>
    public long ProbeLogItemCount
    {
        get { lock (_windowGate) { return _windowHost?.LogItemCount ?? 0; } }
    }

    /// <summary>
    /// 打开探针窗口（幂等；已开则前置激活）。首次调用确保 Avalonia UI 线程可用
    /// （宿主自带则复用，否则启动常驻探针应用），随后在其上创建窗口；失败只记日志、不外抛。
    /// 会话已 Dispose 时为 no-op（旧实现会新建一个再也没人能关掉的窗口）。
    /// </summary>
    public void OpenProbeWindow()
    {
        lock (_windowGate)
        {
            if (_disposed)
            {
                Log.Error("[探针] 会话已 Dispose：忽略开窗请求");
                return;
            }
            _windowHost ??= new ProbeUiHost(this, _engine);
            _windowHost.Open();
        }
    }

    /// <summary>关闭探针窗口（幂等；未开为 no-op）。</summary>
    public void CloseProbeWindow()
    {
        lock (_windowGate)
        {
            _windowHost?.Close();
        }
    }

    /// <summary>切换探针窗口开关（P 键语义）。</summary>
    public void ToggleProbeWindow()
    {
        if (IsProbeWindowOpen) { CloseProbeWindow(); }
        else { OpenProbeWindow(); }
    }

    /// <summary>拆会话（幂等）：还原 Console、关窗、停采样、flush CSV；此后开窗请求被忽略。</summary>
    public void Dispose()
    {
        lock (_windowGate)
        {
            if (_disposed) { return; }
            _disposed = true;
        }
        FlushBeatLog();
        try { _engine.SystemFaulted -= _faultLogger; } catch { /* 卸载失败不影响宿主 */ }
        _consoleCapture.Uninstall();   // 还原原始 Console.Out/Error（幂等）
        lock (_windowGate)
        {
            _windowHost?.Dispose();
            _windowHost = null;
        }
        ThreadProbe?.Dispose();
    }
}
