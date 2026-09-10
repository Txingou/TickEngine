using Avalonia;

namespace TickEngine.Probe;

/// <summary>
/// 探针应用入口。三种用法：
/// ① 独立双击运行（无宿主）→ 打开一个"未装配探针会话"的探针窗，仅用于查看窗体本身；
/// ② 被宿主引用后由 <see cref="ProbeAppLauncher.EnsureUiThread"/> 在后台线程启动（常驻，按需开窗）；
/// ③ 宿主自身已是 Avalonia 应用 → 不启动本应用，直接在宿主 UI 线程上开窗（见 ProbeUiHost）。
/// </summary>
internal static class Program
{
    /// <summary>独立运行标记（App 据此决定自动开窗）。</summary>
    internal static bool StandaloneLaunch { get; private set; }

    /// <summary>独立运行自检（--self-test：开窗约 1.5s 后自动关闭并退出 0）。</summary>
    internal static bool SelfTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StandaloneLaunch = true;
        SelfTest = args.Contains("--self-test");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Avalonia 配置（宿主在后台线程启动时复用同一配置）。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
