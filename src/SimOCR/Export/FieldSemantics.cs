using System.Globalization;
using System.Text;

namespace SimOCR.Export;

/// <summary>
/// 字段语义的显示层：把"语义字段名"翻译成人能看懂的表头名并标明它是不是数值列，
/// 同时提供**数值净化**（把货币符号、单位后缀与数字分开）。
///
/// 为什么需要：识别出来的格子里，数值常常带装饰 —— `¥ 1,234.50`、`531.50 /USD ¥` ——
/// 直接导出成文本的话，"能算的数字"就没了，导出到表格里无法筛选、透视、求和。
///
/// 显示名与"哪些字段是数值列"都来自 <see cref="TableProfile"/>（YAML 配置），
/// 本类不写死任何业务词。
/// </summary>
internal static class FieldSemantics
{
    /// <summary>按字段名取出显示名与"是否数值列"。</summary>
    internal static (string Name, bool Numeric) Describe(TableProfile profile, string? fieldName)
    {
        if (profile.TryGetField(fieldName, out var spec))
        {
            return (spec.Display, spec.Numeric);
        }

        // 认不出的字段：原样返回、按文本列处理（宁可少猜，也不要猜错）
        return (fieldName ?? string.Empty, false);
    }

    /// <summary>
    /// 数值净化：抽出格子里的数字主体。
    /// 例：`¥ 1,234.50` → `1234.50`；`531.50 /USD ¥` → `531.50`。
    /// 抽不出数字时原样返回（宁可保留可疑内容，也不要静默丢数据）。
    /// </summary>
    internal static string CleanNumeric(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        var text = TableProfile.Normalize(raw);

        // 找第一个"数字串"（允许 -, 千分位, 小数点）
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return raw;
        }

        // 往左吃掉紧邻的负号
        if (start > 0 && text[start - 1] == '-')
        {
            start--;
        }

        var end = start;
        var seenDot = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsDigit(c))
            {
                end = i + 1;
                continue;
            }

            if (c == ',' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                continue; // 千分位
            }

            if (c == '.' && !seenDot && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                seenDot = true;
                continue;
            }

            break;
        }

        var candidate = text[start..end].Replace(",", string.Empty);
        return candidate.Length > 0 ? candidate : raw;
    }

    /// <summary>按列类型净化（非数值列原样返回）。</summary>
    internal static string CleanCell(string raw, bool numeric) => numeric ? CleanNumeric(raw) : raw;

    /// <summary>能否当数字写进 Excel（千分位去掉后是合法数值）。</summary>
    internal static bool TryParseNumber(string text, out double value)
        => double.TryParse(
            text.Replace(",", string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// 全角数字/符号拉回半角（识别结果里常见）。
    /// 强版唯一出处是 <see cref="CellValidator.Normalize"/>，本方法只是语义别名
    /// （过去它转发 <c>TableProfile.Normalize</c> 的弱版，两版强度不同 ）。
    /// </summary>
    internal static string Normalize(string text) => CellValidator.Normalize(text);
}
