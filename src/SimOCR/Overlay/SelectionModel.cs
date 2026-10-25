using System.Windows;

namespace SimOCR.Overlay;

/// <summary>选区的可拖拽部位。</summary>
internal enum SelectionHandle
{
    None,
    Inside,
    NW,
    N,
    NE,
    W,
    E,
    SW,
    S,
    SE,
}

/// <summary>
/// 吸附上下文：目标集合 + 容差（物理像素）。
/// 容差由 UI 层每帧从设置里刷新，所以"会话中途关掉吸附"会立刻生效且不会打断正在进行的拖拽。
/// </summary>
internal sealed class SnapContext
{
    public SnapContext(IReadOnlyList<SnapTarget> targets, int tolerance)
    {
        Targets = targets;
        Tolerance = tolerance;
    }

    /// <summary>关闭状态（无目标、容差 0）。</summary>
    public static SnapContext Disabled { get; } = new([], 0);

    public IReadOnlyList<SnapTarget> Targets { get; set; }

    public int Tolerance { get; set; }

    /// <summary>
    /// 可见性判断：只吸"屏幕上真的看得见"的边。
    /// 由 UI 层从 <c>WindowBounds.BuildVisibility</c> 装进来；为 null 表示不做遮盖裁剪
    /// （自检的合成帧与"吸附关闭"路径就是这样）。
    /// </summary>
    public SnapGeometry.VisibilityFn? Visibility { get; set; }

    public bool Enabled => Tolerance > 0 && Targets.Count > 0;
}

/// <summary>
/// 选区几何模型 —— **全部使用物理像素**（虚拟桌面坐标系，左上角可能为负）。
/// 这是整个截图层唯一的几何真源：覆盖层渲染时才换算成 DIP。
/// </summary>
internal sealed class SelectionModel
{
    /// <summary>
    /// 小于该尺寸视为误触，松开左键后按取消处理。
    /// **4px 是"用户意图"门槛，不是几何下界**：几何下界是 <see cref="MinimumSizes.SupportPixels"/>
    /// （1px）—— 两者适用面不同，见 <see cref="MinimumSizes"/> 的类注释。
    /// </summary>
    public const int MinimumMeaningfulSize = MinimumSizes.MeaningfulPixels;

    private SelectionHandle _handle = SelectionHandle.None;
    private int _anchorX;
    private int _anchorY;
    private int _startX;
    private int _startY;
    private bool _creating;
    private bool _dragActive;
    private Rect _startBounds;
    private IReadOnlyList<SnapGuide> _guides = [];
    private IReadOnlyList<SnapGuide> _anchorGuides = [];

