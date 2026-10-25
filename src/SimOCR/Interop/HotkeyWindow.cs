using System.Runtime.InteropServices;
using System.Windows.Interop;
using SimOCR.Diagnostics;

namespace SimOCR.Interop;

/// <summary>
/// 承载全局热键的隐藏消息窗口。
/// 说明：不使用 message-only 窗口（它收不到广播消息），而是一个 0×0 的隐藏顶层窗口，
/// 这样除了 WM_HOTKEY 还能收到 WM_POWERBROADCAST，用于**休眠唤醒后重新注册热键**
/// （<see cref="PowerResumed"/> → <c>AppComposition.OnPowerResumed</c> → 重注册）。
///
/// **这里不处理显示器变化**：既不重注册热键、也不重排贴图，转发出去也只够打一条日志，
/// 而注释一旦写成"用于显示器变化……重新注册热键"，接手的人就会据此以为这条路已经处理了。
/// 所以选择**不实现**：真要做（重读 MonitorLayout + 重排贴图 + DpiChanged）是一次行为变更。
/// 窗口本身**保留**：它是 <see cref="PowerResumed"/> 的载体，那条路径是真的在重注册。
/// </summary>
internal sealed class HotkeyWindow : IDisposable
{
    private const int HotkeyId = 0x510C;

    /// <summary>自定义错误码：热键组合不安全（无修饰键的普通键），未调用系统 API。</summary>
    public const int UnsafeHotkeyError = -2;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly LogService _log;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public HotkeyWindow(LogService log)
    {
        _log = log;

        var p = new HwndSourceParameters("SimOCR.HotkeyWindow")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        };

        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>当前生效的热键（未注册时为空）。</summary>
    public HotkeyGesture Current { get; private set; } = HotkeyGesture.None;

    public event Action? Pressed;

    public event Action? PowerResumed;

    /// <summary>
    /// 注册热键。返回失败原因（Win32 错误码），0 表示成功。
    /// 典型失败：1409 = 已被其他程序占用；<see cref="UnsafeHotkeyError"/> = 组合不安全。
    /// </summary>
    public int TryRegister(HotkeyGesture gesture, bool quiet = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unregister();

        if (gesture.IsEmpty)
        {
            return 0;
        }

        // 防呆：无修饰键时只允许 F1~F24 / PrintScreen，否则会把普通键从系统里抢走
        if (!gesture.IsSafeAsGlobalHotkey())
        {
            if (!quiet)
            {
                _log.Warn($"拒绝注册不安全的热键 {gesture.Display}：无修饰键时只允许 F1~F24 / PrintScreen");
            }

            return UnsafeHotkeyError;
        }

        var ok = NativeMethods.RegisterHotKey(
            _source.Handle,
            HotkeyId,
            (uint)gesture.Modifiers,
            gesture.VirtualKey);

        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            if (!quiet)
            {
                _log.Warn($"热键注册失败: {gesture.Display} (Win32 错误码 {err})");
            }

            return err == 0 ? -1 : err;
        }

        _registered = true;
        Current = gesture;
        if (!quiet)
        {
            _log.Info($"热键已注册: {gesture.Display}");
        }

        return 0;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        }
        catch (Exception ex)
        {
            _log.Warn("注销热键时出错", ex);
        }
        finally
        {
            _registered = false;
            Current = HotkeyGesture.None;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                Pressed?.Invoke();
                break;

            case NativeMethods.WM_POWERBROADCAST when wParam.ToInt32() == 0x0012: // PBT_APMRESUMEAUTOMATIC
                PowerResumed?.Invoke();
                break;

            // WM_DISPLAYCHANGE **有意不处理**：分辨率 / DPI 变化后的重排属于行为变化，
            // 已移到 0.11。这里保持"什么都不做"，但不要再挂一个只写日志的事件假装处理了。
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
