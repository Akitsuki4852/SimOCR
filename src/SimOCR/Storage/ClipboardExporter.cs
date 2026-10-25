using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;

namespace SimOCR.Storage;

/// <summary>
/// 剪贴板导出。
///
/// **为什么要用 CF_BITMAP 这条"老路"**：
/// 在 Word 16.0 上做格式隔离实验，结论是——
///   * 只写 <c>CF_DIB</c>（32bpp，40 字节标准头）→ Word 粘出 **100×100 占位图**
///   * 只写 <c>CF_DIB</c>（24bpp）→ 同样 100×100
///   * 只写自定义 <c>"PNG"</c> 格式 → 100×100；写成 <c>"PNG+Office Art"</c> → Word 直接报错
///   * 只写 <c>CF_BITMAP</c>（HBITMAP）→ **正确粘出 320×200**
///   * CF_BITMAP 与其它格式（DIB / PNG）同时存在时，Word 反而会挑到坏的那份 → 320×200 变 100×100
/// 也就是说：**在这台机器上，"多写几种格式"是有害的**，Word 会挑到它处理不好的那份。
/// WinForms 的 <see cref="System.Windows.Forms.Clipboard.SetImage"/> 走的正是纯 CF_BITMAP 路径，
/// 与所有经典截图工具一致，浏览器 / IM / Office / 画图都认它。截图没有透明区域，因此放弃 PNG 无损格式没有实际损失。
/// </summary>
internal static class ClipboardExporter
{
    private static readonly int[] RetryDelaysMs = [50, 150, 400];

    public static bool Copy(BitmapSource image, LogService log)
        => RetryClipboard(
            () =>
            {
                // 每次重试都重新转一份 GDI+ 位图（与原实现一致）：上一次的位图已在
                // using 结束时释放，SetImage(copy: true) 不依赖它继续存活。
                using var bitmap = ToDrawingBitmap(image);
                // copy: true 必须为真，否则程序退出后剪贴板内容会丢
                System.Windows.Forms.Clipboard.SetImage(bitmap);
                log.Info($"已复制到剪贴板: {image.PixelWidth}x{image.PixelHeight}（CF_BITMAP，兼容 Word/画图/浏览器/IM）");
            },
            "复制到剪贴板失败",
            "复制到剪贴板失败：剪贴板被其他程序占用",
            log);

    /// <summary>复制纯文本（例如放大镜取的 HEX 色值）。</summary>
    public static bool CopyText(string text, LogService log)
        => RetryClipboard(
            () =>
            {
                System.Windows.Forms.Clipboard.SetDataObject(text, copy: true);
                log.Info($"已复制文本到剪贴板: {text}");
            },
            "复制文本失败",
            exhaustedMessage: null,
            log);

    /// <summary>
    /// 剪贴板写入的重试循环原本在 <see cref="Copy"/> / <see cref="CopyText"/> 里各写一份
    /// （同样的 4 次尝试、同样的 50/150/400 ms 退避、同样的"被占用"告警）。这里收敛成一份。
    ///
    /// 只做去重，**不**把调用移出 UI 线程 —— 移出会改变用户可观测的卡顿表现，按 0.10.x 口径留待 0.11。
    /// 日志文本与重试次数/间隔逐字保持不变。
    /// </summary>
    private static bool RetryClipboard(
        Action attempt,
        string failureMessage,
        string? exhaustedMessage,
        LogService log)
    {
        for (var attemptIndex = 0; attemptIndex <= RetryDelaysMs.Length; attemptIndex++)
        {
            try
            {
                attempt();
                return true;
            }
            catch (Exception ex) when (ex is ExternalException or System.Runtime.InteropServices.COMException)
            {
                log.Warn($"剪贴板被占用（第 {attemptIndex + 1} 次）: {ex.Message}");
            }
            catch (Exception ex)
            {
                log.Error(failureMessage, ex);
                return false;
            }

            if (attemptIndex < RetryDelaysMs.Length)
            {
                Thread.Sleep(RetryDelaysMs[attemptIndex]);
            }
        }

        if (exhaustedMessage is not null)
        {
            log.Error(exhaustedMessage);
        }

        return false;
    }

    /// <summary>把图片编码成 PNG 字节（供自检与调试使用）。</summary>
    internal static byte[] EncodePng(BitmapSource image)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 转成 GDI+ 位图（32bpp，无 alpha 语义）。截图本身不透明，用 Format32bppRgb 可避免
    /// 老程序把 alpha=0 的区域显示成黑色。
    /// </summary>
    private static System.Drawing.Bitmap ToDrawingBitmap(BitmapSource image)
    {
        var converted = image.Format == System.Windows.Media.PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        var bitmap = new System.Drawing.Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            if (data.Stride == stride)
            {
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * stride, data.Scan0 + (y * data.Stride), stride);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
