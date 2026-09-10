using TickEngine;
using TickEngine.Probe;

namespace TickEngine.WinformDemo;

/// <summary>
/// 主窗体（画布演示壳 + 探针入口）：引擎启停 + 弹球画布 + 心跳灯 + 「打开探针」按钮。
/// 探针装配是<b>懒</b>的：首次点按钮时才 Attach（不点就不采集、不写 beat-log.csv）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly UpdateEngine _engine = new();
    private readonly BallWorld _world = new();
    private readonly HeartbeatLed _heartbeat = new();

    private BallPhysicsSystem _physics = null!;
    private UiCanvasRenderSystem _uiCanvasRender = null!;
    private HeartbeatSystem _heartbeatBhv = null!;
    private BallCanvas _canvas = null!;

    // 开发期探针（懒装配）：Attach 注册 UiStats/BeatLog（Worker 亲和），UI 侧 ThreadProbe/PaintStats 随附
    private ProbeSession? _probe;
    private readonly string _beatLogPath = System.IO.Path.Combine(
        System.IO.Directory.GetCurrentDirectory(), "beat-log.csv");

    private readonly Button _btnStart = new() { Text = "启动引擎", Width = 100 };
    private readonly Button _btnStop = new() { Text = "停止引擎", Width = 100, Enabled = false };
    private readonly Button _btnProbe = new() { Text = "打开探针(Probe)", Width = 130 };
    private readonly NumericUpDown _physicsFps = new() { Minimum = 1, Maximum = 500, Value = 100, Width = 70 };
    private readonly Label _lblPhysics = new() { Text = "物理Hz:", AutoSize = true };
    private readonly Label _lblStatus = new() { Text = "引擎: 已停止", AutoSize = true };

    /// <summary>探针会话（未装配为 null）；冒烟/自检用。</summary>
    public ProbeSession? Probe => _probe;

    /// <summary>引擎实例（冒烟/自检读快照用）。</summary>
    public UpdateEngine Engine => _engine;

    public MainForm()
    {
        Text = "TickEngine.WinformDemo — 弹球画布（Unity 风格 Update 架构）";
        Width = 900;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;

        _engine.Dispatcher = new WinFormsDispatcher(this);
        RegisterSystems();
        BuildUi();
    }

    private void RegisterSystems()
    {
        _physics = new BallPhysicsSystem(_world, 30) { Enabled = true };
        _heartbeatBhv = new HeartbeatSystem(_heartbeat);
        _uiCanvasRender = new UiCanvasRenderSystem(() => _canvas.Invalidate());

        _engine.Register(_physics, Schedule.Worker(100, "Physics"));
        _engine.Register(_heartbeatBhv, Schedule.Worker(10));
        _engine.Register(_uiCanvasRender, Schedule.Main(30));
    }

    private void BuildUi()
    {
        var ctrl = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6, 8, 6, 0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _btnStart.Click += (_, _) => StartEngine();
        _btnStop.Click += (_, _) => StopEngine();
        _btnProbe.Click += (_, _) => OpenProbe();
        _physicsFps.ValueChanged += (_, _) =>
        {
            if (_engine.IsRunning)
            {
                _engine.ChangeSchedule(_physics, Schedule.Worker((int)_physicsFps.Value));
            }
        };
        ctrl.Controls.Add(_btnStart);
        ctrl.Controls.Add(_btnStop);
        ctrl.Controls.Add(_lblPhysics);
        ctrl.Controls.Add(_physicsFps);
        ctrl.Controls.Add(_lblStatus);
        ctrl.Controls.Add(_btnProbe);

        _canvas = new BallCanvas(_world, _heartbeat, () => _lblStatus.Text)
        {
            Dock = DockStyle.Fill,
        };

        Controls.Add(_canvas);
        Controls.Add(ctrl);

        // 关闭顺序：先停引擎再收探针（探针窗随之关闭；常驻探针应用若由探针启动也一并关闭）——不留孤儿窗/线程
        FormClosed += (_, _) =>
        {
            _engine.Stop();
            _probe?.Dispose();
            _probe = null;
        };
        Shown += (_, _) =>
        {
            _world.Width = _canvas.ClientSize.Width;
            _world.Height = _canvas.ClientSize.Height;
            StartEngine();
        };
    }

    /// <summary>
    /// 打开探针窗口（首次点击懒装配）：Worker 亲和采集 + UI 线程探针；
    /// `CaptureUiThread` 必须在 WinForms UI 线程调用——它正是跑 Main 系统的那条线程。
    /// </summary>
    public void OpenProbe()
    {
        if (_probe is null)
        {
            _probe = ProbeSession.Attach(_engine, new ProbeSessionOptions
            {
                BeatLogPath = _beatLogPath,
                BeatLogEnabled = true,
                TrackUiThread = true,
                TrackPaint = true,
            });
            _canvas.AttachPaintSink(_probe.PaintStats);
            _probe.CaptureUiThread();          // 本方法在 UI 线程上被调用
            if (_engine.IsRunning) { _probe.StartSampling(); }
        }
        _probe.OpenProbeWindow();
    }

    private void StartEngine()
    {
        if (_engine.IsRunning) { return; }
        try
        {
            _engine.Start();
            _probe?.StartSampling();           // 探针已装配但引擎刚启动 → 补上 CPU 采样（幂等）
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
            _lblStatus.Text = "引擎: 运行中";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopEngine()
    {
        _engine.Stop();
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "引擎: 已停止";
    }

    /// <summary>冒烟模式入口：启动引擎并运行（由宿主定时关闭）。</summary>
    public void RunSmoke() => StartEngine();

    /// <summary>
    /// 探针冒烟入口：启动引擎 →（仅此处）注册"首拍抛一次异常"的系统用于断言异常链路 → 懒装配并打开探针窗。
    /// </summary>
    public void RunSmokeProbe()
    {
        StartEngine();
        _engine.Register(new SmokeFaultSystem(), Schedule.Worker(2, "SmokeFault"));
        OpenProbe();
    }

    /// <summary>冒烟专用：首拍抛一次真实异常（验证异常列/日志 Error/双击跳转链路；不进常态演示）。</summary>
    private sealed class SmokeFaultSystem : UpdateSystem
    {
        private long _ticks;

        public override string Name => "SmokeFault";

        protected override void Update(in FrameContext frame)
        {
            if (Interlocked.Increment(ref _ticks) != 1) { return; }
            throw new InvalidOperationException("冒烟异常：WinForms 宿主探针链路自检（双击本行可跳转到这行源码）");
        }
    }
}
