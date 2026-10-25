using System.Diagnostics;
using System.Text;

namespace SimOCR.Diagnostics;

internal enum LogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// 极简滚动文件日志（不引入第三方依赖）。关键路径失败必须可见：热键注册、抓屏、保存、复制、置顶、自启。
/// 单文件上限 2MB，保留最近 7 天。
/// </summary>
internal sealed class LogService : IDisposable
{
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _logDirectory;
    private readonly string _filePath;
    private bool _disposed;

    public LogService(PathService paths, LogLevel minLevel = LogLevel.Information)
    {
        _logDirectory = paths.LogDirectory;
        MinLevel = minLevel;
        Directory.CreateDirectory(_logDirectory);
        _filePath = Path.Combine(_logDirectory, $"simocr-{DateTime.Now:yyyyMMdd}.log");
        PurgeOldLogs();
    }

    /// <summary>可在配置加载后调整（config.json 的 diagnostics.logLevel）。</summary>
    public LogLevel MinLevel { get; set; }

    public string LogDirectory => _logDirectory;

    public void Debug(string message) => Write(LogLevel.Debug, message, null);
    public void Info(string message) => Write(LogLevel.Information, message, null);
    public void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < MinLevel || _disposed)
        {
            return;
        }

        var sb = new StringBuilder(160);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5));
        sb.Append(" [T").Append(Environment.CurrentManagedThreadId).Append("] ");
        sb.Append(message);
        if (ex is not null)
        {
            sb.AppendLine();
            sb.Append(ex);
        }

        var line = sb.ToString();

        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败绝不影响主流程
            }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    private void RollIfNeeded()
    {
        var fi = new FileInfo(_filePath);
        if (!fi.Exists || fi.Length < MaxFileBytes)
        {
            return;
        }

        var rolled = Path.Combine(
            _logDirectory,
            $"simocr-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            File.Move(_filePath, rolled, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }

    private void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(_logDirectory, "simocr-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
