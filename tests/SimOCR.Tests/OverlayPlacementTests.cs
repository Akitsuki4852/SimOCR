using System.Windows;
using SimOCR.Overlay;

namespace SimOCR.Tests;

/// <summary>
/// 工具条/尺寸标签/放大镜的摆放：这是"贴边不被裁掉、始终可点"的正确性基础。
/// </summary>
public class OverlayPlacementTests
{
    private static readonly Size Frame = new(1920, 1080);
    private static readonly Size Toolbar = new(120, 34);

    [Fact]
    public void 普通选区工具条在右下外侧()
    {
        var selection = new Rect(100, 100, 400, 300);
        var placed = OverlayPlacement.PlaceToolbar(selection, Toolbar, Frame, gap: 6);

        Assert.Equal(selection.Right - Toolbar.Width, placed.X);
        Assert.Equal(selection.Bottom + 6, placed.Y);
    }

    [Fact]
    public void 选区贴屏幕底部时工具条翻到选框上方()
    {
        var selection = new Rect(100, 900, 400, 170); // 底边 1070，只剩 10px
        var placed = OverlayPlacement.PlaceToolbar(selection, Toolbar, Frame, gap: 6);

        Assert.True(placed.Bottom <= Frame.Height);
        Assert.True(placed.Y < selection.Bottom);
        Assert.Equal(selection.Bottom - Toolbar.Height - 6, placed.Y);
    }

    [Fact]
    public void 选区贴屏幕右边缘时工具条仍完整可见()
    {
        var selection = new Rect(1820, 300, 100, 200);
        var placed = OverlayPlacement.PlaceToolbar(selection, Toolbar, Frame, gap: 6);

        Assert.True(placed.Right <= Frame.Width);
        Assert.Equal(Frame.Width - Toolbar.Width, placed.X, 3);
    }

    [Fact]
    public void 全屏选区时工具条被夹进画面内()
    {
        var selection = new Rect(0, 0, Frame.Width, Frame.Height);
        var placed = OverlayPlacement.PlaceToolbar(selection, Toolbar, Frame, gap: 6);

        Assert.InRange(placed.Left, 0, Frame.Width - Toolbar.Width);
        Assert.InRange(placed.Top, 0, Frame.Height - Toolbar.Height);
    }

    [Fact]
    public void 尺寸标签在选框上方顶部放不下时翻到内侧()
    {
        var label = new Size(80, 22);
        var top = OverlayPlacement.PlaceSizeLabel(new Rect(300, 100, 200, 150), label, Frame, gap: 6);
        Assert.Equal(300, top.X);
        Assert.Equal(72, top.Y);

        var flipped = OverlayPlacement.PlaceSizeLabel(new Rect(300, 4, 200, 150), label, Frame, gap: 6);
        Assert.Equal(10, flipped.Y);
    }

    [Fact]
    public void 放大镜贴右下角时翻到光标左上()
    {
        var magnifier = new Size(160, 200);
        var cursor = new Point(1900, 1070);
        var placed = OverlayPlacement.PlaceMagnifier(cursor, magnifier, Frame, offset: 18);

        Assert.True(placed.Left < cursor.X);
        Assert.True(placed.Top < cursor.Y);
        Assert.True(placed.Right <= Frame.Width);
        Assert.True(placed.Bottom <= Frame.Height);
    }

    [Fact]
    public void 放大镜在画面中间时保持右下()
    {
        var magnifier = new Size(160, 200);
        var cursor = new Point(500, 400);
        var placed = OverlayPlacement.PlaceMagnifier(cursor, magnifier, Frame, offset: 18);

        Assert.Equal(518, placed.X);
        Assert.Equal(418, placed.Y);
    }

    [Fact]
    public void 元素比画面还大时贴左上角不越界()
    {
        var tinyFrame = new Size(100, 60);
        var huge = new Size(300, 200);
        var placed = OverlayPlacement.PlaceToolbar(new Rect(0, 0, 50, 50), huge, tinyFrame, gap: 6);

        Assert.Equal(0, placed.X);
        Assert.Equal(0, placed.Y);
    }
}
