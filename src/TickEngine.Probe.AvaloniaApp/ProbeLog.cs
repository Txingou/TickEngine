using System.Text;

namespace TickEngine.Probe;

/// <summary>探针日志级别（Info = Console.Out，Error = Console.Error / 引擎 SystemFaulted）。</summary>
public enum ProbeLogLevel
{
    /// <summary>普通输出。</summary>
    Info = 0,

    /// <summary>错误（stderr 或引擎异常隔离上报）。</summary>
    Error = 1,
}

/// <summary>
/// 一条探针日志（不可变；Seq 为全局递增序号，供 UI 增量拉取）。
/// <see cref="Exception"/> 可选携带：引擎故障条目带上原始异常，日志面板双击即可精确定位源码
/// （Console 输出没有异常可带 → 面板回退为从文本里解析完整路径）。
/// </summary>
public readonly record struct ProbeLogEntry(long Seq, DateTime Timestamp, ProbeLogLevel Level,
                                             string Message, Exception? Exception = null);

/// <summary>
/// 探针日志缓冲（线程安全，环形上限 <see cref="DefaultCapacity"/> 条）：
/// 写侧任意线程（Console tee / 引擎 SystemFaulted / 宿主显式写入），读侧 UI 线程按序号增量拉取。
/// 与呈现解耦——窗口没开时照样缓冲，开窗时回放历史。
/// </summary>
public sealed class ProbeLog
{
    /// <summary>环形缓冲默认上限（超出丢最旧）。</summary>
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly ProbeLogEntry[] _buffer;
    private int _count;      // 当前已填条数（≤ 容量）
    private long _total;     // 累计写入条数 = 下一条的 Seq

    public ProbeLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
        _buffer = new ProbeLogEntry[capacity];
    }

    /// <summary>累计写入条数（含已被环形覆盖的旧条；UI 侧据此判断是否漏读）。</summary>
    public long TotalCount
    {
        get { lock (_gate) { return _total; } }
    }

    /// <summary>当前仍保留的条数。</summary>
    public int RetainedCount
    {
        get { lock (_gate) { return _count; } }
    }

    /// <summary>环形容量（UI 侧据此限制自己的显示条数，避免显示列表无界增长）。</summary>
    public int Capacity => _buffer.Length;

    /// <summary>写一条日志（任意线程）。<paramref name="exception"/> 可选：供日志面板双击跳转精确定位。</summary>
    public void Write(ProbeLogLevel level, string message, Exception? exception = null)
    {
        message ??= string.Empty;
        lock (_gate)
        {
            var entry = new ProbeLogEntry(_total, DateTime.Now, level, message, exception);
            _buffer[(int)(_total % _buffer.Length)] = entry;
            _total++;
            if (_count < _buffer.Length) { _count++; }
        }
    }

    /// <summary>Info 便捷重载。</summary>
    public void Info(string message) => Write(ProbeLogLevel.Info, message);

    /// <summary>Error 便捷重载（可带原始异常，便于双击跳转）。</summary>
    public void Error(string message, Exception? exception = null) => Write(ProbeLogLevel.Error, message, exception);

    /// <summary>
    /// 拉取序号 ≥ sinceSeq 的全部保留条目（UI 定时器调用）。
    /// 若 sinceSeq 已被环形覆盖（落后太多），从最旧保留条开始，并通过 baseSeq 告知调用方。
    /// </summary>
    public IReadOnlyList<ProbeLogEntry> GetSince(long sinceSeq, out long baseSeq)
    {
        lock (_gate)
        {
            baseSeq = _total - _count;                  // 最旧保留条的 Seq
            long from = Math.Max(sinceSeq, baseSeq);
            if (from >= _total) { return Array.Empty<ProbeLogEntry>(); }

            var list = new List<ProbeLogEntry>((int)(_total - from));
            for (long seq = from; seq < _total; seq++)
            {
                var e = _buffer[(int)(seq % _buffer.Length)];
                if (e.Seq == seq) { list.Add(e); }      // 防御：槽位未被覆盖时才有效
            }
            return list;
        }
    }

    /// <summary>清空缓冲（UI「清空」按钮）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _count = 0;
            // _total 不归零：序号单调，避免 UI 侧把清空误判成“环形覆盖”
        }
    }
}

/// <summary>
/// Console 输出捕获：把 Console.Out/Error 包成 tee（一份照写原始流 → 控制台照常显示，一份进探针日志）。
/// 由 <see cref="ProbeSession.Attach"/> 安装、<see cref="ProbeSession.Dispose"/> 还原。
/// 行级缓冲：无换行的 Write 会累积到换行/Flush 才成一条日志（避免半行噪音）。
/// <para>
/// 还原的关键：<c>Console.SetOut</c> 会用 <c>SyncTextWriter</c> 把传入的 writer 再包一层，
/// 所以 <c>Console.Out is ProbeTeeWriter</c> 永远为假——必须记住“实际装上去的那个包装对象”
/// 才能判断当前是否仍是我们装的（否则还原静默失效，每次 Attach 都会叠加一层 tee）。
/// </para>
/// </summary>
internal sealed class ProbeConsoleCapture
{
    private readonly ProbeLog _log;
    private readonly object _gate = new();

