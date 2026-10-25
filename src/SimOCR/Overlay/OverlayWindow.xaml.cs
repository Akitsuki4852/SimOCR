using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SimOCR.Capture;
using SimOCR.Diagnostics;
using SimOCR.Interop;
using SimOCR.Settings;

namespace SimOCR.Overlay;

internal enum OverlayActionKind
{
    Cancel,
    Save,
    Copy,
    Pin,

    /// <summary>V1：四个导出动作 —— 对当前选区做 OCR，打开识别结果窗口并导出对应格式。</summary>
    OcrTxt,
    OcrDocx,
    OcrPdf,
    OcrXlsx,
}

internal sealed class OverlayActionRequestedEventArgs : EventArgs
{
    public OverlayActionRequestedEventArgs(OverlayActionKind kind, BitmapSource? image)
    {
        Kind = kind;
        Image = image;
    }

    public OverlayActionKind Kind { get; }

    /// <summary>裁切好的选区位图（Cancel 时为 null）。</summary>
    public BitmapSource? Image { get; }
}

/// <summary>
/// 全屏覆盖层：显示冻结帧、遮罩、选区、尺寸标签、放大镜与右下角工具条。
/// 所有几何计算在物理像素上进行，渲染时才换算成 DIP。
/// </summary>
internal partial class OverlayWindow : Window
{
    private const double HandleSizeDip = 9;
    private const double ToolbarGapDip = 6;
    private const double MagnifierOffsetDip = 18;
    private const double HintTopDip = 24;
    private const string DefaultHintText = "左键拖拽框选  ·  Esc 取消  ·  C 复制色值  ·  方向键微调";
    private const string HoverHintText = "单击抓取整个窗口（拖动可自选区域）";

    private readonly CapturedFrame _frame;
    private readonly AppSettings _settings;
    private readonly LogService _log;
    private readonly SelectionModel _selection = new();
    private readonly List<Rectangle> _handles = [];

    // ------- 窗口吸附 -------
    /// <summary>候选窗口（z 序，自上而下），每次截图只枚举一次。</summary>
    private IReadOnlyList<WindowCandidate> _candidates = [];

    // 吸附目标列表与"这条边露出来了吗"过去各另存一份字段
    //（`_snapTargets` / `_snapVisibility`），可它们与 <see cref="SnapContext.Targets"/> /
    // <see cref="SnapContext.Visibility"/> 是同一次枚举的同一个值 —— 现在只写在 SnapContext 上。
    private readonly SnapContext _snapContext = new([], 0);
    private bool _snapEnumerated;
    private WindowCandidate? _hoverWindow;
    private Rect? _pressedWindowBounds;
    private readonly List<Line> _guideLines = [];
    private IReadOnlyList<SnapGuide> _lastDragGuides = [];
    private string _lastGuideSignature = string.Empty;

    private double _scale = 1.0;
    private bool _dragging;

    // 工具条可见性**只有一个真源** = ToolbarPanel.Visibility。
    // 过去这里另存一个 _toolbarVisible 布尔，与 XAML 元素的 Visibility 两处各写一遍。
    private bool _closing;

    /// <summary>
    /// 显示后是否允许本窗**抢焦点**（<c>false</c> = 自检与生产预热这类"不打扰用户"的场景）。
    ///
    /// **0.10.5 解耦**：过去这个状态叫 <c>_testMode</c>，而生产预热
    /// （<c>AppComposition.WarmUpOverlay</c>）必须借道"测试模式"才能不抢焦点 —— 生产功能依赖测试标志。
    /// 现在参数按**意图**命名：自检侧显式传 <c>activateOnShow: false</c>，生产预热走
    /// <c>CaptureSessionController.CreateOverlay(frame, warmupOffscreen: true)</c>，两者不再共用一个含义。
    /// </summary>
    private readonly bool _activateOnShow;
    private SelectionHandle _hoverHandle = SelectionHandle.None;
    private int _cursorPhysX;
    private int _cursorPhysY;

    /// <summary>从构造开始的计时（用于量"热键→首帧"，冷/热启动对比都靠它）。</summary>
    private readonly Stopwatch _showWatch = Stopwatch.StartNew();
    private bool _firstFrameLogged;

    /// <summary>
    /// **实测探针**：本进程里"覆盖层还挂在 <see cref="CompositionTarget.Rendering"/>（**静态**事件）上"
    /// 的个数。每次订阅 +1、每次退订 −1 —— `CompositionTarget.Rendering` 是静态事件，**没有公开的
    /// 订阅者计数**（`GetInvocationList()` 对静态事件抛 `InvalidOperationException`），
    /// 所以这里用配对计数把"有没有漏退订"变成一个可读的数字。
    /// </summary>
    private static int _renderingSubscribers;

    /// <summary>首帧回调的委托（必须是字段，才能在关闭路径上退订同一个委托实例）。</summary>
    private EventHandler? _firstRenderingHandler;

    /// <summary>Loaded 耗时（ms），供首帧回调把"其中 Loaded xx ms"写进日志。</summary>
    private long _loadedMs;

    internal static int TestRenderingSubscribers => _renderingSubscribers;