    /// <summary>允许的边界（虚拟桌面矩形，物理像素）。</summary>
    public Rect Limits { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>吸附上下文。默认关闭，UI 层设置后才生效（自检的合成帧就靠这个走纯几何）。</summary>
    public SnapContext Snap { get; set; } = SnapContext.Disabled;

    /// <summary>当前生效的吸附导轨（渲染 + 日志用；没有吸附时为空）。</summary>
    public IReadOnlyList<SnapGuide> Guides => _guides;

    public Rect Bounds { get; private set; } = Rect.Empty;

    public bool Exists { get; private set; }

    public bool IsTooSmall => !Exists
        || Bounds.Width < MinimumMeaningfulSize
        || Bounds.Height < MinimumMeaningfulSize;

    public int X => (int)Math.Round(Bounds.X);
    public int Y => (int)Math.Round(Bounds.Y);
    public int Width => (int)Math.Round(Bounds.Width);
    public int Height => (int)Math.Round(Bounds.Height);

    /// <summary>
    /// "单击抓取整个窗口"：把选区直接设成候选窗口的矩形。
    /// 与 <see cref="Limits"/> 求**交集**（窗口可能有一部分在屏幕外，冻结帧里没有那些像素），
    /// 完全在屏幕外时返回 false，调用方按取消处理。
    /// </summary>
    public bool SetBounds(Rect rect)
    {
        var clipped = Rect.Intersect(rect, Limits);
        if (clipped.IsEmpty || clipped.Width < 1 || clipped.Height < 1)
        {
            return false;
        }

        Bounds = clipped;
        Exists = true;
        _creating = false;
        _dragActive = false;
        _handle = SelectionHandle.None;
        _guides = [];
        _anchorGuides = [];
        return true;
    }

    public void BeginCreate(int physicalX, int physicalY)
    {

        _creating = true;
        _dragActive = true;

        if (Snap.Enabled)
        {
            // 锚点只在起手时吸附一次（"从窗口角附近起手就直接落在角上"），之后不再动
            var (x, y) = SnapGeometry.SnapPoint(physicalX, physicalY, Snap.Targets, Limits, Snap.Tolerance, out var guides, Snap.Visibility);
            _anchorX = x;
            _anchorY = y;
            _anchorGuides = guides;
        }
        else
        {
            _anchorX = physicalX;
            _anchorY = physicalY;
            _anchorGuides = [];
        }

        Bounds = Rect.Empty;
        Exists = false;
        _guides = _anchorGuides;
    }

    public void BeginMove(int physicalX, int physicalY)
    {
        if (!Exists)
        {
            return;
        }

        _startX = physicalX;
        _startY = physicalY;
        _startBounds = Bounds;
        _creating = false;
        _dragActive = true;
    }

    public void BeginResize(SelectionHandle handle, int physicalX, int physicalY)
    {
        if (!Exists)
        {
            return;
        }

        _handle = handle;
        _startX = physicalX;
        _startY = physicalY;
        _startBounds = Bounds;
        _creating = false;
        _dragActive = true;
    }

    /// <summary>
    /// 拖拽中更新（物理像素）。分支判据是"上一次 Begin* 记下的状态"：
    /// <c>_creating</c> = 框选；否则看 <see cref="_handle"/>（<c>None</c> = 整体平移，其余 = 缩放）。
    /// 不再另存一个拖拽模式枚举 —— 那个枚举只写不读，0.10.5 已删。
    /// </summary>
    public void Update(int physicalX, int physicalY)
    {

        if (!_dragActive)
        {
            return;
        }

        if (_creating)
        {
            Bounds = CreateBounds(physicalX, physicalY);
            Exists = Bounds.Width >= 1 && Bounds.Height >= 1;
            return;
        }

        if (_handle == SelectionHandle.None)
        {
            UpdateMove(physicalX, physicalY);
        }
        else
        {
            UpdateResize(physicalX, physicalY);
        }
    }

    /// <summary>
    /// Create：只有"远离锚点的那两条边"在动，锚点的两条边保持不动（锚已在 BeginCreate 吸附过一次）。
    /// 每次都从"锚点 + 当前鼠标"重新求原始矩形再吸附，所以一旦超出容差立刻脱开，不会粘住。
    /// </summary>
    private Rect CreateBounds(int physicalX, int physicalY)
    {
        var raw = Normalize(_anchorX, _anchorY, physicalX, physicalY);

        if (!Snap.Enabled)
        {
            _guides = [];
            return ClampTo(raw, Limits);
        }

        var movingEdges = SnapEdgeSet.None;
        movingEdges |= physicalX >= _anchorX ? SnapEdgeSet.Right : SnapEdgeSet.Left;
        movingEdges |= physicalY >= _anchorY ? SnapEdgeSet.Bottom : SnapEdgeSet.Top;

        var result = SnapGeometry.SnapEdges(raw, movingEdges, Snap.Targets, Limits, Snap.Tolerance, Snap.Visibility);
        _guides = MergeGuides(_anchorGuides, result.Guides);
        return result.Rect;
    }

    /// <summary>Move：四边一起参与，选最近的一条候选后**整体平移**（尺寸不变）。</summary>
    private void UpdateMove(int physicalX, int physicalY)
    {
        if (!Exists)
        {
            return;
        }

        var dx = physicalX - _startX;
        var dy = physicalY - _startY;

        if (!Snap.Enabled)
        {
            _guides = [];
            MoveByFromStart(dx, dy);
            return;
        }

        var raw = new Rect(_startBounds.X + dx, _startBounds.Y + dy, _startBounds.Width, _startBounds.Height);
        var result = SnapGeometry.SnapMove(raw, Snap.Targets, Limits, Snap.Tolerance, Snap.Visibility);
        Bounds = result.Rect;
        _guides = result.Guides;
    }

    /// <summary>Resize：只吸附当前控制点会移动的那几条边。</summary>
    private void UpdateResize(int physicalX, int physicalY)
    {
        var dx = physicalX - _startX;
        var dy = physicalY - _startY;
        double l = _startBounds.Left, t = _startBounds.Top, r = _startBounds.Right, b = _startBounds.Bottom;

        switch (_handle)
        {
            case SelectionHandle.NW: l += dx; t += dy; break;
            case SelectionHandle.N: t += dy; break;
            case SelectionHandle.NE: r += dx; t += dy; break;
            case SelectionHandle.W: l += dx; break;
            case SelectionHandle.E: r += dx; break;
            case SelectionHandle.SW: l += dx; b += dy; break;
            case SelectionHandle.S: b += dy; break;
            case SelectionHandle.SE: r += dx; b += dy; break;
            default: return;
        }

        var movingEdges = HandleEdges(_handle);

        // 拖过头把矩形拖成负宽高时不做吸附（交给下面的夹紧逻辑收成 1px），避免 Rect 语义反转
        if (Snap.Enabled && movingEdges != SnapEdgeSet.None && r - l > 0 && b - t > 0)
        {
            var result = SnapGeometry.SnapEdges(new Rect(l, t, r - l, b - t), movingEdges, Snap.Targets, Limits, Snap.Tolerance, Snap.Visibility);
            Bounds = result.Rect;
            _guides = result.Guides;
            return;
        }

        _guides = [];
        Bounds = ClampResize(l, t, r, b);
    }

    private static SnapEdgeSet HandleEdges(SelectionHandle handle) => handle switch
    {
        SelectionHandle.NW => SnapEdgeSet.Left | SnapEdgeSet.Top,
        SelectionHandle.N => SnapEdgeSet.Top,
        SelectionHandle.NE => SnapEdgeSet.Right | SnapEdgeSet.Top,
        SelectionHandle.W => SnapEdgeSet.Left,
        SelectionHandle.E => SnapEdgeSet.Right,
        SelectionHandle.SW => SnapEdgeSet.Left | SnapEdgeSet.Bottom,
        SelectionHandle.S => SnapEdgeSet.Bottom,
        SelectionHandle.SE => SnapEdgeSet.Right | SnapEdgeSet.Bottom,
        _ => SnapEdgeSet.None,
    };

    /// <summary>导轨去重合并（锚点导轨在前）。</summary>
    private static IReadOnlyList<SnapGuide> MergeGuides(IReadOnlyList<SnapGuide> first, IReadOnlyList<SnapGuide> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        if (first.Count == 0)
        {
            return second;
        }

        var merged = new List<SnapGuide>(first.Count + second.Count);
        merged.AddRange(first);
        foreach (var guide in second)
        {
            var duplicate = false;
            foreach (var existing in merged)
            {
                if (existing.Axis == guide.Axis && Math.Abs(existing.Coordinate - guide.Coordinate) < 0.5)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                merged.Add(guide);
            }
        }

        return merged;
    }

    public void EndDrag()
    {
        _creating = false;
        _dragActive = false;
        _guides = [];
        _anchorGuides = [];
    }

    /// <summary>按方向键微调选区（物理像素）。</summary>
    public void MoveBy(int dx, int dy)
    {
        if (!Exists)
        {
            return;
        }

        var x = Math.Clamp(Bounds.X + dx, Limits.Left, Math.Max(Limits.Left, Limits.Right - Bounds.Width));
        var y = Math.Clamp(Bounds.Y + dy, Limits.Top, Math.Max(Limits.Top, Limits.Bottom - Bounds.Height));
        Bounds = new Rect(x, y, Bounds.Width, Bounds.Height);
    }

    /// <summary>
    /// 从"按下那一刻的矩形"整体平移（吸附关闭时的 Move 路径）。
    /// 过去的签名多一个 <c>fromStart</c> 参数，但唯一实参恒为 <c>true</c>，
    /// <c>!fromStart</c> 分支不可达 —— 0.10.5 删掉该参数并把方法名说清意图。
    /// </summary>
    private void MoveByFromStart(int dx, int dy)
    {
        if (!Exists)
        {
            return;
        }

        var x = Math.Clamp(_startBounds.X + dx, Limits.Left, Math.Max(Limits.Left, Limits.Right - _startBounds.Width));
        var y = Math.Clamp(_startBounds.Y + dy, Limits.Top, Math.Max(Limits.Top, Limits.Bottom - _startBounds.Height));
        Bounds = new Rect(x, y, _startBounds.Width, _startBounds.Height);
    }

    /// <summary>
    /// 把 l/t/r/b 夹进 Limits 并保证 ≥1px（吸附关闭时走这条老路径，行为与 0.8.5 完全一致）。
    /// 夹紧算法与最小尺寸只有一份出处：<see cref="MinimumSizes.ClampToLimits"/>，
    /// 与 <c>SnapGeometry.ClampToLimits</c> 共用；两者最后的收口策略由参数区分
    /// （这里 <c>truncateToLimits: false</c>，保证结果永远 ≥1px）。
    /// </summary>
    private Rect ClampResize(double l, double t, double r, double b)
        => MinimumSizes.ClampToLimits(
            l,
            t,
            r,
            b,
            Limits,
            MinimumSizes.SupportPixels,
            truncateToLimits: false);

    /// <summary>命中测试（物理像素，<paramref name="tolerance"/> 为控制点容差，通常 6~8px）。</summary>
    public SelectionHandle HitTest(int physicalX, int physicalY, int tolerance)
    {
        if (!Exists)
        {
            return SelectionHandle.None;
        }

        var x = (double)physicalX;
        var y = (double)physicalY;
        var l = Bounds.Left;
        var t = Bounds.Top;
        var r = Bounds.Right;
        var b = Bounds.Bottom;

        var nearL = Math.Abs(x - l) <= tolerance;
        var nearR = Math.Abs(x - r) <= tolerance;
        var nearT = Math.Abs(y - t) <= tolerance;
        var nearB = Math.Abs(y - b) <= tolerance;
        var withinX = x >= l - tolerance && x <= r + tolerance;
        var withinY = y >= t - tolerance && y <= b + tolerance;

        if (nearL && nearT) return SelectionHandle.NW;
        if (nearR && nearT) return SelectionHandle.NE;
        if (nearL && nearB) return SelectionHandle.SW;
        if (nearR && nearB) return SelectionHandle.SE;
        if (nearL && withinY) return SelectionHandle.W;
        if (nearR && withinY) return SelectionHandle.E;
        if (nearT && withinX) return SelectionHandle.N;
        if (nearB && withinX) return SelectionHandle.S;
        if (x > l && x < r && y > t && y < b) return SelectionHandle.Inside;

        return SelectionHandle.None;
    }

    /// <summary>把任意两个角点规范成矩形。</summary>
    public static Rect Normalize(int x0, int y0, int x1, int y1)
    {
        var left = Math.Min(x0, x1);
        var top = Math.Min(y0, y1);
        var width = Math.Abs(x1 - x0);
        var height = Math.Abs(y1 - y0);
        return new Rect(left, top, width, height);
    }

    /// <summary>裁剪到边界内（保持左上级，必要时收缩尺寸）。</summary>
    public static Rect ClampTo(Rect rect, Rect limits)
    {
        var left = Math.Max(rect.Left, limits.Left);
        var top = Math.Max(rect.Top, limits.Top);
        var right = Math.Min(rect.Right, limits.Right);
        var bottom = Math.Min(rect.Bottom, limits.Bottom);

        if (right <= left || bottom <= top)
        {
            return Rect.Empty;
        }

        return new Rect(left, top, right - left, bottom - top);
    }
}
