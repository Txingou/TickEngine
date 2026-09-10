using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using TickEngine.Probe;

namespace TickEngine.WinformDemo;

/// <summary>
/// 弹球画布：OnPaint 时取一次 <see cref="BallWorld.Latest"/> 快照绘制（跨线程安全）。
/// 可选接 <see cref="IPaintTimingSink"/>（探针）：OnPaint 首尾计时，供探针统计绘制耗时。
/// </summary>
public sealed class BallCanvas : Control
{
    private readonly BallWorld _world;
    private readonly HeartbeatLed _heartbeat;
    private readonly Func<string> _engineStateText;
    private IPaintTimingSink? _paintSink;

    public BallCanvas(BallWorld world, HeartbeatLed heartbeat, Func<string> engineStateText)
    {
        _world = world;
        _heartbeat = heartbeat;
        _engineStateText = engineStateText;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(18, 22, 32);
        ResizeRedraw = true;
    }

    /// <summary>挂接绘制计时接收器（探针的 UiPaintStats；传 null 摘除）。</summary>
    public void AttachPaintSink(IPaintTimingSink? sink) => _paintSink = sink;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 把画布尺寸同步进物理世界（UI 线程写，worker 只读）
        if (ClientSize.Width > 20 && ClientSize.Height > 20)
        {
            _world.Width = ClientSize.Width;
            _world.Height = ClientSize.Height;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var sink = _paintSink;
        long paintStart = sink?.BeginPaint() ?? 0;
        try
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var snap = _world.Latest;
            if (snap != null)
            {
                foreach (var ball in snap.Balls)
                {
                    using var brush = new SolidBrush(BallPhysicsSystem.Palette[ball.ColorIndex % BallPhysicsSystem.Palette.Length]);
                    g.FillEllipse(brush,
                        (float)(ball.X - ball.Radius), (float)(ball.Y - ball.Radius),
                        (float)(ball.Radius * 2), (float)(ball.Radius * 2));
                }

                // 状态栏文字
                string text = $"{_engineStateText()}  |  物理 elapsed={snap.ElapsedSeconds:F1}s  balls={snap.Balls.Count}";
                using var font = new Font("Consolas", 9f);
                using var fg = new SolidBrush(Color.FromArgb(200, 210, 225));
                g.DrawString(text, font, fg, 6, 4);
            }

            // 心跳指示
            int ledX = ClientSize.Width - 26;
            using var ledBrush = new SolidBrush(_heartbeat.On ? Color.LimeGreen : Color.FromArgb(70, 70, 80));
            g.FillEllipse(ledBrush, ledX, 8, 14, 14);
        }
        finally
        {
            // 绘制耗时上报（真实墙钟 Stopwatch 刻度；探针面板「绘制」栏据此显示）
            if (sink is not null)
            {
                sink.EndPaint(Stopwatch.GetTimestamp() - paintStart);
            }
        }
    }
}
