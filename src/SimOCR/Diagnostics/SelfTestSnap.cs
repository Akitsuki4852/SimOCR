using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SimOCR.Interop;
using SimOCR.Overlay;

namespace SimOCR.Diagnostics;

/// <summary>
/// 「窗口吸附」的自检与验收：
///
/// * <c>--selftest-snap</c>：**真实窗口探针** —— 本机候选窗口 Top5 +
///   "已知几何的真实窗口能否被正确读出"（含 DWMWA vs GetWindowRect 的差值）。
///   纯几何用例已移到测试运行器（<c>SimOCR.Tests.exe --case=snap-geometry</c>）。
/// * <c>--accept-snap</c>：**真实鼠标**走完整功能 —— 悬停高亮 / 单击抓取整个窗口（并真的落盘 PNG）/
///   拖动跨窗口边缘吸附；证据图写到 %TEMP%\SimOCR-acceptance\。
///   0.10.7 修：靶子（外来宿主窗）的几何过去是**硬编码**的 1200,620 500x380，在 1156×768 的机器上
///   整块在屏外 → 假失败(1)。现在靶子从**真实虚拟屏幕推导**（<see cref="BuildTargetGeometry"/>），
///   桌面太小按 <c>[用例不成立]</c>(5) 记账。
///
/// **测试专用**：环境变量 <c>SIMOCR_SNAP_PROBE_SCREEN</c>（<c>WxH</c> 或 <c>x,y,w,h</c>，
/// 常量 <see cref="SnapHostWindow.ScreenOverrideEnv"/>）可以覆盖本入口看到的虚拟屏幕，
/// 用来在大屏机器上验证小屏行为；门禁与生产**都不设置**它。
/// </summary>
internal static class SnapSelfTest
{
    /// <summary>
    /// **按层的三态记账（与 `--snap-probe-occlusion`、`TableAcceptance` 完全同口径）：**
    ///
    /// * `Failures > 0` → **1**（真失败，门禁红）；
    /// * 无失败但 `InputInconclusive` → **5**（**用例不成立**：合成鼠标注入被系统丢弃 /
    ///   未生效，输入层那一档没验到；门禁按 `[跳过]` 记账，因为假失败和假通过一样有害）；
    /// * 无失败但 `SetupBlocked` → **5**（**用例不成立**：靶子几何装不下 / 起不出宿主窗 ——
    ///   三条断言一条都没跑；门禁同样按 `[跳过]` 记账）；
    /// * 都没有 → **0**。
    ///
    /// 分层的意思：**几何用例表与真实窗口探针（不依赖输入层）照常执行并计数**，
    /// 只有"必须靠真实鼠标事件才能验"的那一档会因为环境不可用而标为用例不成立 ——
    /// 它既不算通过，也不会把整条用例拖成红。
    ///
    /// **0.10.7 补第三类 `[用例不成立]`**：靶子几何**装不下**（桌面太小 / 推导出的靶子放不进屏内）
    /// 与"合成输入被系统丢弃"是**两种不同的原因**（前者连一条断言都没跑，后者三条断言都跑了、
    /// 只是没验到真实输入层）。两者都记退出码 5，但**结论文案必须各说各的** ——
    /// 把"环境装不下"写成"合成输入被丢弃"是另一种假归因（本次修的正是这类可读性缺陷）。
    /// </summary>
    private sealed class SnapRun
    {
        private readonly List<string> _inconclusive = [];
        private readonly List<string> _setupBlocked = [];

        internal int Failures { get; private set; }

        internal bool InputInconclusive => _inconclusive.Count > 0;

        /// <summary>用例根本没跑起来（靶子几何装不下 / 起不出宿主窗）—— 与输入层那一档分开记账。</summary>
        internal bool SetupBlocked => _setupBlocked.Count > 0;

        internal void Fail(LogService log, string why)
        {
            Failures++;
            log.Error($"  [失败] {why}");
        }

        internal void Inconclusive(LogService log, string why)
        {
            _inconclusive.Add(why);
            log.Error($"  [用例不成立] {why}");
        }

        /// <summary>靶子都没造出来：三条悬停/单击/拖动断言**一条都没跑**（不是"输入层没验到"）。</summary>
        internal void SetupBlockedInconclusive(LogService log, string why)
        {
            _setupBlocked.Add(why);
            log.Error($"  [用例不成立] {why}");
        }

        /// <summary>把已经在别处逐条打过 `[失败]` 的条数搬进来（不重复打日志）。</summary>
        internal void ImportFailures(int count) => Failures += count;

        internal int ExitCode(LogService log, string what, string evidenceSuffix = "")
        {
            var inconclusive = _inconclusive.Count + _setupBlocked.Count;

            if (Failures > 0)
            {
                log.Error($"=== {what}：未通过（{Failures} 项失败；另有 {inconclusive} 项用例不成立）{evidenceSuffix} ===");
                return 1;
            }

            if (SetupBlocked)
            {
                log.Error($"=== {what}：用例不成立（0 项失败；{inconclusive} 项用例不成立 —— **环境装不下这个场景**，\n"
                          + "    悬停 / 单击 / 拖动三条断言一条都没跑（**不是**『合成输入被系统丢弃』；也没有任何几何/输入层判据被放松））"
                          + $"{evidenceSuffix} ===");
                foreach (var why in _setupBlocked) { log.Error($"    [用例不成立] {why}"); }
                foreach (var why in _inconclusive) { log.Error($"    [用例不成立] {why}"); }
                log.Error("  按退出码 5（用例不成立）返回：门禁按 [跳过] 记账，**不是通过，也不是缺陷**");
                return 5;
            }

            if (InputInconclusive)
            {
                log.Error($"=== {what}：用例不成立（0 项失败；{_inconclusive.Count} 项用例不成立 —— 合成输入被系统丢弃，\n"
                          + "    本次没能验到『真实输入层』那一档；几何用例表 / 真实窗口探针照常执行且已通过）"
                          + $"{evidenceSuffix} ===");
                foreach (var why in _inconclusive) { log.Error($"    [用例不成立] {why}"); }
                log.Error("  按退出码 5（用例不成立）返回：门禁按 [跳过] 记账，**不是通过，也不是缺陷**");
                return 5;
            }

            log.Info($"=== {what}：全部通过{evidenceSuffix} ===");
            return 0;
        }
    }

    // ================================================================= --selftest-snap

