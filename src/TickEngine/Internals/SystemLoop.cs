namespace TickEngine;

/// <summary>
/// 循环组抽象基类（模板方法）。一个 (组名, 亲和, FPS) 组 = 一条节拍流，
/// 成员顺序 = 该组注册序；一拍内按注册序串行跑完全部启用系统，共享同一 FrameContext。
/// 骨架 RunLoopBody 由子类决定“节拍从哪来”，差异钩子 <see cref="OnBeat"/> 决定“一拍怎么执行”。
/// 帧执行持成员快照数组、不持锁 → 结构性变更永不撕裂正在执行的帧。
/// 统计（拍号/丢拍/实测FPS/纪元）Interlocked/Volatile 发布。
/// <para>
/// 会话令牌（v3）：会话用一个自增整数标识而不是布尔——Stop 让令牌失效、Start 换新令牌。
/// 于是"上一会话的迟到线程/迟到帧"天然认不出自己已过期（旧布尔在 Start 后又会变真，
/// 会让迟到帧跑进新会话并释放不属于它的在途槽）。
/// </para>
/// </summary>
internal abstract class SystemLoop
{
    private readonly UpdateEngine _engine;
    private readonly object _membersGate = new();
    private readonly List<SystemEntry> _members = new();

    private Thread? _runner;
    private PeriodicTimer? _beatTimer;
    private int _activeSession;        // 0 = 无会话；>0 = 当前会话号（每次 StartRunner 自增）
    private int _sessionCounter;       // 会话号发生器
    private long _tick;
    private long _dropped;
    private double _actualFpsEma;
    private double _lastDeltaSeconds;
    private double _elapsedSeconds;    // 最近一拍观测到的"会话内流逝秒"（停后冻结；从未启动为 0）
    private long _sessionStartTs;
    private long _lastExecTs;

    protected SystemLoop(UpdateEngine engine, Schedule schedule)
    {
        _engine = engine;
        Schedule = schedule;
        Key = (schedule.GroupName, schedule.ThreadKind, schedule.Fps);
    }

    public Schedule Schedule { get; }

    /// <summary>本组在引擎注册表里的键（F8 空组回收时用于安全摘除）。</summary>
    internal (string Group, ThreadKind Kind, int Fps) Key { get; }

    /// <summary>卸载 Stop 队列：引擎锁保护；执行线程拍边界消费。</summary>
    public List<SystemEntry> StopQueue { get; } = new();

    public string ThreadDisplayName
    {
        get
        {
            if (Schedule.ThreadKind == ThreadKind.Main)
            {
                var d = _engine.Dispatcher;
                return d?.ThreadDisplayName ?? "Main";
            }
            return _runner?.Name ?? WorkerThreadName(Schedule);
        }
    }

    /// <summary>线程名（含组名；Default 组保持旧格式简洁）。</summary>
    internal static string WorkerThreadName(Schedule s)
    {
        string kind = s.ThreadKind == ThreadKind.Main ? "Main" : "Worker";
        string suffix = s.ThreadKind == ThreadKind.Main ? "-beat" : "";
        string group = s.GroupName == Schedule.DefaultGroupName
            ? ""
            : $"-{s.GroupName}";
        return $"TickEngine-{kind}{group}-{s.Fps}Hz{suffix}";
    }

    public long TickNumber => Interlocked.Read(ref _tick);
    public long Dropped => Interlocked.Read(ref _dropped);
    public double ActualFps => Volatile.Read(ref _actualFpsEma);
    public double LastDeltaSeconds => Volatile.Read(ref _lastDeltaSeconds);

    /// <summary>
    /// 循环启动以来的流逝时间（秒）。三种状态：
    /// 从未启动 = 0（不再是"开机时长"这类无意义数字）；运行中 = 现算（同一引擎的各个组读到同一时刻，
    /// 保持"所有循环共享同一时间轴"的语义）；已停止 = 冻结在最近一拍的值（不再继续增长）。
    /// </summary>
    public double ElapsedSeconds
    {
        get
        {
            long start = Interlocked.Read(ref _sessionStartTs);
            if (start == 0) { return 0; }
            if (Volatile.Read(ref _activeSession) == 0) { return Volatile.Read(ref _elapsedSeconds); }
            return (double)(Clock.GetTimestamp() - start) / Clock.TimestampFrequency;
        }
    }

    protected TimeProvider Clock => _engine.Clock;
    protected UpdateEngine Engine => _engine;

    /// <summary>当前活跃会话号（0 = 无会话）。</summary>
    internal int ActiveSession => Volatile.Read(ref _activeSession);

    /// <summary>成员列表里是否还有未卸载的系统（F8 空组回收判据）。</summary>
    internal bool HasMembers
    {
        get { lock (_membersGate) { return _members.Any(m => !m.IsRemoved); } }
    }

    /// <summary>给定会话是否仍然活跃（会话令牌：迟到线程/迟到帧据此自我作废）。</summary>
    protected bool IsSessionActive(int session) => session != 0 && Volatile.Read(ref _activeSession) == session;

