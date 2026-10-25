using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SimOCR.Diagnostics;
using SkiaSharp;

namespace SimOCR.Ocr;

/// <summary>
/// 识别参数档：
///   * <see cref="Standard"/> —— 快速识别用（沿用设置页的 <c>ImgResize</c>/<c>TextScore</c>）；
///   * <see cref="Enhance"/> —— 增强识别用（短边自适应 + 更高检测分辨率 + 更宽召回，见
///     <see cref="RecognitionService.BuildEnhanceOptions"/>）。
/// </summary>
internal enum OcrProfile
{
    Standard,
    Enhance,
}

/// <summary>识别服务：把一张位图（选区/剪贴板图）变成带坐标与置信度的文本行。</summary>
internal interface IRecognitionService
{
    /// <summary>模型是否齐全（不齐时 UI 应禁用导出并提示 <see cref="StatusMessage"/>）。</summary>
    bool IsAvailable { get; }

    string StatusMessage { get; }

    Task<OcrDocument> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default, OcrProfile profile = OcrProfile.Standard);

    /// <summary>
    /// 只识别某个子区域（编辑模式的"补识别"用）。返回的坐标会平移回原图。
    /// <paramref name="limitSideLen"/> 只在增强档生效：短边自适应会把**短边放大到该值**，
    /// 对单元格这种小裁切必须给小值（否则 57px 高的小格会被放大 27 倍，一次要 5 秒）。
    /// </summary>
    Task<OcrDocument> RecognizeRegionAsync(BitmapSource image, Int32Rect region, CancellationToken cancellationToken = default, OcrProfile profile = OcrProfile.Standard, int limitSideLen = 0);
}

/// <summary>
/// RapidOcrNet（PP-OCRv5 中文模型）封装。
///
/// 选型与参数依据（保留实测结论、不依赖仓库外的报告）：在"行数召回 + 文本一致率"
/// 两个指标上，"PythonCompat + ImgResize 2000 + TextScore 0.3" 是候选参数里最好的。
///
/// 线程约定：
///   * 引擎实例**懒加载**（首次识别约 0.5s），加载与推理都在后台线程；
///   * ONNX Runtime 的 session 不是为并发推理准备的，因此**用锁串行化** Detect 调用
///     （一次识别几百毫秒，串行不会成为瓶颈，也不会踩并发坑）。
/// </summary>
internal sealed class RecognitionService : IRecognitionService, IDisposable
{
    private readonly LogService _log;
    private readonly SimOCR.Settings.SettingsService? _settings;
    private readonly object _gate = new();
    private RapidOcr? _engine;
    private bool _disposed;
    private string? _lastOptionsDescription;

    internal RecognitionService(LogService log, SimOCR.Settings.SettingsService? settings = null, OcrModelSet? models = null)
    {
        _log = log;
        _settings = settings;
        // 设置里指定了模型目录就用它（不可用时 Locate 会回退到约定目录并记警告）；
        // 注意：这里只在构造时定位一次，改设置要重启才会重新加载模型 —— 设置页的提示里写明了。
        Models = models ?? OcrModelSet.Locate(settings?.Current.Ocr.ModelDirectory, log);
        _log.Info($"OCR: {Models.DescribeForLog()}");
    }

    internal OcrModelSet Models { get; }

    public bool IsAvailable => Models.IsComplete;

    public string StatusMessage => Models.Describe();

    /// <summary>V1 定稿参数（可在设置页调整，见 <see cref="SimOCR.Settings.OcrSettings"/>）。</summary>
    internal static RapidOcrOptions BuildOptions(float textScore = 0.3f, int imgResize = 2000)
        => RapidOcrOptions.PythonCompat with { ImgResize = imgResize, TextScore = textScore };

    /// <summary>
    /// **增强识别的参数档**：走"短边自适应"路径（<c>ImgResize=0</c>）而不是 legacy 长边封顶。
    ///
    /// 为什么这是本产品唯一真正有效的"放大"：
    ///   * `ImgResize &gt; 0` 是 legacy **长边封顶且永不放大**，并且它的 `/32` 向下取整很凶 ——
    ///     实测把 730×140 的**区域裁切**压成 672×96（高度丢掉 31%），小字/密集表越认越糊；
    ///   * `ImgResize = 0` 才会按 `LimitSideLen` 把**短边顶到目标**（可放大），`MaxSideLen` 才生效。
    ///   另外 `WidthHeightRatio = -1` 关掉"宽扁裁切自动加白边"（按行/按列重认时必然又宽又扁）。
    /// </summary>
    internal static RapidOcrOptions BuildEnhanceOptions(float textScore = 0.3f, int limitSideLen = 1536)
        => RapidOcrOptions.PythonCompat with
        {
            ImgResize = 0,
            MaxSideLen = 4000,
            LimitSideLen = Math.Clamp(limitSideLen, 96, 1536),
            WidthHeightRatio = -1,
            TextScore = Math.Min(textScore, 0.2f),
        };

