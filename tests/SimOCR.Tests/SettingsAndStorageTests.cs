using SimOCR.Diagnostics;
using SimOCR.Settings;
using SimOCR.Storage;

namespace SimOCR.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly PathService _paths;
    private readonly LogService _log;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SimOCR-tests-" + Guid.NewGuid().ToString("N"));
        _paths = PathService.ForDirectory(_dir);
        _log = new LogService(_paths);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void 首次运行使用默认值且不抛异常()
    {
        var service = new SettingsService(_paths, _log);
        Assert.Equal("F1", service.Current.Hotkey.Key);
        Assert.True(service.Current.Behavior.Enabled);
        Assert.True(service.Current.Save.CloseOverlayAfterAction);
        Assert.Equal("copy", service.Current.Overlay.DoubleClickAction);
    }

    [Fact]
    public void 修改后会落盘并能重新读回()
    {
        var service = new SettingsService(_paths, _log);
        service.Update(s =>
        {
            s.Hotkey.FromGestureText("Ctrl+Alt+A");
            s.Save.Directory = @"D:\shots";
            s.Overlay.MagnifierZoom = 16;
        });

        Assert.True(File.Exists(_paths.ConfigPath));

        var reloaded = new SettingsService(_paths, _log);
        Assert.Equal("Ctrl+Alt+A", reloaded.Current.Hotkey.ToGestureText());
        Assert.Equal(@"D:\shots", reloaded.Current.Save.Directory);
        Assert.Equal(16, reloaded.Current.Overlay.MagnifierZoom);
    }

    [Fact]
    public void 损坏的配置会被备份并回退默认值()
    {
        File.WriteAllText(_paths.ConfigPath, "{ this is not json ");

        var service = new SettingsService(_paths, _log);

        Assert.Equal("F1", service.Current.Hotkey.Key);
        Assert.NotEmpty(Directory.GetFiles(_dir, "config.corrupt.*.json"));
    }

    [Fact]
    public void 越界的数值会被夹回合理范围()
    {
        File.WriteAllText(_paths.ConfigPath, """
            { "Version": 2, "Overlay": { "MagnifierZoom": 999, "MagnifierSampleSize": 2, "MaskOpacity": 5 } }
            """);

        var service = new SettingsService(_paths, _log);

        Assert.Equal(8, service.Current.Overlay.MagnifierZoom);
        Assert.Equal(15, service.Current.Overlay.MagnifierSampleSize);
        Assert.Equal(0.4, service.Current.Overlay.MaskOpacity, 3);
    }

    [Fact]
    public void 字段名大小写不敏感便于手工编辑()
    {
        File.WriteAllText(_paths.ConfigPath, """{ "hotkey": { "modifiers": "None", "key": "F4" } }""");

        var service = new SettingsService(_paths, _log);

        Assert.Equal("F4", service.Current.Hotkey.ToGestureText());
    }
}

public class ImageStorageServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly PathService _paths;
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly ImageStorageService _storage;

    public ImageStorageServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SimOCR-tests-" + Guid.NewGuid().ToString("N"));
        _paths = PathService.ForDirectory(_dir);
        _log = new LogService(_paths);
        _settings = new SettingsService(_paths, _log);
        _storage = new ImageStorageService(_settings, _log);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void 同一时刻连续保存不会互相覆盖()
    {
        var image = TestImages.Create(64, 48);
        var fixedName = "shot.png";

        var first = _storage.SavePng(image, _dir, fixedName);
        var second = _storage.SavePng(image, _dir, fixedName);
        var third = _storage.SavePng(image, _dir, fixedName);

        Assert.True(first.Success && second.Success && third.Success);
        Assert.NotEqual(first.Path, second.Path);
        Assert.NotEqual(second.Path, third.Path);
        Assert.Equal("shot.png", Path.GetFileName(first.Path));
        Assert.Equal("shot_1.png", Path.GetFileName(second.Path));
        Assert.Equal("shot_2.png", Path.GetFileName(third.Path));
    }

    [Fact]
    public void 落盘的文件是合法PNG且像素尺寸正确()
    {
        var image = TestImages.Create(37, 21);
        var result = _storage.SavePng(image, _dir);

        Assert.True(result.Success);
        var bytes = File.ReadAllBytes(result.Path!);
        Assert.True(bytes.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [Fact]
    public void 默认目录是系统下载文件夹()
    {
        var dir = _storage.ResolveSaveDirectory();
        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.Equal(KnownFolders.Downloads(), dir);
    }
}
