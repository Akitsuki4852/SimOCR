using System.Windows;
using SimOCR.Interop;

namespace SimOCR.Interop;

/// <summary>一个显示器的物理像素信息。</summary>
internal sealed record MonitorInfo(
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    uint DpiX)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public double Scale => DpiX / 96.0;
    public Rect PhysicalBounds => new(X, Y, Width, Height);
}

/// <summary>
/// 虚拟桌面与显示器布局。**所有几何以物理像素为准**（DPI 缩放在渲染时才换算）。
/// </summary>
internal static class MonitorLayout
{
    /// <summary>所有显示器物理像素的并集（左上角常为负，例如副屏在主屏左侧）。</summary>
    public static Rect GetVirtualScreen()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            // 极少数情况下（会话未就绪）退化为 WPF 报告的主屏尺寸
            w = (int)SystemParameters.PrimaryScreenWidth;
            h = (int)SystemParameters.PrimaryScreenHeight;
            x = 0;
            y = 0;
        }

        return new Rect(x, y, w, h);
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                return true;
            }

            uint dpiX = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out _) == 0)
                {
                    dpiX = dx;
                }
            }
            catch (DllNotFoundException)
            {
                // shcore 不可用（理论上 Win8.1+ 都有），退化 96
            }

            var r = mi.rcMonitor;
            list.Add(new MonitorInfo(
                DeviceName: mi.szDevice,
                X: r.Left,
                Y: r.Top,
                Width: r.Width,
                Height: r.Height,
                IsPrimary: (mi.dwFlags & 1) != 0,
                DpiX: dpiX));

            return true;
        }

        if (list.Count == 0)
        {
            // 兜底：把整个虚拟桌面当成一个显示器
            var vs = GetVirtualScreen();
            list.Add(new MonitorInfo("\\\\.\\DISPLAY1", (int)vs.X, (int)vs.Y, (int)vs.Width, (int)vs.Height, true, 96));
        }

        return list;
    }

    public static NativeMethods.POINT GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var pt);
        return pt;
    }
}
