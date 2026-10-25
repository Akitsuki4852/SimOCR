namespace SimOCR.Diagnostics;

/// <summary>
/// 统一决定"配置/日志/模型"存放位置：
/// 便携优先（exe 同目录可写就用它），不可写时回退到 %LOCALAPPDATA%\SimOCR。
/// </summary>
internal sealed class PathService
{
    private const string AppFolderName = "SimOCR";

    private PathService(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
        LogDirectory = Path.Combine(dataDirectory, "logs");
        ModelsDirectory = Path.Combine(dataDirectory, "models");
        ConfigPath = Path.Combine(dataDirectory, "config.json");
        // 覆盖层用的临时位图/裁剪图片目录（定向重认会用到）
        TempDirectory = Path.Combine(Path.GetTempPath(), AppFolderName);
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string ModelsDirectory { get; }
    public string ConfigPath { get; }
    public string TempDirectory { get; }
    public bool IsPortable { get; }

    public static PathService Create()
    {
        var exeDir = AppContext.BaseDirectory;
        if (IsWritable(exeDir))
        {
            return new PathService(exeDir, isPortable: true);
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);
        Directory.CreateDirectory(fallback);
        return new PathService(fallback, isPortable: false);
    }

    /// <summary>指定数据目录（单元测试与将来的"指定配置目录"参数使用）。</summary>
    public static PathService ForDirectory(string dataDirectory, bool portable = true)
    {
        Directory.CreateDirectory(dataDirectory);
        return new PathService(dataDirectory, portable);
    }

    /// <summary>用"写探测文件"判断目录可写（Program Files、只读 U 盘会失败）。</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".simocr-write-probe-{Guid.NewGuid():N}");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
