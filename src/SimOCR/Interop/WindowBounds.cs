using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using SimOCR.Diagnostics;
using SimOCR.Overlay;

namespace SimOCR.Interop;

/// <summary>一个可作为吸附候选的顶层窗口（**物理像素**，虚拟桌面坐标系）。</summary>
internal readonly record struct WindowCandidate(IntPtr Hwnd, string Title, string ClassName, uint ProcessId, Rect Bounds)
{
    public string Describe() => $"{Bounds.X:0},{Bounds.Y:0} {Bounds.Width:0}x{Bounds.Height:0} {ClassName} '{Title}'";
}

/// <summary>枚举过程的过滤账本：说明"为什么只剩这几个候选"（日志与验收报告都用它）。</summary>
internal sealed record WindowEnumerationStats(
    int Total,
    int OwnProcess,
    int Invisible,
    int Minimized,
    int Shell,
    int ToolWindow,
    int NoActivate,
    int Cloaked,
    int NoHostedApp,
    int ZeroArea,
    int OffScreen,
    int Occluded = 0)
{
    public string Describe() =>
        $"顶层 {Total} 个；排除 本进程 {OwnProcess} / 不可见 {Invisible} / 最小化 {Minimized} / 壳层 {Shell} / " +
        $"工具窗 {ToolWindow} / 免激活 {NoActivate} / cloaked {Cloaked} / UWP 无宿主 {NoHostedApp} / " +
        $"零面积 {ZeroArea} / 屏外 {OffScreen}；再丢掉整窗被遮盖 {Occluded} 个（剩下的边都真的看得见）";
}

internal sealed record WindowEnumerationResult(
    IReadOnlyList<WindowCandidate> Candidates,
    WindowEnumerationStats Stats,
    long ElapsedMs);

