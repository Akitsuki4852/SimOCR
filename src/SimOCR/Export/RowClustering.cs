using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// **按 y 把文本行聚成物理行**的单一实现。
///
/// 收敛前的四份副本（容差都是 `max(0.6·medianHeight, 6)`）：
///   * <c>OcrDocument.SortReadingOrder</c> —— 阅读顺序排序的行分组；
///   * <c>OcrDocument.ToPlainTextWithSpans</c> —— 纯文本的行分组（逐段递推）；
///   * <c>FastTableBuilder.RowBands</c> —— 空白对齐路径的行分组；
///   * <c>ContentDetector.BuildRows</c> —— 内容特征判定的行分组；
///   字高中位数另有 <c>OcrTableLayout.Build</c> 一份。
///
/// **合并时保留的语义差异**（消重复不是改算法，四处算法的判别式本就不同）：
///   1. **锚**：前三处把整行锚在"该行第一条框的 <see cref="OcrLine.CenterY"/>"上，
///      <c>ContentDetector</c> 锚在该行第一条框的 **Y0** 上（它判的是"文字是否同一条基线"）；
///   2. **容差**：前三处用**全局字高中位数**（本类的 <c>median → max(0.6·median, 6)</c>），
///      <c>ContentDetector</c> 用**当前这条框自己的高度**（<c>max(6, height·0.6)</c>），
///      所以它必须按框逐条算容差，不能先算一个全局中位；
///   3. **排序键**：前三处按 CenterY 排序，<c>ContentDetector</c> 按 (Y0, X0) 排序。
///
/// 因此本类只收敛**公共骨架**（排序 → 逐条与当前行比锚距 → 落行），把上面三点做成参数；
/// 默认参数就是"中位字高 + CenterY 锚"那一支（也是三处副本的共同形态）。
/// </summary>
internal static class RowClustering{
    /// <summary>默认容差系数与像素下限：`tolerance = max(0.6 × 中位字高, 6)`。</summary>
    private const double ToleranceFactor = 0.6;
    private const double ToleranceFloorPixels = 6.0;

    /// <summary>
    /// 字高中位数（四份副本里三份逐字节相同的那一份）。
    /// 只统计 <c>Height &gt; 0</c> 的框；一个都没有时返回 0（调用方各自按 0 处理）。
    /// **注意**：取的是 <c>heights[Count / 2]</c>（上中位），不是插值中位 —— 与 0.8.x 起的行为一致。
    /// </summary>
    internal static double MedianHeight(IReadOnlyList<OcrLine> lines)
    {
        var heights = new List<double>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Height > 0)
            {
                heights.Add(line.Height);
            }
        }

        if (heights.Count == 0)
        {
            return 0;
        }

        heights.Sort();
        return heights[heights.Count / 2];
    }

    /// <summary>默认容差：`max(0.6 × medianHeight, 6)`。</summary>
    internal static double Tolerance(double medianHeight)
        => Math.Max(medianHeight * ToleranceFactor, ToleranceFloorPixels);

    /// <summary>
    /// 按 CenterY 锚 + 全局中位字高容差聚行（<c>OcrDocument</c> 两处、<c>FastTableBuilder</c>、
    /// <c>OcrTableLayout</c> 走这一支）。**不排序行内顺序**，与原有实现一致 ——
    /// 行内排序由调用方按自己的口径做（阅读顺序按 X0；纯文本按原 <c>Lines</c> 顺序）。
    /// </summary>
    internal static List<List<OcrLine>> Bands(IReadOnlyList<OcrLine> lines, double medianHeight)
        => Cluster(
            lines,
            line => line.CenterY,
            line => line.CenterY,
            _ => Tolerance(medianHeight));

    /// <summary>
    /// 通用聚簇骨架：按 <paramref name="sortKey"/> 升序（稳定排序，键相同时保持输入顺序），
    /// 逐条与"当前行的锚"比较，<c>|anchor(条) − anchor(当前行首条)| &gt; tolerance(条)</c> 就开新行。
    /// 与三份副本的判别式逐字一致，只是把锚/容差/排序键提成参数。
    /// </summary>
    internal static List<List<OcrLine>> Cluster<TKey>(
        IReadOnlyList<OcrLine> lines,
        Func<OcrLine, TKey> sortKey,
        Func<OcrLine, double> anchor,
        Func<OcrLine, double> tolerance)
    {
        var bands = new List<List<OcrLine>>();
        List<OcrLine>? current = null;
        var currentAnchor = double.NaN;

        foreach (var line in lines.OrderBy(sortKey))
        {
            if (current is null || Math.Abs(anchor(line) - currentAnchor) > tolerance(line))
            {
                current = [];
                currentAnchor = anchor(line);
                bands.Add(current);
            }

            current.Add(line);
        }

        return bands;
    }
}
