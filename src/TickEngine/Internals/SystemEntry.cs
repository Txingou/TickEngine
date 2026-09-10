namespace TickEngine;

/// <summary>
/// 单个系统的注册状态（引擎内部使用）。
/// 结构字段在引擎锁内读写；Faults / Update 耗时经 Interlocked/Volatile 发布。
/// </summary>
internal sealed class SystemEntry
{
    private readonly UpdateEngine _engine;
    private volatile bool _removed;
    private volatile bool _startedInSession;   // 执行线程读写
    private volatile bool _stopCalled;
    private long _faults;
    private long _lastUpdateTicks;   // Stopwatch 刻度：最近一拍 Update 实耗
    private long _maxUpdateTicks;    // Stopwatch 刻度：本会话单拍 Update 最大实耗
    private Exception? _lastException;   // 最近一次异常对象（Volatile 发布；仅保留最近一次）

    public SystemEntry(UpdateEngine engine, UpdateSystem system, Schedule schedule)
    {
        _engine = engine;
        UpdateSystem = system;
        Schedule = schedule;
    }

    public UpdateSystem UpdateSystem { get; }

    public Schedule Schedule { get; set; }

    public SystemLoop? Owner { get; internal set; }

    /// <summary>
    /// 本条目被放进哪个循环的卸载队列（F12）：迁移到新组后，旧队列里的陈旧条目不得再停它。
    /// </summary>
    internal SystemLoop? StopQueuedFor { get; set; }

    public bool IsRemoved => _removed;

    public bool StartedInSession => _startedInSession;

    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>最近一次异常对象（任意线程可读；本会话未发生异常为 null）。</summary>
    public Exception? LastException => Volatile.Read(ref _lastException);

    /// <summary>最近一拍 Update 实耗（秒，真实墙钟；0 = 尚未执行）。</summary>
    public double LastUpdateSeconds =>
        (double)Interlocked.Read(ref _lastUpdateTicks) / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>本会话单拍 Update 最大实耗（秒，真实墙钟；0 = 尚未执行）。</summary>
    public double MaxUpdateSeconds =>
        (double)Interlocked.Read(ref _maxUpdateTicks) / System.Diagnostics.Stopwatch.Frequency;

    internal void ResetSession()
    {
        _startedInSession = false;
        _stopCalled = false;
        Interlocked.Exchange(ref _lastUpdateTicks, 0);
        Interlocked.Exchange(ref _maxUpdateTicks, 0);
        Volatile.Write(ref _lastException, null);   // 新会话清空现场（Q2：恢复后保留、跨会话不继承）
    }

    internal void MarkRemoved() => _removed = true;

    /// <summary>执行线程调用：会话内首拍前启动。</summary>
    internal void EnsureStarted()
    {
        if (!_startedInSession && !_removed)
        {
            _startedInSession = true;
            try { UpdateSystem.InvokeStart(); }
            catch (Exception ex) { CountFault(ex); }
        }
    }

    /// <summary>执行线程调用：记录本拍 Update 实耗（真实墙钟刻度，不随注入的 TimeProvider 走）。</summary>
    internal void RecordUpdateTime(long elapsedTicks)
    {
        Interlocked.Exchange(ref _lastUpdateTicks, elapsedTicks);
        // 单写者（执行线程），读端经 Interlocked；max 用 CAS 保证安全
        long cur = Interlocked.Read(ref _maxUpdateTicks);
        while (elapsedTicks > cur)
        {
            long prev = Interlocked.CompareExchange(ref _maxUpdateTicks, elapsedTicks, cur);
            if (prev == cur) { break; }
            cur = prev;
        }
    }

    /// <summary>执行线程调用：帧更新并做异常隔离。</summary>
    internal void UpdateFrame(in FrameContext frame)
    {
        try
        {
            UpdateSystem.InvokeUpdate(frame);
        }
        catch (Exception ex)
        {
            CountFault(ex);
        }
    }

    /// <summary>Stop 恰好一次（任意线程，调用方保证不与 Update 并发）。</summary>
    internal void StopOnce()
    {
        if (_stopCalled) { return; }
        _stopCalled = true;
        if (_startedInSession)
        {
            try { UpdateSystem.InvokeStop(); }
            catch (Exception ex) { CountFault(ex); }
        }
    }

    internal void CountFault(Exception ex)
    {
        Volatile.Write(ref _lastException, ex);   // 先留现场再报事件：事件处理器读 Systems 即可看到本次异常
        Interlocked.Increment(ref _faults);
        try { _engine.RaiseSystemFaulted(UpdateSystem, ex); }
        catch { /* 事件处理器异常不扩散 */ }
    }

    internal SystemView ToView()
    {
        var owner = Owner;
        return new SystemView
        {
            UpdateSystem = UpdateSystem,
            Name = UpdateSystem.Name,
            GroupName = Schedule.GroupName,
            ThreadKind = Schedule.ThreadKind,
            Fps = Schedule.Fps,
            Enabled = UpdateSystem.Enabled,
            TickNumber = owner?.TickNumber ?? 0,
            Dropped = owner?.Dropped ?? 0,
            Faults = Faults,
            ActualFps = owner?.ActualFps ?? 0,
            LastDeltaSeconds = owner?.LastDeltaSeconds ?? 0,
            ThreadName = owner?.ThreadDisplayName ?? "-",
            ElapsedSeconds = owner?.ElapsedSeconds ?? 0,
            LastUpdateSeconds = LastUpdateSeconds,
            MaxUpdateSeconds = MaxUpdateSeconds,
            LastException = LastException,
        };
    }
}
