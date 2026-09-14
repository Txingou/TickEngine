# 0001 — TickEngine 设计决策

- 日期：2026-09（实验开工）
- 状态：已采纳（经 Q1–Q20 逐题确认后实现）

> **本文是历史决策日志：后文取代前文。** 逐条决策按时间顺序排列，架构演进会以
> “v2 修正 / v3 加固 / Dn 新条目”的形式追加；若某条已被更晚的决策取代，其标题或正文会标注
> “已被 Dn 取代”。**要看当前实现，请读 `README.md` 与代码**，不要以本文中途的某条结论为准。
> 关键取代关系：D10/D11/D12（探针形态）→ **D13 取代**；D12（CLI 跳转）→ **D12a 取代**；
> D8（演示内容）中的 `TickEngine.Demo` / `UiRenderSystem` 等类名已在重构中移除。

## 背景

本实验源自一个私有外骨骼控制工程：那里的“循环”被手写成每处 `Thread + PeriodicTimer + while + Join`，
其 Unity 仿真侧则用 MonoBehaviour `Update()` 表达同样的同步控制逻辑。需要一套可在 .NET 8 里模拟
“Unity 每拍更新”心智、同时支持“不同速率后台线程”的抽象，先做独立实验验证，
将来可开源（GitHub + NuGet）。本仓库为该实验的独立开源形态，不包含产品侧代码。

## 为什么自研而不是直接上现成库

2026-09 调研结论（库名/许可为当时记录，版本与下载量未逐一留证，读者请自行核对现状）：
- **LogicLooper**（Cysharp，MIT）：每 `LogicLooper(fps)` = 一个线程跑固定 FPS 循环、同帧顺序执行多个 action；但无组件/生命周期/丢拍可观测语义，`TargetFrameRateOverride` 粒度受限。
- **MonoGame / OpenTK**：有 fixed-timestep 模型但绑定窗口/SDL，无 headless 复用；无“每系统独立后台线程”。
- **SetNet.Ticks**：概念最接近（按 ticker 注册不同速率），但几乎零采用，只当灵感。
- **Akka.NET / R3**：消息式/响应式节奏，无固定步长与丢拍语义。
- 结论：**没有任何库完整覆盖“主线程固定节拍 + 各自速率后台线程 + 组件生命周期”的组合**；循环侧用 LogicLooper 可以，系统分发侧本来就该自己写。故选 A（纯自研，LogicLooper 作 API 参考）。

## 决策点

### D1. 调度模型：按 (线程亲和, FPS) 归组合并循环
同档速率的 worker 系统共享一条后台循环线程、按注册序逐帧调用，而不是一系统一线程。
贴合 Unity“一个循环驱动多个组件”心智，线程数 = 速率档数。
- 已确认：Q16 = A（并补充调用顺序规则，见 D5）。
- 备选：一系统一线程（Q16=B）→ 弃：线程泛滥、失去同帧顺序性。
- **v2 修正（组名归组）**：组键扩展为 **(组名, 线程亲和, FPS)**。默认组 `Default`
  保持 D1 的合并语义；显式给同频系统起不同组名即可独立成组（独立线程/丢拍统计）——
  需要隔离的两条 100Hz 循环不再被迫挤一条线程。已确认：组名放 `Schedule`、
  Worker 与 Main 统一支持、空→Default、Trim、大小写敏感、≤32 字符（决策记录于对话轮次）。

### D2. Main 组 = 核心节拍线程 + 宿主 Dispatcher，丢拍保护落宿主
核心库只定义 `IFrameDispatcher.TryPost`；引擎自建节拍线程按 FPS 投整帧，投不进去即计丢拍。
- 核心库零 WinForms 依赖 → NuGet 干净；丢拍保护可审、可测（测试用可控 Dispatcher）。
- 已确认：Q1=A（WinForms 演示）、Q9=A（10 行实现方案通过审查）。
- 备选：核心库持 UI 定时器（Q9=C）→ 弃：WinForms 系统定时器 ~15.6ms 粒度无法稳定 60Hz。
- **v2 修正（见下一条）**：本节最初设想的“忙检测放在宿主、TryPost 忙则返回 false”**已废弃**——
  现在 `TryPost` 是**纯投递**，忙检测/在途上限上移到组级。请以 D2 的 v2 段与代码为准。
- **v2 修正（组级在途，取代共享忙标志）**：实测发现多个 Main 组共享单 `_pending` 时
  高频组赢者通吃、低频组被饿死（10Hz 组丢拍 ~99%、100Hz 组 ~3%）。改为：
  `IFrameDispatcher` 退化为**纯投递**（不再忙检测）；每个 Main Loop 持自己的组级在途标志
  `_frameInFlight`（CAS 0→1，UI 执行完 finally 释放）——每 Main 组至多一帧在 UI 队列，
  不同组的帧可并存串行执行，**低频组不再被共享忙标志饿死**；丢拍=本组在途时再到的节拍
  （或投递本身失败）。已确认：Q5=A（组级在途 + 允许并发排队）、Q6=A（三循环模板方法收敛）。

### D2a. 引擎纪元拍号（全局时间对齐轴）
所有循环共享同一单调时间轴：引擎 `Start` 时记录纪元起点，每次构造 `FrameContext` 时按
注入的 `TimeProvider` 现算 `GlobalTick`（固定 1ms 刻度 = `UpdateEngine.GlobalTickRateHz`）
与 `GlobalSeconds`（单调秒）。不绑定任何特定组的执行/丢拍 → 任何一拍读到的都是同一时刻。
- 已确认：Q7=A（引擎纪元而非“最快组累加”——后者会因启停/改频/卸载而失真）、
  Q8=拍号 + 纪元秒都进 `FrameContext`。