    /// <summary>每次识别都读一次设置，所以改完立即生效（不用重启）。</summary>
    private RapidOcrOptions CurrentOptions(OcrProfile profile, int limitSideLen = 0)
    {
        var ocr = _settings?.Current.Ocr;
        var textScore = (float)Math.Clamp(ocr?.TextScore ?? 0.3, 0.0, 1.0);
        var imgResize = Math.Clamp(ocr?.ImgResize ?? 2000, 320, 4000);

        // 参数变化时记一条日志（支持排查"设置到底有没有生效"）
        var description = profile == OcrProfile.Enhance
            ? $"增强档 TextScore≤0.2 ImgResize=0(短边自适应) LimitSideLen={(limitSideLen > 0 ? limitSideLen : 1536)} MaxSideLen=4000"
            : $"TextScore={textScore:0.0#} ImgResize={imgResize}";
        if (_lastOptionsDescription != description)
        {
            _lastOptionsDescription = description;
            _log.Info($"OCR 参数：{description}");
        }

        return profile == OcrProfile.Enhance
            ? BuildEnhanceOptions(textScore, limitSideLen > 0 ? limitSideLen : 1536)
            : BuildOptions(textScore, imgResize);
    }

    public Task<OcrDocument> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default, OcrProfile profile = OcrProfile.Standard)
    {
        // 关键：BitmapSource 是 DispatcherObject，属于创建它的线程（UI 线程）。
        // 覆盖层裁出来的 CroppedBitmap **没有冻结**，后台线程读 .Format/CopyPixels 会抛
        // "调用线程无法访问此对象"（整链路自检实测踩到）。所以：
        //   * 像素拷贝（含区域裁剪）**在调用线程上同步做完**；
        //   * 后台线程只碰裸像素与 Skia，不再触碰任何 WPF 对象。
        var prepared = PreparePixels(image, Int32Rect.Empty);
        return Task.Run(() => Recognize(prepared, cancellationToken, profile), cancellationToken);
    }

    public Task<OcrDocument> RecognizeRegionAsync(BitmapSource image, Int32Rect region, CancellationToken cancellationToken = default, OcrProfile profile = OcrProfile.Standard, int limitSideLen = 0)
    {
        var prepared = PreparePixels(image, region);
        return Task.Run(() => Recognize(prepared, cancellationToken, profile, limitSideLen), cancellationToken);
    }

    /// <summary>把位图（或它的一个子区域）拷成 Bgra32 裸像素。必须在拥有该位图的线程上调用。</summary>
    private static PreparedImage PreparePixels(BitmapSource image, Int32Rect region)
    {
        var offsetX = 0;
        var offsetY = 0;
        var source = image;

        if (!region.IsEmpty && region.Width > 0 && region.Height > 0)
        {
            source = new CroppedBitmap(image, region);
            offsetX = region.X;
            offsetY = region.Y;
        }

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[(long)stride * height];
        converted.CopyPixels(pixels, stride, 0);

        return new PreparedImage(pixels, width, height, stride, offsetX, offsetY);
    }

    private OcrDocument Recognize(PreparedImage prepared, CancellationToken cancellationToken, OcrProfile profile = OcrProfile.Standard, int limitSideLen = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Models.IsComplete)
        {
            throw new InvalidOperationException(Models.Describe());
        }

        var watch = Stopwatch.StartNew();
        var (bitmap, handle) = CreateSkBitmap(prepared);
        try
        {
            OcrResult result;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var engine = EnsureEngine();
                result = engine.Detect(bitmap, CurrentOptions(profile, limitSideLen));
            }

            watch.Stop();

            var lines = new List<OcrLine>();
            foreach (var block in result.TextBlocks ?? [])
            {
                var text = block.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var points = block.BoxPoints;
                if (points is null || points.Length < 4)
                {
                    continue;
                }

                var score = block.CharScores is { Length: > 0 } scores ? scores.Average() : 0f;
                var box = points.Select(p => ((double)p.X, (double)p.Y)).ToArray();
                var line = new OcrLine(text, score, box);
                lines.Add(prepared.OffsetX == 0 && prepared.OffsetY == 0
                    ? line
                    : line.Offset(prepared.OffsetX, prepared.OffsetY));
            }

            // 去印章：印章文字会污染列结构，按框内红色占比打标
            var stamped = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (IsMostlyStamp(prepared, lines[i]))
                {
                    lines[i] = lines[i].MarkOnStamp();
                    stamped++;
                }
            }

            if (stamped > 0)
            {
                _log.Info($"去印章：标记了 {stamped} 个压在红章上的文字行（不参与表格列结构）");
            }

            var document = new OcrDocument(prepared.Width, prepared.Height, lines, watch.ElapsedMilliseconds);
            _log.Info(
                $"OCR 完成：{document.Lines.Count} 行 / {document.ElapsedMs}ms " +
                $"({prepared.Width}x{prepared.Height}{(prepared.OffsetX == 0 && prepared.OffsetY == 0 ? string.Empty : " 子区域")})");
            return document;
        }
        finally
        {
            bitmap.Dispose();
            handle.Free();
        }
    }

    /// <summary>
    /// 这一行是否"压在红色印章上"：在框内按步长采样，统计"红色占优"的像素比例。
    /// 纯文本（黑/灰）与白底都不会命中；印章的红色实心区域会大面积命中。
    /// </summary>
    private static bool IsMostlyStamp(PreparedImage image, OcrLine line)
    {
        var x0 = Math.Max(0, (int)line.X0);
        var y0 = Math.Max(0, (int)line.Y0);
        var x1 = Math.Min(image.Width - 1, (int)line.X1);
        var y1 = Math.Min(image.Height - 1, (int)line.Y1);
        if (x1 <= x0 || y1 <= y0)
        {
            return false;
        }

        var total = 0;
        var reddish = 0;
        for (var y = y0; y <= y1; y += 2)
        {
            var rowBase = y * image.Stride;
            for (var x = x0; x <= x1; x += 2)
            {
                var i = rowBase + (x * 4); // Bgra32
                var b = image.Pixels[i];
                var g = image.Pixels[i + 1];
                var r = image.Pixels[i + 2];
                total++;
                if (r > 90 && r - Math.Max(g, b) > 40)
                {
                    reddish++;
                }
            }
        }

        return total > 0 && reddish >= total * 0.12;
    }

    /// <summary>裸像素 + 子区域偏移（后台线程只用它，不碰 WPF）。</summary>
    private sealed record PreparedImage(byte[] Pixels, int Width, int Height, int Stride, int OffsetX, int OffsetY);

    private RapidOcr EnsureEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        var watch = Stopwatch.StartNew();
        var engine = new RapidOcr();
        try
        {
            engine.InitModels(Models.DetPath, Models.ClsPath, Models.RecPath, Models.DictPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                      or TypeInitializationException or FileNotFoundException)
        {
            // 最常见的真因：缺 VC++ v14 运行时（onnxruntime.dll 是原生库，导入 VCRUNTIME140/MSVCP140）。
            // 正常分发包里这几个 DLL 就在 exe 同目录；如果被人删了、或用了不完整的便携包，就会走到这里。
            // 详细清单只写日志；界面上给一句能照着做的提示。
            var detail =
                "本地识别引擎无法加载（ONNX Runtime 原生库）。最常见原因是缺少 VC++ v14 运行时：\n" +
                "请确认程序目录下有 vcruntime140.dll / vcruntime140_1.dll / msvcp140.dll / msvcp140_1.dll；\n" +
                "若缺失，重新解压完整的便携包或重新安装即可（安装包已随包带上这几个文件）。";
            const string userHint = "本地识别引擎加载失败。请重新安装 SimOCR（或重新解压完整的便携包）后再试。";
            _log.Error($"OCR 引擎加载失败：{ex.GetType().Name} - {ex.Message}\n{detail}", ex);
            throw new InvalidOperationException(userHint, ex);
        }

        watch.Stop();
        _log.Info($"OCR 引擎已加载（PP-OCRv5 中文，{watch.ElapsedMilliseconds}ms）");
        _engine = engine;
        return _engine;
    }

    /// <summary>
    /// 裸像素 → SKBitmap。RapidOcrNet 只吃 SkiaSharp 位图，这里把像素锁在托管数组上直接交给 Skia，
    /// 避免再编码成 PNG 转一圈（大图能省几十毫秒与一份临时内存）。
    /// </summary>
    private static (SKBitmap Bitmap, GCHandle Handle) CreateSkBitmap(PreparedImage prepared)
    {
        var handle = GCHandle.Alloc(prepared.Pixels, GCHandleType.Pinned);
        var info = new SKImageInfo(prepared.Width, prepared.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap();
        if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), prepared.Stride))
        {
            bitmap.Dispose();
            handle.Free();
            throw new InvalidOperationException("无法把位图交给 Skia（InstallPixels 失败）");
        }

        return (bitmap, handle);
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
            _engine?.Dispose();
            _engine = null;
        }
    }
}
