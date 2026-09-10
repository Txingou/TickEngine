# 0002 — TickEngine 架构图与运行流程图

- 日期：2026-09（实验验证后补；组名归组已同步）
- 依据：当前实现（`src/TickEngine`）+ 决策记录 0001（Q1–Q20 语义均已落地）

Mermaid 在 GitHub / VS Code（Markdown Preview Mermaid Support）下原生渲染。
类名/方法名保留英文以对应代码，说明文字用中文。

## 〇、归组键：组名 + 亲和 + FPS

引擎按 **(组名, 线程亲和, FPS)** 归组：同组共享一条循环节拍流；**异组名可让多条同频
Worker 循环独立共存**（独立线程、独立丢拍统计）。组名默认 `"Default"`（未命名时归入；
空/空白 → Default；Trim；大小写敏感；≤32 字符）。
命名示例：`Schedule.Worker(100, "ServoA")` 与 `Schedule.Worker(100, "ServoB")`
= 两条独立的 100Hz 后台循环，互不拖累。

## 一、架构图：分层 + 线程映射

```mermaid
flowchart TB
    subgraph host["宿主层（UI 框架无关 —— Demo = WinForms / Avalonia / Console 三宿主）"]
        UI["主/UI 线程\n(消息泵 / 控制台泵)\n执行投递来的整帧"]
        disp["IFrameDispatcher 实现\nTryPost = 纯投递\n(BeginInvoke / Dispatcher.Post / 队列)\n忙检测与在途上限已上移到组级"]
        app["宿主业务\n(弹球世界 / 探针表格轮询 SystemView)"]
    end

    subgraph engine["核心库 TickEngine (net8.0)"]
        eng["UpdateEngine\n(注册表 _entries / 组字典 _groups / _gate)"]

        subgraph loops["循环组 SystemLoop 类族 —— 键 = (组名, 亲和, FPS)"]
            Lw1["Worker / Physics / 100Hz\n(WorkSystemLoop: 自建线程 + PeriodicTimer)"]
            Lw2["Worker / UiStats / 10Hz\n(异组名 → 独立线程，互不拖累)"]
            Lw3["Worker / Default / 10Hz"]
            Lm["Main / Default / 30Hz 节拍线程\n(到拍 → 调 Dispatcher.TryPost)"]
        end

        stat["组级统计: TickNumber / Dropped /\nActualFps(EMA) / LastDeltaSeconds\n(Interlocked/Volatile 发布)"]
    end

    subgraph bhv["系统层 UpdateSystem"]
        B1["BallPhysicsSystem\n组 Physics / Worker 100Hz"]
        B2["UiStatsPublisher\n组 UiStats / Worker 10Hz\n(读 engine.Systems 发快照)"]
        B3["UiCanvasRenderSystem\n组 Default / Main 30Hz\n(只请求重绘，不读 Systems)"]
    end

    app --> UI
    Lm -- "到拍 → TryPost(整帧)" --> disp
    disp -- "投递到主/UI 线程" --> UI
    UI -- "在 UI 线程执行 RunFrame\n→ 组内系统依次 Update" --> B3

    eng --> loops
    Lw1 --> B1
    Lw2 --> B2
    Lw3 -. "Default 组亦按 (组名,亲和,FPS) 归组" .-> Lm
    B1 -. "volatile 快照发布" .-> UI
    B2 -- "读 engine.Systems 发快照" --> stat

    classDef core fill:#eef4ff,stroke:#2b5bd7
    class engine,eng,loops,Lw1,Lw2,Lw3,Lm,stat core
```

要点：
- **线程映射**：`Worker` 循环 = 引擎自建后台线程；`Main` 循环的**节拍线程只负责"到点 TryPost"**，真正的 `Update` 经 dispatcher 在 UI 线程执行（图 1 虚线投递路径）。
- **归组与共存**：组键 = (组名, 亲和, FPS)。同为 Worker 100Hz 的两个系统只要组名不同 → **两条独立循环线程**，一个睡死另一个的丢拍/帧间隔完全不受影响；此外，成员全部迁移/卸载的 Worker 组会被回收（`UpdateEngine.GroupCount` 可观察）。
- **同帧共享**：同一 (组名,亲和,FPS) 组 = 一条节拍流，组内一拍串行跑完，共享同一 `FrameContext`。
- **组级在途（v2）**：`IFrameDispatcher` 为纯投递；每组至多一帧在 UI 队列（组内 CAS 在途标志，执行完释放），多 Main 组帧并存串行执行，低频组不被饿死。

## 二、运行流程图：引擎生命周期 + 一拍主路径

