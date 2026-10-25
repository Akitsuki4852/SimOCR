using System.Runtime.InteropServices;

namespace SimOCR.Installer;

/// <summary>
/// 安装器用到的 Win32 原生调用。仅保留"必须有"的几个：
/// 消息框（交互提示）与消息泵（进度窗口靠它重绘）。
/// </summary>
internal static class NativeMethods
{
    internal const uint MB_OK = 0x00000000;
    internal const uint MB_YESNO = 0x00000004;
    internal const uint MB_ICONERROR = 0x00000010;
    internal const uint MB_ICONWARNING = 0x00000030;
    internal const uint MB_ICONINFORMATION = 0x00000040;
    internal const uint MB_SETFOREGROUND = 0x00010000;
    internal const uint MB_TOPMOST = 0x00040000;

    internal const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// 模态消息框。安装器是 WinExe，没有窗口宿主，所以 hWnd 传 0，
    /// 并用 MB_SETFOREGROUND | MB_TOPMOST 保证它不会被别的窗口盖住。
    /// </summary>
    internal static int MessageBox(string text, string caption, uint type)
        => MessageBoxW(IntPtr.Zero, text, caption, type | MB_SETFOREGROUND | MB_TOPMOST);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal IntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal int ptX;
        internal int ptY;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    private static extern int PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage", ExactSpelling = true)]
    private static extern int TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    private const uint PM_REMOVE = 0x0001;

    /// <summary>把当前线程消息队列里的消息全部处理掉。原生进度对话框靠它保持响应/重绘。</summary>
    internal static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE) != 0)
        {
            if (msg.message == 0x0012) // WM_QUIT
            {
                return;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
}
