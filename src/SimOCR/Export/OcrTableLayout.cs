using System.Globalization;
using SimOCR.Ocr;

using System.Text.RegularExpressions;
namespace SimOCR.Export;

/// <summary>合并单元格：<see cref="Row"/>/<see cref="Column"/> 是左上角（行/列都从 0 起）。</summary>
internal sealed record CellMerge(int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>
/// 简单表格的行列还原。
///
/// 三步（几何阈值来自配置档的 <c>thresholds</c>，与业务无关）：
///   1) 行：按 y 中心聚类，容差取中位行高的 0.6；
///   2) 列：先按 x0 间隙聚类，再做**链式合并** —— 一列文字被检测切成几段时，这些碎片的 x0
///      会落在前一列的水平跨度内，只按间隙聚类会把它们误判成新列（实测会从 3 列变 5 列）；
///   3) 单元格：同一（行,列）里的多段按 x 顺序拼接（同基线直接相连）。
/// 列语义（哪些列是一个字段、字段叫什么）全部来自配置档，见 <see cref="TableProfile"/>。
/// </summary>
internal sealed class OcrTableLayout
{
    private OcrTableLayout(
        IReadOnlyList<IReadOnlyList<string>> cells,
        int rowCount,
        int columnCount,
        IReadOnlyList<CellMerge>? merges = null,
        IReadOnlyList<bool>? columnNumeric = null,
        IReadOnlyList<string>? columnLabels = null,
        string? validationSummary = null,
        IReadOnlyList<string>? validityDetails = null)
    {
        Cells = cells;
        RowCount = rowCount;
        ColumnCount = columnCount;
        Merges = merges ?? [];
        ColumnNumeric = columnNumeric ?? [];
        ColumnLabels = columnLabels ?? [];
        ValidationSummary = validationSummary;
        ValidityDetails = validityDetails ?? [];
    }

    /// <summary>行优先的单元格文本。</summary>
    internal IReadOnlyList<IReadOnlyList<string>> Cells { get; }

    internal int RowCount { get; }

    internal int ColumnCount { get; }

    /// <summary>横向合并（标题/表头跨列）与纵向合并（同一个值跨它的子行）都在这里。</summary>
    internal IReadOnlyList<CellMerge> Merges { get; }

    /// <summary>每一列是否数值列（按配置档声明的字段类型判定）：数值列导出时会净化并写成真数字。</summary>
    internal IReadOnlyList<bool> ColumnNumeric { get; }

    /// <summary>每一列的语义名（显示名来自配置档；认不出时为空串）——导出时写成表头行。</summary>
    internal IReadOnlyList<string> ColumnLabels { get; }

    /// <summary>派生列与算术校验的摘要（供日志/校验问题表用）；没有可校验的内容时为 null。</summary>
    internal string? ValidationSummary { get; }

    /// <summary>校验不一致的明细（最多 10 条）。</summary>
    internal IReadOnlyList<string> ValidityDetails { get; }

    /// <summary>是否像"有列结构的表格"（只有一列时导出 xlsx 意义不大，但仍允许）。</summary>
    internal bool LooksLikeTable => ColumnCount >= 2 && RowCount >= 2;

    /// <summary>派生数量列的显示格式：整数不带小数，否则最多 4 位小数。</summary>
    private static string FormatQuantity(double value)
        => Math.Abs(value - Math.Round(value)) < 0.0001
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>把"一个列承载多个子字段"的列补齐成两列（同一 x 区间），子列拆分来自配置档。</summary>
    private static void ExpandFieldColumns(
        TableProfile profile,
        List<(double Min, double Max)> columns,
        List<string> labels,
        List<bool> numeric)
    {
        for (var i = columns.Count - 1; i >= 0; i--)
        {
            // 列标签可能是显示名（表头路径）也可能是字段名（推断/骨架路径），先归一
            var fieldId = profile.CanonicalId(labels[i]);
            if (fieldId is null || !profile.Expand.TryGetValue(fieldId, out var parts) || parts.Count < 2)
            {
                continue;
            }

            // 子列对外仍用显示名（与原逻辑一致）
            labels[i] = profile.DisplayOf(parts[0]);
            numeric[i] = profile.IsNumeric(parts[0]);
            columns.Insert(i + 1, columns[i]);
            labels.Insert(i + 1, profile.DisplayOf(parts[1]));
            numeric.Insert(i + 1, profile.IsNumeric(parts[1]));
        }
    }

    /// <summary>
    /// 按内容判定"是不是多值列表版式"（一行里带一串同列的多值，另有数值尾列）。
    /// 规则来自配置档的 <c>contentDetection.rules</c>：通用档为空，行业档自行声明。
    /// </summary>
    private static bool DetectsContentLayout(TableProfile profile, IReadOnlyList<OcrLine> lines, string ruleName)
        => profile.DetectByContent(ruleName, lines);

    /// <summary>
    /// 一行里能抽取出的语义字段数（字段集与取值规则都来自配置档）。
    /// 用于"结构塌陷"与"互补行合并"两处判定：前者说明列没切出来，后者说明同一行的字段被拆成了上下两半。
    /// </summary>
    private static int RowCoverage(TableProfile profile, IReadOnlyList<OcrLine> row)
    {
        var text = string.Join(" ", row.OrderBy(l => l.X0).Select(l => l.Text));
        if (text.Length == 0)
        {
            return 0;
        }

        var masked = profile.MaskClaimed(text);
        var count = 0;
        foreach (var field in profile.AllFields)
        {
            if (!profile.HasRule(field))
            {
                continue;
            }

            var value = profile.UsesWholeText(field)
                ? profile.Extract(field, text)
                : profile.ExtractFromMasked(field, masked);
            if (value.Length > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 把「互补」的相邻行合并成一行 —— 只在下游能抽取的语义字段**严格增加**时才合并。
    /// 用于修复同一行的字段被行聚类拆成上下两半的情况（内容覆盖高、逐行字段却全错）。
    /// 判据完全基于内容，因此对"每行已经自足"的版式不会误合并。
    /// </summary>
    private static List<List<OcrLine>> MergeComplementaryRows(TableProfile profile, List<List<OcrLine>> rows)
    {
        if (rows.Count < 2)
        {
            return rows;
        }

        var result = new List<List<OcrLine>>(rows.Count);
        var i = 0;
        while (i < rows.Count)
        {
            if (i + 1 < rows.Count)
            {
                var first = rows[i];
                var second = rows[i + 1];
                var alone = Math.Max(RowCoverage(profile, first), RowCoverage(profile, second));
                var union = RowCoverage(profile, first.Concat(second).ToList());
                if (alone > 0 && union > alone)
                {
                    result.Add(first.Concat(second).ToList());
                    i += 2;
                    continue;
                }
            }

            result.Add(rows[i]);
            i++;
        }

        return result;
    }
    /// <summary>
    /// 列结构是否已经塌陷到必须改用"语义骨架"：列里认出的语义字段太少，
    /// 但**行的内容**其实支撑得起足够的字段（说明值都读到了，只是列没切出来）。
    /// 判定用的字段集与门槛都来自配置档。
    /// </summary>
    private static bool NeedsSemanticSkeleton(
        TableProfile profile,
        IReadOnlyList<string> labels,
        IReadOnlyList<IReadOnlyList<OcrLine>> rows,
        bool multiValueLayout = false)
    {
        var fieldSet = profile.AllFields;

        // 该版式期望的字段数与"列里已认出的字段数"门槛
        var expected = multiValueLayout
            ? profile.Thresholds.MinSemanticFieldsPerRow
            : Math.Max(profile.Thresholds.MinSemanticFieldsPerRow, 4);
        // 列标签可能是显示名也可能是字段名，必须归一后比较
        var semantic = labels
            .Select(profile.CanonicalId)
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (semantic >= expected || rows.Count == 0)
        {
            return false;
        }

        var checks = 0;
        var supported = 0;
        foreach (var row in rows)
        {
            if (checks >= 8)
            {
                break;
            }

            var text = string.Join(" ", row.OrderBy(l => l.X0).Select(l => l.Text));
            if (text.Length == 0)
            {
                continue;
            }

            checks++;
            var masked = profile.MaskClaimed(text);
            var count = 0;
            foreach (var field in fieldSet)
            {
                if (!profile.HasRule(field))
                {
                    continue;
                }

                var value = profile.UsesWholeText(field)
                    ? profile.Extract(field, text)
                    : profile.ExtractFromMasked(field, masked);
                if (value.Length > 0)
                {
                    count++;
                }
            }

            if (count >= expected)
            {
                supported++;
            }
        }

        return checks >= 3 && supported >= (checks / 2) + 1;
    }
    /// <summary>同名标签下取"能解析成数字的格子最多"的那一列；都不行则退回第一个同名列。</summary>
    private static int BestNumericColumn(
        TableProfile profile,
        IReadOnlyList<string> labels,
        IReadOnlyList<IReadOnlyList<string>> table,
        string name)
    {
        var best = -1;
        var bestCount = -1;
        var first = -1;
        for (var c = 0; c < labels.Count; c++)
        {
            if (!profile.LabelIsField(labels[c], name))
            {
                continue;
            }

            if (first < 0)
            {
                first = c;
            }

            var count = 0;
            foreach (var row in table)
            {
                if (c < row.Count && FieldSemantics.TryParseNumber(row[c], out _))
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = c;
            }
        }

        return bestCount > 0 ? best : first;
    }

    /// <summary>把"只有符号没有数字"的子列并进同一字段的前一个子列（如数值列后面的货币符号）。</summary>
    private static List<TableField> MergeSymbolOnlySubs(List<TableField> fields, IReadOnlyList<OcrLine> bodyLines)
    {
        var result = new List<TableField>();
        foreach (var field in fields)
        {
            if (field.Sub.Count < 2)
            {
                result.Add(field);
                continue;
            }

            var subs = field.Sub.ToList();
            var digitCounts = subs
                .Select(s => bodyLines.Count(l =>
                    TableColumnDetector.Overlap(l.X0, l.X1, s.X0, s.X1) >= 0.5 * (l.X1 - l.X0) &&
                    l.Text.Any(char.IsDigit)))
                .ToList();

            for (var i = subs.Count - 1; i > 0; i--)
            {
                if (digitCounts[i] == 0)
                {
                    subs[i - 1] = (subs[i - 1].X0, subs[i].X1);
                    subs.RemoveAt(i);
                }
            }

            result.Add(field with { Sub = subs });
        }

        return result;
    }

    /// <summary>
    /// 按键列把连续行合并成一行：键列非空 ⇒ 开新组，键列为空 ⇒ 属于上一组；
    /// 组内"列表列"去重后空格拼接，其余列取首个非空值。
    ///
    /// 键列与列表列**全部来自配置档**（键列取 <c>roles.cartonCandidates</c>，
    /// 列表列取 <c>features.groupByKeyListFields</c>），因此本方法不认识任何行业词；
    /// 通用档不打开按键分组，所以对普通表格是空操作。
    /// </summary>
    private static List<IReadOnlyList<string>> GroupByKeyColumn(
        TableProfile profile,
        List<IReadOnlyList<string>> rows,
        List<string> labels,
        ref int rowCount)
    {
        // 键列：角色配置里声明的键列候选字段，命中第一个就用它
        var key = -1;
        foreach (var candidate in profile.Roles.CartonCandidates)
        {
            key = profile.IndexOfLabel(labels, candidate);
            if (key >= 0)
            {
                break;
            }
        }

        if (key < 0 || rows.Count < 2)
        {
            return rows;
        }

        // 列表列：配置声明"组内是列表"的字段（一个组里同列出现多个值时依次拼接）
        var listColumns = profile.Features.GroupByKeyListFields
            .Select(field => profile.IndexOfLabel(labels, field))
            .Where(index => index >= 0)
            .Distinct()
            .ToList();

        var grouped = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var seen = new List<HashSet<string>>();
        var hasKey = false;

        void Flush()
        {
            if (current.Count > 0 && current.Any(v => v.Length > 0))
            {
                grouped.Add(current.ToArray());
            }
        }

        foreach (var row in rows)
        {
            var keyValue = key < row.Count ? row[key].Trim() : string.Empty;
            var startsNew = keyValue.Length > 0 && hasKey;
            if (startsNew)
            {
                Flush();
                current = new List<string>();
                seen = new List<HashSet<string>>();
            }

            if (current.Count == 0)
            {
                current = Enumerable.Repeat(string.Empty, labels.Count).ToList();
                seen = Enumerable.Range(0, labels.Count).Select(_ => new HashSet<string>()).ToList();
            }

            for (var c = 0; c < labels.Count && c < row.Count; c++)
            {
                var value = row[c]?.Trim() ?? string.Empty;
                if (value.Length == 0)
                {
                    continue;
                }

                if (listColumns.Contains(c))
                {
                    if (seen[c].Add(value))
                    {
                        current[c] = current[c].Length == 0 ? value : current[c] + " " + value;
                    }
                }
                else if (current[c].Length == 0)
                {
                    current[c] = value;
                }
            }

            if (keyValue.Length > 0)
            {
                hasKey = true;
                current[key] = keyValue;
            }
        }

        Flush();
        if (grouped.Count == 0)
        {
            return rows;
        }

        rowCount = grouped.Count;
        return grouped;
    }
    /// <summary>
    /// 派生列 + 算术校验。全部规则来自配置档：
    ///   * 派生列：<c>derived</c> 里声明"由哪些列按什么公式算出新列"
    ///     （例如 <c>DOZENS + PIECES / perDozen</c> 这类单位换算，只有声明了它的档才会有）；
    ///   * 行级校验：<c>checks.lineAmount</c> 声明"金额列 ≈ 数量列 × 单价列"这类行级等式，
    ///     容差取 绝对值/比例 的大者；
    ///   * 整表校验：<c>checks.documentTotal</c> 声明用明细行求和去对合计行。
    /// 对不上的**只记录、不改数**（静默改数是这类工具最忌讳的）；反算修正会留痕。
    /// </summary>
    private static string? DeriveAndValidate(
        TableProfile profile,
        ref List<IReadOnlyList<string>> table,
        List<string> labels,
        List<bool> numeric,
        List<(double Min, double Max)> columns,
        out IReadOnlyList<string> details)
    {
        var detailList = new List<string>();
        details = detailList;

        // 注意：同一个字段可能对应多列（例如数值列旁边还有个只装货币符号的空列），
        // 必须取**数值最多**的那一列，否则会对着空列做校验（实测会得到"一致 0 行"）。
        var notes = new List<string>();
        foreach (var derived in profile.Derived)
        {
            if (derived.From.Count < 2 || table.Count == 0)
            {
                continue;
            }

            var firstIndex = BestNumericColumn(profile, labels, table, derived.From[0]);
            var secondIndex = BestNumericColumn(profile, labels, table, derived.From[1]);
            if (firstIndex < 0 || secondIndex < 0)
            {
                continue;
            }

            var computed = new List<IReadOnlyList<string>>(table.Count);
            foreach (var row in table)
            {
                var a = firstIndex < row.Count && FieldSemantics.TryParseNumber(row[firstIndex], out var av) ? av : 0;
                var b = secondIndex < row.Count && FieldSemantics.TryParseNumber(row[secondIndex], out var bv) ? bv : 0;
                var value = ApplyDerivedFormula(derived, a, b);
                var list = row.ToList();
                list.Add(FormatQuantity(value));
                computed.Add(list);
            }

            table = computed;

            // 派生列是否进入导出结果由配置决定：通用档不往用户的表里塞人造列。
            if (profile.Features.DerivedColumnsInExport)
            {
                labels.Add(derived.Display);
                numeric.Add(derived.Numeric);
                columns.Add((0, 0)); // 派生列没有物理区间
            }

            notes.Add($"{derived.Display} = {DescribeFormula(profile, derived)}");
        }

        var derivedSummary = notes.Count > 0 ? "派生列 " + string.Join("；", notes) + "；" : string.Empty;

        // 行级算术校验
        var check = profile.Checks.LineAmount;
        if (!check.Enabled || table.Count == 0)
        {
            return derivedSummary.Length > 0 ? derivedSummary.TrimEnd('；') : null;
        }

        var priceIndex = BestNumericColumn(profile, labels, table, check.Price);
        var amountIndex = BestNumericColumn(profile, labels, table, check.Amount);
        var quantityIndex = -1;
        foreach (var candidate in check.Quantity)
        {
            quantityIndex = BestNumericColumn(profile, labels, table, candidate);
            if (quantityIndex >= 0)
            {
                break;
            }
        }

        if (priceIndex < 0 || amountIndex < 0 || quantityIndex < 0)
        {
            return derivedSummary.Length > 0 ? derivedSummary.TrimEnd('；') : null;
        }

        // 留痕里的列名一律取配置档的显示名 —— 代码里不写死任何一种语言的列名
        var quantityLabel = profile.DisplayOf(check.Quantity.Count > 0 ? check.Quantity[0] : null);
        var priceLabel = profile.DisplayOf(check.Price);
        var amountLabel = profile.DisplayOf(check.Amount);

        var perDozen = check.PerDozen <= 0 ? 12 : check.PerDozen;
        var firstSourceIndex = profile.Derived.Count > 0 && profile.Derived[0].From.Count > 0
            ? BestNumericColumn(profile, labels, table, profile.Derived[0].From[0])
            : -1;
        var secondSourceIndex = profile.Derived.Count > 0 && profile.Derived[0].From.Count > 1
            ? BestNumericColumn(profile, labels, table, profile.Derived[0].From[1])
            : -1;

        var ok = 0;
        var reversed = 0;
        var bad = 0;
        for (var i = 0; i < table.Count; i++)
        {
            var row = table[i];
            var hasQuantity = FieldSemantics.TryParseNumber(row[quantityIndex], out var quantity);
            var hasPrice = FieldSemantics.TryParseNumber(row[priceIndex], out var price);
            var hasAmount = FieldSemantics.TryParseNumber(row[amountIndex], out var amount);
            if (!hasPrice || !hasAmount || price <= 0)
            {
                continue;
            }

            var expected = quantity * price;
            var tolerance = Math.Max(check.ToleranceAbs, expected * check.ToleranceRatio);
            if (hasQuantity && Math.Abs(expected - amount) <= tolerance)
            {
                ok++;
                continue;
            }

            // 对不上时是否**按金额反算并写回**识别结果由配置决定（默认关闭）：
            // 静默改写 OCR 的识别结果风险很高，只有行业档显式打开时才会做，并且每次都留痕。
            if (!check.ReverseWriteBack)
            {
                bad++;
                if (detailList.Count < 10)
                {
                    detailList.Add(
                        $"第 {i + 1} 行：{quantityLabel} {FormatQuantity(quantity)} × {priceLabel} {price:0.##} = {expected:0.##}，但{amountLabel}是 {amount:0.##}");
                }

                continue;
            }

            var corrected = amount / price;
            var units = Math.Round(corrected * perDozen, MidpointRounding.AwayFromZero);
            var snapped = units / perDozen;
            var snappedAmount = snapped * price;
            if (Math.Abs(snappedAmount - amount) <= Math.Max(check.ToleranceAbs, amount * check.ToleranceRatio))
            {
                var list = table[i].ToList();
                list[quantityIndex] = FormatQuantity(snapped);
                if (firstSourceIndex >= 0 && firstSourceIndex < list.Count)
                {
                    list[firstSourceIndex] = Math.Floor(units / perDozen).ToString(CultureInfo.InvariantCulture);
                }

                if (secondSourceIndex >= 0 && secondSourceIndex < list.Count)
                {
                    // 反算出来的余数为 0 时**留空**（不是写 0），保持与原件的表示一致
                    var remainder = units % perDozen;
                    list[secondSourceIndex] = remainder == 0
                        ? string.Empty
                        : remainder.ToString(CultureInfo.InvariantCulture);
                }

                table[i] = list;
                reversed++;
                if (detailList.Count < 10)
                {
                    detailList.Add(
                        $"第 {i + 1} 行：已按{amountLabel}反算 {quantityLabel} {FormatQuantity(quantity)} → {FormatQuantity(snapped)}" +
                        $"（{amountLabel} {amount:0.##} ÷ {priceLabel} {price:0.##}）");
                }

                continue;
            }

            bad++;
            if (detailList.Count < 10)
            {
                detailList.Add(
                    $"第 {i + 1} 行：{quantityLabel} {FormatQuantity(quantity)} × {priceLabel} {price:0.##} = {expected:0.##}，但{amountLabel}是 {amount:0.##}（反算也不成立）");
            }
        }

        // 整表合计校验：找到含合计关键字的行，用**明细行的求和**去对它的金额列。
        // 只记录不改数 —— 合计对不上往往是"某行金额识别错"或"原件本身不平"，都需要人来判断。
        var totalCheck = profile.Checks.DocumentTotal;
        if (totalCheck.Enabled)
        {
            var totalRow = -1;
            for (var i = 0; i < table.Count && totalRow < 0; i++)
            {
                var row = table[i];
                for (var c = 0; c < row.Count; c++)
                {
                    if (row[c] is not { } cell)
                    {
                        continue;
                    }

                    if (totalCheck.TotalKeywords.Any(k =>
                            !string.IsNullOrWhiteSpace(k) &&
                            cell.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        totalRow = i;
                        break;
                    }
                }
            }

            var totalAmountIndex = BestNumericColumn(profile, labels, table, totalCheck.Amount);
            if (totalRow > 0 && totalAmountIndex >= 0 && totalAmountIndex < table[totalRow].Count &&
                FieldSemantics.TryParseNumber(table[totalRow][totalAmountIndex], out var declaredTotal))
            {
                var detailSum = 0.0;
                for (var i = 0; i < totalRow; i++)
                {
                    if (totalAmountIndex < table[i].Count &&
                        FieldSemantics.TryParseNumber(table[i][totalAmountIndex], out var value))
                    {
                        detailSum += value;
                    }
                }

                var difference = Math.Abs(detailSum - declaredTotal);
                var totalTolerance = Math.Max(totalCheck.ToleranceAbs, declaredTotal * totalCheck.ToleranceRatio);
                detailList.Add(difference <= totalTolerance
                    ? $"合计校验：{amountLabel} {declaredTotal:0.##} 与明细合计 {detailSum:0.##} 相符"
                    : $"合计校验：{amountLabel} {declaredTotal:0.##} ≠ 明细合计 {detailSum:0.##}（差 {difference:0.##}）");
            }
        }

        return $"{derivedSummary}算术校验：一致 {ok} 行 / 反算修正 {reversed} 行 / 仍不一致 {bad} 行";
    }

    /// <summary>按配置的公式名计算派生列的值。</summary>
    private static double ApplyDerivedFormula(DerivedColumn derived, double a, double b)
    {
        var perDozen = derived.PerDozen <= 0 ? 12 : derived.PerDozen;
        return derived.Formula switch
        {
            "sum" => a + b,
            "difference" => a - b,
            "product" => a * b,
            "dozens_plus_pieces_over" => a + (b / perDozen),
            "pieces_over" => a / perDozen,
            _ => a + (b / perDozen),
        };
    }

    /// <summary>公式的人类可读描述（用于状态条摘要）。字段用配置里的显示名，避免把内部 id 暴露给用户。</summary>
    private static string DescribeFormula(TableProfile profile, DerivedColumn derived)
    {
        var perDozen = derived.PerDozen <= 0 ? 12 : derived.PerDozen;
        var a = derived.From.Count > 0 ? profile.DisplayOf(derived.From[0]) : "A";
        var b = derived.From.Count > 1 ? profile.DisplayOf(derived.From[1]) : "B";
        return derived.Formula switch
        {
            "sum" => $"{a} + {b}",
            "difference" => $"{a} - {b}",
            "product" => $"{a} × {b}",
            "pieces_over" => $"{a} / {perDozen:0.##}",
            _ => $"{a} + {b}/{perDozen:0.##}",
        };
    }

    /// <summary>是否"像数字"（数字、千分位、小数点、负号、斜杠、百分号、空格）——用于避免把数字列做纵向合并。</summary>
    private static bool IsNumericLike(string text)
    {
        var hasDigit = false;
        foreach (var c in text)
        {
            if (char.IsDigit(c))
            {
                hasDigit = true;
                continue;
            }

            if (c is '.' or ',' or '-' or '/' or '%' or ' ' or '$' or '\u00A5' or '\uFFE5')
            {
                continue;
            }

            return false;
        }

        return hasDigit;
    }

    /// <summary>
    /// 从"用户编辑过的文本"构造表格：一行一记录、制表符分列
    /// （识别结果的纯文本在列间隙大时本来就用制表符分隔，粘进 Excel 能直接对齐）。
    /// </summary>
    internal static OcrTableLayout FromText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var raw = new List<string[]>(lines.Length);
        var columns = 1;
        foreach (var line in lines)
        {
            var cells = line.Split('\t');
            columns = Math.Max(columns, cells.Length);
            raw.Add(cells);
        }

        var rows = new List<IReadOnlyList<string>>(raw.Count);
        foreach (var cells in raw)
        {
            var padded = new string[columns];
            for (var i = 0; i < columns; i++)
            {
                padded[i] = i < cells.Length ? cells[i] : string.Empty;
            }

            rows.Add(padded);
        }

        return new OcrTableLayout(rows, rows.Count, columns);
    }

    /// <summary>
    /// 用"已经算好的单元格 + 列属性"重建布局（增强识别里做完**数值/格式校验**后回写用）。
    /// 不改语义、只换内容，保证校验结果进入显示与导出。
    /// </summary>
    internal static OcrTableLayout FromCells(
        IReadOnlyList<IReadOnlyList<string>> cells,
        IReadOnlyList<bool> columnNumeric,
        IReadOnlyList<string> columnLabels,
        IReadOnlyList<CellMerge>? merges = null,
        string? validationSummary = null,
        IReadOnlyList<string>? validityDetails = null)
    {
        var columns = Math.Max(columnLabels.Count, cells.Count > 0 ? cells.Max(r => r.Count) : 0);
        return new OcrTableLayout(cells, cells.Count, columns, merges, columnNumeric, columnLabels, validationSummary, validityDetails);
    }

    /// <summary>
    /// 还原一张表。**配置档的唯一读取点就在这里**：进入构建前取一次快照，交给
    /// <see cref="Build(OcrDocument, TableProfile, Action?)"/>，整条构建链都用那一份
    ///（过去行覆盖 / 语义骨架 / 列展开 / 按键分组 / 派生校验各自重读
    /// <see cref="TableProfiles.Current"/>，构建中途换档就会让**同一张表混用两个档**）。
    /// </summary>
    internal static OcrTableLayout Build(OcrDocument document) => Build(document, TableProfiles.Current);

    /// <summary>
    /// 用指定配置档还原一张表 —— 本方法对静态状态**零依赖**，结果只由
    /// (<paramref name="document"/>, <paramref name="profile"/>) 决定。
    /// </summary>
    /// <param name="afterProfileCaptured">
    /// 自检注入点（生产路径恒为 null）：档位快照已取、真正的构建还没开始时回调一次。
    /// 用来验收"构建中途调用 <see cref="TableProfiles.Initialize"/> 换到另一个档，
    /// 结果仍与开始时的档一致"（断言本体见
    /// <c>tests/SimOCR.Tests/PureLogic/ProfileSourceChecks.cs</c>；0.10.7 起由控制台运行器的
    /// <c>--case=profile-source</c> 执行，门禁用例 <c>profile-source</c> 钉着它）。
    /// </param>
    internal static OcrTableLayout Build(
        OcrDocument document,
        TableProfile profile,
        Action? afterProfileCaptured = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        afterProfileCaptured?.Invoke();

        // 去印章：压在红章上的文字行不参与表格（印章会污染列结构）
        var lines = document.Lines.Where(l => !l.OnStamp).ToList();
        if (lines.Count == 0)
        {
            lines = [.. document.Lines];
        }

        if (lines.Count == 0)
        {
            return new OcrTableLayout([], 0, 0);
        }

        // 字高中位数只有一份实现；列间隙阈值与"行聚类容差"都由它派生。
        var medianHeight = RowClustering.MedianHeight(lines);

        // 1) 行 —— 行分组的算法只有一份：CenterY 锚 + 中位字高容差
        var rows = RowClustering.Bands(lines, medianHeight);

        // 2) 先找表头行：整页识别时页眉（标题 / 日期…）会污染列结构，
        //    实测整页会退化成 1 列；因此**表体限定在表头之后**。标签组按页面文字选。
        //    档位来自本方法的参数（构建开始时的那一份），下面不再重读静态。
        var group = TableFieldBinder.ResolveGroup(profile, lines);
        var labels = TableFieldBinder.ExpandLabels(group.Labels);
        // 多值列表版式（需要互补行合并 / 按键分组的版式）：由**配置**决定，
        // 而不是由组名或某个行业的标题关键字决定。
        var multiValueLayout = profile.IsMultiValueLayout;
        var header = TableFieldBinder.FindHeaderLine(profile, rows, labels);
        var bodyRows = header is null ? rows : rows.Skip(header.Value.Index + 1).ToList();
        var bodyLines = bodyRows.SelectMany(r => r).ToList();

        // 配置声明的"内容特征"判定：不依赖标题，只看内容
        foreach (var rule in profile.ContentDetection.Rules)
        {
            if (!string.IsNullOrWhiteSpace(rule.Name) && DetectsContentLayout(profile, bodyLines, rule.Name!))
            {
                multiValueLayout = true;
                break;
            }
        }

        // 互补行合并：只在配置允许时做（普通表格的相邻行也可能互补，
        // 但下游把它们当两行，合并会破坏结构）。
        if (multiValueLayout && profile.Features.MergeComplementaryRows)
        {
            bodyRows = MergeComplementaryRows(profile, bodyRows);
            bodyLines = bodyRows.SelectMany(r => r).ToList();
        }
        if (bodyLines.Count == 0)
        {
            bodyRows = rows;
            bodyLines = [.. lines];
        }

        // 3) 列：列切分算法本身与业务无关（见 TableColumnDetector 的类注释）。
        //    间隙阈值与**字高**挂钩，而不是表格宽度的百分比 —— 后者会把相邻字段粘成一列。
        var minGap = TableColumnDetector.MinGap(medianHeight, profile.Thresholds.GapFactor, profile.Thresholds.MinGapPixels);
        var columns = TableColumnDetector.Detect(bodyLines, minGap);

        // 4) 列语义：把物理列归并成**语义字段**，没有表头覆盖的列向左继承
        //    （见 TableFieldBinder 的类注释）。
        var fields = header is null
            ? new List<TableField>()
            : TableFieldBinder.BuildFields(profile, columns, header.Value.Matches, group.Labels.Keys.ToList(), bodyLines);

        // 把"只有符号没有数字"的子列并进同一字段的前一个子列：
        // 例如数值列后面的货币符号（常被识别成汉字）本来会白占一列。
        fields = MergeSymbolOnlySubs(fields, bodyLines);

        // 网格列用字段的**子区间**：一个物理列可能对应两个语义子列，
        // 拆开后下游的字段抽取与纵向合并才能各就各位。
        var gridColumns = fields.Count > 0
            ? fields.SelectMany(f => f.Sub).Select(s => (Min: s.X0, Max: s.X1)).ToList()
            : columns;

        // 每一列是不是数值列 + 列名：都由**该列对应的语义字段**决定。
        //
        // 关键区分：field.Sub 是**物理子列**（表头把相邻同标签列合并后留下的各段 x 区间），
        // 而配置里的 expand 声明的是**语义子列**（如 QUANTITY → 打数 + 件数）。两者混在一起
        // 会造成"父名按子列数重复 + 后面 ExpandFieldColumns 再展开一次"的**双重展开**：
        // 实测某张整页样本（发票示例档）因此输出 货号|尺码|货号|尺码|打数|打数|单价|金额|数量(打)
        // 共 9 列（值成对重复），而且 **件数整列丢失** → 派生列 数量(打)=打数+件数/12
        // 只算了打数（16 而不是 16.5）。
        var gridLabels = new List<string>();
        var gridNumeric = new List<bool>();
        if (fields.Count > 0)
        {
            foreach (var field in fields)
            {
                var described = FieldSemantics.Describe(profile, field.Label);
                var fieldId = profile.CanonicalId(field.Label);
                var semanticSubs = fieldId is not null && profile.Expand.TryGetValue(fieldId, out var parts)
                    ? parts
                    : null;

                if (semanticSubs is { Count: > 1 } && semanticSubs.Count == field.Sub.Count)
                {
                    // 物理子列数恰好等于语义子列数：直接按语义子列命名（打数/件数、货号/尺码），
                    // 下游就能各就各位地填值，也不需要再让 ExpandFieldColumns 动手。
                    foreach (var sub in semanticSubs)
                    {
                        gridLabels.Add(profile.DisplayOf(sub));
                        gridNumeric.Add(profile.IsNumeric(sub));
                    }
                }
                else if (profile.Features.HeaderNamesForMultiColumn)
                {
                    // 通用档口径（通用表兜底＝表头原样 + 数据格，不猜字段名）：
                    // 列名用**表头原文**，横跨多列时第二列起加序号。这样既不会出现
                    // `描述|描述` 这种无区分度的重名，也不会把"尺码"说成"描述"。
                    // 序号后缀不在 Fields 里 → CanonicalId 返回 null → 不当成语义字段。
                    //（变量名避开外层的 header —— 那是整张表的表头行对象。）
                    var headerText = string.IsNullOrWhiteSpace(field.Label) ? null : field.Label.Trim();
                    for (var i = 0; i < field.Sub.Count; i++)
                    {
                        gridLabels.Add(headerText is null
                            ? $"第 {gridLabels.Count + 1} 列"
                            : i == 0 ? headerText : $"{headerText}({i + 1})");
                        gridNumeric.Add(described.Numeric);
                    }
                }
                else if (field.Sub.Count > 1)
                {
                    // 没有语义子列声明、但表头确实横跨了多列（物理过切分，或一个表头覆盖两个数据列）。
                    // **几何与列名都保持原样**：每个子列各出一列、都用父名。
                    // 为什么不加 (2)/(3) 后缀：这类子列里常常有一个是"只有货币符号/单位"的列，
                    // 它会被 CellValidator 清空并作为**全空列整列删除** —— 后缀会留在活下来的那一列上，
                    // 反而把列名改成了对不上参考的 金额(2)（该样本实测）。
                    // 为什么不合并成一个区间：合并会改变下游按坐标取值的落点，实测通用档会把值取错。
                    for (var i = 0; i < field.Sub.Count; i++)
                    {
                        gridLabels.Add(described.Name);
                        gridNumeric.Add(described.Numeric);
                    }
                }
                else
                {
                    gridLabels.Add(described.Name);
                    gridNumeric.Add(described.Numeric);
                }
            }
        }
        else
        {
            for (var i = 0; i < columns.Count; i++)
            {
                gridNumeric.Add(false);
            }

            // 没有表头时按**列内容**推断语义。门槛：列数太少、或连配置声明为
            // item / size 角色的字段都没认出来时不做推断 —— 实测乱猜会把本来"值都在"的页面改差。
            // 宁可留空（退回按坐标取整段文字）。
            var inferred = columns.Count >= profile.Thresholds.MinColumnsForInference
                ? TableFieldBinder.InferColumnLabels(profile, columns, bodyLines, group)
                : new List<string?>(new string?[columns.Count]);
            var identity = new[] { profile.ItemField, profile.SizeField }
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToArray();
            if (identity.Length > 0 && !inferred.Any(l => l is not null && identity.Contains(l)))
            {
                inferred = new List<string?>(new string?[columns.Count]);
            }

            for (var i = 0; i < columns.Count; i++)
            {
                gridLabels.Add(inferred[i] ?? string.Empty);
                if (inferred[i] is { } name)
                {
                    gridNumeric[i] = FieldSemantics.Describe(profile, name).Numeric;
                }
            }
        }

        // 字段粒度对齐：配置的 expand 声明了"一个物理列其实是两个语义子列"的情况，
        // 物理检测常把两者并成一列（尤其遇到桥框），这里按语义把单列补成两列
        //（同一 x 区间，靠后面的行级抽取分别填值）。
        ExpandFieldColumns(profile, gridColumns, gridLabels, gridNumeric);

        var columnOfLine = new Dictionary<OcrLine, int>();

        // 列结构塌陷时的「语义骨架」：少数行的桥框会把多列并成一个框，
        // 列检测无从下手（低谷根本不存在），但**内容其实都读到了**。
        // 这时不猜列了，改用配置声明的语义列骨架（区间留空），全部靠行级抽取填值。
        //
        // 安全阀：骨架只在"确实能比现有列多认出字段"时才采用。
        // 骨架会丢掉真实列几何（全部改成同一 x 区间），若现成列的字段数并不更少，
        // 用骨架反而会把已经切好的表改坏。
        var existingFields = gridLabels
            .Select(profile.CanonicalId)
            .Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (NeedsSemanticSkeleton(profile, gridLabels, bodyRows, multiValueLayout))
        {
            var skeleton = profile.SkeletonFor(group, multiValueLayout);
            var skeletonFields = skeleton
                .Select(profile.CanonicalId)
                .Where(id => id is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (skeleton.Count > 0 && skeletonFields > existingFields)
            {
                // 骨架里的名字是内部字段名；对外仍用显示名（表头/界面与其它路径保持一致）
                gridLabels = skeleton.Select(profile.DisplayOf).ToList();
                gridNumeric = gridLabels.Select(profile.IsNumeric).ToList();
                gridColumns = gridLabels.Select(_ => (Min: 0.0, Max: 0.0)).ToList();
                foreach (var line in bodyLines)
                {
                    columnOfLine[line] = 0;
                }
            }
        }
        else
        {
            foreach (var line in bodyLines)
            {
                var index = 0;
                var bestOverlap = double.NegativeInfinity;
                for (var c = 0; c < gridColumns.Count; c++)
                {
                    var overlap = TableColumnDetector.Overlap(line.X0, line.X1, gridColumns[c].Min, gridColumns[c].Max);
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        index = c;
                    }
                }

                columnOfLine[line] = index;
            }
        }

        // 5) 单元格 + 横向合并识别
        //    列分界用**相邻列的中点**：文字框往往只压到列的一部分，按"与列的重叠比例"判据会漏判
        //    （实测居中标题落进 B1、mergeCells 为空）。看文字的 x0 / x1 分别落在哪一列，
        //    起止列不同即为跨列；文本写进合并区左上角。
        var boundaries = new double[Math.Max(0, gridColumns.Count - 1)];
        for (var c = 0; c + 1 < gridColumns.Count; c++)
        {
            boundaries[c] = (gridColumns[c].Max + gridColumns[c + 1].Min) / 2;
        }

        int ColumnAt(double x)
        {
            var index = 0;
            while (index < boundaries.Length && x > boundaries[index])
            {
                index++;
            }

            return index;
        }

        // 同一单元格里的多段文字：间隙大就补空格，最后去掉首尾空白
        string JoinText(IReadOnlyList<OcrLine> ordered)
        {
            var text = string.Empty;
            OcrLine? previous = null;
            foreach (var line in ordered)
            {
                if (previous is not null && line.X0 - previous.X1 > medianHeight * 0.35)
                {
                    text += " ";
                }

                text += line.Text;
                previous = line;
            }

            return text.Trim();
        }

        var width = lines.Max(l => l.X1) - lines.Min(l => l.X0); // 表格总宽（"整行合并"判据用）
        var merges = new List<CellMerge>();
        var table = new List<IReadOnlyList<string>>(bodyRows.Count);
        for (var rowIndex = 0; rowIndex < bodyRows.Count; rowIndex++)
        {
            var row = bodyRows[rowIndex];
            var cells = new string[gridColumns.Count];
            Array.Fill(cells, string.Empty);

            var cellGroups = row.GroupBy(l => columnOfLine[l]).OrderBy(g => g.Key).ToList();

            // 整行合并（最常见的"标题跨全表"）：这一行只识别到一个文字组，且横向跨度 ≥ 表格宽度的 40%。
            // 为什么必须走行级判断：列聚类会把"居中标题"和它右边的列文字**链式并进同一列**
            //（列聚类是为了修复被切碎的单元格，见上面 §3），于是按列看永远显示"只占一列"。
            if (cellGroups.Count == 1 && gridColumns.Count >= 2)
            {
                var single = cellGroups[0].OrderBy(l => l.X0).ToList();
                if (single[^1].X1 - single[0].X0 >= width * 0.4)
                {
                    cells[0] = JoinText(single);
                    merges.Add(new CellMerge(rowIndex, 0, 1, gridColumns.Count));
                    // 行级语义抽取：一行常被识别成一个大框（桥框），
                    // 先按模式遮蔽"已属于其它字段"的部分，再把剩下的数字按字段分配 ——
                    // 否则文本字段（item / size 之类）里的数字会被当成数值字段的值。
                    // 多值列表版式不做"行级数值覆盖"：它的数值都靠列内取值，
                    // 行级取数会把别的字段里的数字抢过来。
                    var usable = gridLabels.Any(l => l.Length > 0 && profile.HasRule(l));
                    if (usable && !profile.BlocksRowLevelOverride(gridLabels))
                    {
                        var rowText = JoinText(row.OrderBy(l => l.X0).ToList());
                        var masked = profile.MaskClaimed(rowText);
                        for (var c = 0; c < gridColumns.Count && c < gridLabels.Count; c++)
                        {
                            var field = gridLabels[c];
                            if (field.Length == 0)
                            {
                                continue;
                            }

                            var value = profile.UsesWholeText(field)
                                ? profile.Extract(field, rowText)
                                : profile.ExtractFromMasked(field, masked);
                            if (value.Length > 0)
                            {
                                cells[c] = value;
                            }
                        }
                    }

                    table.Add(cells);
                    continue;
                }
            }

            foreach (var cellGroup in cellGroups)
            {
                var ordered = cellGroup.OrderBy(l => l.X0).ToList();

                // 起止列：这一组文字的 x0 / x1 分别落在哪一列（相邻列中点分界）
                var first = ColumnAt(ordered.Min(l => l.X0));
                var last = ColumnAt(ordered.Max(l => l.X1));

                cells[first] = FieldSemantics.CleanCell(
                    JoinText(ordered),
                    first < gridNumeric.Count && gridNumeric[first]);
                if (last > first)
                {
                    merges.Add(new CellMerge(rowIndex, first, 1, last - first + 1));
                }
            }

            // 语义抽取：对有取值规则的列，把**所有与该列相交的框**
            // 交给该字段的规则去取自己那一份 —— 这样"一行被认成一个框"（桥框）也能取对值，
            // 而按 x 切分做不到（一个框只落进一列，另一列就空了）。
            for (var c = 0; c < gridColumns.Count && c < gridLabels.Count; c++)
            {
                var field = gridLabels[c];
                if (!profile.HasRule(field))
                {
                    continue;
                }

                var touching = row
                    .Where(l => TableColumnDetector.Overlap(l.X0, l.X1, gridColumns[c].Min, gridColumns[c].Max) > 0)
                    .OrderBy(l => l.X0)
                    .ToList();
                if (touching.Count == 0)
                {
                    continue;
                }

                var text = JoinText(touching);
                var value = profile.Extract(field, text);

                // 取不到时按配置允许的"向左借文"再试一次：
                // 有些字段在原件里和它左边的字段挤在同一段文字里（两个数字一前一后），
                // 单独看这一列会一个都取不到。
                if (value.Length == 0 && profile.BorrowLeft.TryGetValue(field, out var donors))
                {
                    for (var p = c - 1; p >= 0; p--)
                    {
                        var leftField = gridLabels[p];
                        if (string.Equals(leftField, field, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!donors.Contains(leftField, StringComparer.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        var leftText = JoinText(row
                            .Where(l => TableColumnDetector.Overlap(l.X0, l.X1, gridColumns[p].Min, gridColumns[p].Max) > 0)
                            .OrderBy(l => l.X0)
                            .ToList());
                        value = profile.Extract(field, leftText + " " + text);
                        break;
                    }
                }

                if (value.Length > 0)
                {
                    cells[c] = value;
                }
            }

            table.Add(cells);
        }

        // 纵向合并：同一列里"上面有内容、下面连续空"的格按原件的分组习惯合并
        //    （同一个值跨它的子行只写一次，下面几行同一列是空的）。
        //    只在**非数字**列上做：数字列右对齐、偶发空值不该被合并。
        for (var c = 0; c < gridColumns.Count; c++)
        {
            var row = 0;
            while (row < bodyRows.Count)
            {
                var text = table[row][c];
                if (text.Length == 0 || IsNumericLike(text))
                {
                    row++;
                    continue;
                }

                var span = 1;
                while (row + span < bodyRows.Count && table[row + span][c].Length == 0)
                {
                    span++;
                }

                if (span > 1)
                {
                    merges.Add(new CellMerge(row, c, span, 1));
                }

                row += span;
            }
        }

        // 6) 多值版式的行合并（按键列把连续行并成一行），以及派生列与算术校验：
        //    两者都由配置档声明（features.groupByKey / derived / checks），通用档两者都不生效。
        var rowCount = bodyRows.Count;
        if (profile.Features.GroupByKey)
        {
            table = GroupByKeyColumn(profile, table, gridLabels, ref rowCount);
        }

        var validationSummary = DeriveAndValidate(
            profile, ref table, gridLabels, gridNumeric, gridColumns, out var validityDetails);

        return new OcrTableLayout(
            table, rowCount, gridColumns.Count, merges, gridNumeric, gridLabels,
            validationSummary, validityDetails);
    }
}
