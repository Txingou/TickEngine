# TickEngine

[![build](https://github.com/Txingou/TickEngine/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Txingou/TickEngine/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/TickEngine.svg)](https://www.nuget.org/packages/TickEngine)
[![NuGet downloads](https://img.shields.io/nuget/dt/TickEngine.svg)](https://www.nuget.org/packages/TickEngine)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Unity MonoBehaviour `Update()` 风格的 .NET 8 调度引擎实验。

主/UI 线程固定节拍更新 **+** 各自独立 FPS 的后台线程更新，一个引擎统一表达——
你现有的"每个控制器一个 `Thread + PeriodicTimer` 循环"的手写模式，收敛成一组可复用系统。

## 安装

```bash
dotnet add package TickEngine    # 目标框架 net8.0，无第三方依赖
```

## 为什么

> "我个人比较喜欢 Unity MonoBehaviour Update() 的方式……模拟使用 Unity 的 Update 架构去更新，
> 有固定 FPS 主 UI 线程更新和不同 FPS 的子线程更新两种。"

调研结论：**没有现成开源库完整覆盖该模型**（LogicLooper 覆盖循环侧、SetNet.Ticks 仅概念接近、
MonoGame/OpenTK 绑定窗口、Akka/R3 无固定步长语义），因此纯自研，本实验即最小可验证骨架。

## 概念（5 分钟版）

| 概念 | 说明 |
|---|---|
| `UpdateSystem` | 像 MonoBehaviour：`Start / Update(FrameContext) / Stop` + `Enabled` |
| `Schedule` | 注册时声明 `Schedule.Worker(100, "ServoA")` 或 `Schedule.Main(30)`（组名 + 线程亲和 + FPS） |
| 循环组 | 同 **(组名, 亲和, FPS)** 的系统共享一条节拍流；一拍内按注册序跑完，同帧共享同一 `FrameContext` |
| 组名 | 默认 `"Default"`；给同频 Worker 起不同组名 → 独立循环线程（如两条 100Hz 各跑各的，一个睡死另一个不受影响） |
| 丢拍不补 | 迟到/宿主忙 → 计数丢拍、不攒帧追赶；`DeltaTime` 实测，系统按真实 dt 自愈 |
| 组级在途 | Main 组各持在途标志（至多一帧在 UI 队列）；低频组不被高频组共享忙标志饿死 |
| Main 组 | 引擎节拍线程经 `IFrameDispatcher` 投整帧到主/UI 线程（纯投递）；本组忙则拒投 = 丢拍 |
| 引擎纪元 | `FrameContext.GlobalTick`(1ms 拍) / `GlobalSeconds`(单调秒)：所有循环共享同一时间轴 |
| 异常隔离 | 一个系统炸 → `Faults++` + 事件，引擎与其他系统继续 |
| 更新耗时 | 每系统 Update 实际耗时（真实墙钟）：`SystemView.LastUpdateSeconds / MaxUpdateSeconds`，尖峰卡顿（如 Sleep）一目了然 |

## 架构图与运行流程

见 [`docs/0002-tickengine-diagrams.md`](docs/0002-tickengine-diagrams.md)（Mermaid，GitHub 直接渲染）：
① 架构图（分层 + 线程映射）→ ② 运行流程图（生命周期 + 一拍主路径）→ ③ 一拍内部细节放大图（拍边界变更 / 快照 / 异常隔离 / Update 耗时插桩）。

## 环境要求

- **.NET 8 SDK**（`net8.0`；本项目用 SDK 10 也验证过）。
- 跨平台说明：核心库、探针、`AvaloniaUIDemo`、`ConsoleDemo` 都是 `net8.0`；
  `TickEngine.WinformDemo` 是 `net8.0-windows`，因此 **`dotnet build TickEngine.sln` 在 Linux/macOS 上会失败**——
  非 Windows 环境请按项目单独构建（`dotnet build src/TickEngine/TickEngine.csproj` 等）。
- 双击跳转到 VS 源码行是 **Windows + Visual Studio 专属开发期功能**：找不到 VS 时自动降级为"复制异常与堆栈到剪贴板"。

## 结构

```
<仓库根>/
├── CONTEXT.md                  # 术语表（本仓库即该实验的独立开源形态）
├── LICENSE                     # MIT
├── README.md / RELEASE.md      # 本文件 / 发版 checklist（一次性配置、发版步骤、失败排查）
├── Directory.Build.props       # 仓库级构建/打包默认值（默认 IsPackable=false，只有核心库开）
├── .github/workflows/build.yml # CI：构建+测试+打包（artifact）/ tag 触发发版
├── .gitignore
├── docs/
│   ├── 0001-tickengine-design.md      # 设计决策日志（后文取代前文）+ 发布前审查与硬化记录
│   └── 0002-tickengine-diagrams.md    # 架构图 / 运行流程图（Mermaid）
├── TickEngine.sln
├── src/
│   ├── TickEngine/             # 核心库 net8.0（无 UI 依赖，发 NuGet 包 TickEngine）
│   │   ├── UpdateEngine.cs     # 公共入口：注册/调度/纪元/事件/生命周期
│   │   ├── UpdateSystem.cs     # 系统基类（MonoBehaviour Update 风格）
│   │   ├── Schedule.cs / FrameContext.cs / SystemView.cs / ThreadKind.cs / IFrameDispatcher.cs
│   │   └── Internals/          # internal 类族：SystemEntry + SystemLoop 层次
│   │       ├── SystemLoop.cs               # 抽象基类（共享骨架 + 会话令牌 + 空组回收）
│   │       ├── MainSystemLoop.cs           # Main：组级在途 + 槽归属 + 吞帧看门狗
│   │       ├── WorkSystemLoop.cs           # Worker：PeriodicTimer 节拍直跑
│   │       └── UnthrottledWorkSystemLoop.cs# Worker 特化：无节拍忙跑
│   ├── TickEngine.Probe.AvaloniaApp/   # 性能探测器（开发期引用，上线前摘除）net8.0：
│   │                           #   Avalonia 应用：可独立运行，也可被宿主引用后由探针
│   │                           #   在后台线程启动常驻应用（关窗不退出、可反复开关）
│   │                           #   BeatLogSystem(CSV) / UiStatsPublisher / UiThreadProbe+UiPaintStats
│   │                           #   ProbeLog(Console tee + 环形缓冲) / ProbeCodeJump(双击跳 VS 定位)
│   │                           #   ProbeSession(统一窗口入口) / ProbeWindow(自带主题) / ProbeAppLauncher
│   ├── TickEngine.WinformDemo/ # WinForms 最小演示：弹球画布 + 心跳 + 「打开探针(Probe)」入口（懒装配）
│   ├── TickEngine.AvaloniaUIDemo/  # Avalonia 演示：弹球画布；开发期引用探针 → 在宿主 UI 线程开探针窗
│   └── TickEngine.ConsoleDemo/ # 控制台演示：计算 Worker + 心跳 + Main 文本屏 + P 键开探针
└── tests/TickEngine.Tests/     # xunit（41 项）：FakeTimeProvider 确定性 + 生命周期/在途加固回归
```

运行（在仓库根）：

```bash
dotnet build TickEngine.sln                     # 6 个项目（Windows）
dotnet test  tests/TickEngine.Tests             # 41 项
dotnet run --project src/TickEngine.WinformDemo
dotnet run --project src/TickEngine.AvaloniaUIDemo
dotnet run --project src/TickEngine.ConsoleDemo
dotnet run --project src/TickEngine.Probe.AvaloniaApp -- --self-test   # 独立探针窗自检（1.5s 自动退出）
dotnet pack src/TickEngine/TickEngine.csproj -c Release -o artifacts   # 打包（见「打包与发布」）
```

各宿主的自检开关：

| 宿主 | 开关 |
|---|---|
| `ConsoleDemo` | `--smoke`（N 秒引擎自检）/ `--probe-smoke`（开→断言刷新与日志→关）/ `--jump-live`（真 DTE 跳转，VS 未运行自动跳过）/ `--seconds <秒>` / `--no-beatlog` / `--beatlog <路径>` / `--de-culture`（逗号小数点区域自检） |
| `WinformDemo` | `--smoke` / `--smoke-probe` |
| `AvaloniaUIDemo` | `--smoke-probe` |
| `Probe.AvaloniaApp` | `--self-test` |

> **副作用**：三个演示宿主默认会把 `beat-log.csv` 写到**进程当前目录**（覆盖写）。不想留文件就用
> `ConsoleDemo --no-beatlog` 或 `--beatlog <路径>`；WinformDemo/AvaloniaUIDemo 目前恒写入，可自行改 `ProbeSessionOptions.BeatLogPath`。

## 快速上手

```csharp
public sealed class MyUpdateSystem : UpdateSystem
{
    protected override void Update(in FrameContext frame)
    {
        // frame.DeltaTime 实测间隔 / frame.TickNumber 拍号 / frame.Dropped 累计丢拍
    }
}

var engine = new UpdateEngine();
engine.Dispatcher = new YourWinFormsDispatcher(form);   // 只有注册 Main 亲和需要

engine.Register(new MyUpdateSystem(), Schedule.Worker(100));          // 后台线程 100Hz（默认组）
engine.Register(new ServoA(), Schedule.Worker(100, "ServoA"));     // 同频异组 → 独立线程
engine.Register(new ServoB(), Schedule.Worker(100, "ServoB"));     // 与 ServoA 互不拖累
engine.Register(new UiRefresher(), Schedule.Main(30));             // 主/UI 线程 30Hz
engine.Start();
// ...
engine.Stop();   // 幂等，可再次 Start
```

不同 UI 框架只要实现 `IFrameDispatcher`（~15 行：BeginInvoke 投递即可——忙检测/在途上限已上移到引擎各组）。
参考实现：WinForms `WinFormsDispatcher`、Avalonia `AvaloniaDispatcher`、控制台泵 `ConsoleFrameDispatcher`（见各 Demo）。

## 性能探测器 TickEngine.Probe.AvaloniaApp（开发期专用）

统计表/CSV 日志/UI 线程探针的独立 Avalonia **应用**（net8.0）：既能独立运行看窗体，也能被宿主引用后
由探针自己在后台线程启动常驻应用来开窗。
**上线前删除宿主对 `TickEngine.Probe.AvaloniaApp` 的 ProjectReference 与装配代码即整体移除。**

```csharp
// ① 纯采集装配：控制台/无 UI 宿主也能用（Worker 亲和，不要求 UI 线程）
var probe = ProbeSession.Attach(engine, new ProbeSessionOptions {
    BeatLogPath = "beat-log.csv",   // 启用 CSV 节拍日志；null = 关
    TrackUiThread = false,          // 无 UI 线程时关掉 CPU 探针
});

// ② 统一窗口入口：宿主自带 Avalonia → 用它的 UI 线程开窗；否则探针启动常驻应用再用其 UI 线程
probe.CaptureUiThread();            // 目标线程调用一次（记录 CPU 采样基准线程）
engine.Start();
probe.StartSampling();
probe.OpenProbeWindow();            // 控制台 / WinForms / Avalonia 宿主都能弹（窗口自带主题）
probe.ToggleProbeWindow();          // P 键语义：开⇄关
// 关闭前：probe.Dispose();  → 关窗（+ 若常驻应用是我们启动的则关闭它）+ flush beat-log
```

要点：
- `UiStatsPublisher` 读 `engine.Systems` 发快照（Worker 10Hz）；`BeatLogSystem` 落 CSV（GlobalTick 时间线）；
- `UiThreadProbe` 采 UI 线程 OS CPU（`CaptureUiThread` 必须在目标线程调用一次）；绘制计时经 `IPaintTimingSink` 由宿主画布挂接（WinForms / Avalonia 画布都已挂）；
- **宿主适配（实测约束）**：Avalonia 的 `AppBuilder.Setup()` 是**进程级一次性闩锁**——同进程起第二个 Avalonia 应用必抛
  `InvalidOperationException("Setup was already called on one of AppBuilder instances")`，`Shutdown()` 不重置；
  且裸线程跑 app 的 `Main` 一旦抛异常会**未处理而拆掉宿主进程**（exit `0xE0434352`）。因此：
  Avalonia 宿主 → 直接在**它的** UI 线程开窗（不启动第二个应用）；其他宿主 → 探针启一个**常驻**应用
  （`ShutdownMode = OnExplicitShutdown` + 无主窗，关窗只关窗口、UI 线程继续泵，已实测可反复开关），
  启动体包 try/catch，失败只记 Error 到探针日志、`IsProbeWindowOpen` 保持 false（不抛给宿主）；
- 探针窗**自带主题**（`Window.Styles` 内 `FluentTheme` + DataGrid 主题）→ 宿主 `App.axaml` **无需**任何改动；
- 状态行「宿主UI线程CPU」采集的是被 `CaptureUiThread` 捕获的线程（宿主 UI 线程 / 控制台主泵线程），不是探针窗自己的线程；
- 窗口自驱 0.5s 刷新（三个宿主都已冒烟断言刷新增量）；关闭时探针窗随之关闭，不留孤儿窗（常驻应用若由探针启动也一并 Shutdown）。

### 三宿主的探针接入方式

三个演示宿主都有探针入口，装配代码是同一套（`ProbeSession.Attach` → `CaptureUiThread()` → `OpenProbeWindow()`），差别只在"入口长什么样"与"哪条 UI 线程"：

| 宿主 | 入口 | 探针窗口跑在哪 | 自动化 |
|---|---|---|---|
| `ConsoleDemo` | `P` 键切换开/关 | 探针**常驻应用**的后台线程（宿主无 UI 框架） | `--smoke --probe-smoke [--jump-live]` |
| `WinformDemo` | 工具栏「打开探针(Probe)」按钮（**懒装配**：不点就不采集、不写 CSV） | 同上（WinForms 宿主不是 Avalonia 宿主） | `--smoke`、`--smoke-probe` |
| `AvaloniaUIDemo` | 工具栏「打开探针(Probe)」按钮 | **宿主自己的 UI 线程**（不能再起第二个 Avalonia 应用） | `--smoke-probe` |

`WinformDemo` 的「宿主UI线程CPU」测的是 **WinForms UI 线程**（跑 Main 系统那条），绘制耗时来自画布 `OnPaint` 计时。

### 异常记录与溯源（`SystemView.LastException`）

`SystemView.LastException`（`Exception?`）承载"最近一次异常现场"：**新会话（引擎 Start）清空、系统恢复后保留**到下一次异常覆盖——与累计计数 `Faults` 互补（计数回答"炸过几次"，该字段回答"最近炸的是什么、炸在哪"）。
- 探针表格新增「异常消息」列（`Message` + 首个 `file:line`，异常行标红）；
- **双击「异常消息」列的错误行 → 在 Visual Studio 里打开并定位到源码行**：
  主路径是 **DTE COM**（附到运行中的 VS 实例：`ItemOperations.OpenFile → Activate → ExecuteCommand("Edit.GoTo", line)`），
  并用 `ActiveDocument.Selection.ActivePoint.Line` **回读实际落点**显示在状态行（不是"发出去就算成功"）；
  为什么不用 CLI（实测鉴别）：VS 18 在"实例已在运行"时 `devenv /edit` 会打开文件，但 **`/command` 根本不执行**
  （`devenv.com` 退出码 -1；用 DTE 预置光标到第 5 行后跑 `/command "Edit.GoTo 30"`，光标仍是 5，且其它文档光标逐字节未变），
  "停在首行"只是新加载文件的默认光标——所以 CLI 既不能定位，也不能靠"两段式+延迟"救；
  主路径不可用（VS 未运行）时退回 `devenv /edit`（只打开、不定位）→ 再退回**复制「类型 + Message + 完整堆栈」到剪贴板**；
- **双击日志行也能跳转**：日志条目在引擎故障时**携带原始异常**（精确定位）；Console 输出没有异常可带，
  于是回退为从该行文本里解析**完整盘符路径** `X:\...\File.cs:line N`（日志里的堆栈行天然带完整路径）；
  两者都不可用 → 复制该行日志到剪贴板（状态行提示）；
- 栈帧解析优先走 `new StackTrace(ex, fNeedFileInfo: true)`，失败再回退解析 `ex.StackTrace` 文本；
- 实现注意：DTE 回读光标要包 try/catch——非文本文档（.csproj/.axaml/.resx）上 `doc.Selection` 会抛 `RuntimeBinderException`。

### 日志面板（底部）

`ProbeSession.Attach` 会安装 Console tee（`Console.Out`→**Info**、`Console.Error`→**Error**，一份照写原始流、控制台照常显示），
并把引擎 `SystemFaulted` 也写成 **Error**（引擎异常不经 Console，不主动写入就看不到）；`Dispose` 还原原始 Console。
窗底日志清单：时间/级别/内容（Error 标红）+「仅错误」过滤 +「自动滚动」+「清空」，环形缓冲 2000 条、窗口没开也照样缓冲、开窗回放历史。
- **渲染与日志分道**：屏幕重绘不是日志——`ConsoleDemo` 的文本屏写的是探针安装 tee 之前的**原始 stdout**，
  因此 15Hz 整块重绘不会灌进日志清单，日志里只留真正的日志输出。

## 控制台演示 TickEngine.ConsoleDemo

控制台没有系统消息泵，演示把**进程主线程当泵**：`ConsoleFrameDispatcher` 把 Main 组整帧收进队列，
主循环在按键轮询间隙 Drain 执行——Main 亲和在控制台宿主下照常成立（组级在途/丢拍语义不变）。
- 系统：`ComputeLoad`（Worker 100Hz 真实 CPU 负载）+ `Heartbeat`（Worker 10Hz）+ `FaultDemo`（Worker 2Hz，按节奏抛异常，演示异常隔离/异常列/日志 Error/双击跳转）+ `ConsoleScreen`（Main 15Hz 文本屏）；
- 按键：`P` 开/关探针（探针在后台线程启动常驻 Avalonia 应用后开窗）、`+/-` 运行中改计算 FPS、`Q/Esc` 退出；
- 自动化：`--smoke`（跑 N 秒引擎自检）/ `--probe-smoke`（开→断言自驱刷新→断言日志进面板→关，外加异常现场与 `file:line` 溯源解析自检）/ `--jump-live`（真走 DTE 跳转并回读落点；**VS 未运行则自动跳过**，不会顺手启动 VS，也不会判失败——Q4=A 的降级语义）。

## 语义边界（有意不做）

- 无 `Priority` 排序字段：注册序即执行序（可预测性 > 灵活度）。
- 无 Unity `FixedUpdate` 式固定步长累积器：丢拍不补 + 实测 dt。
- 不要在 Worker 系统自己的 `Update` 内调用 `engine.Stop()`：此时只失效会话、不 Join 自身线程，
  同帧后续成员不会再被 `Update`（v3 加固），但真正的收尾要等 Stop 返回后由调用线程完成。
- 仅 `net8.0`（不给 `PeriodicTimer` 打 netstandard polyfill）；`MaxFps = 1000` 是配置上限，
  实际节拍受 OS 定时器粒度限制（Windows 上 `PeriodicTimer` 约 15.6ms），高 FPS 会被如实计成丢拍。
- 成员的 Worker 组在成员全部迁移/卸载后会被回收；Main 组不回收（避免与在途门控纠缠）。
- 探针窗口在 Avalonia 宿主下不设 Owner：宿主若用默认 `ShutdownMode`，退出时序依赖会话 Dispose 里 Post 的关窗
  （三个官方演示都在 `OnClosed`/`FormClosed`/`finally` 里 Dispose，行为确定）。

## 打包与发布

只发布核心库 **`TickEngine`**：其余项目（探针 + 三个演示）在 [`Directory.Build.props`](Directory.Build.props) 里默认 `IsPackable=false`，
因此 `dotnet pack` **不会**把它们顺手打成包。

```bash
cd experiments/TickEngine
dotnet pack src/TickEngine/TickEngine.csproj -c Release -o artifacts
# artifacts/TickEngine.<版本>.nupkg   ← 含 README.md / LICENSE / lib/net8.0/TickEngine.dll + TickEngine.xml
# artifacts/TickEngine.<版本>.snupkg  ← 符号包（SourceLink 指向 GitHub 对应提交，可单步进源码）
```

CI 是 [`.github/workflows/build.yml`](.github/workflows/build.yml)（工作流文件名是 nuget.org 可信发布策略的一部分，**不要改名**）：

| 事件 | 行为 |
|---|---|
| `push main` / PR | **ubuntu 作业**：构建核心库 + 41 项单测 + 打包 + 包内容校验 + **消费冒烟**（新建临时项目引用打出来的包并真跑一遍）；`push main` 额外跑 **windows 作业**：整 `TickEngine.sln` 构建（覆盖 `net8.0-windows` 的 WinformDemo）+ 无窗口冒烟。产物上传 artifact，**不发布** |
| `push tag v*` | 校验 tag 与 `TickEngine.csproj` 的 `<Version>` 一致 → 打包 → OIDC 换取临时 API Key → 推 `.nupkg`/`.snupkg` 到 nuget.org → 建 GitHub Release（notes 自动生成） |

- 发布凭据是 nuget.org **可信发布（Trusted Publishing）**：仓库里**没有任何长期密钥**，也不需要在 GitHub 配置 secret；
  代价是工作流文件名、`environment: production` 与 nuget.org 策略三者必须严格对应（[`RELEASE.md`](RELEASE.md) 有对照表）。
- **GUI 冒烟只在本地跑**（`--probe-smoke` / `--smoke-probe` / `--self-test`）：它们会真开窗口，放进 CI 只会把 runner 的桌面会话问题误判成代码缺陷。
- 发版步骤、失败排查、已发布版本号不可复用等约束：见 [`RELEASE.md`](RELEASE.md)。

## License

MIT，见 [LICENSE](LICENSE)。

本项目由 **MingMm（Txingou）** 与 **DeepSeek Harness AI 助手（deepseek-v4-flash）** 协作完成：
需求、架构取舍与验收由人主导，实现、审查与文档在 AI 助手参与下共同产出（初版提交带 `Co-authored-by` 尾注）。
