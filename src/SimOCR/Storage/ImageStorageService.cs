using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;
using SimOCR.Settings;

namespace SimOCR.Storage;

internal sealed record SaveResult(bool Success, string? Path, string? Error)
{
    public static SaveResult Ok(string path) => new(true, path, null);

    public static SaveResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 图片落盘：下载目录解析 + 文件名模板 + 重名自增 + 原子写（先写后改名，避免半截文件）。
/// </summary>
internal sealed class ImageStorageService
{
    private readonly SettingsService _settings;
    private readonly LogService _log;

    public ImageStorageService(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>解析保存目录：配置优先，未配置用系统"下载"文件夹。</summary>
    public string ResolveSaveDirectory(AppSettings? settings = null)
    {
        var s = settings ?? _settings.Current;

        if (!string.IsNullOrWhiteSpace(s.Save.Directory))
        {
            return s.Save.Directory!;
        }

        try
        {
            var downloads = KnownFolders.Downloads();
            if (!string.IsNullOrWhiteSpace(downloads))
            {
                return downloads;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("获取系统下载目录失败，回退到桌面", ex);
        }

        try
        {
            return KnownFolders.Desktop();
        }
        catch (Exception ex)
        {
            _log.Error("获取桌面目录也失败，回退到临时目录", ex);
            return Path.GetTempPath();
        }
    }

    public string BuildFileName(AppSettings settings, DateTime timestamp, string extension = ".png")
        => FileNameTemplate.Render(settings.Save.FileNameTemplate, timestamp) + extension;

    /// <summary>保存为 PNG。失败时返回错误信息而不是抛异常（UI 只需提示）。</summary>
    public SaveResult SavePng(BitmapSource image, string? targetDirectory = null, string? fileName = null)
    {
        string? path = null;
        try
        {
            var settings = _settings.Current;
            var dir = targetDirectory ?? ResolveSaveDirectory(settings);
            Directory.CreateDirectory(dir);

            var name = fileName ?? BuildFileName(settings, DateTime.Now);

            // 编码到内存，避免"半截 PNG"
            var buffer = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(buffer);
            var bytes = buffer.ToArray();

            path = WriteUnique(dir, name, bytes);
            _log.Info($"已保存 PNG: {path} ({bytes.Length / 1024.0:F1} KB, {image.PixelWidth}x{image.PixelHeight})");
            return SaveResult.Ok(path);
        }
        catch (Exception ex)
        {
            _log.Error($"保存 PNG 失败: {path ?? "(未确定路径)"}", ex);
            // 详细异常（含路径）只进日志；返回给界面的是一句能照着做的话。
            return SaveResult.Fail(FriendlyError.ForIo(ex));
        }
    }

    /// <summary>
    /// 重名时追加 _1、_2…（用 <c>CreateNew</c> 抢占文件名，避免竞态）。
    /// 实现只有一份：<see cref="UniqueFile.Write"/> —— 与导出侧共用同一套原子机制。
    /// </summary>
    private static string WriteUnique(string directory, string fileName, byte[] bytes)
        => UniqueFile.Write(directory, fileName, bytes);
}
