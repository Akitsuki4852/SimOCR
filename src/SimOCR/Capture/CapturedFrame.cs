using System.Windows;
using System.Windows.Media.Imaging;
using SimOCR.Interop;

namespace SimOCR.Capture;

/// <summary>
/// 一次截图会话的"冻结帧"：整个虚拟桌面在按下热键瞬间的位图（物理像素 1:1）。
/// 覆盖层显示它，因此框选期间屏幕静止，且工具自身窗口不会进入结果。
/// </summary>
internal sealed class CapturedFrame
{
    public CapturedFrame(
        BitmapSource bitmap,
        Rect virtualScreen,
        IReadOnlyList<MonitorInfo> monitors,
        TimeSpan captureDuration)
    {
        Bitmap = bitmap;
        VirtualScreen = virtualScreen;
        Monitors = monitors;
        CaptureDuration = captureDuration;
    }

    public BitmapSource Bitmap { get; }

    /// <summary>虚拟桌面物理像素矩形（左上角常为负）。</summary>
    public Rect VirtualScreen { get; }

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    public TimeSpan CaptureDuration { get; }

    public int PixelWidth => Bitmap.PixelWidth;

    public int PixelHeight => Bitmap.PixelHeight;

    /// <summary>
    /// 按虚拟桌面坐标（物理像素）裁切。坐标会被裁剪到画面范围内。
    /// </summary>
    public CroppedBitmap? Crop(int x, int y, int width, int height)
    {
        var offsetX = (int)Math.Round(VirtualScreen.X);
        var offsetY = (int)Math.Round(VirtualScreen.Y);

        var localX = Math.Clamp(x - offsetX, 0, Math.Max(0, PixelWidth - 1));
        var localY = Math.Clamp(y - offsetY, 0, Math.Max(0, PixelHeight - 1));
        var maxW = PixelWidth - localX;
        var maxH = PixelHeight - localY;
        // Math.Clamp 的下界是 1，且 maxW/maxH ≥ 1（localX/localY 已经夹进 [0, Pixel-1]），
        // 所以 w/h 恒 ≥ 1 —— 过去那条 `if (w <= 0 || h <= 0) return null;` 永不成立，0.10.5 已删。
        var w = Math.Clamp(width, 1, maxW);
        var h = Math.Clamp(height, 1, maxH);

        return new CroppedBitmap(Bitmap, new Int32Rect(localX, localY, w, h));
    }

    /// <summary>取某个物理像素的颜色（越界返回透明）。</summary>
    public System.Windows.Media.Color SamplePixel(int x, int y)
    {
        var offsetX = (int)Math.Round(VirtualScreen.X);
        var offsetY = (int)Math.Round(VirtualScreen.Y);
        var localX = x - offsetX;
        var localY = y - offsetY;

        if (localX < 0 || localY < 0 || localX >= PixelWidth || localY >= PixelHeight)
        {
            return System.Windows.Media.Colors.Transparent;
        }

        var buffer = new byte[4];
        Bitmap.CopyPixels(new Int32Rect(localX, localY, 1, 1), buffer, 4, 0);
        // 冻结帧是 Bgr32/Pbgra32：字节序 B,G,R,A
        return System.Windows.Media.Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }
}