### D2b. Loop → SystemLoop 类族拆分（结构等价重构）
单一 `Loop`（Main/Worker/Unthrottled 三分支于一身）拆为继承层次（Q2=A 三层、Q3=A internal 独立文件）：
`SystemLoop`（抽象基类：成员/统计/StopQueue/线程生命周期/RunFrame/模板骨架）
→ `WorkSystemLoop`（PeriodicTimer 节拍，实测 dt + gap 会计 + 直跑）
→ `UnthrottledWorkSystemLoop : WorkSystemLoop`（Work 特化：覆盖骨架为无节拍忙跑）
→ `MainSystemLoop`（组级在途门控 + Dispatcher 投递）。
纯结构重构：公开 API、调度语义、测试断言零变化（当轮 30/30 全绿；当前套件 32 项）。

### D3. 丢拍语义：丢拍不补 + 实测 dt
迟到/宿主忙造成的丢拍只累计计数、不做 catch-up；`DeltaTime` 用实测墙钟间隔，迟到拍 dt 变大，
系统按真实 dt 积分即自愈（弹球演示）。不采用 Unity `FixedUpdate` 式固定步长累积器。
- 已确认：Q4=C、Q17=A。
- 理由：控制/仿真循环“丢拍不补比补拍安全”（现有手写代码也是这个系统）；累计器增加复杂度且改变时序可预测性。

### D4. 生命周期与 API 形态
- `UpdateSystem`：`Start / Update(in FrameContext) / Stop` + `Enabled`（volatile），Unity 味命名（Q10=A）。
- 调度档案注册时显式传 `Schedule(ThreadKind, Fps)`（Q11=A），不搞特性扫描魔法。
- `FrameContext` 携带：实测 DeltaTime、拍号、累计丢拍、流逝时间、线程名。
- 异常策略 = 记录 + 本拍跳过该系统 + `Faults` 计数 + `SystemFaulted` 事件，引擎继续（Q18=A）；
  这与真机安全代码“异常→Failed→停”的取舍不同：TickEngine 是通用调度实验，安全域语义留到落地层。

### D5. 调用顺序规则（Q20=A 的正式化）
- 同一循环组内：注册序（FIFO），先注册先跑，永不重排。
- 一拍 = 组内全部启用系统的顺序调用；同帧所有系统共享同一 `FrameContext`（同 dt/拍号/丢拍/全局拍号）。
- 一个 (组名,亲和,FPS) 组 = 一条节拍流；Main 组一个 Post 跑完整帧，不按系统拆分 Post
  （v2：每组至多一帧在 UI 队列，不同组的帧可并存串行执行）。
- 结构性变更（注册/卸载/改速/启停）在拍边界生效；`Enabled` 即时翻转、下一拍跳过。
- v1 不加 `Priority` 字段（可预测性 > 灵活度，真机代码无同循环排序需求）。

### D6. 线程模型与并发正确性
- 帧执行持“组内成员快照数组”，不持锁跑用户代码 → 结构性变更永不撕裂正在执行的帧。
- 统计（拍号/丢拍/实测FPS）Interlocked/Volatile 发布；系统 `Faults` 计数原子。
- 引擎 Stop：停节拍（Dispose 计时器强制唤醒 WaitForNextTickAsync）→ Join（超时 = max(100ms, 2×周期+200ms)）→ 调用线程收尾 Stop()。
- 已在 Worker 系统自身 Update 内调用 Stop() 属不支持用法（文档注明）：Join 自身线程不成立。

### D7. 时间与可测性
- `UpdateEngine(TimeProvider?)` 注入；Worker/Main 节拍均用 `PeriodicTimer(period, provider)`。
- 测试用 `FakeTimeProvider` 逐步推进，丢拍/不补拍/启停/迁移全部确定性可断（见 tests）。
- 已确认：Q13=A（xunit + FakeTimeProvider）、Q15=A（仅 net8.0，不给 PeriodicTimer 打 polyfill）。

### D8. 演示内容（最初的 TickEngine.Demo，WinForms —— 已被后续重构取代）
> 已被取代：`TickEngine.Demo` 这个项目名与 `UiRenderSystem` 等类名在后续拆分中都不存在了；
> 当前演示是 WinformDemo / AvaloniaUIDemo / ConsoleDemo 三个宿主 + 探针（见 D13/D14）。

“系统动物园 + 交接演示”：系统统计表（每系统目标Hz/实测HzEMA/拍号/丢拍/Faults/启停）+
弹球画布（物理在 Worker 100Hz 系统里积分、volatile 快照发布，UI 30Hz 系统读快照重绘——
直观复现“后台算、前台画 + 快照交接”），外加捣乱系统（异常隔离演示）。
- 已确认：Q12=X（弹球意象）、Q6=A + “更有意思的直观演示”。
- 不带外骨骼影子（Q2=A 自包含、开源时干净）。

### D9. 实机教训：Main 系统内不要做重活（UI 线程共享挤压）
现象：当时的 Demo 中有一个（现已删除的）统计表刷新系统（Main 10Hz）每次 Update 对 DataGridView 做**全表
`Rows.Clear()` + 逐行 `Add`**（重布局）。它自身 0 丢拍（它慢、10Hz 有余量），
但同处 UI 线程的 `UiCanvasRenderSystem`（Main 30Hz）被周期性挤压：
- 帧被推迟 → 组级在途满 → **持续丢拍**（TickNumber 每窗 +2 而非 +3 = 实际 20Hz）；
- 挤压解除瞬间 UI 积压帧 burst 执行 → **帧间隔出现 < 目标周期的假小样本**，
  实测 Hz(EMA) 被污染成 > 目标（38/90+，看起来"跑太快"，实为测量假象）。
- 最小复现（去掉刷表系统）与刷表系统空转（不刷表）两组均恢复正常
  （+3/窗、0 丢拍、帧间隔均值 33ms）→ 实锤"重活挤压"而非引擎 bug。

