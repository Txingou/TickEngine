namespace TickEngine;

/// <summary>
/// Worker 不限速循环（WorkSystemLoop 的特化，Q2=A）：覆盖骨架为“无节拍忙跑”，
/// 逐圈实测 dt 并直跑（无节拍即无丢拍会计）。慎用：忙循环烧 CPU，靠 Thread.Yield 让出。
/// </summary>
internal sealed class UnthrottledWorkSystemLoop : WorkSystemLoop
{
    public UnthrottledWorkSystemLoop(UpdateEngine engine, Schedule schedule)
        : base(engine, schedule)
    {
    }

    protected override void RunLoopBody(int session)
    {
        while (IsSessionActive(session))
        {
            if (!HasMembers && Engine.PruneEmptyLoop(this)) { break; }   // 空组自回收（同限速循环）
            long now = Clock.GetTimestamp();
            double dt = MeasureDelta(now);
            RunFrame(now, dt, session);
            Thread.Yield();   // 让出时间片，避免独占核心
        }
    }

    // 本类不跑 RunTimedLoop，OnBeat 无调用点；保持空实现闭合抽象契约。
    protected override void OnBeat(long now, int session)
    {
    }
}
