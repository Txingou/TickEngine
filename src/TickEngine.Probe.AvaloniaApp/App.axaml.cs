using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace TickEngine.Probe;

/// <summary>
/// 探针应用（常驻形态）：
/// <c>ShutdownMode = OnExplicitShutdown</c> + 无主窗——关窗不退出应用，UI 线程继续泵，
/// 宿主可反复开/关探针窗；只有会话 Dispose（或独立运行自检）才显式 Shutdown。
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 常驻：反复开关窗不退出（Q3=A）
            desktop.MainWindow = null;

            // 独立运行：开一个"未装配探针会话"的窗，供查看窗体本身（Q10=A）
            if (Program.StandaloneLaunch)
            {
                var window = new ProbeWindow(null, null);
                desktop.MainWindow = window;
                window.Show();

                if (Program.SelfTest)
                {
                    // --self-test：开窗后自动关闭并退出（自动化验证"独立可运行"）
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        desktop.Shutdown(0);
                    };
                    timer.Start();
                }
            }
        }

        ProbeAppLauncher.NotifyReady();   // 唤醒 EnsureUiThread 的等待者
        base.OnFrameworkInitializationCompleted();
    }
}
