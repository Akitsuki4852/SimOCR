using System.Runtime.InteropServices;

namespace SimOCR.Installer;

/// <summary>
/// 安装进度窗口。
///
/// 为什么是自己 P/Invoke 画一个窗口，而不是用更省事的办法：
///   * WPF / WinForms 在 NativeAOT 下不可用（本工程靠 AOT 才能把安装器压到 1~2 MB）；
///   * COM 的 IProgressDialog 在 NativeAOT 下也不行 —— 实例化 ComImport coclass
///     会被 ILC 直接判定为"永远抛异常"（内置 COM 激活不支持）。
/// 所以这里用最原始也最可靠的 user32 + comctl32：一个标题窗 + 一行说明 + 原生进度条。
/// 全程没有任何反射/COM，AOT 友好，且不依赖第三方控件。
/// </summary>
internal sealed class InstallProgressWindow : IProgressSink
{
    private const string WindowClassName = "SimOCRSetupProgressWnd";
    private const string ProgressClassName = "msctls_progress32";

    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsExTopmost = 0x00000008;

    private const uint WmSetFont = 0x0030;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;

    private const uint PbmSetRange32 = 0x0400 + 6;
    private const uint PbmSetPos = 0x0400 + 2;

    private const int SwShowNormal = 1;
    private const int ColorWindow = 5;
    private const int DefaultGuiFont = 17;

    private const uint IccProgressClass = 0x00000020;

    private static readonly object Gate = new();
    private static InstallProgressWindow? _current;
    private static WndProcDelegate? _wndProc; // 必须持有引用，否则被 GC 回收后窗口过程会崩
    private static bool _classRegistered;

    private IntPtr _hwnd;
    private IntPtr _label;
    private IntPtr _progress;
    private long _lastPos = -1;
    private volatile bool _cancelled;

    private float _scale = 1f;

    /// <summary>创建并显示进度窗口；失败返回 null（调用方降级为静默）。</summary>
    internal static InstallProgressWindow? TryCreate()
    {
        lock (Gate)
        {
            if (_current is not null)
            {
                return _current;
            }

            var window = new InstallProgressWindow();
            if (!window.CreateNative())
            {
                return null;
            }

            _current = window;
            return window;
        }
    }

    public bool Cancelled => _cancelled;

    public void Start(string title, string detail, long totalBytes)
    {
        UpdateText(title, detail);
        UpdateProgress(0, totalBytes);
    }

    public void Report(long doneBytes, long totalBytes, string line)
    {
        UpdateText(null, line);
        UpdateProgress(doneBytes, totalBytes);
    }

    public void Stop()
    {
        lock (Gate)
        {
            if (_current == this)
            {
                _current = null;
            }
        }

        var hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        _label = IntPtr.Zero;
        _progress = IntPtr.Zero;

        if (hwnd != IntPtr.Zero && IsWindow(hwnd) != 0)
        {
            DestroyWindow(hwnd);
        }

        NativeMethods.PumpMessages();
    }

