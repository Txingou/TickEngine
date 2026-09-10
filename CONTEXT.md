# TickEngine 上下文（Unity 风格 Update 调度引擎）

本上下文描述一个独立的 .NET 8 调度库实验：把 Unity MonoBehaviour 的 `Update()` 心智模型搬到普通 C#/.NET（命名致敬 ECS 的 System，但保持有状态单实例系统语义，不引入 Entity/组件/查询），用于“主/UI 线程固定节拍更新 + 各自独立速率的后台线程更新”两类调度需求。仓库含核心库 `TickEngine`（net8.0 可移植）、开发期性能探测器 `TickEngine.Probe.AvaloniaApp`（net8.0，Avalonia 应用：可独立运行，也可被宿主引用后由探针在后台线程启动）、三个演示宿主（WinForms 画布壳 / AvaloniaUIDemo / ConsoleDemo）。本实验源自一个私有外骨骼控制工程，但**自包含、不含产品代码**：本目录就是开源仓库的根，本文件即根上下文。

## Language

**系统 (UpdateSystem)**:
被引擎调度、可在 `Update` 中逐拍执行逻辑的单元；生命周期含 `Start/Update/Stop` 与 `Enabled` 开关。
_Avoid_: 组件、脚本、任务、服务

**调度档案 (Schedule)**:
系统注册时声明的“组名 + 线程亲和 + 目标 FPS”元数据，决定系统归属哪个循环组（引擎按 (组名,亲和,FPS) 归组）。
_Avoid_: 配置、参数

**线程亲和 (ThreadKind)**:
`Worker` = 引擎自建后台循环线程执行；`Main` = 引擎节拍线程投递、宿主搬到主/UI 线程执行。
_Avoid_: 线程池、Task、async

**循环组 (SystemLoop)**:
同 (组名, 线程亲和, FPS) 的系统共享的一条节拍流；一拍内按注册序串行跑完该组全部系统，同帧共享同一帧上下文。
实现为 internal 类族：`SystemLoop`（抽象基类）→ `MainSystemLoop`（投递）/ `WorkSystemLoop`（节拍直跑）
→ `UnthrottledWorkSystemLoop`（Work 特化，无节拍忙跑）。
_Avoid_: 线程池、调度器队列

**组名 (GroupName)**:
循环组的第一维键：未命名归入 `Default` 组；给同频 Worker 起不同组名即可让它们独立成组（独立线程、独立丢拍统计）。
_Avoid_: 分区、命名空间、标签

**帧上下文 (FrameContext)**:
一拍内传给每个系统的元数据：实测间隔、拍号、累计丢拍、流逝时间、线程名。
_Avoid_: 参数对象、delta

**丢拍 (Dropped)**:
循环因迟到/宿主忙而跳过的拍数；只计数不补拍（引擎不攒帧追赶）。按成因分两型（见下两条）。
_Avoid_: 卡顿、超时、错过

**丢拍·迟到型 (Worker)**:
后台循环在上一拍 Update 尚未跑完时 PeriodicTimer 合并了中间拍 → 按 wall-gap 计丢拍。
_Avoid_: catch-up、固定步长补帧

**丢拍·在途型 (Main / 组级在途)**:
每个 Main 组持自己的在途标志（至多一帧在 UI 队列）；节拍到而本组上一帧未执行完 → 本拍丢。
不同组的帧可并存排队串行执行，低频组不被高频组饿死。v2 取代共享忙标志。
_Avoid_: 忙标志共享、饿死

**组级在途 (per-loop in-flight)**:
Main 组投递纪律：CAS 置 1 → UI 执行完 finally 释放；保证每组至多一帧排队、绝不堆积。
_Avoid_: 全局单槽、一次性多帧入队

**丢拍不补**:
丢掉的拍不靠“本拍多跑几次”追赶；靠实测间隔让系统自愈（如物理按真实 dt 积分）。
_Avoid_: catch-up、固定步长补帧

