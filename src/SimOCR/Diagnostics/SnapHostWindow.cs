using System.Diagnostics;
using System.Windows;
using SimOCR.Interop;

namespace SimOCR.Diagnostics;

/// <summary>
/// 吸附自检 / 验收共用的"外来宿主窗口"启动器（唯一一份实现）。
///
/// 收敛前的重复：<c>SelfTestSnap.LaunchSelfHostWindow</c> 与 <c>SnapOcclusionProbe.Launch</c>
/// 是**同一套逻辑各写了一遍** —— 起一个自己的 <c>--snap-host-window</c> 子进程（几何精确到像素、
/// 窗口过程与主程序同源但**在另一个进程里**，因此照样要过"排除本进程"这条候选过滤器），
/// 再轮询 <see cref="WindowBounds.EnumerateRaw"/> 用 **pid** 找回它的 HWND。
/// 两个探针各自复制了一份这套逻辑，合并成这一处（可再省 ≈79 行）。
///
/// **为什么放在 <c>Diagnostics/</c> 而不是 <c>PureLogic/</c>**：它起真实进程、等真实窗口、
/// 读真实 HWND —— 正是"需要真桌面 / 真窗口的才留下"的那一类；纯逻辑那层
/// （<c>tests/SimOCR.Tests/PureLogic/</c>）里放不进任何一句。
///
/// **为什么单独一份文件，而不是塞进两个调用方之一**：两个调用方（<c>SnapSelfTest</c> 的
/// <c>--accept-snap</c>、<c>SnapOcclusionProbe</c> 的 <c>--snap-probe-occlusion</c>）是**平级**的
/// 自检入口，谁都不该拥有对方要用的东西；放进任一方都会让另一方为了一个工具去依赖一个功能模块。
/// 单独成文件后，<c>Diagnostics/</c> 少两份重复实现、多一份共享实现 —— **净减**（见交付报告的行数对账）。
///
/// **0.10.7 补**：本类还持有"本探针看到的虚拟屏幕"的**唯一一份**解析器
/// （<see cref="ResolveScreen"/> + <see cref="ScreenOverrideEnv"/>）。靶子几何必须在**屏内**，
/// 而"在不在屏内"取决于屏幕大小 —— 把解析器放在这里，两个宿主窗口用例
/// （<c>--accept-snap</c> / <c>--snap-probe-occlusion</c>）才可能用**同一套**屏幕口径推导几何。
///
/// **另一半**：<c>--accept-snap</c> 过去要**找一个外来的记事本窗口**（<c>notepad.exe</c>），
/// 在 Store 版记事本 / 精简镜像上直接 `[失败] 没能创建/找到外来测试窗口（notepad.exe）`、exit 2。
/// 现在它和遮挡探针走**同一条路**：自己起宿主窗 —— 于是全仓不再有任何 `notepad` 依赖。
/// </summary>
internal sealed class SnapHostWindow
{
    /// <summary>
    /// 宿主窗口的标题（<c>--snap-host-window</c> 子进程给自己那扇窗设的标题）。
    /// **唯一一份**：写标题的地方（<c>SnapSelfTest.RunHostWindow</c>）与校验标题的地方
    /// （<see cref="Launch"/> 按 pid 找回窗口时）都读它，避免两处字面量漂移。
    /// </summary>
    internal const string WindowTitle = "SimOCR-snap-host";

    /// <summary>
    /// WPF 顶层窗口的类名前缀（每个 <c>Window</c> 一个 <c>HwndWrapper[SimOCR.exe;;&lt;guid&gt;]</c>）。
    /// 用来把"我们的宿主窗"与**同一 pid 下别的顶层窗口**区分开。
    /// </summary>
    private const string WpfWindowClassPrefix = "HwndWrapper[";

    /// <summary>宿主窗口进程的默认存活秒数（与 <c>--snap-host-window</c> 的第 5 个参数同义）。</summary>
    private const int DefaultHoldSeconds = 45;

    /// <summary>轮询找回 HWND 的上限：60 × 250ms ≈ 15s（与收敛前两处逐字一致）。</summary>
    private const int MaxFindAttempts = 60;

    /// <summary>轮询节拍（ms）。</summary>
    private const int FindIntervalMs = 250;

    /// <summary>
    /// 已起过的宿主进程句柄：<see cref="KillAll"/> 与 <see cref="Close"/> 共用的**唯一一份**登记表
    /// （收敛前两处各有一份 <c>Dictionary&lt;int, Process&gt;</c>）。
    /// </summary>
    private static readonly Dictionary<int, Process> Handles = [];

    private SnapHostWindow()
    {
    }

