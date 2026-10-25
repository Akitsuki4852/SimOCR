using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SimOCR.Interop;
using SimOCR.Overlay;

namespace SimOCR.Diagnostics;

/// <summary>
/// 遮盖裁剪缺陷复现：**被遮盖窗口的边缘不该被吸附**（"框选时会吸附被遮盖的窗口边缘"）。
///
/// <c>--snap-probe-occlusion[=a…|b…|ui|assert|screen…]</c>：
/// 1) 起两扇**另一个进程**的已知几何窗口：A（小，先起）+ B（大，后起，盖住 A 的全部四条边）；
///    **几何由真实虚拟屏幕推导**（B ≈ 70%×55% 居中留边距、A 严格内含于 B）——
///    0.10.1 修正：过去写死 A=(600,600,300,200) / B=(500,550,800,400)，
///    在小于约 1300×950 的桌面上 B 的右边/下边落在屏外（吸附结果被 Limits 夹回 → 导轨被丢），
///    于是"遮盖者可见边照常吸 4/4"在 1156×768 的机器上**必然假失败**（退出码 4）。
///    现在几何随屏幕走；仍然装不下的边**逐边跳过并在日志里写明**，绝不假失败。
/// 2) 用 <c>GetWindow(GW_HWNDPREV)</c> **实测** z 序（不假定 SetWindowPos 生效）；
/// 3) 打印候选枚举账本 + 每个候选的可见区间；
/// 4) 扫描式"拖动"：把移动边依次放到 A 的四条被遮盖边与 B 的四条可见边上，记录每一步的导轨（V/H:coord:kind）；
/// 5) <c>ui</c>：再走一遍真实覆盖层的拖拽路径（TestPressAt/TestDragTo/TestReleaseAt）；
/// 6) <c>assert</c>：把上面的观测量变成**可机判的闸门**（"真实窗口、occluded &gt; 0 端到端用例"）：
///    * 前置：A 真的被 B 整窗盖住、且真的被 <c>DropFullyOccluded</c> 丢掉（occluded &gt; 0）；
///    * **配对不变量**：<c>targets</c> 前 N 个必须与 <c>raw.Candidates</c> 逐项（Bounds + Kind）一致
///      —— 这正是那个错位（targets 由过滤后的可见列表构建）会破坏的东西；
///    * 行为：A 的四条边**都不吸**，B 的四条可见边**都照常吸**（只断言落在虚拟屏幕内的边，见下）。
///
/// 退出码（0.10.1 起是三态）：
///   0 = 通过；
///   2 = 运行异常 / 用法错误（宿主进程、抓屏等意外）；
///   3 = <c>ui</c> 模式抓屏失败；
///   4 = **断言失败**（真回归：targets/raw 配对错位、被遮盖边吸上了、遮盖者可见边不吸……）；
///   5 = **用例不成立 / 环境不支持**（虚拟屏幕小于最小可构造桌面 800×600、没有可用的交互桌面、
///       A 无法被 B 完全盖住、B 四条边一条都装不下……）—— **这不是缺陷**。
///       <c>build\gate-check.ps1</c> 把 5 当「跳过（不影响退出码）」、把 4 当失败：
///       于是小屏机器不再假失败，而真回归仍然失败。
///
/// **测试专用**：<c>screen=WxH</c> / <c>screen=x,y,w,h</c>（命令行）或环境变量
/// <c>SIMOCR_SNAP_PROBE_SCREEN</c> 可以**覆盖本探针看到的虚拟屏幕**（只影响本探针的几何推导与
/// 吸附 Limits；真实桌面不动），用来在大屏机器上模拟小屏机器。门禁与生产都**不设置**它；
/// 只给"大屏机器上验证小屏行为"这一件事用。
/// **0.10.7**：这套覆盖的唯一一份解析器搬到了 <see cref="SnapHostWindow.ResolveScreen"/>
/// （常量 <see cref="SnapHostWindow.ScreenOverrideEnv"/>）—— `--accept-snap` 的靶子几何也要读它，
/// 两条用例必须看到**同一个**屏幕。
/// </summary>
internal static class SnapOcclusionProbe
{
    /// <summary>最小可构造桌面：低于它 → 用例不成立（退出码 5），而不是假失败（退出码 4）。</summary>
    private const int MinScreenWidth = 800;
    private const int MinScreenHeight = 600;

    /// <summary>B（遮盖者）占虚拟屏幕的比例：留出边距 → 四条边都真的在屏内。</summary>
    private const double BWidthRatio = 0.70;
    private const double BHeightRatio = 0.55;

    /// <summary>A 相对 B 的尺寸与偏移 —— 保留 0.10.1 的场景语义（300/800、200/400、偏移 100/50）。</summary>
    private const double AWidthRatioOfB = 0.375;
    private const double AHeightRatioOfB = 0.50;
    private const double AOffsetRatioXOfB = 0.125;
    private const double AOffsetRatioYOfB = 0.125;

    /// <summary>
    /// A 必须这样深地内含于 B（像素）：比吸附容差（8px）大一倍，
    /// 否则"A 被遮盖的边"会和"B 自己可见的边"抢同一条导轨，场景不成立。
    /// </summary>
    private const double MinCoverMargin = 16;