/// <summary>
/// 窗口边框与候选枚举（窗口吸附的 Win32 侧）。
///
/// 关键取舍（都在本机实测过，见验收报告）：
/// * 尺寸一律取 <c>DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)</c>：它排除 Win10/11 那圈
///   **不可见**的缩放边框，得到的才是用户眼睛看到的窗口边缘（实测最大化 Chrome：GetWindowRect
///   -8,-8,1936x1048 vs DWMWA 0,0,1920x1032；记事本 +5/-5/-5）。DWM 失败（例如子窗口会返回
///   E_INVALIDARG）时回退 <see cref="NativeMethods.GetWindowRect"/>。
/// * <c>EnumWindows</c> 是按 **z 序自上而下**枚举的，所以"光标下最上层的窗口"= 第一个命中的候选。
/// * 宿主进程过滤用 <c>GetWindowThreadProcessId</c>：覆盖层自己就是全屏置顶窗口，绝不能被吸附，
///   贴图窗 / 识别结果窗 / 设置窗同理（它们都属于本进程）。
/// * UWP/Store 应用：顶层是 <c>ApplicationFrameWindow</c>，它自己的矩形（含透明边框）不是内容矩形。
///   本机（Win11 26200）实测 Calculator 的宿主真窗口是它的子窗口 <c>Windows.UI.Core.CoreWindow</c>
///   （pid 属于应用进程），因此解析到这个子窗口并用它的 rect；解析不到（应用已挂起/纯壳层 frame）
///   就整个跳过，绝不返回一个错误的矩形。
/// </summary>
internal static class WindowBounds
{
    /// <summary>这些类名是桌面壳层自己的一部分（开始菜单/任务栏/输入法等），永远不是用户想截的"窗口"。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",                       // 顶层的这类窗口是开始菜单/搜索/ActionCenter 等壳层
        "ApplicationFrameTitleBarWindow",
        "ApplicationFrameInputSinkWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.Internal.Shell.TabProxyWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "ApplicationManager_DesktopShellWindow",
        "SystemTray_Main",
        "ThumbnailDeviceHelperWnd",
        "tooltips_class32",
        "IME",
        "MSCTFIME UI",
        "GDI+ Hook Window Class",
    };

    /// <summary>UWP 宿主 frame 的类名。</summary>
    private const string ApplicationFrameClass = "ApplicationFrameWindow";

    /// <summary>UWP 应用真正的内容窗口类名。</summary>
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// 取窗口边框（物理像素）。<paramref name="fromDwm"/> 说明用的是 DWM 还是 GetWindowRect（验收要看这个）。
    /// </summary>
    public static Rect GetBounds(IntPtr hwnd, out bool fromDwm)
    {
        fromDwm = false;

        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var dwm,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0 &&
            dwm.Width > 0 && dwm.Height > 0)
        {
            fromDwm = true;
            return ToRect(dwm);
        }

        return NativeMethods.GetWindowRect(hwnd, out var win) && win.Width > 0 && win.Height > 0
            ? ToRect(win)
            : Rect.Empty;
    }

    /// <summary>DWM 的 cloaked 标记（其它虚拟桌面 / 挂起的 UWP / 会话切换）。</summary>
    public static bool IsCloaked(IntPtr hwnd)
        => NativeMethods.DwmGetWindowAttributeInt(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
           && cloaked != 0;

    /// <summary>是否带某个扩展样式位（工具窗 / 免激活）。</summary>
    public static bool HasExStyle(IntPtr hwnd, long flag)
        => (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & flag) != 0;

    /// <summary>
    /// 枚举吸附候选（物理像素，**按 z 序自上而下**），并把**完全被更高 z 序窗口盖住**的窗口丢掉。
    /// 只走生产口径：过滤本进程窗口，且**总是**过滤工具窗/免激活窗
    /// （需要"不过滤本进程"的自检走 <see cref="EnumerateRaw"/>）。
    /// </summary>
    public static WindowEnumerationResult Enumerate()
    {
        var raw = EnumerateRaw();
        var (visible, occluded) = DropFullyOccluded(raw.Candidates);

        return raw with
        {
            Candidates = visible,
            Stats = raw.Stats with { Occluded = occluded },
        };
    }

    /// <summary>
    /// 不做遮盖过滤的原始枚举（按 z 序自上而下）。
    /// 自检用它来验证"已知几何的窗口能被读出来"，并自己计算可见性（见 <see cref="BuildVisibility"/>）。
    /// </summary>
    /// <param name="includeOwnProcess">
    /// true = 不过滤本进程窗口。**仅供自检**验证"已知几何的窗口能否被正确读出"。
    /// </param>
    public static WindowEnumerationResult EnumerateRaw(bool includeOwnProcess = false)
    {
        var watch = Stopwatch.StartNew();
        var candidates = new List<WindowCandidate>();
        var virtualScreen = MonitorLayout.GetVirtualScreen();
        var ownPid = (uint)Environment.ProcessId;

        var total = 0;
        var own = 0;
        var invisible = 0;
        var minimized = 0;
        var shell = 0;
        var tool = 0;
        var noActivate = 0;
        var cloaked = 0;
        var noHostedApp = 0;
        var zeroArea = 0;
        var offScreen = 0;

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            total++;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!includeOwnProcess && pid == ownPid)
            {
                own++;
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                invisible++;
                return true;
            }

            if (NativeMethods.IsIconic(hwnd))
            {
                minimized++;
                return true;
            }

            var className = GetClassName(hwnd);

            if (!string.Equals(className, ApplicationFrameClass, StringComparison.Ordinal) && ShellClasses.Contains(className))
            {
                shell++;
                return true;
            }

            // 工具窗（不进 Alt-Tab）与"永不获得焦点"的辅助层/输入陷阱都不该被吸附：
            // 后者最典型的坏例子是全屏的输入陷阱窗（Discord 的 DiscordDesktopOverlayInputTrap），
            // 留着它会把整个屏幕的悬停高亮全部吃掉。
            // 过去这里还有一个 includeToolWindows 开关，但全仓从不传 true（0.10.5 已删）。
            if (HasExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW) || HasExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE))
            {
                if (HasExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW))
                {
                    tool++;
                }
                else
                {
                    noActivate++;
                }

                return true;
            }

            if (IsCloaked(hwnd))
            {
                cloaked++;
                return true;
            }

            // UWP：ApplicationFrameWindow 的矩形是宿主 frame 的（不是内容），解析到真窗口再取尺寸
            var boundsHost = hwnd;
            var title = GetWindowTitle(hwnd);

            if (string.Equals(className, ApplicationFrameClass, StringComparison.Ordinal))
            {
                var hosted = FindHostedAppWindow(hwnd);
                if (hosted == IntPtr.Zero)
                {
                    noHostedApp++;
                    return true;
                }

                boundsHost = hosted;
                var hostedTitle = GetWindowTitle(hosted);
                if (!string.IsNullOrWhiteSpace(hostedTitle))
                {
                    title = hostedTitle;
                }
            }

            var bounds = GetBounds(boundsHost, out _);

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                zeroArea++;
                return true;
            }

            if (!bounds.IntersectsWith(virtualScreen))
            {
                offScreen++;
                return true;
            }

            candidates.Add(new WindowCandidate(hwnd, title, className, pid, bounds));
            return true;
        }, IntPtr.Zero);

        watch.Stop();

        return new WindowEnumerationResult(
            candidates,
            new WindowEnumerationStats(total, own, invisible, minimized, shell, tool, noActivate, cloaked, noHostedApp, zeroArea, offScreen),
            watch.ElapsedMilliseconds);
    }

    /// <summary>在候选列表里找"包含该点的最上层窗口"（列表本身就是 z 序）。</summary>
    public static WindowCandidate? TopmostAt(IReadOnlyList<WindowCandidate> candidates, int physicalX, int physicalY)
    {
        foreach (var candidate in candidates)
        {
            var b = candidate.Bounds;
            if (physicalX >= b.Left && physicalX < b.Right && physicalY >= b.Top && physicalY < b.Bottom)
            {
                return candidate;
            }
        }

        return null;
    }

    // ------------------------------------------------------ 可见性（z 序遮盖）

    /// <summary>
    /// "这条边露出来的那一段"。
    /// * <see cref="CoversAll"/>：整条边都可见（不做遮盖裁剪的目标，例如显示器/虚拟桌面）；
    /// * <see cref="Visible"/>：至少有一段可见（<see cref="Start"/>..<see cref="End"/> 是**并集**范围，
    ///   被"挖空"的中间段仍包含在内 —— 只用于几何探测，不用于画线）；
    /// * 两者都 false = 这条边整条被更高 z 序的窗口盖住。
    /// </summary>
    internal readonly record struct VisibleSpan(bool CoversAll, bool Visible, double Start, double End)
    {
        public static VisibleSpan None { get; } = new(false, false, double.NaN, double.NaN);
    }

    /// <summary>可见矩形片段（物理像素）。</summary>
    internal readonly record struct VisiblePiece(Rect Bounds);

    /// <summary>
    /// 计算某个候选窗口**露出来的**矩形片段：候选矩形依次减去排在它前面（z 序更高）的候选矩形。
    /// 这就是"用户眼睛看到的那部分窗口"。
    ///
    /// 代价：池化的小列表 + 矩形减法 O(n·k)，n=候选数(≈15)、k≤16，无需逐像素。
    /// </summary>
    internal static List<VisiblePiece> ComputeVisiblePieces(WindowCandidate candidate, IReadOnlyList<WindowCandidate> higherZ)
    {
        var pieces = new List<VisiblePiece> { new(candidate.Bounds) };

        foreach (var occluder in higherZ)
        {
            if (pieces.Count == 0)
            {
                break;
            }

            if (!occluder.Bounds.IntersectsWith(candidate.Bounds))
            {
                continue;
            }

            var c = occluder.Bounds;
            var next = new List<VisiblePiece>(pieces.Count + 4);

            foreach (var piece in pieces)
            {
                var p = piece.Bounds;
                var overlap = Rect.Intersect(p, c);

                if (overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0)
                {
                    next.Add(piece);
                    continue;
                }

                var exposed = Rect.Intersect(p, new Rect(p.Left, p.Top, p.Width, overlap.Top - p.Top));
                if (exposed.Width > 0 && exposed.Height > 0)
                {
                    next.Add(new VisiblePiece(exposed));
                }

                exposed = Rect.Intersect(p, new Rect(p.Left, overlap.Bottom, p.Width, p.Bottom - overlap.Bottom));
                if (exposed.Width > 0 && exposed.Height > 0)
                {
                    next.Add(new VisiblePiece(exposed));
                }

                exposed = Rect.Intersect(p, new Rect(p.Left, overlap.Top, overlap.Left - p.Left, overlap.Height));
                if (exposed.Width > 0 && exposed.Height > 0)
                {
                    next.Add(new VisiblePiece(exposed));
                }

                exposed = Rect.Intersect(p, new Rect(overlap.Right, overlap.Top, p.Right - overlap.Right, overlap.Height));
                if (exposed.Width > 0 && exposed.Height > 0)
                {
                    next.Add(new VisiblePiece(exposed));
                }
            }

            pieces = next;
        }

        return pieces;
    }

    /// <summary>把候选列表里**整窗都被更高 z 序窗口盖住**的丢掉（返回可见候选 + 被丢掉的数量）。</summary>
    public static (IReadOnlyList<WindowCandidate> Visible, int Occluded) DropFullyOccluded(IReadOnlyList<WindowCandidate> candidates)
    {
        var visible = new List<WindowCandidate>(candidates.Count);
        var occluded = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            // 列表本身就是 z 序：只可能被前面的盖住（本机实测：z 序 + 矩形 → 不可见的边）
            var higher = new List<WindowCandidate>(i);
            for (var j = 0; j < i; j++)
            {
                higher.Add(candidates[j]);
            }

            if (ComputeVisiblePieces(candidate, higher).Count == 0)
            {
                occluded++;
                continue;
            }

            visible.Add(candidate);
        }

        return (visible, occluded);
    }

    /// <summary>
    /// 某条边露出来的**并集**范围（只用于日志/报告；判断"能不能吸"用 <see cref="BuildVisibility"/>，
    /// 它要求落点落在**同一个**可见片段里，比并集更严格）。
    /// </summary>
    public static VisibleSpan VisibleSpanOf(WindowCandidate candidate, IReadOnlyList<WindowCandidate> higherZ, SnapAxis axis)
    {
        var pieces = ComputeVisiblePieces(candidate, higherZ);
        if (pieces.Count == 0)
        {
            return VisibleSpan.None;
        }

        var start = double.PositiveInfinity;
        var end = double.NegativeInfinity;

        foreach (var piece in pieces)
        {
            if (axis == SnapAxis.Vertical)
            {
                start = Math.Min(start, piece.Bounds.Top);
                end = Math.Max(end, piece.Bounds.Bottom);
            }
            else
            {
                start = Math.Min(start, piece.Bounds.Left);
                end = Math.Max(end, piece.Bounds.Right);
            }
        }

        var coversAll = Math.Abs(start - (axis == SnapAxis.Vertical ? candidate.Bounds.Top : candidate.Bounds.Left)) < 0.5
                        && Math.Abs(end - (axis == SnapAxis.Vertical ? candidate.Bounds.Bottom : candidate.Bounds.Right)) < 0.5;

        return new VisibleSpan(coversAll, true, start, end);
    }

    /// <summary>
    /// 从"原始枚举（带 hwnd 与 z 序）"造一个"这条边这一段露出来了吗"的判断函数。
    ///
    /// 映射方式：<c>targets</c> 的前 N 个与 <c>candidates</c> 一一对应（<see cref="BuildSnapTargets"/> 就是这么建的），
    /// 因此按顺序建索引，查不到的目标（显示器/虚拟桌面）一律视为**永远可见**。
    ///
    /// 规则（唯一规则）：候选边必须与探测段落在**同一个可见矩形片段**里
    /// —— 也就是"这一点真的露在屏幕上"，而不是"这个窗口的另一头露着"。
    /// </summary>
    public static SnapGeometry.VisibilityFn BuildVisibility(
        IReadOnlyList<WindowCandidate> candidates,
        IReadOnlyList<SnapTarget> targets,
        LogService? log = null)
    {
        var byKey = new Dictionary<(Rect Bounds, SnapTargetKind Kind), List<VisiblePiece>>();
        var fullyOccluded = 0;

        // 约定：targets 的前 N 个就是这 N 个候选窗口（由 BuildSnapTargets 统一构建）；
        // 其余（显示器/虚拟桌面）不进索引 → 一律"永远可见"。
        for (var i = 0; i < candidates.Count && i < targets.Count; i++)
        {
            var candidate = candidates[i];
            var higher = new List<WindowCandidate>(i);
            for (var j = 0; j < i; j++)
            {
                higher.Add(candidates[j]);
            }

            var pieces = ComputeVisiblePieces(candidate, higher);
            if (pieces.Count == 0)
            {
                fullyOccluded++;
            }

            byKey[(candidate.Bounds, targets[i].Kind)] = pieces;
        }

        log?.Debug($"窗口吸附：可见性索引 {byKey.Count} 个候选窗口，其中整窗被遮盖 {fullyOccluded} 个；" +
                   $"其余 {Math.Max(0, targets.Count - byKey.Count)} 个目标（显示器/虚拟桌面）永远可见");

        return (target, axis, edgeCoordinate, probeStart, probeEnd) =>
        {
            if (!byKey.TryGetValue((target.Bounds, target.Kind), out var pieces))
            {
                // 不在索引里（显示器/虚拟桌面这类）：永远可见，不做裁剪
                return true;
            }

            if (pieces.Count == 0)
            {
                return false;
            }

            double freeStart, freeEnd;
            if (axis == SnapAxis.Vertical)
            {
                freeStart = target.Bounds.Top;
                freeEnd = target.Bounds.Bottom;
            }
            else
            {
                freeStart = target.Bounds.Left;
                freeEnd = target.Bounds.Right;
            }

            var from = Math.Max(Math.Min(probeStart, probeEnd), freeStart);
            var to = Math.Min(Math.Max(probeStart, probeEnd), freeEnd);
            if (to < from)
            {
                return false;
            }

            foreach (var piece in pieces)
            {
                // 必须落在**同一个**可见片段里：自由轴有交集，且候选边本身也在这个片段内
                // （只比自由轴会把"同一个窗口另一头露出来的那条边"误判成可见）
                if (axis == SnapAxis.Vertical)
                {
                    if (edgeCoordinate < piece.Bounds.Left - 0.5 || edgeCoordinate > piece.Bounds.Right + 0.5)
                    {
                        continue;
                    }

                    if (Math.Max(from, piece.Bounds.Top) <= Math.Min(to, piece.Bounds.Bottom))
                    {
                        return true;
                    }
                }
                else
                {
                    if (edgeCoordinate < piece.Bounds.Top - 0.5 || edgeCoordinate > piece.Bounds.Bottom + 0.5)
                    {
                        continue;
                    }

                    if (Math.Max(from, piece.Bounds.Left) <= Math.Min(to, piece.Bounds.Right))
                    {
                        return true;
                    }
                }
            }

            return false;
        };
    }

    /// <summary>
    /// **唯一的吸附目标列表构建点**（生产 <see cref="OverlayWindow"/> 与自检 <c>SnapSelfTest</c> 共用）。
    ///
    /// 顺序 = 优先级：**窗口（原始 z 序，最上层在前）→ 显示器 → 虚拟桌面**。
    /// 必须收**未过滤**的原始候选（<see cref="EnumerateRaw"/> 的结果，而不是 <see cref="DropFullyOccluded"/> 的过滤结果）：
    /// <see cref="BuildVisibility"/> 按**下标**把 <c>candidates[i]</c> 与 <c>targets[i]</c> 配对，
    /// 用过滤结果建列表会让 occluded &gt; 0 时下标整体错位（0.10.0 修复）。
    /// 被整窗遮盖的窗口留在列表里没有副作用：它在 <see cref="BuildVisibility"/> 的索引里拿到空片段 → 永远不可见。
    /// </summary>
    public static List<SnapTarget> BuildSnapTargets(
        IReadOnlyList<WindowCandidate> rawCandidates,
        IReadOnlyList<Rect> monitorBounds,
        Rect virtualScreen)
    {
        var targets = new List<SnapTarget>(rawCandidates.Count + monitorBounds.Count + 1);

        foreach (var candidate in rawCandidates)
        {
            targets.Add(new SnapTarget(candidate.Bounds, SnapTargetKind.Window));
        }

        // 显示器边框也要进目标：双屏拼接处（两块屏的公共边）是真实存在的"窗口边界"，
        // 虚拟桌面外框本身覆盖不到它。
        foreach (var monitor in monitorBounds)
        {
            targets.Add(new SnapTarget(monitor, SnapTargetKind.Monitor));
        }

        targets.Add(new SnapTarget(virtualScreen, SnapTargetKind.VirtualScreen));
        return targets;
    }

    private static IntPtr FindHostedAppWindow(IntPtr frame)
    {
        var found = IntPtr.Zero;

        NativeMethods.EnumChildWindows(frame, (child, lParam) =>
        {
            if (found != IntPtr.Zero)
            {
                return false;
            }

            if (!NativeMethods.IsWindowVisible(child) ||
                !string.Equals(GetClassName(child), CoreWindowClass, StringComparison.Ordinal))
            {
                return true;
            }

            if (NativeMethods.GetWindowRect(child, out var rect) && rect.Width > 0 && rect.Height > 0)
            {
                found = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static Rect ToRect(NativeMethods.RECT rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
}