结论/约束（写入 CONTEXT，真机必守）：
1. **所有 Main 亲和系统共享同一条 UI 线程**——一个系统 Update 里做重活（全表重建、
   长 I/O、Sleep）会以丢拍形式挤压同线程的其他 Main 系统；引擎如实计数丢拍，
   **不负责**替你优化 UI 负载。
2. **重活放 Worker**；必须留在 Main 的 UI 刷新采用**原位更新**（行结构不变时
   `Rows[i].Cells[...].Value = ...`，仅内容变化；行数/结构变化才重建），而非每拍全表重建。
3. 丢拍排查铁律：`TickNumber` 增量是执行真相；`ActualFps(EMA)`/帧间隔会被
   burst 假样本污染，**不可单独用作"是否跑太快"的证据**。

## 后果

- 开源友好：核心库 net8.0 无 UI 依赖；WinForms / Avalonia / 控制台泵各自实现一个 `IFrameDispatcher`（20–50 行，含注释与错误处理）。
- 测试友好：**41 个 xunit 用例**（假时钟确定性 + 生命周期/在途门控加固回归）全绿；各演示宿主都有 `--smoke*` 自检可在真实消息泵下冒烟。
- 明确边界：库不做 `Priority`、不做固定步长累积器、Worker 系统内自我 Stop 只失效会话不 Join（文档与测试钉死这些语义）。

### D10. 工程分离：探测库与演示壳（TickEngine.Probe / WinformDemo / AvaloniaUIDemo）
> **已被 D13 取代**（探针从"类库 + WinForms 自托管"演进为"独立 Avalonia 应用"）。以下为当时形态，保留作决策史。

性能观测（统计表/CSV 日志/UI 线程探针）从单一 WinForms Demo 拆出为 `TickEngine.Probe`
（net8.0 类库，引用 Avalonia 12 作表格呈现；**开发期引用、上线前摘除 ProjectReference 即整体移除**）：
- `BeatLogSystem`（CSV 节拍日志）、`UiStatsPublisher`（读 Systems → 快照发布，替代被删除的刷表系统职责）、
  `UiThreadProbe`+`UiPaintStats`（原 UiProfiler 重构去 WinForms 依赖，绘制计时经 `IPaintTimingSink`
  由宿主画布挂接）、`ProbeWindow`（Avalonia DataGrid）。
原 Demo 瘦身为 `TickEngine.WinformDemo`（仅 WinForms 弹球画布壳），新增 `TickEngine.AvaloniaUIDemo`
（Avalonia 弹球画布，弹球/心跳演示系统按 Q9=B 两份独立）。当时同一 sln 五项目（现已演进）。
- 决策：Q1=B（Probe 带 Avalonia 直接做控件库，接受开发期专用语义）、Q2=B（表格在 Probe，AvaloniaUIDemo 只画球）、
  Q3=A（WinformDemo 最小画布壳）、Q8=A（宿主进程内引用 Probe 弹探针窗；独立进程无法探查）、Q9=B（演示系统复制两份）。

### D11. Probe 双后端 + 统一窗口入口（ConsoleDemo / WinForms 自托管探针）
> **已被 D13 取代**：`UiKind`/`ProbeUiKind`/`IProbeWindowHost` 都已删除，探针不再是 WinForms 自托管。
> 以下为当时形态与决策，保留作决策史（读者请以 D13 为准）。
控制台宿主（无任何 UI 框架）也要能弹探针 → `TickEngine.Probe` 改为**双 TFM**：
`net8.0`（纯采集 + Avalonia 呈现，不变）与 `net8.0-windows`（额外 `#if WINDOWS` 编译
**WinForms 自托管探针**：独立 STA 线程 + 自建消息泵 `Application.Run`，无需宿主 App/App.axaml/主题——
Q1 早前“控制台要带 Avalonia 主题资源”的前提被 G1=A 撤销）。
- 统一入口：`ProbeSession.OpenProbeWindow / CloseProbeWindow / ToggleProbeWindow / IsProbeWindowOpen`，
  形态由 `ProbeSessionOptions.UiKind`（`Auto/WinForms/Avalonia`）决定；Auto = 进程内已有
  Avalonia Application → Avalonia 呈现，否则 → WinForms 自托管（控制台/无 UI 宿主）。
  宿主可显式指定形态覆盖检测。内部 `IProbeWindowHost` + 工厂：Avalonia 宿主 Post 到
  Dispatcher.UIThread；WinForms 宿主自启 STA 线程。
- 呈现双后端共用行 VM `SystemViewRow`（Avalonia DataGrid / WinForms DataGridView 列语义一致）；
  窗口关闭即停自驱时钟，会话可再次 Open 重建。
- 新演示 `TickEngine.ConsoleDemo`（net8.0-windows Exe）：Worker 计算系统 + 独立 Worker 心跳
  + Main 文本屏系统（控制台无消息泵 → 进程主线程当泵：`ConsoleFrameDispatcher` 队列 +
  主循环 Drain + 按键轮询 = 控制台版“UI 线程”，Main 亲和语义照常成立）；
  按键：P 切探针（Auto→WinForms）、+/- 运行中改计算 FPS（`ChangeSchedule` 现场演示）、Q/Esc 退出。
  `--smoke`（引擎自检）/`--probe-smoke`（开→关 WinForms 探针全程断言）可自动化。
- AvaloniaUIDemo 同步切到统一入口（`UiKind.Auto` → Avalonia），并补 `--smoke-probe` 冒烟
  （开→关 Avalonia 探针全程断言）。
- 决策：G1=A（WinForms 自托管探针迁入 Probe，控制台不用引导 Avalonia）、G2=A（统一入口 +
  Auto 检测 + 显式 UiKind 覆盖）、G3=A（本轮 WinformDemo 不动，仅 ConsoleDemo 作新验证宿主）。