    public static int Run(string arg, CaptureSessionController session, Settings.SettingsService settings, LogService log)
    {
        var (request, parseProblem) = Parse(arg);
        log.Info("=== 复现：被遮盖窗口的边缘是否被吸附===");

        if (parseProblem.Length > 0)
        {
            log.Error($"  [用法] {parseProblem}");
            return 2;
        }

        // 屏幕口径的唯一一份解析器在 SnapHostWindow（0.10.7：`--accept-snap` 的靶子几何也要用它，
        // 于是这里不再自带一份 "request.Screen ?? MonitorLayout.GetVirtualScreen()" 的重复实现）。
        // 命令行 `screen=` 优先（调试用），缺省时读**同一个**测试专用环境变量。
        var screenInfo = SnapHostWindow.ResolveScreen(log);
        var realScreen = screenInfo.RealScreen;
        var screen = request.Screen ?? screenInfo.Screen;
        var screenSource = request.Screen is not null
            ? "命令行 screen=（**测试专用**）"
            : screenInfo.Note;

        log.Info($"  虚拟屏幕 = {Sign(screen)}（来源：{screenSource}；真实虚拟屏幕 {Sign(realScreen)}）");
        if (request.Screen is not null && !realScreen.Contains(screen))
        {
            log.Warn($"  [覆盖] 测试用虚拟屏幕 {Sign(screen)} 不在真实虚拟屏幕 {Sign(realScreen)} 内：" +
                     "宿主窗口可能被摆到屏外（只在测试用覆盖时才会发生）");
        }

        var geometry = BuildGeometry(request, screen);
        if (geometry.Problem.Length > 0)
        {
            return Skip(log, geometry.Problem);
        }

        var rectA = geometry.A;
        var rectB = geometry.B;

        log.Info($"  A（被遮盖，先起）= {Sign(rectA)}   B（遮盖，后起）= {Sign(rectB)}   ui={request.Ui}   assert={request.Assert}");
        log.Info($"  几何推导：{geometry.Note}");

        // 覆盖层自己的日志会往同一个目录写，先确保临时目录存在（沙箱不许写 %TEMP%）
        Directory.CreateDirectory(Path.GetTempPath());

        var originalSnap = settings.Current.Behavior.SnapToWindows;
        var originalTolerance = settings.Current.Behavior.SnapTolerancePx;

        OverlayWindow? overlay = null;

        try
        {
            settings.Update(s =>
            {
                s.Behavior.SnapToWindows = true;
                s.Behavior.SnapTolerancePx = 8;
            }, notify: false);

            // 1) A 先起；2) B 后起 → 两扇都进置顶组，后起的 B 压住 A（实测，见下面的 z 序链）
            // 启动器与 --accept-snap 共用唯一一份（合并，见 SnapHostWindow）
            var a = SnapHostWindow.Launch(rectA, "A（被遮盖）", log, topmost: true);
            var b = SnapHostWindow.Launch(rectB, "B（遮盖者）", log, topmost: true);

            if (a.Hwnd == IntPtr.Zero || b.Hwnd == IntPtr.Zero)
            {
                var failed = a.Hwnd == IntPtr.Zero ? a : b;
                var label = a.Hwnd == IntPtr.Zero ? "A" : "B";
                if (!failed.ProcessExited && !failed.HostWindowMismatch)
                {
                    // 进程活着却建不出窗口（或压根起不来）：这台机器没有可用的交互桌面。
                    // 这种情况**不是**吸附缺陷 → 用例不成立（退出码 5），门禁按「跳过」处理。
                    return Skip(log, $"{label} 的宿主窗口没起来（{failed.Why}）—— 没有可用的交互桌面（headless / 会话未就绪）");
                }

                log.Error($"  [失败] {label} 的宿主窗口没起来（{failed.Why}）—— --snap-host-window 这条路径自己坏了");
                log.Error("  [结论] FAIL");
                return 4;
            }

            UiPump.For(600);
            ProbeZOrder(a, log, "A");
            ProbeZOrder(b, log, "B");

            // 实测几何（DWM 边框）为准：后续扫描一律用实测值，避免 DPI/系统摆位差异造成假失败
            var measuredA = WindowBounds.GetBounds(a.Hwnd, out var aFromDwm);
            var measuredB = WindowBounds.GetBounds(b.Hwnd, out var bFromDwm);

            if (measuredA.IsEmpty || measuredB.IsEmpty)
            {
                return Skip(log, $"宿主窗口的实测几何是空的（A={Sign(measuredA)} B={Sign(measuredB)}）—— 这台机器摆不出已知几何的窗口");
            }

            if (!SameRect(measuredA, rectA) || !SameRect(measuredB, rectB))
            {
                log.Warn($"  [几何] 实测与请求不一致：A 请求 {Sign(rectA)} → 实测 {Sign(measuredA)}（DWM={aFromDwm}）；" +
                         $"B 请求 {Sign(rectB)} → 实测 {Sign(measuredB)}（DWM={bFromDwm}）。后续断言一律用**实测值**。");
            }

            rectA = measuredA;
            rectB = measuredB;

            var cover = CoverProblem(rectA, rectB);
            if (cover.Length > 0)
            {
                return Skip(log, $"{cover}（A/B 为实测几何）");
            }

            // 扫描表：A 的四条被遮盖边 + B 的四条可见边。
            // **只断言落在虚拟屏幕内的边**：屏外的边（小屏机器上就可能出现）不参与断言、不计入分母，
            // 并在日志里写明"为什么没断言"——环境装不下的几何不该报 FAIL。
            var sweeps = BuildSweeps(rectA, rectB, screen);
            var bAsserted = sweeps.Where(s => s.ExpectSnap && s.Assertable).ToList();
            var aAsserted = sweeps.Where(s => !s.ExpectSnap && s.Assertable).ToList();

            // **仅记录（盘点项）**：这两行写的是"本次要断言哪些边"（分母声明），
            // 本身不是断言 —— 真正的判定在 6c 的 `edgeFailures` / `noSnapOk` / `snapOk`（计入 failures）。
            // 原措辞用 `[断言]` 前缀容易被读成"这里已经在断言"，故改成 `[记录] … 分母`。
            log.Info($"  [记录] 本次断言的分母：B（遮盖者）可见边 {bAsserted.Count}/4：{Describe(bAsserted)}");
            log.Info($"  [记录] 本次断言的分母：A（被遮盖）遮盖边 {aAsserted.Count}/4：{Describe(aAsserted)}");
            foreach (var skipped in sweeps.Where(s => !s.Assertable))
            {
                log.Warn($"  [跳过] {skipped.Which} {skipped.Axis}:{skipped.Coordinate:0} —— {skipped.SkipWhy}；" +
                         "**本次不断言**（不计入分母，也不会造成 FAIL）");
            }

            if (bAsserted.Count == 0)
            {
                return Skip(log, $"B（遮盖者）的四条边没有一条落在虚拟屏幕 {Sign(screen)} 内 —— 没有可断言的可见边，场景不成立");
            }

            // 3) 候选枚举：生产路径（Enumerate：已丢掉整窗被遮盖的） vs 原始（EnumerateRaw：z 序）
            var production = WindowBounds.Enumerate();
            log.Info($"  [候选] 生产路径 Enumerate() → {production.Candidates.Count} 个（枚举 {production.ElapsedMs}ms）— {production.Stats.Describe()}");

            var raw = WindowBounds.EnumerateRaw();

            // 反向路径（悬停高亮）：TopmostAt = z 序第一个包含该点的候选 → 不该是 A
            var hoverProbeX = (int)rectA.Left + 1;
            var hoverProbeY = (int)rectA.Top + 1;
            var topmost = WindowBounds.TopmostAt(production.Candidates, hoverProbeX, hoverProbeY);
            log.Info($"  [悬停] TopmostAt({hoverProbeX},{hoverProbeY})（在 A 里、但被 B 盖住）→ " +
                     $"{(topmost is { } t ? t.Describe() : "(无)")}；" +
                     (topmost?.Hwnd == b.Hwnd ? "→ 正确（B 更靠前）" : "→ 可疑！高亮到了被遮盖的窗口"));
            log.Info($"  [悬停] 把 A 也留在候选里（修复前的候选集）TopmostAt({hoverProbeX},{hoverProbeY}) → " +
                     $"{(WindowBounds.TopmostAt(raw.Candidates, hoverProbeX, hoverProbeY) is { } r ? r.Describe() : "(无)")}" +
                     "（z 序第一个命中的仍是 B，所以悬停路径本来就是对的）");

            log.Info($"  [候选] 原始 EnumerateRaw() → {raw.Candidates.Count} 个（z 序自上而下；A/B 会标出来）:");
            for (var i = 0; i < raw.Candidates.Count; i++)
            {
                var candidate = raw.Candidates[i];
                var tag = candidate.Hwnd == a.Hwnd ? "  ←A（本应被 B 完全盖住）" : candidate.Hwnd == b.Hwnd ? "  ←B（遮盖者）" : string.Empty;
                log.Info($"    {i + 1,2}. {candidate.Describe()} pid={candidate.ProcessId}{tag}");
            }

            var (visibleCandidates, droppedCount) = WindowBounds.DropFullyOccluded(raw.Candidates);
            log.Info($"  [候选] DropFullyOccluded → 可见 {visibleCandidates.Count} 个，丢掉 {droppedCount} 个");

            // 每个候选"露出来的"并集范围（只看 A/B，日志别太长）
            foreach (var candidate in raw.Candidates)
            {
                if (candidate.Hwnd != a.Hwnd && candidate.Hwnd != b.Hwnd)
                {
                    continue;
                }

                var index = raw.Candidates.ToList().FindIndex(c => c.Hwnd == candidate.Hwnd);
                var higher = raw.Candidates.Take(index).ToList();
                var ySpan = WindowBounds.VisibleSpanOf(candidate, higher, SnapAxis.Vertical);
                var xSpan = WindowBounds.VisibleSpanOf(candidate, higher, SnapAxis.Horizontal);

                log.Info($"    [可见] {(candidate.Hwnd == a.Hwnd ? "A" : "B")} {Sign(candidate.Bounds)} → " +
                         $"竖边可见段={(ySpan.CoversAll ? "全可见" : ySpan.Visible ? $"[{ySpan.Start:0},{ySpan.End:0}]" : "整条被遮盖")} " +
                         $"横边可见段={(xSpan.CoversAll ? "全可见" : xSpan.Visible ? $"[{xSpan.Start:0},{xSpan.End:0}]" : "整条被遮盖")}");
            }

            // 吸附目标 = **原始候选**（未过滤）+ 虚拟桌面，构建点与 OverlayWindow 共用
            // WindowBounds.BuildSnapTargets（0.10.0 起二者是同一个方法）。
            // 关键：BuildVisibility 按**下标**配对 candidates[i] 与 targets[i]，
            // 用 visibleCandidates 建 targets 会让 occluded > 0 时整体错位（复现写法）。
            var targets = WindowBounds.BuildSnapTargets(raw.Candidates, [], screen);
            var visibility = WindowBounds.BuildVisibility(raw.Candidates, targets, log);

            // 老行为（修复前）：全部候选都进目标列表、且不做可见性判断 —— 只用于对照
            var legacyTargets = new List<SnapTarget>(raw.Candidates.Count + 1);
            foreach (var candidate in raw.Candidates)
            {
                legacyTargets.Add(new SnapTarget(candidate.Bounds, SnapTargetKind.Window));
            }

            legacyTargets.Add(new SnapTarget(screen, SnapTargetKind.VirtualScreen));

            // 4) 扫描：把"移动边"放到这些坐标上，看导轨。
            //    A 的四条边（整条被 B 盖住）→ 修复后必须**不吸**；B 的四条边（可见）→ 必须照常吸。
            log.Info("  [扫描] 列 1 = 修复前（所有候选都当可见、不做可见性判断）；列 2 = 修复后（生产路径）");
            var edgeFailures = new List<string>();
            var noSnapOk = 0;
            var snapOk = 0;
            foreach (var sweep in sweeps)
            {
                var legacy = GuidesSign(GuidesAt(sweep.Probe, sweep.Edges, legacyTargets, screen, null));
                var current = GuidesSign(GuidesAt(sweep.Probe, sweep.Edges, targets, screen, visibility));
                var axis = sweep.Axis;
                var changed = string.Equals(legacy, current, StringComparison.Ordinal) ? string.Empty : "   ← 变了";
                var snapped = current.Length > 0;

                if (!sweep.Assertable)
                {
                    // 屏外的边：只打印观测量，不参与断言（见上面的 [跳过] 行）
                    log.Info($"    {sweep.EdgeLabel}={axis}:{sweep.Coordinate:0}  修复前=[{legacy}]  修复后=[{current}]{changed}" +
                             $"   {sweep.Which}（{ExpectText(sweep.ExpectSnap)}）→ 本次不断言：{sweep.SkipWhy}");
                    continue;
                }

                log.Info($"    {sweep.EdgeLabel}={axis}:{sweep.Coordinate:0}  修复前=[{legacy}]  修复后=[{current}]{changed}" +
                         $"   {sweep.Which}（{ExpectText(sweep.ExpectSnap)}）");

                if (snapped == sweep.ExpectSnap)
                {
                    if (sweep.ExpectSnap)
                    {
                        snapOk++;
                    }
                    else
                    {
                        noSnapOk++;
                    }
                }
                else
                {
                    edgeFailures.Add($"{sweep.Which} = {axis}:{sweep.Coordinate:0}：" +
                                     (sweep.ExpectSnap
                                         ? $"应吸附却没有吸附（修复前=[{legacy}]）"
                                         : $"被遮盖却吸到了 [{current}]（修复前=[{legacy}]）"));
                }
            }

            // 5) ui=true：走真实的覆盖层拖拽路径（TestPressAt/TestDragTo/TestReleaseAt → LogGuides）
            if (request.Ui && session is not null)
            {
                var frame = session.CaptureScreenOnly();
                if (frame is null)
                {
                    log.Error("  [UI] 抓屏失败");
                    return 3;
                }

                overlay = session.CreateOverlay(frame);
                overlay.Show();
                UiPump.For(500);
                log.Info($"  [UI] 覆盖层已显示：候选窗口={overlay.TestCandidateCount} 容差={overlay.TestSnapTolerance}px");

                // 起手点/落点同样由几何推导（不再写死 700/900/1035 这些坐标）
                var aMidY = (int)Math.Round(rectA.Top + (rectA.Height / 2));
                var aThirdX = (int)Math.Round(rectA.Left + (rectA.Width / 3));
                var aTwoThirdX = (int)Math.Round(rectA.Left + (rectA.Width * 2 / 3));
                var gapY = (int)Math.Round(rectB.Bottom + Math.Max(20, (screen.Bottom - rectB.Bottom) / 2));

                // A 右边整条被遮盖：起手点在框内，往右拖到 A 的右边
                UiDrag(overlay, log, $"右边缘→A 被遮盖的右边 {rectA.Right:0}",
                    startX: aThirdX, startY: aMidY, endX: (int)Math.Round(rectA.Right), endY: aMidY);

                // A 上边整条被遮盖：往左上拖
                UiDrag(overlay, log, $"上边缘→A 被遮盖的上边 {rectA.Top:0}",
                    startX: aTwoThirdX,
                    startY: (int)Math.Round(Math.Min(rectB.Bottom - 10, rectA.Bottom + (rectA.Height / 2))),
                    endX: aTwoThirdX, endY: (int)Math.Round(rectA.Top));

                // B 左边可见：必须照常吸附
                UiDrag(overlay, log, $"左边缘→B 可见的左边 {rectB.Left:0}（回归）",
                    startX: (int)Math.Round(rectB.Left + Math.Max(20, rectB.Width * 0.075)), startY: aMidY,
                    endX: (int)Math.Round(rectB.Left), endY: aMidY);

                // 缝隙里起手（B 之外）：往左拖到 A 的左边 +3（A 左边在容差内但不可见，B 左边远在容差外）
                // → 修复前会吸到 A 的左边，修复后不该吸附
                UiDrag(overlay, log, $"缝隙里往左拖到 A 被遮盖的左边附近 {rectA.Left + 3:0}",
                    startX: aThirdX, startY: gapY, endX: (int)Math.Round(rectA.Left + 3), endY: gapY);

                overlay.CloseOverlay();
                UiPump.For(200);
            }

            if (!request.Assert)
            {
                return 0;
            }

            // ------------------------------------------------------------------
            // 6) --snap-probe-occlusion=assert：把上面的观测量变成**可机判的闸门**
            //    （"真实窗口、occluded > 0 的端到端用例"任何人都能跑一条命令，
            //      任何把 targets 换回 DropFullyOccluded 过滤结果 —— 原来的写法 ——
            //      的回归都会让它非 0。）
            // ------------------------------------------------------------------
            var failures = new List<string>(edgeFailures);

            // 6a) 端到端 occluded > 0：A 必须真的被 B 整窗盖住，并且真的被 DropFullyOccluded 丢掉
            var aDropped = !visibleCandidates.Any(c => c.Hwnd == a.Hwnd);
            var bVisible = visibleCandidates.Any(c => c.Hwnd == b.Hwnd);
            log.Info($"  [断言] occluded>0：原始 {raw.Candidates.Count} → 可见 {visibleCandidates.Count}，丢掉 {droppedCount}；" +
                     $"A 在丢弃名单里={aDropped}；B 仍在可见名单里={bVisible}");

            if (droppedCount <= 0)
            {
                failures.Add("整窗被遮盖的候选数 = 0：这台机器/这次运行没有造出 occluded > 0 的场景（几何已由屏幕推导且 A 严格内含于 B → 这属于代码回归）");
            }

            if (!aDropped)
            {
                failures.Add("A（应被 B 完全盖住的小窗）没有被 DropFullyOccluded 丢掉 —— z 序或几何没复现出来（B 没压住 A？）");
            }

            if (!bVisible)
            {
                // **B 被第三个窗口整窗盖住**不是吸附缺陷，而是"本用例的场景不成立"——
                // 与"没有可用的交互桌面 / 虚拟屏幕太小 / A 装不进 B"同类，必须记 `[用例不成立]` → 退出码 **5**
                // （gate-check 按「跳过」记账）。旧行为把它 `failures.Add` → 与真回归一起 `return 4`，
                // 于是虚拟机/被别人窗口压住的机器上会把**环境**读成**产品缺陷**。
                //
                // **判据不许放松**：真失败仍必须 4。这里把两种情形分开：
                //   * 枚举里**确实有**别的窗口压在 B 身上（`TopmostAt(B 中心)` 不是 B 也不是 A）→ 环境 → 5；
                //   * 没有任何窗口压在 B 身上，而 `DropFullyOccluded` 却把 B 丢了 → 那是
                //     "z 序 / 遮盖裁剪"这条链自己坏了（真回归）→ 仍进 failures → 4。
                var bInRaw = raw.Candidates.Any(c => c.Hwnd == b.Hwnd);
                var coverer = WindowBounds.TopmostAt(
                    raw.Candidates,
                    (int)Math.Round(rectB.Left + (rectB.Width / 2)),
                    (int)Math.Round(rectB.Top + (rectB.Height / 2)));

                if (!bInRaw)
                {
                    return Skip(log, "B（遮盖者）自己没进原始候选枚举（被最小化 / 被 DWM 遮罩 / 几何为空）—— 场景不成立");
                }

                if (coverer is { } described && described.Hwnd != b.Hwnd && described.Hwnd != a.Hwnd)
                {
                    return Skip(log, $"B（遮盖者）被**第三个窗口**整窗盖住（最上层是 {described.Describe()}）—— " +
                                     "本用例要造的\"A 被 B 盖住\"场景不成立（不是吸附缺陷）");
                }

                failures.Add("B（遮盖者）不在可见候选里，而枚举里**没有任何别的窗口**压在它身上 —— " +
                             "DropFullyOccluded / z 序这条链自己坏了（真回归）");
            }

            // 6b) 配对不变量：targets 的前 N 个必须与 raw.Candidates 一一对应（同 Bounds、同 Window kind）。
            //     BuildVisibility 就是**按下标**把 candidates[i] 与 targets[i] 配对的；targets 一旦由
            //     过滤后的可见列表构建，下标立刻整体错位 —— 逐项比对必然抓到。
            var paired = 0;
            var pairingProblem = string.Empty;
            for (var i = 0; i < raw.Candidates.Count; i++)
            {
                if (i >= targets.Count)
                {
                    pairingProblem = $"targets 只有 {targets.Count} 个，第 {i + 1} 个原始候选没有对应目标（下标被截断）";
                    break;
                }

                if (targets[i].Kind != SnapTargetKind.Window || targets[i].Bounds != raw.Candidates[i].Bounds)
                {
                    pairingProblem = $"targets[{i}] = ({Sign(targets[i].Bounds)}, {targets[i].Kind}) 与 " +
                                     $"raw.Candidates[{i}] = ({Sign(raw.Candidates[i].Bounds)}, Window) 不是同一扇窗" +
                                     " —— targets 与 raw.Candidates 的下标错位";
                    break;
                }

                paired++;
            }

            log.Info($"  [断言] 配对不变量：targets 前 {paired}/{raw.Candidates.Count} 个与 raw.Candidates 逐项（Bounds+Kind）一致");
            if (pairingProblem.Length > 0)
            {
                failures.Add(pairingProblem);
            }

            // 6c) 行为：A 被遮盖的边都不吸（noSnapOk/aAsserted），B 可见的边都吸（snapOk/bAsserted）。
            //     分母是**本次真的断言了**的边数（屏外的边在上面就跳过了，不算失败也不算进分母）。
            log.Info($"  [断言] 被遮盖边不吸 {noSnapOk}/{aAsserted.Count}；可见边照常吸 {snapOk}/{bAsserted.Count}" +
                     $"（B 四条边本次断言 {bAsserted.Count}/4）");
            foreach (var edgeFailure in edgeFailures)
            {
                log.Info($"    [失败] {edgeFailure}");
            }

            if (failures.Count > 0)
            {
                log.Error($"  [失败] --snap-probe-occlusion=assert 未通过（{failures.Count} 项）：");
                foreach (var failure in failures)
                {
                    log.Error($"    [失败] {failure}");
                }

                log.Error("  [结论] FAIL");
                return 4;
            }

            log.Info($"  [结论] OK  occluded={droppedCount}  配对={paired}/{raw.Candidates.Count}  " +
                     $"被遮盖边={noSnapOk}/{aAsserted.Count} 不吸  可见边={snapOk}/{bAsserted.Count} 吸  " +
                     $"B 边断言={bAsserted.Count}/4");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("复现异常", ex);
            return 2;
        }
        finally
        {
            settings.Update(s =>
            {
                s.Behavior.SnapToWindows = originalSnap;
                s.Behavior.SnapTolerancePx = originalTolerance;
            }, notify: false);

            SnapHostWindow.KillAll(log);
        }
    }

