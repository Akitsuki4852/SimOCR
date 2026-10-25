using System;
using System.Collections.Generic;
using System.Linq;
using SimOCR.Export;
using SimOCR.Ocr;

namespace SimOCR.PureLogic;

/// <summary>
/// 手工分割线切分自检（纯 <see cref="GridTableFiller"/>/<see cref="GridTable"/> 逻辑，
/// 不读素材、不进引擎、不起窗口）。
///
/// **位置沿革**：这段断言原本是
/// <c>src/SimOCR/Diagnostics/SelfTest.cs:1514-1583</c> 的 <c>RunManualLineSplit</c>，
/// 由 <c>--selftest-linegrid=&lt;图&gt;</c> 调用（这里过去直接丢弃返回值，
/// 6 条断言全部不影响退出码）。现在断言本体在这份源码里，被三方共用：
/// <c>--selftest-linegrid</c>（薄壳）、xUnit 的 <c>[Fact]</c>、控制台运行器。
///
/// 缺陷注入门禁（<c>build/inject-check.ps1</c> 的 <c>p04-linegrid-split</c>）的锚点就落在这里的
/// <c>rebuilt == "166s166s"</c> 上：把等号翻过来，退出码必须变红。
/// </summary>
internal static class ManualLineSplitChecks
{
    internal static PureCheckOutcome Run(PureCheckContext ctx)
    {
        // ① 一个框 "Gear C" 横跨 x=100..300，列线在 200：字符中心分配 → 左 "Gear" / 右 "C"
        var split1 = GridTableFiller.SplitByColumns("Gear C", 100, 300, [0, 200, 400]);
        var text1 = string.Join("|", split1.Select(p => $"{p.Column}:{p.Text}"));
        ctx.Info($"  [切分①] “Gear C” 跨列(边界200) → {text1}");
        ctx.Assert(split1.Count == 2 && split1[0].Text == "Gear" && split1[1].Text == "C",
            $"  [切分①] 期望 0:Gear | 1:C，实际 {text1}");

        // ② 切点落在字符内部、但附近有空格：把切点挪到空格处（"检查是否有空位"）
        //    "Amount 12" 按字符中心本来会切成 "Amoun|t 12"（n/t 之间，属于切字符），
        //    因为 +3 字内有空格，应改切成 "Amount|12"。
        var split2 = GridTableFiller.SplitByColumns("Amount 12", 100, 280, [0, 200, 400]);
        var text2 = string.Join("|", split2.Select(p => $"{p.Column}:{p.Text}"));
        ctx.Info($"  [切分②] “Amount 12” 跨列(边界200) → {text2}");
        ctx.Assert(split2.Count == 2 && split2[0].Text == "Amount" && split2[1].Text == "12",
            $"  [切分②] 期望切在空格空隙上 0:Amount | 1:12，实际 {text2}");

        // ②b 不变量：无论如何都不许把字符切掉或切重 —— 拼回去必须等于原文（去空白后）
        var split2b = GridTableFiller.SplitByColumns("166s166s", 100, 260, [0, 195, 400]);
        var rebuilt = string.Concat(split2b.Select(p => p.Text));
        ctx.Info($"  [切分②b] “166s166s” 无空格可挪 → {string.Join("|", split2b.Select(p => $"{p.Column}:{p.Text}"))}（拼回={rebuilt}）");
        ctx.Assert(rebuilt == "166s166s", $"  [切分②b] 字符不允许被切开或丢失：拼回得到 “{rebuilt}”");

        // ③ 跨行置中：一个高的框（y=10..90）横跨 y=50 的行线 → **不按行切**，
        //    整块文字落在中心点所在行（行0）；行1 不许出现它的任何字符。
        var lines = new List<OcrLine>
        {
            new("MERGED LABEL", 0.9f, [(120, 10), (300, 10), (300, 90), (120, 90)]),
            new("Gear C", 0.9f, [(100, 110), (300, 110), (300, 140), (100, 140)]),
        };
        var cells = GridTableFiller.Fill([0, 50, 150], [0, 200, 400], lines);
        var shape = $"{cells.Count}x{(cells.Count > 0 ? cells[0].Count : 0)}";
        var row0 = string.Join("/", cells[0]);
        var row1 = string.Join("/", cells[1]);
        ctx.Info($"  [切分③] {shape} 行0=[{row0}] 行1=[{row1}]");
        ctx.Assert(cells.Count == 2 && row0.Contains("MERGED") && row0.Contains("LABEL"),
            "  [切分③] 跨行置中的文字应整块留在中心点所在行（行0）");
        ctx.Assert(!row1.Contains("MERGED") && !row1.Contains("LABEL"),
            "  [切分③] 行1 不应出现跨行置中的字符（说明按行切开了）");
        ctx.Assert(row1.Contains("Gear") && row1.Contains("C"),
            "  [切分③] 行1 应含被横向切开的 “Gear” 与 “C”");

        var outcome = ctx.Outcome();
        ctx.Info(outcome.Failures == 0
            ? "  [切分] 手工分割线切分自检全部通过"
            : $"  [切分] 手工分割线切分自检失败 {outcome.Failures} 项");
        return outcome;
    }
}
