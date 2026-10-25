using System.Runtime.InteropServices;
using SimOCR.Interop;

namespace SimOCR.Diagnostics;

/// <summary>
/// 自检用的合成鼠标输入（SendInput）。
///
/// 只用于自动化验收：把"真实鼠标事件"打进系统输入队列，从而验证
/// "左键拖框补识别 / 右键点框删除 / 中键退出编辑模式"这三条**鼠标层面**的链路
/// （数据链路已由 --selftest-ocr-window 覆盖）。
///
/// 注意：合成输入作用于**当前光标位置的窗口**，所以调用方必须先用窗口坐标算出屏幕坐标，
/// 并确保被测窗口可见、未被遮挡。坐标是**物理像素**（与截图/识别同一套几何约定）。
/// </summary>
internal static class InputSimulator
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventVirtualDesk = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    /// <summary>把光标移动到屏幕物理像素坐标；返回 <c>SendInput</c> 的返回值
    /// （0 = 被系统丢弃，见 <c>SnapSelfTest.ProbeInjection</c> 的沙箱探测）。</summary>
    internal static uint MoveTo(int screenX, int screenY)
    {
        // 归一化基准改成**虚拟桌面**（原实现用主屏 SM_CXSCREEN / SM_CYSCREEN，
        // 副屏在主屏左侧时原点为负，坐标会算错），并补 MOUSEEVENTF_VIRTUALDESK
        // 让系统按虚拟桌面解释归一化坐标。单屏（原点 0,0）下与旧写法结果完全相同。
        var desktop = MonitorLayout.GetVirtualScreen();
        var width = Math.Max(1, (int)desktop.Width - 1);
        var height = Math.Max(1, (int)desktop.Height - 1);
        var normalizedX = (int)Math.Round((screenX - desktop.X) * 65535.0 / width);
        var normalizedY = (int)Math.Round((screenY - desktop.Y) * 65535.0 / height);

        return Send(new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                Dx = normalizedX,
                Dy = normalizedY,
                Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
            },
        });
    }

    /// <summary>
    /// 按下左键**不抬起**。之前这份原语是 `SelfTestSnap` 里私有的 `SnapMouse`
    /// （连同它自己那份 `Input` / `MouseInput` / `SendInput` 声明，约 70 行重复）——
    /// 验收要在"按住不放"的中途给叠加层截图看导轨，所以 <see cref="LeftDrag"/> 这种
    /// 完整手势不够用，原语得留，但只能有一份。
    /// </summary>
    internal static uint LeftDown() => Send(Button(MouseEventLeftDown));

    /// <summary>抬起左键（与 <see cref="LeftDown"/> 配对）。</summary>
    internal static uint LeftUp() => Send(Button(MouseEventLeftUp));

    internal static void LeftClick(int screenX, int screenY)
    {
        MoveTo(screenX, screenY);
        Send(Button(MouseEventLeftDown));
        Thread.Sleep(30);
        Send(Button(MouseEventLeftUp));
    }

    internal static void RightClick(int screenX, int screenY)
    {
        MoveTo(screenX, screenY);
        Send(Button(MouseEventRightDown));
        Thread.Sleep(30);
        Send(Button(MouseEventRightUp));
    }

    internal static void MiddleClick(int screenX, int screenY)
    {
        MoveTo(screenX, screenY);
        Send(Button(MouseEventMiddleDown));
        Thread.Sleep(30);
        Send(Button(MouseEventMiddleUp));
    }

    /// <summary>按住左键从起点拖到终点（分步移动，WPF 才能收到 MouseMove 并画出橡皮筋）。</summary>
    internal static void LeftDrag(int fromX, int fromY, int toX, int toY, int steps = 8)
    {
        MoveTo(fromX, fromY);
        Thread.Sleep(40);
        Send(Button(MouseEventLeftDown));
        Thread.Sleep(40);

        for (var i = 1; i <= steps; i++)
        {
            var x = fromX + ((toX - fromX) * i / steps);
            var y = fromY + ((toY - fromY) * i / steps);
            MoveTo(x, y);
            Thread.Sleep(25);
        }

        Thread.Sleep(40);
        Send(Button(MouseEventLeftUp));
        Thread.Sleep(40);
    }

    private static Input Button(uint flag) => new()
    {
        Type = InputMouse,
        Mouse = new MouseInput { Flags = flag },
    };

    private static uint Send(Input input) => SendInput(1, [input], Marshal.SizeOf<Input>());
}
