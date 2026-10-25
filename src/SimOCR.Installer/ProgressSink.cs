namespace SimOCR.Installer;

/// <summary>解压进度接收端。安装器在"有 UI"和"静默"两种场景下用不同实现。</summary>
internal interface IProgressSink
{
    /// <summary>开始显示进度。</summary>
    void Start(string title, string detail, long totalBytes);

    /// <summary>推进进度。<paramref name="line"/> 是给用户看的一行说明。</summary>
    void Report(long doneBytes, long totalBytes, string line);

    /// <summary>用户是否点了取消。</summary>
    bool Cancelled { get; }

    /// <summary>关闭进度显示。</summary>
    void Stop();
}

/// <summary>
/// 记录"进度里程碑"到日志的接收端（和 UI 无关）。安装器在静默模式下也会挂它，
/// 这样验收只看日志就能确认解压真的把全部文件写完了。
/// </summary>
internal sealed class LogProgressSink : IProgressSink
{
    private readonly Logger _log;
    private readonly string _label;
    private int _lastBucket = -1;

    internal LogProgressSink(Logger log, string label)
    {
        _log = log;
        _label = label;
    }

    public bool Cancelled => false;

    public void Start(string title, string detail, long totalBytes)
        => _log.Info($"{_label}:开始 {title} {detail} 总计={Format(totalBytes)}");

    public void Report(long doneBytes, long totalBytes, string line)
    {
        if (totalBytes <= 0)
        {
            return;
        }

        var bucket = (int)(doneBytes * 10 / totalBytes);
        if (bucket != _lastBucket)
        {
            _lastBucket = bucket;
            _log.Info($"{_label}:进度 {bucket * 10}% ({Format(doneBytes)}/{Format(totalBytes)}) {line}");
        }
    }

    public void Stop()
    {
    }

    private static string Format(long bytes) => $"{bytes / 1048576.0:F1}MB";
}

/// <summary>
/// 把多个进度接收端串起来（日志 + 原生对话框）。
/// </summary>
internal sealed class CompositeProgressSink : IProgressSink
{
    private readonly IProgressSink[] _sinks;

    internal CompositeProgressSink(params IProgressSink[] sinks) => _sinks = sinks;

    public bool Cancelled => _sinks.Any(s => s.Cancelled);

    public void Start(string title, string detail, long totalBytes)
    {
        foreach (var sink in _sinks)
        {
            sink.Start(title, detail, totalBytes);
        }
    }

    public void Report(long doneBytes, long totalBytes, string line)
    {
        foreach (var sink in _sinks)
        {
            sink.Report(doneBytes, totalBytes, line);
        }
    }

    public void Stop()
    {
        foreach (var sink in _sinks)
        {
            sink.Stop();
        }
    }
}