- 保留旧 WinForms 版本探针（网格列语义）为 Probe 的 WinForms 呈现后端；WinformDemo 保持画布壳。
- **v2 修正（Avalonia 呈现整体移除，唯一后端 = WinForms 自托管）**：Avalonia 宿主经自托管
  WinForms 探针同样可行且实测通过（AvaloniaUIDemo --smoke-probe 全程断言），故 `Probe` 收成
  单 TFM `net8.0-windows`：删除 Avalonia 包引用 / ProbeWindow(axaml) / AvaloniaProbeWindowHost /
  `ProbeUiKind` / `UiKind` 选项（单后端 = 选项失义）；呈现行 VM `SystemViewRow` 独供 WinForms。
  同时修复控件线程亲和 bug（Q1-A）：WinForms 窗体与 Timer 必须在消息泵线程内构造——
  窗体改在泵线程内 `new` + 就绪信号量，跨线程 Close/Activate 一律 BeginInvoke；
  `--probe-smoke`/`--smoke-probe` 增补“刷新增量断言”（窗口开着时 ΔRefreshCount ≥ 2），
  防止“窗口静态打开但从不刷新”类回归漏网。决策：Q5=A、Q6=A、Q7=A（WinformDemo 仍不动）、Q8=A
  （状态行标签改为「宿主UI线程CPU」，明确采集对象 = 被 CaptureUiThread 捕获的线程而非探针窗自身）。

### D12. 异常记录与溯源 + 探针日志面板（两件事）
异常原先只有累计计数 `Faults` 与 `SystemFaulted` 事件，异常对象被丢弃（`SystemEntry.CountFault`）→ 无法回答"最近炸的是什么、炸在哪"。
- **`SystemView.LastException`（`Exception?`）**：`SystemEntry` 保存最近一次异常并经 Volatile 发布；
  语义 = **新会话（Start→ResetSession）清空、系统恢复后保留**到下一次异常覆盖；`Faults` 保持整生命周期累计不变。
- **探针表格「异常消息」列**：`Message`（多行压一行）+ 首个 `file:line`，异常行标红；
  ~~**双击该列错误行 → 跳转源码**：`devenv /edit "<file>" /command "Edit.GoTo <line>"`~~（**已否决，见 D12a**：
  实测 VS 18 附着运行实例时 `/command` 根本不执行，于是只打开文件停在首行；现改用 DTE COM 精确跳转并回读落点）。
  事实核查（子代理实测）：本机只有 Visual Studio（VS 18 Professional 在运行；VS Code/Rider 未安装）；
  Debug + PDB 下 `Exception.StackTrace` 与 `new StackTrace(ex, true)` 均带绝对 `file.cs:line`，
  且经引擎故障隔离路径（后台线程/线程池/async）到达时用户帧的 file:line 完好。
  VS `/edit` 会附着到运行中实例、且 `/edit` 打开的文件是否已是活动文档无契约保证 → **best-effort**；
  无 file:line / 无 VS 时**降级为复制「类型+Message+完整堆栈」到剪贴板**（状态行提示）。
- **探针日志面板**：`ProbeSession.Attach` 安装 Console tee（`Out`→Info、`Error`→Error，一份照写原始流）+ 订阅
  `SystemFaulted`→Error；`Dispose` 还原 Console。环形缓冲 2000 条、窗口未开也缓冲、开窗回放；
  面板 = Avalonia `ListBox`（时间/级别/内容，Error 标红）+ 仅错误过滤 + 自动滚动 + 清空（当时写作 WinForms `ListView`，D13 迁移后为 ListBox）。
- **渲染与日志分道**：`ConsoleDemo` 文本屏（Main 15Hz 整块重绘）写的是 tee 安装前的原始 stdout，
  不进日志（否则每秒数百行重绘淹没真日志）。
- 决策：Q1=A（`LastException` 最小形态）、Q2=A（新会话清、恢复后保留）、Q3=A（Attach 装 tee/环形缓冲/回放）、
  Q4=A（渲染与日志分道）、Q5=A（上下分栏 + 日志清单三列 + 过滤/滚动/清空；当时写作 SplitContainer+ListView，D13 后为 GridSplitter+ListBox）、Q6=A（引擎异常主动写 Error，CSV 不加列）、
  Q7=A（VS `Edit.GoTo` 跳转 + 剪贴板降级）、Q8=A（标签「宿主UI线程CPU」）。
- 范围：只做这两件事——日志不落盘、无级别配置、无异常历史（只保留最近一次）、不动 BeatLog CSV、双击跳转只在系统表。
- 验证：xunit 32/32（新增 2 个 `LastException` 用例：持有/恢复保留、新会话清空）；ConsoleDemo `--smoke --probe-smoke`
  断言 `PROBE_OK` + 日志 `errors≥1/items>0` + `LastException` 有现场 + `file:line` 溯源解析 OK；
  AvaloniaUIDemo `--smoke-probe` 断言刷新 Δ≥2 且日志进面板。

### D12a. 跳转实现修正：CLI → DTE（"只到首行"缺陷）+ 日志面板双击跳转
实测发现 D12 的 CLI 跳转**只把文件打开、停在第 1 行**。独立复核（子代理在 VS 18 Professional 上做鉴别实验）给出了精确机制：
- VS 18 在"实例已在运行"时，`devenv.com /edit "<file>"` **会被执行**（文件打开并成为活动文档），但
  **`/command "Edit.GoTo N"` 根本不被执行**（`devenv.com` 退出码 **-1**）。
- 鉴别方法：先用 DTE 把目标文件光标预置到第 5 行，再跑同一条 CLI 命令 → 光标**仍是 5**（不是 30），
  且**其它所有已打开文档的光标逐字节未变** ⇒ 不是"命令打到了先前的活动文档"的竞态，也不是文档重载；
  "停在第 1 行"只是新加载文件的默认光标。3 次复现一致。
