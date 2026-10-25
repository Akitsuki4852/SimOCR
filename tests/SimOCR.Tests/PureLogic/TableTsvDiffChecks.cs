using System;
using System.Collections.Generic;
using System.Linq;
using SimOCR.Export;

namespace SimOCR.PureLogic;

/// <summary>
/// 证明"两串单元格的差异报告"工具（<see cref="TableTsvDiff"/>）本身是可判的。
///
/// 为什么这条用例必须存在：这个工具是**判据的诊断面** —— 不变量被破坏时，
/// 生产/自检靠它指出"第几行第几列、屏幕是什么、导出是什么"。如果它自己坏了
/// （永远返回"(逐格一致)"，或永远指向第一格），那么每次不变量失败都会得到一条**误导性**的分叉点，
/// 而没有任何断言会红。所以这里对它本身做两条方向的断言：
///   * 两边相同 → 必须报"逐格一致"（否则它是个噪声源）；
///   * 两边不同 → 必须**精确指到那一格**（行列号 + 两边的值都要对）。
///
/// 搬运沿革：这三段逻辑原本是 `OcrResultWindow` 上的
/// `TestDiffDetails` / `TestViewCellsKey` / `TestExportedCellsKey` / `TestScreenExportDiff`
/// 四个 `Test*` 钩子（0.10.2-R4 点名"更适合抽到测试侧工具"）。
/// **0.10.7 口径更正**：被测工具本身是**生产**代码（`Export/TableTsvDiff.cs`，
/// 生产诊断路径真的会调它），所以它回 `src/`；**断言**留在这里，由 xUnit 的 <c>[Fact]</c>
/// 与控制台运行器 <c>SimOCR.Tests.exe --case=table-tsv-diff</c> 执行。
/// 它不再被链接进 SimOCR.dll —— 发布产物里只剩工具、没有它的断言。
/// </summary>
internal static class TableTsvDiffChecks
{
    internal static PureCheckOutcome Run(PureCheckContext ctx)
    {
        ctx.Info("=== 自检：屏幕/导出差异工具（TableTsvDiff 共享真源）===");

        var screen = Rows(["A", "B"], ["C", "D"]);

        // ① 相同 → "逐格一致"（工具不能是个恒报差异的噪声源）
        var describedSame = TableTsvDiff.Describe(screen, Rows(["A", "B"], ["C", "D"]));
        ctx.Check(
            "两边相同",
            string.Equals(describedSame, "(逐格一致)", StringComparison.Ordinal),
            $"→ 「{describedSame}」");

        // ② 一处不同 → 必须精确指到第 2 行第 2 列，且两边的值都要出现在报告里
        var describedOne = TableTsvDiff.Describe(screen, Rows(["A", "B"], ["C", "X"]));
        ctx.Check(
            "一处不同要指到那一格",
            describedOne.Contains("第2行第2列", StringComparison.Ordinal)
            && describedOne.Contains("屏幕「D」", StringComparison.Ordinal)
            && describedOne.Contains("导出「X」", StringComparison.Ordinal),
            $"→ 「{describedOne}」");

        // ③ 只差一处时不许报出第二处（报告要短、要准）
        ctx.Check(
            "只差一处就只报一处",
            describedOne.IndexOf("；", StringComparison.Ordinal) < 0,
            $"→ 「{describedOne}」");

        // ④ 分叉点明细：行数不同必须单独说一句
        var forkDetail = TableTsvDiff.ForkDetail(screen, Rows(["A", "B"]));
        ctx.Check(
            "行数不同要单独报",
            string.Join("；", forkDetail).Contains("行数不同：屏幕 2 / 导出 1", StringComparison.Ordinal),
            $"→ 「{string.Join("；", forkDetail)}」");

        // ⑤ 分叉点明细：每行只报第一处不同（搬运前 parts.Count > 5 的判据保留）
        var forkFirst = TableTsvDiff.ForkDetail(screen, Rows(["X", "Y"], ["Z", "W"]), alreadyListed: 10);
        ctx.Check(
            "每行只报第一处",
            forkFirst.Count == 1 && forkFirst[0].Contains("第 1 行第 1 列", StringComparison.Ordinal),
            $"→ {forkFirst.Count} 条：「{(forkFirst.Count > 0 ? forkFirst[0] : "(空)")}」");

        // ⑤b 上限：`alreadyListed` 为 0 时最多 6 条（搬运前 `parts.Count > 5` 的原样口径）
        var eightRows = Rows(["a"], ["b"], ["c"], ["d"], ["e"], ["f"], ["g"], ["h"]);
        var eightOther = Rows(["A"], ["B"], ["C"], ["D"], ["E"], ["F"], ["G"], ["H"]);
        var capped = TableTsvDiff.ForkDetail(eightRows, eightOther);
        ctx.Check(
            "分叉点上限 6 条",
            capped.Count == 6,
            $"→ {capped.Count} 条");

        // ⑥ CellsKey：只留非空格子、顺序不变、\u0001 分隔、格内换行压成空格
        var key = TableTsvDiff.CellsKey(Rows(["A", "B\nC"], ["", "D\r\nE"]));
        ctx.Check(
            "非空格子的键",
            string.Equals(key, "A\u0001B C\u0001D  E", StringComparison.Ordinal),
            $"→ 「{key.Replace("\u0001", "|", StringComparison.Ordinal)}」");

        // ⑦ 上限：报告最多 3 处（否则长表会把日志冲掉）—— 5 行各差一处时只报前 3 行
        var manyDiffs = TableTsvDiff.Describe(
            Rows(["a1"], ["a2"], ["a3"], ["a4"], ["a5"]),
            Rows(["z1"], ["z2"], ["z3"], ["z4"], ["z5"]));
        ctx.Check(
            "最多报 3 处",
            CountOccurrences(manyDiffs, "屏幕「") == 3,
            $"→ {CountOccurrences(manyDiffs, "屏幕「")} 处");

        // ⑧ 截断口径（搬运前的 Cut）：>24 字符才截，且以 … 结尾
        var cut = TableTsvDiff.Cut(new string('x', 25));
        ctx.Check(
            "超长截断",
            cut.Length == 25 && cut.EndsWith("…", StringComparison.Ordinal),
            $"→ 长度 {cut.Length}、结尾「{cut[^1]}」");

        var shortCut = TableTsvDiff.Cut(new string('x', 24));
        ctx.Check(
            "不超长不截断",
            shortCut.Length == 24 && !shortCut.EndsWith("…", StringComparison.Ordinal),
            $"→ 长度 {shortCut.Length}");

        var outcome = ctx.Outcome();
        ctx.Info($"=== 自检结果：{(outcome.Passed ? "通过" : $"未通过（{outcome.Failures} 项失败）")} ===");
        return outcome;
    }

    private static List<IReadOnlyList<string>> Rows(params string[][] rows)
        => [.. rows.Select(row => (IReadOnlyList<string>)row)];

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