```mermaid
flowchart TD
    Start([Register 系统 × N]) --> HasDisp{含 Main 亲和?}
    HasDisp -- 是 --> NeedDisp{Dispatcher 已设置?}
    NeedDisp -- 否 --> Err1["抛 InvalidOperationException"]
    NeedDisp -- 是 --> Go
    HasDisp -- 否 --> Go
    Go["engine.Start()\n(校验 → 重置会话 → 每组 StartRunner)"] --> Run{引擎运行中}

    subgraph perLoop["每组循环线程独立执行"]
        Run --> Beat["等待节拍\nPeriodicTimer(1/Fps)\n或 Stop 时 Dispose 唤醒"]
        Beat --> Alive{会话存活?}
        Alive -- 否 --> Exit
        Alive -- 是 --> Kind{Worker 还是 Main?}
        Kind -- Worker --> WRun["线程内 RunFrame(now, dt)"]
        Kind -- Main --> Post["Dispatcher.TryPost(整帧)"]
        Post -- 被拒 --> Drop["Dropped++ (忙 → 丢拍)"]
        Post -- 接受 --> MExec["UI 线程执行 RunFrame(now, dt)"]
        Drop --> Beat
    end

    WRun --> Done{"更新中引擎被 Stop?"}
    MExec --> Done
    Done -- 否 --> Beat
    Done -- 是 --> Exit

    Exit["退出循环线程"] --> StopSeq["engine.Stop()\n(每组 RequestStopAndJoin →\n线程退出后 DrainStopQueue\n→ 各系统 StopOnce ×1)"]
    StopSeq --> End([可再次 Start：新会话])
```

要点（对应决策）：
- **丢拍不补**：Worker 迟到合并 → `Dropped++`，不补帧；`dt` 实测，系统自愈。
- **Main 组级在途（v2）**：每组至多一帧在 UI 队列（组内 CAS 在途标志，执行完释放）；
  节拍到而本组上一帧未执行完 → 本拍丢。不同组帧可并存串行执行 → 低频组不被高频组饿死。
- **拍边界**：运行中的注册/卸载/改速入队，在下一拍 `RunFrame` 开头消费。
- **Stop**：Dispose 计时器强制唤醒 `WaitForNextTickAsync` → Join（超时 = max(100ms, 2×周期+200ms)）→ 调用线程收尾。

## 三、一拍内部细节：RunFrame 放大图

```mermaid
flowchart TB
    Enter(["RunFrame(now, dt) — 拍已到\n(Worker: 循环线程内 / Main: UI 线程内)"])
    Enter --> Drain["① 拍边界变更\n消费 StopQueue → 对被卸载且已 Start 的系统\n调 StopOnce(执行线程上)"]
    Drain --> Snap["② 快照成员\n(持 _membersGate 复制数组，之后不持锁)"]
    Snap --> Tick["③ 组级统计先行\nTickNumber++ / 读 Dropped /\nElapsed / dt>0 时更新 ActualFps(EMA)"]
    Tick --> Ctx["④ 构造共享 FrameContext\n(dt, tick, dropped, elapsed, threadName)"]

    Ctx --> Loop{"⑤ 对每个成员(member)\n(快照数组，注册序)"}
    Loop -- 下一成员 --> ChkRemoved{IsRemoved?}
    ChkRemoved -- 是 --> Loop
    ChkRemoved -- 否 --> ChkEn{Enabled?}
    ChkEn -- 否 --> Loop
    ChkEn -- 是 --> Start["EnsureStarted\n(会话内首次 → UpdateSystem.Start ×1)"]
    Start --> T0["计时 t0 = Stopwatch.GetTimestamp()\n(真实墙钟, 与假时钟解耦)"]
    T0 --> Upd["member.UpdateFrame(frame)\n→ UpdateSystem.Update 若抛异常:\nFaults++ + SystemFaulted 事件\n(隔离, 引擎继续)"]
    Upd --> T1["Δt = Stopwatch - t0\n→ RecordUpdateTime:\nLastUpdateTicks = Δt\nMaxUpdateTicks = max(_, Δt)"]
    T1 --> Loop
    Loop -- 全部跑完 --> Out(["拍结束 → 等下一拍\n(期间可被 Stop 打断)"])

    style T0 fill:#fff2cc
    style T1 fill:#fff2cc
```

要点：
- **⑤ 顺序 = 注册序（FIFO）**，先注册先跑，永不重排（Q20）。
- **计时范围**：只包 `UpdateFrame`（`Start()` 不计入）；异常路径也计入（抛异常的系统同样消耗时间）。
- **耗时字段**：`SystemView.LastUpdateSeconds / MaxUpdateSeconds`，每系统独立；探针表格「最近Update / 最大Update」两列可见，窗口自驱 **0.5s（2Hz）** 刷新。
- 丢拍/拍号/实测 FPS 是**组级**指标；耗时是**系统级**指标——表里同组多行会看到相同丢拍、不同耗时。
