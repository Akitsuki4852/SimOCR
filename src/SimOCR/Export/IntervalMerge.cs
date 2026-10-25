namespace SimOCR.Export;

/// <summary>
/// **区间并集**：把"同一方向上一串区间"按 <c>minGap</c> 并成不重叠的若干段。
///
/// 收敛前的副本（说是 4 份，实际逐条核对如下）：
///   * <c>TableColumnDetector.DataColumns</c> 与 <c>TableColumnDetector.MergeColumns</c>
///     —— **同一算法写两遍**（前者靠调用方已排序，后者自己排），本类合并这两处；
///   * <c>BlankColumnDetector.Coverage</c> 里"同一行内先把区间并起来，避免重复计数"那一段
///     —— 并的是像素 <c>int</c> 区间，判据是"下一段的起点 &lt;= 上一段的终点 + 1"（相邻即并），
///     由 <see cref="UnionSpans"/> 表达，不再各写一遍循环；
///   * <c>TableGrid.Normalize</c> **不是**并集：它不扩张既有区间，只把"离上一条太近"的
///     分割线**丢掉**（保留第一条，不取并集/中位）。语义不同，故不并进来 —— 它只共用
///     <see cref="MinLineSpacing"/> 这一个常量与 <see cref="HasMinimumSpacing"/> 这道闸门。
///
/// 判据与 0.8.x 起的副本逐字一致：**严格小于** <c>minGap</c> 才算"缝太小、并起来"。
/// </summary>
internal static class IntervalMerge
{
    /// <summary>
    /// **形状闸门**的最小分割线间距（像素）：相邻行/列分割线窄于此的表格一律否决
    /// （用户明确要求"宁可不出表也不出畸形表"）。适用于**分割线列表**的清理与校验：
    /// <c>TableGrid.Normalize</c>（丢弃过近的线）、<c>FastTableBuilder.IsSane</c> /
    /// <c>IsSaneForManual</c> / <c>LineGridDetector.IsSane</c> 的逐列逐行闸门。
    /// **不适用于**覆盖直方图/区间并集的"缝"阈值（那些是相对量，见各自的 minGap）。
    /// </summary>
    internal const double MinLineSpacing = 3.0;

    /// <summary>
    /// 合并 double 区间：相邻两段"缝"严格小于 <paramref name="minGap"/> 时取并集
    /// （Min 取小、Max 取大）。**与输入顺序无关**（内部先按 Min 排序；已排序的输入
    /// 多一次 OrderBy 不改变结果）。
    /// </summary>
    internal static List<(double Min, double Max)> Union(
        IEnumerable<(double Min, double Max)> intervals,
        double minGap)
    {
        var merged = new List<(double Min, double Max)>();
        foreach (var interval in intervals.OrderBy(i => i.Min))
        {
            if (merged.Count > 0 && interval.Min - merged[^1].Max < minGap)
            {
                var last = merged[^1];
                merged[^1] = (last.Min, Math.Max(last.Max, interval.Max));
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    /// <summary>
    /// 合并像素 <c>int</c> 区间：**相邻即并**（下一段起点 &lt;= 上一段终点 + 1），
    /// 与 <see cref="Union"/> 的"严格小于 minGap"是同一族里参数为 1 的那一档。
    /// </summary>
    internal static List<(int Start, int End)> UnionSpans(IEnumerable<(int Start, int End)> spans)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>
    /// 逐列/逐行闸门：任意两条相邻分割线的间距 &lt; <paramref name="minSpacing"/> 即不合格
    /// （"&lt;3px 间隙循环"的单一实现）。
    ///
    /// **`IsSane` 与 `IsSaneForManual` 的差异不在这里，也不得被这里吃掉**：
    /// 两者的区别在**前置准入条件**（自动档要求 ≥2 列 ≥1 行、整表尺寸 ≥8px；手工档只要有一条
    /// 内部分割线、不要求整表尺寸），而**这一道循环的阈值两者相同**（都是 3px）。
    /// 差异由调用方各自的前置条件表达，本方法只负责那段逐字节相同的循环。
    /// </summary>
    internal static bool HasMinimumSpacing(IReadOnlyList<double> lines, double minSpacing = MinLineSpacing)
    {
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i + 1] - lines[i] < minSpacing)
            {
                return false;
            }
        }

        return true;
    }
}