- 推论：CLI 既不能定位，也**不能靠"两段式 + 延迟"救**（Q1=B 因此不成立）。
- **主路径改为 DTE COM**：`Type.GetTypeFromProgID("VisualStudio.DTE.18.0"/17.0/16.0)` 取 CLSID →
  P/Invoke `oleaut32!GetActiveObject` 附到运行中的 VS → `ItemOperations.OpenFile(path)` → `Window.Activate()` →
  `ExecuteCommand("Edit.GoTo", line)` → **回读 `ActiveDocument.Selection.ActivePoint.Line` 验证落点**
  （`ProbeJumpResult` 带 `ActualLine`，状态行显示"已跳转 File.cs:42"，落点不符时如实标注）。
  实现注意：回读要包 try/catch——非文本文档（.csproj/.axaml/.resx）上 `doc.Selection` 会抛 `RuntimeBinderException`。
- 降级链：DTE 不可用（VS 未运行）→ `devenv /edit` 只打开文件（结果模式 `OpenedOnly`，状态行明说未定位行）→ 复制异常+堆栈到剪贴板。
- **日志面板双击跳转**：`ProbeLogEntry` 增加可选 `Exception`（引擎故障条目携带原始异常，精确定位）；
  Console 条目无异常 → 回退正则解析行文本里的**完整盘符路径** `X:\...\File.cs:line N`；都不行 → 复制该行日志。
- 决策：Q1=A（DTE 主路径 + CLI 兜底 + 剪贴板降级）、Q2=A（异常对象 + 文本兜底，双击任意行都尝试）、
  Q3=A（日志文本保留 `@ File.cs:77` 的人读形式，长路径靠异常对象而非摊在日志里）、Q4=A（VS 未运行不同步等待）。
- 实测证据：
  - 本仓库 `--jump-live` 端到端：`JUMP_LIVE=mode=DteLocated file=Program.cs line=229 actual=229`（回读落点 == 请求行号；行号随代码演进而变，此处为当轮记录）；
  - 独立复核（临时文档，已清理）：`DTE_GOTO want=40/55/7 → actual=40/55/7` 全精确、可重复、无 `RPC_E_CALL_REJECTED`；
    对照实验复现 CLI 缺陷；用后经 `Documents.Item(path).Close(vsSaveChangesNo)` 关闭，无遗留标签页、用户文档未受影响。

### D13. 探针形态再迁移：WinForms 自托管 → Avalonia 应用（TickEngine.Probe.AvaloniaApp）
D12/D12a 阶段探针是 "WinForms 库 + 自托管 STA 线程 + 自建消息泵"。改成 **Avalonia 应用**的动机：
Avalonia 呈现能力更强（DataGrid/主题/自带样式），且"在后台线程跑一个应用的 `Main`"这条路径被验证成立。
- **项目形态**：新建 `src\TickEngine.Probe.AvaloniaApp\`（WinExe + 可被引用，net8.0，Avalonia 12 + DataGrid），
  把旧 `TickEngine.Probe` 的**全部功能**迁入（采集/诊断/日志 tee/DTE 跳转/会话/行 VM），
  删除 WinForms 一切（窗体、自托管宿主、`UseWindowsForms`、WinForms `Clipboard`）；
  旧 `TickEngine.Probe` 项目与其目录一并删除。命名空间仍为 `TickEngine.Probe`（宿主 `using` 不变）。
- **宿主适配（实测约束驱动）**：
  - `AppBuilder.Setup()` 是**进程级一次性闩锁**：同进程起第二个 Avalonia 应用必抛
    `InvalidOperationException("Setup was already called on one of AppBuilder instances")`；
    `Configure<T>()` 两次返回不同实例，`Shutdown()` 也**不重置**——故 "每次都重启应用"不可行；
  - 裸线程跑 app 的 `Main` 若抛异常会**未处理而拆掉宿主进程**（exit `0xE0434352`）→ 启动体必须 try/catch；
  - 结论：**Avalonia 宿主 → 用宿主自己的 UI 线程开窗**（绝不启第二个应用）；
    **其他宿主 → 探针启动一个常驻应用**（`ShutdownMode = OnExplicitShutdown` + `MainWindow = null`，
    已实测可反复开/关窗且 UI 线程全程在泵），启动失败/已关机只记 Error、`IsProbeWindowOpen` 保持 false（不抛给宿主）。
- **窗口自带主题（实测）**：`<Window.Styles><FluentTheme/><StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"/></Window.Styles>`
  在**零 `Application.Styles`** 的宿主里也能出完整 DataGrid（对照实验：去掉后 `DataGrid.Template` 为 null、完全空白）
  ⇒ 宿主 `App.axaml` **无需任何改动**，这是"宿主零配合"的关键。
- **数据传递**：不做静态桥——窗口创建点就在探针内部（`ProbeUiHost` 经 `Dispatcher.UIThread` 建窗），
  直接把 `ProbeSession`/`UpdateEngine` 作为构造参数传进去；`null` 会话即"独立运行"展示模式（Q10=A 独立可运行、不崩）。
- **TFM 归一**：探针 `net8.0`；`AvaloniaUIDemo` 与 `ConsoleDemo` 回退 `net8.0` 并移除 `UseWindowsForms`；
  `WinformDemo` 保持 `net8.0-windows`（本轮不动，G3 延续）。
- **清理**：删除用户手工验证用的六个脚手架项目（`ConsoleApp1`/`WinFormsApp1`/`AvaloniaAppConsole`/`App1`(AvaloniaApp)/
  `App2`(AvaloniaApp1)/`MyApp`）及其 sln 条目——其中 `App2` 试图同进程起两个 Avalonia 应用（编译即失败，
  并曾拖垮 `TickEngine.sln` 构建）。
- 决策：Q1=A（全部迁入新项目并删旧库）、Q2=A（宿主自适应，Avalonia 宿主走自己的 UI 线程）、Q3=A（单例常驻应用）、
  Q4=A（直接传 `UpdateEngine`/会话，不用静态桥）、Q5=A（WinForms 全删、其余迁移）、Q6=A（六个脚手架全删）、
  Q7=A（`WinformDemo` 仍不动）、Q8=A（TFM 归一）、Q9=A（启动失败不外抛，只记 Error）、Q10=A（保留独立可运行、
  不公开裸 `ThreadMain`）、Q11=A（常驻线程 `IsBackground = true`）。
