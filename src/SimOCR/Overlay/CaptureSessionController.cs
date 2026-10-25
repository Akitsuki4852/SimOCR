using System.Windows;
using System.Windows.Media.Imaging;
using SimOCR.Capture;
using SimOCR.Diagnostics;
using SimOCR.Export;
using SimOCR.Pinning;
using SimOCR.Settings;
using SimOCR.Storage;

namespace SimOCR.Overlay;

/// <summary>
/// 一次截图的完整编排：抓冻结帧 → 显示覆盖层 → 处理（保存/复制/置顶/取消）→ 关闭与反馈。
/// 同一时刻只允许一个会话。
/// </summary>
internal sealed class CaptureSessionController
{
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly ScreenCaptureService _capture;
    private readonly ImageStorageService _storage;
    private readonly PinnedImageService _pinning;

    private bool _active;

    public CaptureSessionController(        LogService log,
        SettingsService settings,
        ScreenCaptureService capture,
        ImageStorageService storage,
        PinnedImageService pinning)
    {
        _log = log;
        _settings = settings;
        _capture = capture;
        _storage = storage;
        _pinning = pinning;
    }

    /// <summary>最近一次选区图（供"识别最近的截图"用）；本次运行没截过图时为 null。</summary>
    public BitmapSource? LastSelectionImage { get; private set; }

    /// <summary>短提示（message, isError）。</summary>
    public event Action<string, bool>? Notify;

    /// <summary>
    /// OCR 导出请求（选区图 + 目标格式）。由组合根负责：后台识别 → 打开识别结果窗口 → 导出。
    /// 覆盖层在这一步之前已经关闭（它是全屏置顶窗口，留着会挡住结果窗口）。
    /// </summary>
    public event Action<BitmapSource, OcrExportFormat>? OcrRequested;

    /// <summary>选区图更新（供托盘启用"识别最近的截图"）。</summary>
    public event Action<BitmapSource>? LastSelectionChanged;

