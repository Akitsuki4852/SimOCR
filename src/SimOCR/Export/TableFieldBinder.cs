using System.Text;
using System.Text.RegularExpressions;
using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>一个**语义字段**（如 DESCRIPTION / QUANTITY / UNIT PRICE / AMOUNT），可含多个子区间。</summary>
/// <param name="Label">字段名（未能绑定的列为 null）。</param>
/// <param name="X0">字段左边界。</param>
/// <param name="X1">字段右边界。</param>
/// <param name="Sub">该字段覆盖的物理子列区间（如 QUANTITY 下可能还有两个"槽位"子列）。</param>
internal sealed record TableField(string? Label, double X0, double X1, IReadOnlyList<(double X0, double X1)> Sub);

/// <summary>
/// 列语义绑定：把"按识别框切出来的物理列"归并成**语义字段**。
///
/// 规则（全部与业务无关）：
///   * 表头行 = 匹配标签数最多的一行（达到配置里的下限才算找到）；
///   * 标签与列的绑定按**横向重叠**；同一个框里挤了多个标签时，按标签文字长度切分该框；
///   * 没有表头覆盖的列**向左继承**最近的已标注列（左侧没有才向右）；
///   * 相邻同标签的列合并成一个字段，并保留各自的 <c>Sub</c> 子区间（供后续拆子列用）。
///
/// 所有表头标签、字段名与"哪些字段算 size / carton 角色"都来自 <see cref="TableProfile"/>。
/// </summary>
internal static class TableFieldBinder
{
    /// <summary>只保留字母数字汉字并大写（把表头文字归一化后再去匹配标签）。</summary>
    internal static string Squash(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || (c >= 0x4E00 && c <= 0x9FFF))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>在 squash 基础上把易混字符归一：O→0、I→1、T→I（这样 STZE 与 SIZE 能对上）。</summary>
    internal static string Fuzzy(string? text)
    {
        var squashed = Squash(text);
        var builder = new StringBuilder(squashed.Length);
        foreach (var c in squashed)
        {
            builder.Append(c switch
            {
                'O' => '0',
                'I' => '1',
                'T' => 'I',
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>按整页文字挑出命中的标签组（组的选择规则来自配置）。</summary>
    internal static FieldGroup ResolveGroup(TableProfile profile, IEnumerable<OcrLine> lines)
    {
        var joined = Squash(string.Join(" ", lines.Select(l => l.Text)));
        return profile.ResolveGroup(joined);
    }

    /// <summary>把"字段名 → 标签写法列表"统一成只读字典（供表头匹配使用）。</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ExpandLabels(
        IReadOnlyDictionary<string, List<string>> labels)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, list) in labels)
        {
            result[field] = list.ToArray();
        }

        return result;
    }