- 验证：sln 构建 0 警告 0 错误（当时 6 项目；D14 后仍为 6）；xunit 32/32；三宿主冒烟 —— 控制台宿主
  `PROBE_OK` + `JUMP_LIVE … actual=<请求行>` + `LOG_TEXT_RESOLVE=OK`；Avalonia 宿主（宿主 UI 线程路径）
  `PROBE_REFRESH_DELTA=3`、`PROBE_LOG total=2 items=2`、`SMOKE_OK`；独立运行 `--self-test` 开窗 1.5s 自动退出（exit 0）。
- 遗留观察：迁移期间有一次 `dotnet test` 报 1 例失败，但**未能复现**（随后 18 次连跑全绿，其中 12 次在 4 路 CPU 压力下），
  失败用例名因当时输出被截断而未捕获；测试里有若干依赖真实 `Thread.Sleep`/墙钟的用例是嫌疑对象，待其复现后定位并加固。
- 迁移后补的两处修正（都是实测踩出来的）：
  1. `devenv /edit` 兜底启动进程必须 `UseShellExecute = true`——否则 devenv 继承调用方 stdio，
     让"捕获输出"的调用方（自动化/终端管道）一直等管道关闭而**假死**（实测一次 300s 超时即由此产生）；
  2. `--jump-live` 在 VS 未运行（DTE 不可附加）时**跳过断言**而非判失败：新增只读探测
     `ProbeCodeJump.IsVisualStudioRunning()`，避免自检顺手启动一个 VS 实例（那也是上面假死的诱因）。

### D14. WinForms 宿主接入探针（三宿主对称）
此前 G3/Q7 一直把 `WinformDemo` 排除在探针之外（"保持纯画布壳"）。D13 之后探针已与 UI 框架解耦，
WinForms 宿主接入的成本降到"一个按钮 + 一次懒装配"，故补齐入口：
- `MainForm` 工具栏加「打开探针(Probe)」按钮 → `OpenProbe()`：**首次点击才** `ProbeSession.Attach`
  （不点就不采集、不写 `beat-log.csv`）→ `_canvas.AttachPaintSink(_probe.PaintStats)` →
  `_probe.CaptureUiThread()`（在 UI 线程调用 ⇒「宿主UI线程CPU」测的正是跑 Main 系统那条 WinForms UI 线程）→
  引擎在跑则 `StartSampling()` → `OpenProbeWindow()`。窗口跑在探针常驻应用的后台线程（WinForms 宿主不是 Avalonia 宿主）。
- **画布绘制计时**：WinForms `BallCanvas.OnPaint` 原先没有计时钩子 → 补上 `IPaintTimingSink`
  （`BeginPaint`/`EndPaint` 包住绘制体，约 5 行），使探针「绘制」栏在本宿主也有真实数据（此前只会显示"画布未上报"）。
- **关闭语义**：`FormClosed` = 停引擎 → `_probe?.Dispose()`（关探针窗；常驻应用若由探针启动一并 Shutdown）→ 不留孤儿窗/线程。
- **自动化**：新增 `--smoke-probe`（与另两宿主同语义）：开探针 → 断言 `IsOpen` + 刷新增量 ≥2 + 日志进面板 +
  异常现场（`SystemView.LastException`）→ 关 → 断言已关 → `SMOKE_OK`；冒烟时才注册"首拍抛一次"的
  `SmokeFaultSystem`（常态演示不引入报错，Q5=A）。
  踩坑记录：最初把断言写在 `form.Close()` **之后**并 `await`——主窗一关 `Application.Run` 即返回、消息泵消失，
  续体再也不会执行（断言恒为初值）→ 改为"先关探针窗、主窗仍开着时断言、最后再关主窗且此后不再 await"。
- 决策：Q1=A（工具栏按钮，与 AvaloniaUIDemo 对齐）、Q2=A（懒装配）、Q3=A（加 `--smoke-probe`）、
  Q4=A（WinForms 画布补绘制计时）、Q5=A（不加常驻故障系统，仅冒烟注册）、Q6=A（关闭时一并收探针）、
  Q7=A（README 加"三宿主接入方式"表 + 本决策记录）。
- 验证：sln 0 警告 0 错误；xunit 32/32；`WinformDemo --smoke` = `SMOKE_OK`；
  `--smoke-probe` = `WINFORMS_PROBE_OPEN=True / WINFORMS_PROBE_REFRESH_DELTA=3 / WINFORMS_PROBE_LOG_ITEMS=1 / WINFORMS_PROBE_EXCEPTION=True / WINFORMS_PROBE_CLOSED=True / SMOKE_OK`；
  `ConsoleDemo` 与 `AvaloniaUIDemo` 探针冒烟回归通过；四次运行后均**无残留进程**。
- 遗留观察：迁移期间曾见过一次残留的 `TickEngine.Probe.AvaloniaApp` 进程（锁住输出 exe 导致一次构建失败），
  但随后 `--self-test` 与三宿主冒烟共四次运行都干净退出、**未能复现**；若再现将排查常驻应用的 Shutdown 路径。

### D15. 发布前代码审查与硬化（引擎 F1–F13 / 探针 A1–A18）
发布前做了三路只读审查（引擎并发与生命周期、探针线程与进程、文档与仓库卫生），修掉的真问题如下。

