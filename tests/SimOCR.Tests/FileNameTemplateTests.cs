using SimOCR.Interop;
using SimOCR.Storage;

namespace SimOCR.Tests;

public class FileNameTemplateTests
{
    private static readonly DateTime Sample = new(2026, 3, 7, 9, 5, 4, 123);

    [Fact]
    public void 默认模板渲染出毫秒级唯一名字()
    {
        var name = FileNameTemplate.Render("SimOCR_{yyyy}{MM}{dd}_{HH}{mm}{ss}_{fff}", Sample);
        Assert.Equal("SimOCR_20260307_090504_123", name);
    }

    [Fact]
    public void 空模板回退默认值()
    {
        var name = FileNameTemplate.Render(null, Sample);
        Assert.StartsWith("SimOCR_", name);
        Assert.EndsWith("123", name);
    }

    [Fact]
    public void 月和分是不同的占位符()
    {
        Assert.Equal("03-05", FileNameTemplate.Render("{MM}-{mm}", Sample));
    }

    [Fact]
    public void 非法字符会被替换()
    {
        var name = FileNameTemplate.Render("a/b:c*d?e\"f<g>h|i", Sample);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('?', name);
        Assert.DoesNotContain('"', name);
        Assert.DoesNotContain('<', name);
        Assert.DoesNotContain('>', name);
        Assert.DoesNotContain('|', name);
    }

    [Fact]
    public void 未知占位符原样保留()
    {
        Assert.Equal("{unknown}", FileNameTemplate.Render("{unknown}", Sample));
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("F1", "F1")]
    [InlineData("f4", "F4")]
    [InlineData("Ctrl+Alt+A", "Ctrl+Alt+A")]
    [InlineData("ctrl+shift+x", "Ctrl+Shift+X")]
    [InlineData("Win+F1", "Win+F1")]
    [InlineData("Control, Alt+A", "Ctrl+Alt+A")]
    [InlineData("control alt a", "Ctrl+Alt+A")]
    [InlineData("Ctrl|Shift|PrintScreen", "Ctrl+Shift+PrintScreen")]
    public void 解析后能还原成标准写法(string input, string expected)
    {
        Assert.Equal(expected, HotkeyGesture.Parse(input).Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Alt")]
    [InlineData("NotAKey")]
    public void 非法输入返回空热键(string input)
    {
        Assert.True(HotkeyGesture.Parse(input).IsEmpty);
    }

    [Theory]
    [InlineData("F1", true)]
    [InlineData("F12", true)]
    [InlineData("PrintScreen", true)]
    [InlineData("A", false)]
    [InlineData("1", false)]
    [InlineData("Tab", false)]
    [InlineData("Ctrl+A", true)]
    [InlineData("Ctrl+Shift+F4", true)]
    public void 只允许安全的热键组合注册为全局热键(string input, bool safe)
    {
        // 防呆回归：曾因配置写成 "Control, Alt" 被解析成裸 A 键，把整个系统的 A 键抢走
        Assert.Equal(safe, HotkeyGesture.Parse(input).IsSafeAsGlobalHotkey());
    }

    [Fact]
    public void 设置写回时修饰键被规范化()
    {
        var settings = new SimOCR.Settings.HotkeySettings();
        settings.FromGestureText("Control, Alt+A");

        Assert.Equal("Ctrl+Alt", settings.Modifiers);
        Assert.Equal("A", settings.Key);
        Assert.Equal("Ctrl+Alt+A", settings.ToGestureText());
    }

    [Fact]
    public void 无修饰键的虚拟键码是VK_F1()
    {
        Assert.Equal(0x70u, HotkeyGesture.Parse("F1").VirtualKey);
    }
}