    public void BeginCapture()
    {
        if (_active)
        {
            _log.Info("已有截图会话进行中，忽略本次触发");
            return;
        }

        _active = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 抓屏放在线程池上，UI 线程只等这一小段（1080p 约 10~25ms）
            var frame = Task.Run(_capture.Capture).GetAwaiter().GetResult();
            if (frame is null)
            {
                Notify?.Invoke("抓屏失败，详情见「设置 → 运行信息 → 打开日志目录」", true);
                return;
            }

            var overlay = CreateOverlay(frame);

            sw.Stop();
            _log.Info($"覆盖层即将显示（热键→就绪 {sw.ElapsedMilliseconds}ms）");

            overlay.ShowDialog();

            _log.Debug("覆盖层已关闭，会话结束");
        }
        catch (Exception ex)
        {
            _log.Error("截图会话异常", ex);
            Notify?.Invoke("截图会话异常，详情见「设置 → 运行信息 → 打开日志目录」", true);
        }
        finally
        {
            _active = false;
        }
    }

    /// <summary>
    /// 供命令行验收使用：只抓屏、不起模态覆盖层（<see cref="BeginCapture"/> 走 ShowDialog，
    /// 会阻塞调用线程，验收要在覆盖层显示后**继续打真实鼠标事件**，所以必须用非模态路径）。
    /// </summary>
    internal CapturedFrame? CaptureScreenOnly() => _capture.Capture();

    /// <summary>
    /// 构造覆盖层并接上真实的动作处理链（保存/复制/贴图/取消都会走 <see cref="HandleAction"/>）。
    ///
    /// **0.10.5 解耦**：这个入口过去只有一个 <c>testMode</c> 参数，而生产预热
    /// （<c>AppComposition.WarmUpOverlay</c>）必须传 <c>testMode: true</c> 才能拿到
    /// "不抢焦点"的行为 —— 生产功能借道测试标志。现在拆成两个**表达意图**的参数：
    /// <list type="bullet">
    ///   <item><paramref name="activateOnShow"/>：显示后是否允许抢焦点（自检显式传 <c>false</c>）。</item>
    ///   <item><paramref name="warmupOffscreen"/>：生产预热专用 —— 整窗不可见、不抢焦点、用完即关。</item>
    /// </list>
    /// 生产路径不再出现 <c>testMode</c>，自检侧也不再借"测试模式"表达"别抢焦点"。
    /// </summary>
    internal OverlayWindow CreateOverlay(
        CapturedFrame frame,
        bool activateOnShow = true,
        bool warmupOffscreen = false)
    {
        var overlay = new OverlayWindow(
            frame,
            _settings.Current,
            _log,
            activateOnShow: activateOnShow && !warmupOffscreen);
        overlay.ActionRequested += (_, e) => HandleAction(overlay, e);
        return overlay;
    }

    private void HandleAction(OverlayWindow overlay, OverlayActionRequestedEventArgs e)
    {
        var settings = _settings.Current;

        // 记住这次的选区图：托盘"识别最近的截图"要用
        if (e.Image is not null)
        {
            LastSelectionImage = e.Image;
            LastSelectionChanged?.Invoke(e.Image);
        }

        try
        {
            switch (e.Kind)
            {
                case OverlayActionKind.Cancel:
                    break;

                case OverlayActionKind.Save:
                    if (e.Image is null)
                    {
                        break;
                    }

                    var saveResult = _storage.SavePng(e.Image);
                    if (saveResult.Success)
                    {
                        PlaySoundIfEnabled(settings);
                        Notify?.Invoke($"已保存：{Path.GetFileName(saveResult.Path)}", false);
                    }
                    else
                    {
                        Notify?.Invoke($"保存失败：{saveResult.Error}", true);
                    }

                    break;

                case OverlayActionKind.Copy:
                    if (e.Image is null)
                    {
                        break;
                    }

                    if (ClipboardExporter.Copy(e.Image, _log))
                    {
                        Notify?.Invoke("已复制到剪贴板", false);
                    }
                    else
                    {
                        Notify?.Invoke("复制失败：剪贴板被其他程序占用", true);
                    }

                    break;

                case OverlayActionKind.Pin:
                    if (e.Image is null)
                    {
                        break;
                    }

                    var bounds = overlay.SelectionBounds;
                    _pinning.Pin(e.Image, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
                    Notify?.Invoke("已置顶为贴图", false);
                    break;

                case OverlayActionKind.OcrTxt:
                case OverlayActionKind.OcrDocx:
                case OverlayActionKind.OcrPdf:
                case OverlayActionKind.OcrXlsx:
                    if (e.Image is null)
                    {
                        break;
                    }

                    var format = e.Kind switch
                    {
                        OverlayActionKind.OcrDocx => OcrExportFormat.Docx,
                        OverlayActionKind.OcrPdf => OcrExportFormat.Pdf,
                        OverlayActionKind.OcrXlsx => OcrExportFormat.Xlsx,
                        _ => OcrExportFormat.Txt,
                    };

                    // 先关掉全屏置顶的覆盖层，否则它会挡住识别结果窗口
                    overlay.CloseOverlay();
                    _log.Info($"覆盖层请求 OCR 导出：{format.ShortLabel()}");
                    OcrRequested?.Invoke(e.Image, format);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"处理覆盖层动作 {e.Kind} 失败", ex);
            Notify?.Invoke($"操作失败：{FriendlyError.ForIo(ex)}", true);
        }
        finally
        {
            // 取消总是关闭；其余按设置（复制/保存/置顶后默认都自动关闭）
            if (e.Kind == OverlayActionKind.Cancel || settings.Save.CloseOverlayAfterAction)
            {
                overlay.CloseOverlay();
            }
        }
    }

    private static void PlaySoundIfEnabled(AppSettings settings)
    {
        if (!settings.Behavior.PlaySoundOnSave)
        {
            return;
        }

        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch
        {
            // 声音失败无所谓
        }
    }
}