**引擎侧（都有新增回归测试，见 `tests/TickEngine.Tests/LifecycleHardeningTests.cs`）**
- **F1 并发 Stop/Start 造出"无线程的活动组"**：旧实现 `Stop()` 见 `_running == false` 直接返回，
  另一线程可立即 `Start()`，而 `StartRunner` 的守卫是 `_runner != null && _sessionActive` →
  该组**跳过建线程**，`Start()` 却"成功"、`IsRunning=true`，系统此后再不动；
  且 `ResetSession` 让在途的 `StopOnce` 跳过用户 `Stop()`（Start/Stop 不配对）。
  修法：**会话令牌**取代布尔（`_activeSession` 自增，循环线程/闭包认自己的会话）+
  `Stop()` 在注册表锁内**先失效全部会话**再 Join + 生命周期锁串行化 Start/Stop
  （停止中 `Start()` 抛明确异常，而不是静默造死组）。
- **F2 迟到帧跑进新会话并释放不属于它的槽**：闭包只判 `SessionActive`，会话换代后旧帧会被当成新帧执行，
  且 `finally` 把新会话的在途槽清掉 → 一组成员永久双帧在途、节流失真。
  修法：闭包捕获**会话令牌**（不匹配整帧作废）+ **槽序号归属**（只释放自己占的那个槽）。
- **F3 `TryPost` 抛异常/NRE 杀死组线程**：CAS 与 `Engine.Dispatcher!` 都在 try 之外，
  异常逃到 `Run()` 把整条循环判死，`IsRunning` 仍为 true 且无处可查。
  修法：快照 dispatcher（null → 计丢拍）、`try/catch` 包住 `TryPost`（失败计丢拍 + 抛 `DispatcherFaulted` 事件）。
- **F4 宿主"收下帧却永不执行"把组永久钉死**（含我们自己的 WinForms dispatcher 在控件销毁后吞回调）：
  修法：引擎侧**在途看门狗**（超过 max(1s, 10×周期) 强制作废槽并上报 `DispatcherFaulted`）+
  WinForms dispatcher 的投递回调**必须执行 frame**（否则槽永不释放）；Avalonia dispatcher 补齐可捕获异常分支。
- **F5 `ElapsedSeconds` 无意义/持续增长**：未启动时返回"开机时长"，停止后继续涨。
  修法：从未启动 = 0；运行中现算（保持"各组共享同一时间轴"）；停止后冻结在最后一拍。
- **F6 成员内 `Stop()` 后同帧后续成员仍被 Update**（先 Stop 后 Update，可能用到已释放资源）。
  修法：`RunFrame` 成员循环每步检查会话令牌，已结束即 break。
- **F7 用户 `Stop()` 回调里注册/卸载会炸 `Stop()` 并留下半停引擎**：修法：快照条目 + **锁外**逐个 `StopOnce` + 单个回调异常不阻断其余。
- **F8 空组遗留线程**：成员全部迁移/卸载的 Worker 组现在由自己的循环线程**自我回收**（`UpdateEngine.GroupCount` 可观察；Main 组不回收以避免与在途门控纠缠）。
- **F9 首拍假丢拍**：dt 基线改在计时器就绪后取（启动延迟不再被算成丢拍）。
- **F11 运行中注册时 `Start()` 可能读到 `Engine`/`Schedule == null`**：先填元数据再加入成员。
- **F12 陈旧卸载条目停掉重新注册的实例**：卸载队列条目带归属组、迁移后不再由旧组停它；`RemoveMember` 清 `Owner`；`Unregister` 只清理仍指向本引擎的元数据；`Register` 拒绝"已注册在另一个引擎实例上"的同一实例。
- **F13 文档/文化修正**：`FrameContext.ToString()` 用 InvariantCulture（CSV/日志解析不受区域影响）、
  `ThreadKind`/`Schedule`/`UpdateSystem`/`SystemView` 的 XML 文档与实现对齐、`Dispose` 后再 `Start/Register` 抛 `ObjectDisposedException`。
- 已知未做：`MaxFps=1000` 在 Windows 上受 `PeriodicTimer` 粒度（~15.6ms）限制，实际达不到 1ms 周期——
  文档已如实说明，不再暗示"1ms 可达成"。

**探针侧**
- **A1 `Console.Out/Error` 永不还原**（`Console.SetOut` 会再包一层 `SyncTextWriter`，用 `is ProbeTeeWriter` 判断恒假）→
  每次 Dispose 泄漏一层 tee、最后半行输出丢失。修法：记住**实际装上去的包装对象**再比对；安装/还原加锁；Dispose 幂等。
  回归：ConsoleDemo 冒烟断言 `CONSOLE_RESTORED=True`。
- **A2 已死的探针应用仍报可用、开窗静默失败** → 可用性改由"线程是否活着"推导；建窗/投递异常一律记 Error 到探针日志（不再抛到 Avalonia 派发器上）。
- **A3 `beat-log.csv` 随区域设置损坏**（de-DE 下 16 列表头 vs 22 列数据）→ 行格式化改 `string.Create(InvariantCulture, …)`。回归：`--de-culture` + `BEATLOG_COLUMNS_OK=True`。
- **A4 日志清单无界增长**（环形 2000 条但 UI 列表 2403+）→ 显示列表按环形容量裁剪。
- **A5 表格每 0.5s 重建导致选中项被清空**（双击跳转/键盘导航不可靠）→ 刷新后按系统名恢复选中。
- **A6 探针 UI 线程是 MTA**（DTE COM 跨套间）→ 常驻应用线程 `SetApartmentState(STA)`。
- **A8 `Attach` 不幂等**（重复装配出重复系统、CSV 行翻倍）→ 每引擎一份会话（`ConditionalWeakTable`），重复 Attach 返回既有会话。
- **A9 Dispose 后再开窗会留下没人关的窗口** → 会话加 `_disposed` 守卫，Dispose 幂等。
- **A10 未采集却显示 0%** → `UiThreadProbe.HasBaseline`，窗口显示"宿主未调用 CaptureUiThread"。
- **A11/A12 采样线程竞争/Dispose 后复活、每 200ms 全进程枚举线程、线程消失时混算全进程 CPU** → 加锁 + `_disposed`；缓存目标 `ProcessThread`；未知时保持上次读数（不再伪造 0% 或假尖峰）。
- **A13/A14 超长行不截断、逐行整串复制的 O(n²) 拼接** → 单条日志上限 4096 字符（截断并标注剩余长度）、按换行单次扫描。
- **A15/A16 启动器静态字段无 volatile/`LastError` 不清、旧窗 `Closed` 迟到清状态** → 加 volatile 与清空、按窗体实例比对后再清。
- 已知未做（记录在案）：`BeatLogSystem.FlushNow()` 与 worker 线程写入未加锁（压力测试未复现损坏，契约上建议 Stop 后再调）；
  `ProbeCodeJump` 的 `TextLocation` 正则最坏二次复杂度（仅超长行双击时可能卡一下）；
  演示默认把 `beat-log.csv` 写到进程 CWD（README 已说明，可用 `--beatlog` 改路径）；
  探针窗口在 Avalonia 宿主下不设 Owner（宿主 `ShutdownMode` 为默认值时的退出时序依赖 Post，见 README）。

