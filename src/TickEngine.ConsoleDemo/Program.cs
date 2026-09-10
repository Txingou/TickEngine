using TickEngine;
using TickEngine.ConsoleDemo;
using TickEngine.Probe;

namespace TickEngine.ConsoleDemo;

/// <summary>
/// 控制台演示（无任何 UI 框架宿主）：
///   后台计算系统（Worker 100Hz） + 心跳（Worker 10Hz） + 文本屏系统（Main 15Hz，
///   经 ConsoleFrameDispatcher 投递到控制台主线程泵执行）+ 按键泵（同一主线程）。
///   按键：P 开/关探针窗口、+/- 运行中改计算频率、Q/Esc 退出。
/// 探针装配：ProbeSession.Attach —— 宿主进程内无 Avalonia 应用，故由探针在后台线程启动
/// 自己的常驻 Avalonia 应用，并在其 UI 线程上开探针窗（不需要宿主 App.axaml/主题）。
/// 自动化：--smoke 运行 N 秒（默认 4）自动退出并 SMOKE_OK；加 --probe-smoke 在同一窗口
/// 会话里开→断言自驱刷新→断言日志进面板→关；另有 --jump-live（真走 DTE 跳转并回读落点，
/// VS 未运行会自动跳过）与 --de-culture（切成逗号小数点区域验证 CSV/日志不受区域影响）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // --de-culture：把当前区域切成逗号小数点（de-DE），用于验证 CSV/日志不受区域设置影响
        if (args.Contains("--de-culture"))
        {
            var de = new System.Globalization.CultureInfo("de-DE");
            System.Globalization.CultureInfo.CurrentCulture = de;
            System.Globalization.CultureInfo.CurrentUICulture = de;
        }

        bool smoke = args.Contains("--smoke");
        bool probeSmoke = args.Contains("--probe-smoke");
        double seconds = ReadSeconds(args) ?? (smoke ? 4.0 : 0.0);
        bool beatLogOn = !args.Contains("--no-beatlog");
        string beatLogPath = ReadArg(args, "--beatlog") ?? "beat-log.csv";

        // 引擎 + 控制台调度器（先设 Dispatcher 才能注册 Main 亲和系统）
        var dispatcher = new ConsoleFrameDispatcher();
        var engine = new UpdateEngine { Dispatcher = dispatcher };
        var world = new DemoWorld();

        // Q4=A 渲染与日志分道：先抓原始 stdout —— 文本屏重绘走这条流（不进探针日志），
        // 其余 Console.Write/WriteLine 由探针的 tee 捕获（Info）并进日志面板。
        var rawOut = Console.Out;
        var rawError = Console.Error;

        // 探针装配：纯采集 Worker 亲和；呈现由探针在自己的常驻 Avalonia 应用里提供
        using var probe = ProbeSession.Attach(engine, new ProbeSessionOptions
        {
            BeatLogPath = beatLogOn ? beatLogPath : null,
            BeatLogEnabled = true,
            TrackUiThread = true,      // “UI 线程” = 控制台主线程（跑 Main 帧 + 按键）
            TrackPaint = false,        // 无画布
        });

        var compute = new ComputeLoadSystem(world);
        var heartbeat = new HeartbeatSystem(world);
        var faultDemo = new FaultDemoSystem(throwOnFirstTick: smoke);   // 异常演示夹具（冒烟首拍必抛）
        var screen = new ConsoleScreenSystem(() => engine, world, rawOut);

        engine.Register(compute, Schedule.Worker(100, "Compute"));
        engine.Register(heartbeat, Schedule.Worker(10, "Heartbeat"));
        engine.Register(faultDemo, Schedule.Worker(2, "FaultDemo"));
        engine.Register(screen, Schedule.Main(15, "ConsoleScreen"));

        // “UI 线程”CPU 采样基准 = 本进程主线程（跑 Main 帧 + 按键泵）
        probe.CaptureUiThread();
        engine.Start();
        probe.StartSampling();

        bool quit = false;
        bool smokeDone = false;
        double finalSeconds = 0;
        long observedRefreshDelta = -1;
        long observedLogTotal = -1;
        long observedLogErrors = -1;
        long observedLogItems = -1;
        bool jumpResolved = false;
        bool jumpLiveOk = false;
        var startedAt = DateTime.UtcNow;
        var deadline = smoke ? startedAt.AddSeconds(seconds) : DateTime.MaxValue;

        // 溯源自检（不启动 VS，只验证「能从异常解析出 file:line」——跳转的前提）：
        // 真抛一个异常并解析，Debug + PDB 下应得到本文件的行号。
        string jumpLocation = "未解析";
        string jumpFullPath = string.Empty;
        int jumpLine = 0;
        try
        {
            ThrowForJumpProbe();
        }
        catch (Exception ex)
        {
            jumpResolved = ProbeCodeJump.TryResolveLocation(ex, out jumpFullPath, out jumpLine) && jumpLine > 0;
            if (jumpResolved) { jumpLocation = $"{Path.GetFileName(jumpFullPath)}:{jumpLine}"; }
        }
        Console.WriteLine($"JUMP_RESOLVE={(jumpResolved ? jumpLocation : "FAILED")} " +
                          $"(VS={(ProbeCodeJump.VisualStudioPath is null ? "未找到" : "已找到")})");

        // 日志面板双击的文本兜底：从"日志里的堆栈行"这类含完整路径的文本解析位置（无需 UI/VS）
        bool logTextResolveOk = false;
        if (jumpResolved)
        {
            string stackLikeLine = $"   at X.Y() in {jumpFullPath}:line {jumpLine}";
            logTextResolveOk = ProbeCodeJump.TryResolveLocationInText(stackLikeLine, out string tf, out int tl)
                               && tl == jumpLine
                               && string.Equals(tf, jumpFullPath, StringComparison.OrdinalIgnoreCase);
        }
        Console.WriteLine($"LOG_TEXT_RESOLVE={(logTextResolveOk ? "OK" : "FAILED")}");

        // --jump-live：真跳转端到端验证（走 DTE 在 VS 里定位并回读落点）。
        // 默认关闭——它会把 VS 窗口带到前台，只在显式要求时执行。
        // VS 未运行（无法 DTE 附加）时按 Q4=A 的降级语义**跳过**：既不启动 VS（避免副作用/管道假死），也不判失败。
        bool jumpLiveRequested = args.Contains("--jump-live");
        bool jumpLiveSkipped = jumpLiveRequested && !ProbeCodeJump.IsVisualStudioRunning();
        if (jumpLiveRequested && jumpLiveSkipped)
        {
            Console.WriteLine("JUMP_LIVE_SKIPPED (VS 未运行：精确跳转不可用，按 Q4=A 走 devenv /edit 兜底)");
        }
        else if (jumpLiveRequested)
        {
            string liveReport = "未执行";
            var jumpThread = new Thread(() =>
            {
                try { ThrowForJumpProbe(); }
                catch (Exception ex)
                {
                    var r = ProbeCodeJump.JumpToSource(ex);
                    liveReport = $"mode={r.Mode} file={Path.GetFileName(r.File)} " +
                                 $"line={r.Line} actual={r.ActualLine}";
                    jumpLiveOk = r.Located && r.Line > 0 && r.ActualLine == r.Line;
                }
            });
            jumpThread.SetApartmentState(ApartmentState.STA);   // 独立 STA 线程跑 DTE COM（探针窗自身线程也是 STA）
            jumpThread.IsBackground = true;
            jumpThread.Start();
            jumpThread.Join(TimeSpan.FromSeconds(25));
            Console.WriteLine($"JUMP_LIVE={liveReport}");
            Console.WriteLine($"JUMP_LIVE_OK={jumpLiveOk}");
        }

        try
        {
            // --probe-smoke 状态机：0.8s 请求开 → 等到 IsOpen → 采样基准刷新数 →
            // 再等 ≥1.4s 断言刷新确实发生 + 断言日志真的在追加 → 请求关 → 等到关闭
            bool openRequested = false, closeRequested = false, logChecked = false;
            DateTime openedAt = DateTime.MinValue;
            DateTime refreshCheckedAt = DateTime.MinValue;
            long refreshBase = 0;

            while (!quit && !smokeDone && DateTime.UtcNow < deadline)
            {
                dispatcher.DrainPosted();   // 执行已投递的 Main 帧（控制台主线程 = 真“UI 线程”）
                var now = DateTime.UtcNow;

                if (probeSmoke && !openRequested && now >= startedAt.AddSeconds(0.8))
                {
                    probe.OpenProbeWindow();
                    openRequested = true;
                }
                if (probeSmoke && probe.IsProbeWindowOpen && openedAt == DateTime.MinValue)
                {
                    openedAt = now;
                    refreshBase = probe.ProbeRefreshCount;
                    Console.WriteLine($"PROBE_OPENED (refresh base={refreshBase})");
                }
                if (probeSmoke && probe.IsProbeWindowOpen && openedAt != DateTime.MinValue
                    && refreshCheckedAt == DateTime.MinValue && now >= openedAt.AddSeconds(1.4))
                {
                    refreshCheckedAt = now;
                    observedRefreshDelta = probe.ProbeRefreshCount - refreshBase;
                    Console.WriteLine($"PROBE_REFRESH_DELTA={observedRefreshDelta}");
                }
                // 日志面板断言：至少 1 条 Error（引擎异常上报）且面板显示条数 > 0
                if (probeSmoke && probe.IsProbeWindowOpen && refreshCheckedAt != DateTime.MinValue
                    && !logChecked)
                {
                    logChecked = true;
                    var entries = probe.Log.GetSince(0, out _);
                    observedLogTotal = probe.Log.TotalCount;
                    observedLogErrors = entries.Count(e => e.Level == ProbeLogLevel.Error);
                    observedLogItems = probe.ProbeLogItemCount;
                    Console.WriteLine($"PROBE_LOG total={observedLogTotal} errors={observedLogErrors} " +
                                      $"items={observedLogItems}");
                }
                if (probeSmoke && probe.IsProbeWindowOpen && logChecked && !closeRequested)
                {
                    probe.CloseProbeWindow();
                    closeRequested = true;
                }
                if (probeSmoke && closeRequested && !probe.IsProbeWindowOpen)
                {
                    Console.WriteLine("PROBE_CLOSED");
                    smokeDone = true;
                }

                if (!smoke)
                {
                    quit |= ReadKeys(probe, compute);
                }
                Thread.Sleep(5);
                finalSeconds = engine.GlobalSeconds;   // Stop 前读取纪元秒（Stop 后归零）
            }
        }
        finally
        {
            engine.Stop();
            dispatcher.Close();
            probe.FlushBeatLog();
        }

        if (probeSmoke)
        {
            // 开 → 自驱刷新（Δ≥2）→ 日志进面板（items>0、含 Error）→ 关，全程走通才算过
            bool probeOk = smokeDone;
            bool refreshOk = observedRefreshDelta >= 2;
            bool logOk = observedLogTotal > 0 && observedLogErrors >= 1 && observedLogItems > 0;
            Console.WriteLine($"PROBE_REFRESH_DELTA={observedRefreshDelta}");
            Console.WriteLine($"PROBE_LOG_FINAL total={observedLogTotal} errors={observedLogErrors} items={observedLogItems}");
            Console.WriteLine(probeOk && refreshOk && logOk ? "PROBE_OK" : "PROBE_FAILED");
            if (!probeOk || !refreshOk || !logOk) { return 2; }
        }

        // 异常记录自检：FaultDemo 抛过异常 → SystemView.LastException 有现场（探针表格异常列的数据源）
        var faultView = engine.Systems.FirstOrDefault(v => ReferenceEquals(v.UpdateSystem, faultDemo));
        bool lastExceptionOk = faultView.LastException != null;
        Console.WriteLine($"异常自检: Faults={faultView.Faults} " +
                          $"LastException={(lastExceptionOk ? faultView.LastException!.Message : "null")}");
        if (!lastExceptionOk) { return 4; }

        bool engineOk = world.ComputeIterations > 0 && world.Heartbeats > 0;
        Console.WriteLine($"引擎自检: compute={world.ComputeIterations} 心跳={world.Heartbeats} " +
                          $"纪元={finalSeconds:F1}s 溯源解析={(jumpResolved ? "OK" : "FAILED")}");
        if (!engineOk) { return 3; }
        if (!jumpResolved) { return 5; }
        if (!logTextResolveOk) { return 7; }                              // 日志面板双击的文本兜底不可用
        if (jumpLiveRequested && !jumpLiveSkipped && !jumpLiveOk) { return 6; }   // 真跳转未落到指定行

        // A1 回归：Dispose 必须还原原始 Console.Out/Error。
        // （Console.SetOut 会再包一层 SyncTextWriter，早期实现用 `Console.Out is ProbeTeeWriter` 判断，
        //   还原永远不生效 → 每次装配叠加一层 tee，最后半行输出还会丢。）
        probe.Dispose();
        bool consoleRestored = ReferenceEquals(Console.Out, rawOut) && ReferenceEquals(Console.Error, rawError);
        Console.WriteLine($"CONSOLE_RESTORED={consoleRestored}");
        if (!consoleRestored) { return 8; }

        // A3 回归：beat-log.csv 的行列结构不得受区域设置影响（de-DE 下曾出现 16 列表头 vs 22 列数据）。
        bool beatLogOk = true;
        if (beatLogOn && File.Exists(beatLogPath))
        {
            var lines = File.ReadAllLines(beatLogPath);
            if (lines.Length >= 2)
            {
                int headerCols = lines[0].Split(',').Length;
                beatLogOk = lines.Skip(1).All(l => l.Length == 0 || CountCsvColumns(l) == headerCols);
            }
        }
        Console.WriteLine($"BEATLOG_COLUMNS_OK={beatLogOk} (culture={System.Globalization.CultureInfo.CurrentCulture.Name})");
        if (!beatLogOk) { return 9; }

        Console.WriteLine("SMOKE_OK");
        return 0;
    }

    /// <summary>按 CSV 规则数列数（引号内的逗号不计；用于校验行列结构完整）。</summary>
    private static int CountCsvColumns(string line)
    {
        int cols = 1;
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes) { cols++; }
        }
        return cols;
    }

    /// <summary>溯源自检用：抛一个真实异常（栈里有本文件 file:line），供 TryResolveLocation 解析。</summary>
    private static void ThrowForJumpProbe()
        => throw new InvalidOperationException("溯源自检：用于验证异常栈能解析出 file:line");

    /// <summary>非阻塞按键轮询：P 开/关探针；+/- 运行中改 Compute 频率；Q/Esc 退出。</summary>
    private static bool ReadKeys(ProbeSession probe, ComputeLoadSystem compute)
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.P:
                        probe.ToggleProbeWindow();
                        Console.WriteLine($"探针 IsOpen={probe.IsProbeWindowOpen}");
                        break;
                    case ConsoleKey.OemPlus:
                    case ConsoleKey.Add:
                        ChangeComputeFps(compute, +10);
                        break;
                    case ConsoleKey.OemMinus:
                    case ConsoleKey.Subtract:
                        ChangeComputeFps(compute, -10);
                        break;
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        Console.WriteLine("退出");
                        return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 标准输入被重定向（自动化）→ 无按键可用
        }
        return false;
    }

    private static void ChangeComputeFps(ComputeLoadSystem compute, int delta)
    {
        if (compute.Engine is not { } engine || compute.Schedule is not { } s) { return; }
        int fps = Math.Clamp(s.Fps + delta, 10, 400);
        engine.ChangeSchedule(compute, Schedule.Worker(fps, "Compute"));
        Console.WriteLine($"计算频率 → {fps}Hz");
    }

    private static double? ReadSeconds(string[] args)
    {
        string? v = ReadArg(args, "--seconds");
        return double.TryParse(v, out double d) ? d : null;
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) { return args[i + 1]; }
        }
        return null;
    }
}
