namespace TickEngine;

/// <summary>
/// TickEngine：Unity MonoBehaviour Update() 风格的 .NET 8 调度引擎。
/// 系统按 (组名, 亲和, FPS) 归组共享循环：Worker 组由引擎自建后台循环线程
/// （PeriodicTimer 节拍，丢拍不补、实测 dt 自愈）；Main 组由引擎的节拍线程
/// 经 <see cref="IFrameDispatcher"/> 把整帧投到主/UI 线程执行（组级在途，低频不被饿死）。
/// 同一组内按注册序逐帧顺序调用；同帧所有系统共享同一 <see cref="FrameContext"/>。
/// </summary>
public sealed class UpdateEngine : IDisposable
{
    /// <summary>FPS 上限别名（Schedule 已强制；保留便于公开引用）。</summary>
    public const int MaxFps = Schedule.MaxFps;

    /// <summary>引擎纪元拍号刻度：GlobalTick 固定 1ms 一拍（所有循环共享同一时间对齐轴）。</summary>
    public const int GlobalTickRateHz = 1000;

    /// <summary>注册表锁：保护结构变更；帧执行持成员快照，不持本锁 → 变更永不撕裂正在执行的帧。</summary>
    private readonly object _gate = new();

    /// <summary>生命周期锁：串行化 Start/Stop（含 Join 阶段），杜绝"Stop 未收敛时 Start"造出死组。</summary>
    private readonly object _lifecycleGate = new();

    /// <summary>按注册序保存全部系统条目。</summary>
    private readonly List<SystemEntry> _entries = new();

    /// <summary>(组名, ThreadKind, Fps) → 循环组。同组名同频合并共享；异组名可让多条同频循环独立共存。</summary>
    private readonly Dictionary<(string Group, ThreadKind Kind, int Fps), SystemLoop> _groups = new();

    private readonly TimeProvider _timeProvider;

    private IFrameDispatcher? _dispatcher;
    private volatile bool _running;
    private bool _stopping;      // 正在执行 Stop 的收敛阶段（此期间 Start 明确拒绝）
    private bool _disposed;
    private long _epochStartTs;

    /// <summary>系统 Update 抛异常时触发（执行线程上；不影响引擎继续运行）。</summary>
    public event Action<UpdateSystem, Exception>? SystemFaulted;

    /// <summary>
    /// 宿主帧调度器异常或"收下帧却长期不执行"时触发（引擎侧诊断；引擎继续运行）。
    /// 用于暴露"宿主把帧吞掉"这类问题——以前它会静默把 Main 组钉死且无人知晓。
    /// </summary>
    public event Action<Exception>? DispatcherFaulted;

    /// <summary>触发系统异常事件（internal：条目/循环上报用，事件仅可在声明类内 invoke）。</summary>
    internal void RaiseSystemFaulted(UpdateSystem system, Exception ex) => SystemFaulted?.Invoke(system, ex);

    /// <summary>上报调度器层故障（internal：Main 循环的投递异常/看门狗回收）。</summary>
    internal void RaiseDispatcherFault(Exception ex)
    {
        try { DispatcherFaulted?.Invoke(ex); }
        catch { /* 事件处理器异常不扩散 */ }
    }

    /// <summary>构造引擎。</summary>
    /// <param name="timeProvider">时间源（默认系统时钟；测试可注入 FakeTimeProvider）。</param>
    public UpdateEngine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>主/UI 线程调度器（注册 Main 亲和系统前必须设置；Start 前校验）。</summary>
    public IFrameDispatcher? Dispatcher
    {
        get { lock (_gate) { return _dispatcher; } }
        set { lock (_gate) { _dispatcher = value; } }
    }

    /// <summary>引擎纪元当前刻度（1ms 一拍；未 Start 为 0）。</summary>
    public long GlobalTick => _running ? GlobalTimeAt(_timeProvider.GetTimestamp()).Tick : 0;

    /// <summary>引擎纪元当前单调秒（未 Start 为 0）。</summary>
    public double GlobalSeconds => _running ? GlobalTimeAt(_timeProvider.GetTimestamp()).Seconds : 0;

    /// <summary>引擎是否在运行。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 当前循环组数（诊断用）：成员全部迁移/卸载的 Worker 组会被回收，故该值可观察"是否遗留空组"。
    /// </summary>
    public int GroupCount
    {
        get { lock (_gate) { return _groups.Count; } }
    }

    /// <summary>按注册序返回全部系统的运行时快照。</summary>
    public IReadOnlyList<SystemView> Systems
    {
        get
        {
            lock (_gate)
            {
                var views = new SystemView[_entries.Count];
                for (int i = 0; i < _entries.Count; i++)
                {
                    views[i] = _entries[i].ToView();
                }
                return views;
            }
        }
    }

