using System.Drawing;
using System.Windows;
using SimOCR.Diagnostics;
using SimOCR.Settings;

namespace SimOCR.Tray;

/// <summary>
/// 托盘图标与右键菜单（WinForms NotifyIcon：零额外依赖，行为稳定）。
/// 菜单：立即截图 / 快捷键 ▸ / 启用状态 / 设置… / 打开保存位置 / 关闭全部贴图 / 退出。
/// 不含"关于"（后续放进设置页面）。
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _hotkeyItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _enabledItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _closePinsItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _lastCaptureItem;

    private Icon? _normalIcon;
    private Icon? _pausedIcon;
    private bool _disposed;

    public TrayService(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;

        _normalIcon = LoadAppIcon();
        _pausedIcon = CreatePausedIcon(_normalIcon);

        _menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = true };

        var captureItem = new System.Windows.Forms.ToolStripMenuItem("立即截图", null, (_, _) => CaptureRequested?.Invoke());
        _menu.Items.Add(captureItem);

        // V1：OCR 入口 —— 不用先截图也能识别（剪贴板里的图 / 上一次的截图）
        _menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "识别剪贴板图片", null, (_, _) => OcrClipboardRequested?.Invoke()));
        _lastCaptureItem = new System.Windows.Forms.ToolStripMenuItem(
            "识别最近的截图", null, (_, _) => OcrLastCaptureRequested?.Invoke())
        {
            Enabled = false,
        };
        _menu.Items.Add(_lastCaptureItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _hotkeyItem = new System.Windows.Forms.ToolStripMenuItem("快捷键");
        RebuildHotkeyMenu();
        _menu.Items.Add(_hotkeyItem);

        _enabledItem = new System.Windows.Forms.ToolStripMenuItem("启用状态", null, (_, _) => EnabledToggleRequested?.Invoke());
        _menu.Items.Add(_enabledItem);

        _menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("设置…", null, (_, _) => SettingsRequested?.Invoke()));
        _menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开保存位置", null, (_, _) => OpenSaveFolderRequested?.Invoke()));

        _closePinsItem = new System.Windows.Forms.ToolStripMenuItem("关闭全部贴图", null, (_, _) => ClosePinsRequested?.Invoke())
        {
            Enabled = false,
        };
        _menu.Items.Add(_closePinsItem);

        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = "SimOCR —— 截图工具",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _icon.DoubleClick += (_, _) => CaptureRequested?.Invoke();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                CaptureRequested?.Invoke();
            }
        };

        UpdateEnabledState(settings.Current.Behavior.Enabled);
        UpdateHotkeyText();
    }

    public event Action? CaptureRequested;
    public event Action? OcrClipboardRequested;
    public event Action? OcrLastCaptureRequested;
    public event Action? SettingsRequested;
    public event Action? OpenSaveFolderRequested;
    public event Action? EnabledToggleRequested;
    public event Action? ClosePinsRequested;
    public event Action? ExitRequested;
    public event Action<string>? HotkeyPresetSelected;

    public void UpdateHotkeyText(string? effectiveDisplay = null)
    {
        var gesture = Interop.HotkeyGesture.Parse(_settings.Current.Hotkey.ToGestureText());
        var display = effectiveDisplay ?? gesture.Display;
        var suffix = effectiveDisplay is null ? string.Empty : "（临时）";
        _hotkeyItem.Text = $"快捷键 ({display}){suffix}";
        RebuildHotkeyMenu();
    }

    public void UpdateEnabledState(bool enabled)
    {
        _enabledItem.Text = enabled ? "启用状态：已启用" : "启用状态：已暂停";
        _icon.Icon = enabled ? _normalIcon : _pausedIcon;
        _icon.Text = enabled ? "SimOCR —— 截图工具" : "SimOCR —— 已暂停";
    }

    public void UpdatePinCount(int count)
    {
        _closePinsItem.Enabled = count > 0;
        _closePinsItem.Text = count > 0 ? $"关闭全部贴图 ({count})" : "关闭全部贴图";
    }

    /// <summary>"识别最近的截图"只在本次运行真的截过图之后才可用。</summary>
    public void UpdateLastCaptureAvailable(bool available) => _lastCaptureItem.Enabled = available;

    public void ShowBalloon(string message, bool isError = false)
    {
        try
        {
            if (!_settings.Current.Behavior.ShowTrayBalloon)
            {
                return;
            }

            _icon.BalloonTipTitle = isError ? "SimOCR 出错了" : "SimOCR";
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = isError ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(2500);
        }
        catch (Exception ex)
        {
            _log.Warn("托盘气泡失败", ex);
        }
    }

    private void RebuildHotkeyMenu()
    {
        _hotkeyItem.DropDownItems.Clear();
        var current = _settings.Current.Hotkey.ToGestureText();

        foreach (var preset in _settings.Current.Hotkey.Presets)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(preset)
            {
                Checked = string.Equals(preset, current, StringComparison.OrdinalIgnoreCase),
            };
            var captured = preset;
            item.Click += (_, _) => HotkeyPresetSelected?.Invoke(captured);
            _hotkeyItem.DropDownItems.Add(item);
        }

        _hotkeyItem.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        _hotkeyItem.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("自定义 / 检测冲突…", null, (_, _) => SettingsRequested?.Invoke()));
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("Assets/SimOCR.ico", UriKind.Relative))?.Stream;
            if (stream is null)
            {
                return SystemIcons.Application;
            }

            using (stream)
            {
                return new Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>暂停状态：把图标去饱和，让人一眼看出热键已停用。</summary>
    private static Icon? CreatePausedIcon(Icon? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            using var bmp = source.ToBitmap();
            // `gray` 过去没有释放（`GetHicon()` 之后就没用了，返回的 Icon 也不持有它）。
            using var gray = new Bitmap(bmp.Width, bmp.Height);
            using (var g = Graphics.FromImage(gray))
            {
                var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { 0.3f, 0.3f, 0.3f, 0f, 0f },
                    new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                    new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 0.6f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f },
                });
                using var attr = new System.Drawing.Imaging.ImageAttributes();
                attr.SetColorMatrix(matrix);
                g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attr);
            }

            var hIcon = gray.GetHicon();
            return Icon.FromHandle(hIcon);
        }
        catch
        {
            return source;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            // ContextMenuStrip.Dispose 本来就会连带释放它的项，
            // 但那层关系不写在代码里就看不出来；这里**显式**把本类持有的四个菜单项也释放一遍
            // （Dispose 幂等），规则也就有了明确的所有权声明。
            _hotkeyItem.Dispose();
            _enabledItem.Dispose();
            _closePinsItem.Dispose();
            _lastCaptureItem.Dispose();
            _menu.Dispose();
            _normalIcon?.Dispose();
            _pausedIcon?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("释放托盘资源失败", ex);
        }
    }
}
