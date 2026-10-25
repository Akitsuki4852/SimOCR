using System.Windows;

namespace SimOCR.Overlay;

/// <summary>吸附导轨的轴向：Vertical = 竖线（约束 x），Horizontal = 横线（约束 y）。</summary>
internal enum SnapAxis
{
    Vertical,
    Horizontal,
}

/// <summary>吸附目标来自哪里（决定日志措辞与导轨的"理由"）。</summary>
internal enum SnapTargetKind
{
    Window,
    Monitor,
    VirtualScreen,
}

/// <summary>矩形上的某一条边。</summary>
internal enum SnapEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>一组边（Create / Resize 时说明"哪几条边在动"）。</summary>
[Flags]
internal enum SnapEdgeSet
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom,
}

/// <summary>一个吸附目标：候选窗口 / 显示器 / 虚拟桌面的**物理像素**矩形。</summary>
internal readonly record struct SnapTarget(Rect Bounds, SnapTargetKind Kind);

/// <summary>
/// 一条真正被应用的吸附导轨（画线 + 日志都靠它）。
/// <see cref="SpanStart"/>/<see cref="SpanEnd"/> 是导轨在**自身轴上**的可见范围
/// （竖线 = y 区间，横线 = x 区间）；NaN = 全幅（显示器/虚拟桌面这类永远可见的目标）。
/// 被遮盖的窗口边只画"露出来"的那一段，用户不会看到一条穿过别人窗口的线。
/// </summary>
internal readonly record struct SnapGuide(
    SnapAxis Axis,
    double Coordinate,
    SnapTargetKind Kind,
    SnapEdge Edge,
    Rect TargetBounds,
    double SpanStart = double.NaN,
    double SpanEnd = double.NaN)
{
    /// <summary>是否带可见范围（不带就整条画满）。</summary>
    public bool HasSpan => !double.IsNaN(SpanStart) && !double.IsNaN(SpanEnd);

    /// <summary>紧凑形式：<c>V:300:Window</c>（日志与自检断言用）。</summary>
    public override string ToString()
        => $"{(Axis == SnapAxis.Vertical ? "V" : "H")}:{Math.Round(Coordinate)}:{Kind}";
}

/// <summary>吸附结果：吸附（并按 Limits 夹紧、保证 ≥1px）后的矩形 + 生效的导轨。</summary>
internal sealed record SnapResult(Rect Rect, IReadOnlyList<SnapGuide> Guides);

/// <summary>
/// **窗口吸附的纯几何核心**。
///
/// 设计要点：
/// * 不依赖 WPF 控件、不读写任何状态、不调用 Win32 —— 只吃 <see cref="Rect"/>/<see cref="Point"/>，
///   因此可以脱离窗口直接单元测试（见 <c>SnapSelfTest</c> 的用例表）。
/// * **可脱离（escapable）**：每次都用"原始鼠标位置算出来的矩形"重新求最近候选，
///   从不基于上一次的吸附结果递推 —— 一旦超出容差就立刻不吸附，绝不会"粘住"。
/// * 容差内**最近者胜**；距离相同时取先出现的目标（枚举顺序 = z 序，窗口先于显示器/虚拟桌面）。
/// * 结果一定夹紧到 <paramref name="limits"/> 内且宽高 ≥1px；被夹紧后不再成立的导轨会被丢掉
///   （避免"画了线但其实没吸上"）。
/// * <paramref name="tolerance"/> ≤ 0 表示**关闭**：原样返回、不夹紧、无导轨（等同于旧行为）。
/// </summary>
internal static class SnapGeometry
{
    /// <summary>吸附后允许的最小尺寸（与 SelectionModel 的"≥1px"一致，收敛到一处）。</summary>
    private const double MinimumSize = MinimumSizes.SupportPixels;