    // ------------------------------------------------------------------ 用例不成立（退出码 5）
    //   与"断言失败（退出码 4）"严格区分：环境装不下这个场景时**不是缺陷**。
    //   build\gate-check.ps1 把 5 当「跳过」，所以小屏机器/无桌面机器不会假失败。

    private static int Skip(LogService log, string why)
    {
        log.Warn($"  [用例不成立] {why}");
        log.Warn("  [用例不成立] 环境装不下这个场景 → 跳过（退出码 5 = 用例不成立/环境不支持，**不是**断言失败）");
        log.Warn("  [结论] 用例不成立（skip，退出码 5）：build\\gate-check.ps1 按「跳过」处理，不影响退出码");
        return 5;
    }

    // ------------------------------------------------------------------ 几何

    private sealed record ProbeRequest(Rect? A, Rect? B, bool Ui, bool Assert, Rect? Screen, bool ScreenFromEnv);

    private sealed record Sweep(
        string EdgeLabel,
        SnapEdgeSet Edges,
        bool Vertical,
        double Coordinate,
        Rect Probe,
        bool ExpectSnap,
        string Which,
        string SkipWhy)
    {
        /// <summary>本次是否断言这条边（false = 该边/探测段不在虚拟屏幕内，环境装不下）。</summary>
        public bool Assertable => SkipWhy.Length == 0;

        public string Axis => Vertical ? "V" : "H";
    }

