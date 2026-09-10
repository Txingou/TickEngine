using System.Text;
using TickEngine;

namespace TickEngine.Probe;

/// <summary>
/// 节拍日志系统（性能探测器核心，引擎无关）：
/// 周期性把引擎全部系统的快照（engine.Systems）落成 CSV，时间线用引擎纪元拍号 GlobalTick。
/// - 固定文件名、运行目录、存在即覆盖（每次引擎 Start 重建）；
/// - 每采样点为每个系统写一行 → 同一 GlobalTick 有 N 行（N=系统数）；
/// - AI/Excel 双友好的 UTF-8 带 BOM CSV，表头英文，单位后缀列名便于机器解析。
/// 用法（宿主引擎侧）：engine.Register(new BeatLogSystem(engine, path), Schedule.Worker(10, "BeatLog"));
/// </summary>
public sealed class BeatLogSystem : UpdateSystem
{
    private readonly UpdateEngine _engine;
    private readonly string _filePath;
    private StreamWriter? _writer;
    private long _sampleSeq;

    public BeatLogSystem(UpdateEngine engine, string filePath)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public override string Name => "BeatLog";

    /// <summary>已写入的数据行数（不含表头；0 = 尚未采样）。</summary>
    public long WrittenLines { get; private set; }

    protected override void Start()
    {
        // 每次会话重建文件（覆盖旧日志）
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
        _writer = new StreamWriter(_filePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = false,
        };
        _writer.WriteLine(
            "GlobalTick,GlobalSeconds_s,SampleSeq,Name,GroupName,ThreadKind," +
            "Fps_hz,Enabled,TickNumber,Dropped,Faults,ActualFps_hz," +
            "FrameInterval_ms,UpdateLast_ms,UpdateMax_ms,Elapsed_s");
    }

    protected override void Update(in FrameContext frame)
    {
        if (_writer is not { } w) { return; }

        long tick = frame.GlobalTick;
        double seconds = frame.GlobalSeconds;
        long seq = ++_sampleSeq;

        var views = _engine.Systems;   // 注册序快照
        foreach (var v in views)
        {
            string name = EscapeCsv(v.Name);
            string group = EscapeCsv(v.GroupName);
            // string.Create(InvariantCulture, ...)：数值一律用不变文化格式化。
            // 否则在逗号小数点区域（de/fr/es/…）里 "{seconds:F6}" 会写成 "0,123456"，
            // 直接把 CSV 列结构撑坏（实测 de-DE 下 16 列表头 vs 22 列数据）。
            w.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{tick},{seconds:F6},{seq},{name},{group},{(int)v.ThreadKind},{v.Fps},{(v.Enabled ? 1 : 0)},{v.TickNumber},{v.Dropped},{v.Faults},{v.ActualFps:F2},{v.LastDeltaSeconds * 1000:F3},{v.LastUpdateSeconds * 1000:F3},{v.MaxUpdateSeconds * 1000:F3},{v.ElapsedSeconds:F3}"));
        }
        WrittenLines += views.Count;
        w.Flush();   // 低频采样：每拍 flush 成本可忽略，防崩溃丢日志
    }

    protected override void Stop()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>宿主在引擎停止前可主动调用，确保尾日志落盘（幂等）。</summary>
    public void FlushNow()
    {
        try { _writer?.Flush(); } catch { }
    }

    private static string EscapeCsv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
