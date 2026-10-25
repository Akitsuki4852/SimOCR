using System.Windows;

namespace SimOCR.Overlay;

/// <summary>
/// 覆盖层内部元素的摆放数学（纯函数，便于单元测试）。
/// 全部使用 DIP；调用方负责把物理像素换算好。
///
/// **取舍**：过去 `IsToolbarInside` 把 `PlaceToolbar` 内部算过的翻转条件
/// 重算了一遍（唯一用途是拼日志字符串）。消重复的做法是"翻转只判一次、结果随摆放结果带出"，
/// 但那会把 <c>PlaceToolbar</c> 的返回类型从 <see cref="Rect"/> 改成 <see cref="Placement"/>，
/// **破坏既有调用契约**（<c>tests/SimOCR.Tests/OverlayPlacementTests.cs</c> 直接读 <c>placed.X</c>，
/// 而 <c>tests/</c> 不属本次改动范围）。
/// 因此保留三个原来的 <see cref="Rect"/> 重载（薄壳，内部就是取 <c>XxxWithFlip(...).Rect</c>），
/// 另加三个 <c>…WithFlip</c> 重载给需要翻转信息的生产调用方 —— 真实算法仍然只有一份。
/// </summary>
internal static class OverlayPlacement
{
    /// <summary>
    /// 摆放结果：矩形 + 是否发生了翻转。
    /// **翻转只在这里判定一次**，不再有第二个判据（过去的 <c>IsToolbarInside</c> 已删）。
    /// </summary>
    internal readonly record struct Placement(Rect Rect, bool Flipped);

    /// <summary>
    /// 工具条：默认贴在选框**右下角外侧**；下方放不下就翻到选框上方内侧；
    /// 最后统一夹到画面范围内，保证任何情况下都完整可见、可点击。
    /// </summary>
    public static Rect PlaceToolbar(Rect selection, Size toolbar, Size frame, double gap)
        => PlaceToolbarWithFlip(selection, toolbar, frame, gap).Rect;

    /// <summary>工具条 + 翻转标记（生产日志用；翻转判定只有这一处）。</summary>
    public static Placement PlaceToolbarWithFlip(Rect selection, Size toolbar, Size frame, double gap)
    {
        var x = selection.Right - toolbar.Width;
        var y = selection.Bottom + gap;

        var flipped = y + toolbar.Height > frame.Height;
        if (flipped)
        {
            y = selection.Bottom - toolbar.Height - gap;
        }

        return new Placement(ClampToFrame(new Rect(x, y, toolbar.Width, toolbar.Height), frame), flipped);
    }

    /// <summary>尺寸标签：贴在选框左上角上方；顶部放不下就翻到选框内侧。</summary>
    public static Rect PlaceSizeLabel(Rect selection, Size label, Size frame, double gap)
        => PlaceSizeLabelWithFlip(selection, label, frame, gap).Rect;

    /// <summary>尺寸标签 + 翻转标记。</summary>
    public static Placement PlaceSizeLabelWithFlip(Rect selection, Size label, Size frame, double gap)
    {
        var x = selection.Left;
        var y = selection.Top - label.Height - gap;

        var flipped = y < 0;
        if (flipped)
        {
            y = selection.Top + gap;
        }

        return new Placement(ClampToFrame(new Rect(x, y, label.Width, label.Height), frame), flipped);
    }

    /// <summary>
    /// 放大镜：默认在光标右下；碰到右边/下边就翻到左上；再统一夹回画面内。
    /// </summary>
    public static Rect PlaceMagnifier(Point cursor, Size magnifier, Size frame, double offset)
        => PlaceMagnifierWithFlip(cursor, magnifier, frame, offset).Rect;

    /// <summary>放大镜 + 翻转标记（任一个轴翻转就算翻转）。</summary>
    public static Placement PlaceMagnifierWithFlip(Point cursor, Size magnifier, Size frame, double offset)
    {
        var x = cursor.X + offset;
        var y = cursor.Y + offset;

        var flipped = false;
        if (x + magnifier.Width > frame.Width)
        {
            x = cursor.X - magnifier.Width - offset;
            flipped = true;
        }

        if (y + magnifier.Height > frame.Height)
        {
            y = cursor.Y - magnifier.Height - offset;
            flipped = true;
        }

        return new Placement(ClampToFrame(new Rect(x, y, magnifier.Width, magnifier.Height), frame), flipped);
    }

    /// <summary>
    /// 把矩形整体夹进画面（元素比画面还大时贴左上角）。
    ///
    /// **与 <c>MinimumSizes</c> 的最小尺寸无关，故不并进 的收敛**：这里操作的是
    /// **DIP** 且最小尺寸是 **0**（控件可以小到看不见也要能摆，只要不越界），
    /// 而这里收敛的是**物理像素选区**的 "≥1px / 误触 ≥4px" 不变量。
    /// </summary>
    public static Rect ClampToFrame(Rect rect, Size frame)
    {
        var maxX = Math.Max(0, frame.Width - rect.Width);
        var maxY = Math.Max(0, frame.Height - rect.Height);

        var x = Math.Clamp(rect.X, 0, maxX);
        var y = Math.Clamp(rect.Y, 0, maxY);

        return new Rect(x, y, rect.Width, rect.Height);
    }
}