    public static int RunSnap(LogService log)
    {
        log.Info("=== 自检：窗口吸附真实窗口边框 ===");

        // 0.10.7 起：这里过去先跑**纯几何用例表**（28 条，经 RunGeometryCases 薄壳
        // 调 tests/SimOCR.Tests/PureLogic/SnapGeometryChecks.cs）。那张表已经完全移出发布产物，
        // 本入口只剩"必须留在 Diagnostics/"的那一半：**真的起窗口**、读 DWM/GetWindowRect、
        // 核对候选枚举能不能按同样的矩形把它读出来。
        //
        // **判据没有丢**：`build/gate-check.ps1` 的 runner 用例 `snap-geometry`
        // （`SimOCR.Tests.exe --case=snap-geometry`）继续钉着 `[几何] 用例 28 条` 与生产配对链那条
        // `[通过] 生产配对：正确配对不吸附`；本入口自己的探针判据在 `selftest-snap`
        // 用例里钉住（两条探针窗口标题 + 收尾三态文案）。

        // 生产路径的候选枚举（含所有过滤器、不含本进程）
        var enumeration = WindowBounds.Enumerate();
        log.Info($"  [候选] 本机候选窗口 {enumeration.Candidates.Count} 个（枚举 {enumeration.ElapsedMs}ms）— {enumeration.Stats.Describe()}");
        log.Info("  [候选] Top5（标题 | x,y | w x h | 类名）:");
        foreach (var candidate in enumeration.Candidates.Take(5))
        {
            log.Info($"    {candidate.Title} | {candidate.Bounds.X:0},{candidate.Bounds.Y:0} | " +
                     $"{candidate.Bounds.Width:0} x {candidate.Bounds.Height:0} | {candidate.ClassName}");
        }

        if (enumeration.Candidates.Count == 0)
        {
            log.Warn("  [候选] 本机没有任何候选窗口：吸附会退化成只吸显示器/屏幕边缘");
        }

        var failures = RunWindowBoundsProbe(log);

        // ---selftest-snap 的 0/1/5 规则与 --accept-snap 共用一套（SnapRun.ExitCode）：
        // 本探针**不依赖输入层**，所以本入口只可能得 0 或 1；
        // 形态一致是为了让"用例不成立(5)"在任何入口都只有一个口径。
        var run = new SnapRun();
        run.ImportFailures(failures); // 明细已经在探针里逐条打过 [失败]，这里只搬条数
        return run.ExitCode(log, "自检结果");
    }

    /// <summary>
    /// 已知几何的真实 WPF 窗口 + DWM/GetWindowRect 对照。
    /// 目的：证明 (1) DWMWA 与 GetWindowRect 在无边框窗口上一致、在普通边框窗口上差一圈隐形边框；
    /// (2) 本进程的 DPI 感知是 PerMonitorV2，因此这些坐标就是 SelectionModel 用的物理像素；
    /// (3) 枚举（关掉本进程过滤）能把该窗口按同样的矩形读出来。
    /// </summary>
    private static int RunWindowBoundsProbe(LogService log)
    {
        var expected = new Rect(300, 200, 400, 300);
        var failures = 0;

        var borderless = CreateProbeWindow("SimOCR-snap-probe-borderless", WindowStyle.None, ResizeMode.NoResize);
        var chromed = CreateProbeWindow("SimOCR-snap-probe-chrome", WindowStyle.SingleBorderWindow, ResizeMode.CanResize);

        try
        {
            PlaceWindow(borderless, expected);
            PlaceWindow(chromed, expected);

            var enumeration = WindowBounds.EnumerateRaw(includeOwnProcess: true);
            log.Info($"  [真实窗口] 枚举(含本进程)候选 {enumeration.Candidates.Count} 个（{enumeration.ElapsedMs}ms；{enumeration.Stats.Describe()}）");

            failures += ProbeWindow(log, "无边框窗口 WindowStyle=None", borderless, expected, enumeration, assertAgainstTarget: true);
            failures += ProbeWindow(log, "普通边框窗口 SingleBorderWindow", chromed, expected, enumeration, assertAgainstTarget: false);
        }
        finally
        {
            borderless.Close();
            chromed.Close();
            UiPump.For(100);
        }

        return failures;
    }

    private static int ProbeWindow(
        LogService log,
        string label,
        Window window,
        Rect expected,
        WindowEnumerationResult enumeration,
        bool assertAgainstTarget)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dwm = WindowBounds.GetBounds(hwnd, out var fromDwm);
        NativeMethods.GetWindowRect(hwnd, out var raw);
        var winRect = new Rect(raw.Left, raw.Top, raw.Width, raw.Height);

