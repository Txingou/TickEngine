namespace TickEngine.WinformDemo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--smoke-probe"))
        {
            return RunSmokeProbe();
        }

        if (args.Contains("--smoke"))
        {
            // 冒烟：真实消息泵下跑 1.6s，验证 Main dispatcher/worker 线程都在动，然后自动退出
            using var form = new MainForm();
            form.Shown += async (_, _) =>
            {
                form.RunSmoke();
                await Task.Delay(1600);
                form.BeginInvoke(() => form.Close());
            };
            Application.Run(form);
            Console.WriteLine("SMOKE_OK");
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// 探针冒烟（WinForms 宿主 → 探针在后台线程启动常驻 Avalonia 应用）：
    /// 开探针 → 断言 IsOpen + 自驱刷新增量 + 日志进面板 + 异常现场(LastException) → 关 → 断言已关。
    /// </summary>
    private static int RunSmokeProbe()
    {
        using var form = new MainForm();
        bool opened = false, closed = false, refreshOk = false, logOk = false, exceptionOk = false;
        long refreshDelta = -1, logItems = -1;

        form.Shown += async (_, _) =>
        {
            form.RunSmokeProbe();          // 启动引擎 + 注册首拍抛异常系统 + 懒装配并打开探针
            await Task.Delay(1500);        // 等窗口就绪
            opened = form.Probe?.IsProbeWindowOpen == true;
            long refreshBase = form.Probe?.ProbeRefreshCount ?? 0;
            await Task.Delay(1400);        // 0.5s 周期 ×1.4s → 期望 ≥2 次刷新
            refreshDelta = (form.Probe?.ProbeRefreshCount ?? 0) - refreshBase;
            refreshOk = refreshDelta >= 2;
            logItems = form.Probe?.ProbeLogItemCount ?? 0;
            logOk = logItems > 0 && (form.Probe?.Log.TotalCount ?? 0) > 0;
            exceptionOk = form.Engine.Systems.Any(v => v.LastException is not null);

            // 先关探针窗并在主窗仍开着时断言，最后再关主窗——
            // 主窗一关 Application.Run 即返回、消息泵消失，之后任何 await 续体都不会再跑。
            form.Probe?.CloseProbeWindow();
            await Task.Delay(400);
            closed = form.Probe?.IsProbeWindowOpen != true;
            form.BeginInvoke(() => form.Close());   // 此后再无 await
        };

        Application.Run(form);

        Console.WriteLine($"WINFORMS_PROBE_OPEN={opened}");
        Console.WriteLine($"WINFORMS_PROBE_REFRESH_DELTA={refreshDelta}");
        Console.WriteLine($"WINFORMS_PROBE_LOG_ITEMS={logItems}");
        Console.WriteLine($"WINFORMS_PROBE_EXCEPTION={exceptionOk}");
        Console.WriteLine($"WINFORMS_PROBE_CLOSED={closed}");
        bool ok = opened && closed && refreshOk && logOk && exceptionOk;
        Console.WriteLine(ok ? "SMOKE_OK" : "SMOKE_FAILED");
        return ok ? 0 : 1;
    }
}