    /// <summary>本线程是否就是本组的循环线程（用于循环内 Stop 的自 Join 规避）。</summary>
    internal bool IsRunnerThread(Thread thread) => ReferenceEquals(_runner, thread);

    /// <summary>丢拍计数自增（子类在组级在途拒投等场景调用）。</summary>
    protected void CountDrop() => Interlocked.Increment(ref _dropped);

    internal void AddMember(SystemEntry entry)
    {
        lock (_membersGate)
        {
            entry.Owner = this;
            _members.Add(entry);
        }
    }

    internal void RemoveMember(SystemEntry entry)
    {
        lock (_membersGate)
        {
            _members.Remove(entry);
        }
        // 不再滞留归属（被迁移/卸载后再注册的实例不应让本组误以为仍持有它）
        if (ReferenceEquals(entry.Owner, this)) { entry.Owner = null; }
    }

    /// <summary>启动本组循环线程（引擎 Start / 动态注册 / 迁移建组时调用一次）。</summary>
    internal void StartRunner()
    {
        // 本会话已有活线程 → 幂等返回；否则开新会话（旧令牌立即失效，旧线程自行退出）
        if (_runner is { IsAlive: true } && Volatile.Read(ref _activeSession) != 0) { return; }

        int session = Interlocked.Increment(ref _sessionCounter);
        long now = Clock.GetTimestamp();
        _sessionStartTs = now;
        _lastExecTs = now;                      // F9：基线在这里取，首拍 dt ≈ 一个周期（启动开销不计成丢拍）
        Volatile.Write(ref _elapsedSeconds, 0);
        OnSessionStarted();
        Volatile.Write(ref _activeSession, session);   // 状态就绪后再生效

        var thread = new Thread(() => Run(session))
        {
            IsBackground = true,
            Name = WorkerThreadName(Schedule),
        };
        _runner = thread;
        thread.Start();
    }

    /// <summary>会话启动钩子（Main 用于清残留组级在途槽）。</summary>
    protected virtual void OnSessionStarted() { }

    /// <summary>让本组会话失效并唤醒节拍线程（不 Join）。</summary>
    internal void Deactivate()
    {
        Volatile.Write(ref _activeSession, 0);
        // 强制唤醒阻塞在 WaitForNextTickAsync 的循环线程（周期任意长也能立即退出）
        Interlocked.Exchange(ref _beatTimer, null)?.Dispose();
    }

    /// <summary>请求停止并 Join（引擎 Stop 调用；循环线程自身调用时会跳过 Join）。</summary>
    internal void RequestStopAndJoin()
    {
        Deactivate();
        var t = _runner;
        if (t == null) { return; }
        if (t == Thread.CurrentThread)
        {
            return; // 自线程 Stop 不成立 Join：仅失效会话
        }
        int timeoutMs = Schedule.Fps > 0
            ? Math.Max(100, 2000 / Schedule.Fps + 200)
            : 300;
        // 用户 Update 卡死时 Join 超时：放弃等待（后台线程最终随进程退出）
        t.Join(timeoutMs);
    }

    /// <summary>只失效会话、不 Join（引擎 Stop 的第一阶段，持引擎锁内调用，杜绝与 Start 交错）。</summary>
    internal void RequestStop() => Deactivate();

    /// <summary>引擎停止后：调用线程清点（线程已退出）。</summary>
    internal void DrainStopQueueNow()
    {
        List<SystemEntry> pending;
        lock (_engine.SyncGate) { pending = DrainLocked(); }
        foreach (var e in pending) { e.StopOnce(); }
    }

    /// <summary>取走 Stop 队列内容（调用方持引擎锁）；跳过已被迁移到别组的陈旧条目。</summary>
    private List<SystemEntry> DrainLocked()
    {
        if (StopQueue.Count == 0) { return new List<SystemEntry>(); }
        var pending = new List<SystemEntry>(StopQueue.Count);
        foreach (var e in StopQueue)
        {
            // 陈旧条目：已被迁移到别的组（Owner 指向它组）→ 不该由本组停它
            if (e.Owner is { } owner && !ReferenceEquals(owner, this)) { continue; }
            pending.Add(e);
        }
        StopQueue.Clear();
        return pending;
    }

    protected SystemEntry[] SnapshotMembers()
    {
        lock (_membersGate)
        {
            return _members.Where(m => !m.IsRemoved).ToArray();
        }
    }

    // ---------------- 线程主体（模板方法：RunLoopBody 骨架 + 差异钩子 OnBeat） ----------------

    private void Run(int session)
    {
        try
        {
            RunLoopBody(session);
        }
        catch (Exception ex)
        {
            // 循环线程意外崩溃：记到第一个成员头上并让本会话失效
            SystemEntry? victim = null;
            lock (_membersGate) { victim = _members.FirstOrDefault(m => !m.IsRemoved); }
            victim?.CountFault(ex);
        }
        finally
        {
            // 只有仍持有该会话时才作废（避免把新会话踩掉）
            if (Volatile.Read(ref _activeSession) == session) { Volatile.Write(ref _activeSession, 0); }
        }
    }

    /// <summary>模板骨架：由子类选择节拍来源（PeriodicTimer 循环 / 不限速忙跑）。</summary>
    protected abstract void RunLoopBody(int session);

