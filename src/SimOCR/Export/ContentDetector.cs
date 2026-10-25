using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using SimOCR.Ocr;

namespace SimOCR.Export;

/// <summary>
/// 按**内容特征**判定版式（不依赖标题、也不依赖表头）。
///
/// 为什么需要：有些版式的页面标题 OCR 认不出来，只看标题会导致列语义、字段骨架、
/// 行合并等下游机制全部不触发。内容特征反而更稳。
///
/// 判定规则完全来自配置（<c>contentDetection.rules</c>）：通用档不带任何规则，
/// 行业档自行声明。这样"某个行业的版式特征"不会成为通用工具的默认行为。
/// </summary>
internal sealed class ContentDetector
{
    /// <summary>
    /// **规则正则的缓存**：过去 <see cref="Matches"/> 每次都
    /// `new Regex(rule.Pattern, RegexOptions.Compiled)` —— 每构建一张表、对每条规则
    /// 都重新编译一遍（编译一次的代价 ≈ 解释执行几十次）。
    /// 规则文本来自配置档，天然是"少量字符串反复用"，所以按 pattern 文本缓存。
    /// 用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是普通字典：导出与自检可能在
    /// 不同线程上构建表。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> CompiledPatterns = new(StringComparer.Ordinal);

    private readonly TableProfile _profile;

    internal ContentDetector(TableProfile profile)
    {
        _profile = profile;
    }

    /// <summary>取（必要时编译并缓存）一条规则的正则。</summary>
    private static Regex Compiled(string pattern)
        => CompiledPatterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled));

    /// <summary>某条判定规则是否在给定的文字行上命中。</summary>
    internal bool Matches(string ruleName, IReadOnlyList<OcrLine> lines)
    {
        var rule = _profile.ContentDetection.Rules
            .FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase));
        if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || lines.Count == 0)
        {
            return false;
        }

        var pattern = Compiled(rule.Pattern!);
        var minPerRow = Math.Max(1, rule.MinMatchesPerRow);
        var minRows = Math.Max(1, rule.MinRows);

        // 需要"独立记号"计数时，先把区间写法（如 22"-32"）剔除
        var rangePattern = rule.ExcludeRanges ? _profile.Pattern("inchRange") : null;

        // 逐行计数：文件里的文字行本身就是"一行"，不再重新做行聚类
        var rows = BuildRows(lines);
        var hits = 0;
        foreach (var row in rows)
        {
            var text = rangePattern is null ? row.Text : rangePattern.Replace(row.Text, " ");
            var matched = pattern.Matches(text).Count;
            if (matched < minPerRow || !TailMatches(rule, row))
            {
                continue;
            }

            hits++;
            if (hits >= minRows)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>行尾特征：最后 N 个框依次落在配置给出的区间里。</summary>
    private static bool TailMatches(ContentRule rule, RowCandidate row)
    {
        var specs = rule.RowTailPattern;
        if (specs is null || specs.Count == 0)
        {
            return true;
        }

        if (row.Boxes.Count < specs.Count)
        {
            return false;
        }

        var tail = row.Boxes.TakeLast(specs.Count).ToList();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = ParseRange(specs[i]);
            var text = (tail[i].Text ?? string.Empty).Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            if (spec.Integral && Math.Abs(value - Math.Round(value)) > 0.001)
            {
                return false;
            }

            if (value < spec.Min || value > spec.Max)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>解析形如 <c>int:5-40</c> / <c>decimal:0-1</c> 的区间描述。</summary>
    private static (bool Integral, double Min, double Max) ParseRange(string spec)
    {
        var integral = spec.StartsWith("int", StringComparison.OrdinalIgnoreCase);
        var colon = spec.IndexOf(':');
        var bounds = colon >= 0 ? spec[(colon + 1)..] : spec;
        var dash = bounds.IndexOf('-', 1);
        var min = 0.0;
        var max = double.MaxValue;
        if (dash > 0)
        {
            double.TryParse(bounds[..dash], NumberStyles.Float, CultureInfo.InvariantCulture, out min);
            double.TryParse(bounds[(dash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out max);
        }

        return (integral, min, max);
    }

    /// <summary>把文字框按 y 聚类成物理行（容差与行高挂钩）。</summary>
    /// <summary>
    /// 把文字框按 y 聚类成物理行（容差与行高挂钩）。
    ///
    /// 聚簇骨架改成 <see cref="RowClustering.Cluster"/>（四份副本里唯一被合并的那一层）。
    /// 这里的三个参数与另外三处**不同，且必须保持不同**（消重复不得改算法）：
    ///   * 锚 = 该行首框的 **Y0**（不是 CenterY）—— 判的是"文字是否落在同一条基线上"；
    ///   * 容差 = **当前这条框自己的高度** `max(6, height·0.6)`（不是全局中位字高）；
    ///   * 排序键 = (Y0, X0)。
    /// 所以这里不能改用 <see cref="RowClustering.Bands"/>。
    /// </summary>
    private static List<RowCandidate> BuildRows(IReadOnlyList<OcrLine> lines)
    {
        var rows = RowClustering.Cluster(
            lines,
            line => (line.Y0, line.X0), // 与原来的 OrderBy(Y0).ThenBy(X0) 等价的元组键（ValueTuple 的比较器按字典序）
            line => line.Y0,
            line => Math.Max(6, Math.Max(1, line.Y1 - line.Y0) * 0.6));

        var result = new List<RowCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var candidate = new RowCandidate(row[0].Y0);
            candidate.Boxes.AddRange(row);
            candidate.Boxes.Sort((a, b) => a.X0.CompareTo(b.X0));
            result.Add(candidate);
        }

        return result;
    }

    private sealed class RowCandidate
    {
        internal RowCandidate(double anchorY)
        {
            AnchorY = anchorY;
        }

        internal double AnchorY { get; }

        internal List<OcrLine> Boxes { get; } = [];

        internal string Text => string.Join(" ", Boxes.Select(b => b.Text ?? string.Empty));
    }
}