    /// <summary>
    /// 启动结果。**三态语义（0.10.5 建立，保持不许回退；0.10.7 补第四种输入）**：
    /// <list type="bullet">
    /// <item><see cref="Hwnd"/> 非 0 → 窗口已经在，<see cref="Bounds"/> 是**实测**（DWM）几何；</item>
    /// <item><see cref="Hwnd"/> == 0 且 <see cref="HostWindowMismatch"/> == true → **产品缺陷**
    ///   （看到了本程序的 WPF 宿主窗，但标题不是 <see cref="WindowTitle"/> → <c>--snap-host-window</c>
    ///   这条路径自己坏了）→ 调用方记**真失败(1)**；</item>
    /// <item><see cref="Hwnd"/> == 0 且 <see cref="ProcessExited"/> == false → **环境问题**
    ///   （进程活着却建不出窗口 / 同一 pid 下压根没有本程序的 WPF 窗：无交互桌面 / headless / 会话未就绪）
    ///   → 调用方记**用例不成立(5)**；</item>
    /// <item><see cref="Hwnd"/> == 0 且 <see cref="ProcessExited"/> == true → **产品缺陷**
    ///   （<c>--snap-host-window</c> 这条路径自己坏了）→ 调用方记**真失败(1)**。</item>
    /// </list>
    /// </summary>
    internal sealed record HostStatus(IntPtr Hwnd, int Pid, bool ProcessExited, string Why, bool HostWindowMismatch = false)
    {
        /// <summary>没起来（环境问题或产品缺陷都在内）；调用方按 <see cref="ProcessExited"/> 分流。</summary>
        internal bool IsEmpty => Hwnd == IntPtr.Zero;
    }

