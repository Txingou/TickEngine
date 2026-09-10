using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TickEngine.Probe;

/// <summary>
/// 绘制计时挂点（跨框架）：宿主画布在“一次绘制”首尾各取一次 Stopwatch 刻度并上报。
/// WinForms 在 OnPaint、Avalonia 在 Render 里调用；实现方负责线程安全累加。
/// </summary>
public interface IPaintTimingSink
{
    /// <summary>一次绘制开始（返回 token 传入 EndPaint）。简单用法可不接 token：两端各取 GetTimestamp 求差。</summary>
    long BeginPaint();

    /// <summary>一次绘制结束，上报耗时刻度（elapsedTicks = End 时刻 - Begin 时刻）。</summary>
    void EndPaint(long elapsedTicks);
}

/// <summary>绘制耗时累加器（P1）：Begin/End 之间由宿主画布调用，任意线程可读统计。</summary>
public sealed class UiPaintStats : IPaintTimingSink
{
    private long _paintCount;
    private long _paintTotalTicks;   // Stopwatch 刻度
    private long _paintMaxTicks;

    /// <summary>累计绘制次数。</summary>
    public long PaintCount => Interlocked.Read(ref _paintCount);

    /// <summary>绘制平均耗时（ms；无数据为 0）。</summary>
    public double AverageMs
    {
        get
        {
            long n = Interlocked.Read(ref _paintCount);
            if (n == 0) { return 0; }
            long total = Interlocked.Read(ref _paintTotalTicks);
            return (double)total / n / Stopwatch.Frequency * 1000.0;
        }
    }

    /// <summary>绘制最大耗时（ms；无数据为 0）。</summary>
    public double MaxMs =>
        (double)Interlocked.Read(ref _paintMaxTicks) / Stopwatch.Frequency * 1000.0;

    long IPaintTimingSink.BeginPaint() => Stopwatch.GetTimestamp();

    void IPaintTimingSink.EndPaint(long elapsedTicks) => RecordPaint(elapsedTicks);

    /// <summary>上报一次绘制耗时刻度（Begin 与 End 的差）。</summary>
    public void RecordPaint(long elapsedTicks)
    {
        Interlocked.Increment(ref _paintCount);
        Interlocked.Add(ref _paintTotalTicks, elapsedTicks);
        long cur = Interlocked.Read(ref _paintMaxTicks);
        while (elapsedTicks > cur)
        {
            long prev = Interlocked.CompareExchange(ref _paintMaxTicks, elapsedTicks, cur);
            if (prev == cur) { break; }
            cur = prev;
        }
    }
}

/// <summary>
/// UI 线程观测（P2，框架无关）：OS 级 CPU 占用率采样。
/// 零自扰：读 ProcessThread.TotalProcessorTime 差分，不投递任何消息，
/// 不会像 Application.Idle 自喂那样自己制造忙。CaptureUiThread 须在目标 UI 线程调用一次。
/// </summary>
public sealed class UiThreadProbe : IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly object _gate = new();
    private Thread? _sampler;
    private volatile bool _sampling;
    private bool _disposed;
    private ProcessThread? _cachedThread;      // 目标线程句柄缓存（避免每 200ms 全进程枚举）

    private int _uiThreadOsId = -1;
    private TimeSpan _lastCpu;
    private double _lastWallSeconds;
    private double _cpuFraction;               // 最近窗口 UI 线程 CPU 占用率 0..1
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    public UiThreadProbe()
    {
    }

    /// <summary>最近窗口 UI 线程 CPU 占用率（0..1；1 = 线程饱和）。</summary>
    public double UiCpuFraction => Volatile.Read(ref _cpuFraction);

    /// <summary>
    /// 是否已有采样基线（= 宿主在目标线程调用过 <see cref="CaptureUiThread"/>）。
    /// 为 false 时 <see cref="UiCpuFraction"/> 属“无数据”，UI 应显示“未采集”而不是看似合理的 0%。
    /// </summary>
    public bool HasBaseline => Volatile.Read(ref _uiThreadOsId) >= 0;

    /// <summary>必须在目标 UI 线程调用一次：记录该线程的 OS thread id（采样基准）。</summary>
    public void CaptureUiThread()
    {
        lock (_gate)
        {
            _uiThreadOsId = (int)GetCurrentThreadId();
            _cachedThread = null;              // 换线程 → 丢弃旧句柄
            _lastCpu = TryCpuTime() ?? TimeSpan.Zero;
            _lastWallSeconds = _wall.Elapsed.TotalSeconds;
        }
    }

    /// <summary>启动后台采样（每 200ms 读目标线程 CPU 时间差分）。幂等；Dispose 后不再启动。</summary>
    public void StartSampling()
    {
        lock (_gate)
        {
            if (_sampling || _disposed) { return; }
            _sampling = true;
            _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "UiThreadProbe" };
            _sampler.Start();
        }
    }

    private void SampleLoop()
    {
        while (_sampling)
        {
            Thread.Sleep(200);
            SampleOnce();
        }
    }

    private void SampleOnce()
    {
        lock (_gate)
        {
            if (_uiThreadOsId < 0) { return; }
            TimeSpan? cpu = TryCpuTime();
            if (cpu is null)
            {
                // 目标线程已消失：保持上一次读数（不伪造 0%，也不混算“全进程 CPU”）
                return;
            }
            double now = _wall.Elapsed.TotalSeconds;
            double dt = now - _lastWallSeconds;
            if (dt > 0.05)
            {
                double frac = (cpu.Value - _lastCpu).TotalSeconds / dt;   // 单线程上限 ~1
                Volatile.Write(ref _cpuFraction, Math.Clamp(frac, 0, 1));
            }
            _lastCpu = cpu.Value;
            _lastWallSeconds = now;
        }
    }

    /// <summary>
    /// 读目标线程累计 CPU 时间；线程已退出返回 null（不再退回“全进程 CPU”——那会让差分两端
    /// 不是同一个对象，瞬间算出假尖峰并夹到 100%）。首次解析后缓存 ProcessThread，避免每 200ms
    /// 全进程枚举带来 5Hz × N 线程的句柄/终结器开销。
    /// </summary>
    private TimeSpan? TryCpuTime()
    {
        int id = Volatile.Read(ref _uiThreadOsId);
        if (id < 0) { return null; }

        if (_cachedThread is not null)
        {
            try { return _cachedThread.TotalProcessorTime; }
            catch { _cachedThread = null; }
        }

        try
        {
            using var proc = Process.GetCurrentProcess();
            foreach (ProcessThread pt in proc.Threads)
            {
                if (pt.Id == id)
                {
                    _cachedThread = pt;        // 缓存并保留（不 Dispose）
                    return pt.TotalProcessorTime;
                }
                pt.Dispose();
            }
        }
        catch { /* 枚举失败按“未知”处理 */ }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _sampling = false;
        }
        _sampler?.Join(300);
        lock (_gate)
        {
            _cachedThread?.Dispose();
            _cachedThread = null;
        }
    }
}