    private static (Rect A, Rect B, string Note, string Problem) BuildGeometry(ProbeRequest request, Rect screen)
    {
        var screenTooSmall = screen.IsEmpty || screen.Width < MinScreenWidth || screen.Height < MinScreenHeight;

        Rect b;
        string bNote;
        if (request.B is { } explicitB)
        {
            b = explicitB;
            bNote = $"B 由命令行 b= 指定 {Sign(b)}";
        }
        else if (screenTooSmall)
        {
            return (Rect.Empty, Rect.Empty, string.Empty, SmallScreenProblem(screen));
        }
        else
        {
            var w = Math.Round(screen.Width * BWidthRatio);
            var h = Math.Round(screen.Height * BHeightRatio);
            b = new Rect(
                Math.Round(screen.Left + ((screen.Width - w) / 2)),
                Math.Round(screen.Top + ((screen.Height - h) / 2)),
                w,
                h);
            bNote = $"B = 虚拟屏幕的 {BWidthRatio:P0}×{BHeightRatio:P0}（居中留边距）{Sign(b)}";
        }

        if (b.Width < 1 || b.Height < 1)
        {
            return (Rect.Empty, Rect.Empty, string.Empty, $"B 的几何无效：{Sign(b)}");
        }

        Rect a;
        string aNote;
        if (request.A is { } explicitA)
        {
            a = explicitA;
            aNote = $"A 由命令行 a= 指定 {Sign(a)}";
        }
        else
        {
            a = new Rect(
                Math.Round(b.Left + (b.Width * AOffsetRatioXOfB)),
                Math.Round(b.Top + (b.Height * AOffsetRatioYOfB)),
                Math.Round(b.Width * AWidthRatioOfB),
                Math.Round(b.Height * AHeightRatioOfB));
            aNote = $"A = B 的内含矩形（严格被 B 盖住）{Sign(a)}";
        }

        var note = $"{bNote}；{aNote}；边距 左{a.Left - b.Left:0} 上{a.Top - b.Top:0} " +
                   $"右{b.Right - a.Right:0} 下{b.Bottom - a.Bottom:0}（要求 ≥{MinCoverMargin:0}px）";
        return (a, b, note, CoverProblem(a, b));
    }

