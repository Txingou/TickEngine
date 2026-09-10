using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using TickEngine;

namespace TickEngine.Probe;

/// <summary>
/// 探针窗口（Avalonia 呈现）：
/// 上半 = 状态行（宿主UI线程CPU / 绘制 / 纪元 + BeatLog 开关）+ 系统统计表（含「异常消息」列）；
/// 下半 = 日志清单（Console.Out→INFO / Console.Error→ERROR / 引擎异常→ERROR，Error 标红）。
/// 双击表格错误行或日志行 → 经 DTE 跳转到 VS 源码行（失败降级复制到剪贴板）。
/// 窗口自带主题（Window.Styles），宿主无需改 App.axaml；会话为 null 时进入"独立运行"展示模式。
/// </summary>
public partial class ProbeWindow : Window
{
    private readonly ProbeSession? _session;
    private readonly UpdateEngine? _engine;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<ProbeLogItem> _logItems = new();

    private long _refreshCount;
    private long _logItemCount;
    private long _logSeq;
    private List<SystemViewRow> _rows = new();

    /// <summary>自开窗以来已执行的刷新次数（interlocked；任意线程可读，冒烟断言用）。</summary>
    public long RefreshCount => Interlocked.Read(ref _refreshCount);

    /// <summary>日志清单当前显示条数（任意线程可读，冒烟断言用）。</summary>
    public long LogItemCount => Interlocked.Read(ref _logItemCount);

    /// <summary>设计器/独立展示用（无会话）。</summary>
    public ProbeWindow() : this(null, null) { }

    public ProbeWindow(ProbeSession? session, UpdateEngine? engine)
    {
        _session = session;
        _engine = engine;
        InitializeComponent();

        Title = _session is null
            ? "TickEngine Probe — 独立运行（未装配探针会话）"
            : "TickEngine Probe — 系统性能探测";

        ChkBeatLog.IsChecked = _session?.BeatLog?.Enabled ?? false;
        ChkBeatLog.IsEnabled = _session?.BeatLog is not null;
        ChkBeatLog.IsCheckedChanged += (_, _) => _session?.SetBeatLogEnabled(ChkBeatLog.IsChecked == true);

        ChkErrorsOnly.IsCheckedChanged += (_, _) => RebuildLogItems();
        BtnClear.Click += (_, _) =>
        {
            _session?.Log.Clear();
            _logItems.Clear();
            Interlocked.Exchange(ref _logItemCount, 0);
            _logSeq = 0;
            LblHint.Text = "日志已清空";
        };

        StatsGrid.DoubleTapped += OnGridDoubleTapped;
        LogList.DoubleTapped += OnLogDoubleTapped;

        LogList.ItemsSource = _logItems;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshUi();
        Closed += (_, _) => _timer.Stop();   // 关窗即停表；会话可再次开窗
        _timer.Start();
        RefreshUi();
    }

    private void RefreshUi()
    {
        Interlocked.Increment(ref _refreshCount);

        if (_session is null)
        {
            LblHostCpu.Text = "宿主UI线程CPU: -（独立运行，无会话）";
            LblPaint.Text = "绘制: -";
            LblEpoch.Text = "纪元: -";
            return;
        }

        // “宿主UI线程CPU” = 被 CaptureUiThread 捕获的线程（宿主 UI 线程/控制台主泵线程）。
        // 注意：宿主没调用 CaptureUiThread 时是“无数据”，必须显示未采集——不能显示看似合理的 0%。
        var probe = _session.ThreadProbe;
        LblHostCpu.Text = probe is null
            ? "宿主UI线程CPU: -（未启用采集）"
            : probe.HasBaseline
                ? $"宿主UI线程CPU: {probe.UiCpuFraction * 100:F0}%"
                : "宿主UI线程CPU: -（宿主未调用 CaptureUiThread）";
        var paint = _session.PaintStats;
        LblPaint.Text = paint is not null && paint.PaintCount > 0
            ? $"绘制: 均{paint.AverageMs:F2}ms 最大{paint.MaxMs:F2}ms"
            : "绘制: -（画布未上报）";
        LblEpoch.Text = _engine is not null
            ? $"纪元: {_engine.GlobalSeconds:F1}s (tick {_engine.GlobalTick})"
            : "纪元: -";

        // 表格：行数少，整体替换 ItemsSource（D9 教训：不逐格重建）；
        // 替换会清掉选中项，故按系统名恢复选中（否则用户正在看的行每 0.5s 被清一次，
        // 双击跳转会因选中项丢失而失效）。
        var views = _session.Stats.Latest;
        _rows = new List<SystemViewRow>(views.Count);
        foreach (var v in views) { _rows.Add(SystemViewRow.From(v)); }
        string? selectedName = (StatsGrid.SelectedItem as SystemViewRow)?.Name;
        StatsGrid.ItemsSource = _rows;
        if (selectedName is not null)
        {
            var again = _rows.FirstOrDefault(r => r.Name == selectedName);
            if (again is not null) { StatsGrid.SelectedItem = again; }
        }

        DrainLog();
    }

