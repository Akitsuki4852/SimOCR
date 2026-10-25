using System.IO.Compression;
using System.Text;

namespace SimOCR.Export;

/// <summary>
/// 极简 OOXML 包写入器：docx/xlsx 本质都是"若干 XML 打进一个 zip"。
///
/// 为什么手写而不是引 NuGet（OpenXml SDK / ClosedXML）：本项目从 MVP 起就是**零第三方依赖**
/// （托盘用内置 WinForms、日志自己写、图标自己生成），而 V1 只需要"纯文本段落"与"简单表格"这两种
/// 最小结构 —— 手写一个 zip + 几个 XML 反而更可控，也不会因为 SDK 版本带来分发风险。
/// 复杂结构（样式、合并单元格、图片、公式）留给后续按需引入。
/// </summary>
internal sealed class OoxmlWriter : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FileStream _stream;
    private readonly ZipArchive _archive;

    internal OoxmlWriter(string path)
    {
        // 注意：不要用 ZipFile.Open(path, ZipArchiveMode.Create) —— 它内部是 FileMode.CreateNew，
        // 目标文件已存在时直接抛 "The file ... already exists"（用户"另存为"选已有文件名并确认覆盖时会踩到）。
        // 这里显式用 FileMode.Create（覆盖）建流，再交给 ZipArchive。
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _archive = new ZipArchive(_stream, ZipArchiveMode.Create);
    }

    /// <summary>写入一个 XML 部件（不带 BOM，OOXML 要求 UTF-8）。</summary>
    internal void WriteXml(string entryName, string xml)
    {
        var entry = _archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(xml);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    /// <summary>XML 文本转义（含引号，属性值也安全）。</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&apos;");
                    break;
                default:
                    // XML 1.0 不允许的控制字符（除 \t \n \r）直接丢弃，否则文件会打不开
                    if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
                    {
                        continue;
                    }

                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