    /// <summary>
    /// "这个目标的这条边，在 <paramref name="probeStart"/>..<paramref name="probeEnd"/> 这一段上露出来了吗"。
    ///
    /// 由 <c>WindowBounds.BuildVisibility</c>（Win32/z 序侧）提供实现；缺省实现 = 全部可见，
    /// 因此本文件**依然不认识窗口、不调用 Win32**，只消费"已经算好可见性"的区间。
    /// </summary>
    /// <param name="edgeCoordinate">候选边在自己的轴上的坐标（竖边 = x，横边 = y）。</param>
    /// <param name="probeStart">探测段（同一条边上的范围，必须落在实心区里）。</param>
    /// <param name="probeEnd">探测段末端。</param>
    internal delegate bool VisibilityFn(
        SnapTarget target,
        SnapAxis axis,
        double edgeCoordinate,
        double probeStart,
        double probeEnd);

    /// <summary>缺省可见性：没有任何目标被遮盖（纯几何自检与显示器/虚拟桌面目标走这条）。</summary>
    internal static VisibilityFn AlwaysVisible { get; } = (_, _, _, _, _) => true;

    /// <summary>
    /// 日志 / 证据里的矩形写法（<c>(x,y) w x h</c>）。
    ///
    /// **0.10.7 起**：它过去住在纯逻辑断言表
    /// （<c>tests/SimOCR.Tests/PureLogic/SnapGeometryChecks.cs</c>），靠 <c>SimOCR.csproj</c> 的
    /// <c>&lt;Compile Include&gt;</c> 链接进发布产物才能给 <c>Diagnostics/SelfTestSnap*.cs</c> 用。
    /// 断言表移出产物后，"真正的用户"是生产侧的自检探针，所以按职责回到这里 ——
    /// 于是断言表（测试侧）与探针（生产侧）**共用同一份实现**，日志文案逐字不变。
    /// </summary>
    internal static string Sign(Rect rect) => $"({rect.X:0},{rect.Y:0}) {rect.Width:0}x{rect.Height:0}";

    /// <summary>导轨序列的可比写法（<c>V:300:Window,H:250:Window</c>），供日志与断言共用。</summary>
    internal static string FormatGuides(IReadOnlyList<SnapGuide> guides)
        => string.Join(",", guides.Select(g => g.ToString()));

    /// <summary>只吸附指定的边（Create：移动角的两条边；Resize：控制点会动的那几条边）。</summary>
    public static SnapResult SnapEdges(
        Rect rect,
        SnapEdgeSet movingEdges,
        IReadOnlyList<SnapTarget> targets,
        Rect limits,
        int tolerance,
        VisibilityFn? visibility = null)
    {
        var visible = visibility ?? AlwaysVisible;

        if (!CanSnap(rect, targets, tolerance) || movingEdges == SnapEdgeSet.None)
        {
            return new SnapResult(rect, []);
        }

        var guides = new List<SnapGuide>(4);
        double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;

        if (Has(movingEdges, SnapEdgeSet.Left) &&
            NearestEdge(targets, l, SnapAxis.Vertical, SnapEdge.Left, tolerance, visible, t, b) is { } gl)
        {
            l = gl.Candidate.Coordinate;
            guides.Add(ToGuide(gl.Candidate, SnapAxis.Vertical, SnapEdge.Left, gl.SpanStart, gl.SpanEnd));
        }

        if (Has(movingEdges, SnapEdgeSet.Right) &&
            NearestEdge(targets, r, SnapAxis.Vertical, SnapEdge.Right, tolerance, visible, t, b) is { } gr)
        {
            r = gr.Candidate.Coordinate;
            guides.Add(ToGuide(gr.Candidate, SnapAxis.Vertical, SnapEdge.Right, gr.SpanStart, gr.SpanEnd));
        }

        if (Has(movingEdges, SnapEdgeSet.Top) &&
            NearestEdge(targets, t, SnapAxis.Horizontal, SnapEdge.Top, tolerance, visible, l, r) is { } gt)
        {
            t = gt.Candidate.Coordinate;
            guides.Add(ToGuide(gt.Candidate, SnapAxis.Horizontal, SnapEdge.Top, gt.SpanStart, gt.SpanEnd));
        }

        if (Has(movingEdges, SnapEdgeSet.Bottom) &&
            NearestEdge(targets, b, SnapAxis.Horizontal, SnapEdge.Bottom, tolerance, visible, l, r) is { } gb)
        {
            b = gb.Candidate.Coordinate;
            guides.Add(ToGuide(gb.Candidate, SnapAxis.Horizontal, SnapEdge.Bottom, gb.SpanStart, gb.SpanEnd));
        }

        // 保证 ≥1px：让"正在动的那条边"让位，没动的那条边（锚/对边）保持不动
        if (r - l < MinimumSize)
        {
            if (Has(movingEdges, SnapEdgeSet.Left) && !Has(movingEdges, SnapEdgeSet.Right))
            {
                l = r - MinimumSize;
            }
            else
            {
                r = l + MinimumSize;
            }
        }

        if (b - t < MinimumSize)
        {
            if (Has(movingEdges, SnapEdgeSet.Top) && !Has(movingEdges, SnapEdgeSet.Bottom))
            {
                t = b - MinimumSize;
            }
            else
            {
                b = t + MinimumSize;
            }
        }

        var clamped = ClampToLimits(new Rect(l, t, r - l, b - t), limits);
        return new SnapResult(clamped, KeepApplied(clamped, guides));
    }