### D16. 打包与发布：单包 + tag 触发 + OIDC 可信发布（CI）
把"提交即可编译打包、发版是一次有意动作"落成流水线（流程细节见 `RELEASE.md`）。

**发布范围**
- 只发布核心库 `TickEngine`（`PackageId=TickEngine`；发布前核实过该 ID 未被占用：扁平容器 404 + `dotnet package search` 无同名 ID）；
- 探针 `TickEngine.Probe.AvaloniaApp` 与三个演示一律 `IsPackable=false`——它们是开发期工具/示例，不会有人用
  `PackageReference` 引，且探针是 `WinExe`，发上去也装不起来。**实测依据**：不加该约束时 `dotnet pack TickEngine.sln`
  会产出 5 个包（核心库 + 探针 + 三个演示）。

**版本与触发**
- 版本号唯一来源 = `src/TickEngine/TickEngine.csproj` 的 `<Version>`；不引 MinVer/GitVersion（零构建期依赖、
  不需 `fetch-depth: 0`），也不加 `global.json`（本机继续可用更新的 SDK 编译）。
- `push main` / PR → 构建 + 测试 + 打包，产物上传 artifact，**不发布**；`push tag v*` → CI 校验 `v<tag>` 与
  csproj `<Version>` 一致（不一致直接失败）→ 发布 nuget.org → 建 GitHub Release。
  "每次提交都能编译打包"与"发版"被刻意拆成两件事，避免日常提交消耗版本号。

**凭据（可信发布 / OIDC）**
- 用 nuget.org Trusted Publishing，无长期密钥、仓库里不配任何 secret：`NuGet/login@v1`（`user` = nuget.org
  **profile 名**，不是邮箱）+ job 级 `id-token: write`，换取 **1 小时有效的一次性**临时 API Key →
  因此换取动作放在**打包之后、推送之前**。
- 策略字段与工作流严格绑定：`Workflow File = build.yml`（只填文件名）、`Environment = production`
  → 发布 job 必须留在 `build.yml` 内且声明 `environment: production`，否则换不到 Key。
  GitHub 的 `production` 环境当前无保护规则；日后若加 Required reviewers，工作流不用改，只是在发布 job 上等人工批准。
- 官方文档只是**建议**把 profile 名存成 secret（并非必须）——本项目直接把用户名写在工作流里（公开信息，少一处配置）。

**CI 矩阵（public 仓库，runner 分钟数不花钱）**
- `core`（ubuntu-latest，SDK 固定 `8.0.x`，与 `TargetFramework` 对齐 → 产包可复现）：构建核心库 + 41 项单测 +
  打包 + 包内容校验 + **消费冒烟**（临时项目 `dotnet add package --source <本地包目录>`，编译并真跑一遍，
  断言 ticks/groups/faults）——这一步能在十几秒内抓住"缺依赖、缺 lib 分组、元数据写错"这类发布事故。
- `windows`（windows-latest）：整 `TickEngine.sln` 构建（唯一能验证 `net8.0-windows` 的 WinformDemo 真能编译的地方）
  + 无窗口冒烟 `ConsoleDemo --smoke`；**只在 push/tag 上跑，PR 不跑**（PR 噪音交给 ubuntu 作业）。
- GUI 冒烟不上 CI：它们会真开窗口，runner 的桌面会话不稳会制造假红灯；本机验证仍是主战场。
- `concurrency` 按 ref 分组并取消进行中的旧运行，但 **tag 运行不取消**（发版不允许被打断）。

**包内容与工程化（一次做完）**
- 根 `Directory.Build.props`：`IsPackable=false` 默认值 + Authors/Company/Copyright/ProjectUrl/RepositoryUrl/
  RepositoryType + `PublishRepositoryUrl`/`EmbedUntrackedSources`/`Deterministic` + `ContinuousIntegrationBuild`
  （仅 `CI=true` 时开）+ `IncludeSymbols`/`SymbolPackageFormat=snupkg`。
- 核心库显式 `IsPackable=true`，并把 `README.md` + `LICENSE` 打进包（nuget.org 包页面直接显示 README）；
  `Microsoft.SourceLink.GitHub 8.0.0` **只装在可打包项目**——`Directory.Build.props` 在项目体之前求值，
  条件 ItemGroup（`Condition="'$(IsPackable)' == 'true'"`）在那里恒为 false，所以放在 csproj 里。
- 实测包内容：`TickEngine.nuspec / README.md / LICENSE / lib/net8.0/TickEngine.dll / lib/net8.0/TickEngine.xml`；
  符号包含 PDB，nuspec 的 `<repository>` 已带 SourceLink 解析出的 `commit=<sha>`。

**本次不发版**：先让 `push main` 跑绿，再由维护者打 `v0.1.0` 触发首次真实发布——把"CI 配置对不对"与
"首次发布会不会失败"两个变量分开。`CONTEXT.md` 是领域术语表（引擎语义），发布/打包不属于该领域语言，故不加条目。

