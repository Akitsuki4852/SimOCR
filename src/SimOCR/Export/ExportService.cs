using System.IO;
using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;
using SimOCR.Ocr;
using SimOCR.Settings;
using SimOCR.Storage;

namespace SimOCR.Export;

internal sealed record ExportResult(bool Success, string? Path, string? Error)
{
    public static ExportResult Ok(string path) => new(true, path, null);

    public static ExportResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 导出服务：决定写到哪里（复用截图保存目录与文件名模板）、调用具体导出器、记录日志。
/// 默认目录与 PNG 截图一致（设置里的保存目录，未设置则"下载"），文件名同模板，重名自动 _1、_2…
/// </summary>
internal sealed class ExportService
{
    private readonly SettingsService _settings;
    private readonly ImageStorageService _storage;
    private readonly LogService _log;

    internal ExportService(SettingsService settings, ImageStorageService storage, LogService log)
    {
        _settings = settings;
        _storage = storage;
        _log = log;
    }

    /// <summary>
    /// 按模板算出本次导出的**建议默认路径**（供"另存为"对话框当初始文件名）。
    /// 它只是名字，**必然有 TOCTOU 窗口**，因此真正写盘不走它 —— 见 <see cref="Export"/>。
    /// </summary>
    internal string BuildDefaultPath(OcrExportFormat format)
    {
        var settings = _settings.Current;
        var directory = _storage.ResolveSaveDirectory(settings);
        var fileName = _storage.BuildFileName(settings, DateTime.Now, format.Extension());
        return UniqueFile.Suggest(directory, fileName);
    }

    /// <summary>
    /// 导出。
    /// <paramref name="targetPath"/> 由调用方给定时按**用户明确指定**处理（可覆盖同名文件，
    /// 与"另存为对话框里选了一个已存在的文件"一致）。
    /// 未给定时走默认路径 —— 此时**不覆盖**：路径由 <see cref="UniqueFile.Reserve"/> 原子抢占，
    /// 并发导出同名文件各自拿到不同的 <c>_1</c>/<c>_2</c>…。
    /// </summary>
    internal ExportResult Export(
        OcrExportContent content,
        OcrExportFormat format,
        BitmapSource? image,
        string? targetPath = null)
    {
        // 失败日志要能报出"卡在哪个路径"（与 ImageStorageService.SavePng 的同一写法）
        string? path = targetPath;
        try
        {
            var directory = targetPath is null
                ? _storage.ResolveSaveDirectory(_settings.Current)
                : (Path.GetDirectoryName(targetPath) ?? string.Empty);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            path = targetPath ?? UniqueFile.Reserve(
                directory,
                _storage.BuildFileName(_settings.Current, DateTime.Now, format.Extension()));

            OcrExporter.Write(format, path, content, image);

            // 算术校验摘要：让"哪几行对不上"出现在日志里（不静默改数）
            var layout = format == OcrExportFormat.Xlsx
                ? OcrTableLayout.Build(content.Model)
                : null;
            if (layout?.ValidationSummary is { } summary)
            {
                _log.Info($"表格校验：{summary}");
                foreach (var detail in layout.ValidityDetails)
                {
                    _log.Warn($"表格校验明细：{detail}");
                }
            }
            var size = new FileInfo(path).Length;
            _log.Info(
                $"导出成功：{format.DisplayName()} → {path}（{size / 1024.0:F1} KB，" +
                $"{content.Model.Lines.Count} 行识别{(content.TextEdited ? "，文本已被用户编辑" : string.Empty)}）");
            return ExportResult.Ok(path);
        }
        catch (Exception ex)
        {
            _log.Error($"导出失败：{format.DisplayName()} → {path ?? "(未确定路径)"}", ex);
            return ExportResult.Fail(FriendlyError.ForIo(ex));
        }
    }

    /// <summary>便捷重载：直接用识别结果（没有编辑过文本时）。</summary>
    internal ExportResult Export(
        OcrDocument document,
        OcrExportFormat format,
        BitmapSource? image,
        string? targetPath = null)
        => Export(OcrExportContent.FromDocument(document), format, image, targetPath);
}
