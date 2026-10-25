using System.Globalization;
using System.Text;

namespace SimOCR.Export;

/// <summary>
/// **单元格数值/格式校验**（增强识别的一环）。目标是把"货币列混进汉字、数字列混进 166s"
/// 这类问题从"静默产出垃圾"变成"能修就修、修不了就留痕"。
///
/// 三条纪律（照抄数据校验领域的通行做法）：
///   1. **先归一、再判断**：全角→半角、各种连字符/小数点统一、去零宽字符（无损）；
///   2. **受控白名单剥离单位**：`166s` 是"166 秒"，不是错误 —— 拆成值 166 + 单位 s；
///   3. **不确定就不动**：形近字修复要求"恰好一种改法能得到合法数值"，否则只报告不修改；
///      列类型判错（把文本列当数值列）会把正确数据改坏，因此列类型要有支撑度门槛。
/// </summary>
internal static class CellValidator
{
    /// <summary>一处改动/一处提醒（Before == After 表示只是提醒，没有改内容）。</summary>
    internal sealed record Fix(int Row, int Column, string Before, string After, string Reason)
    {
        internal bool Changed => !string.Equals(Before, After, StringComparison.Ordinal);
    }

    /// <summary>列类型判定门槛：非空单元格里至少这么多比例是数值，才当数值列处理。</summary>
    private const double NumericSupport = 0.7;

    /// <summary>样本太少就不做离群提醒（避免把正常业务值当异常）。</summary>
    private const int OutlierMinSamples = 6;

    /// <summary>
    /// 受控单位后缀白名单里的**通用单位**（必须白名单：不能"剥掉任何非数字尾巴"，否则会吃掉合法文本）。
    /// 行业性的包装单位（打/件/箱/套…）不写在这里，而是随配置档的 <c>unitWords</c> 一起生效
    /// （见 <see cref="Suffixes"/>）：换个行业只要换配置，不必改代码。
    /// 另：单字母条目一律不收 —— 一个字母的白名单会在任何以该字母结尾的值上静默截断。
    /// </summary>
    private static readonly string[] GenericUnitSuffixes =
    [
        "kgs", "kg", "mg", "ml", "lbs", "lb", "oz", "cbm",
        "km", "cm", "mm", "ms", "us", "ns", "ft", "yd", "min",
        "g", "t", "m", "s", "h", "d", "e",
        "%", "‰",
    ];

    /// <summary>形近字（OCR 常见混淆）。只用于**数值列**，且要求唯一可行解。</summary>
    private static readonly (char From, char To)[] Confusions =
    [
        ('O', '0'), ('o', '0'), ('D', '0'), ('Q', '0'), ('〇', '0'),
        ('l', '1'), ('I', '1'), ('i', '1'), ('|', '1'), ('!', '1'), ('丨', '1'),
        ('Z', '2'), ('z', '2'),
        ('S', '5'), ('s', '5'),
        ('G', '6'), ('b', '6'),
        ('B', '8'),
        ('g', '9'), ('q', '9'),
    ];

    /// <summary>
    /// 这些是"没有值的正当写法"，不算异常、也不清空。
    /// 清单来自配置档的 <c>nonValues</c>（通用档与行业档各自声明），代码只留一个兜底。
    /// </summary>
    private static IReadOnlyList<string> NonValues => TableProfiles.Current.NonValues;

    /// <summary>
    /// 生效的单位后缀白名单 = 代码里的通用单位 + 当前配置档的 <c>unitWords</c>。
    /// 行业档因此可以声明自己的包装单位（打/件/箱/套…），通用档一个都不带 —— 换个行业只要换配置。
    /// </summary>
    private static IEnumerable<string> Suffixes()
    {
        foreach (var unit in GenericUnitSuffixes)
        {
            yield return unit;
        }

        foreach (var unit in TableProfiles.Current.UnitSuffixes)
        {
            yield return unit;
        }
    }

    /// <summary>
    /// 生效的"货币噪声"字符 = 配置档的货币符号 + 常被误读成的字符。
    /// 误读字符是引擎相关的（例如 `¥` 常被 PP-OCR 认成 `我`/`天`），因此由配置档覆盖：<c>currencyNoiseGlyphs</c>。
    /// </summary>
    private static string CurrencyNoise => TableProfiles.Current.CurrencyNoise;

