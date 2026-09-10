using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TickEngine.Probe;

namespace TickEngine.AvaloniaUIDemo;

/// <summary>
/// 弹球画布（Avalonia）：Render 时取一次 BallWorld.Latest 快照绘制（跨线程安全）。
/// 可选接 IPaintTimingSink（Probe）：Render 首尾计时，供探针统计绘制耗时。
/// </summary>
public sealed class BallCanvas : Control
{
    public static readonly IBrush[] Palette =
    {
        Brushes.DodgerBlue, Brushes.OrangeRed, Brushes.LimeGreen,
        Brushes.Gold, Brushes.MediumOrchid, Brushes.Teal,
    };

    private readonly BallWorld _world;
    private readonly HeartbeatLed _heartbeat;
    private readonly Func<string> _engineStateText;
    private IPaintTimingSink? _paintSink;

    public BallCanvas(BallWorld world, HeartbeatLed heartbeat, Func<string> engineStateText)
    {
        _world = world;
        _heartbeat = heartbeat;
        _engineStateText = engineStateText;
        ClipToBounds = true;
    }

    public void AttachPaintSink(IPaintTimingSink? sink) => _paintSink = sink;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncWorldSize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            SyncWorldSize();
        }
    }

    private void SyncWorldSize()
    {
        var b = Bounds;
        if (b.Width > 20 && b.Height > 20)
        {
            _world.Width = b.Width;
            _world.Height = b.Height;
        }
    }

    public override void Render(DrawingContext context)
    {
        long t0 = _paintSink?.BeginPaint() ?? 0;
        try
        {
            base.Render(context);
            var snap = _world.Latest;
            if (snap != null)
            {
                foreach (var ball in snap.Balls)
                {
                    context.DrawEllipse(
                        Palette[ball.ColorIndex % Palette.Length],
                        null,
                        new Point(ball.X, ball.Y),
                        ball.Radius, ball.Radius);
                }
                var text = new FormattedText(
                    $"{_engineStateText()}  |  物理 elapsed={snap.ElapsedSeconds:F1}s  balls={snap.Balls.Count}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    Avalonia.Media.FlowDirection.LeftToRight,
                    Typeface.Default, 13, Brushes.LightGray);
                context.DrawText(text, new Point(6, 4));
            }
        }
        finally
        {
            if (_paintSink != null) { _paintSink.EndPaint(System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
        }
    }
}
