using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SimOCR.Interop;
using SimOCR.Overlay;

namespace SimOCR.PureLogic;

/// <summary>
/// 「窗口吸附」的**纯几何用例表**（28 条：18 条基础 + 4 条遮盖 + 6 条生产配对链）——
/// 每条都只吃 <see cref="SnapGeometry"/> 的公开 API，不需要窗口、不需要桌面、不需要鼠标。
///
/// **位置沿革**：这张表原本是
/// <c>src/SimOCR/Diagnostics/SelfTestSnap.cs:54-307</c> 里的 <c>RunGeometryCases</c>
/// （连同 <c>OcclusionScene</c>/<c>PairingScene</c>/<c>MispairedScene</c> 三个场景类）。
/// 它是 <c>--selftest-snap</c> 里唯一不碰真实桌面的部分，因此按 的口径搬进 tests/；
/// 同文件里那些**真的起窗口、读 DWM/GetWindowRect、调 SendInput** 的探针
/// （<c>RunWindowBoundsProbe</c> / <c>--accept-snap</c>）留在 <c>Diagnostics/</c>。
///
/// 0.10.1 决定保留：这张用例表的 Limits 继续**写死** 0,0 1920x1080 —— 它是**自包含的几何输入**
/// （直接喂给 SnapGeometry 的纯函数），不读真实桌面、不起窗口，所以在小屏机器上照样全跑、
/// 期望值也稳定可比。改成"按真实屏幕推导"会让 28 条用例的期望矩形/坐标随机器变化
/// （等于重写判据、削弱回归强度），而它本来就测不到"宿主几何装不下"这类环境问题 ——
/// 那类问题由 --snap-probe-occlusion 的逐边断言负责（见 Diagnostics/SelfTestSnapOcclusion.cs）。
/// </summary>
internal static class SnapGeometryChecks
{
    private sealed record SnapCase(string Name, Func<SnapResult> Run, Rect Expected, string ExpectedGuides);