    internal static List<Fix> Apply(
        IReadOnlyList<IReadOnlyList<string>> cells,
        IReadOnlyList<bool> columnNumeric,
        IReadOnlyList<string> labels)
    {
        var fixes = new List<Fix>();
        if (cells.Count == 0)
        {
            return fixes;
        }

        var columns = cells.Max(r => r.Count);
        var working = new List<List<string>>(cells.Count);
        foreach (var row in cells)
        {
            var copy = new List<string>(columns);
            for (var c = 0; c < columns; c++)
            {
                copy.Add(c < row.Count ? row[c] : string.Empty);
            }

            working.Add(copy);
        }

        for (var c = 0; c < columns; c++)
        {
            var values = working.Select(r => r[c]).ToList();
            var declared = c < columnNumeric.Count && columnNumeric[c];
            var numeric = declared || LooksNumeric(values);
            if (!numeric)
            {
                // 非数值列：只做无损归一（全角→半角、连字符统一、去零宽）
                for (var r = 0; r < working.Count; r++)
                {
                    var before = working[r][c];
                    var after = Normalize(before);
                    if (!string.Equals(before, after, StringComparison.Ordinal))
                    {
                        working[r][c] = after;
                        fixes.Add(new Fix(r, c, before, after, "字符归一（全角→半角/符号统一）"));
                    }
                }

                continue;
            }

            var samples = new List<double>();
            for (var r = 0; r < working.Count; r++)
            {
                var before = working[r][c];
                var normalized = Normalize(before);
                if (normalized.Length == 0)
                {
                    if (before.Length > 0)
                    {
                        working[r][c] = string.Empty;
                        fixes.Add(new Fix(r, c, before, string.Empty, "空白/不可见字符"));
                    }

                    continue;
                }

                if (IsNonValue(normalized))
                {
                    if (!string.Equals(before, normalized, StringComparison.Ordinal))
                    {
                        working[r][c] = normalized;
                        fixes.Add(new Fix(r, c, before, normalized, "字符归一"));
                    }

                    continue;
                }

                var (cleaned, reason) = CleanNumeric(normalized);
                if (!string.Equals(before, cleaned, StringComparison.Ordinal))
                {
                    working[r][c] = cleaned;
                    fixes.Add(new Fix(r, c, before, cleaned, reason));
                }

                if (FieldSemantics.TryParseNumber(cleaned, out var value))
                {
                    samples.Add(value);
                }
                else if (cleaned.Length > 0)
                {
                    fixes.Add(new Fix(r, c, cleaned, cleaned,
                        $"数值列里出现非数值内容（列名：{(c < labels.Count && labels[c].Length > 0 ? labels[c] : $"第 {c + 1} 列")}），请人工确认"));
                }
            }

            FlagOutliers(working, c, samples, fixes);
        }

        // 把清洗结果写回（调用方拿到的是同一批 cells 的副本语义：这里直接改原列表是危险的，
        // 因此通过返回值交给调用方重建 layout）
        for (var r = 0; r < cells.Count; r++)
        {
            if (cells[r] is List<string> mutable)
            {
                for (var c = 0; c < columns; c++)
                {
                    mutable[c] = working[r][c];
                }
            }
        }

        return fixes;
    }

    /// <summary>用中位数 + MAD 提醒"明显离开本列分布"的值（只提醒，绝不修改）。</summary>
    private static void FlagOutliers(List<List<string>> working, int column, List<double> samples, List<Fix> fixes)
    {
        if (samples.Count < OutlierMinSamples)
        {
            return;
        }

        var sorted = samples.OrderBy(v => v).ToList();
        var median = sorted[sorted.Count / 2];
        var deviations = samples.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
        var mad = deviations[deviations.Count / 2];
        if (mad <= 0)
        {
            return;
        }

        var limit = 3.5 * 1.4826 * mad;
        for (var r = 0; r < working.Count; r++)
        {
            if (!FieldSemantics.TryParseNumber(working[r][column], out var value))
            {
                continue;
            }

            if (Math.Abs(value - median) > limit)
            {
                fixes.Add(new Fix(r, column, working[r][column], working[r][column],
                    $"数值明显偏离本列中位数（中位 {median:0.##}）—— 请人工确认"));
            }
        }
    }