    /// <summary>
    /// 找表头行：返回行索引与该行匹配到的 (字段名, 识别行)；匹配数低于配置下限视为没找到。
    /// </summary>
    internal static (int Index, List<(string Label, OcrLine Box)> Matches)? FindHeaderLine(
        TableProfile profile,
        IReadOnlyList<List<OcrLine>> rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> labels)
    {
        // 标签文本（归一化）→ 字段名
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, variants) in labels)
        {
            foreach (var variant in variants)
            {
                var key = Fuzzy(variant);
                if (key.Length > 0)
                {
                    wanted[key] = field;
                }
            }
        }

        var bestCount = 0;
        var bestIndex = -1;
        List<(string Label, OcrLine Box)>? bestMatches = null;

        for (var index = 0; index < rows.Count; index++)
        {
            var matched = new List<(string, OcrLine)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in rows[index])
            {
                var key = Fuzzy(line.Text);
                foreach (var (wantedKey, field) in wanted)
                {
                    if (seen.Contains(field))
                    {
                        continue;
                    }

                    if (key == wantedKey || (wantedKey.Length >= 3 && key.Contains(wantedKey)))
                    {
                        matched.Add((field, line));
                        seen.Add(field);
                    }
                }
            }

            if (matched.Count > bestCount)
            {
                bestCount = matched.Count;
                bestIndex = index;
                bestMatches = matched;
            }
        }

        var minimum = Math.Max(1, profile.Thresholds.MinHeaderLabels);
        return bestCount >= minimum ? (bestIndex, bestMatches!) : null;
    }

    /// <summary>把表头标签框整理成 [(字段名, x0, x1)]；共用同一个框的标签按文字长度切分该框。</summary>
    internal static List<(string Label, double X0, double X1)> HeaderAnchors(
        IReadOnlyList<(string Label, OcrLine Box)> matches,
        IReadOnlyList<string> fieldOrder)
    {
        var anchors = new List<(string, double, double)>();

        int Order(string field)
        {
            for (var i = 0; i < fieldOrder.Count; i++)
            {
                if (string.Equals(fieldOrder[i], field, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 99;
        }

        foreach (var group in matches.GroupBy(m => m.Box))
        {
            var box = group.Key;
            var ordered = group.Select(g => g.Label).OrderBy(Order).ToList();
            var weights = ordered.Select(l => Math.Max(1, l.Length)).ToList();
            var total = (double)weights.Sum();
            var x = box.X0;
            var span = box.X1 - box.X0;
            for (var i = 0; i < ordered.Count; i++)
            {
                var width = total > 0 ? span * weights[i] / total : span / ordered.Count;
                anchors.Add((ordered[i], x, x + width));
                x += width;
            }
        }

        return anchors.OrderBy(a => a.Item2).Select(a => (a.Item1, a.Item2, a.Item3)).ToList();
    }

    private static readonly char[] InchMarks = ['"', '\u201C', '\u201D', '\u2033'];

    /// <summary>列内容是否"像尺寸/规格"：含英寸符号，或有独立的字母尺码 token。</summary>
    internal static bool IsSizeLike(TableProfile profile, IEnumerable<OcrLine> lines)
    {
        foreach (var line in lines)
        {
            var text = line.Text ?? string.Empty;
            if (text.IndexOfAny(InchMarks) >= 0)
            {
                return true;
            }

            foreach (var token in text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var cleaned = token.Trim().Trim('"', '\u201C', '\u201D', '\u2033', '\'', ' ');
                if (cleaned.Length == 0)
                {
                    continue;
                }

                if (profile.ContainsLetterSize(cleaned))
                {
                    return true;
                }

                // 形如 8XL / 12XS 这类带数字前缀的字母尺码
                foreach (var size in profile.LetterSizes.Where(s => s.Length >= 2))
                {
                    if (cleaned.EndsWith(size, StringComparison.OrdinalIgnoreCase) &&
                        cleaned[..^size.Length].All(char.IsDigit) &&
                        cleaned.Length > size.Length)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static List<OcrLine> MajorBoxes(double x0, double x1, IReadOnlyList<OcrLine> lines)
        => lines.Where(l => l.X1 - l.X0 > 0
                            && TableColumnDetector.Overlap(l.X0, l.X1, x0, x1) >= 0.5 * (l.X1 - l.X0))
            .ToList();

    /// <summary>把物理列归并成语义字段（按 x 升序）。</summary>
    internal static List<TableField> BuildFields(
        TableProfile profile,
        IReadOnlyList<(double Min, double Max)> columns,
        IReadOnlyList<(string Label, OcrLine Box)> matches,
        IReadOnlyList<string> fieldOrder,
        IReadOnlyList<OcrLine> bodyLines)
    {
        var anchors = HeaderAnchors(matches, fieldOrder);
        var sizeField = profile.Roles.Size;
        var cartonFields = profile.Roles.CartonCandidates;
        var resolved = new List<(double X0, double X1, string? Label)>();

        foreach (var column in columns)
        {
            var hits = anchors
                .Select(a => (a.Label, Overlap: TableColumnDetector.Overlap(column.Min, column.Max, a.X0, a.X1)))
                .Where(h => h.Overlap > 0)
                .OrderByDescending(h => h.Overlap)
                .ToList();

            var sizeLike = IsSizeLike(profile, MajorBoxes(column.Min, column.Max, bodyLines));
            string? label = null;
            if (hits.Count == 1)
            {
                label = hits[0].Label;

                // size 类列常常左对齐而整个列很宽，表头标签会偏在列的右侧，
                // 按重叠会把 size 列错认成它右边那个（carton 类）字段 —— 这里用内容纠正。
                if (sizeLike && sizeField is not null &&
                    cartonFields.Contains(label) &&
                    (resolved.Count == 0 || !string.Equals(resolved[^1].Label, sizeField, StringComparison.OrdinalIgnoreCase)))
                {
                    label = sizeField;
                }
            }
            else if (hits.Count > 1)
            {
                var names = hits.Select(h => h.Label).ToList();
                label = sizeLike && sizeField is not null && names.Contains(sizeField) ? sizeField : hits[0].Label;
            }

            resolved.Add((column.Min, column.Max, label));
        }

        // 未标注的列向最近的已标注列继承（先左后右）
        for (var i = 0; i < resolved.Count; i++)
        {
            if (resolved[i].Label is not null)
            {
                continue;
            }

            var inherited = false;
            for (var j = i - 1; j >= 0; j--)
            {
                if (resolved[j].Label is not null)
                {
                    resolved[i] = (resolved[i].X0, resolved[i].X1, resolved[j].Label);
                    inherited = true;
                    break;
                }
            }

            if (inherited)
            {
                continue;
            }

            for (var j = i + 1; j < resolved.Count; j++)
            {
                if (resolved[j].Label is not null)
                {
                    resolved[i] = (resolved[i].X0, resolved[i].X1, resolved[j].Label);
                    break;
                }
            }
        }

        var fields = new List<TableField>();
        foreach (var (x0, x1, label) in resolved)
        {
            var merge = profile.Features.MergeAdjacentSameLabel;
            if (merge && fields.Count > 0 && string.Equals(fields[^1].Label, label, StringComparison.OrdinalIgnoreCase))
            {
                var last = fields[^1];
                fields[^1] = last with
                {
                    X1 = x1,
                    Sub = [.. last.Sub, (x0, x1)],
                };
            }
            else
            {
                fields.Add(new TableField(label, x0, x1, [(x0, x1)]));
            }
        }

        // 把相邻字段的边界推到两列中线，首尾字段向两侧放宽 ——
        // 页面之间会有几十像素水平偏移，边界贴死会把边缘的框挤到列外漏掉。
        for (var i = 0; i + 1 < fields.Count; i++)
        {
            var mid = (fields[i].X1 + fields[i + 1].X0) / 2;
            fields[i] = fields[i] with { X1 = mid };
            fields[i + 1] = fields[i + 1] with { X0 = mid };
        }

        if (fields.Count > 0)
        {
            fields[0] = fields[0] with { X0 = Math.Min(fields[0].X0, 0) };
            fields[^1] = fields[^1] with { X1 = Math.Max(fields[^1].X1, double.MaxValue / 4) };
        }

        return fields;
    }

    /// <summary>
    /// 没有表头时，按**列内容**推断每一列的字段名（续页、无表头的表就靠这个）。
    /// 返回与 <paramref name="columns"/> 等长的字段名数组（推断不出的为 null）。
    /// </summary>
    internal static List<string?> InferColumnLabels(
        TableProfile profile,
        IReadOnlyList<(double Min, double Max)> columns,
        IReadOnlyList<OcrLine> bodyLines,
        FieldGroup group)
    {
        var labels = new List<string?>(new string?[columns.Count]);
        var stats = new List<(double ItemCode, double Size, double Numeric, double Integer,
            double MultiNumber, double DecimalRatio)>();
        var numberRegex = profile.Pattern("number");
        foreach (var column in columns)
        {
            var inside = bodyLines
                .Where(l => TableColumnDetector.Overlap(l.X0, l.X1, column.Min, column.Max) >= 0.5 * (l.X1 - l.X0))
                .ToList();
            if (inside.Count == 0)
            {
                stats.Add((0, 0, 0, 0, 0, 0));
                continue;
            }

            var itemCode = inside.Count(l => LooksLikeItemCode(profile, l.Text)) / (double)inside.Count;
            var size = inside.Count(l => IsSizeLike(profile, [l])) / (double)inside.Count;
            var numbers = inside.Select(l => l.Text).Where(t => t.Any(char.IsDigit)).ToList();
            var numeric = numbers.Count / (double)inside.Count;
            var integer = numbers.Count == 0
                ? 0
                : numbers.Count(t => t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(tok => tok.Any(char.IsDigit))
                        .All(tok => !tok.Contains('.'))) / (double)numbers.Count;
            // 一行含多个数字的列（多值单元格），以及小数列
            var multi = numberRegex is null
                ? 0
                : inside.Count(l => numberRegex.Matches(l.Text ?? string.Empty).Count >= 2) / (double)inside.Count;
            var decimals = numbers.Count == 0
                ? 0
                : numbers.Count(t => t.Contains('.')) / (double)numbers.Count;
            stats.Add((itemCode, size, numeric, integer, multi, decimals));
        }

        var roles = profile.Roles;
        var thresholds = profile.Thresholds;

        // 1) item 角色字段：最左、像货号的占比达标
        var itemIndex = -1;
        if (roles.Item is { } itemField && stats.Count > 0)
        {
            for (var i = 0; i < stats.Count; i++)
            {
                if (stats[i].ItemCode >= thresholds.ItemColumnRatio)
                {
                    itemIndex = i;
                    labels[i] = itemField;
                    break;
                }
            }
        }

        // 2) size 角色字段：item 右边、英寸/字母尺码占比达标
        if (roles.Size is { } sizeField)
        {
            for (var i = itemIndex >= 0 ? itemIndex + 1 : 0; i < stats.Count; i++)
            {
                if (stats[i].Size >= thresholds.SizeColumnRatio)
                {
                    labels[i] = sizeField;
                    break;
                }
            }
        }

        // 3) 数字列：整数列 → 配置声明的整数槽位；小数列 → price / amount 角色
        var numericColumns = Enumerable.Range(0, stats.Count)
            .Where(i => labels[i] is null && stats[i].Numeric >= thresholds.NumericColumnRatio)
            .ToList();
        var integerColumns = numericColumns.Where(i => stats[i].Integer >= thresholds.IntegerColumnRatio).ToList();
        var decimalColumns = numericColumns.Where(i => stats[i].Integer < thresholds.IntegerColumnRatio)
            .OrderBy(i => i).ToList();

        if (roles.IntegerSlots.Count > 0 && integerColumns.Count > 0)
        {
            labels[integerColumns[0]] = roles.IntegerSlots[0];
        }

        if (roles.IntegerSlots.Count > 1 && integerColumns.Count > 1)
        {
            labels[integerColumns[1]] = roles.IntegerSlots[1];
        }

        if (roles.Price is { } priceField && decimalColumns.Count > 0)
        {
            labels[decimalColumns[0]] = priceField;
        }

        if (roles.Amount is { } amountField && decimalColumns.Count > 1)
        {
            labels[decimalColumns[^1]] = amountField;
        }

        // 4) 组内附加规则：把剩下的列按位置补成该组声明的其它字段
        if (group.Infer.Count > 0)
        {
            var remaining = Enumerable.Range(0, stats.Count)
                .Where(i => labels[i] is null && stats[i].Numeric >= thresholds.NumericColumnRatio)
                .ToList();

            // 按声明顺序补：多值列、小数列、整数槽、以及"紧跟 item 之后"的字段
            foreach (var rule in group.Infer)
            {
                if (string.Equals(rule.Kind, "multi", StringComparison.OrdinalIgnoreCase))
                {
                    var target = Enumerable.Range(0, stats.Count)
                        .FirstOrDefault(i => labels[i] is null && stats[i].MultiNumber >= thresholds.MultiValueRatio, -1);
                    if (target >= 0 && rule.Fields.Count > 0)
                    {
                        labels[target] = rule.Fields[0];
                    }

                    continue;
                }

                if (string.Equals(rule.Kind, "decimal", StringComparison.OrdinalIgnoreCase))
                {
                    var targets = Enumerable.Range(0, stats.Count)
                        .Where(i => labels[i] is null && stats[i].DecimalRatio >= thresholds.DecimalColumnRatio)
                        .OrderBy(i => i)
                        .ToList();
                    for (var k = 0; k < targets.Count && k < rule.Fields.Count; k++)
                    {
                        labels[targets[k]] = rule.Fields[k];
                    }

                    continue;
                }

                if (string.Equals(rule.Kind, "integerSlots", StringComparison.OrdinalIgnoreCase))
                {
                    var targets = Enumerable.Range(0, stats.Count)
                        .Where(i => labels[i] is null && stats[i].Integer >= thresholds.IntegerColumnRatio
                                    && stats[i].Numeric >= thresholds.NumericColumnRatio)
                        .ToList();
                    for (var k = 0; k < targets.Count && k < rule.Fields.Count; k++)
                    {
                        labels[targets[k]] = rule.Fields[k];
                    }

                    continue;
                }

                if (string.Equals(rule.Kind, "afterItem", StringComparison.OrdinalIgnoreCase) && rule.Fields.Count > 0)
                {
                    // 紧跟在 item 之后的第一个可用数字列（列序约定：item | 该字段 | …）
                    var target = Enumerable.Range(0, stats.Count)
                        .FirstOrDefault(i => labels[i] is null
                                             && (itemIndex < 0 || i > itemIndex)
                                             && stats[i].Numeric >= thresholds.NumericColumnRatio, -1);
                    if (target >= 0)
                    {
                        labels[target] = rule.Fields[0];
                    }
                }
            }
        }

        return labels;
    }

    /// <summary>是否"像 item 字段"：含字母+数字的 token。</summary>
    private static bool LooksLikeItemCode(TableProfile profile, string text)
    {
        foreach (var token in text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 4 && token.Any(char.IsLetter) && token.Any(char.IsDigit) &&
                !profile.ContainsLetterSize(token))
            {
                return true;
            }
        }

        return false;
    }
}