    private void UpdateText(string? title, string detail)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || IsWindow(hwnd) == 0)
        {
            return;
        }

        if (!string.IsNullOrEmpty(title))
        {
            SetWindowTextW(hwnd, title!);
        }

        if (!string.IsNullOrEmpty(detail) && _label != IntPtr.Zero)
        {
            SetWindowTextW(_label, detail);
        }

        NativeMethods.PumpMessages();
    }

    private void UpdateProgress(long doneBytes, long totalBytes)
    {
        var progress = _progress;
        if (progress == IntPtr.Zero || IsWindow(progress) == 0)
        {
            return;
        }

        if (totalBytes > 0)
        {
            var position = (int)Math.Clamp(doneBytes * 100 / totalBytes, 0, 100);
            if (position != _lastPos)
            {
                _lastPos = position;
                SendMessageW(progress, PbmSetPos, (IntPtr)position, IntPtr.Zero);
            }
        }

        NativeMethods.PumpMessages();
    }

    /// <summary>
    /// 建窗（**只做一次**，包括 DPI 缩放因子的取值）。
    ///
    /// **0.10.5：DPI 缓存第 4 处的处置决定 = "仅启动时取一次"，理由如下**
    /// （同一段也回答"为什么不加 <c>WM_DPICHANGED</c> 处理"）：
    ///
    /// 1. **它不可能被拖到别的显示器**。本窗是 <c>WS_POPUP | WS_CAPTION | WS_SYSMENU</c> 的
    ///    **固定尺寸、居中、非可调、置顶**的进度窗（下面一行算的就是"屏幕居中"），
    ///    没有任何改变 DPI 的交互路径 —— 而当时点名的另外三处
    ///    （<c>OverlayWindow._scale</c> / <c>PinnedImageWindow._scale</c> / <c>MonitorInfo.DpiX</c>）
    ///    服务的都是**用户会拖来拖去**的窗口/贴图。这条区别是本处唯一的判据。
    /// 2. **它的生存期只有一次安装**（秒级），跑完立刻 <c>DestroyWindow</c>；用户在这几秒里
    ///    改显示设置（换分辨率/换缩放/插拔屏）的概率可以忽略，而真发生时后果也只是"进度条
    ///    略微错位"——**不影响安装结果**（这不是布局精密的界面，内容就是一行说明 + 一条进度条）。
    /// 3. **加处理的代价与风险不对称**：<c>WM_DPICHANGED</c> 要求重算窗口/子控件尺寸与字体、
    ///    并且必须回填 <c>lParam</c> 里的建议矩形；本工程是 NativeAOT + 裸 Win32，这些都要手写，
    ///    而安装器是**用户双击即用的最前端**——在这里引入一个只在"安装过程中改 DPI"才生效的
    ///    新分支，风险大于收益（改动安装链路必须跑完整回归）。
    /// 4. **已知的、被接受的不精确之处**（写清楚而不是假装没有）：<c>GetDpiForSystem()</c> 返回的是
    ///    **系统 DPI**，在"主屏 150% + 安装器被启动到副屏 100%"这类混合 DPI 下，本窗的像素尺寸
    ///    会按系统 DPI 算 —— 视觉上偏大/偏小，但仍然居中、可读、可取消。
    ///    真要修，正确做法是按窗口实际所在显示器的 DPI 取值（<c>GetDpiForWindow</c> 在
    ///    <c>WM_CREATE</c> 之后才可用，需要先建窗再调尺寸），**那属于 0.10.6+ 的改动**，本版不做。
    /// </summary>
    private bool CreateNative()
    {
        try
        {
            _scale = GetDpiForSystem() / 96f;
            if (_scale <= 0f || _scale > 8f)
            {
                _scale = 1f;
            }

            var instance = GetModuleHandleW(null);
            if (!EnsureClassRegistered(instance))
            {
                return false;
            }

            var width = Scale(460);
            var height = Scale(150);
            var screenWidth = GetSystemMetrics(0);
            var screenHeight = GetSystemMetrics(1);
            var x = Math.Max(0, (screenWidth - width) / 2);
            var y = Math.Max(0, (screenHeight - height) / 2);

            _hwnd = CreateWindowExW(
                WsExTopmost,
                WindowClassName,
                "正在安装 SimOCR",
                WsPopup | WsCaption | WsSysMenu,
                x,
                y,
                width,
                height,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            var font = GetStockObject(DefaultGuiFont);

            _label = CreateWindowExW(
                0,
                "STATIC",
                "正在解压…",
                WsChild | WsVisible,
                Scale(18),
                Scale(18),
                width - Scale(36),
                Scale(22),
                _hwnd,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            _progress = CreateWindowExW(
                0,
                ProgressClassName,
                string.Empty,
                WsChild | WsVisible,
                Scale(18),
                Scale(50),
                width - Scale(36),
                Scale(24),
                _hwnd,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (_label != IntPtr.Zero)
            {
                SendMessageW(_label, WmSetFont, font, (IntPtr)1);
            }

            if (_progress != IntPtr.Zero)
            {
                SendMessageW(_progress, PbmSetRange32, (IntPtr)0, (IntPtr)100);
                SendMessageW(_progress, WmSetFont, font, (IntPtr)1);
            }

            ShowWindow(_hwnd, SwShowNormal);
            UpdateWindow(_hwnd);
            SetForegroundWindow(_hwnd);
            NativeMethods.PumpMessages();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int Scale(int value) => (int)Math.Round(value * _scale);

    private static bool EnsureClassRegistered(IntPtr instance)
    {
        if (_classRegistered)
        {
            return true;
        }

        // 进度条属于 comctl32 的"通用控件"，用之前必须初始化
        var icc = new InitCommonControlsEx
        {
            dwSize = (uint)Marshal.SizeOf<InitCommonControlsEx>(),
            dwICC = IccProgressClass,
        };
        InitCommonControls(ref icc);

        _wndProc = StaticWndProc;
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hbrBackground = (IntPtr)(ColorWindow + 1),
            lpszClassName = WindowClassName,
        };

        var atom = RegisterClassExW(ref windowClass);
        // 0 通常是"类已存在"（上一次异常退出残留），不算失败
        _classRegistered = atom != 0 || Marshal.GetLastWin32Error() == 1410;
        return _classRegistered;
    }

    private void OnCloseRequested()
    {
        // 用户点了右上角的 X：等同于"取消安装"
        _cancelled = true;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero && IsWindow(hwnd) != 0)
        {
            ShowWindow(hwnd, 0); // SW_HIDE，让窗口立刻消失，安装线程负责收尾
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClose)
        {
            _current?.OnCloseRequested();
            return IntPtr.Zero;
        }

        if (message == WmDestroy)
        {
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // ---------------------------------------------------------------------
    // P/Invoke
    // ---------------------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        internal uint cbSize;
        internal uint style;
        internal IntPtr lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitCommonControlsEx
    {
        internal uint dwSize;
        internal uint dwICC;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SetWindowTextW(IntPtr hwnd, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("comctl32.dll", EntryPoint = "InitCommonControlsEx", ExactSpelling = true)]
    private static extern bool InitCommonControls(ref InitCommonControlsEx commonControls);
}
