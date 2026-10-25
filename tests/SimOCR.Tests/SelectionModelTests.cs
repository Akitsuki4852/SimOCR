using System.Windows;
using SimOCR.Overlay;

namespace SimOCR.Tests;

/// <summary>
/// 选区几何（物理像素）：这是多屏/混合 DPI 下"框到哪就存哪"的正确性基础。
/// </summary>
public class SelectionModelTests
{
    private static SelectionModel NewModel() => new() { Limits = new Rect(0, 0, 1920, 1080) };

    [Fact]
    public void 反向拖拽会被规范成矩形()
    {
        var m = NewModel();
        m.BeginCreate(500, 400);
        m.Update(200, 150);
        m.EndDrag();

        Assert.True(m.Exists);
        Assert.Equal(200, m.X);
        Assert.Equal(150, m.Y);
        Assert.Equal(300, m.Width);
        Assert.Equal(250, m.Height);
    }

    [Fact]
    public void 拖出屏幕会被裁剪到虚拟桌面内()
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(5000, 5000);
        m.EndDrag();

        Assert.Equal(100, m.X);
        Assert.Equal(100, m.Y);
        Assert.Equal(1820, m.Width);
        Assert.Equal(980, m.Height);
    }

    [Fact]
    public void 平移不会越界()
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(300, 300);
        m.EndDrag();

        m.BeginMove(200, 200);
        m.Update(5000, 5000);
        m.EndDrag();

        Assert.Equal(1720, m.X);
        Assert.Equal(880, m.Y);
        Assert.Equal(200, m.Width);
        Assert.Equal(200, m.Height);
    }

    [Fact]
    public void 拖右下控制点只改宽高()
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(300, 300);
        m.EndDrag();

        m.BeginResize(SelectionHandle.SE, 300, 300);
        m.Update(400, 350);
        m.EndDrag();

        Assert.Equal(100, m.X);
        Assert.Equal(100, m.Y);
        Assert.Equal(300, m.Width);
        Assert.Equal(250, m.Height);
    }

    [Fact]
    public void 拖左上控制点会同时移动原点与尺寸()
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(300, 300);
        m.EndDrag();

        m.BeginResize(SelectionHandle.NW, 100, 100);
        m.Update(150, 130);
        m.EndDrag();

        Assert.Equal(150, m.X);
        Assert.Equal(130, m.Y);
        Assert.Equal(150, m.Width);
        Assert.Equal(170, m.Height);
    }

    [Theory]
    [InlineData(100, 100, "NW")]
    [InlineData(300, 300, "SE")]
    [InlineData(200, 100, "N")]
    [InlineData(200, 300, "S")]
    [InlineData(100, 200, "W")]
    [InlineData(300, 200, "E")]
    [InlineData(200, 200, "Inside")]
    [InlineData(600, 600, "None")]
    public void 命中测试能区分控制点与内部(int x, int y, string expected)
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(300, 300);
        m.EndDrag();

        Assert.Equal(Enum.Parse<SelectionHandle>(expected), m.HitTest(x, y, tolerance: 6));
    }

    [Fact]
    public void 小于4像素的选区视为误触()
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(102, 103);
        m.EndDrag();

        Assert.True(m.IsTooSmall);
    }

    [Fact]
    public void 方向键微调保持尺寸不变()
    {
        var m = NewModel();
        m.BeginCreate(100, 100);
        m.Update(300, 300);
        m.EndDrag();

        m.MoveBy(10, -20);

        Assert.Equal(110, m.X);
        Assert.Equal(80, m.Y);
        Assert.Equal(200, m.Width);
        Assert.Equal(200, m.Height);
    }
}
