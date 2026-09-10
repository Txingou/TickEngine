namespace TickEngine;

/// <summary>
/// Worker 限速循环：引擎自建后台线程 + PeriodicTimer 节拍。
/// 每拍：本线程实测 dt → gap 丢拍会计（丢拍不补）→ 直跑 RunFrame。
/// </summary>
internal class WorkSystemLoop : SystemLoop
{
    public WorkSystemLoop(UpdateEngine engine, Schedule schedule)
        : base(engine, schedule)
    {
    }

    protected override void RunLoopBody(int session) => RunTimedLoop(session);

    protected override void OnBeat(long now, int session)
    {
        double dt = MeasureDelta(now);
        CountMissedFromGap(dt, TimeSpan.FromSeconds(1.0 / Schedule.Fps));
        RunFrame(now, dt, session);
    }
}
