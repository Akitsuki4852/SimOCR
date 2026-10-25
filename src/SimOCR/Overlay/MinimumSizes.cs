using System.Windows;

namespace SimOCR.Overlay;

/// <summary>
/// **矩形最小尺寸不变量**的单一出处。收敛前这里有四份"矩形夹紧"副本，
/// 逐条核对后落在下面两个**语义不同**的最小尺寸上 —— 它们各自适用面不同，不能合成一个数：
///
/// * <see cref="SupportPixels"/> = **1px**：**几何结果本身**的最小宽高。
///   适用面：选区/吸附的矩形（<c>SelectionModel.ClampResize</c> 的"拖过头收成 1px"、
///   <c>SnapGeometry.ClampToLimits</c> 的"吸附后保证 ≥1px"）。这些矩形随后要变成
///   屏幕上的框、要拿去做像素裁剪，0 宽/0 高没有意义。
/// * <see cref="MeaningfulPixels"/> = **4px**：**用户意图**的最小尺寸。小于它的选区算误触，
///   松开左键按取消处理（<c>SelectionModel.IsTooSmall</c>）。它**不是**几何下界 ——
///   几何下界仍是 1px，4px 只是"这么小说明你并不想截图"的判定门槛。
///
/// 二者的关系：`SupportPixels &lt; MeaningfulPixels`，几何可以收成 1px，
/// 但 1~3px 的结果会被 <c>IsTooSmall</c> 判为误触而丢弃。
/// </summary>
internal static class MinimumSizes
{
    /// <summary>几何结果的最小宽高（像素）：**任何**被接受的选区/吸附矩形都不小于它。</summary>
    public const int SupportPixels = 1;

    /// <summary>最小"有意义"的选区边长（像素）：小于它视为误触，按取消处理。</summary>
    public const int MeaningfulPixels = 4;

    /// <summary>
    /// 把 <c>l/t/r/b</c> 夹进 <paramref name="limits"/> 并保证宽高 ≥ <paramref name="minimum"/>。
    ///
    /// 收敛 <c>SelectionModel.ClampResize</c> 与 <c>SnapGeometry.ClampToLimits</c> 两份副本
    /// 。两者的差异**全部**落在"最后收口"那一步，故做成参数而不是两段代码：
    ///   * <paramref name="truncateToLimits"/> = <c>false</c>（SelectionModel）：
    ///     `r = l + min` / `b = t + min` —— 保证结果**永远**不小于 min，
    ///     代价是当 <c>limits</c> 本身比 min 还窄时结果会略微越过 <c>limits</c>；
    ///   * <paramref name="truncateToLimits"/> = <c>true</c>（SnapGeometry）：
    ///     `r = min(limits.Right, l + min)` —— 优先不越界，允许退化成 0 宽/0 高
    ///     （<c>limits</c> 是虚拟桌面，正常屏幕下不会比 1px 还窄）。
    /// </summary>
    internal static Rect ClampToLimits(double l, double t, double r, double b, Rect limits, double minimum, bool truncateToLimits)
    {
        l = Math.Clamp(l, limits.Left, limits.Right - minimum);
        t = Math.Clamp(t, limits.Top, limits.Bottom - minimum);
        r = Math.Clamp(r, limits.Left + minimum, limits.Right);
        b = Math.Clamp(b, limits.Top + minimum, limits.Bottom);

        if (r - l < minimum)
        {
            r = truncateToLimits ? Math.Min(limits.Right, l + minimum) : l + minimum;
        }

        if (b - t < minimum)
        {
            b = truncateToLimits ? Math.Min(limits.Bottom, t + minimum) : t + minimum;
        }

        return new Rect(l, t, r - l, b - t);
    }
}
