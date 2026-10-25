using System.Text;

namespace SimOCR.Storage;

/// <summary>
/// 文件名模板渲染。支持的占位符：
/// {yyyy} {yy} {MM} {dd} {HH} {mm} {ss} {fff} {date} {time}
/// 例：SimOCR_{yyyy}{MM}{dd}_{HH}{mm}{ss}_{fff}
/// </summary>
internal static class FileNameTemplate
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Render(string? template, DateTime timestamp)
    {
        var t = string.IsNullOrWhiteSpace(template) ? "SimOCR_{yyyy}{MM}{dd}_{HH}{mm}{ss}_{fff}" : template;

        var sb = new StringBuilder(t.Length + 16);
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] != '{')
            {
                sb.Append(t[i]);
                continue;
            }

            var end = t.IndexOf('}', i + 1);
            if (end < 0)
            {
                sb.Append(t[i..]);
                break;
            }

            var token = t[(i + 1)..end];
            sb.Append(ResolveToken(token, timestamp));
            i = end;
        }

        return Sanitize(sb.ToString());
    }

    // 占位符大小写敏感：{MM}=月, {mm}=分, {HH}=24 小时制（与常见截图工具习惯一致）
    private static string ResolveToken(string token, DateTime ts) => token switch
    {
        "yyyy" => ts.ToString("yyyy"),
        "yy" => ts.ToString("yy"),
        "MM" => ts.ToString("MM"),
        "dd" => ts.ToString("dd"),
        "HH" => ts.ToString("HH"),
        "mm" => ts.ToString("mm"),
        "ss" => ts.ToString("ss"),
        "fff" => ts.ToString("fff"),
        "date" => ts.ToString("yyyy-MM-dd"),
        "time" => ts.ToString("HHmmss"),
        _ => "{" + token + "}",
    };

    /// <summary>去掉文件名非法字符、控制字符与首尾空白。</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "SimOCR" : result;
    }
}
