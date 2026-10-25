using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;
using SimOCR.Settings;
using SimOCR.Storage;

namespace SimOCR.Pinning;

/// <summary>
/// 贴图生命周期管理：创建/关闭/全部关闭，右键菜单动作分派（复制/保存）。
/// 贴图只保存在内存里（不做重启恢复）。
/// </summary>
internal sealed class PinnedImageService : IDisposable
{
    private readonly List<PinnedImageWindow> _windows = [];
    private readonly SettingsService _settings;
    private readonly ImageStorageService _storage;
    private readonly LogService _log;
    private bool _disposed;

    public PinnedImageService(SettingsService settings, ImageStorageService storage, LogService log)
    {
        _settings = settings;
        _storage = storage;
        _log = log;
    }

    public int Count => _windows.Count;

    public event Action<int>? CountChanged;

    /// <summary>给用户看的短提示（由托盘气泡展示）。</summary>
    public event Action<string>? Notify;

    public void Pin(BitmapSource image, int physicalX, int physicalY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var window = new PinnedImageWindow(image, _settings.Current, _log, physicalX, physicalY);

        window.CopyRequested += w =>
        {
            if (ClipboardExporter.Copy(w.Image, _log))
            {
                Notify?.Invoke("贴图已复制到剪贴板");
            }
            else
            {
                Notify?.Invoke("复制失败：剪贴板被占用");
            }
        };

        window.SaveRequested += w =>
        {
            var result = _storage.SavePng(w.Image);
            Notify?.Invoke(result.Success
                ? $"已保存：{Path.GetFileName(result.Path)}"
                : $"保存失败：{result.Error}");
        };

        window.CloseRequested += Close;
        window.CloseAllRequested += _ => CloseAll();

        // **唯一移除点就是"窗口真的关掉了"这个事实**。
        // 过去只在 CloseRequested / CloseAll 里 `_windows.Remove`，而 Alt+F4（系统投递
        // WM_SYSCOMMAND/SC_CLOSE → DefWindowProc 转 WM_CLOSE → WPF 关窗）根本不走
        // CloseRequested —— 贴图关掉了、`_windows` 里还留着死窗口，于是托盘
        // "关闭全部贴图 (N)" 与被钉住的位图一起漂移。
        // 挂在 Closed 上以后，任何关闭路径（菜单 / Esc / Alt+F4 / 以后新增的路径）
        // 都会走到同一个收敛点。
        window.Closed += (_, _) => Remove(window);

        _windows.Add(window);
        window.Show();
        window.Activate();

        _log.Info($"已置顶贴图: {image.PixelWidth}x{image.PixelHeight} @ {physicalX},{physicalY}（当前 {_windows.Count} 张）");
        CountChanged?.Invoke(_windows.Count);
    }

    /// <summary>贴图窗口已关闭（窗口 Closed 事件的唯一入口）：移出计数并通知托盘。</summary>
    private void Remove(PinnedImageWindow window)
    {
        // 已经移除过（CloseAll 兜底清过一遍）就不重复通知，避免托盘收到同一个数字两次
        if (!_windows.Remove(window))
        {
            return;
        }

        _log.Debug($"贴图已关闭（剩 {_windows.Count} 张）");
        CountChanged?.Invoke(_windows.Count);
    }

    /// <summary>
    /// 关掉一张贴图。移除与通知都在 <see cref="Remove"/>（由窗口的 Closed 事件驱动），
    /// 所以这里只负责"请求关窗"。
    /// </summary>
    public void Close(PinnedImageWindow window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            _log.Warn("关闭贴图失败", ex);
        }
    }

    public void CloseAll()
    {
        if (_windows.Count == 0)
        {
            return;
        }

        var snapshot = _windows.ToArray();
        foreach (var w in snapshot)
        {
            Close(w);   // 正常路径：Closed → Remove → CountChanged
        }

        // 兜底：用户点"关闭全部贴图"的承诺是**计数归零**。万一某个窗口没走到 Closed
        //（关闭被取消、异常），这里强制收敛，绝不让一张关不掉的贴图留在托盘标签里。
        // 正常路径上 _windows 此时已空，Remove 会直接返回 —— 不会重复通知托盘。
        foreach (var w in snapshot)
        {
            Remove(w);
        }

        _log.Info($"已关闭全部贴图（{snapshot.Length} 张）");
    }

    /// <summary>
    /// 自检用：当前活着的贴图窗口（验收要拿到 HWND 投递 Alt+F4 等价的窗口消息）。
    /// </summary>
    internal IReadOnlyList<PinnedImageWindow> TestWindows => _windows;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAll();
    }
}