    private static string SmallScreenProblem(Rect screen)
        => $"虚拟屏幕 {Sign(screen)} 小于本用例的最小可构造桌面 {MinScreenWidth}×{MinScreenHeight}" +
           $"（B 要占 {BWidthRatio:P0}×{BHeightRatio:P0} 并留边距、A 要严格内含于 B）：环境装不下这个场景";

    /// <summary>
    /// 场景前提：A 必须**严格内含**于 B（四条边距 ≥ <see cref="MinCoverMargin"/>px），
    /// 否则 A 的边不是"整条被 B 盖住"（边距太小时 A 的边会和 B 的可见边抢同一条导轨）。
    /// 返回空串 = 成立。
    /// </summary>
    private static string CoverProblem(Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty || a.Width < 1 || a.Height < 1 || b.Width < 1 || b.Height < 1)
        {
            return $"几何无效：A={Sign(a)} B={Sign(b)}";
        }

        if (!b.Contains(a))
        {
            return $"A {Sign(a)} 没有完全落在 B {Sign(b)} 里 —— 「被整窗遮盖」这个场景不成立";
        }

        var min = new[] { a.Left - b.Left, a.Top - b.Top, b.Right - a.Right, b.Bottom - a.Bottom }.Min();
        if (min < MinCoverMargin)
        {
            return $"A {Sign(a)} 与 B {Sign(b)} 的边距太小（最小 {min:0}px < {MinCoverMargin:0}px）：" +
                   "被遮盖的边会与 B 自己的可见边抢同一条导轨（容差 8px），场景不成立";
        }