    /// <summary>
    /// 注册系统。可在 Start 前后随时调用；运行中注册在所属循环的下一拍边界生效。
    /// 同一系统重复注册（含已注册到另一个引擎实例）抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Register(UpdateSystem system, Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(schedule);
        ValidateSchedule(schedule);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.Any(e => ReferenceEquals(e.UpdateSystem, system)))
            {
                throw new InvalidOperationException($"系统已注册: {system.Name}");
            }
            if (system.Engine is { } owner && !ReferenceEquals(owner, this))
            {
                throw new InvalidOperationException(
                    $"系统已注册在另一个引擎实例上: {system.Name}（请先在该引擎上 Unregister）");
            }
            if (schedule.ThreadKind == ThreadKind.Main && _dispatcher is null)
            {
                throw new InvalidOperationException("注册 Main 亲和系统前必须先设置 UpdateEngine.Dispatcher");
            }

            var entry = new SystemEntry(this, system, schedule);
            _entries.Add(entry);

            // 先填元数据，再让循环线程有机会看到本条目：否则运行中注册时，
            // 用户的 Start() 可能读到 Engine/Schedule == null。
            system.Engine = this;
            system.Schedule = schedule;

            var group = GetOrCreateGroup(schedule);
            group.AddMember(entry);
            if (_running) { group.StartRunner(); }   // 成员就位后再起线程（空组会自我回收）
        }
    }

    /// <summary>
    /// 卸载系统。运行中卸载：Stop() 在所属循环执行线程的下一拍边界被调用一次
    /// （正在执行的那一拍最多再多跑一次 Update，与 Unity 的 Destroy 延迟语义一致）。
    /// </summary>
    public bool Unregister(UpdateSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        lock (_gate)
        {
            int idx = _entries.FindIndex(e => ReferenceEquals(e.UpdateSystem, system));
            if (idx < 0) { return false; }
            var entry = _entries[idx];
            _entries.RemoveAt(idx);

            entry.MarkRemoved();
            if (entry.Owner is { } loop)
            {
                loop.RemoveMember(entry);
                entry.StopQueuedFor = loop;
                loop.StopQueue.Add(entry);   // 拍边界由执行线程消费
            }

            // 只清理仍指向本引擎的元数据（避免踩掉另一个引擎实例的注册信息）
            if (ReferenceEquals(system.Engine, this)) { system.Engine = null; }
            if (ReferenceEquals(system.Schedule, entry.Schedule)) { system.Schedule = null; }
            return true;
        }
    }

    /// <summary>启用/停用系统（即时翻转；下一个成员起跳过）。</summary>
    public void SetEnabled(UpdateSystem system, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.Enabled = enabled;
    }

    /// <summary>
    /// 修改系统调度档案（换线程亲和或 FPS）。运行中迁移在目标组下一拍边界生效；
    /// 源组正在执行的一拍最多再多跑一次 Update。返回是否找到该系统。
    /// </summary>
    public bool ChangeSchedule(UpdateSystem system, Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(schedule);
        ValidateSchedule(schedule);

        lock (_gate)
        {
            int idx = _entries.FindIndex(e => ReferenceEquals(e.UpdateSystem, system));
            if (idx < 0) { return false; }
            var entry = _entries[idx];
            if (entry.Schedule == schedule) { return true; }
            if (schedule.ThreadKind == ThreadKind.Main && _dispatcher is null)
            {
                throw new InvalidOperationException("迁移到 Main 亲和前必须先设置 UpdateEngine.Dispatcher");
            }

            var target = GetOrCreateGroup(schedule);
            if (entry.Owner is { } old && !ReferenceEquals(old, target))
            {
                old.RemoveMember(entry);
            }
            target.AddMember(entry);

            entry.Schedule = schedule;
            system.Schedule = schedule;
            if (_running) { target.StartRunner(); }
            return true;
        }
    }

    /// <summary>
    /// 启动引擎：为每个含系统的组创建循环线程（Worker 自建；Main 经 Dispatcher 投递）。
    /// 单次引擎实例可 Stop 后再次 Start（每组重建线程，系统 Start() 在新会话首拍重新调用）。
    /// 正在停止中（Stop 尚未返回）调用会抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_running) { throw new InvalidOperationException("引擎已在运行"); }
                if (_stopping) { throw new InvalidOperationException("引擎正在停止中，请等 Stop() 返回后再 Start()"); }
                if (_entries.Count == 0) { throw new InvalidOperationException("没有已注册系统"); }
                if (_groups.Values.Any(g => g.Schedule.ThreadKind == ThreadKind.Main) && _dispatcher is null)
                {
                    throw new InvalidOperationException("存在 Main 亲和组，但未设置 UpdateEngine.Dispatcher");
                }

                foreach (var entry in _entries) { entry.ResetSession(); }
                _running = true;
                _epochStartTs = _timeProvider.GetTimestamp();
                foreach (var group in _groups.Values)
                {
                    group.StartRunner();
                }
            }
        }
    }

    /// <summary>
    /// 停止引擎（同一时刻只有一个 Stop 真正收敛）：
    /// 持注册表锁把全部组的会话令牌失效（杜绝与 Start 交错造出"无线程的活动组"）→
    /// Join 各循环线程（超时 = max(100ms, 2×周期+200ms)；调用线程本身是某组循环线程时跳过 Join）→
    /// 快照条目后在<b>锁外</b>逐个调用用户 Stop()。
    /// 注意：不要在 Worker 系统自己的 Update 内调用 Stop()（Join 自身线程不成立），此时 Stop 只失效会话，
    /// 系统应在下一拍自行退出。
    /// </summary>
    public void Stop()
    {
        List<SystemLoop> groups;
        bool callerIsLoopThread;

        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (!_running) { return; }        // 幂等：从未启动 / 已停
                _running = false;
                _stopping = true;
                groups = _groups.Values.ToList();
                foreach (var group in groups) { group.RequestStop(); }   // 锁内先失效全部会话
                callerIsLoopThread = IsCurrentThreadLoopThread(groups);
            }

            if (!callerIsLoopThread)
            {
                foreach (var group in groups) { group.RequestStopAndJoin(); }
            }

            SystemEntry[] snapshot;
            lock (_gate)
            {
                foreach (var group in _groups.Values) { group.DrainStopQueueNow(); }
                snapshot = _entries.ToArray();
            }

            // 用户 Stop() 在锁外调用：回调里 Register/Unregister 不会撞集合版本，也不会与引擎锁互锁；
            // 单个回调抛异常也不阻断其余系统的收尾。
            foreach (var entry in snapshot)
            {
                try { entry.StopOnce(); }
                catch { /* 用户 Stop 异常不扩散 */ }
            }

            lock (_gate) { _stopping = false; }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) { throw new ObjectDisposedException(nameof(UpdateEngine)); }
    }

    private static bool IsCurrentThreadLoopThread(List<SystemLoop> groups)
    {
        var current = Thread.CurrentThread;
        return groups.Any(g => g.IsRunnerThread(current));
    }

    // ---- 供 internal 循环/条目访问的最小面（不扩大公共 API）----

    /// <summary>引擎单调时钟（internal：调度内部使用）。</summary>
    internal TimeProvider Clock => _timeProvider;

    /// <summary>引擎注册表锁（internal：拍边界/队列操作使用）。</summary>
    internal object SyncGate => _gate;

    /// <summary>
    /// 某个 Worker 组已无成员时把它从注册表摘掉并让它的循环退出（由该组自己的循环线程调用）。
    /// 返回 true 表示"本循环应当退出"。主线程组不回收（避免与在途门控纠缠）。
    /// </summary>
    internal bool PruneEmptyLoop(SystemLoop loop)
    {
        if (!_running) { return true; }        // 引擎已停：本循环本就该退出
        lock (_gate)
        {
            if (!_groups.TryGetValue(loop.Key, out var current) || !ReferenceEquals(current, loop))
            {
                return true;                   // 已被替换/摘除：本循环退出
            }
            if (loop.HasMembers) { return false; }
            _groups.Remove(loop.Key);
        }
        loop.RequestStop();                    // 自线程调用：只失效会话，RunTimedLoop 随即退出
        return true;
    }

    /// <summary>取循环组；不存在则创建（不在锁内起线程：由调用方在成员就位后 StartRunner）。引擎锁内调用。</summary>
    private SystemLoop GetOrCreateGroup(Schedule schedule)
    {
        var key = (schedule.GroupName, schedule.ThreadKind, schedule.Fps);
        if (!_groups.TryGetValue(key, out var group))
        {
            group = SystemLoop.Create(this, schedule);
            _groups.Add(key, group);
        }
        return group;
    }

    private static void ValidateSchedule(Schedule schedule)
    {
        if (schedule.ThreadKind == ThreadKind.Main && schedule.Fps <= 0)
        {
            throw new ArgumentException("Main 亲和必须固定 FPS（≥1）", nameof(schedule));
        }
        if (schedule.Fps > MaxFps)
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), $"FPS 超过上限 {MaxFps}（周期低于 1ms）");
        }
    }

    /// <summary>引擎纪元换算：给定时间戳 → (1ms 刻度拍号, 单调秒)。需会话运行中调用。</summary>
    internal (long Tick, double Seconds) GlobalTimeAt(long timestamp)
    {
        double seconds = (double)(timestamp - _epochStartTs) / _timeProvider.TimestampFrequency;
        long tick = (long)(seconds * GlobalTickRateHz);
        return (tick, seconds);
    }
}