    private TextWriter? _originalOut;
    private TextWriter? _originalError;
    private TextWriter? _installedOut;      // Console.Out 实际返回的包装对象
    private TextWriter? _installedError;
    private ProbeTeeWriter? _teeOut;
    private ProbeTeeWriter? _teeError;

    public ProbeConsoleCapture(ProbeLog log) => _log = log;

    /// <summary>安装 tee（幂等：已安装则跳过；跨线程安全）。</summary>
    public void Install()
    {
        lock (_gate)
        {
            if (_originalOut is not null) { return; }
            _originalOut = Console.Out;
            _originalError = Console.Error;

            _teeOut = new ProbeTeeWriter(_originalOut, _log, ProbeLogLevel.Info);
            _teeError = new ProbeTeeWriter(_originalError, _log, ProbeLogLevel.Error);
            Console.SetOut(_teeOut);
            Console.SetError(_teeError);

            // 记住包装后的实例（Console 返回 SyncTextWriter），还原时按它比对
            _installedOut = Console.Out;
            _installedError = Console.Error;
        }
    }

    /// <summary>还原原始 Console.Out/Error（仅当当前仍是我们装的那一层；幂等）。</summary>
    public void Uninstall()
    {
        lock (_gate)
        {
            try
            {
                if (_installedOut is not null && ReferenceEquals(Console.Out, _installedOut))
                {
                    _teeOut?.FlushPendingLine();     // 不丢最后一段未换行的输出
                    if (_originalOut is not null) { Console.SetOut(_originalOut); }
                }
            }
            catch { /* 还原失败不影响宿主 */ }

            try
            {
                if (_installedError is not null && ReferenceEquals(Console.Error, _installedError))
                {
                    _teeError?.FlushPendingLine();
                    if (_originalError is not null) { Console.SetError(_originalError); }
                }
            }
            catch { /* 同上 */ }

            _originalOut = null;
            _originalError = null;
            _installedOut = null;
            _installedError = null;
            _teeOut = null;
            _teeError = null;
        }
    }

    /// <summary>tee 写入器：原样写内层 + 逐行喂日志。</summary>
    private sealed class ProbeTeeWriter : TextWriter
    {
        /// <summary>单条日志的字符上限（超长行截断，避免一次 WriteLine 就吃掉几百 MB）。</summary>
        private const int MaxLineLength = 4096;

        private readonly TextWriter _inner;
        private readonly ProbeLog _log;
        private readonly ProbeLogLevel _level;
        private readonly StringBuilder _partial = new();
        private readonly object _gate = new();

        public ProbeTeeWriter(TextWriter inner, ProbeLog log, ProbeLogLevel level)
        {
            _inner = inner;
            _log = log;
            _level = level;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            lock (_gate)
            {
                if (value == '\n') { EmitPendingLineLocked(); }
                else
                {
                    _partial.Append(value);
                    if (_partial.Length > MaxLineLength) { EmitPendingLineLocked(); }
                }
            }
        }

        public override void Write(string? value)
        {
            _inner.Write(value);
            if (!string.IsNullOrEmpty(value))
            {
                lock (_gate) { AppendLocked(value); }
            }
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(value)) { AppendLocked(value); }
                EmitPendingLineLocked();
            }
        }

        public override void WriteLine()
        {
            _inner.WriteLine();
            lock (_gate) { EmitPendingLineLocked(); }
        }

        public override void Flush()
        {
            _inner.Flush();
            FlushPendingLine();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { FlushPendingLine(); }
            base.Dispose(disposing);
        }

        /// <summary>把未成行的残留内容作为一条日志输出（Flush/WriteLine/卸载时调用）。</summary>
        public void FlushPendingLine()
        {
            lock (_gate) { EmitPendingLineLocked(); }
        }

        /// <summary>按换行切分并把完整行喂给日志（单次扫描，避免逐行整串复制）。</summary>
        private void AppendLocked(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') { continue; }
                _partial.Append(text, start, i - start);
                EmitPendingLineLocked();
                start = i + 1;
            }
            if (start < text.Length)
            {
                _partial.Append(text, start, text.Length - start);
                if (_partial.Length > MaxLineLength) { EmitPendingLineLocked(); }
            }
        }

        private void EmitPendingLineLocked()
        {
            if (_partial.Length == 0) { return; }
            string line = _partial.ToString().TrimEnd('\r');
            _partial.Clear();
            if (line.Length > MaxLineLength)
            {
                line = string.Concat(line.AsSpan(0, MaxLineLength), $"…(+{line.Length - MaxLineLength} chars)");
            }
            _log.Write(_level, line);
        }
    }
}
