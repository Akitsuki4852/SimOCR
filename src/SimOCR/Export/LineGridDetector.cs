using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimOCR.Export;

/// <summary>线格检测结果：一张表（多个可有）。坐标是**图片像素**坐标。</summary>
internal sealed record GridTable(
    double X0, double Y0, double X1, double Y1,
    IReadOnlyList<double> RowLines,
    IReadOnlyList<double> ColumnLines)
{
    internal int RowCount => Math.Max(RowLines.Count - 1, 0);

    internal int ColumnCount => Math.Max(ColumnLines.Count - 1, 0);

    internal int CellCount => RowCount * ColumnCount;
}

/// <summary>
/// 线格检测的"故障"与"结论"分码。
///
/// 背景：<see cref="LineGridDetector.ToGray"/> 过去把 <c>catch (Exception) { return null; }</c>
/// 与"图太小 / 真的没找到网格"**混成同一个返回值** —— 于是 OOM、线程亲和不符（调用方在
/// 后台线程上用 UI 线程创建的 <c>BitmapSource</c>）、编解码错误这些**故障**都被静默读成
/// "这张图没有网格"，调用方照常回退到"空白对齐"路径，日志里也没有任何痕迹。
///
/// 现在的口径（**只改可诊断性，不改任何对外行为**）：
///   * **结论**"这张图里没有线格" → <see cref="LineGridDetector.Detect"/> 返回**空列表**（不变）；
///   * **故障**（灰度转换抛异常）→ 抛本异常，由调用方就地记成一条**独立的**诊断（含异常原文），
///     返回值**仍然是空列表**（回退路径与改动前逐字一致）。
///
/// 判据：日志里出现 `[线格] 检测故障:` ⇔ 走的是故障分支；没有这行而表数 = 0 ⇔ 真的没找到网格。
/// </summary>
internal sealed class LineGridFailureException : Exception
{
    internal LineGridFailureException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// **线格路径**：直接从像素里找表格线，不依赖 OCR 框 —— 对"框选出来的规则表格"又快又准
/// （实测常见截图里多数带网格；毫秒级，可用于"框选后 1 秒内出表格"的快速通道）。
///
/// 算法（阈值全部用**相对量**，与图幅/分辨率无关）：
///   1) 灰度 + 全图 Otsu 二值化（钳制 80..210）；
///   2) **方向性行程长度**（S2，取代旧的"先膨胀再造候选"）：横线要求某行的暗像素连续段
///      ≥ 10% 图宽（横线掩膜保留 1px 断口桥接）；竖线在**未膨胀**的二值图上要求某列的
///      连续段 ≥ max(2% 图高, 0.6×文字行距, 2×笔画宽)；
///   3) 去重：位置相距 &lt; 0.6% 图高的算同一条；
///   4) **交叉筛选**：一条线段必须与另一方向 ≥2 条线段相交 —— 下划线（没有竖线穿过）在此自然淘汰；
///   5) **按 x 跨度分簇**：每簇一张表（支持一页多表/并排多块）；
///   6) 单元格 = (横线-1)×(竖线-1)，≥4 格才算表（Tabula 的判据）。
///
/// 灰表头/半调列过切分的实测背景（结论保留、来源路径删掉）：小裁剪（585×200）上
/// `0.02×图高` 只剩 4px，**先把 1px 文字笔画横向膨胀成 3px、再逐列扫 4px 连续段**，
/// 于是每个文字竖笔都成了"竖线"，585×200 的裁剪被切成 **10 行 × 31 列**（用户看到的畸形表）。
/// 实测：本改动把某张整页样本从 10×31 收敛到 **10×6**、另一张扫描件从 20×34
/// 到 **20×6**，而 6 张业务样本 + 37 张扫描件基准的形状与改动前**逐条一致**（除其中一张少一条假列线）。
/// </summary>
internal static class LineGridDetector
{
    private const double MinRunWidthRatio = 0.10;   // 横线最短：10% 图宽（不变）