**主线程帧调度器 (IFrameDispatcher)**:
核心库与 UI 宿主之间的边界；v2 起为**纯投递**（不忙检测），丢拍/在途管理上移到循环组。
_Avoid_: UI 注入核心库、跨线程访问控件

**引擎纪元 (Engine Epoch)**:
引擎 Start 时的单调时间基准；所有循环共享同一时间轴。GlobalTick = 纪元起 1ms 一拍，
GlobalSeconds = 纪元起单调秒；任何一拍从 FrameContext 读到的都是同一时刻。
_Avoid_: 每循环本地时间、最快组执行计数

**实测间隔 (DeltaTime)**:
两次实际执行之间的墙钟间隔；迟到的那一拍明显变大，系统可感知“自己迟到了”。
_Avoid_: 固定步长、标称间隔

**异常隔离 (Fault)**:
单个系统 `Update` 抛异常 → 计数 + 事件上报 + 本拍跳过该系统，引擎与其他系统继续运行。
_Avoid_: 崩溃、停止引擎

**更新耗时 (UpdateDuration)**:
单个系统一次 `Update` 调用的实际墙钟耗时（Stopwatch 实测，与调度时间轴/注入时钟解耦）；以“最近一拍 + 本会话最大”发布。
_Avoid_: 帧间隔、调度开销、CPU 占用率

**异常现场 (LastException)**:
系统最近一次异常的异常对象（类型/Message/StackTrace）；新会话清空、系统恢复后保留到下一次异常覆盖。
与累计计数 `Faults` 互补：计数回答“炸过几次”，现场回答“最近炸的是什么、炸在哪（file:line）”。
_Avoid_: 异常历史列表、只留 Message 字符串、按计数代替现场

**拍边界生效 (tick boundary)**:
运行中的注册/卸载/改调度请求在所属循环下一拍开始时应用，绝不撕裂正在执行的帧。
_Avoid_: 即时生效、中断帧

**Main 系统共享 UI 线程 (shared UI thread)**:
所有 Main 亲和系统共用同一条主/UI 线程执行帧；某系统在 Update 里做重活会以丢拍形式挤压同线程其他 Main 系统。
_Avoid_: Main 系统内做全表重建/长 I/O/Sleep 等重活（重活放 Worker）

**执行真相 (execution truth)**:
排查丢拍时以 `TickNumber` 增量为准；`ActualFps(EMA)` 与帧间隔会被"挤压解除后的 burst 假样本"污染，不可单独作证据。
_Avoid_: 拿实测 Hz > 目标 Hz 断言"跑太快"

**探针会话 (ProbeSession)**:
一次装配：把探测系统（UiStats/BeatLog，Worker 亲和）注册进宿主引擎并持有 UI 侧探针；
统一窗口入口（Open/Close/Toggle/IsOpen）打开 Avalonia 探针窗——宿主自带 Avalonia 就用它的 UI 线程，
否则由探针在后台线程启动常驻探针应用；呈现代码不必感知宿主是谁。
_Avoid_: 每宿主各自实现一套探针装配；宿主里再起第二个 Avalonia 应用

**常驻探针应用 (keep-alive probe app)**:
非 Avalonia 宿主下由探针自己在后台线程启动的 Avalonia 应用：`ShutdownMode = OnExplicitShutdown`、
无主窗，关窗只关窗口、UI 线程继续泵，可反复开/关探针窗。启动是**进程级一次性**的，失败/被关掉后不再重启。
_Avoid_: 每次开窗重启应用；把启动异常抛给宿主（未处理会拆掉宿主进程）

**控制台主线程泵 (console pump)**:
控制台宿主无系统消息泵时，把进程主线程当“UI 线程”：`ConsoleFrameDispatcher` 收 Main 组整帧入队，
主循环 Drain + 按键轮询。Main 亲和在控制台宿主下照常成立（组级在途/丢拍语义不变）。
_Avoid_: 控制台里把所有系统都塞 Worker、放弃 Main 亲和演示
