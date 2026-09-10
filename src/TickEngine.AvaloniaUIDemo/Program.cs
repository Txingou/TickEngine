using Avalonia;
using System;

namespace TickEngine.AvaloniaUIDemo;

internal static class Program
{
    /// <summary>冒烟模式（--smoke-probe）：主窗加载后自动启动引擎 → 统一入口开探针 → 断言 → 关闭 → 退出。</summary>
    public static bool SmokeProbeMode { get; private set; }

    // Avalonia 初始化入口：StartWithClassicDesktopLifetime 前不得使用 Avalonia API。
    [STAThread]
    public static int Main(string[] args)
    {
        SmokeProbeMode = args.Contains("--smoke-probe");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