        var found = false;
        var enumerated = Rect.Empty;
        foreach (var candidate in enumeration.Candidates)
        {
            if (candidate.Hwnd == hwnd)
            {
                found = true;
                enumerated = candidate.Bounds;
                break;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var origin = window.PointToScreen(new Point(0, 0));
        var context = NativeMethods.GetThreadDpiAwarenessContext();
        var awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(context);
        var isPerMonitorV2 = NativeMethods.AreDpiAwarenessContextsEqual(
            context, NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var windowDpi = NativeMethods.GetDpiForWindow(hwnd);

        log.Info($"  [{label}] SetWindowPos 指定 = {Sign(expected)}");
        log.Info($"    GetWindowRect              = {Sign(winRect)}  Δ相对指定 = {Delta(winRect, expected)}");
        log.Info($"    DWMWA_EXTENDED_FRAME_BOUNDS= {Sign(dwm)}  Δ相对指定 = {Delta(dwm, expected)} (fromDwm={fromDwm})");
        log.Info($"    GetWindowRect − DWMWA      = 左{Offset(winRect.Left - dwm.Left)} 上{Offset(winRect.Top - dwm.Top)} " +
                 $"右{Offset(winRect.Right - dwm.Right)} 下{Offset(winRect.Bottom - dwm.Bottom)}（>0 就是被排除的隐形缩放边框）");
        log.Info($"    PointToScreen(0,0)         = {origin.X:0},{origin.Y:0}  WPF DIP 缩放={dpi.DpiScaleX:F2} 窗口 DPI={windowDpi}");
        log.Info($"    DPI 感知：GetAwareness={awareness}(2=PerMonitorAware，该枚举分不出 v1/v2) " +
                 $"AreDpiAwarenessContextsEqual(PerMonitorV2)={isPerMonitorV2}");
        log.Info($"    枚举命中 = {found}  rect={(found ? Sign(enumerated) : "(未命中)")}  " +
                 $"Δ相对指定 = {(found ? Delta(enumerated, expected) : "-")}  Δ相对 DWMWA = {(found ? Delta(enumerated, dwm) : "-")}");

        var ok = found && MaxDelta(enumerated, dwm) <= 1;

        if (assertAgainstTarget)
        {
            ok = ok && MaxDelta(enumerated, expected) <= 2 && MaxDelta(dwm, expected) <= 2;
        }

        log.Info($"    → {(ok ? "OK" : "FAIL")}" + (assertAgainstTarget
            ? "（要求：枚举/DWMWA 与指定几何差 ≤2px）"
            : "（普通边框窗口只要求：枚举 == DWMWA，差 ≤1px；DWMWA 与指定几何的差就是隐形边框）"));

        return ok ? 0 : 1;
    }

    private static Window CreateProbeWindow(string title, WindowStyle style, ResizeMode resize)
    {
        var window = new Window
        {
            Title = title,
            WindowStyle = style,
            ResizeMode = resize,
            // 不能设 ShowInTaskbar=false：WPF 会给这种窗口加 WS_EX_TOOLWINDOW，就绕过了候选过滤器
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Brushes.White,
            Width = 400,
            Height = 300,
            Content = new TextBlock { Text = title, Margin = new Thickness(8) },
        };

        window.Show();
        UiPump.UntilIdle(window.Dispatcher);
        return window;
    }

    private static void PlaceWindow(Window window, Rect target)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            (int)target.X,
            (int)target.Y,
            (int)target.Width,
            (int)target.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // `Thread.Sleep(120)` + `UiPump.UntilIdle` 并成一次 120ms 切片泵（等消息的同时不冻结线程）。
        UiPump.For(120);
    }

    // ================================================================= --accept-snap

    /// <summary>
    /// 靶子（宿主窗）几何推导的结果：<see cref="Problem"/> 为空串 = 可以跑；非空 = 环境装不下
    /// （**用例不成立 5**，见 <see cref="SmallScreenProblem"/>）。
    /// </summary>
    private sealed record TargetGeometry(Rect Target, string Note, string Problem)
    {
        internal bool Usable => Problem.Length == 0;
    }

    /// <summary>靶子占虚拟屏幕的比例：留足边距 → 靶子**整块**在屏内，不会被系统夹到屏外。</summary>
    private const double TargetWidthRatio = 0.46;
    private const double TargetHeightRatio = 0.50;

    /// <summary>靶子尺寸下限（像素）。**不是审美**，是三条断言各自所需的最小工作面：
    ///   * 拖动那一档固定用 <c>Top+<see cref="DragFromOffsetPx"/> → Top+<see cref="DragSpanPx"/></c>
    ///     （跨窗口左边缘的导轨验证），高度至少要容得下 <see cref="DragSpanPx"/> 的行程再加一档余量；
    ///   * 悬停/单击的采样点与"最上层候选就是靶子自己"的搜索都要在靶子里；
    ///   * 覆盖层的工具条 + 尺寸标签要摆得下。
    /// 640×480 按下面 0.46×0.50 的比例只能推到 ≈294×240 → 高度不达标 → **[用例不成立]**。
    /// </summary>
    private const int MinTargetWidth = 240;
    private const int MinTargetHeight = 260;

    /// <summary>拖动那一档的起手点：靶子上边往下这么多像素（<c>DragSnapCheck</c> 用的就是这个值）。</summary>
    private const int DragFromOffsetPx = 40;

    /// <summary>拖动那一档的落点：靶子上边往下这么多像素 —— 也就是那一档真正要走的竖直行程。</summary>
    private const int DragSpanPx = 220;

    /// <summary>
    /// 最小可构造桌面。**与 <c>--snap-probe-occlusion</c> 同一口径（800×600）**：
    /// 低于它按三态语义记 <c>[用例不成立]</c>（5），绝不硬失败。
    /// 推导：桌面上限 MARGIN×2 后要装下 ≥260px 高（拖动的 220px 行程 + 40px 起手边距）的靶子，
    /// 即 H ≥ 320；800×600 在满足它的同时与遮挡探针的最小桌面**逐字一致**，两条用例不会各说各话。
    /// </summary>
    private const int MinScreenWidth = 800;
    private const int MinScreenHeight = 600;

    /// <summary>靶子与虚拟屏幕四边各留的最小边距（物理像素）。</summary>
    private const int MinScreenMargin = 24;

    /// <summary>
    /// **靶子窗口的目标矩形（物理像素）：从真实虚拟屏幕推导，绝不硬编码。**
    ///
    /// 0.10.7 修（虚拟机实测缺陷）：过去这里是写死的 <c>new Rect(1200, 620, 500, 380)</c>，
    /// 在 1156×768 的机器上**整块在屏幕外**（x 1200..1700 &gt; 1156；y 620..1000 &gt; 768）——
    /// 于是宿主窗被摆到屏外/被夹紧，按 pid 找回时拿到的是一个"类名空 / 标题空"的别的窗口，
    /// <c>TopmostAt(采样点)</c> 命不中靶子 → 打印"靶子窗口整块都被别的窗口盖住了" →
    /// 悬停/单击/拖动 3 项全失败 → 退出码 1（真失败码）——**那是假失败**。
    ///
    /// 这与另一处是**同一类问题**：遮挡探针（<c>SelfTestSnapOcclusion.BuildGeometry</c>）在 0.10.1
    /// 就改成"从 <see cref="MonitorLayout.GetVirtualScreen"/> 推导"，<c>--accept-snap</c> 当时漏改了。
    /// 现在两条用例共用同一个屏幕解析器（<see cref="SnapHostWindow.ResolveScreen"/>，含**测试专用**的
    /// <c>SIMOCR_SNAP_PROBE_SCREEN</c> 覆盖），推导口径也一致：屏幕的 46%×50%、**居中**留边距。
    ///
    /// 为什么必须**整块在屏内**：靶子要"最上层候选就是它自己"的点、要被真实鼠标点中、
    /// 要被拖动跨过自己的左边缘 —— 任何一条落在屏外的边都会被 <c>Limits</c> 夹回，
    /// 断言必然假失败（与遮挡探针里"屏外的边只跳过、不断言"是同一个道理，只是这里没有可跳过的边：
    /// 三条断言都要靶子，靶子装不下就只能整条用例不成立）。
    /// </summary>
    private static TargetGeometry BuildTargetGeometry(Rect screen)
    {
        if (screen.IsEmpty || screen.Width < MinScreenWidth || screen.Height < MinScreenHeight)
        {
            return new TargetGeometry(Rect.Empty, string.Empty, SmallScreenProblem(screen));
        }

        var width = Math.Round(screen.Width * TargetWidthRatio);
        var height = Math.Round(screen.Height * TargetHeightRatio);
        var target = new Rect(
            Math.Round(screen.Left + ((screen.Width - width) / 2)),
            Math.Round(screen.Top + ((screen.Height - height) / 2)),
            width,
            height);

        var margins = new[]
        {
            target.Left - screen.Left,
            target.Top - screen.Top,
            screen.Right - target.Right,
            screen.Bottom - target.Bottom,
        };

        var note = $"靶子 = 虚拟屏幕 {screen.Width:0}×{screen.Height:0} 的 {TargetWidthRatio:P0}×{TargetHeightRatio:P0}" +
                   $"（居中留边距）→ {Sign(target)}；四边留白 左{margins[0]:0} 上{margins[1]:0} 右{margins[2]:0} 下{margins[3]:0}" +
                   $"（要求 ≥{MinScreenMargin}px）；靶子完全在屏内={screen.Contains(target)}";

        if (width < MinTargetWidth || height < MinTargetHeight)
        {
            return new TargetGeometry(target, note,
                $"推导出的靶子 {Sign(target)} 小于本用例所需的最小工作面 {MinTargetWidth}×{MinTargetHeight}" +
                $"（拖动那一档要跨 {DragSpanPx}px 的行程 + 采样点/工具条要摆得下）：环境装不下这个场景");
        }

        if (!screen.Contains(target) || margins.Min() < MinScreenMargin)
        {
            return new TargetGeometry(target, note,
                $"推导出的靶子 {Sign(target)} 没有整块落在虚拟屏幕 {Sign(screen)} 内、或留白不足 {MinScreenMargin}px" +
                $"（最小留白 {margins.Min():0}px）：宿主窗会被系统夹到屏外，靶子不再是可采样的目标");
        }

        return new TargetGeometry(target, note, string.Empty);
    }

    /// <summary>桌面/靶子装不下时**唯一**的出路：`[用例不成立]`(5)，并把实际屏幕与推导结果写清楚。</summary>
    private static string SmallScreenProblem(Rect screen)
        => $"虚拟屏幕 {Sign(screen)} 小于本用例的最小可构造桌面 {MinScreenWidth}×{MinScreenHeight}" +
           $"（靶子要居中留 ≥{MinScreenMargin}px 边距并保证 ≥{MinTargetWidth}×{MinTargetHeight} 的工作面）：环境装不下这个场景";

    /// <summary>
    /// 推导失败时的靶子写法。**不能**直接 <c>Sign(Rect.Empty)</c>：WPF 的 <c>Rect.Empty</c> 是
    /// <c>(∞,∞) -∞x-∞</c>，日志里那串无穷符号既不等于"没推导出来"，也会让读日志的人以为是数值错误
    /// （0.10.7 小屏实测第一版就打出了这串）。
    /// </summary>
    private static string DerivedTargetText(TargetGeometry geometry)
        => geometry.Target.IsEmpty ? "(未推导：屏幕本身就没过最小桌面阈值)" : Sign(geometry.Target);

    /// <summary>靶子（宿主窗）的进程与几何 —— 起不来时按三态语义带上"为什么"。</summary>
    private sealed class HostWindow
    {
        public IntPtr Hwnd { get; set; }

        public Rect Bounds { get; set; }

        /// <summary>请求（推导）出来的靶子矩形 —— 与 <see cref="Bounds"/>（实测 DWMWA）对照用。</summary>
        public Rect Target { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        /// <summary>验收用采样点（靶子窗口里"最上层候选就是它自己"的那个点）。</summary>
        public int PointX { get; set; }

        public int PointY { get; set; }

        /// <summary>宿主窗口进程的 pid（收尾时交给 <see cref="SnapHostWindow.Close"/> 结束它）。</summary>
        public int Pid { get; set; }

        /// <summary>起不来时：进程是否已经退出 —— 决定 [用例不成立](5) 还是真失败(1)。</summary>
        public bool ProcessExited { get; set; }

        /// <summary>
        /// 起不来时：是不是"看到了本程序的 WPF 宿主窗、但标题不是 <see cref="SnapHostWindow.WindowTitle"/>"
        /// —— 那是 <c>--snap-host-window</c> 自己坏了 → 真失败(1)，**不能**记 [用例不成立]。
        /// </summary>
        public bool HostWindowMismatch { get; set; }

        /// <summary>起不来时的原因（原样来自 <see cref="SnapHostWindow.HostStatus.Why"/>）。</summary>
        public string Why { get; set; } = string.Empty;
    }

    /// <summary>
    /// <c>--snap-host-window=x,y,w,h[;秒]</c>：开一扇**已知几何**的窗口并保持一段时间，供验收当"外来窗口"用
    /// （不依赖 notepad / 打包应用激活，几何精确到像素）。窗口过程与主程序同源，但是**另一个进程**，
    /// 因此照样要过"排除本进程"这条过滤器 —— 这正是验收想验证的。
    /// </summary>
    public static int RunHostWindow(string arg, LogService log)
    {
        var parts = arg.Split('=', 2, StringSplitOptions.TrimEntries);
        var spec = parts is { Length: 2 } ? parts[1] : string.Empty;
        var numbers = spec.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 缺省几何（只在不给 x,y,w,h 时用）：同样**从虚拟屏幕推导**，不再写死 1200,620 500x380
        // —— 那个字面量在小屏机器上整块在屏外，正是本次要修的缺陷；推导不出来的极端情况留 1200,620 的兜底。
        var geometry = BuildTargetGeometry(SnapHostWindow.ResolveScreen(log).Screen);
        var target = geometry.Usable ? geometry.Target : new Rect(1200, 620, 500, 380);
        var seconds = 30;

        if (numbers.Length >= 4 &&
            int.TryParse(numbers[0], out var x) && int.TryParse(numbers[1], out var y) &&
            int.TryParse(numbers[2], out var w) && int.TryParse(numbers[3], out var h))
        {
            target = new Rect(x, y, w, h);
        }

        if (numbers.Length >= 5 && int.TryParse(numbers[4], out var hold) && hold > 0)
        {
            seconds = Math.Min(hold, 600);
        }

        // 第 6 个参数 = top：把本窗口抬进**置顶组**（复现"谁盖住谁"时用；同组的置顶窗按启动先后压叠）
        var topmost = numbers.Length >= 6 && numbers[5].Equals("top", StringComparison.OrdinalIgnoreCase);

        var window = new Window
        {
            // 标题是**共享常量**（SnapHostWindow.WindowTitle）：按 pid 找回窗口时要拿它校验，
            // 两处各写一份字面量迟早漂移（0.10.7 把校验做进 SnapHostWindow.Launch）。
            Title = SnapHostWindow.WindowTitle,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF3, 0xE4)),
            Content = new TextBlock
            {
                Text = "SimOCR 外来测试窗口",
                FontSize = 20,
                Margin = new Thickness(16),
                Foreground = Brushes.Black,
            },
        };

        window.Show();
        var hwnd = new WindowInteropHelper(window).Handle;
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero,
            (int)target.X, (int)target.Y, (int)target.Width, (int)target.Height,
            NativeMethods.SWP_SHOWWINDOW);

