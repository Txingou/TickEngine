using Avalonia.Controls;
using Avalonia.Interactivity;
using TickEngine;
using TickEngine.Probe;

namespace TickEngine.AvaloniaUIDemo;

public partial class MainWindow : Window
{
    private readonly UpdateEngine _engine = new();
    private readonly BallWorld _world = new();
    private readonly HeartbeatLed _heartbeat = new();
    private readonly BallCanvas _canvas;

    private BallPhysicsSystem? _physics;
    private UiCanvasRenderSystem? _uiCanvasRender;
    private HeartbeatSystem? _heartbeatSystem;

    // 开发期探针：ProbeSession 注册 UiStats/BeatLog（Worker 亲和），UI 侧 ThreadProbe/PaintStats 随附
    private ProbeSession? _probe;
    private readonly string _beatLogPath = System.IO.Path.Combine(
        System.IO.Directory.GetCurrentDirectory(), "beat-log.csv");

    public MainWindow()
    {
        InitializeComponent();
        _engine.Dispatcher = new AvaloniaDispatcher();

        // 画布由代码构造（需注入 world/heartbeat，XAML 无法无参实例化）
        _canvas = new BallCanvas(_world, _heartbeat, () => StatusText?.Text ?? "引擎: -");
        _canvas.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _canvas.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        CanvasHost.Children.Add(_canvas);

        RegisterSystems();

        FpsPicker.ValueChanged += (_, _) =>
        {
            if (_engine.IsRunning && _physics != null)
            {
                _engine.ChangeSchedule(_physics, Schedule.Worker((int)(FpsPicker.Value ?? 100)));
            }
        };

        // 冒烟模式：--smoke-probe 时主窗加载后自动跑「启动→开探针→断言→关探针」序列
        if (Program.SmokeProbeMode)
        {
            Opened += async (_, _) => await RunSmokeProbeAsync();
        }
    }

    /// <summary>
    /// 冒烟：验证宿主（Avalonia UI）能打开探针窗，且窗口真实刷新、日志确实进面板。
    /// 宿主自身已是 Avalonia 应用 → 探针窗直接建在**宿主自己的 UI 线程**上（同进程不能再起第二个 Avalonia 应用）。
    /// </summary>
    private async System.Threading.Tasks.Task RunSmokeProbeAsync()
    {
        StartEngine();
        await System.Threading.Tasks.Task.Delay(800);          // 给引擎首拍/采样一点时间
        _probe!.OpenProbeWindow();                              // 统一入口：宿主 UI 线程呈现
        await System.Threading.Tasks.Task.Delay(1500);
        bool opened = _probe.IsProbeWindowOpen;
        Console.WriteLine($"PROBE_OPEN={opened}");
        long refreshBase = _probe.ProbeRefreshCount;
        Console.WriteLine("PROBE_LOG_MARKER=av-alive");         // 经 Console tee 进日志面板
        await System.Threading.Tasks.Task.Delay(1200);          // 等 ≥2 次 0.5s 周期刷新
        long refreshDelta = _probe.ProbeRefreshCount - refreshBase;
        long logTotal = _probe.Log.TotalCount;
        long logItems = _probe.ProbeLogItemCount;
        Console.WriteLine($"PROBE_REFRESH_DELTA={refreshDelta}");
        Console.WriteLine($"PROBE_LOG total={logTotal} items={logItems}");
        _probe.CloseProbeWindow();
        await System.Threading.Tasks.Task.Delay(600);
        bool closed = !_probe.IsProbeWindowOpen;
        Console.WriteLine($"PROBE_CLOSE={closed}");
        StopEngine();
        _probe.Dispose();
        bool ok = opened && closed && refreshDelta >= 2 && logTotal > 0 && logItems > 0;
        Console.WriteLine(ok ? "SMOKE_OK" : "SMOKE_FAILED");
        Environment.Exit(ok ? 0 : 1);
    }

    private void RegisterSystems()
    {
        _physics = new BallPhysicsSystem(_world, 30) { Enabled = true };
        _heartbeatSystem = new HeartbeatSystem(_heartbeat);
        _uiCanvasRender = new UiCanvasRenderSystem(() => _canvas.InvalidateVisual());

        _engine.Register(_physics, Schedule.Worker(100, "Physics"));
        _engine.Register(_heartbeatSystem, Schedule.Worker(10));
        _engine.Register(_uiCanvasRender, Schedule.Main(30));
    }

    private void StartEngine()
    {
        if (_engine.IsRunning) { return; }

        // 装配探针（首次启动时）：Worker 亲和采集 + UI 线程探针；画布绘制计时挂 PaintStats
        if (_probe == null)
        {
            _probe = ProbeSession.Attach(_engine, new ProbeSessionOptions
            {
                BeatLogPath = _beatLogPath,
                BeatLogEnabled = true,
                TrackUiThread = true,
                TrackPaint = true,
            });
            _canvas.AttachPaintSink(_probe.PaintStats);
            _probe.CaptureUiThread();   // 必须在 UI 线程
        }

        _engine.Start();
        _probe.StartSampling();
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        StatusText.Text = "引擎: 运行中";
    }

    private void StopEngine()
    {
        _engine.Stop();
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        StatusText.Text = "引擎: 已停止";
    }

    private void OnStartClick(object? sender, RoutedEventArgs e) => StartEngine();
    private void OnStopClick(object? sender, RoutedEventArgs e) => StopEngine();

    private void OnOpenProbeClick(object? sender, RoutedEventArgs e)
    {
        if (_probe == null)
        {
            // 探针未装配（引擎没启动过）→ 先装配但不启动引擎
            _probe = ProbeSession.Attach(_engine, new ProbeSessionOptions
            {
                BeatLogPath = _beatLogPath,
                BeatLogEnabled = true,
                TrackUiThread = true,
                TrackPaint = true,
            });
            _canvas.AttachPaintSink(_probe.PaintStats);
            _probe.CaptureUiThread();
        }

        // 统一入口：宿主自带 Avalonia → 探针窗建在宿主 UI 线程上（窗口自带主题，宿主无需改 App.axaml）
        _probe.OpenProbeWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Stop();
        _probe?.Dispose();
        base.OnClosed(e);
    }
}
