namespace TickEngine;

/// <summary>
/// Main（主/UI 线程）循环：引擎自建节拍线程，节拍 → 组级在途门控 → Dispatcher 投递整帧。
/// 组级在途（v2）：每组至多一帧在 UI 队列（CAS 置 1，UI 执行完 finally 释放），
/// 不同 Main 组的帧可并存串行执行 → 低频组不被高频组饿死；本组在途时新节拍 = 丢拍。
/// dt 由投递闭包在 UI 线程真正执行时实测（自愈），故不在节拍线程做间隔会计。
/// <para>
/// v3 加固（三处静默失败）：
/// ① <b>会话令牌</b>——闭包捕获投递时的会话号，会话已换代则整帧作废（迟到帧不得跑进新会话）；
/// ② <b>在途槽归属序号</b>——闭包只释放自己占的那一个槽，杜绝"迟到帧释放新会话的槽"导致双帧在途；
/// ③ <b>投递异常/看门狗</b>——TryPost 抛异常或宿主收下帧却永不执行（如控件已销毁）时，
///    不再让本组线程死掉或槽位永久卡死：抛异常按丢拍处理，长时间未回收则由看门狗强制释放。
/// </para>
/// </summary>
internal sealed class MainSystemLoop : SystemLoop
{
    /// <summary>在途（0=空闲；非 0 = 已占用的槽序号）。</summary>
    private int _frameInFlight;

    /// <summary>槽序号发生器：每次占槽自增，闭包据此判断自己是否仍是槽主。</summary>
    private int _slotSeq;

    /// <summary>当前槽被占用的时刻（Stopwatch 刻度；0 = 空闲）。</summary>
    private long _slotTakenTs;

    public MainSystemLoop(UpdateEngine engine, Schedule schedule)
        : base(engine, schedule)
    {
    }

    protected override void OnSessionStarted()
    {
        // 新会话清残留在途（旧会话的迟到闭包会因槽序号不匹配而不再释放/执行）
        Interlocked.Increment(ref _slotSeq);
        Interlocked.Exchange(ref _frameInFlight, 0);
        Interlocked.Exchange(ref _slotTakenTs, 0);
    }

    protected override void RunLoopBody(int session) => RunTimedLoop(session);

    protected override void OnBeat(long now, int session) => DeliverToMainThread(session);

    /// <summary>
    /// 组级在途投递：本组上一帧尚未在 UI 线程执行完 → 本拍丢弃（Dropped++）。
    /// 投递闭包在 UI 线程执行：判会话令牌 → 实测 dt → RunFrame → 仅当自己仍是槽主时释放槽。
    /// </summary>
    private void DeliverToMainThread(int session)
    {
        var dispatcher = Engine.Dispatcher;   // 快照：运行中换/清 Dispatcher 不得抛 NRE
        if (dispatcher is null)
        {
            CountDrop();
            return;
        }

        // F4 看门狗：槽被占太久（宿主收下帧却永不执行）→ 强制回收，避免本组永久卡死
        ReclaimStaleSlotIfNeeded();

        int slot = Interlocked.Increment(ref _slotSeq);
        if (Interlocked.CompareExchange(ref _frameInFlight, slot, 0) != 0)
        {
            CountDrop();                      // 本组仍有一帧排队/执行中 → 本拍丢
            return;
        }
        Volatile.Write(ref _slotTakenTs, System.Diagnostics.Stopwatch.GetTimestamp());

        bool accepted;
        try
        {
            accepted = dispatcher.TryPost(() =>
            {
                try
                {
                    // 会话令牌不匹配 = 迟到帧（Stop 后又被 Start 换代）→ 整帧作废，绝不在新会话里执行
                    if (!IsSessionActive(session)) { return; }
                    long now = Clock.GetTimestamp();
                    double dt = MeasureDelta(now);
                    RunFrame(now, dt, session);
                }
                finally
                {
                    ReleaseSlotIfOwner(slot);
                }
            });
        }
        catch (Exception ex)
        {
            // 宿主 TryPost 抛异常（如 BeginInvoke 在句柄销毁时抛）：按"投递失败"处理，
            // 不让异常杀死本组节拍线程（旧实现会逃到 Run() 里把整条循环判死）。
            ReleaseSlotIfOwner(slot);
            CountDrop();
            Engine.RaiseDispatcherFault(ex);
            return;
        }

        if (!accepted)
        {
            // 投递本身失败（宿主不可用）：释放槽并计一次丢拍
            ReleaseSlotIfOwner(slot);
            CountDrop();
        }
    }

    /// <summary>只在"槽序号仍是投递时那一个"时释放槽（迟到闭包不得动新会话的槽）。</summary>
    private void ReleaseSlotIfOwner(int slot)
    {
        if (Interlocked.CompareExchange(ref _frameInFlight, 0, slot) == slot)
        {
            Volatile.Write(ref _slotTakenTs, 0);
        }
    }

    /// <summary>
    /// 槽被占用超过 max(1s, 10×周期) 仍未回收 → 视为宿主吞帧，强制释放并计一次丢拍。
    /// 有界等待，避免"宿主收下帧却永不执行"把整个 Main 组永久钉死。
    /// </summary>
    private void ReclaimStaleSlotIfNeeded()
    {
        if (Volatile.Read(ref _frameInFlight) == 0) { return; }
        long taken = Interlocked.Read(ref _slotTakenTs);
        if (taken == 0) { return; }

        double heldSeconds = (double)(System.Diagnostics.Stopwatch.GetTimestamp() - taken)
                             / System.Diagnostics.Stopwatch.Frequency;
        double period = Schedule.Fps > 0 ? 1.0 / Schedule.Fps : 1.0;
        double limit = Math.Max(1.0, period * 10.0);
        if (heldSeconds < limit) { return; }

        // 换槽序号让旧闭包失去所有权，再释放
        Interlocked.Increment(ref _slotSeq);
        Interlocked.Exchange(ref _frameInFlight, 0);
        Volatile.Write(ref _slotTakenTs, 0);
        CountDrop();
        Engine.RaiseDispatcherFault(new TimeoutException(
            $"Main 组 {Schedule.GroupName} 的在途帧超过 {limit:F1}s 未被宿主执行，已强制回收（疑似宿主收下帧却未执行）"));
    }
}
