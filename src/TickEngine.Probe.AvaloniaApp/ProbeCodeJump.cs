using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TickEngine.Probe;

/// <summary>跳转结果模式。</summary>
public enum ProbeJumpMode
{
    /// <summary>经 DTE 在运行中的 VS 里打开并定位（含回读实际落点）。</summary>
    DteLocated = 0,

    /// <summary>DTE 不可用（VS 未运行）：已用 devenv /edit 打开文件，但未定位行。</summary>
    OpenedOnly = 1,

    /// <summary>跳转失败（无位置、文件不存在、VS 不可用）。</summary>
    Failed = 2,
}

/// <summary>一次跳转的结果（供状态行显示"真验证"过的落点）。</summary>
public readonly record struct ProbeJumpResult(ProbeJumpMode Mode, string File, int Line, int ActualLine)
{
    /// <summary>是否经 DTE 定位成功。</summary>
    public bool Located => Mode == ProbeJumpMode.DteLocated;

    /// <summary>状态行/提示文本。</summary>
    public string Describe()
    {
        string name = string.IsNullOrEmpty(File) ? "(未知文件)" : Path.GetFileName(File);
        return Mode switch
        {
            ProbeJumpMode.DteLocated when ActualLine > 0 && ActualLine != Line =>
                $"已跳转 {name}:{Line}（回读落点 {ActualLine}，命令可能被 VS 调整）",
            ProbeJumpMode.DteLocated => $"已跳转 {name}:{Line}",
            ProbeJumpMode.OpenedOnly => $"VS 未运行：已用 devenv 打开 {name}，但未定位到第 {Line} 行",
            _ => "跳转失败",
        };
    }
}

/// <summary>
/// 「跳转到源码」助手：
/// 主路径 = **DTE COM**（附到运行中的 Visual Studio：OpenFile → Activate → ExecuteCommand("Edit.GoTo")，
/// 再回读 <c>ActiveDocument.Selection.ActivePoint.Line</c> 验证落点）；
/// 兜底 = <c>devenv /edit</c> 打开文件（不保证定位）→ 调用方再退化为复制堆栈到剪贴板。
/// 说明：VS 的 <c>/edit</c> 与 <c>/command</c> 之间的时序微软无契约保证，"CLI 直接定位行"实测落空（停在首行），
/// 故不再用 CLI 做定位主路径。
/// </summary>
public static class ProbeCodeJump
{
    /// <summary>Visual Studio 的 DTE ProgID（由新到旧尝试）。</summary>
    private static readonly string[] DteProgIds =
    {
        "VisualStudio.DTE.18.0",
        "VisualStudio.DTE.17.0",
        "VisualStudio.DTE.16.0",
    };

