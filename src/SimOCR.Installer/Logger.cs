using System.Diagnostics.CodeAnalysis;

namespace SimOCR.Installer;

/// <summary>
/// 安装器自己的日志。写到 %LOCALAPPDATA%\SimOCR\logs\installer-*.log。
///
/// 为什么要有它：安装器是 WinExe（没有控制台），自动化验收要能拿到证据。
/// 验收脚本读这个文件 + 进程退出码就够判定，不需要截屏或交互。
/// 日志只记"每 10% 一次"的里程碑，不逐文件写，避免几十 MB 的日志。
///
/// 每次运行写自己的文件（<c>installer-时间戳-pid.log</c>），所以同一目录里可以并存
/// 多次尝试的完整记录 —— 自动化验收靠"最新那份"定位本次运行。这条性质在
/// <see cref="CreateInTemp"/> 切到 %TEMP% 之后仍然成立（那只是换了目录）。
///
/// 句柄用 <c>FileShare.Read</c> 打开、随 <see cref="Dispose"/> 释放：这意味着
/// <b>只要本进程还持有它，数据目录就删不掉</b>。要删数据目录的调用方必须先释放
/// （见 <see cref="UninstallService.PurgeDataDirectory"/>），否则 rmdir 一定失败。
/// </summary>
internal sealed class Logger : IDisposable
{
    /// <summary>
    /// 日志保留上限（份数）。**0.10.5 补的保留策略**：
    /// 过去每次运行都新建 <c>installer-{stamp}-{pid}.log</c> 且**从不清理**，
    /// 而它写的正是应用自己的日志目录（<c>%LOCALAPPDATA%\SimOCR\logs</c>，应用侧有 2MB 滚动 + 7 天清理）
    /// —— 装/卸几次就攒一堆永不消失的文件。
    /// 取 20 份：足够覆盖"最近若干次尝试 + 同时用 --verify/--uninstall 交叉排查"，
    /// 又远小于会撑爆目录的数量。
    /// </summary>
    private const int MaxRetainedLogFiles = 20;

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly string _path;
    private bool _disposed;

    internal string Path => _path;

    /// <summary>这条日志是否真的落在磁盘上（false = 退化成"没有日志"，只能在对话框里报错）。</summary>
    internal bool IsPersisted => _writer is not null;

    private Logger(string path, StreamWriter? writer)
    {
        _path = path;
        _writer = writer;
    }

    internal static Logger Create(string logDirectory)
    {
        // （CA2000）：这里先记"自己建的对象"，在返回前把本地变量置空表示**所有权转移给调用方**，
        // finally 只释放没有移交的那些 —— 这正是 CA2000 消息里给的首选写法（try-finally + 置空）。
        // 行为不变：成功路径返回同一个 Logger（句柄不关），失败路径返回 null 的退化 Logger。
        Logger? primary = null;
        Logger? fallback = null;
        try
        {
            if (TryOpen(logDirectory, out primary))
            {
                var owned = primary;
                primary = null;
                return owned;
            }

            // 主日志目录写不进去（实测：从沙箱/受限上下文启动时，连 %LOCALAPPDATA% 都建不了文件）。
            // 退到 %TEMP% —— 至少要留下证据，否则用户只能看到一个"Access denied"。
            var temp = System.IO.Path.GetTempPath();
            if (!IsSameDirectory(temp, logDirectory) && TryOpen(temp, out fallback))
            {
                fallback.Warn($"日志目录 {logDirectory} 不可写，已改用 {temp}");
                fallback.Info($"identity: {ProcessIdentity.Describe()}");
                var owned = fallback;
                fallback = null;
                return owned;
            }

            // 连 %TEMP% 都写不进去：退化成"没有日志"，但安装流程继续（错误会走对话框）
            return new Logger(System.IO.Path.Combine(logDirectory, "SimOCR-Setup.log"), null);
        }
        finally
        {
            primary?.Dispose();
            fallback?.Dispose();
        }
    }

    /// <summary>
    /// 直接开在 %TEMP% 里，<b>不先试主日志目录</b>。
    ///
    /// 唯一的调用方是卸载时"数据目录已经被删掉"之后的收尾日志：那时
    /// <c>%LOCALAPPDATA%\SimOCR\logs</c> 已经不存在，再走 <see cref="Create"/>
    /// 只会白建一次目录（等于把刚删掉的数据目录又创建回来）。
    /// </summary>
    internal static Logger CreateInTemp()
    {
        var temp = System.IO.Path.GetTempPath();
        return TryOpen(temp, out var log)
            ? log
            : new Logger(System.IO.Path.Combine(temp, "SimOCR-Setup.log"), null);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        // 注意：类里有实例属性 Path，这里必须写全 System.IO.Path。
        return string.Equals(
            System.IO.Path.GetFullPath(left).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            System.IO.Path.GetFullPath(right).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 尝试在 <paramref name="directory"/> 里开一份日志。
    /// 返回 <c>true</c> 表示成功，且新建 <see cref="Logger"/> 的**所有权交给调用方**；
    /// 返回 <c>false</c> 时 <paramref name="logger"/> 必为 <c>null</c>（用 out 参数表达所有权转移，
    /// 让 CA2000 能证明"非 null 分支已移交"，不必放宽规则）。
    /// </summary>
    private static bool TryOpen(string directory, [NotNullWhen(true)] out Logger? logger)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(directory, $"installer-{stamp}-{Environment.ProcessId}.log");
        logger = null;
        try
        {
            Directory.CreateDirectory(directory);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };

            var opened = new Logger(path, writer);
            logger = opened;
            opened.PruneOldLogs();
            return true;
        }
        catch (Exception)
        {
            // 构造到一半失败：把半成品（可能已打开的句柄）释放掉再报失败。
            // 旧写法只是丢掉引用；这里释放不改变可观测行为（失败仍然退化成"没有日志"）。
            logger?.Dispose();
            logger = null;
            return false;
        }
    }

    /// <summary>
    /// 清掉超出保留上限的旧日志（只动本进程目录里的 <c>installer-*.log</c>，
    /// 按写入时间从新到旧保留 <see cref="MaxRetainedLogFiles"/> 份；**不删自己这份**）。
    /// 删不掉（被别的进程占用）就跳过 —— 保留策略绝不能影响安装。
    /// </summary>
    private void PruneOldLogs()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var stale = new DirectoryInfo(directory)
                .GetFiles("installer-*.log", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(f.FullName, _path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxRetainedLogFiles - 1)
                .ToList();

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception)
                {
                    // 被占用 / 权限不足：下一次运行再试
                }
            }
        }
        catch (Exception)
        {
            // 列目录都可能失败（例如目录刚被删）：忽略
        }
    }

    internal void Info(string message) => Write("INFO ", message);

    internal void Warn(string message) => Write("WARN ", message);

    internal void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
            }
            catch (Exception)
            {
                // 写日志失败不影响安装
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