    /// <summary>差异钩子：骨架里“等到一个节拍后”做什么（now 为节拍时刻）。</summary>
    protected abstract void OnBeat(long now, int session);

    /// <summary>
    /// 共享骨架（有节拍）：PeriodicTimer 等节拍 → 判会话存活 → 空组自回收 → OnBeat。
    /// Main 的 dt 由 OnBeat 的投递闭包在真正执行时实测（自愈），故此处不做间隔会计。
    /// </summary>
    protected void RunTimedLoop(int session)
    {
        var period = TimeSpan.FromSeconds(1.0 / Schedule.Fps);
        var timer = new PeriodicTimer(period, Clock);
        Interlocked.Exchange(ref _beatTimer, timer);
        try
        {
            while (IsSessionActive(session))
            {
                try
                {
                    if (!timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult())
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                if (!IsSessionActive(session)) { break; }

                // F8：成员全部迁移/卸载后自我回收（Worker 组；Main 组保留，避免与在途门控纠缠）
                if (Schedule.ThreadKind == ThreadKind.Worker && !HasMembers)
                {
                    if (Engine.PruneEmptyLoop(this)) { break; }
                }

                long now = Clock.GetTimestamp();
                OnBeat(now, session);
            }
        }
        finally
        {
            timer.Dispose();
            Interlocked.Exchange(ref _beatTimer, null);
        }
    }

    /// <summary>实测上一拍到现在的间隔（秒）并推进 _lastExecTs（由测量者单线程调用）。</summary>
    protected double MeasureDelta(long now)
    {
        long prev = Interlocked.Read(ref _lastExecTs);
        double dt = (double)(now - prev) / Clock.TimestampFrequency;
        Interlocked.Exchange(ref _lastExecTs, now);
        return dt;
    }

    /// <summary>丢拍会计：dt 超过周期 N 倍 → 中间 N-1 拍被 PeriodicTimer 合并丢弃（丢拍不补）。</summary>
    protected void CountMissedFromGap(double dtSeconds, TimeSpan period)
    {
        double periods = dtSeconds / period.TotalSeconds;
        if (periods >= 2.0)
        {
            long missed = (long)(periods - 1.0);
            if (missed > 0) { Interlocked.Add(ref _dropped, missed); }
        }
    }

    /// <summary>执行一帧：消费卸载队列 → 快照成员 → 逐成员 Update（同帧共享同一 FrameContext）。</summary>
    protected void RunFrame(long now, double dt, int session)
    {
        DrainStopQueue();

        var members = SnapshotMembers();
        long tick = Interlocked.Increment(ref _tick);
        long dropped = Interlocked.Read(ref _dropped);
        double elapsed = (double)(now - _sessionStartTs) / Clock.TimestampFrequency;
        Volatile.Write(ref _elapsedSeconds, elapsed);

        // 实测 FPS EMA
        if (dt > 0)
        {
            double inst = 1.0 / dt;
            double prevEma = Volatile.Read(ref _actualFpsEma);
            double ema = prevEma <= 0 ? inst : prevEma + (inst - prevEma) * 0.1;
            Volatile.Write(ref _actualFpsEma, ema);
        }
        Volatile.Write(ref _lastDeltaSeconds, dt);

        var (globalTick, globalSeconds) = _engine.GlobalTimeAt(now);
        var frame = new FrameContext(dt, tick, dropped, elapsed, ThreadDisplayName,
                                     globalTick, globalSeconds);
        foreach (var member in members)
        {
            // F6：会话已在本帧中途结束（如某成员在 Update 里调了 engine.Stop()）→
            // 后面的成员绝不能再收到 Update（否则会“先 Stop 后 Update”，用到已释放资源）。
            if (!IsSessionActive(session)) { break; }
            if (member.IsRemoved) { continue; }
            if (!member.UpdateSystem.Enabled) { continue; }
            member.EnsureStarted();
            // Update 实耗：真实墙钟计时（Stopwatch），与注入的 TimeProvider 解耦，
            // 这样 FakeTimeProvider 测试的调度确定性不受插桩影响。
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            member.UpdateFrame(frame);
            member.RecordUpdateTime(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
    }

    /// <summary>拍边界消费卸载队列：对被卸载且已 Start 的系统在【执行线程】上调 Stop。</summary>
    protected void DrainStopQueue()
    {
        List<SystemEntry> pending;
        lock (_engine.SyncGate)
        {
            if (StopQueue.Count == 0) { return; }
            pending = DrainLocked();
        }
        foreach (var e in pending)
        {
            e.StopOnce();
        }
    }

    /// <summary>创建与调度档案匹配的循环子类（引擎归组工厂用）。</summary>
    internal static SystemLoop Create(UpdateEngine engine, Schedule schedule)
    {
        if (schedule.ThreadKind == ThreadKind.Main)
        {
            return new MainSystemLoop(engine, schedule);
        }
        if (schedule.IsUnthrottled)
        {
            return new UnthrottledWorkSystemLoop(engine, schedule);
        }
        return new WorkSystemLoop(engine, schedule);
    }
}