    /// <summary>源码路径 + 行号（用于从日志文本里解析位置；只认完整盘符路径）。</summary>
    private static readonly Regex TextLocation = new(
        @"(?<file>[A-Za-z]:\\[^\r\n:*?""<>|]+?\.(?:cs|fs|vb|cpp|c|h)):(?:line\s+)?(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>已解析出的 Visual Studio devenv.exe 绝对路径（未找到为 null）。</summary>
    private static readonly Lazy<string?> DevenvPath = new(FindVisualStudio);

    /// <summary>devenv.exe 绝对路径（未找到为 null）。</summary>
    public static string? VisualStudioPath => DevenvPath.Value;

    // ---------------- 位置解析 ----------------

    /// <summary>
    /// 从异常解析首个有效源码位置：优先走 <see cref="StackTrace"/> 帧信息（PDB 在场时最可靠），
    /// 失败则回退解析 <see cref="Exception.StackTrace"/> 文本。
    /// </summary>
    public static bool TryResolveLocation(Exception exception, out string file, out int line)
    {
        ArgumentNullException.ThrowIfNull(exception);
        file = string.Empty;
        line = 0;

        try
        {
            var trace = new StackTrace(exception, fNeedFileInfo: true);
            if (trace.GetFrames() is { } frames)
            {
                foreach (var frame in frames)
                {
                    string? fn = frame.GetFileName();
                    int ln = frame.GetFileLineNumber();
                    if (!string.IsNullOrEmpty(fn) && ln > 0)
                    {
                        file = fn;
                        line = ln;
                        return true;
                    }
                }
            }
        }
        catch { /* 退化为文本解析 */ }

        return TryResolveLocationInText(exception.StackTrace, out file, out line);
    }

    /// <summary>
    /// 从任意文本里解析首个"存在的文件 + 行号"（日志面板双击用：日志里的堆栈行天然带完整路径）。
    /// 只认带盘符的完整路径，避免把 <c>@ DemoSystems.cs:77</c> 这类只有文件名的片段误判。
    /// </summary>
    public static bool TryResolveLocationInText(string? text, out string file, out int line)
    {
        file = string.Empty;
        line = 0;
        if (string.IsNullOrEmpty(text)) { return false; }

        foreach (Match m in TextLocation.Matches(text))
        {
            string candidate = m.Groups["file"].Value.Trim();
            if (!int.TryParse(m.Groups["line"].Value, out int parsed) || parsed <= 0) { continue; }
            if (!File.Exists(candidate)) { continue; }
            file = candidate;
            line = parsed;
            return true;
        }
        return false;
    }

    /// <summary>可读的位置描述（"File.cs:42"；无则 null）——状态行/日志用。</summary>
    public static string? DescribeLocation(Exception exception)
        => TryResolveLocation(exception, out string file, out int line)
            ? $"{Path.GetFileName(file)}:{line}"
            : null;

    // ---------------- 跳转 ----------------

    /// <summary>跳转到异常所在源码行（解析 + 跳转一步到位）。</summary>
    public static ProbeJumpResult JumpToSource(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return TryResolveLocation(exception, out string file, out int line)
            ? JumpToFile(file, line)
            : new ProbeJumpResult(ProbeJumpMode.Failed, string.Empty, 0, 0);
    }

    /// <summary>
    /// 打开指定文件并定位到行：DTE（精确定位 + 回读落点）→ 失败退 devenv /edit（仅打开）。
    /// </summary>
    public static ProbeJumpResult JumpToFile(string file, int line)
    {
        file = (file ?? string.Empty).Trim();
        if (file.Length == 0 || line <= 0 || !File.Exists(file))
        {
            return new ProbeJumpResult(ProbeJumpMode.Failed, file, line, 0);
        }

        if (TryAttachDte(out object? dte) && dte is not null)
        {
            try
            {
                dynamic app = dte;
                dynamic window = app.ItemOperations.OpenFile(file);
                try { window.Activate(); } catch { /* 激活失败仍尝试定位 */ }
                app.ExecuteCommand("Edit.GoTo", line.ToString(CultureInfo.InvariantCulture));

                int actual = 0;
                try { actual = Convert.ToInt32(app.ActiveDocument.Selection.ActivePoint.Line); }
                catch { /* 回读失败不判定为跳转失败 */ }
                return new ProbeJumpResult(ProbeJumpMode.DteLocated, file, line, actual);
            }
            catch
            {
                // DTE 中途失败（VS 忙/权限）：落到 CLI 兜底
            }
            finally
            {
                ReleaseComObject(dte);
            }
        }

        return LaunchDevenvEdit(file)
            ? new ProbeJumpResult(ProbeJumpMode.OpenedOnly, file, line, 0)
            : new ProbeJumpResult(ProbeJumpMode.Failed, file, line, 0);
    }

    /// <summary>正在运行的 Visual Studio 是否可经 DTE 附加（= 精确跳转可用）。只读探测，无 UI 副作用。</summary>
    public static bool IsVisualStudioRunning()
    {
        if (!TryAttachDte(out object? dte) || dte is null) { return false; }
        ReleaseComObject(dte);
        return true;
    }

    /// <summary>异常的可粘贴文本（降级路径：复制到剪贴板）。</summary>
    public static string ToClipboardText(Exception exception)
        => $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";

    // ---------------- DTE ----------------

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    /// <summary>附到正在运行的 Visual Studio 实例（没有运行实例/未注册则为 false）。</summary>
    private static bool TryAttachDte(out object? dte)
    {
        foreach (string progId in DteProgIds)
        {
            try
            {
                Type? type = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (type is null) { continue; }
                Guid clsid = type.GUID;
                GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
                if (obj is not null)
                {
                    dte = obj;
                    return true;
                }
            }
            catch (COMException) { /* 该版本没有运行中的实例：试下一个 */ }
            catch { /* 其它异常：试下一个 */ }
        }
        dte = null;
        return false;
    }

    private static void ReleaseComObject(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject)) { Marshal.ReleaseComObject(comObject); }
        }
        catch { /* 释放失败不影响宿主 */ }
    }

    // ---------------- devenv 兜底 ----------------

    /// <summary>
    /// 用 devenv /edit 打开文件（无 VS 时为 false）。注意：不保证定位到行。
    /// <b>必须 UseShellExecute = true</b>：否则 devenv 会继承调用方的 stdio，
    /// 让"捕获输出"的调用方（自动化/终端管道）一直等管道关闭而假死——实测踩过。
    /// </summary>
    private static bool LaunchDevenvEdit(string file)
    {
        string? vs = DevenvPath.Value;
        if (string.IsNullOrEmpty(vs)) { return false; }
        try
        {
            var psi = new ProcessStartInfo(vs)
            {
                Arguments = $"/edit \"{file}\"",
                UseShellExecute = true,   // 脱离调用方 stdio（见 summary）
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 vswhere 找最新 Visual Studio 的 devenv.exe（找不到返回 null）。</summary>
    private static string? FindVisualStudio()
    {
        const string vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        try
        {
            if (File.Exists(vswhere))
            {
                var psi = new ProcessStartInfo(vswhere, "-latest -property productPath")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is not null)
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    if (!string.IsNullOrEmpty(output) && File.Exists(output)) { return output; }
                }
            }
        }
        catch { /* 落到下方兜底 */ }

        // 兜底：常见安装位置（18 = VS 2026，17 = VS 2022）
        string[] candidates =
        {
            @"C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\devenv.exe",
            @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe",
        };
        foreach (string c in candidates)
        {
            if (File.Exists(c)) { return c; }
        }
        return null;
    }
}