    /// <summary>
    /// 整体平移吸附：四条边都参与，x/y 各自选最近候选后**整体平移**（尺寸不变）。
    /// </summary>
    public static SnapResult SnapMove(
        Rect rect,
        IReadOnlyList<SnapTarget> targets,
        Rect limits,
        int tolerance,
        VisibilityFn? visibility = null)
    {
        var visible = visibility ?? AlwaysVisible;

        if (!CanSnap(rect, targets, tolerance))
        {
            return new SnapResult(rect, []);
        }

        var vx = Best(
            NearestEdge(targets, rect.Left, SnapAxis.Vertical, SnapEdge.Left, tolerance, visible, rect.Top, rect.Bottom),
            NearestEdge(targets, rect.Right, SnapAxis.Vertical, SnapEdge.Right, tolerance, visible, rect.Top, rect.Bottom));

        var hy = Best(
            NearestEdge(targets, rect.Top, SnapAxis.Horizontal, SnapEdge.Top, tolerance, visible, rect.Left, rect.Right),
            NearestEdge(targets, rect.Bottom, SnapAxis.Horizontal, SnapEdge.Bottom, tolerance, visible, rect.Left, rect.Right));

        var dx = vx?.Candidate.Delta ?? 0;
        var dy = hy?.Candidate.Delta ?? 0;

        var moved = new Rect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
        var clamped = ClampToLimits(moved, limits);

        var guides = new List<SnapGuide>(2);
        if (vx is { } gx)
        {
            guides.Add(ToGuide(gx.Candidate, SnapAxis.Vertical, gx.Edge, gx.SpanStart, gx.SpanEnd));
        }

        if (hy is { } gy)
        {
            guides.Add(ToGuide(gy.Candidate, SnapAxis.Horizontal, gy.Edge, gy.SpanStart, gy.SpanEnd));
        }

        return new SnapResult(clamped, KeepApplied(clamped, guides));
    }

