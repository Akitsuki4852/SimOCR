using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace SimOCR.Export;

/// <summary>
/// PDF 导出：**整页图片 + 隐形文本层**（文字可选、可搜索，观感与原图一致）。
///
/// 为什么可以"不嵌字体"：文本层用 `3 Tr`（既不填充也不描边）绘制，**看不见**，
/// 所以不求渲染正确，只求"复制/搜索能取到正确的字"。用 PDF 标准中文字符集
/// `UniGB-UCS2-H`（编码即 UCS-2 码位）+ 标准 CJK 字体名 `STSong-Light`，就满足这一点，
/// 且不需要往包里塞 10MB+ 的中文字体。
///
/// 已知取舍（记为 P2 待办）：少数阅读器对非嵌入的 STSong-Light 只做字形替换，
/// 若将来需要"文本层也能正确显示"，应改为**子集化嵌入**一份 OFL 中文字体。
/// 非 BMP 字符（emoji、扩展区汉字）会被跳过（UniGB-UCS2-H 只覆盖 BMP）。
/// </summary>
internal static class PdfExporter
{
    private static readonly byte[] BinaryComment = [0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A];

    internal static void Write(string path, OcrExportContent content, BitmapSource? image)
    {
        var document = content.Model;
        var jpeg = image is null ? null : EncodeJpeg(image);
        var pageWidth = Math.Max(1, document.ImageWidth);
        var pageHeight = Math.Max(1, document.ImageHeight);
        if (jpeg is null)
        {
            pageWidth = 595;
            pageHeight = 842;
        }

        var renderStream = Encoding.ASCII.GetBytes(BuildContent(content, pageWidth, pageHeight, jpeg is not null));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new long[9];

        void Raw(byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
        void Ascii(string text) => Raw(Encoding.ASCII.GetBytes(text));
        void BeginObject(int id)
        {
            offsets[id] = stream.Position;
            Ascii(FormattableString.Invariant($"{id} 0 obj\n"));
        }

        Ascii("%PDF-1.4\n");
        Raw(BinaryComment);

        // 1 目录
        BeginObject(1);
        Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2 页面树
        BeginObject(2);
        Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // 3 页面
        BeginObject(3);
        Ascii(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Resources << "));
        Ascii(jpeg is not null ? "/XObject << /Im0 4 0 R >> " : string.Empty);
        Ascii("/Font << /F0 5 0 R >> >> /Contents 6 0 R >>\nendobj\n");

        // 4 图像（JPEG 直接以 DCTDecode 塞进去，不做重编码）
        if (jpeg is not null)
        {
            BeginObject(4);
            Ascii(FormattableString.Invariant(
                $"<< /Type /XObject /Subtype /Image /Width {pageWidth} /Height {pageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"));
            Raw(jpeg);
            Ascii("\nendstream\nendobj\n");
        }

        // 5 文本字体（Type0 + 标准中文编码，不嵌入字体文件）
        BeginObject(5);
        Ascii("<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light " +
              "/Encoding /UniGB-UCS2-H /DescendantFonts [7 0 R] >>\nendobj\n");

        // 6 内容流
        BeginObject(6);
        Ascii(FormattableString.Invariant($"<< /Length {renderStream.Length} >>\nstream\n"));
        Raw(renderStream);
        Ascii("\nendstream\nendobj\n");

        // 7 CIDFont
        BeginObject(7);
        Ascii("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light " +
              "/CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 2 >> " +
              "/FontDescriptor 8 0 R /DW 1000 >>\nendobj\n");

        // 8 字体描述（无 FontFile：非嵌入）
        BeginObject(8);
        Ascii("<< /Type /FontDescriptor /FontName /STSong-Light /Flags 4 " +
              "/FontBBox [-25 -254 1000 880] /ItalicAngle 0 /Ascent 880 /Descent -120 " +
              "/CapHeight 880 /StemV 80 >>\nendobj\n");

        // 交叉引用表 + trailer
        var xrefPosition = stream.Position;
        var count = 9;
        Ascii(FormattableString.Invariant($"xref\n0 {count}\n"));
        Ascii("0000000000 65535 f \n");
        for (var i = 1; i < count; i++)
        {
            var offset = offsets[i];
            if (offset == 0)
            {
                // 未使用的对象也要占位（本实现里 4 号在无图时缺省）
                Ascii("0000000000 65535 f \n");
            }
            else
            {
                Ascii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            }
        }

        Ascii(FormattableString.Invariant($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n"));
    }

    private static string BuildContent(OcrExportContent content, int pageWidth, int pageHeight, bool hasImage)
    {
        var document = content.Model;
        // 用户在文本框里改过错字时：行数一致就按行用改后的文字（PDF 里选中/搜索到的是他改后的内容），
        // 行数变了（比如他加了段落）则退回识别原文 —— 隐形文本层的位置只能来自识别框。
        var overrides = content.TextEdited && content.Lines.Count == document.Lines.Count
            ? content.Lines
            : null;

        var builder = new StringBuilder(2048);

        if (hasImage)
        {
            builder.Append("q\n")
                .Append(CultureInfo.InvariantCulture, $"{pageWidth} 0 0 {pageHeight} 0 0 cm\n")
                .Append("/Im0 Do\nQ\n");
        }

        builder.Append("BT\n/F0 12 Tf\n3 Tr\n");
        for (var i = 0; i < document.Lines.Count; i++)
        {
            var line = document.Lines[i];
            var text = overrides is null ? line.Text : overrides[i];
            var hex = ToUcs2Hex(text);
            if (hex.Length == 0)
            {
                continue;
            }

            var size = Math.Max(4.0, (line.Y1 - line.Y0) * 0.8);
            // PDF 原点在左下角：把"框底"当基线
            var x = line.X0;
            var y = pageHeight - line.Y1;
            builder.Append(CultureInfo.InvariantCulture, $"/F0 {size:F2} Tf\n1 0 0 1 {x:F2} {y:F2} Tm\n<{hex}> Tj\n");
        }

        builder.Append("ET\n");
        return builder.ToString();
    }

    /// <summary>把文本转成 UTF-16BE 十六进制串（UniGB-UCS2-H 的编码就是 UCS-2 码位）。</summary>
    private static string ToUcs2Hex(string text)
    {
        var builder = new StringBuilder(text.Length * 4);
        foreach (var ch in text)
        {
            if (ch is < ' ' or > '\uFFFF')
            {
                continue;
            }

            builder.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>把位图编码为 JPEG（PDF 的 DCTDecode 直接吃 JPEG，无需再处理像素）。</summary>
    private static byte[] EncodeJpeg(BitmapSource image)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}
