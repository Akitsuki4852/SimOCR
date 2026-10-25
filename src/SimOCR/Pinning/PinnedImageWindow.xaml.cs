using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimOCR.Diagnostics;
using SimOCR.Interop;
using SimOCR.Settings;

namespace SimOCR.Pinning;

/// <summary>
/// 置顶贴图窗口：拖动移动、滚轮以光标为锚点缩放、右键菜单（复制/保存/关闭/全部关闭）。
/// 位置与尺寸一律用物理像素经 SetWindowPos 落地，避免多屏 DPI 换算误差。
/// </summary>
internal partial class PinnedImageWindow : Window
{
    private readonly BitmapSource _image;
    private readonly AppSettings _settings;
    private readonly LogService _log;

    private double _zoom;
    private double _scale = 1.0;
    private int _physX;
    private int _physY;
    private int _physW;
    private int _physH;
    private bool _placed;

    public PinnedImageWindow(BitmapSource image, AppSettings settings, LogService log, int physicalX, int physicalY)
    {
        _image = image;
        _settings = settings;
        _log = log;
        _physX = physicalX;
        _physY = physicalY;
        _zoom = Math.Clamp(settings.Pin.DefaultZoom, settings.Pin.MinZoom, settings.Pin.MaxZoom);

        InitializeComponent();

        PinnedImage.Source = image;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceWindow();

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseWheel += OnMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;

        MenuCopy.Click += (_, _) => CopyRequested?.Invoke(this);
        MenuSave.Click += (_, _) => SaveRequested?.Invoke(this);
        MenuClose.Click += (_, _) => CloseRequested?.Invoke(this);
        MenuCloseAll.Click += (_, _) => CloseAllRequested?.Invoke(this);
    }

    public event Action<PinnedImageWindow>? CopyRequested;
    public event Action<PinnedImageWindow>? SaveRequested;
    public event Action<PinnedImageWindow>? CloseRequested;
    public event Action<PinnedImageWindow>? CloseAllRequested;

    public BitmapSource Image => _image;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0)
        {
            _scale = 1.0;
        }
    }

    /// <summary>按当前 zoom 计算物理尺寸并落地。</summary>
    private void PlaceWindow()
    {
        RecalcPhysicalSize();
        ApplyGeometry(show: true);
        _placed = true;
    }

    private void RecalcPhysicalSize()
    {
        _physW = Math.Max(16, (int)Math.Round(_image.PixelWidth * _zoom));
        _physH = Math.Max(16, (int)Math.Round(_image.PixelHeight * _zoom));
    }

    private void ApplyGeometry(bool show = false)
    {
        // 内容按 DIP 铺满（DIP = 物理 / scale）
        PinnedImage.Width = _physW / _scale;
        PinnedImage.Height = _physH / _scale;
        Width = PinnedImage.Width + 2;
        Height = PinnedImage.Height + 2;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var flags = NativeMethods.SWP_NOACTIVATE;
        if (show)
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _physX, _physY, _physW + 2, _physH + 2, flags);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (e.ClickCount == 2)
        {
            // 双击贴图 = 关闭（与"贴图便于临时对照"的习惯一致）
            CloseRequested?.Invoke(this);
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove 在鼠标已释放时会抛，忽略
        }

        // DragMove 结束后记录新位置，后续缩放以此为基准
        if (PresentationSource.FromVisual(this) is HwndSource src && src.Handle != IntPtr.Zero)
        {
            // Left/Top 是 DIP，换算回物理
            _physX = (int)Math.Round(Left * _scale);
            _physY = (int)Math.Round(Top * _scale);
            ApplyGeometry();
        }

        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        var oldZoom = _zoom;
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newZoom = Math.Clamp(oldZoom * factor, _settings.Pin.MinZoom, _settings.Pin.MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        // 以光标为锚点：保持光标下的像素不动，避免"越缩越跑"
        var cursor = MonitorLayout.GetCursorPosition();
        var relX = cursor.X - _physX;
        var relY = cursor.Y - _physY;
        var ratio = newZoom / oldZoom;

        _physX = cursor.X - (int)Math.Round(relX * ratio);
        _physY = cursor.Y - (int)Math.Round(relY * ratio);
        _zoom = newZoom;

        RecalcPhysicalSize();
        ApplyGeometry();

        _log.Debug($"贴图缩放: {newZoom:F2}x 位置 {_physX},{_physY} 尺寸 {_physW}x{_physH}");
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _settings.Pin.CloseOnEscape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_placed)
        {
            _log.Info($"关闭贴图: {_image.PixelWidth}x{_image.PixelHeight} @ {_physX},{_physY} zoom={_zoom:F2}");
        }

        base.OnClosing(e);
    }
}
