using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimOCR.Capture;
using SimOCR.Diagnostics;
using SimOCR.Interop;

namespace SimOCR.Tests;

internal static class TestImages
{
    /// <summary>造一张纯色位图（Bgra32），用于不依赖真实屏幕的测试。</summary>
    public static BitmapSource Create(int width, int height, byte b = 0x20, byte g = 0x80, byte r = 0x40)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// 抓屏属于"必须真机验证"的部分：这一组测试直接抓当前桌面，
/// 验证虚拟桌面尺寸、裁切与取色是否与物理像素一致。
/// </summary>
public class ScreenCaptureTests
{
    private static CapturedFrame? Capture()
    {
        var paths = PathService.ForDirectory(Path.Combine(Path.GetTempPath(), "SimOCR-capture-tests"));
        using var log = new LogService(paths);
        return new ScreenCaptureService(log).Capture();
    }

    [Fact]
    public void 抓到的冻结帧尺寸等于虚拟桌面物理像素()
    {
        var frame = Capture();
        Assert.NotNull(frame);

        var vs = MonitorLayout.GetVirtualScreen();
        Assert.Equal((int)vs.Width, frame!.PixelWidth);
        Assert.Equal((int)vs.Height, frame.PixelHeight);
    }

    [Fact]
    public void 裁切出来的尺寸与请求一致()
    {
        var frame = Capture();
        Assert.NotNull(frame);

        var crop = frame!.Crop(frame.X0(), frame.Y0(), 120, 80);
        Assert.NotNull(crop);
        Assert.Equal(120, crop!.PixelWidth);
        Assert.Equal(80, crop.PixelHeight);
    }

    [Fact]
    public void 裁切越界会被收缩而不是抛异常()
    {
        var frame = Capture();
        Assert.NotNull(frame);

        var crop = frame!.Crop(frame.X0() + frame.PixelWidth - 10, frame.Y0() + frame.PixelHeight - 10, 500, 500);
        Assert.NotNull(crop);
        Assert.True(crop!.PixelWidth <= 10);
        Assert.True(crop.PixelHeight <= 10);
    }

    [Fact]
    public void 取色返回不透明像素()
    {
        var frame = Capture();
        Assert.NotNull(frame);

        var color = frame!.SamplePixel(frame.X0() + 5, frame.Y0() + 5);
        Assert.Equal(0xFF, color.A);
    }

    [Fact]
    public void 画面外的取色返回透明()
    {
        var frame = Capture();
        Assert.NotNull(frame);

        var color = frame!.SamplePixel(frame.X0() - 1000, frame.Y0() - 1000);
        Assert.Equal(Colors.Transparent, color);
    }
}

internal static class CapturedFrameTestExtensions
{
    public static int X0(this CapturedFrame frame) => (int)Math.Round(frame.VirtualScreen.X);

    public static int Y0(this CapturedFrame frame) => (int)Math.Round(frame.VirtualScreen.Y);
}