    /// <summary>
    /// 单点吸附（框选起点 = 锚点，只在 <c>BeginCreate</c> 时调用一次）：
    /// 两个轴各自独立取最近候选，因此"从窗口角附近起手"会精确落在角上。
    /// </summary>
    public static (int X, int Y) SnapPoint(
        int x,
        int y,
        IReadOnlyList<SnapTarget> targets,
        Rect limits,
        int tolerance,
        out IReadOnlyList<SnapGuide> guides,
        VisibilityFn? visibility = null)
    {
        var visible = visibility ?? AlwaysVisible;
        guides = [];

        if (tolerance <= 0 || targets.Count == 0)
        {
            return (x, y);
        }

        var list = new List<SnapGuide>(2);

        // 单点：要落上去的那个点本身必须可见（纯点探针，零长度段）
        var nx = NearestEdge(targets, x, SnapAxis.Vertical, SnapEdge.Left, tolerance, visible, y, y);
        var ny = NearestEdge(targets, y, SnapAxis.Horizontal, SnapEdge.Top, tolerance, visible, x, x);

        var sx = nx is { } gx ? (int)Math.Round(gx.Candidate.Coordinate) : x;
        var sy = ny is { } gy ? (int)Math.Round(gy.Candidate.Coordinate) : y;

        sx = (int)Math.Clamp(sx, limits.Left, Math.Max(limits.Left, limits.Right - 1));
        sy = (int)Math.Clamp(sy, limits.Top, Math.Max(limits.Top, limits.Bottom - 1));

        if (nx is { } vx && sx == (int)Math.Round(vx.Candidate.Coordinate))
        {
            list.Add(ToGuide(vx.Candidate, SnapAxis.Vertical, SnapEdge.Left, vx.SpanStart, vx.SpanEnd));
        }

        if (ny is { } vy && sy == (int)Math.Round(vy.Candidate.Coordinate))
        {
            list.Add(ToGuide(vy.Candidate, SnapAxis.Horizontal, SnapEdge.Top, vy.SpanStart, vy.SpanEnd));
        }

        guides = list;
        return (sx, sy);
    }

    // ------------------------------------------------------------------ 内部

    private static bool CanSnap(Rect rect, IReadOnlyList<SnapTarget> targets, int tolerance)
        => !rect.IsEmpty && tolerance > 0 && targets.Count > 0;

    private static bool Has(SnapEdgeSet edges, SnapEdgeSet flag) => (edges & flag) == flag;

    /// <summary>
    /// 候选（含"目标坐标偏好"），Distance = |目标 - 当前边|。
    /// SpanStart/SpanEnd = 这次吸附实际落点的可见重合段（画导轨用；NaN = 整条可见）。
    /// </summary>
    private readonly record struct Candidate(
        double Coordinate,
        double Delta,
        SnapTargetKind Kind,
        Rect TargetBounds,
        double Distance,
        double SpanStart = double.NaN,
        double SpanEnd = double.NaN);

    private static SnapGuide ToGuide(in Candidate candidate, SnapAxis axis, SnapEdge edge, double spanStart, double spanEnd)
        => new(axis, candidate.Coordinate, candidate.Kind, edge, candidate.TargetBounds, spanStart, spanEnd);

    /// <summary>某条边在矩形里的坐标。</summary>
    private static double EdgeCoordinate(Rect rect, SnapEdge edge) => edge switch
    {
        SnapEdge.Left => rect.Left,
        SnapEdge.Top => rect.Top,
        SnapEdge.Right => rect.Right,
        _ => rect.Bottom,
    };

    /// <summary>
    /// 找某条边最近的候选（容差内最近者胜；距离相同取先枚举到的目标）。
    /// <paramref name="freeStart"/>/<paramref name="freeEnd"/> 是**正在动的那条边**沿着自己轴的范围
    /// （竖边 = 选区的 y 区间）：被遮盖的边只有在"可见段与它相交"时才算候选。
    /// </summary>
    private static (Candidate Candidate, SnapEdge Edge, double SpanStart, double SpanEnd)? NearestEdge(
        IReadOnlyList<SnapTarget> targets,
        double edgeCoordinate,
        SnapAxis axis,
        SnapEdge edge,
        int tolerance,
        VisibilityFn visibility,
        double freeStart,
        double freeEnd)
    {
        (Candidate Candidate, double SpanStart, double SpanEnd)? best = null;

        foreach (var target in targets)
        {
            if (axis == SnapAxis.Vertical)
            {
                best = Better(best, target, target.Bounds.Left, edgeCoordinate, tolerance, visibility, SnapAxis.Vertical, freeStart, freeEnd);
                best = Better(best, target, target.Bounds.Right, edgeCoordinate, tolerance, visibility, SnapAxis.Vertical, freeStart, freeEnd);
            }
            else
            {
                best = Better(best, target, target.Bounds.Top, edgeCoordinate, tolerance, visibility, SnapAxis.Horizontal, freeStart, freeEnd);
                best = Better(best, target, target.Bounds.Bottom, edgeCoordinate, tolerance, visibility, SnapAxis.Horizontal, freeStart, freeEnd);
            }
        }

        return best is null
            ? null
            : (best.Value.Candidate, edge, best.Value.SpanStart, best.Value.SpanEnd);
    }

