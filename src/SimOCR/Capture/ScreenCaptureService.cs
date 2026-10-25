using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;
using SimOCR.Interop;

namespace SimOCR.Capture;

/// <summary>
/// 冻结帧抓屏：一次性抓取整个虚拟桌面（GDI BitBlt + CAPTUREBLT）。
/// CAPTUREBLT 必须带，否则分层窗口（部分游戏覆盖层、提示框）抓不到。
/// </summary>
internal sealed class ScreenCaptureService
{
    private readonly LogService _log;

    public ScreenCaptureService(LogService log) => _log = log;

    public CapturedFrame? Capture()
    {
        var sw = Stopwatch.StartNew();
        var vs = MonitorLayout.GetVirtualScreen();
        var width = (int)Math.Round(vs.Width);
        var height = (int)Math.Round(vs.Height);

        if (width <= 0 || height <= 0)
        {
            _log.Error($"抓屏失败：虚拟桌面尺寸非法 ({width}x{height})");
            return null;
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                _log.Error("抓屏失败：GetDC 返回 0");
                return null;
            }

            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == IntPtr.Zero || hBitmap == IntPtr.Zero)
            {
                _log.Error("抓屏失败：CreateCompatibleDC/CreateCompatibleBitmap 失败");
                return null;
            }

            oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

            var ok = NativeMethods.BitBlt(
                memDc, 0, 0, width, height,
                screenDc,
                (int)Math.Round(vs.X), (int)Math.Round(vs.Y),
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!ok)
            {
                _log.Error($"抓屏失败：BitBlt 返回 false（{width}x{height} @ {vs.X},{vs.Y}）");
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();

            sw.Stop();
            _log.Info($"冻结帧完成: {width}x{height} ({source.Format}) 用时 {sw.ElapsedMilliseconds}ms, 虚拟桌面 @ {vs.X},{vs.Y}");

            return new CapturedFrame(source, vs, MonitorLayout.GetMonitors(), sw.Elapsed);
        }
        catch (Exception ex)
        {
            _log.Error("抓屏异常", ex);
            return null;
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
            }

            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }

            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