    /// <summary>
    /// 起一扇**另一个进程**的已知几何窗口并找回它的 HWND。
    /// </summary>
    /// <param name="rect">目标矩形（物理像素）——用 <c>--snap-host-window=x,y,w,h</c> 直接交给子进程。</param>
    /// <param name="label">日志里区分多扇窗（<c>A</c> / <c>B</c> / <c>--accept-snap 靶子</c>）。</param>
    /// <param name="topmost">是否把子进程窗口抬进**置顶组**（遮挡探针要靠同组先后压叠造"谁盖住谁"）。</param>
    /// <param name="holdSeconds">子进程保持窗口的秒数（默认 <see cref="DefaultHoldSeconds"/>）。</param>
    internal static HostStatus Launch(Rect rect, string label, LogService log, bool topmost = false, int holdSeconds = DefaultHoldSeconds)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            log.Warn($"  [宿主] {label} 拿不到自身 exe 路径");
            return new HostStatus(IntPtr.Zero, 0, false, "拿不到自身 exe 路径");
        }

        var suffix = topmost ? $";{holdSeconds};top" : $";{holdSeconds}";
        var arguments = $"--snap-host-window={(int)rect.X},{(int)rect.Y},{(int)rect.Width},{(int)rect.Height}{suffix}";

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            log.Warn($"  [宿主] {label} 启动失败：{ex.Message}");
            return new HostStatus(IntPtr.Zero, 0, false, $"进程起不来：{ex.Message}");
        }

        if (process is null)
        {
            log.Warn($"  [宿主] {label} 启动失败");
            return new HostStatus(IntPtr.Zero, 0, false, "进程起不来");
        }

        log.Info($"  [宿主] {label} 已启动自带宿主窗口进程 pid={process.Id}（{arguments}）");

        // *** 按 pid 找回窗口时必须**校验它就是靶子**（0.10.7 修，虚拟机实测缺陷）***
        // 实测（VM）：同一个 pid 下先出现了一个"类名空 / 标题空"的顶层窗口，旧代码**只要 pid 对上就
        // 立刻采用** → host.Hwnd 指向一个不是靶子的窗口 → 后面 "最上层候选是不是它自己" 的采样点
        // 搜索必然全部失败 → 打印"靶子窗口整块都被别的窗口盖住了" → 高亮落到别人身上 → 3 项失败。
        // 现在的口径：pid 对上了还不行，**类名与标题都要是宿主窗口的样子**；对不上就继续找，
        // 全部尝试都失败时把**实际看到的类名/标题**写进 Why（调用方按三态分流 → [用例不成立](5)）。
        var hwnd = IntPtr.Zero;
        var rejected = string.Empty;
        var wrongTitle = string.Empty;
        for (var attempt = 0; attempt < MaxFindAttempts && hwnd == IntPtr.Zero; attempt++)
        {
            // 0.10.10：轮询间隔仍是 FindIntervalMs（250ms）、随后仍泵 10ms —— 节拍逐值不变；
            // 只把"阻塞等待"换成"等长消息泵"。这里等的是**另一个进程**的窗口，枚举结果只取决于时间，
            // 不取决于本进程是否泵消息，所以等待语义不变。
            UiPump.For(FindIntervalMs);
            UiPump.For(10);

            foreach (var candidate in WindowBounds.EnumerateRaw(includeOwnProcess: true).Candidates)
            {
                if (candidate.ProcessId != (uint)process.Id)
                {
                    continue;
                }

                if (!IsHostWindow(candidate))
                {
                    rejected = candidate.Describe();

                    // 类名是 WPF 宿主窗、标题却不是 —— 这**不可能是别人的窗口**，是我们的
                    // --snap-host-window 自己坏了（标题常量与设置处漂移）→ 调用方必须记真失败(1)。
                    if (candidate.ClassName.StartsWith(WpfWindowClassPrefix, StringComparison.Ordinal))
                    {
                        wrongTitle = candidate.Describe();
                    }

                    continue;
                }

                hwnd = candidate.Hwnd;
                break;
            }
        }

        var exited = false;
        try
        {
            exited = process.HasExited;
        }
        catch (Exception)
        {
            // 取不到就按"还活着"处理（更保守：宁可判成环境问题，也不谎报代码缺陷）
        }

        if (hwnd == IntPtr.Zero)
        {
            var mismatch = wrongTitle.Length > 0;
            var why = mismatch
                ? $"本程序的 WPF 宿主窗口标题不是 '{WindowTitle}'（实际：{wrongTitle}）—— --snap-host-window 这条路径自己坏了"
                : rejected.Length > 0
                    ? $"进程 {process.Id} 的窗口里没有一个像宿主窗口（期望类名 {WpfWindowClassPrefix}… / 标题 '{WindowTitle}'，" +
                      $"实际看到的最后一个候选：{rejected}）"
                    : $"进程 {process.Id} 起不来窗口（已退出={exited}）";
            log.Warn($"  [宿主] {label} {why}");
            Handles[process.Id] = process;
            return new HostStatus(IntPtr.Zero, process.Id, exited, why, mismatch);
        }

        Handles[process.Id] = process;

        // 精确摆位（不动 z 序 → 不抢焦点）；置顶属性在上面那个进程里就设好了
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero,
            (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        Thread.Sleep(200);
        UiPump.For(50);

        return new HostStatus(hwnd, process.Id, false, string.Empty);
    }

    /// <summary>
    /// 这个候选是不是我们要找的那扇宿主窗？
    ///
    /// 判据两条（都取自**同一个** <c>--snap-host-window</c> 子进程的写法，不是猜的）：
    ///   * 标题 == <see cref="WindowTitle"/>（<c>SimOCR-snap-host</c>）；
    ///   * 类名以 <see cref="WpfWindowClassPrefix"/> 开头（WPF <c>Window</c> 的 <c>HwndWrapper[…]</c>）。
    ///
    /// 为什么不能只看 pid：pid 对得上只说明"是那个进程的某个顶层窗口"，
    /// 实测（VM）就撞上过一个"类名空 / 标题空"的窗口先出现 —— 拿它当靶子会让后续几何/采样点断言
    /// 全部指向一个不相干的窗口（假失败）。
    /// </summary>
    private static bool IsHostWindow(WindowCandidate candidate)
        => string.Equals(candidate.Title, WindowTitle, StringComparison.Ordinal)
           && candidate.ClassName.StartsWith(WpfWindowClassPrefix, StringComparison.Ordinal);

    // ================================================================= 虚拟屏幕（含**测试专用**覆盖）
    //
    // 遗留缺陷（0.10.7 清理线修）：`--accept-snap` 的靶子矩形过去是**硬编码**的
    // 1200,620 500x380，1156x768 的机器上整块在屏外 → 靶子被摆到屏外 → 按 pid 找回时拿到的是
    // 一个"类名空 / 标题空"的别的窗口 → 采样点找不到 → 悬停/单击/拖动 3 项全失败。
    // 遮挡探针（SelfTestSnapOcclusion）在 0.10.1 就把几何改成"从虚拟屏幕推导"，这里把**同一份**
    // 口径（同一个解析器、同一个环境变量、同一套"测试专用"声明）搬到共用的宿主窗口侧，
    // 让 `--accept-snap` 复用，而不是各写一套。

    /// <summary>
    /// **测试专用**虚拟屏幕覆盖（与 <c>--snap-probe-occlusion</c> 的 <c>screen=</c> 是同一个开关）：
    /// <c>1156x768</c> 或 <c>0,0,1156,768</c>。**只影响几何推导**，真实桌面一个像素都不动；
    /// 门禁与生产**都不设置**它，只给"大屏机器上验证小屏行为"这一件事用。
    /// </summary>
    internal const string ScreenOverrideEnv = "SIMOCR_SNAP_PROBE_SCREEN";

    /// <summary>
    /// 虚拟屏幕的解析结果。<see cref="FromOverride"/> 为 true 说明这台机器上的"屏幕"是测试用的假值，
    /// 因此任何"几何装不下"的结论都只能记 <c>[用例不成立]</c>（5），不能当成产品缺陷。
    /// </summary>
    internal sealed record ScreenInfo(Rect Screen, Rect RealScreen, bool FromOverride, string Note);

    /// <summary>
    /// 取"本探针看到的虚拟屏幕"：真实值来自 <see cref="MonitorLayout.GetVirtualScreen"/>（与生产同一份），
    /// 环境变量 <see cref="ScreenOverrideEnv"/> 可以**测试专用**地覆盖它。
    /// </summary>
    internal static ScreenInfo ResolveScreen(LogService? log = null)
    {
        var real = MonitorLayout.GetVirtualScreen();
        var envValue = Environment.GetEnvironmentVariable(ScreenOverrideEnv);
        if (string.IsNullOrWhiteSpace(envValue) || !TryParseScreen(envValue, out var parsed))
        {
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                log?.Warn($"  [覆盖] 环境变量 {ScreenOverrideEnv}={envValue} 看不懂（要 WxH 或 x,y,w,h）→ 忽略，按真实虚拟屏幕 {Format(real)} 走");
            }

            return new ScreenInfo(real, real, false, $"MonitorLayout.GetVirtualScreen() = {Format(real)}");
        }

        var note = $"环境变量 {ScreenOverrideEnv}={envValue}（**测试专用**，门禁不设）= {Format(parsed)}；" +
                   $"真实虚拟屏幕 {Format(real)}";
        if (!real.Contains(parsed))
        {
            note += "；⚠ 测试用屏幕不在真实屏幕内 —— 只可能出现在测试覆盖时";
        }

        return new ScreenInfo(parsed, real, true, note);
    }

    /// <summary>解析 <c>WxH</c> 或 <c>x,y,w,h</c>（测试用屏幕覆盖的两种写法）。</summary>
    internal static bool TryParseScreen(string spec, out Rect screen)
    {
        screen = Rect.Empty;
        var text = spec.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var numbers = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (numbers.Length == 4 &&
            int.TryParse(numbers[0], out var x) && int.TryParse(numbers[1], out var y) &&
            int.TryParse(numbers[2], out var w) && int.TryParse(numbers[3], out var h))
        {
            screen = new Rect(x, y, w, h);
            return true;
        }

        var size = text.Split('x', 'X', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (size.Length == 2 && int.TryParse(size[0], out var sw) && int.TryParse(size[1], out var sh))
        {
            screen = new Rect(0, 0, sw, sh);
            return true;
        }

        return false;
    }

    /// <summary>日志里的矩形写法（与遮挡探针逐字一致，便于两条用例的日志横向对照）。</summary>
    internal static string Format(Rect rect) => $"({rect.X:0},{rect.Y:0}) {rect.Width:0}x{rect.Height:0}";

    /// <summary>
    /// 结束**一扇**宿主窗（<c>--accept-snap</c> 收尾用：它只起一扇，允许"结束不了"只告警）。
    /// 先 <c>Kill</c> 子进程；拿不到进程时退回 <c>WM_CLOSE</c>。
    /// </summary>
    internal static void Close(HostStatus host, LogService log)
    {
        if (host.IsEmpty)
        {
            return;
        }

        if (Handles.Remove(host.Pid, out var process))
        {
            // 从登记表里取出来的 `Process` 是 IDisposable —— 用完（或换走 WM_CLOSE
            // 那条路）都要放掉它持有的句柄；`Process.Dispose` **不会**影响被启动的那个进程（我们
            // 该 Kill 的地方照旧 Kill）。
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                        log.Info($"  [宿主] 已结束宿主窗口进程 pid={host.Pid}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"  [宿主] 结束宿主窗口进程失败：{ex.Message}");
                }
            }
        }

        NativeMethods.PostMessage(host.Hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        log.Info("  [宿主] 已请求靶子窗口关闭（WM_CLOSE）");
    }

    /// <summary>
    /// 结束**全部**登记过的宿主窗（遮挡探针收尾用：它一次起两扇，必须先清干净再判结论）。
    /// 单扇失败只告警，绝不抛 —— 收尾路径不该改变退出码。
    /// </summary>
    internal static void KillAll(LogService log)
    {
        foreach (var process in Handles.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                log.Warn($"  [宿主] 结束进程失败：{ex.Message}");
            }
        }

        Handles.Clear();
    }
}