    public OverlayWindow(
        CapturedFrame frame,
        AppSettings settings,
        LogService log,
        bool activateOnShow = true)
    {
        _frame = frame;
        _settings = settings;
        _log = log;
        _activateOnShow = activateOnShow;

        InitializeComponent();

        // **先贴帧、再显示**：这是"F1 时黑屏闪一下"的正面修复，详见 PrepareFrameVisuals 的注释。
        PrepareFrameVisuals();

        _selection.Limits = frame.VirtualScreen;

        // 窗口吸附：候选窗口在**覆盖层第一次可见之前**就枚举好（此时本进程还没有窗口，靠 PID 也照样排除），
        // 因此第一次鼠标移动就能立刻高亮，不需要等枚举。关闭吸附时（设置里关掉 / 容差 0）这一整段不发生。
        RefreshSnapState();

        _selection.Snap = _snapContext;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        MouseMove += OnMouseMove;
        PreviewMouseLeftButtonDown += OnLeftButtonDown;
        PreviewMouseLeftButtonUp += OnLeftButtonUp;
        PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; RaiseAction(OverlayActionKind.Cancel); };
        MouseDown += OnMouseDownOtherButtons;
        MouseDoubleClick += OnMouseDoubleClick;
    }

    public event EventHandler<OverlayActionRequestedEventArgs>? ActionRequested;

    /// <summary>选区（物理像素），供日志与测试使用。</summary>
    public Rect SelectionBounds => _selection.Bounds;

    private double FrameDipWidth => _frame.PixelWidth / _scale;

    private double FrameDipHeight => _frame.PixelHeight / _scale;

    // ------------------------------------------------------------------ 初始化

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0)
        {
            _scale = 1.0;
        }

        // 构造期还不知道 DPI，PrepareFrameVisuals 里用的是 1.0；这里拿到真实 DPI 后重算一次图片尺寸
        //（物理像素 1:1 是硬要求）。100% 缩放下这次重算与构造期等价。
        RenderFrame();

        // 在窗口**第一次可见之前**就把它摆到整屏并置顶。
        // 原来这一步在 Loaded 里，而 Loaded 之前窗口已经可见 —— 窗口又没在 XAML 里设 Width/Height，
        // 于是会先闪一个"左上空的小黑窗"，再跳到全屏。
        PositionOverWindow();

        _log.Debug($"覆盖层：HWND 就绪 {_showWatch.ElapsedMilliseconds}ms（DPI 缩放 {_scale:F3}）");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 冻结帧与窗口背景已在构造期贴好（PrepareFrameVisuals），这里只补交互相关的东西。
        BuildHandles();
        BuildMagnifierGrid();
        UpdateHint();

        // 窗口吸附：第一次可见时就把光标下的窗口高亮出来（不用等第一次鼠标移动）
        RefreshSnapState();
        var cursor = MonitorLayout.GetCursorPosition();
        _cursorPhysX = cursor.X;
        _cursorPhysY = cursor.Y;
        UpdateHover(cursor.X, cursor.Y);

        Activate();
        Focus();

        _loadedMs = _showWatch.ElapsedMilliseconds;
        _log.Debug($"覆盖层：显示→已加载 {_loadedMs}ms");

        // Loaded 之后的第一次合成 = 用户真正看到冻结帧的那一刻（诊断冷/热启动用）。
        // 过去这里是**局部函数**，退订只写在它自己内部 —— 于是"Show 之后立刻 Close"
        // 的形状（`AppComposition.WarmUpOverlay` 的启动预热、以及自检反复新建覆盖层）会在
        // **一次合成回调都还没落地**时就把窗口关掉：静态事件仍然持有那个闭包 → 持有窗口 →
        // 持有冻结帧（1920×1080 约 7.9 MB）直到进程结束。现在它是有名字的**实例方法**
        // （`OnFirstRendering`），退订点从 1 处变成 3 处（首帧 / CloseOverlay / OnClosed）。
        _firstRenderingHandler = OnFirstRendering;
        CompositionTarget.Rendering += _firstRenderingHandler;
        _renderingSubscribers++;
    }

    /// <summary>首帧回调（从局部函数提升为实例方法，便于在关闭路径上退订）。</summary>
    private void OnFirstRendering(object? sender, EventArgs e)
    {
        UnsubscribeFirstRendering();
        if (_firstFrameLogged)
        {
            return;
        }

        _firstFrameLogged = true;
        _log.Debug($"覆盖层：显示→首帧 {_showWatch.ElapsedMilliseconds}ms（其中 Loaded {_loadedMs}ms）");
    }

    /// <summary>退订首帧回调（幂等）。**静态**事件必须显式退订，否则事件源会一直持有本窗口。</summary>
    private void UnsubscribeFirstRendering()
    {
        if (_firstRenderingHandler is null)
        {
            return;
        }

        CompositionTarget.Rendering -= _firstRenderingHandler;
        _firstRenderingHandler = null;
        _renderingSubscribers--;
    }

    /// <summary>
    /// **在窗口可见之前**把冻结帧铺好 —— 这是"每次 F1 都闪一下黑屏（首次尤其明显）"的正面修复。
    ///
    /// 原来 <see cref="RenderFrame"/> 只在 <c>OnLoaded</c> 里跑，而窗口在 <c>ShowDialog()</c> 时就已可见：
    /// 于是每次都会先用 XAML 里的 <c>Background="#FF101010"</c> 把整屏涂黑一瞬，等 Loaded 跑完才换成截图。
    /// 首次调用还要额外付 BAML 解析 / JIT / D3D 初始化 / 1920×1080 位图上传的代价，黑屏就格外长。
    ///
    /// 两步一起做才彻底：
    /// 1. 构造期就贴 <c>FrameImage.Source</c> 与尺寸；
    /// 2. 把**窗口自身的背景**也设成同一张冻结帧（<see cref="ImageBrush"/>，Stretch=Fill）——
    ///    即使第一帧只画了背景、子元素还没合成，用户看到的也是截图本身，而不是纯黑。
    /// </summary>
    private void PrepareFrameVisuals()
    {
        RenderFrame();

        Background = new ImageBrush(_frame.Bitmap)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }

    /// <summary>用物理像素把窗口铺满整个虚拟桌面（不能用 WPF 的 Left/Top，那是 DIP）。</summary>
    private void PositionOverWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var vs = _frame.VirtualScreen;

        var ok = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            (int)Math.Round(vs.X),
            (int)Math.Round(vs.Y),
            (int)Math.Round(vs.Width),
            (int)Math.Round(vs.Height),
            NativeMethods.SWP_SHOWWINDOW);

        if (!ok)
        {
            _log.Warn("覆盖层 SetWindowPos 失败");
        }
    }

    private void RenderFrame()
    {
        FrameImage.Source = _frame.Bitmap;
        FrameImage.Width = FrameDipWidth;
        FrameImage.Height = FrameDipHeight;

        // 1 物理像素 = 1 屏幕像素：绝不能被平滑插值
        RenderOptions.SetBitmapScalingMode(FrameImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(FrameImage, EdgeMode.Aliased);
    }

    private void BuildHandles()
    {
        HandlesLayer.Children.Clear();
        _handles.Clear();

        for (var i = 0; i < 8; i++)
        {
            var rect = new Rectangle
            {
                Width = HandleSizeDip,
                Height = HandleSizeDip,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            _handles.Add(rect);
            HandlesLayer.Children.Add(rect);
        }
    }

    private void BuildMagnifierGrid()
    {
        // 这层格子线只被 Clear/Add 过、从没被枚举过（真源就是
        // MagnifierGridLayer.Children），中心格也只在方法内用 —— 两者都改成局部/元素树本身。
        MagnifierGridLayer.Children.Clear();

        var sample = EffectiveSampleSize();
        var zoom = Math.Clamp(_settings.Overlay.MagnifierZoom, 2, 32);
        var cellDip = zoom / _scale;
        var totalDip = sample * cellDip;

        for (var i = 0; i <= sample; i++)
        {
            var offset = i * cellDip;

            var v = new Line
            {
                X1 = offset, Y1 = 0, X2 = offset, Y2 = totalDip,
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
                StrokeThickness = 1,
            };
            var h = new Line
            {
                X1 = 0, Y1 = offset, X2 = totalDip, Y2 = offset,
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
                StrokeThickness = 1,
            };

            MagnifierGridLayer.Children.Add(v);
            MagnifierGridLayer.Children.Add(h);
        }

        var half = (sample - 1) / 2.0;
        var centerCell = new Rectangle
        {
            Width = Math.Max(1, cellDip),
            Height = Math.Max(1, cellDip),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(centerCell, half * cellDip);
        Canvas.SetTop(centerCell, half * cellDip);
        MagnifierGridLayer.Children.Add(centerCell);
    }

    private int EffectiveSampleSize()
    {
        var sample = Math.Clamp(_settings.Overlay.MagnifierSampleSize, 5, 51);
        return sample % 2 == 0 ? sample + 1 : sample;
    }

    private void UpdateHint() => SetHintText(DefaultHintText);

    private void SetHintText(string text)
    {
        HintText.Text = text;

        HintPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HintPanel.DesiredSize;
        Canvas.SetLeft(HintPanel, Math.Max(0, (FrameDipWidth - size.Width) / 2));
        Canvas.SetTop(HintPanel, HintTopDip);
        HintPanel.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------ 窗口吸附

    /// <summary>当前生效的吸附容差（0 = 关闭）。每帧从设置读取，所以改设置立即生效。</summary>
    private int EffectiveSnapTolerance()
        => _settings.Behavior.SnapToWindows ? Math.Clamp(_settings.Behavior.SnapTolerancePx, 0, 40) : 0;

    /// <summary>把设置里的容差同步给选区模型（会话中途关掉吸附时立刻不再吸附/画导轨）。</summary>
    private void RefreshSnapState()
    {
        // 先写容差再枚举：日志里那句"容差 Npx"要反映真正生效的值
        _snapContext.Tolerance = EffectiveSnapTolerance();

        // 会话中途才打开吸附 / 构造时没枚举（当时是关的）：这里补一次，且只补一次
        if (_snapContext.Tolerance > 0 && !_snapEnumerated)
        {
            EnumerateSnapTargets();
        }
    }

    /// <summary>
    /// 枚举吸附候选：候选窗口 + 各显示器边框 + 虚拟桌面边框。
    /// **每次截图只做一次**（冻结帧让几何静止），且必须在覆盖层第一次可见之前完成 → 第一次悬停就是即时的。
    ///
    /// 遮盖裁剪（缺陷修复）：枚举出来的候选里，**完全被更高 z 序窗口盖住**的直接丢掉，
    /// 剩下的每个候选还要带一份"露出来的矩形片段"，吸附时只认这些片段里的边
    /// （否则会吸到屏幕上看不见的线 —— 用户报告的"吸附被遮盖的窗口边缘"）。
    /// </summary>
    private void EnumerateSnapTargets()
    {
        _snapEnumerated = true;

        var raw = WindowBounds.EnumerateRaw();
        var (visibleCandidates, occluded) = WindowBounds.DropFullyOccluded(raw.Candidates);
        _candidates = visibleCandidates;

        // **0.10.0（提前落地的一半）**：targets 必须用**未过滤**的 raw.Candidates 构建。
        // BuildVisibility 按**下标**把 candidates[i] 与 targets[i] 配对（见 WindowBounds 的约定），
        // 过去这里用 visibleCandidates（DropFullyOccluded 的**过滤**结果，不重排）建 targets，
        // 却把 raw.Candidates 交给 BuildVisibility —— 只要 occluded > 0 下标就整体错位，
        // 一部分**其实可见**的窗口反而查不到索引，落到"不在索引里=永远可见"的兜底，
        // 于是又能吸到屏幕上被遮盖的边（用户报告的那个缺陷）。
        // 顺序与过滤前一致：窗口（z 序）→ 显示器 → 虚拟桌面；构建点统一在 WindowBounds.BuildSnapTargets，
        // 这样自检与生产用的是同一份列表语义（回归用例 23a~23d 走的是同一个方法）。
        var targets = WindowBounds.BuildSnapTargets(
            raw.Candidates,
            [.. _frame.Monitors.Select(m => m.PhysicalBounds)],
            _frame.VirtualScreen);

        // 可见性必须用**原始（未丢遮盖）**的 z 序列表算：被丢掉的那几个也有可能是别人的遮盖物
        var visibility = WindowBounds.BuildVisibility(raw.Candidates, targets, _log);

        // 选区模型读的就是这个上下文，目标必须挂上去，否则 Enabled=false（等于没开吸附）；
        // 这两个值过去还要各搬一份到实例字段，现在只落在 SnapContext（唯一真源）。
        _snapContext.Targets = targets;
        _snapContext.Visibility = visibility;

        var stats = raw.Stats with { Occluded = occluded };
        _log.Info($"窗口吸附：候选窗口 {raw.Candidates.Count} → {visibleCandidates.Count} 个" +
                  $"（枚举 {raw.ElapsedMs}ms，容差 {_snapContext.Tolerance}px，整窗被遮盖丢掉 {occluded} 个）—— {stats.Describe()}");

        foreach (var candidate in visibleCandidates.Take(5))
        {
            _log.Debug($"  候选：{candidate.Describe()}");
        }
    }

    /// <summary>悬停高亮：光标下**最上层**的候选窗口（候选列表本身就是 z 序）。</summary>
    private void UpdateHover(int physicalX, int physicalY)
    {
        var candidate = _snapContext.Enabled
            ? WindowBounds.TopmostAt(_candidates, physicalX, physicalY)
            : null;

        if (candidate?.Hwnd == _hoverWindow?.Hwnd)
        {
            return;
        }

        _hoverWindow = candidate;
        RedrawHover();

        if (candidate is { } window)
        {
            _log.Info($"窗口吸附：悬停高亮 {window.Describe()}");
        }
    }

    private void RedrawHover()
    {
        if (_hoverWindow is not { } window || _selection.Exists || _dragging)
        {
            HoverOutline.Visibility = Visibility.Collapsed;

            if (!_selection.Exists && !_dragging)
            {
                UpdateHint();   // 离开窗口 → 恢复默认提示
            }

            return;
        }

        var rect = ToDipRect(window.Bounds);
        Canvas.SetLeft(HoverOutline, rect.Left);
        Canvas.SetTop(HoverOutline, rect.Top);
        HoverOutline.Width = Math.Max(1, rect.Width);
        HoverOutline.Height = Math.Max(1, rect.Height);
        HoverOutline.Visibility = Visibility.Visible;

        SetHintText(HoverHintText);
        ShowHoverSizeLabel(window.Bounds);
    }

    /// <summary>悬停时用同一个尺寸标签显示"这扇窗的尺寸"（与选区共用，互斥出现）。</summary>
    private void ShowHoverSizeLabel(Rect physicalBounds)
    {
        if (!_settings.Overlay.ShowSizeLabel)
        {
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        SizeLabelText.Text = $"{(int)Math.Round(physicalBounds.Width)} × {(int)Math.Round(physicalBounds.Height)}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var placed = OverlayPlacement.PlaceSizeLabelWithFlip(
            ToDipRect(physicalBounds),
            SizeLabel.DesiredSize,
            new Size(FrameDipWidth, FrameDipHeight),
            gap: 6);

        Canvas.SetLeft(SizeLabel, placed.Rect.X);
        Canvas.SetTop(SizeLabel, placed.Rect.Y);
    }

    private void ClearHover()
    {
        _hoverWindow = null;
        HoverOutline.Visibility = Visibility.Collapsed;

        if (!_selection.Exists)
        {
            UpdateHint();
        }
    }

    /// <summary>把当前吸附导轨画出来（竖线/横线各一条对应一个坐标），并淡淡勾出被吸附的窗口。</summary>
    private void UpdateGuideVisuals()
    {
        var guides = _selection.Guides;

        if (guides.Count == 0 || !_snapContext.Enabled)
        {
            ClearGuides();
            return;
        }

        EnsureGuideLines(guides.Count);

        for (var i = 0; i < _guideLines.Count; i++)
        {
            var line = _guideLines[i];
            if (i >= guides.Count)
            {
                line.Visibility = Visibility.Collapsed;
                continue;
            }

            var guide = guides[i];
            var coordinate = guide.Axis == SnapAxis.Vertical
                ? (guide.Coordinate - _frame.VirtualScreen.X) / _scale
                : (guide.Coordinate - _frame.VirtualScreen.Y) / _scale;

            // 被遮盖的窗口边只画"露出来"的那一段（guide 自带可见范围；没有就整条画满）
            var spanStart = 0.0;
            var spanEnd = guide.Axis == SnapAxis.Vertical ? FrameDipHeight : FrameDipWidth;

            if (guide.HasSpan)
            {
                var visibleStart = guide.SpanStart;
                var visibleEnd = guide.SpanEnd;

                if (guide.Axis == SnapAxis.Vertical)
                {
                    spanStart = Math.Max(0, (visibleStart - _frame.VirtualScreen.Y) / _scale);
                    spanEnd = Math.Min(FrameDipHeight, (visibleEnd - _frame.VirtualScreen.Y) / _scale);
                }
                else
                {
                    spanStart = Math.Max(0, (visibleStart - _frame.VirtualScreen.X) / _scale);
                    spanEnd = Math.Min(FrameDipWidth, (visibleEnd - _frame.VirtualScreen.X) / _scale);
                }

                if (spanEnd <= spanStart)
                {
                    line.Visibility = Visibility.Collapsed;
                    continue;
                }
            }

            if (guide.Axis == SnapAxis.Vertical)
            {
                line.X1 = coordinate;
                line.X2 = coordinate;
                line.Y1 = spanStart;
                line.Y2 = spanEnd;
            }
            else
            {
                line.X1 = spanStart;
                line.X2 = spanEnd;
                line.Y1 = coordinate;
                line.Y2 = coordinate;
            }

            line.Visibility = Visibility.Visible;
        }

        // 可选反馈：把吸到的那扇窗淡淡勾一下，让用户知道"吸附理由"
        var outlineSet = false;
        foreach (var guide in guides)
        {
            if (guide.Kind != SnapTargetKind.Window || guide.TargetBounds.Width <= 0 || guide.TargetBounds.Height <= 0)
            {
                continue;
            }

            var target = ToDipRect(guide.TargetBounds);
            Canvas.SetLeft(SnapTargetOutline, target.Left);
            Canvas.SetTop(SnapTargetOutline, target.Top);
            SnapTargetOutline.Width = Math.Max(1, target.Width);
            SnapTargetOutline.Height = Math.Max(1, target.Height);
            SnapTargetOutline.Visibility = Visibility.Visible;
            outlineSet = true;
            break;
        }

        if (!outlineSet)
        {
            SnapTargetOutline.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsureGuideLines(int count)
    {
        while (_guideLines.Count < Math.Min(count, 4))
        {
            var line = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xA0, 0xFF)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            _guideLines.Add(line);
            SnapGuideLayer.Children.Add(line);
        }
    }

    private void ClearGuides()
    {
        foreach (var line in _guideLines)
        {
            line.Visibility = Visibility.Collapsed;
        }

        SnapTargetOutline.Visibility = Visibility.Collapsed;
    }

    /// <summary>拖拽中导轨集合变了就记一条日志（验收就是靠它确认"确实吸上了"）。</summary>
    private void LogGuides()
    {
        var guides = _selection.Guides;
        var signature = string.Join(",", guides.Select(g => g.ToString()));

        if (string.Equals(signature, _lastGuideSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastGuideSignature = signature;
        _lastDragGuides = guides.Count == 0 ? [] : guides.ToList();

        if (guides.Count == 0)
        {
            _log.Debug("窗口吸附：无吸附（超出容差）");
            return;
        }

        _log.Info($"窗口吸附：吸附 {signature}  选区={_selection.X},{_selection.Y} {_selection.Width}x{_selection.Height}");
    }

    private Rect ToDipRect(Rect physical)
        => new(ToDip(physical.Left, physical.Top), ToDip(physical.Right, physical.Bottom));

    // ------------------------------------------------------------- 坐标换算

    private Point ToDip(double physicalX, double physicalY)
        => new((physicalX - _frame.VirtualScreen.X) / _scale, (physicalY - _frame.VirtualScreen.Y) / _scale);

    private (int X, int Y) ToPhysical(Point dip)
        => ((int)Math.Round(_frame.VirtualScreen.X + (dip.X * _scale)),
            (int)Math.Round(_frame.VirtualScreen.Y + (dip.Y * _scale)));

    // ---------------------------------------------------------------- 鼠标

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var dip = e.GetPosition(this);
        var (px, py) = ToPhysical(dip);


        if (_dragging)
        {
            DragTo(px, py);
            return;
        }

        _cursorPhysX = px;
        _cursorPhysY = py;

        if (ToolbarPanel.Visibility == Visibility.Visible)
        {
            ClearHover();
            return;
        }

        // 悬停时的光标反馈
        var handle = _selection.HitTest(px, py, HitTolerance());
        if (handle != _hoverHandle)
        {
            _hoverHandle = handle;
            Cursor = handle switch
            {
                SelectionHandle.NW or SelectionHandle.SE => Cursors.SizeNWSE,
                SelectionHandle.NE or SelectionHandle.SW => Cursors.SizeNESW,
                SelectionHandle.N or SelectionHandle.S => Cursors.SizeNS,
                SelectionHandle.W or SelectionHandle.E => Cursors.SizeWE,
                SelectionHandle.Inside => Cursors.SizeAll,
                _ => Cursors.Cross,
            };
        }

        RefreshSnapState();

        // 窗口吸附：还没开始拖拽时，高亮光标下最上层的候选窗口（没有候选就什么都不画 = 旧行为）
        if (!_selection.Exists || _selection.IsTooSmall)
        {
            UpdateHover(px, py);
        }
        else
        {
            ClearHover();
        }

        UpdateMagnifier(px, py);
    }

    /// <summary>拖拽中把光标移到某个物理像素点（鼠标事件与自检钩子共用同一条路径）。</summary>
    private void DragTo(int px, int py)
    {
        _cursorPhysX = px;
        _cursorPhysY = py;
        RefreshSnapState();
        _selection.Update(px, py);
        LogGuides();
        RedrawSelection();
        UpdateMagnifier(px, py);
    }

    private int HitTolerance() => (int)Math.Ceiling(6 * _scale);

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinToolbar(e.OriginalSource as DependencyObject))
        {
            return; // 让按钮自己处理
        }

        Focus();
        var dip = e.GetPosition(this);
        var (px, py) = ToPhysical(dip);

        BeginDragAt(px, py);
        e.Handled = true;

    }

    /// <summary>按下左键的全部逻辑（物理像素）。鼠标事件与自检钩子共用，保证验的就是真实路径。</summary>
    private void BeginDragAt(int px, int py)
    {
        RefreshSnapState();
        _pressedWindowBounds = null;

        // 按下瞬间以真实坐标为准再算一次悬停候选（窗口可能在光标下出现，而鼠标一直没动过）
        if (!_selection.Exists && _snapContext.Enabled)
        {
            _hoverWindow = WindowBounds.TopmostAt(_candidates, px, py);
        }

        var handle = _selection.HitTest(px, py, HitTolerance());

        if (_selection.Exists && handle is SelectionHandle.Inside)
        {
            _selection.BeginMove(px, py);
        }
        else if (_selection.Exists && handle is not SelectionHandle.None and not SelectionHandle.Inside && _settings.Overlay.AllowResizeSelection)
        {
            _selection.BeginResize(handle, px, py);
        }
        else
        {
            // 点击框外：开始新的框选。
            // 按下时如果光标下有高亮窗口，就记下来 —— 松开时若"没有实质拖拽"，这一下就是
            // "单击抓取整个窗口"，而不是旧的"选区过小 → 取消"。
            _pressedWindowBounds = _hoverWindow?.Bounds;
            _selection.BeginCreate(px, py);
            HideToolbar();
        }

        _dragging = true;
        HideHint();   // = HintPanel.Visibility = Collapsed（0.10.5 删掉了紧跟其后的重复赋值）

        // 悬停高亮让位给选区/导轨
        _hoverWindow = null;
        HoverOutline.Visibility = Visibility.Collapsed;

        RedrawSelection();
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        EndDragAt();
    }

    /// <summary>松开左键的全部逻辑（与 <see cref="BeginDragAt"/> 配对）。</summary>
    private void EndDragAt()
    {
        _dragging = false;
        _selection.EndDrag();

        if (_selection.IsTooSmall)
        {
            // 窗口吸附：按下时高亮过窗口 → 这一下点击就是"抓取整个窗口"，走既有的动作链
            //（选区 + 工具条，用户可以继续 保存/复制/贴图/OCR）。
            if (_pressedWindowBounds is { } windowBounds && _selection.SetBounds(windowBounds))
            {
                _pressedWindowBounds = null;
                _log.Info($"窗口吸附：单击抓取整个窗口 {_selection.X},{_selection.Y} {_selection.Width}x{_selection.Height}");

                if (_settings.Save.AutoSaveAfterRelease)
                {
                    RaiseAction(OverlayActionKind.Save);
                    return;
                }

                RedrawSelection();
                ShowToolbar();
                MagnifierPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // 光标下没有窗口：保持旧行为（误触 → 取消）
            _pressedWindowBounds = null;
            _log.Info("选区过小，按取消处理");
            RaiseAction(OverlayActionKind.Cancel);
            return;
        }

        _pressedWindowBounds = null;

        if (_settings.Save.AutoSaveAfterRelease)
        {
            RaiseAction(OverlayActionKind.Save);
            return;
        }

        RedrawSelection();
        ShowToolbar();
        MagnifierPanel.Visibility = Visibility.Collapsed;
    }

    private void OnMouseDownOtherButtons(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle)
        {
            e.Handled = true;
            RaiseAction(OverlayActionKind.Cancel);
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinToolbar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (!_selection.Exists || _selection.IsTooSmall)
        {
            return;
        }

        var dip = e.GetPosition(this);
        var (px, py) = ToPhysical(dip);
        if (_selection.HitTest(px, py, HitTolerance()) != SelectionHandle.Inside)
        {
            return;
        }

        // 双击默认"复制到剪贴板"（不保存），可在设置里改成保存
        var action = string.Equals(_settings.Overlay.DoubleClickAction, "save", StringComparison.OrdinalIgnoreCase)
            ? OverlayActionKind.Save
            : OverlayActionKind.Copy;

        e.Handled = true;
        RaiseAction(action);
    }

    private static bool IsWithinToolbar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border { Name: "ToolbarPanel" })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    // ---------------------------------------------------------------- 键盘

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var plain = Keyboard.Modifiers == ModifierKeys.None;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                RaiseAction(OverlayActionKind.Cancel);
                return;

            case Key.Enter when _selection.Exists && !_selection.IsTooSmall:
                e.Handled = true;
                RaiseAction(OverlayActionKind.Save);
                return;

            case Key.C when ctrl:
                e.Handled = true;
                RaiseAction(OverlayActionKind.Copy);
                return;

            case Key.T when ctrl:
                e.Handled = true;
                RaiseAction(OverlayActionKind.Pin);
                return;

            case Key.C when plain:
                e.Handled = true;
                CopyColorUnderCursor();
                return;

            case Key.A when ctrl:
                e.Handled = true;
                SelectAll();
                return;

            case Key.Left or Key.Right or Key.Up or Key.Down when _selection.Exists:
                e.Handled = true;
                var step = shift ? 10 : 1;
                var dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                var dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                _selection.MoveBy(dx, dy);
                RedrawSelection();
                return;
        }
    }

    private void SelectAll()
    {
        _selection.BeginCreate((int)Math.Round(_frame.VirtualScreen.Left), (int)Math.Round(_frame.VirtualScreen.Top));
        _selection.Update((int)Math.Round(_frame.VirtualScreen.Right), (int)Math.Round(_frame.VirtualScreen.Bottom));
        _selection.EndDrag();
        HideHint();
        RedrawSelection();
        ShowToolbar();
    }

    private void CopyColorUnderCursor()
    {
        var c = _frame.SamplePixel(_cursorPhysX, _cursorPhysY);
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (Storage.ClipboardExporter.CopyText(hex, _log))
        {
            MagnifierColorText.Text = $"RGB {c.R}, {c.G}, {c.B}   {hex}   (已复制)";
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (!_activateOnShow)
        {
            return; // 不抢焦点（自检 / 生产预热），免得干扰正常使用
        }

        // 覆盖层必须拿到键盘焦点；被别的窗口抢走时抢回来（不关闭会话）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !IsVisible)
            {
                return;
            }

            Activate();
            Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------------------------------------------------------------- 自检钩子
    // 这些方法只走"鼠标/键盘处理里真正调用的那条路径"，因此能验证：
    // 选区模型 → 渲染 → 工具条摆放 → 动作分发 → 保存/复制/贴图，全链路不崩且结果正确。

    internal bool TestToolbarVisible => ToolbarPanel.Visibility == Visibility.Visible;

    internal Rect TestToolbarBoundsDip => new(
        Canvas.GetLeft(ToolbarPanel),
        Canvas.GetTop(ToolbarPanel),
        ToolbarPanel.DesiredSize.Width,
        ToolbarPanel.DesiredSize.Height);

    internal Rect TestFrameDipBounds => new(0, 0, FrameDipWidth, FrameDipHeight);

    /// <summary>模拟一次"从左键按下到松开"的框选（物理像素）。</summary>
    internal void TestCreateSelection(int physicalX, int physicalY, int width, int height)
    {
        // 自检用的是合成帧，与真实桌面上的候选窗口没有对应关系（吸附会按真实窗口把尺寸改掉），
        // 所以这条钩子显式走"无吸附"的纯几何路径，保证 0.8.5 的端到端断言仍然逐像素可比。
        var savedSnap = _selection.Snap;
        _selection.Snap = SnapContext.Disabled;

        try
        {
            _selection.BeginCreate(physicalX, physicalY);
            _dragging = true;
            _selection.Update(physicalX + width, physicalY + height);
            RedrawSelection();

            _dragging = false;
            _selection.EndDrag();
            RedrawSelection();
        }
        finally
        {
            _selection.Snap = savedSnap;
        }

        if (_settings.Save.AutoSaveAfterRelease)
        {
            RaiseAction(OverlayActionKind.Save);
            return;
        }

        ShowToolbar();
        MagnifierPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>模拟点击工具条按钮 / 按快捷键。</summary>
    internal void TestRaiseAction(OverlayActionKind kind) => RaiseAction(kind);

    /// <summary>
    /// 自检/验收用：工具条上某个导出按钮的**屏幕坐标中心**（供 InputSimulator 打真实鼠标事件）。
    /// 按钮还没布局时退化为覆盖层中心，保证调用方总能拿到一个可点的坐标。
    /// </summary>
    internal Point TestExportButtonCenterScreen(OverlayActionKind kind)
    {
        var button = kind switch
        {
            OverlayActionKind.OcrTxt => OcrTxtButton,
            OverlayActionKind.OcrDocx => OcrDocxButton,
            OverlayActionKind.OcrPdf => OcrPdfButton,
            _ => OcrXlsxButton,
        };

        if (button.ActualWidth > 0 && button.ActualHeight > 0)
        {
            return button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        }

        return PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
    }

    /// <summary>模拟鼠标移动（用于放大镜路径）。</summary>
    internal void TestHover(int physicalX, int physicalY)
    {
        _cursorPhysX = physicalX;
        _cursorPhysY = physicalY;
        UpdateMagnifier(physicalX, physicalY);
    }

    internal bool TestMagnifierVisible => MagnifierPanel.Visibility == Visibility.Visible;

    // ------------------------------------------------- 窗口吸附自检/验收钩子

    internal int TestSnapTolerance => EffectiveSnapTolerance();

    internal int TestCandidateCount => _candidates.Count;

    internal Rect? TestHoverWindowBounds => _hoverWindow?.Bounds;

    internal string TestHoverWindowTitle => _hoverWindow?.Title ?? string.Empty;

    internal bool TestHoverOutlineVisible => HoverOutline.Visibility == Visibility.Visible;

    internal bool TestSnapOutlineVisible => SnapTargetOutline.Visibility == Visibility.Visible;

    internal string TestHintText => HintText.Text;

    internal string TestSizeLabelText => SizeLabelText.Text;

    internal int TestVisibleGuideCount => _guideLines.Count(line => line.Visibility == Visibility.Visible);

    /// <summary>当前生效的导轨（拖拽结束后被清空）。</summary>
    internal IReadOnlyList<SnapGuide> TestGuides => _selection.Guides;

    /// <summary>本次拖拽中最后被应用过的导轨（拖拽结束后仍保留，供验收断言）。</summary>
    internal IReadOnlyList<SnapGuide> TestLastDragGuides => _lastDragGuides;

    /// <summary>驱动一次"悬停窗口高亮"（与 OnMouseMove 里非拖拽分支走同一条路径）。</summary>
    internal void TestHoverWindow(int physicalX, int physicalY)
    {
        _cursorPhysX = physicalX;
        _cursorPhysY = physicalY;
        RefreshSnapState();
        UpdateHover(physicalX, physicalY);
    }

    /// <summary>
    /// 按下左键 / 拖到某点 / 松开左键的钩子 —— 与真实鼠标事件**共用同一段逻辑**
    /// （<see cref="BeginDragAt"/> / <see cref="DragTo"/> / <see cref="EndDragAt"/>）。
    /// 存在的理由：某些自动化环境（沙箱/低完整性令牌）里 SendInput 注入会被静默丢弃，
    /// 这时仍然要能验收"吸附几何"这条链路。
    /// </summary>
    internal void TestPressAt(int physicalX, int physicalY) => BeginDragAt(physicalX, physicalY);

    internal void TestDragTo(int physicalX, int physicalY) => DragTo(physicalX, physicalY);

    internal void TestReleaseAt() => EndDragAt();

    /// <summary>保存按钮的屏幕坐标中心（真实鼠标点它来落盘，验收用）。</summary>
    internal Point TestSaveButtonCenterScreen()
        => SaveButton.ActualWidth > 0 && SaveButton.ActualHeight > 0
            ? SaveButton.PointToScreen(new Point(SaveButton.ActualWidth / 2, SaveButton.ActualHeight / 2))
            : PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));

    // ---------------------------------------------------------------- 渲染

    private void RedrawSelection()
    {
        var fullRect = new Rect(0, 0, FrameDipWidth, FrameDipHeight);

        if (!_selection.Exists || _selection.IsTooSmall)
        {
            MaskPath.Data = new RectangleGeometry(fullRect);
            SelectionOutlineBlack.Visibility = Visibility.Collapsed;
            SelectionOutlineWhite.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            foreach (var h in _handles)
            {
                h.Visibility = Visibility.Collapsed;
            }

            // 拖拽刚起手（锚点已吸附、移动角还没离开锚点）时也要能看到锚点的导轨
            UpdateGuideVisuals();
            return;
        }

        var r = _selection.Bounds;
        var tl = ToDip(r.Left, r.Top);
        var br = ToDip(r.Right, r.Bottom);
        var rect = new Rect(tl, br);

        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(fullRect));
        group.Children.Add(new RectangleGeometry(rect));
        MaskPath.Data = group;

        SelectionOutlineBlack.Visibility = Visibility.Visible;
        SelectionOutlineWhite.Visibility = Visibility.Visible;

        foreach (var (element, left, top) in new (FrameworkElement, double, double)[]
                 {
                     (SelectionOutlineBlack, rect.Left, rect.Top),
                     (SelectionOutlineWhite, rect.Left, rect.Top),
                 })
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            element.Width = Math.Max(1, rect.Width);
            element.Height = Math.Max(1, rect.Height);
        }

        UpdateHandles(rect);
        UpdateSizeLabel(rect);
        UpdateGuideVisuals();
    }

    private void UpdateHandles(Rect rect)
    {
        if (!_settings.Overlay.AllowResizeSelection)
        {
            foreach (var h in _handles)
            {
                h.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var l = rect.Left;
        var t = rect.Top;
        var r = rect.Right;
        var b = rect.Bottom;
        var cx = (l + r) / 2;
        var cy = (t + b) / 2;

        var points = new (double X, double Y)[]
        {
            (l, t), (cx, t), (r, t),
            (l, cy), (r, cy),
            (l, b), (cx, b), (r, b),
        };

        for (var i = 0; i < _handles.Count && i < points.Length; i++)
        {
            var h = _handles[i];
            Canvas.SetLeft(h, points[i].X - (HandleSizeDip / 2));
            Canvas.SetTop(h, points[i].Y - (HandleSizeDip / 2));
            h.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSizeLabel(Rect rect)
    {
        if (!_settings.Overlay.ShowSizeLabel)
        {
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        SizeLabelText.Text = $"{_selection.Width} × {_selection.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var placed = OverlayPlacement.PlaceSizeLabelWithFlip(
            rect,
            SizeLabel.DesiredSize,
            new Size(FrameDipWidth, FrameDipHeight),
            gap: 6);

        Canvas.SetLeft(SizeLabel, placed.Rect.X);
        Canvas.SetTop(SizeLabel, placed.Rect.Y);
    }

    private void ShowToolbar()
    {
        ToolbarPanel.Visibility = Visibility.Visible;
        ToolbarPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var r = _selection.Bounds;
        var selectionDip = new Rect(ToDip(r.Left, r.Top), ToDip(r.Right, r.Bottom));
        var frame = new Size(FrameDipWidth, FrameDipHeight);

        // 翻转与否由 PlaceToolbar 一次判定并随结果带出：过去这里用 IsToolbarInside
        // 把同一个条件重算一遍，两个判据分头演进就会让日志说谎。
        var placed = OverlayPlacement.PlaceToolbarWithFlip(selectionDip, ToolbarPanel.DesiredSize, frame, ToolbarGapDip);

        Canvas.SetLeft(ToolbarPanel, placed.Rect.X);
        Canvas.SetTop(ToolbarPanel, placed.Rect.Y);

        _log.Debug($"工具条位置: {placed.Rect.X:F0},{placed.Rect.Y:F0} " +
                   $"({(placed.Flipped ? "框内" : "框外")})");
    }

    private void HideToolbar() => ToolbarPanel.Visibility = Visibility.Collapsed;

    private void HideHint() => HintPanel.Visibility = Visibility.Collapsed;

    private void UpdateMagnifier(int physicalX, int physicalY)
    {
        if (!_settings.Overlay.ShowMagnifier || ToolbarPanel.Visibility == Visibility.Visible)
        {
            MagnifierPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var sample = EffectiveSampleSize();
        var half = sample / 2;
        var crop = _frame.Crop(physicalX - half, physicalY - half, sample, sample);
        if (crop is null)
        {
            MagnifierPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var zoom = Math.Clamp(_settings.Overlay.MagnifierZoom, 2, 32);
        var cellDip = zoom / _scale;

        MagnifierImage.Source = crop;
        MagnifierImage.Width = crop.PixelWidth * cellDip;
        MagnifierImage.Height = crop.PixelHeight * cellDip;
        MagnifierGridLayer.Width = MagnifierImage.Width;
        MagnifierGridLayer.Height = MagnifierImage.Height;

        var color = _frame.SamplePixel(physicalX, physicalY);
        MagnifierCoordsText.Text = $"({physicalX}, {physicalY})";
        MagnifierColorText.Text = $"RGB {color.R}, {color.G}, {color.B}   #{color.R:X2}{color.G:X2}{color.B:X2}";

        MagnifierPanel.Visibility = Visibility.Visible;
        MagnifierPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var placed = OverlayPlacement.PlaceMagnifierWithFlip(
            ToDip(physicalX, physicalY),
            MagnifierPanel.DesiredSize,
            new Size(FrameDipWidth, FrameDipHeight),
            MagnifierOffsetDip);

        Canvas.SetLeft(MagnifierPanel, placed.Rect.X);
        Canvas.SetTop(MagnifierPanel, placed.Rect.Y);
    }

    // ---------------------------------------------------------------- 动作

    private BitmapSource? CropSelection()
    {
        if (!_selection.Exists || _selection.IsTooSmall)
        {
            return null;
        }

        return _frame.Crop(_selection.X, _selection.Y, _selection.Width, _selection.Height);
    }

    private void RaiseAction(OverlayActionKind kind)
    {
        if (_closing)
        {
            return;
        }

        BitmapSource? image = null;
        if (kind != OverlayActionKind.Cancel)
        {
            image = CropSelection();
            if (image is null)
            {
                kind = OverlayActionKind.Cancel;
            }
        }

        var bounds = _selection.Exists
            ? $"{_selection.X},{_selection.Y} {_selection.Width}x{_selection.Height}"
            : "(无)";

        _log.Info($"覆盖层动作: {kind}  选区={bounds}");

        ActionRequested?.Invoke(this, new OverlayActionRequestedEventArgs(kind, image));
    }

    /// <summary>由控制器调用，确保只关闭一次。</summary>
    public void CloseOverlay()
    {
        // 无论走哪条关闭路径，先把静态事件的订阅退掉 —— 一次合成回调都没落地就关闭时
        // （预热路径就是"Show 后立刻 Close"），旧写法会把闭包（→ 窗口 → 冻结帧）留在静态事件上。
        UnsubscribeFirstRendering();

        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _log.Warn("关闭覆盖层异常", ex);
        }
    }

    /// <summary>窗口以**任何**方式关闭（Alt+F4 / WM_CLOSE / Close()）都在这里兜一次退订。</summary>
    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeFirstRendering();
        base.OnClosed(e);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.Cancel);

    private void OnPinClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.Pin);

    private void OnSaveClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.Save);

    private void OnCopyClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.Copy);

    private void OnOcrTxtClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.OcrTxt);

    private void OnOcrDocxClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.OcrDocx);

    private void OnOcrPdfClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.OcrPdf);

    private void OnOcrXlsxClick(object sender, RoutedEventArgs e) => RaiseAction(OverlayActionKind.OcrXlsx);
}