    internal static PureCheckOutcome Run(PureCheckContext ctx)
    {
        var screen = new Rect(0, 0, 1920, 1080);
        var w1 = Window(100, 100, 200, 150);
        var w2 = Window(1000, 300, 400, 300);
        var defaults = new[] { w1, w2, Monitor(screen), VirtualScreen(screen) };
        const int tol = 8;

        var cases = new List<SnapCase>
        {
            // 1) 精确命中边
            new("正好压在边缘",
                () => SnapGeometry.SnapEdges(new Rect(100, 100, 200, 150), SnapEdgeSet.Right | SnapEdgeSet.Bottom, defaults, screen, tol),
                new Rect(100, 100, 200, 150), "V:300:Window,H:250:Window"),

            // 2) 容差内（5px）→ 吸附
            new("容差内 5 像素",
                () => SnapGeometry.SnapEdges(new Rect(205, 205, 100, 50), SnapEdgeSet.Right | SnapEdgeSet.Bottom, defaults, screen, tol),
                new Rect(205, 205, 95, 45), "V:300:Window,H:250:Window"),

            // 3) 差 9px（> 8）→ 不吸附（可脱离，不会粘住）
            new("容差外 9 像素不吸附",
                () => SnapGeometry.SnapEdges(new Rect(209, 209, 100, 50), SnapEdgeSet.Right | SnapEdgeSet.Bottom, defaults, screen, tol),
                new Rect(209, 209, 100, 50), string.Empty),

            // 4) 两个目标竞争 → 最近者胜（600 差 5px、609 差 4px → 取 609）
            new("两个目标取最近",
                () => SnapGeometry.SnapEdges(new Rect(605, 500, 100, 50), SnapEdgeSet.Left,
                    [Window(600, 0, 100, 100), Window(609, 0, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(609, 500, 96, 50), "V:609:Window"),

            // 5) 每条边各自独立：左边吸窗口 A(600)，右边吸窗口 C(1208)
            new("左右两边各自判断",
                () => SnapGeometry.SnapEdges(new Rect(605, 500, 597, 100), SnapEdgeSet.Left | SnapEdgeSet.Right,
                    [Window(600, 0, 100, 100), Window(1208, 0, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(600, 500, 608, 100), "V:600:Window,V:1208:Window"),

            // 6) 两个轴互不影响：x 吸上（只有左边在动 → 宽度 +5）、y 不吸
            new("两轴独立：X 吸附、Y 不吸附",
                () => SnapGeometry.SnapEdges(new Rect(605, 900, 100, 100), SnapEdgeSet.Left | SnapEdgeSet.Top,
                    [Window(600, 700, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(600, 900, 105, 100), "V:600:Window"),

            // 7) Move：四条边一起选最近，然后整体平移（尺寸不变）
            new("移动模式整体平移",
                () => SnapGeometry.SnapMove(new Rect(497, 697, 100, 100),
                    [Window(500, 700, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(500, 700, 100, 100), "V:500:Window,H:700:Window"),

            // 8) Resize：只有活动边（W → Left）动，其余三条边一格不动（左边 -3 → 宽度 -3）
            new("缩放模式只动活动边",
                () => SnapGeometry.SnapEdges(new Rect(497, 697, 100, 100), SnapEdgeSet.Left,
                    [Window(500, 700, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(500, 697, 97, 100), "V:500:Window"),

            // 9) Resize：E 手柄只动右边
            new("缩放模式拖右边",
                () => SnapGeometry.SnapEdges(new Rect(300, 300, 197, 100), SnapEdgeSet.Right,
                    [Window(500, 300, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(300, 300, 200, 100), "V:500:Window"),

            // 10) 显示器边界（两块屏的公共边）也是吸附目标
            new("显示器边界",
                () => SnapGeometry.SnapEdges(new Rect(500, 500, 457, 100), SnapEdgeSet.Right,
                    [Monitor(new Rect(0, 0, 960, 1080)), Monitor(new Rect(960, 0, 960, 1080))], screen, tol),
                new Rect(500, 500, 460, 100), "V:960:Monitor"),

            // 11) 虚拟桌面右边也是目标
            new("虚拟屏幕边缘",
                () => SnapGeometry.SnapEdges(new Rect(1700, 500, 217, 100), SnapEdgeSet.Right,
                    [VirtualScreen(screen)], screen, tol),
                new Rect(1700, 500, 220, 100), "V:1920:VirtualScreen"),

            // 12) 夹紧到 Limits（2200 → 1920，宽度收缩）
            new("夹回边界内",
                () => SnapGeometry.SnapEdges(new Rect(1900, 100, 300, 200), SnapEdgeSet.Right, defaults, screen, tol),
                new Rect(1900, 100, 20, 200), string.Empty),

            // 13) 吸到屏幕外的目标 → 被 Limits 夹回来，导轨必须丢掉（不能"画了线却没吸上"）
            new("超界吸附时丢弃导轨",
                () => SnapGeometry.SnapEdges(new Rect(1900, 100, 25, 200), SnapEdgeSet.Right,
                    [Window(1928, 100, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(1900, 100, 20, 200), string.Empty),

            // 14) 容差 0 = 关闭
            new("容差 0 等于关闭",
                () => SnapGeometry.SnapEdges(new Rect(205, 205, 100, 50), SnapEdgeSet.Right | SnapEdgeSet.Bottom, defaults, screen, 0),
                new Rect(205, 205, 100, 50), string.Empty),

            // 15) ≥1px：吸附把左边推过右边时收缩成 1px，并且导轨被丢掉
            new("最小尺寸 1 像素",
                () => SnapGeometry.SnapEdges(new Rect(500, 300, 10, 10), SnapEdgeSet.Left,
                    [Window(512, 0, 100, 100), VirtualScreen(screen)], screen, 12),
                new Rect(509, 300, 1, 10), string.Empty),

            // 16) 没有目标 → 原样返回
            new("没有候选窗口",
                () => SnapGeometry.SnapEdges(new Rect(209, 209, 100, 50), SnapEdgeSet.Right | SnapEdgeSet.Bottom, [], screen, tol),
                new Rect(209, 209, 100, 50), string.Empty),

            // 17) 单点吸附（框选锚点）：两轴各自独立落点
            new("空选区按锚点吸附",
                () =>
                {
                    var (x, y) = SnapGeometry.SnapPoint(605, 903,
                        [Window(600, 900, 100, 100), VirtualScreen(screen)], screen, tol, out var guides);
                    return new SnapResult(new Rect(x, y, 0, 0), guides);
                },
                new Rect(600, 900, 0, 0), "V:600:Window,H:900:Window"),

            // 18) 可脱离：移到容差外的那条轴立刻不吸（这里 x 不吸、y 仍吸）
            new("超出容差即脱开",
                () => SnapGeometry.SnapMove(new Rect(520, 697, 100, 100),
                    [Window(500, 700, 100, 100), VirtualScreen(screen)], screen, tol),
                new Rect(520, 700, 100, 100), "H:700:Window"),

            // ===== 19~22) 被遮盖的窗口边缘不该被吸附（用户报告：框选时会吸附被遮盖的窗口边缘）=====
            // 场景：A=(600,600,300,200) 完全落在 B=(500,550,800,400) 里（B 的 z 序更高）→
            //        A 的左边 x=600 整条不可见；A 的下边 y=800 只有 x∈[900,920] 那一小段露出来。
            // 可见性直接调 WindowBounds.ComputeVisiblePieces（真实现），不是手写常量。

            // 19) 被完全遮盖的边在容差内 → 不吸（拖动左边落在 A 的 600 附近，容差内只剩这条不可见的边）
            new("被遮住的边不吸附",
                () => SnapGeometry.SnapEdges(new Rect(603, 700, 150, 100), SnapEdgeSet.Left,
                    OcclusionScene.Targets, screen, tol, OcclusionScene.Visibility),
                new Rect(603, 700, 150, 100), string.Empty),

            // 20) 被部分遮盖的边：拖动段与可见段相交 → 允许吸附（A 下边 y=800，露在 B 右侧的可见段 x∈[1300,1320]）
            new("部分遮挡时按可见段吸附",
                () => SnapGeometry.SnapEdges(new Rect(1300, 700, 20, 96), SnapEdgeSet.Bottom,
                    OcclusionScene.Targets, screen, tol, OcclusionScene.Visibility),
                new Rect(1300, 700, 20, 100), "H:800:Window"),

            // 21) 整窗被遮盖 → 候选里就不该有它（DropFullyOccluded 丢掉 A，留下 B）
            new("整窗被遮挡时丢弃候选",
                () =>
                {
                    var dropped = WindowBounds.DropFullyOccluded(
                    [
                        new WindowCandidate(new IntPtr(2), "B", "SimOCR-test", 2, new Rect(500, 550, 800, 400)),
                        new WindowCandidate(new IntPtr(1), "A", "SimOCR-test", 1, new Rect(600, 600, 300, 200)),
                    ]);

                    return new SnapResult(new Rect(dropped.Visible.Count, dropped.Occluded, 0, 0), []);
                },
                new Rect(1, 1, 0, 0), string.Empty),

            // 22) 同一个场景里，**遮盖者自己的可见边**照常吸附（回归：修复不能顺手把正常吸附关掉）
            new("遮盖者自身的可见边仍可吸附",
                () => SnapGeometry.SnapEdges(new Rect(497, 700, 150, 100), SnapEdgeSet.Left,
                    OcclusionScene.Targets, screen, tol, OcclusionScene.Visibility),
                new Rect(500, 700, 147, 100), "V:500:Window"),

            // ===== 23) 生产**调用点配对方式**的回归（0.10.0 的头条判据）=====
            // 上面 19~22 条把 targets 与 candidates **手工对齐**后调用 BuildVisibility，
            // 因此它们**测不出**真正的生产缺陷：OverlayWindow 曾用**过滤后**的 visibleCandidates 建 targets，
            // 却把**未过滤**的 raw.Candidates 交给 BuildVisibility，而后者按**下标**配对
            // （WindowBounds 的约定 / byKey）。只要 occluded > 0 下标就整体错位，
            // 一部分可见的窗口查不到索引 → 落到"不在索引里=永远可见"兜底 → 又能吸到被遮盖的边。
            // 0.10.0 起 targets 的**唯一构建点**是 WindowBounds.BuildSnapTargets（生产与自检共用同一个方法），
            // 它固定收**原始**候选 → 下标配对天然成立。下面 23a~23d 用这个构建点走同一条配对链。

            // 23a) A 被 B 部分遮盖（A 左边 x=600 整条不可见）→ 吸它**隐藏的左边**必须被拒
            new("生产配对：隐藏边不吸附",
                () => SnapGeometry.SnapEdges(new Rect(603, 700, 150, 100), SnapEdgeSet.Left,
                    PairingScene.Targets, screen, tol, PairingScene.Visibility),
                new Rect(603, 700, 150, 100), string.Empty),

            // 23b) 同一场景、同一条**可见**边：A 的下边 y=800 在 A 自己宽度范围内整条露着（x∈[600,750]）
            //      → 必须照常吸附（证明 23a 的"不吸"不是整体关掉了吸附）
            new("生产配对：可见边吸附",
                () => SnapGeometry.SnapEdges(new Rect(600, 700, 150, 96), SnapEdgeSet.Bottom,
                    PairingScene.Targets, screen, tol, PairingScene.Visibility),
                new Rect(600, 700, 150, 100), "H:800:Window"),

            // 23c) 遮盖者 B 自己的可见左边照常吸附（回归：修配对不能顺手把正常吸附关掉）
            new("生产配对：遮盖边仍吸附",
                () => SnapGeometry.SnapEdges(new Rect(497, 700, 150, 100), SnapEdgeSet.Left,
                    PairingScene.Targets, screen, tol, PairingScene.Visibility),
                new Rect(500, 700, 147, 100), "V:500:Window"),

            // 23d) 用**生产形状**的目标列表（window×2 + 显示器 + 虚拟桌面 → targets.Count = 5 > raw.Count = 4，
            //      即 里"下标配对静默错位"的那种形状）复核 23a：A 隐藏的左边照样不吸。
            //      这条同时把"显示器/虚拟桌面排在窗口之后"的顺序契约钉住 —— 顺序一错，A 的索引又会错位。
            new("生产配对：列表形的隐藏边不吸附",
                () => SnapGeometry.SnapEdges(new Rect(603, 700, 150, 100), SnapEdgeSet.Left,
                    OcclusionScene.ProductionShapeTargets, screen, tol, OcclusionScene.ProductionShapeVisibility),
                new Rect(603, 700, 150, 100), string.Empty),

            // 23e) **缺陷注入 / "错位报警器"**：复刻的失效结构（"永远可见"兜底）。
            //      MispairedScene 的 z 序是 [B, D, A]，D ⊂ B（整窗被盖住、被 DropFullyOccluded 丢掉）：
            //        * 正确配对：targets 由原始列表 [B, D, A] 构建 → A 的条目 = 自己露出来的那部分片段
            //          → A 被遮盖的左边 x=600 判为**不可见** → 不吸；
            //        * 错位配对（旧的调用点写法）：targets 由过滤结果 [B, A] 构建（长度 2 < 3），
            //          按下标配对时 A 根本没进索引 → 落到"不在索引里 = 永远可见"兜底 → 同一条边**被吸上**。
            //      两条用例用**同一个场景、同一个查询**断言相反结果，因此：
            //        - 谁把 targets 改回"过滤后的可见候选"，23e-2 立刻变红；
            //        - 谁把 BuildVisibility 的兜底删掉/改掉，23e-1 立刻变红。
            //      （生产代码只经 WindowBounds.BuildSnapTargets 一个构建点，23a~23d 负责那条链。）
            new("生产配对：错配能被发现",
                () => SnapGeometry.SnapEdges(new Rect(603, 950, 150, 100), SnapEdgeSet.Left,
                    MispairedScene.FilteredTargets, screen, tol, MispairedScene.FilteredVisibility),
                new Rect(600, 950, 153, 100), "V:600:Window"),
            new("生产配对：正确配对不吸附",
                () => SnapGeometry.SnapEdges(new Rect(603, 950, 150, 100), SnapEdgeSet.Left,
                    MispairedScene.Targets, screen, tol, MispairedScene.Visibility),
                new Rect(603, 950, 150, 100), string.Empty),
        };

        ctx.Info($"  [几何] 用例 {cases.Count} 条（矩形坐标均为物理像素；Limits=0,0 1920x1080 是**自包含的几何输入**，" +
                 "与真实桌面尺寸无关：小屏机器上这 28 条一样全跑、期望值一样）");
        ctx.Info($"  [遮盖] 错位报警器场景（失效结构）：raw={MispairedScene.RawCandidates.Count} 个候选 → "
                 + $"DropFullyOccluded 后 {MispairedScene.VisibleCandidates.Count} 个；"
                 + $"A={Sign(MispairedScene.A.Bounds)} 的可见片段 {WindowBounds.ComputeVisiblePieces(MispairedScene.A, [MispairedScene.B, MispairedScene.D]).Count} 个 = "
                 + string.Join(" | ", WindowBounds.ComputeVisiblePieces(MispairedScene.A, [MispairedScene.B, MispairedScene.D]).Select(p => Sign(p.Bounds))));
        ctx.Info($"  [遮盖] {OcclusionScene.Describe()}");

        foreach (var snapCase in cases)
        {
            SnapResult result;
            try
            {
                result = snapCase.Run();
            }
            catch (Exception ex)
            {
                ctx.Error($"  [失败] {snapCase.Name} 抛异常", ex);
                ctx.FailWithoutMessage();
                continue;
            }

            var rectOk = RectEquals(result.Rect, snapCase.Expected);
            var guidesOk = string.Equals(FormatGuides(result.Guides), snapCase.ExpectedGuides, StringComparison.Ordinal);
            var ok = rectOk && guidesOk;

            ctx.Check(
                snapCase.Name.PadRight(36),
                ok,
                $"rect={Sign(result.Rect)} 期望={Sign(snapCase.Expected)} " +
                $"| 导轨=[{FormatGuides(result.Guides)}] 期望=[{snapCase.ExpectedGuides}]");
        }

        return ctx.Outcome();
    }

    /// <summary>
    /// 日志/证据里的矩形写法与导轨写法：**0.10.7 起起住在生产侧**
    /// （<see cref="SnapGeometry.Sign"/> / <see cref="SnapGeometry.FormatGuides"/>）——
    /// 它们的真正用户是"真的起窗口"的自检探针（<c>Diagnostics/SelfTestSnap*.cs</c>），
    /// 这里只是跟着用同一份实现（同一份文案，改一处两处一起变）。
    /// </summary>
    private static string Sign(Rect rect) => SnapGeometry.Sign(rect);

    private static string FormatGuides(IReadOnlyList<SnapGuide> guides) => SnapGeometry.FormatGuides(guides);

    private static bool RectEquals(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001
           && Math.Abs(a.Width - b.Width) < 0.001 && Math.Abs(a.Height - b.Height) < 0.001;

    private static SnapTarget Window(double x, double y, double w, double h)
        => new(new Rect(x, y, w, h), SnapTargetKind.Window);

    private static SnapTarget Monitor(Rect bounds) => new(bounds, SnapTargetKind.Monitor);

    private static SnapTarget VirtualScreen(Rect bounds) => new(bounds, SnapTargetKind.VirtualScreen);

    /// <summary>
    /// 用例 19~22 的遮盖场景：B（大，z 序更高）压住 A（小）。
    /// 目标的可见性由**生产代码** <see cref="WindowBounds.ComputeVisiblePieces"/> 现算，
    /// 所以这几条用例同时回归了"可见性计算"本身。
    /// </summary>
    private static class OcclusionScene
    {
        public static readonly WindowCandidate B = new(new IntPtr(2), "B", "SimOCR-test", 2, new Rect(500, 550, 800, 400));

        /// <summary>A 比 B 更宽也更矮：只有**露在 B 右侧**的那一条（x∈[1300,1320], y∈[600,800]）可见。</summary>
        public static readonly WindowCandidate A = new(new IntPtr(1), "A", "SimOCR-test", 1, new Rect(600, 600, 720, 200));

        /// <summary>z 序自上而下：B 在前（盖住 A）→ A 自己的可见片段只剩右边那一条窄条。</summary>
        public static readonly IReadOnlyList<WindowCandidate> Candidates = [B, A];

        public static readonly IReadOnlyList<SnapTarget> Targets =
        [
            new(B.Bounds, SnapTargetKind.Window),
            new(A.Bounds, SnapTargetKind.Window),
            new(new Rect(0, 0, 1920, 1080), SnapTargetKind.VirtualScreen),
        ];

        public static readonly SnapGeometry.VisibilityFn Visibility = WindowBounds.BuildVisibility(Candidates, Targets);

        /// <summary>
        /// **生产形状**（0.10.0 之后的写法）：窗口（z 序）→ 显示器 → 虚拟桌面，
        /// 且窗口段由**未过滤**的 <see cref="Candidates"/> 构建 —— 即 targets.Count 可以 &gt; candidates.Count。
        /// 用同一个构建点 <see cref="WindowBounds.BuildSnapTargets"/>，用例 23d 复核"隐藏边不吸"，
        /// 把"显示器/虚拟桌面排在窗口之后"的顺序契约与下标配对一起钉住。
        /// </summary>
        public static readonly IReadOnlyList<SnapTarget> ProductionShapeTargets =
            WindowBounds.BuildSnapTargets(Candidates, [new Rect(0, 0, 1920, 1080)], new Rect(0, 0, 1920, 1080));

        public static readonly SnapGeometry.VisibilityFn ProductionShapeVisibility =
            WindowBounds.BuildVisibility(Candidates, ProductionShapeTargets);

        /// <summary>给日志用：打印两个候选算出来的可见片段数（A 只露右边那一条）。</summary>
        public static string Describe()
            => $"A={Sign(A.Bounds)} 被 B={Sign(B.Bounds)} 遮住：A 可见片段 {WindowBounds.ComputeVisiblePieces(A, [B]).Count} 个" +
               $"（左边 x=600 整条不可见；下边 y=800 只露 x∈[1300,1320]）；B 可见片段 {WindowBounds.ComputeVisiblePieces(B, []).Count} 个";
    }

    /// <summary>
    /// 用例 23a~23c 的**生产调用点配对**场景（回归）。
    ///
    /// 与 <see cref="OcclusionScene"/> 的区别在"陷阱形状"：这里 A 与遮盖者 B **部分相交**
    /// （A=(600,600,720,200)、B=(500,550,800,250)，B 的 z 序更高），因此
    /// A **不会被 DropFullyOccluded 丢掉**（必须留在可见候选里，否则根本测不到"错位"）。
    /// A 减掉 B 之后的可见片段恰好两条：
    ///   * <c>[1300,1320]×[600,800]</c> —— A 露在 B 右侧的窄条；
    ///   * <c>[600,1300]×[800,900]</c> —— A 露在 B 下侧的那一条（高 100）。
    /// 于是 A 的左边 x=600 **整条不可见**（第二条从 y=800 才开始，够不到 y=700 的探测段），
    /// 而 A 的下边 y=800 在 x∈[600,1300] 上都在第一条片段的边界上 → 探测段 [600,750] 落在可见区里，照常吸附。
    ///
    /// 这个形状能把"用可见候选建 targets"的错误暴露出来：raw 有 3 个候选（B、A、D），
    /// DropFullyOccluded 丢掉 D 只剩 2 个（**过滤不重排**），于是按下标配对时
    /// <c>i=0: byKey[(B.Bounds, Window)] = A 的片段</c>、<c>i=1: byKey[(A.Bounds, Window)] = A 自己的片段</c>
    /// —— D 整个漏出索引，"永远可见"兜底也救不了：A 隐藏的左边在错误配对下**会被吸上**
    /// （用例 23a 失败、退出码非 0）。正确配对下 A 的条目是上面那两条片段 → 左边整条不吸、下边照吸。
    /// </summary>
    private static class PairingScene
    {
        /// <summary>遮盖者：z 序最高，自己四条边都可见。</summary>
        public static readonly WindowCandidate B = new(new IntPtr(2), "B", "SimOCR-test", 2, new Rect(500, 550, 800, 250));

        /// <summary>被遮盖者：左边 x=600 整条不可见；下边 y=800 在 x∈[600,1300] 上可见。</summary>
        public static readonly WindowCandidate A = new(new IntPtr(1), "A", "SimOCR-test", 1, new Rect(600, 600, 720, 200));

        /// <summary>整窗被 B 盖住（制造 occluded &gt; 0：下标错位的前提）。放在 A 右侧，不与 A 抢同一条边。</summary>
        public static readonly WindowCandidate D = new(new IntPtr(4), "D", "SimOCR-test", 4, new Rect(1200, 700, 90, 90));

        /// <summary>原始枚举（含被整窗遮盖的 D）→ occluded = 1。</summary>
        public static readonly IReadOnlyList<WindowCandidate> RawCandidates = [B, A, D];

        /// <summary>DropFullyOccluded 的过滤结果：[B, A]（D 被丢掉，列表**不重排**）。</summary>
        public static readonly IReadOnlyList<WindowCandidate> VisibleCandidates =
            WindowBounds.DropFullyOccluded(RawCandidates).Visible;

        /// <summary>
        /// 生产目标列表：**与 OverlayWindow 共用同一个构建点** <see cref="WindowBounds.BuildSnapTargets"/>，
        /// 传入**原始**候选（含被整窗遮盖的 D）。用例 23a~23d 因此回归的就是生产那条配对链。
        /// </summary>
        public static readonly IReadOnlyList<SnapTarget> Targets =
            WindowBounds.BuildSnapTargets(RawCandidates, [], new Rect(0, 0, 1920, 1080));

        public static readonly SnapGeometry.VisibilityFn Visibility = WindowBounds.BuildVisibility(RawCandidates, Targets);
    }

    /// <summary>
    /// 用例 23e 的"错位报警器"场景：枚举里有一个**整窗被遮盖**的候选 D 排在 B 后面
    /// （D 被 <see cref="WindowBounds.DropFullyOccluded"/> 丢掉但不重排，其后的 A 留在原位）。
    ///
    /// 列表 = [B, D, A]，其中 D ⊂ B（整窗被盖住）；B 盖住 A 的左边：
    ///   * <see cref="WindowBounds.DropFullyOccluded"/> → [B, A]（D 丢掉）；
    ///   * A 的可见片段 = 只剩 y∈[800,900] 那条下条 → 左边 x=600 **整条不可见**（y=700 处不吸）。
    ///
    /// 于是两种配对方式在 x=600 上给出**相反**的答案：
    ///   * 正确：targets 由原始列表 [B, D, A] 构建 → <c>byKey[(A.Bounds, Window)] = ComputeVisiblePieces(A, [B, D])</c>
    ///     （只露下条）→ 左边不可见 → **不吸**；
    ///   * 错位：targets 由过滤列表 [B, A] 构建（长度 2 &lt; 3），按下标配对时
    ///     <c>i=0: candidate=B</c> 拿走 <c>targets[0] = D 的 kind</c>、<c>i=1: candidate=D</c> 拿走 <c>targets[1] = A 的 kind</c>
    ///     → <c>byKey[(D.Bounds, Window)] = B 的片段</c>、<c>byKey[(B.Bounds, Window)] = D 的空片段</c>，
    ///     A 自己根本没进索引 → 落到"不在索引里 = 永远可见"兜底 → 左边**被吸上**。
    /// 这就是的失效机制（"永远可见"兜底），用例 23e-1 断言它确实会发生（报警器有效），
    /// 用例 23e-2 断言正确配对下不会发生。
    /// </summary>
    private static class MispairedScene
    {
        /// <summary>遮盖者（z 序最高）：整体压在 A 的左边，把 A 的 x∈[600,800] 整条盖住。</summary>
        public static readonly WindowCandidate B = new(new IntPtr(2), "B", "SimOCR-test", 2, new Rect(600, 500, 200, 400));

        /// <summary>整窗被 B 盖住、且**排在 A 前面**（制造"过滤后下标错位"）。</summary>
        public static readonly WindowCandidate D = new(new IntPtr(4), "D", "SimOCR-test", 4, new Rect(700, 500, 100, 100));

        /// <summary>被遮盖者：左边 x=600 整条被 B 盖住，只露 x∈[800,900] 那一竖条。</summary>
        public static readonly WindowCandidate A = new(new IntPtr(1), "A", "SimOCR-test", 1, new Rect(600, 600, 300, 300));

        /// <summary>z 序自上而下：B → D（被 B 盖光）→ A。</summary>
        public static readonly IReadOnlyList<WindowCandidate> RawCandidates = [B, D, A];

        /// <summary>过滤结果（D 被丢掉、不重排）= [B, A]。</summary>
        public static readonly IReadOnlyList<WindowCandidate> VisibleCandidates =
            WindowBounds.DropFullyOccluded(RawCandidates).Visible;

        /// <summary>**正确**的生产写法：targets 由原始列表构建（与 OverlayWindow 共用同一个方法）。</summary>
        public static readonly IReadOnlyList<SnapTarget> Targets =
            WindowBounds.BuildSnapTargets(RawCandidates, [], new Rect(0, 0, 1920, 1080));

        public static readonly SnapGeometry.VisibilityFn Visibility =
            WindowBounds.BuildVisibility(RawCandidates, Targets);

        /// <summary>**错误**的旧写法：targets 由过滤结果构建，candidates 却传原始列表。仅用于报警器断言。</summary>
        public static readonly IReadOnlyList<SnapTarget> FilteredTargets =
            WindowBounds.BuildSnapTargets(VisibleCandidates, [], new Rect(0, 0, 1920, 1080));

        public static readonly SnapGeometry.VisibilityFn FilteredVisibility =
            WindowBounds.BuildVisibility(RawCandidates, FilteredTargets);
    }
}