        if (topmost)
        {
            // 抬进置顶组：这样同组的另一扇置顶窗就能按启动顺序压住它（实测 HWND_TOP 在本环境不生效）
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            log.Info($"宿主窗口已抬进置顶组（{Sign(WindowBounds.GetBounds(hwnd, out _))}）");
        }

        var bounds = WindowBounds.GetBounds(hwnd, out var fromDwm);
        log.Info($"宿主窗口 (pid={Environment.ProcessId}) 指定={Sign(target)} DWMWA={Sign(bounds)}(fromDwm={fromDwm}) 保持 {seconds}s topmost={topmost}");

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            UiPump.For(200);
        }

        window.Close();
        return 0;
    }

    /// <summary>
    /// 在靶子窗口里找一个"**最上层候选就是它自己**"的点。
    /// 为什么需要：本机桌面上还有别的窗口压着靶子的中心（本机实测是记事本，
    /// (1450,810) 既在靶子里也在记事本里，而记事本的 z 序更靠前）——
    /// 这时候按"最上层窗口"规则高亮的本来就该是记事本，所以验收要点一个只属于靶子的点。
    /// </summary>
    private static (int X, int Y) FindHostPoint(HostWindow host, LogService log)
    {
        var candidates = WindowBounds.EnumerateRaw(includeOwnProcess: true).Candidates;
        var bounds = host.Bounds;
        double[] fracs = [0.5, 0.85, 0.15, 0.7, 0.3, 0.95, 0.05];

        foreach (var fy in fracs)
        {
            foreach (var fx in fracs)
            {
                var x = (int)Math.Round(bounds.Left + (bounds.Width * fx));
                var y = (int)Math.Round(bounds.Top + (bounds.Height * fy));
                var top = WindowBounds.TopmostAt(candidates, x, y);
                if (top?.Hwnd == host.Hwnd)
                {
                    var center = Center(bounds);
                    if (x != center.X || y != center.Y)
                    {
                        log.Info($"  [宿主] 靶子中心 {center.X},{center.Y} 被其它窗口盖住（最上层是 " +
                                 $"'{WindowBounds.TopmostAt(candidates, center.X, center.Y)?.Title}'）→ 采样点改用 {x},{y}");
                    }

                    return (x, y);
                }
            }
        }

        log.Warn("  [宿主] 靶子窗口整块都被别的窗口盖住了，采样点退回中心（高亮可能落到别人身上）");
        return Center(bounds);
    }

    /// <summary>把靶子窗口重新提到（非置顶组的）最前，避免别的程序在启动完成后把自己顶上来。</summary>
    private static void RaiseHostWindow(HostWindow host, LogService log)
    {
        if (host.Hwnd == IntPtr.Zero)
        {
            return;
        }

        var bounds = WindowBounds.GetBounds(host.Hwnd, out _);
        NativeMethods.SetWindowPos(
            host.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        Thread.Sleep(250);
        UiPump.For(50);

        if (!bounds.IsEmpty && Math.Abs(bounds.Left - host.Bounds.Left) > 0.5)
        {
            log.Warn($"  [宿主] 靶子窗口位置变了？{Sign(host.Bounds)} → {Sign(bounds)}");
            host.Bounds = bounds;
        }
    }

    /// <summary>
    /// 开一扇**外来**窗口当靶子 —— 与遮挡探针**共用同一份**启动器
    /// （<see cref="SnapHostWindow.Launch"/>：起自己的 <c>--snap-host-window</c> 子进程 + 按 pid 找回 HWND），
    /// 不再去找 <c>notepad.exe</c>（Store 版 / 精简镜像上根本没有可靠的那个窗口）。
    ///
    /// **为什么这条路比记事本可靠**（0.10.4 起的实测口径，随合并搬到这里）：
    ///   1) Win11 26200 的记事本是"单进程多标签"：用它打开文件**不会**产生新窗口，只改已有窗口的标题，
    ///      "把窗口摆到已知矩形"就等于动了用户正在编辑的那个窗口（本机那个还是"已修改未保存"状态）；
    ///   2) 它启动慢，激活时会把自己的窗口提到 z 序最前，正好盖住靶子窗口 ——
    ///      悬停高亮就变成高亮记事本（实测到过：高亮矩形 = 记事本 109,104 1430x748 而不是靶子）；
    ///   3) 宿主窗的窗口过程与主程序同源但**在另一个进程里**，照样要过"排除本进程"这条过滤器 ——
    ///      这正是验收想验证的东西，而且几何精确到像素、随时可结束。
    ///
    /// **几何由虚拟屏幕推导**（<see cref="BuildTargetGeometry"/>，与 <see cref="SnapOcclusionProbe"/>
    /// 同一个屏幕解析器）：装不下就整条 <c>[用例不成立]</c>(5)，返回的 <see cref="HostWindow.Target"/>
    /// 永远是**推导**出来的那一个（<see cref="HostWindow.Bounds"/> 是启动后的实测 DWMWA）。
    /// </summary>
    private static (HostWindow Host, TargetGeometry Geometry) LaunchHostWindow(LogService log)
    {
        var screenInfo = SnapHostWindow.ResolveScreen(log);
        var geometry = BuildTargetGeometry(screenInfo.Screen);

        log.Info($"  [宿主] 虚拟屏幕 = {Sign(screenInfo.Screen)}（来源：{screenInfo.Note}）");
        log.Info($"  [宿主] 靶子几何推导：{(geometry.Note.Length > 0
            ? geometry.Note
            : $"虚拟屏幕 {Sign(screenInfo.Screen)} 没过最小桌面阈值 → 不推导靶子矩形")}");
        if (!geometry.Usable)
        {
            log.Warn($"  [宿主] {geometry.Problem}");
            return (new HostWindow { Target = geometry.Target }, geometry);
        }

        var target = geometry.Target;

        // topmost: true —— **0.10.7 修（虚拟机实测）**：与遮挡探针走**同一条路**，不再自创第二套。
        //
        // 为什么靶子必须进置顶组：`--accept-snap` 的靶子是**另一个进程**的普通窗口，而验收过程中
        // 桌面上任何后来居上的窗口（控制台、终端、别的应用）都会整块盖住它 —— 于是
        // `FindHostPoint` 找不到"最上层候选就是靶子自己"的点，打印
        // `靶子窗口整块都被别的窗口盖住了，采样点退回中心` → 高亮/单击落到别人身上 → 3 项失败。
        // 遮挡探针（`--snap-probe-occlusion`）在**同一个 VM 上**用 `topmost:true` 一直是正常的，
        // 说明这台机器的置顶带是可靠的 —— 所以正确的修法是把靶子抬进置顶带，而不是换一套采样逻辑。
        //
        // 与覆盖层的关系：覆盖层（`OverlayWindow`）也是在靶子之后 Show + `SetWindowPos(HWND_TOPMOST)`
        // （`OverlayWindow.xaml.cs:233`），因此它仍然压在靶子之上 —— 真实鼠标事件照旧落在覆盖层上，
        // 靶子只是不再被**第三方**窗口盖住。主机实测：`--accept-snap` 仍是 0 或 5（三态语义不变）。
        var host = SnapHostWindow.Launch(target, "--accept-snap 靶子", log, topmost: true);
        if (host.IsEmpty)
        {
            return (new HostWindow
            {
                Target = target,
                ProcessExited = host.ProcessExited,
                HostWindowMismatch = host.HostWindowMismatch,
                Why = host.Why,
                Pid = host.Pid,
            }, geometry);
        }

        var bounds = WindowBounds.GetBounds(host.Hwnd, out var fromDwm);
        var title = string.Empty;
        var className = string.Empty;
        foreach (var candidate in WindowBounds.EnumerateRaw(includeOwnProcess: true).Candidates)
        {
            if (candidate.Hwnd == host.Hwnd)
            {
                title = candidate.Title;
                className = candidate.ClassName;
                break;
            }
        }

        log.Info($"  [宿主] 自建靶子窗口 hwnd=0x{host.Hwnd.ToInt64():X} 类名={className} 标题='{title}'");
        log.Info($"  [宿主] SetWindowPos 指定（推导）={Sign(target)}  DWMWA={Sign(bounds)}(fromDwm={fromDwm})  " +
                 $"实测完全在屏内={screenInfo.Screen.Contains(bounds)}");
        if (screenInfo.Screen.Contains(target) && !screenInfo.Screen.Contains(bounds))
        {
            log.Warn($"  [宿主] 推导矩形在屏内、但**实测** DWMWA {Sign(bounds)} 不是 —— 系统把窗口摆到了别处（DPI/夹紧）");
        }

        return (new HostWindow
        {
            Hwnd = host.Hwnd,
            Bounds = bounds,
            Target = target,
            Title = title,
            Label = $"{className} '{title}'",
            Pid = host.Pid,
        }, geometry);
    }

    private static void CleanupHostWindow(HostWindow host, LogService log)
    {
        if (host.Hwnd == IntPtr.Zero)
        {
            return;
        }

        SnapHostWindow.Close(new SnapHostWindow.HostStatus(host.Hwnd, host.Pid, false, string.Empty), log);
    }

    public static int RunAcceptance(CaptureSessionController session, Settings.SettingsService settings, LogService log)
    {
        log.Info("=== 验收：窗口吸附真实鼠标 悬停高亮 → 单击抓取 → 拖动吸附 ===");

        // 证据图与"抓下来的窗图"是**同一个**目录（%TEMP%\SimOCR-acceptance）——
        // 之前这里把同一个字面量又写了一遍、还多建了一次目录（见 SelfTestEvidence）。
        // 目录建不出来 = **环境问题** → exit 5 + 原因（过去会变成未捕获异常 → exit 2）。
        var evidenceDir = SelfTestEvidence.AcceptanceDirectory(log);
        if (evidenceDir is null)
        {
            log.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return 5;
        }

        var outputDir = evidenceDir;
        var beforePngs = SelfTestEvidence.SnapshotPngs(outputDir);

        var originalDirectory = settings.Current.Save.Directory;
        var originalAutoSave = settings.Current.Save.AutoSaveAfterRelease;
        var originalClose = settings.Current.Save.CloseOverlayAfterAction;
        var originalSnap = settings.Current.Behavior.SnapToWindows;
        var originalTolerance = settings.Current.Behavior.SnapTolerancePx;

        settings.Update(s =>
        {
            s.Save.Directory = outputDir;
            s.Save.AutoSaveAfterRelease = false;
            s.Save.CloseOverlayAfterAction = true;
            s.Behavior.SnapToWindows = true;
            s.Behavior.SnapTolerancePx = 8;
        }, notify: false);

        var run = new SnapRun();
        var (host, geometry) = LaunchHostWindow(log);
        var screen = SnapHostWindow.ResolveScreen().Screen;

        try
        {
            if (host.Hwnd == IntPtr.Zero)
            {
                // **三态分流**（与 --snap-probe-occlusion 完全同口径）。
                // 0.10.7 把"环境"这一档**再拆一层**（清理线 · 测试可信）：靶子几何过去是硬编码的，
                // 小屏机器上"装不下"会走到下面 ②/① 的兜底文案里、被读成"没有可用的交互桌面"——
                // 那是**另一个原因**，日志必须说清楚，否则同一台 VM 上没人能判断该修产品还是换机器。
                //
                //   ① 几何装不下（**环境**）→ [用例不成立](5)：桌面太小 / 推导出的靶子放不进屏内。
                //      **优先级最高**：这是本次运行**唯一**能确定的、可解释的原因（实际虚拟屏 + 推导矩形都写进日志）；
                //   ② 宿主进程自己退了 / 看到了本程序的 WPF 窗但标题漂移（**产品**）→ 真失败(1)。
                //      这两条都是 `--snap-host-window` 这条路径自己坏了，与桌面大小无关；
                //   ③ 进程活着、几何也装得下，却找不到一扇像宿主窗的窗口（**环境**：无交互桌面 /
                //      headless / 会话未就绪 / 沙箱 / 该 pid 下压根没建出窗口）→ [用例不成立](5)。
                //      **瞬态**（窗口还没建出来）不会走到这里：SnapHostWindow.Launch 已经轮询
                //      60×250ms ≈15s、期间一直 continue，只有全部尝试失败才返回空。
                //
                // 旧代码在这里一律 `[失败] 没能创建/找到外来测试窗口（notepad.exe）` + exit 2 ——
                // 既把"环境没有桌面"谎报成缺陷，也在日志里留下一条早已不成立的 notepad 归因。
                if (!geometry.Usable)
                {
                    run.SetupBlockedInconclusive(
                        log,
                        $"环境装不下这个场景：{geometry.Problem}；实际虚拟屏幕 = {Sign(screen)}；" +
                        $"推导出的靶子矩形 = {DerivedTargetText(geometry)}；靶子几何推导 = " +
                        $"{(geometry.Note.Length > 0 ? geometry.Note : "(屏幕没过最小阈值，未推导)")} —— 这不是吸附缺陷，" +
                        "悬停/单击/拖动三条断言一条都没跑（几何用例表与真实窗口探针不受影响，见 --selftest-snap）");
                }
                else if (host.ProcessExited || host.HostWindowMismatch)
                {
                    run.Fail(log, $"自建宿主窗没起来（{host.Why}）：--snap-host-window 这条路径自己坏了"
                                  + $"（靶子几何 {Sign(geometry.Target)} 是推导出来的、装得下，与本用例的桌面大小无关）");
                }
                else
                {
                    run.SetupBlockedInconclusive(
                        log,
                        $"自建宿主窗没起来（{host.Why}）—— 靶子几何 {Sign(geometry.Target)} 本身装得下，"
                        + "但没有可用的交互桌面（headless / 会话未就绪 / 沙箱）；"
                        + "悬停/单击/拖动三条断言一条都没跑（几何用例表与真实窗口探针不受影响，见 --selftest-snap）");
                }

                return run.ExitCode(log, "验收结束");
            }

            // 靶子必须在采样前**再确认一次整块在屏内**：FindHostPoint 的兜底是与靶子几何无关的
            // "退回中心"，屏外的靶子会让后面三条断言一起假失败 —— 那种情况下本轮没有可断言的目标。
            if (!screen.Contains(host.Bounds))
            {
                run.SetupBlockedInconclusive(
                    log,
                    $"靶子实测几何 {Sign(host.Bounds)} 没有整块落在虚拟屏幕 {Sign(screen)} 内" +
                    $"（推导矩形 {Sign(host.Target)}）：宿主窗被系统夹到了屏外，悬停/单击/拖动都没有可采样的目标");
                return run.ExitCode(log, "验收结束");
            }

            var point = FindHostPoint(host, log);
            host.PointX = point.X;
            host.PointY = point.Y;

            HoverCheck(session, host, log, evidenceDir, run);
            ClickCaptureCheck(session, host, log, evidenceDir, outputDir, beforePngs, run);
            DragSnapCheck(session, host, log, evidenceDir, run);
        }
        catch (Exception ex)
        {
            run.Fail(log, $"验收异常：{ex}");
        }
        finally
        {
            settings.Update(s =>
            {
                s.Save.Directory = originalDirectory;
                s.Save.AutoSaveAfterRelease = originalAutoSave;
                s.Save.CloseOverlayAfterAction = originalClose;
                s.Behavior.SnapToWindows = originalSnap;
                s.Behavior.SnapTolerancePx = originalTolerance;
            }, notify: false);

            CleanupHostWindow(host, log);
        }

        // 0/1/5：真失败优先(1)；只有"输入层没用上"这一档时是**用例不成立**(5，门禁按 [跳过])。
        return run.ExitCode(log, "验收结束", $"；证据目录 {evidenceDir}");
    }


    /// <summary>
    /// 探测"合成鼠标注入"是否真的生效：先挪到角落，再看光标有没有真的到那里。
    /// 沙箱 / 低完整性令牌下 SendInput 会被**静默丢弃**（返回 0），此时退回同路径钩子，
    /// 并在日志里明确写出"这一步没有验到真实输入层"。
    /// </summary>
    private static bool ProbeInjection(LogService log, string tag)
    {
        var origin = MonitorLayout.GetCursorPosition();
        var sent = InputSimulator.MoveTo(3, 3);
        UiPump.For(200);
        var landed = MonitorLayout.GetCursorPosition();
        var works = Math.Abs(landed.X - 3) <= 2 && Math.Abs(landed.Y - 3) <= 2;

        var setOk = "-";
        if (!works)
        {
            // 再试一次"直接挪光标"：用来区分"注入被丢弃"和"光标本身就动不了"
            setOk = NativeMethods.SetCursorPos(3, 3) ? "SetCursorPos=成功" : "SetCursorPos=失败";
            UiPump.For(120);
            var afterSet = MonitorLayout.GetCursorPosition();
            setOk += $"（之后光标 {afterSet.X},{afterSet.Y}）";
        }

        log.Info($"  {tag} 合成输入探测：光标 {origin.X},{origin.Y} → SendInput 返回 {sent} → 实际 {landed.X},{landed.Y} {setOk} → " +
                 (works ? "SendInput 生效（真实鼠标事件）" : "SendInput 被丢弃（沙箱/低完整性）→ 改用同路径钩子"));
        return works;
    }

    /// <summary>(a) 悬停高亮：真实鼠标移进外来窗口 → 叠加层应高亮它，且高亮矩形 == 它的 DWMWA 边框。</summary>
    private static void HoverCheck(CaptureSessionController session, HostWindow host, LogService log, string evidenceDir, SnapRun run)
    {
        RaiseHostWindow(host, log);

        var frame = session.CaptureScreenOnly();
        if (frame is null)
        {
            run.Fail(log, "[悬停] 抓屏失败");
            return;
        }

        var overlay = session.CreateOverlay(frame);
        overlay.Show();
        UiPump.For(500);

        var center = (X: host.PointX, Y: host.PointY);
        var injection = ProbeInjection(log, "[悬停]");
        if (!injection)
        {
            // 输入层那一档：合成鼠标被系统丢弃 → 这一步**没验到真实输入层**。
            // 按裁决记 [用例不成立]（5），**不**继续把它算成通过，也不算成失败。
            run.Inconclusive(log, "[悬停] 合成输入被系统丢弃（沙箱/低完整性），未验到输入层 —— 悬停高亮这一档不成立");
        }

        if (injection)
        {
            InputSimulator.MoveTo(center.X, center.Y);
            UiPump.For(500);
        }
        else
        {
            overlay.TestHoverWindow(center.X, center.Y);
            UiPump.For(200);
        }

        var cursorNow = MonitorLayout.GetCursorPosition();
        var hover = overlay.TestHoverWindowBounds;
        var outline = overlay.TestHoverOutlineVisible;
        var hint = overlay.TestHintText;
        var label = overlay.TestSizeLabelText;
        var tolerance = overlay.TestSnapTolerance;
        var candidates = overlay.TestCandidateCount;

        log.Info($"  [悬停] 鼠标→({center.X},{center.Y}) 实际光标=({cursorNow.X},{cursorNow.Y})  候选窗口={candidates} 容差={tolerance}px");
        log.Info($"  [悬停] 高亮窗口='{overlay.TestHoverWindowTitle}' 高亮矩形={(hover is null ? "(无)" : Sign(hover.Value))}  " +
                 $"期望(DWMWA)={Sign(host.Bounds)}  Δ={(hover is null ? "-" : Delta(hover.Value, host.Bounds))}");
        log.Info($"  [悬停] 高亮边框可见={outline}  尺寸标签='{label}'  提示='{hint}'");

        var ok = hover is { } highlighted
                 && MaxDelta(highlighted, host.Bounds) <= 1
                 && outline
                 && hint.Contains("单击抓取整个窗口", StringComparison.Ordinal);

        SelfTestEvidence.SaveWindowPng(overlay, Path.Combine(evidenceDir, "snap-window-hover.png"), log);

        overlay.CloseOverlay();
        UiPump.For(200);

        log.Info($"  [悬停] → {(ok ? "OK" : "FAIL")}（输入层={(injection ? "真实鼠标" : "未验到，本档用例不成立")}）");
        if (injection && !ok)
        {
            run.Fail(log, "[悬停] 真实鼠标已注入但高亮矩形/边框/提示不符（见上方 [悬停] 明细）");
        }

    }

    /// <summary>(b) 单击不拖动：应抓取整个窗口，并且保存后落盘的 PNG 尺寸 == 窗口边框尺寸。</summary>
    private static void ClickCaptureCheck(
        CaptureSessionController session,
        HostWindow host,
        LogService log,
        string evidenceDir,
        string outputDir,
        HashSet<string> beforePngs,
        SnapRun run)
    {
        RaiseHostWindow(host, log);

        var frame = session.CaptureScreenOnly();
        if (frame is null)
        {
            run.Fail(log, "[单击] 抓屏失败");
            return;
        }

        var overlay = session.CreateOverlay(frame);
        overlay.Show();
        UiPump.For(500);

        var center = (X: host.PointX, Y: host.PointY);
        var injection = ProbeInjection(log, "[单击]");
        if (!injection)
        {
            run.Inconclusive(log, "[单击] 合成输入被系统丢弃（沙箱/低完整性），未验到输入层 —— 单击抓取 + 落盘这一档不成立");
        }

        if (injection)
        {
            InputSimulator.LeftClick(center.X, center.Y);
            UiPump.For(500);
        }
        else
        {
            overlay.TestPressAt(center.X, center.Y);
            UiPump.For(120);
            overlay.TestReleaseAt();
            UiPump.For(300);
        }

        var cursorNow = MonitorLayout.GetCursorPosition();
        var selection = overlay.SelectionBounds;
        var toolbar = overlay.TestToolbarVisible;
        var label = overlay.TestSizeLabelText;

        log.Info($"  [单击] 单击 ({center.X},{center.Y})（无拖动，实际光标 {cursorNow.X},{cursorNow.Y}）");
        log.Info($"  [单击] 选区={Sign(selection)}  期望(DWMWA)={Sign(host.Bounds)}  Δ={Delta(selection, host.Bounds)}  " +
                 $"工具条可见={toolbar} 尺寸标签='{label}'");

        SelfTestEvidence.SaveWindowPng(overlay, Path.Combine(evidenceDir, "snap-click-capture.png"), log);

        var selectionOk = MaxDelta(selection, host.Bounds) <= 1 && toolbar;
        var savedOk = false;

        if (selectionOk)
        {
            var button = overlay.TestSaveButtonCenterScreen();
            if (injection)
            {
                InputSimulator.LeftClick((int)button.X, (int)button.Y);
            }
            else
            {
                log.Warn("  [单击] 输入注入不可用 → 用 TestRaiseAction(Save) 触发同一条保存链路");
                overlay.TestRaiseAction(OverlayActionKind.Save);
            }

            UiPump.For(1500);

            var produced = SelfTestEvidence.NewestNewPng(outputDir, beforePngs);

            if (produced is null)
            {
                log.Error("  [单击] 触发保存但没有 PNG 落盘");
            }
            else
            {
                var expectedSize = $"{(int)Math.Round(selection.Width)}x{(int)Math.Round(selection.Height)}";
                var actual = SelfTestEvidence.DescribePng(produced);
                savedOk = actual == expectedSize;
                log.Info($"  [单击] 保存按钮@({button.X:0},{button.Y:0}) → {Path.GetFileName(produced)} 尺寸={actual} 期望={expectedSize}");
            }
        }
        else
        {
            log.Error("  [单击] 选区不等于窗口边框（或工具条没出现），跳过保存验证");
        }

        overlay.CloseOverlay();
        UiPump.For(200);

        var ok = selectionOk && savedOk;
        log.Info($"  [单击] → {(ok ? "OK" : "FAIL")}（选区=窗口边框 {selectionOk} 落盘尺寸匹配 {savedOk}；输入层={(injection ? "真实鼠标" : "未验到，本档用例不成立")}）");
        if (injection && !ok)
        {
            run.Fail(log, "[单击] 真实鼠标已注入但选区/落盘尺寸不符（见上方 [单击] 明细）");
        }
    }

    /// <summary>(c) 拖动跨窗口边缘：拖到距边缘 3px 处，松手后选区应精确落在边缘上（导轨在拖拽中被记录/绘制）。</summary>
    private static void DragSnapCheck(CaptureSessionController session, HostWindow host, LogService log, string evidenceDir, SnapRun run)
    {
        RaiseHostWindow(host, log);

        var frame = session.CaptureScreenOnly();
        if (frame is null)
        {
            run.Fail(log, "[拖动] 抓屏失败");
            return;
        }

        var overlay = session.CreateOverlay(frame);
        overlay.Show();
        UiPump.For(500);

        var edge = (int)Math.Round(host.Bounds.Left);
        var fromX = edge + 60;
        var toX = edge - 3;
        // 行程与起手边距是**命名常量**：靶子几何推导（BuildTargetGeometry）要用它们算"最小工作面"，
        // 两处不能再各写一个字面量（0.10.7 修缺陷时就是这么漏掉同步的）。
        var fromY = (int)Math.Round(host.Bounds.Top + DragFromOffsetPx);
        var toY = (int)Math.Round(host.Bounds.Top + DragSpanPx);

        var injection = ProbeInjection(log, "[拖动]");
        if (!injection)
        {
            run.Inconclusive(log, "[拖动] 合成输入被系统丢弃（沙箱/低完整性），未验到输入层 —— 拖动吸附这一档不成立");
        }

        // 真实的"按住 → 逐步移动 → 松开"（为了在**按住不放**的中途截图，验证导轨真的画出来了）
        if (injection)
        {
            InputSimulator.MoveTo(fromX, fromY);
            UiPump.For(150);
            InputSimulator.LeftDown();
        }
        else
        {
            overlay.TestPressAt(fromX, fromY);
        }

        for (var step = 1; step <= 10; step++)
        {
            var x = fromX + ((toX - fromX) * step / 10);
            var y = fromY + ((toY - fromY) * step / 10);

            if (injection)
            {
                InputSimulator.MoveTo(x, y);
            }
            else
            {
                overlay.TestDragTo(x, y);
            }

            UiPump.For(60);
        }

        UiPump.For(200);

        var liveGuides = overlay.TestGuides;
        var liveLines = overlay.TestVisibleGuideCount;
        var liveOutline = overlay.TestSnapOutlineVisible;
        SelfTestEvidence.SaveWindowPng(overlay, Path.Combine(evidenceDir, "snap-drag-guides.png"), log);

        if (injection)
        {
            InputSimulator.LeftUp();
        }
        else
        {
            overlay.TestReleaseAt();
        }

        UiPump.For(400);

        var selection = overlay.SelectionBounds;
        var lastGuides = overlay.TestLastDragGuides;
        var snappedTo = false;
        foreach (var guide in lastGuides)
        {
            if (guide.Axis == SnapAxis.Vertical && Math.Abs(guide.Coordinate - edge) <= 1)
            {
                snappedTo = true;
            }
        }

        // 起手点在窗口内、终点在窗口左边缘外 3px：往左拖 → **左边**在动，右边是锚点。
        // 所以"吸上"的判据是左边精确落在窗口左边缘上，且右边（锚点）仍等于起手 x。
        var edgeOk = Math.Abs(selection.Left - edge) <= 1;
        var anchorKept = Math.Abs(selection.Right - fromX) <= 2;

        log.Info($"  [拖动] 从 ({fromX},{fromY}) 拖到 ({toX},{toY})：终点在窗口左边缘({edge}) 外 3px（容差 8px 内）");
        log.Info($"  [拖动] 拖拽中导轨=[{GuideSign(liveGuides)}] 可见导轨线={liveLines} 目标窗淡勾={liveOutline}");
        log.Info($"  [拖动] 松手后最后导轨=[{GuideSign(lastGuides)}] 选区={Sign(selection)} " +
                 $"左边={selection.Left:0} 边缘={edge} Δ={selection.Left - edge:0} 右边(锚点)={selection.Right:0}（起手={fromX}，Δ={selection.Right - fromX:0}）");

        var ok = edgeOk && snappedTo && anchorKept;
        log.Info($"  [拖动] → {(ok ? "OK" : "FAIL")}（左边吸到边缘={edgeOk} 导轨记录={snappedTo} 锚点边未动={anchorKept}；输入层={(injection ? "真实鼠标" : "未验到，本档用例不成立")}）");
        if (injection && !ok)
        {
            run.Fail(log, "[拖动] 真实鼠标已注入但左边没吸到边缘 / 导轨未记录 / 锚点边被改（见上方 [拖动] 明细）");
        }

        overlay.CloseOverlay();
        UiPump.For(200);
    }

    // ------------------------------------------------------------------ 工具

    // 0.10.7 起：这两个"日志写法"helper 过去住在纯逻辑断言那边
    // （tests/…/PureLogic/SnapGeometryChecks.cs），靠 <Compile Include> 链接进发布产物。
    // 断言表移出产物后，它们的**唯一用户**变成生产侧的自检探针（本文件与
    // SelfTestSnapOcclusion / SelfTestSnap 的证据日志），所以按职责搬到生产侧的
    // SnapGeometry（同文件、同命名空间，见 Overlay/SnapGeometry.cs 的 Sign/FormatGuides）——
    // 一处定义、两处消费（应用探针 + 测试侧断言表），日志文案逐字不变。
    private static string Sign(Rect rect) => SnapGeometry.Sign(rect);

    private static string GuideSign(IReadOnlyList<SnapGuide> guides) => SnapGeometry.FormatGuides(guides);

    private static string Delta(Rect a, Rect b)
        => $"左{a.Left - b.Left:+0;-0;0} 上{a.Top - b.Top:+0;-0;0} 右{a.Right - b.Right:+0;-0;0} 下{a.Bottom - b.Bottom:+0;-0;0}";

    private static string Offset(double value) => value.ToString("+0;-0;0");

    private static double MaxDelta(Rect a, Rect b)
        => new[] { Math.Abs(a.Left - b.Left), Math.Abs(a.Top - b.Top), Math.Abs(a.Right - b.Right), Math.Abs(a.Bottom - b.Bottom) }.Max();

    private static (int X, int Y) Center(Rect rect)
        => ((int)Math.Round(rect.X + (rect.Width / 2)), (int)Math.Round(rect.Y + (rect.Height / 2)));
}