    /// <summary>列类型投票：非空单元格里 ≥70% 能解析成数值（剥离单位后）就当数值列。</summary>
    private static bool LooksNumeric(IReadOnlyList<string> values)
    {
        var total = 0;
        var numeric = 0;
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0 || IsNonValue(normalized))
            {
                continue;
            }

            total++;
            var (cleaned, _) = CleanNumeric(normalized);
            if (FieldSemantics.TryParseNumber(cleaned, out _))
            {
                numeric++;
            }
        }

        return total >= 3 && (double)numeric / total >= NumericSupport;
    }

    private static bool IsNonValue(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        return NonValues.Contains(lower);
    }

    /// <summary>无损归一：全角→半角、各种连字符/小数点/逗号统一、去零宽与首尾空白。</summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // 零宽 / 软连字符 / BOM
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' or '\u00AD')
            {
                continue;
            }

            var mapped = ch switch
            {
                '−' or '–' or '—' or '‐' or '‑' or 'ー' => '-',
                '。' or '．' or '·' or '・' => '.',
                '，' or '、' => ',',
                '：' => ':',
                _ => ch,
            };

            // 全角 ASCII（！-～）与全角空格
            if (mapped == '\u3000')
            {
                builder.Append(' ');
                continue;
            }

            if (mapped is >= '\uFF01' and <= '\uFF5E')
            {
                builder.Append((char)(mapped - 0xFEE0));
                continue;
            }

            builder.Append(mapped);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 数值净化：剥货币符号/受控单位后缀 → 必要时做**唯一解**形近字修复。
    /// 返回清洗后的文本与原因（原因用于留痕）。
    /// </summary>
    internal static (string Value, string Reason) CleanNumeric(string text)
    {
        var value = text;
        var currencyNoise = CurrencyNoise;

        // 1) 货币符号（含被误读成汉字的 ¥）。允许前后各出现一次
        var strippedCurrency = false;
        while (value.Length > 0 && currencyNoise.IndexOf(value[0]) >= 0)
        {
            value = value[1..].TrimStart();
            strippedCurrency = true;
        }

        while (value.Length > 0 && currencyNoise.IndexOf(value[^1]) >= 0)
        {
            value = value[..^1].TrimEnd();
            strippedCurrency = true;
        }

        if (value.Length == 0)
        {
            return (string.Empty, "非数值内容（货币符号/噪声）已清空");
        }

        if (FieldSemantics.TryParseNumber(value, out _))
        {
            return (value, strippedCurrency ? "剥离货币符号" : "字符归一");
        }

        // 2) 受控单位后缀（通用单位 + 配置档声明的单位词）
        foreach (var unit in Suffixes())
        {
            if (value.Length <= unit.Length || !value.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var head = value[..^unit.Length].TrimEnd();
            if (FieldSemantics.TryParseNumber(head, out _))
            {
                return (head, $"剥离受控单位后缀 “{unit}”");
            }
        }

        // 3) 形近字修复：要求**恰好一种**改法能得到合法数值
        var candidates = new List<(string Text, string Reason)>();
        for (var i = 0; i < value.Length; i++)
        {
            foreach (var (from, to) in Confusions)
            {
                if (value[i] != from)
                {
                    continue;
                }

                var replaced = value[..i] + to + value[(i + 1)..];
                if (FieldSemantics.TryParseNumber(Normalize(replaced), out _))
                {
                    candidates.Add((Normalize(replaced), $"形近字修正 {from}→{to}"));
                }
            }
        }

        if (candidates.Count == 1)
        {
            return (candidates[0].Text, candidates[0].Reason);
        }

        // 4) 只剩符号没有数字（例如一整格就是一个被误读的货币符号）→ 清空并留痕
        if (!value.Any(char.IsDigit))
        {
            return (string.Empty, "非数值内容（货币符号/噪声）已清空");
        }

        return (value, candidates.Count > 1 ? "有多种形近字改法，未自动修改，请人工确认" : "未识别为数值，请人工确认");
    }
}