        return string.Empty;
    }

    /// <summary>
    /// 扫描表：A 的四条被遮盖边（期望**不吸**）+ B 的四条可见边（期望**吸**）。
    /// 每条边都带一个探测矩形（移动边正好落在被探的边上），并判断它是否**完全落在虚拟屏幕内**：
    /// 不在屏内就不该断言（SnapEdges 会把结果夹回 Limits 并丢掉导轨 → 那会变成假失败）。
    /// </summary>
    private static List<Sweep> BuildSweeps(Rect a, Rect b, Rect screen)
    {
        var list = new List<Sweep>(8);

        // 探测矩形的两条尺寸：长边（从被探的边朝目标内部延伸）与自由轴上的探测段长度
        var extendV = Math.Clamp(Math.Round(a.Width * 0.5), 24.0, 150.0);
        var extendH = Math.Clamp(Math.Round(a.Height * 0.5), 20.0, 120.0);

        // A：四条边都严格内含于 B → 探测段取 A 的"中间 50%"，保证整段都落在 B 里（整条被遮盖）
        var aY0 = a.Top + (a.Height * 0.25);
        var aY1 = a.Bottom - (a.Height * 0.25);
        var aX0 = a.Left + (a.Width * 0.25);
        var aX1 = a.Right - (a.Width * 0.25);

        // B：四条边自己可见 → 探测段取 B 的中间带（不碰别的边）
        var bY0 = b.Top + (b.Height * 0.35);
        var bY1 = b.Top + (b.Height * 0.65);
        var bX0 = b.Left + (b.Width * 0.25);
        var bX1 = b.Left + (b.Width * 0.45);

        Add("右边缘", SnapEdgeSet.Right, true, a.Right, false, "A 被遮盖的右边", aY0, aY1);
        Add("右边缘", SnapEdgeSet.Right, true, b.Right, true, "B 可见的右边", bY0, bY1);
        Add("下边缘", SnapEdgeSet.Bottom, false, a.Bottom, false, "A 被遮盖的下边", aX0, aX1);
        Add("下边缘", SnapEdgeSet.Bottom, false, b.Bottom, true, "B 可见的下边", bX0, bX1);
        Add("左边缘", SnapEdgeSet.Left, true, a.Left, false, "A 被遮盖的左边", aY0, aY1);
        Add("左边缘", SnapEdgeSet.Left, true, b.Left, true, "B 可见的左边", bY0, bY1);
        Add("上边缘", SnapEdgeSet.Top, false, a.Top, false, "A 被遮盖的上边", aX0, aX1);
        Add("上边缘", SnapEdgeSet.Top, false, b.Top, true, "B 可见的上边", bX0, bX1);

        return list;

        void Add(
            string label,
            SnapEdgeSet edges,
            bool vertical,
            double coordinate,
            bool expectSnap,
            string which,
            double freeStart,
            double freeEnd)
        {
            // 探测矩形 = "移动边正好落在 coordinate 上"的选区。先把自由轴夹进虚拟屏幕，再从被探的边
            // 朝**内**延伸出长边 —— 这样探测矩形自己也在屏内（不需要靠"夹紧后丢掉导轨"来兜底，
            // 那正是小屏机器上假失败的成因）。
            double probeStart, probeEnd, extendStart, extendEnd;
            if (vertical)
            {
                probeStart = Math.Max(freeStart, screen.Top);
                probeEnd = Math.Min(freeEnd, screen.Bottom);
                if (edges == SnapEdgeSet.Left)
                {
                    extendStart = coordinate;
                    extendEnd = Math.Min(coordinate + extendV, screen.Right);
                }
                else
                {
                    extendStart = Math.Max(coordinate - extendV, screen.Left);
                    extendEnd = coordinate;
                }
            }
            else
            {
                probeStart = Math.Max(freeStart, screen.Left);
                probeEnd = Math.Min(freeEnd, screen.Right);
                if (edges == SnapEdgeSet.Top)
                {
                    extendStart = coordinate;
                    extendEnd = Math.Min(coordinate + extendH, screen.Bottom);
                }
                else
                {
                    extendStart = Math.Max(coordinate - extendH, screen.Top);
                    extendEnd = coordinate;
                }
            }

            var probe = vertical
                ? new Rect(extendStart, probeStart, extendEnd - extendStart, probeEnd - probeStart)
                : new Rect(probeStart, extendStart, probeEnd - probeStart, extendEnd - extendStart);

            // 可断言的条件（精确）：① **被探的那条边自己**在虚拟屏幕内；② 夹紧后的探测矩形仍然有面积。
            // 边在屏外 → 吸附结果会被 Limits 夹回、导轨被丢（"应吸附"必然假失败）→ 跳过并写明理由。
            var why = string.Empty;
            var edgeOnScreen = vertical
                ? coordinate >= screen.Left && coordinate <= screen.Right
                : coordinate >= screen.Top && coordinate <= screen.Bottom;

            if (!edgeOnScreen)
            {
                why = OffScreenWhy(vertical, coordinate, screen);
            }
            else if (probe.Width < 1 || probe.Height < 1 || !screen.Contains(probe))
            {
                why = $"探测段被虚拟屏幕 {Sign(screen)} 夹光（{Sign(probe)}）";
            }

            list.Add(new Sweep(label, edges, vertical, coordinate, probe, expectSnap, which, why));
        }
    }

    /// <summary>"这条边为什么不断言"的具体理由（屏幕的哪条边界之外）。</summary>
    private static string OffScreenWhy(bool vertical, double coordinate, Rect screen)
        => vertical
            ? coordinate > screen.Right
                ? $"该边在虚拟屏幕右边界之外（x={coordinate:0} > {screen.Right:0}）"
                : $"该边在虚拟屏幕左边界之外（x={coordinate:0} < {screen.Left:0}）"
            : coordinate > screen.Bottom
                ? $"该边在虚拟屏幕下边界之外（y={coordinate:0} > {screen.Bottom:0}）"
                : $"该边在虚拟屏幕上边界之外（y={coordinate:0} < {screen.Top:0}）";

    private static string ExpectText(bool expectSnap) => expectSnap ? "应吸附" : "不该吸附";

    private static string EdgeName(SnapEdgeSet edge) => edge switch
    {
        SnapEdgeSet.Left => "左",
        SnapEdgeSet.Top => "上",
        SnapEdgeSet.Right => "右",
        _ => "下",
    };

    private static string Describe(List<Sweep> sweeps)
        => sweeps.Count == 0
            ? "(无)"
            : string.Join("  ", sweeps.Select(s => $"{EdgeName(s.Edges)}{s.Axis}:{s.Coordinate:0}"));

    private static bool SameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
           && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    // ------------------------------------------------------------------ 命令行

    /// <summary>
    /// 解析 <c>--snap-probe-occlusion[=…]</c>。规格用 <c>|</c> 分隔：
    /// <c>ui</c> / <c>assert</c> / <c>a=x,y,w,h</c> / <c>b=x,y,w,h</c> /
    /// <c>screen=WxH</c>（或 <c>screen=x,y,w,h</c>，**测试专用**虚拟屏幕覆盖）。
    /// 环境变量 <see cref="SnapHostWindow.ScreenOverrideEnv"/> 是同一个覆盖的另一种给法（命令行优先）。
    /// </summary>
    private static (ProbeRequest Request, string Problem) Parse(string arg)
    {
        Rect? a = null;
        Rect? b = null;
        Rect? argScreen = null;
        var ui = false;
        var assert = false;
        var problem = string.Empty;

        var parts = arg.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts is { Length: 2 })
        {
            foreach (var spec in parts[1].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (spec.Equals("ui", StringComparison.OrdinalIgnoreCase))
                {
                    ui = true;
                    continue;
                }

                // assert：把观测量变成闸门（非 0 退出）；可与 ui 组合
                if (spec.Equals("assert", StringComparison.OrdinalIgnoreCase))
                {
                    assert = true;
                    continue;
                }

                // **测试专用**：覆盖本探针看到的虚拟屏幕（只影响几何推导与 Limits，真实桌面不动）
                if (spec.StartsWith("screen=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = spec["screen=".Length..];
                    if (!SnapHostWindow.TryParseScreen(raw, out var parsed))
                    {
                        problem = $"screen= 的值看不懂：{raw}（要 WxH 或 x,y,w,h）";
                        break;
                    }

                    argScreen = parsed;
                    continue;
                }

                // 显式几何（调试用）：a=x,y,w,h / b=x,y,w,h。
                // 0.10.1 顺手修掉一个老 bug：以前直接拿整段 spec 去 int.TryParse，于是 "b=500" 解析失败、
                // 显式几何被**静默忽略**（这里必须先把 "a="/"b=" 前缀摘掉）。
                var isA = spec.StartsWith("a=", StringComparison.OrdinalIgnoreCase);
                var isB = spec.StartsWith("b=", StringComparison.OrdinalIgnoreCase);
                if (isA || isB)
                {
                    var raw = spec[2..];
                    var numbers = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (numbers.Length >= 4 &&
                        int.TryParse(numbers[0], out var x) && int.TryParse(numbers[1], out var y) &&
                        int.TryParse(numbers[2], out var w) && int.TryParse(numbers[3], out var h))
                    {
                        if (isA)
                        {
                            a = new Rect(x, y, w, h);
                        }
                        else
                        {
                            b = new Rect(x, y, w, h);
                        }
                    }
                    else
                    {
                        problem = $"{(isA ? "a=" : "b=")} 的值看不懂：{raw}（要 x,y,w,h）";
                        break;
                    }
                }
            }
        }

        // **测试专用**环境变量：解析失败时**不报用法错误**（与 0.10.1 起的既有口径一致：
        // 环境变量坏了只忽略并告警，由 SnapHostWindow.ResolveScreen 打出那一行）。
        var envRaw = Environment.GetEnvironmentVariable(SnapHostWindow.ScreenOverrideEnv);
        Rect? envScreen = null;
        if (problem.Length == 0 && !string.IsNullOrWhiteSpace(envRaw) &&
            SnapHostWindow.TryParseScreen(envRaw, out var parsedEnv))
        {
            envScreen = parsedEnv;
        }

        var screen = argScreen ?? envScreen;
        return (new ProbeRequest(a, b, ui, assert, screen, argScreen is null && envScreen is not null), problem);
    }

    // ------------------------------------------------------------------ 宿主窗口
    //   启动 / 收尾的唯一一份实现搬到 SnapHostWindow（与 --accept-snap 合并，
    //   0.10.4 裁决把这次合并推迟到本版）。这里只剩"用它"。

    /// <summary>实测 z 序：从自己开始沿 GW_HWNDPREV 往上走，打印可见的顶层窗口。</summary>
    private static void ProbeZOrder(SnapHostWindow.HostStatus host, LogService log, string label)
    {
        var chain = new List<string>();
        var current = host.Hwnd;

        for (var step = 0; step < 12 && current != IntPtr.Zero; step++)
        {
            if (NativeMethods.IsWindowVisible(current))
            {
                var title = new System.Text.StringBuilder(128);
                NativeMethods.GetWindowTextW(current, title, title.Capacity);
                NativeMethods.GetWindowThreadProcessId(current, out var pid);
                var rect = WindowBounds.GetBounds(current, out _);
                chain.Add($"{Sign(rect)} pid={pid} '{title}'");
            }

            current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDPREV);
        }

        log.Info($"  [z序] {label} hwnd=0x{host.Hwnd.ToInt64():X} 之上的可见窗口（自上而下）:");
        for (var i = 0; i < chain.Count; i++)
        {
            log.Info($"    {i + 1}. {chain[i]}");
        }

        if (chain.Count == 0)
        {
            log.Info("    (链上无其它可见窗口 → 它是最上层)");
        }
    }

    private static IReadOnlyList<SnapGuide> GuidesAt(
        Rect probe,
        SnapEdgeSet edges,
        IReadOnlyList<SnapTarget> targets,
        Rect screen,
        SnapGeometry.VisibilityFn? visibility)
        // 探测矩形就是"移动边正好落在这里"的选区（由 BuildSweeps 按几何推导）
        => SnapGeometry.SnapEdges(probe, edges, targets, screen, 8, visibility).Guides;

    private static void UiDrag(OverlayWindow overlay, LogService log, string label, int startX, int startY, int endX, int endY)
    {
        log.Info($"  [UI] {label}：({startX},{startY}) → ({endX},{endY})");
        overlay.TestPressAt(startX, startY);
        UiPump.For(60);

        for (var step = 1; step <= 8; step++)
        {
            var x = startX + ((endX - startX) * step / 8);
            var y = startY + ((endY - startY) * step / 8);
            overlay.TestDragTo(x, y);
            UiPump.For(30);
        }

        UiPump.For(120);
        log.Info($"  [UI]   拖拽中导轨=[{GuidesSign(overlay.TestGuides)}] 可见导轨线={overlay.TestVisibleGuideCount} " +
                 $"选区={Sign(overlay.SelectionBounds)}");
        overlay.TestReleaseAt();
        UiPump.For(120);
        log.Info($"  [UI]   松手后导轨=[{GuidesSign(overlay.TestLastDragGuides)}] 选区={Sign(overlay.SelectionBounds)}");
    }

    private static string GuidesSign(IReadOnlyList<SnapGuide> guides) => string.Join(",", guides.Select(g => g.ToString()));

    private static string Sign(Rect rect) => $"({rect.X:0},{rect.Y:0}) {rect.Width:0}x{rect.Height:0}";
}
