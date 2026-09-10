using TickEngine;

namespace TickEngine.AvaloniaUIDemo;

/// <summary>弹球场景共享世界：物理 worker 写、UI 渲染读，经 volatile 发布避免撕裂。</summary>
public sealed class BallWorld
{
    private double _width = 640;
    private double _height = 360;

    public double Width { get => Volatile.Read(ref _width); set => Volatile.Write(ref _width, value); }
    public double Height { get => Volatile.Read(ref _height); set => Volatile.Write(ref _height, value); }

    private BallSnapshot? _snapshot;

    /// <summary>发布最新球位快照（worker 整帧替换引用；UI 一次取引用后不可变）。</summary>
    public BallSnapshot? Latest
    {
        get => Volatile.Read(ref _snapshot);
        set => Volatile.Write(ref _snapshot, value);
    }
}

/// <summary>一帧球位快照（不可变）。</summary>
public sealed class BallSnapshot
{
    public BallSnapshot(IReadOnlyList<BallState> balls, double elapsedSeconds)
    {
        Balls = balls;
        ElapsedSeconds = elapsedSeconds;
    }

    public IReadOnlyList<BallState> Balls { get; }
    public double ElapsedSeconds { get; }
}

/// <summary>单球状态（结构体快照，无共享可变引用）。ColorIndex 供画布查调色板。</summary>
public readonly record struct BallState(double X, double Y, double Vx, double Vy, double Radius, int ColorIndex);

/// <summary>弹球物理系统：Worker（默认 100Hz，可改频）。发布 volatile 快照供 UI 轮询。</summary>
public sealed class BallPhysicsSystem : UpdateSystem
{
    private readonly BallWorld _world;
    private readonly BallState[] _balls;
    private readonly Random _rng = new(1234);
    private int _ballCount;
    private double _lastWidth = -1;
    private double _lastHeight = -1;
    private int _resetCounter;

    public BallPhysicsSystem(BallWorld world, int ballCount = 30)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _ballCount = ballCount;
        _balls = new BallState[ballCount];
    }

    public override string Name => "BallPhysics";

    public int BallCount
    {
        get => _ballCount;
        set => _ballCount = Math.Clamp(value, 1, 1200);
    }

    protected override void Update(in FrameContext frame)
    {
        double w = _world.Width;
        double h = _world.Height;
        if (_lastWidth != w || _lastHeight != h || _resetCounter++ > 600)
        {
            ResetBalls(w, h);
        }

        double dt = Math.Min(frame.DeltaTime, 0.1);
        if (dt <= 0) { dt = 0.01; }

        for (int i = 0; i < _ballCount; i++)
        {
            var b = _balls[i];
            double nx = b.X + b.Vx * dt;
            double ny = b.Y + b.Vy * dt;
            double nvx = b.Vx;
            double nvy = b.Vy + 980.0 * dt;
            double r = b.Radius;

            if (ny + r > h) { ny = h - r; nvy = -nvy * 0.82; nvx *= 0.992; }
            else if (ny - r < 0) { ny = r; nvy = -nvy * 0.82; }
            if (nx - r < 0) { nx = r; nvx = -nvx * 0.95; }
            else if (nx + r > w) { nx = w - r; nvx = -nvx * 0.95; }

            _balls[i] = new BallState(nx, ny, nvx, nvy, r, b.ColorIndex);
        }

        _world.Latest = new BallSnapshot(_balls.Take(_ballCount).ToArray(), frame.ElapsedSeconds);
    }

    private void ResetBalls(double w, double h)
    {
        _lastWidth = w;
        _lastHeight = h;
        _resetCounter = 0;
        for (int i = 0; i < _balls.Length; i++)
        {
            double r = 8 + _rng.NextDouble() * 10;
            double x = r + _rng.NextDouble() * Math.Max(1, w - 2 * r);
            double y = r + _rng.NextDouble() * Math.Max(1, h * 0.4);
            _balls[i] = new BallState(x, y,
                (_rng.NextDouble() - 0.5) * 240,
                (_rng.NextDouble() - 0.5) * 60,
                r, i % 6);
        }
    }
}

/// <summary>心跳系统：Worker 10Hz，翻转 UI 侧可见标志。</summary>
public sealed class HeartbeatSystem : UpdateSystem
{
    private readonly HeartbeatLed _led;
    private long _beats;

    public HeartbeatSystem(HeartbeatLed led)
    {
        _led = led ?? throw new ArgumentNullException(nameof(led));
    }

    public override string Name => "Heartbeat";

    protected override void Update(in FrameContext frame)
    {
        Interlocked.Increment(ref _beats);
        _led.Pulse((_beats & 1) == 1);
    }
}

/// <summary>UI 侧可见的心跳指示（volatile 发布，渲染系统轮询）。</summary>
public sealed class HeartbeatLed
{
    private bool _on;

    public bool On => Volatile.Read(ref _on);

    public void Pulse(bool on) => Volatile.Write(ref _on, on);
}

/// <summary>画布渲染系统（Main 亲和）：请求画布重绘。</summary>
public sealed class UiCanvasRenderSystem : UpdateSystem
{
    private readonly Action _requestRedraw;

    public UiCanvasRenderSystem(Action requestRedraw)
    {
        _requestRedraw = requestRedraw;
    }

    public override string Name => "UiCanvasRender";

    protected override void Update(in FrameContext frame) => _requestRedraw();
}