    // 竖线最短的**兜底**值（旧口径，2% 图高）。灰表头实测它在小裁剪上只剩 4px（585×200 → 4px），
    // 是列过切分的容忍来源；但它在整页上（1000px → 20px）是**有效**的召回防线，
    // 所以新判据对它取 max（只会更严、不会更松）—— 见 Detect 里的 minRunV。
    private const double MinRunHeightRatio = 0.02;

    private const int BandTolerance = 3;            // 线宽容差（像素）

    private const int MinCrossings = 2;             // 至少要穿过 2 条另一方向的线

    private const int MinCells = 4;                 // 至少 4 格才算表

    /// <summary>检测图中的线格表格；没有则返回空列表（调用方应回退到空白/对齐路径）。
    /// **故障与结论分码**：灰度转换失败时抛 <see cref="LineGridFailureException"/>，
    /// 不再与"这张图里没有线格"共用同一个返回值 —— 见该异常类型的注释。</summary>
    internal static List<GridTable> Detect(BitmapSource image)
    {
        var gray = ToGray(image, out var width, out var height);
        if (gray is null || width < 40 || height < 40)
        {
            // 走到这里的 gray is null 只剩一种情形：**图太小**（宽或高 < 40）被提前放弃，
            // 这是"结论"不是"故障"；真正的转换故障已经在 ToGray 里抛出来了。
            return [];
        }

        var binary = Binarize(gray, width, height);

        // ---- 尺度锚（研究报告 §6.1「S0」的最小实现：只用两趟 1D 统计，不做连通分量）----
        // rowPitch = **文字行距中位**：先按横线口径扫一遍候选，取相邻候选的间距中位。
        //   本素材实测 16.0~17.5px（四张样本分别为 16.5 / 16.25 / 16.0 / 17.5），
        //   与研究报告 §6.1 的 lh ≈ 17px 一致。
        // strokeWidth = **笔画宽**：水平墨游程的中位长度（文字竖笔、半调网点都是 1~3px）。
        //   本素材实测 2px（另一张扫描件为 1px），与研究报告 §6.1 的 sw ≈ 2px 一致。
        var minRunH = MinRunWidthRatio * width;
        var rowPitch = MedianGap(FindSegments(binary, width, height, vertical: false, minRunH));
        var strokeWidth = MedianStrokeWidth(binary, width, height);

        // ---- S2：方向性行程长度，**竖线候选不再先膨胀**（研究报告 §6.2）----
        // 旧的 `Dilate → FindSegments`：横向膨胀把 1px 笔画撑成 3px，逐列扫描立刻凑出
        // `0.02×图高`（585×200 的裁剪上只有 4px）的"连续段"，于是每个文字竖笔都成了竖线。
        // 实测某张整页样本：同一张未膨胀的二值图上，旧 4px 口径 → 198 条竖候选；
        // 新口径 9.9px → 29 条，其中只有 7 条是 178px 的真竖线（6 列 = 7 条竖线）。
        //   真竖线的最长连续暗段 = 177~178px；文字笔画列 = 4..19px（研究报告 §6.2 实测）。
        // **注意 max 里的 `MinRunHeightRatio * height`**：取旧口径作为下限，
        // 保证"竖线最短长度"**永不比今天更松**（combined never worse）。
        // 实测这一项是必需的：去掉它（只用 0.6×行距 = 9.6px）时另一张扫描件会多出两条假竖线
        // （x=160、x=286，长度 10~19px），整页形状从 20×6 恶化成 20×8 + 一张假表。
        var minRunV = Math.Max(MinRunHeightRatio * height, Math.Max(0.6 * rowPitch, 2 * strokeWidth));

        // 横线掩膜：**保留**沿 y 的 1px 断口桥接（抗锯齿/扫描的横线常被文字压出断口）。
        // 实测这一步不能省：完全不桥接（直接用 binary）时某张扫描件的行线从 12 掉到 7+2、
        // 另一张从 21+5 掉到 20+4（横线在单行里凑不够 10% 图宽的连续段）。
        // 它**不会**制造假竖线 —— 因为竖线候选根本不读这张掩膜。
        var horizontalMask = BridgeGapsVertically(binary, width, height);

        // 竖线掩膜：**不桥接**。旧代码在这里做了横向 Dilate（桥接开关的 `false` 分支），
        // 把 1px 文字笔画横向撑成 3px，逐列扫描立刻够长度 —— 这就是 31 列的来源（研究报告 §6.2）。
        var verticalMask = binary;

        var horizontal = FindSegments(horizontalMask, width, height, vertical: false, minRunH);
        var vertical = FindSegments(verticalMask, width, height, vertical: true, minRunV);

        var tolerance = Math.Max(4.0, height * 0.006);
        horizontal = Dedupe(horizontal, tolerance);
        vertical = Dedupe(vertical, tolerance);

        // 注意（本次已回退的尝试 · S5）：研究报告 §6.5 建议在这里再加一道"明暗边界否决"
        // （线上方 4px 与下方 4px 均值差 > 0.25×(p90−p2) 就不是线）。它在某张扫描件上确实
        // 能去掉灰晕边 y=205，但实测**误伤真线**：θ = 0.25×(255−44) = 52.8 时，
        // 另一张扫描件的 6 条真规则（y=572/631/660/689/894/748，间距 17~29px 的正常网格）
        // 的 |上−下| 是 44.5~50.3（整行均值口径，离阈值只差 5~10%），
        // 同一张扫描件甚至否决了真正的右边框 x=595。原因是这条判据**无法区分**
        // "底纹带的下边缘恰好是一条真规则"与"底纹带的下边缘没有线" —— 两者上下灰度差一样。
        // 而它唯一的真阳性 y=205 落在真线 y=206 的 Dedupe(6px) 容差内，本来就会被合并掉，
        // 即"否决它"等于零收益。所以不加这道，见交付报告「已尝试并回退」。

        // 交叉筛选
        var crossable = horizontal.Where(h => vertical.Count(v => Crosses(h, v)) >= MinCrossings).ToList();
        var tables = new List<GridTable>();
        foreach (var cluster in SpanClusters(crossable, Math.Max(6.0, width * 0.02)))
        {
            var columns = vertical.Where(v => cluster.Count(h => Crosses(h, v)) >= MinCrossings)
                                  .Select(v => v.Pos)
                                  .OrderBy(x => x)
                                  .ToList();
            // 至少 3 条竖线（= 2 列以上）：浅灰掩膜下文字竖笔 + 长横线有可能凑出假表，
            // 提高这条下限后仍能识别正常表格（实测正常表格的竖线数远多于 3）。
            if (columns.Count < 3)
            {
                continue;
            }

            var rows = cluster.Select(h => h.Pos).OrderBy(y => y).ToList();
            var table = new GridTable(
                cluster.Min(h => h.Start),
                rows[0],
                cluster.Max(h => h.End),
                rows[^1],
                rows,
                columns);
            if (IsSane(table, strokeWidth))
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    /// <summary>
    /// **从多张候选表里挑一张**。取舍与理由（此前的问题正是"生产侧只看 tables[0]"）：
    ///
    /// * **保留多表能力** —— 一页多表/并排多块是真实版式（本类算法的第 5 步就是为它写的，
    ///   自检也用得到：`Diagnostics/SelfTest.cs` 会遍历全部表核对形状）。所以**不删**多表返回。
    /// * **生产侧真的用它，不再写 <c>tables[0]</c>** —— `Detect` 的返回顺序是
    ///   `SpanClusters` 的"簇内横线数降序"，那是**横线多少**的排序，不是"哪张表是用户要的"
    ///   的排序：一张"横线很多但只有 2 列"的窄表会排在"横线少一些的真表格"前面。
    /// * **挑法（确定性的、不依赖 OCR）**：格子数 → 面积 → 行数 → 列数 → 位置（左上优先）。
    ///   为什么先看格子数：`IsSane` 已经保证进来的每张表都 ≥4 格且形状正常，此时
    ///   "格子最多"最接近"用户框选的那张表"；面积作第一顺位会偏向"又宽又高的稀疏表格"，
    ///   行数作第一顺位会偏向"很多行但只有 2 列"的窄条。
    /// * **为什么不用 OCR 文字覆盖度**：`Detect` 在 OCR 之前就要跑（"框选后 1 秒内出表格"
    ///   的快速通道），那时还没有文字行可算；用文字覆盖度会让同一个函数在"识别前/识别后"
    ///   给出不同答案。所以选表必须是**纯几何**的。
    ///
    /// 实测（整页业务单据样本 + 行业档）：本函数选出的表与
    /// 原来的 `tables[0]` 是同一张（导出 TSV/xlsx 逐字节一致）。
    /// </summary>
    internal static GridTable? Best(IReadOnlyList<GridTable> tables)
    {
        GridTable? best = null;
        foreach (var table in tables)
        {
            if (best is null || IsBetter(table, best))
            {
                best = table;
            }
        }

        return best;
    }

    private static bool IsBetter(GridTable candidate, GridTable incumbent)
    {
        if (candidate.CellCount != incumbent.CellCount)
        {
            return candidate.CellCount > incumbent.CellCount;
        }

        var candidateArea = (candidate.X1 - candidate.X0) * (candidate.Y1 - candidate.Y0);
        var incumbentArea = (incumbent.X1 - incumbent.X0) * (incumbent.Y1 - incumbent.Y0);
        if (candidateArea != incumbentArea)
        {
            return candidateArea > incumbentArea;
        }

        if (candidate.RowCount != incumbent.RowCount)
        {
            return candidate.RowCount > incumbent.RowCount;
        }

        if (candidate.ColumnCount != incumbent.ColumnCount)
        {
            return candidate.ColumnCount > incumbent.ColumnCount;
        }

        // 全平（同格子数/面积/形状）时取"更靠左上"的那张：确定性收尾，
        // 避免"哪张先被枚举到"这种实现细节决定输出。
        if (candidate.Y0 != incumbent.Y0)
        {
            return candidate.Y0 < incumbent.Y0;
        }

        return candidate.X0 < incumbent.X0;
    }

    /// <summary>
    /// **形状是否正常**（用户明确要求：不能输出形状不正常的表格；"宁可不出表也不出畸形表"）：
    /// 行列数在合理范围内、单元格数够多也不离谱、每一列都有实际宽度、每一行都有实际高度，
    /// 并且**列宽中位 ≫ 笔画宽**（见下面的形状闸门）。
    /// </summary>
    private static bool IsSane(GridTable table, double strokeWidth)
    {
        if (table.CellCount < MinCells || table.RowCount > 400 || table.ColumnCount > 100)
        {
            return false;
        }

        if (table.X1 - table.X0 < 8 || table.Y1 - table.Y0 < 8)
        {
            return false;
        }

        // 列宽/行高都不该接近 0（退化的"线挤在一起"会把表格切成锯齿状）。
        // 逐列逐行闸门只有一份实现：与 FastTableBuilder.IsSane / IsSaneForManual 共用。
        // 手工档（IsSaneForManual）与此处的差异在**准入条件**，同一道循环阈值相同。
        if (!IntervalMerge.HasMinimumSpacing(table.ColumnLines)
            || !IntervalMerge.HasMinimumSpacing(table.RowLines))
        {
            return false;
        }

        // 形状闸门（研究报告 §6.7「S7」，复用**这一道** IsSane，不新增第二套闸门）：
        // **列宽中位 < 3×笔画宽** 的表一律否决 —— 3×笔画宽的列连一个字都放不下，
        // 只可能是"文字笔画被当成竖线"凑出来的形状。否决后调用方回退"空白对齐"路径
        // （SelfTest.cs:`grid is null` 分支、OcrResultWindow 同款），符合"宁可不出表也不出畸形表"。
        // 实测：1005 张同类发票裁剪里，
        // S2 之后仍有 5 张"表"，列宽中位 6~8.5px 而笔画宽 3px（3×sw = 9px）→ 本闸门否掉 4 张，
        // 剩 1 张 2 行×2 列（两列宽 7/10px，取上中位 10 ≥ 9 所以过闸）——这是已知的边界残留。
        // 同一批 6 张业务样本的 144 张裁剪共 41 张表（列宽中位 ~100px）→ **41/41 保留**；
        // 整页输入：某张整页样本列宽中位 76px（sw=2）、另一张扫描件 65px（sw=1）、合成规则表 120px，
        // 全部远大于 3×sw，所以这条闸门在验收清单上零副作用。
        var widths = new List<double>(Math.Max(table.ColumnLines.Count - 1, 0));
        for (var c = 0; c + 1 < table.ColumnLines.Count; c++)
        {
            widths.Add(table.ColumnLines[c + 1] - table.ColumnLines[c]);
        }

        if (widths.Count > 0)
        {
            widths.Sort();
            if (widths[widths.Count / 2] < 3 * strokeWidth)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 灰度化。<c>null</c> 只表示"**这张图不该进线格路径**"（宽或高 &lt; 40 的极小图）；
    /// **灰度转换失败的故障另走 <see cref="LineGridFailureException"/>**——
    /// 过去这里的 <c>catch (Exception) { return null; }</c> 把 OOM / 线程亲和不符 /
    /// 编解码错误与"没找到网格"压成同一个返回值，故障在日志里**完全不可见**。
    /// </summary>
    private static byte[]? ToGray(BitmapSource image, out int width, out int height)
    {
        width = image.PixelWidth;
        height = image.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            var gray = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
            var stride = width;
            var buffer = new byte[stride * height];
            gray.CopyPixels(buffer, stride, 0);
            return buffer;
        }
        catch (Exception ex)
        {
            // 不再 return null（那会把故障读成"没有网格"）：抛出去，让调用方记下**独立的**一条诊断。
            throw new LineGridFailureException(
                $"像素灰度化失败（{width}x{height}）：{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 二值化：**全图 Otsu**（表格线/文字 vs 纸底是典型双峰）。
    /// 旧版用「32×32 块中位数 − 25」，在文字密集的块里阈值被压得很低，
    /// **细、浅、抗锯齿的表格线**（截图里最常见）就判不成墨 —— 实测导致浅灰细线表格建不出表。
    /// 灰表头实测补充：本素材全图 Otsu = 162，正好落在表头半调底纹（128..247）**内部**，
    /// 所以全局阈值单独用一定会在底纹区劈错 —— 由 <see cref="RestrictShadedRegions"/> 收紧。
    /// </summary>
    private static int OtsuThreshold(int[] histogram)
    {
        long total = 0;
        foreach (var count in histogram)
        {
            total += count;
        }

        if (total == 0)
        {
            return 160;
        }

        var sum = 0.0;
        for (var i = 0; i < 256; i++)
        {
            sum += i * (double)histogram[i];
        }

        var sumBackground = 0.0;
        var weightBackground = 0;
        var best = 0.0;
        var threshold = 160;
        for (var i = 0; i < 256; i++)
        {
            weightBackground += histogram[i];
            if (weightBackground == 0)
            {
                continue;
            }

            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += i * (double)histogram[i];
            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sum - sumBackground) / weightForeground;
            var variance = (double)weightBackground * weightForeground
                           * (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (variance > best)
            {
                best = variance;
                threshold = i;
            }
        }

        // 双峰不明显时（整体很淡）给一个保守上限，避免把纸底当墨
        return Math.Clamp(threshold, 80, 210);
    }

    private static int[] Histogram(byte[] gray)
    {
        var histogram = new int[256];
        foreach (var value in gray)
        {
            histogram[value]++;
        }

        return histogram;
    }

    private static bool[] Binarize(byte[] gray, int width, int height)
    {
        var threshold = OtsuThreshold(Histogram(gray));
        var binary = new bool[width * height];
        for (var i = 0; i < binary.Length; i++)
        {
            // 注意（已回退的尝试）：曾在此叠加"浅灰也算墨"（gray < 235）来抓截图的浅灰网格线，
            // 召回是上来了（浅灰细线表格能检出），但**精度垮掉**：文字笔画被当成竖线，
            // 出现 21x20、2x16 这种形状不正常的表（用户明确禁止）。
            // 正确做法是用浅灰掩膜**延长**严格掩膜找到的线段，而不是让它**新增**候选线 —— 留待下一轮。
            //
            // 注意（本次已回退的尝试 · S3）：半调底纹区"二次二值化"（窗口中间灰 ≥25% 时取
            // min(全局 Otsu, 窗口 Otsu)）也**没有保留**。它在全部 15 个验收输入上只改变了一处：
            // 另一张扫描件的局部裁剪丢掉真规则 y=679（该行间距 17px，正好落在表内规则网格上）。
            // 机制已复算：窗口 x[71,142)×y[639,710) 中间灰 27.38%（刚过闸门）→ 局部 Otsu 152
            // < 全局 169 → 线自身抗锯齿的 2 个像素（灰 156/158）被剔掉 → 该行连续段断开。
            // 详见交付报告「已尝试并回退」一节。
            binary[i] = gray[i] < threshold;
        }

        return binary;
    }

    /// <summary>
    /// 水平墨游程的**中位**长度 = 笔画宽 `sw`（文字竖笔 1~3px；真横线是极少数长游程，不拉高中位）。
    /// 实测：本素材五张样本都是 **2px**，
    /// 另一张扫描件是 1px（研究报告 §6.1 sw ≈ 2px）。
    /// </summary>
    private static double MedianStrokeWidth(bool[] binary, int width, int height)
    {
        var runHistogram = new int[width + 1];
        long counted = 0;
        for (var y = 0; y < height; y++)
        {
            var rowBase = y * width;
            var run = 0;
            for (var x = 0; x < width; x++)
            {
                if (binary[rowBase + x])
                {
                    run++;
                }
                else if (run > 0)
                {
                    runHistogram[run]++;
                    counted++;
                    run = 0;
                }
            }

            if (run > 0)
            {
                runHistogram[run]++;
                counted++;
            }
        }

        if (counted == 0)
        {
            return 1;
        }

        var target = counted / 2;
        long accumulated = 0;
        for (var length = 1; length <= width; length++)
        {
            accumulated += runHistogram[length];
            if (accumulated > target)
            {
                return length;
            }
        }

        return width;
    }

    /// <summary>
    /// 相邻候选线的**间距中位** = 文字行距（<see cref="Segment"/> 已经按 BandTolerance 合并，
    /// 所以 &lt; 3px 的间距是同一条线的多行，不计入）。候选 &lt; 3 条时返回 0（退回旧口径）。
    /// </summary>
    private static double MedianGap(List<Segment> segments)
    {
        if (segments.Count < 3)
        {
            return 0;
        }

        var positions = segments.Select(s => s.Pos).OrderBy(p => p).ToList();
        var gaps = new List<double>(positions.Count);
        for (var i = 1; i < positions.Count; i++)
        {
            var gap = positions[i] - positions[i - 1];
            if (gap >= BandTolerance)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
        {
            return 0;
        }

        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>
    /// 沿**列方向**（上下邻域）桥接 1px 断口。
    /// 注意：它**只用于横线掩膜**。旧代码这里有个 <c>vertical</c> 开关，也用在竖线掩膜上
    /// （<c>vertical: false</c> = 左右邻域），结果是 1px 文字笔画被撑成 3px、逐列扫描立刻够长度
    /// —— 列过切分就是这么来的。该 <c>false</c> 分支自那次修复后再无调用点
    /// （唯一调用传 <c>true</c>），0.10.5 已删参数并把方法名改成它真正的语义。
    /// </summary>
    private static bool[] BridgeGapsVertically(bool[] binary, int width, int height)
    {
        var result = new bool[binary.Length];
        for (var y = 0; y < height; y++)
        {
            var rowBase = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = rowBase + x;
                if (binary[index])
                {
                    result[index] = true;
                    continue;
                }

                result[index] = (y > 0 && binary[index - width]) || (y + 1 < height && binary[index + width]);
            }
        }

        return result;
    }

    private readonly record struct Segment(double Pos, int Start, int End);

    /// <summary>找候选线段（横线逐行扫、竖线逐列扫），同一条线占多行/列时取并集。</summary>
    private static List<Segment> FindSegments(bool[] binary, int width, int height, bool vertical, double minLength)
    {
        var count = vertical ? width : height;
        var segments = new List<Segment>();
        var i = 0;
        while (i < count)
        {
            var (start, end) = LongestRun(binary, width, height, vertical, i, minLength);
            if (end < start)
            {
                i++;
                continue;
            }

            var j = i + 1;
            while (j < count && j - i < BandTolerance)
            {
                var (nextStart, nextEnd) = LongestRun(binary, width, height, vertical, j, minLength);
                if (nextEnd < nextStart || nextEnd < start || nextStart > end)
                {
                    break;
                }

                start = Math.Min(start, nextStart);
                end = Math.Max(end, nextEnd);
                j++;
            }

            segments.Add(new Segment((i + j - 1) / 2.0, start, end));
            i = j;
        }

        return segments;
    }

    /// <summary>第 i 行（或列）里满足最小长度的最长连续暗段。</summary>
    private static (int Start, int End) LongestRun(bool[] binary, int width, int height, bool vertical, int index, double minLength)
    {
        var count = vertical ? height : width;
        var bestStart = -1;
        var bestEnd = -2;
        var runStart = -1;
        for (var k = 0; k <= count; k++)
        {
            var dark = k < count && (vertical
                ? binary[(k * width) + index]
                : binary[(index * width) + k]);
            if (dark && runStart < 0)
            {
                runStart = k;
            }
            else if (!dark && runStart >= 0)
            {
                if (k - runStart > bestEnd - bestStart + 1)
                {
                    bestStart = runStart;
                    bestEnd = k - 1;
                }

                runStart = -1;
            }
        }

        return bestEnd - bestStart + 1 >= minLength ? (bestStart, bestEnd) : (-1, -2);
    }

    private static List<Segment> Dedupe(List<Segment> segments, double tolerance)
    {
        var result = new List<Segment>();
        foreach (var segment in segments.OrderBy(s => s.Pos))
        {
            if (result.Count > 0 && Math.Abs(segment.Pos - result[^1].Pos) <= tolerance)
            {
                var last = result[^1];
                result[^1] = new Segment(
                    (last.Pos + segment.Pos) / 2.0,
                    Math.Min(last.Start, segment.Start),
                    Math.Max(last.End, segment.End));
            }
            else
            {
                result.Add(segment);
            }
        }

        return result;
    }

    /// <summary>横线段与竖线段是否相交（容差 5px）。</summary>
    private static bool Crosses(Segment horizontal, Segment vertical)
    {
        const int tol = BandTolerance + 2;
        return vertical.Start - tol <= horizontal.Pos && horizontal.Pos <= vertical.End + tol
               && horizontal.Start - tol <= vertical.Pos && vertical.Pos <= horizontal.End + tol;
    }

    /// <summary>
    /// 把横线按 x 跨度聚簇，每簇 = 一张表（并排/上下的多张表会分成多簇）。
    /// 返回顺序是"簇内横线数降序" —— 这是**横线多少**的排序，**不代表**"哪张表是用户要的"：
    /// 生产侧要用哪张由 <see cref="Best"/> 决定。
    /// </summary>
    private static List<List<Segment>> SpanClusters(List<Segment> segments, double spanTolerance)
    {
        var clusters = new List<List<Segment>>();
        foreach (var segment in segments.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            var placed = false;
            foreach (var cluster in clusters)
            {
                var reference = cluster[0];
                if (Math.Abs(segment.Start - reference.Start) <= spanTolerance &&
                    Math.Abs(segment.End - reference.End) <= spanTolerance)
                {
                    cluster.Add(segment);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                clusters.Add([segment]);
            }
        }

        return clusters.Where(c => c.Count >= 3).OrderByDescending(c => c.Count).ToList();
    }
}