    /// <summary>按序号增量拉取日志（环形覆盖时整表重建；显示条数同样以环形容量为上限）。</summary>
    private void DrainLog()
    {
        if (_session is null) { return; }
        var entries = _session.Log.GetSince(_logSeq, out long baseSeq);
        if (_logSeq < baseSeq) { RebuildLogItems(); return; }
        foreach (var entry in entries)
        {
            _logSeq = entry.Seq + 1;
            if (ChkErrorsOnly.IsChecked == true && entry.Level != ProbeLogLevel.Error) { continue; }
            _logItems.Add(new ProbeLogItem(entry));
            Interlocked.Increment(ref _logItemCount);
        }
        TrimLogItems();
        if (ChkAutoScroll.IsChecked == true && _logItems.Count > 0)
        {
            LogList.ScrollIntoView(_logItems[^1]);
        }
    }

    /// <summary>
    /// 把显示列表裁到环形容量：显示列表若不设上限，会随会话时长无界增长
    /// （也把"环形只保留 2000 条异常对象"的内存约束在 UI 侧一并破掉）。
    /// </summary>
    private void TrimLogItems()
    {
        int cap = _session?.Log.Capacity ?? ProbeLog.DefaultCapacity;
        int excess = _logItems.Count - cap;
        if (excess <= 0) { return; }
        for (int i = 0; i < excess; i++) { _logItems.RemoveAt(0); }
        Interlocked.Add(ref _logItemCount, -excess);
    }

    /// <summary>按当前过滤条件重建日志清单（过滤切换/环形覆盖）。</summary>
    private void RebuildLogItems()
    {
        if (_session is null) { return; }
        _logItems.Clear();
        Interlocked.Exchange(ref _logItemCount, 0);
        _logSeq = 0;
        foreach (var entry in _session.Log.GetSince(0, out _))
        {
            _logSeq = entry.Seq + 1;
            if (ChkErrorsOnly.IsChecked == true && entry.Level != ProbeLogLevel.Error) { continue; }
            _logItems.Add(new ProbeLogItem(entry));
            Interlocked.Increment(ref _logItemCount);
        }
        if (ChkAutoScroll.IsChecked == true && _logItems.Count > 0)
        {
            LogList.ScrollIntoView(_logItems[^1]);
        }
    }

    /// <summary>双击表格行 → 有异常现场则跳转源码（Avalonia DataGrid 按行判定，比按列更省事）。</summary>
    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (StatsGrid.SelectedItem is not SystemViewRow row || row.Exception is not { } ex)
        {
            LblHint.Text = "该行没有异常现场可跳转";
            return;
        }
        JumpAndReport(ex, ProbeCodeJump.ToClipboardText(ex));
    }

    /// <summary>双击日志行 → 优先用条目携带的异常，否则从行文本解析完整路径。</summary>
    private void OnLogDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LogList.SelectedItem is not ProbeLogItem item) { return; }
        var entry = item.Entry;

        if (entry.Exception is { } ex)
        {
            JumpAndReport(ex, ProbeCodeJump.ToClipboardText(ex));
            return;
        }
        if (ProbeCodeJump.TryResolveLocationInText(entry.Message, out string file, out int line))
        {
            var result = ProbeCodeJump.JumpToFile(file, line);
            if (result.Located) { LblHint.Text = result.Describe(); return; }
            CopyAndReport(entry.Message, result.Describe() + "：已复制该行日志到剪贴板");
            return;
        }
        CopyAndReport(entry.Message, "该行没有可跳转的源码位置：已复制该行日志");
    }

    private void JumpAndReport(Exception exception, string clipboardPayload)
    {
        var result = ProbeCodeJump.JumpToSource(exception);
        if (result.Located) { LblHint.Text = result.Describe(); return; }
        CopyAndReport(clipboardPayload, result.Describe() + "：已复制异常+堆栈到剪贴板");
    }

    private async void CopyAndReport(string payload, string status)
    {
        try
        {
            var clipboard = Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetValueAsync(DataFormat.Text, payload);   // Avalonia 12：无 SetTextAsync
                LblHint.Text = status;
            }
            else
            {
                LblHint.Text = "剪贴板不可用：跳转与复制均失败";
            }
        }
        catch (Exception ex)
        {
            LblHint.Text = $"跳转与复制均失败: {ex.GetType().Name}";
        }
    }
}

/// <summary>日志清单行 VM（框架无关的显示预处理：时间/级别文本 + 错误着色）。</summary>
internal sealed class ProbeLogItem
{
    public ProbeLogItem(ProbeLogEntry entry)
    {
        Entry = entry;
        TimeText = entry.Timestamp.ToString("HH:mm:ss.fff");
        LevelText = entry.Level == ProbeLogLevel.Error ? "ERROR" : "INFO";
        Message = entry.Message;
        Foreground = entry.Level == ProbeLogLevel.Error ? Brushes.Firebrick : null;
    }

    public ProbeLogEntry Entry { get; }
    public string TimeText { get; }
    public string LevelText { get; }
    public string Message { get; }

    /// <summary>Error 行标红；null = 用主题默认前景色。</summary>
    public IBrush? Foreground { get; }
}