    private static (Candidate Candidate, double SpanStart, double SpanEnd)? Better(
        (Candidate Candidate, double SpanStart, double SpanEnd)? best,
        SnapTarget target,
        double coordinate,
        double edgeCoordinate,
        int tolerance,
        VisibilityFn visibility,
        SnapAxis axis,
        double freeStart,
        double freeEnd)
    {
        var delta = coordinate - edgeCoordinate;
        var distance = Math.Abs(delta);

        if (distance > tolerance)
        {
            return best;
        }

        // 只吸**看得见**的边：候选边与"正在动的那条边的可见段"求交，交集为空就整条跳过。
        // 两段都先夹进目标自己的范围，得到的交集同时也是导轨要画的可见范围
        // （纯点探针 freeStart==freeEnd 时退化成"落点本身可见吗"）。
        double clipStart, clipEnd;
        if (axis == SnapAxis.Vertical)
        {
            clipStart = Math.Max(freeStart, target.Bounds.Top);
            clipEnd = Math.Min(freeEnd, target.Bounds.Bottom);
        }
        else
        {
            clipStart = Math.Max(freeStart, target.Bounds.Left);
            clipEnd = Math.Min(freeEnd, target.Bounds.Right);
        }

        if (clipEnd < clipStart)
        {
            clipStart = freeStart;
            clipEnd = freeEnd;
        }

        if (!visibility(target, axis, coordinate, clipStart, clipEnd))
        {
            return best;
        }

        if (best is not null && distance >= best.Value.Candidate.Distance)
        {
            return best;
        }

        return (new Candidate(coordinate, delta, target.Kind, target.Bounds, distance), clipStart, clipEnd);
    }

    private static (Candidate Candidate, SnapEdge Edge, double SpanStart, double SpanEnd)? Best(
        (Candidate Candidate, SnapEdge Edge, double SpanStart, double SpanEnd)? a,
        (Candidate Candidate, SnapEdge Edge, double SpanStart, double SpanEnd)? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return a.Value.Candidate.Distance <= b.Value.Candidate.Distance ? a : b;
    }

    /// <summary>
    /// 夹紧到 Limits 内并保证 ≥1px（保持左上角，必要时收缩）。
    /// 最小尺寸与夹紧算法只有一份出处：<see cref="MinimumSizes"/>。
    /// </summary>
    private static Rect ClampToLimits(Rect rect, Rect limits)
    {
        if (limits.IsEmpty || limits.Width < MinimumSizes.SupportPixels || limits.Height < MinimumSizes.SupportPixels)
        {
            return rect;
        }

        // truncateToLimits: true —— 优先不越界，允许在退化 limits 下缩到 0 宽/0 高
        //（与原实现一致；见 MinimumSizes.ClampToLimits 的参数说明）。
        return MinimumSizes.ClampToLimits(
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            limits,
            MinimumSizes.SupportPixels,
            truncateToLimits: true);
    }

    /// <summary>丢掉被夹紧后不再成立的导轨（画了线却没吸上会误导用户）。</summary>
    private static IReadOnlyList<SnapGuide> KeepApplied(Rect rect, List<SnapGuide> guides)
    {
        if (guides.Count == 0)
        {
            return [];
        }

        var kept = new List<SnapGuide>(guides.Count);
        foreach (var guide in guides)
        {
            if (Math.Abs(EdgeCoordinate(rect, guide.Edge) - guide.Coordinate) < 0.5)
            {
                kept.Add(guide);
            }
        }

        return kept;
    }
}
