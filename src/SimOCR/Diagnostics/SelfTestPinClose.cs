using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SimOCR.Interop;
using SimOCR.Pinning;

namespace SimOCR.Diagnostics;

/// <summary>
/// 验收（「贴图计数：缺少 Closed 清理，绕过关闭即漂移」）：
/// 打开 N 张贴图，再用**真实关闭路径之外的消息**关掉其中几张，计数（= 托盘
/// "关闭全部贴图 (N)" 标签的唯一输入）必须跟着走。
///
/// 为什么不用真实键盘：Alt+F4 的效果就是系统向窗口投递
/// <c>WM_SYSCOMMAND</c> + <c>SC_CLOSE</c>（DefWindowProc 再转成 <c>WM_CLOSE</c>），
/// 这里把这两条消息直接 PostMessage 到贴图窗口的 HWND —— 与用户按 Alt+F4
/// 走的是同一个 WndProc 入口，且不依赖前台焦点（自检里抢焦点会干扰真实使用）。
/// 两条消息都投：① WM_SYSCOMMAND/SC_CLOSE（= Alt+F4）② WM_CLOSE（其它绕过
/// CloseRequested 的关闭路径）。
/// </summary>
internal static class SelfTestPinClose
{
    /// <summary>Alt+F4 由系统投递的消息号（wParam = SC_CLOSE）。</summary>
    private const uint WmSysCommand = 0x0112;

    /// <summary>WM_SYSCOMMAND 的"关闭窗口"命令。</summary>
    private const int ScClose = 0xF060;

    /// <summary>打开几张（本用例用 3 张：关 1 张后计数要降、剩下的要能被"全部关闭"收走）。</summary>
    private const int PinCount = 3;

    internal static int Run(PinnedImageService pinning, LogService log)
    {
        log.Info("=== 自检：贴图 Alt+F4 计数（绕过 CloseRequested 的关闭必须让计数跟着走）===");

        // 托盘标签（TrayService.UpdatePinCount）是 CountChanged 的唯一订阅者：
        // 把通知序列记下来 = 记下托盘看到过的每一个计数。
        var notified = new List<int>();
        void OnCountChanged(int count) => notified.Add(count);
        pinning.CountChanged += OnCountChanged;

        var failures = 0;
        var opened = 0;
        PinnedImageWindow? first = null;
        PinnedImageWindow? second = null;

        try
        {
            var before = pinning.Count;
            var image = CreateProbeImage();
            for (var i = 0; i < PinCount; i++)
            {
                pinning.Pin(image, 240 + (i * 90), 240 + (i * 70));
            }

            opened = pinning.Count;
            var openedOk = opened == before + PinCount;
            log.Info($"  [打开] 贴图数量 {before} → {opened}（期望 {before + PinCount}）→ {(openedOk ? "OK" : "FAIL")}");
            failures += openedOk ? 0 : 1;

            // ① Alt+F4：WM_SYSCOMMAND + SC_CLOSE
            first = pinning.TestWindows[0];
            var handle1 = HandleOf(first);
            var posted1 = handle1 != IntPtr.Zero && NativeMethods.PostMessage(handle1, WmSysCommand, ScClose, IntPtr.Zero);
            var afterAltF4 = WaitForCount(pinning, opened - 1);
            var altF4Ok = posted1 && afterAltF4 == opened - 1;
            log.Info($"  [Alt+F4] WM_SYSCOMMAND/SC_CLOSE → hwnd=0x{handle1.ToInt64():X} 投递={posted1} " +
                     $"数量 {opened} → {afterAltF4}（期望 {opened - 1}）→ {(altF4Ok ? "OK" : "FAIL")}");
            failures += altF4Ok ? 0 : 1;

            // ② 直接 WM_CLOSE（另一条绕过 CloseRequested 的路径）
            second = pinning.TestWindows[0];
            var handle2 = HandleOf(second);
            var posted2 = handle2 != IntPtr.Zero && NativeMethods.PostMessage(handle2, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var afterWmClose = WaitForCount(pinning, opened - 2);
            var wmCloseOk = posted2 && afterWmClose == opened - 2;
            log.Info($"  [WM_CLOSE] → hwnd=0x{handle2.ToInt64():X} 投递={posted2} " +
                     $"数量 {afterAltF4} → {afterWmClose}（期望 {opened - 2}）→ {(wmCloseOk ? "OK" : "FAIL")}");
            failures += wmCloseOk ? 0 : 1;

            // ③ 关掉的两张确实从计数里出去了（不是"消息没生效所以计数没变"的假通过）
            var bothGone = !pinning.TestWindows.Contains(first) && !pinning.TestWindows.Contains(second)
                           && !first.IsVisible && !second.IsVisible;
            log.Info($"  [真关掉] 两张贴图已不在计数里且不可见={bothGone} → {(bothGone ? "OK" : "FAIL")}");
            failures += bothGone ? 0 : 1;

            // ④ 剩下的走"关闭全部贴图"（托盘菜单），计数必须归零
            pinning.CloseAll();
            var afterCloseAll = pinning.Count;
            log.Info($"  [关闭全部] 数量 {afterWmClose} → {afterCloseAll}（期望 0）→ {(afterCloseAll == 0 ? "OK" : "FAIL")}");
            failures += afterCloseAll == 0 ? 0 : 1;

            // ⑤ 托盘看到的最后一个数字必须等于真实计数（否则标签与图标已经漂移）
            var last = notified.Count > 0 ? notified[^1] : -1;
            var notifyOk = last == pinning.Count && last == 0;
            log.Info($"  [托盘] CountChanged 序列=[{string.Join(",", notified)}] 末值={last} " +
                     $"真实计数={pinning.Count} → {(notifyOk ? "OK" : "FAIL")}");
            failures += notifyOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Error("贴图 Alt+F4 计数自检异常", ex);
            failures++;
        }
        finally
        {
            pinning.CountChanged -= OnCountChanged;
            try
            {
                pinning.CloseAll();
            }
            catch (Exception ex)
            {
                log.Warn("贴图自检收尾关闭失败", ex);
            }
        }

        var pass = failures == 0;
        log.Info($"=== 自检结果：{(pass ? "通过" : $"未通过（{failures} 项失败）")} ===");
        return pass ? 0 : 1;
    }

    private static IntPtr HandleOf(PinnedImageWindow window) => new WindowInteropHelper(window).Handle;

    /// <summary>
    /// 等计数变成期望值。用的是全自检共用的那一个泵（收敛）：`DispatcherPriority.Background`
    /// 的 Invoke 会 PushFrame，从而把 `PostMessage` 投递过来的窗口消息派发掉 ——
    /// 这条用例不动光标（设计如此），窗口消息是它唯一能推进状态的途径。
    /// </summary>
    private static int WaitForCount(PinnedImageService pinning, int expected, int timeoutMs = 2000)
    {
        UiPump.Until(() => pinning.Count == expected, timeoutMs);
        return pinning.Count;
    }

    /// <summary>一张纯色小图当贴图内容（本用例只关心窗口生命周期，不关心像素）。</summary>
    private static BitmapSource CreateProbeImage()
    {
        const int width = 64;
        const int height = 48;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x60;
            pixels[i + 2] = 0x80;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
