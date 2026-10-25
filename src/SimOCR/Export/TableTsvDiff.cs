using System;
using System.Collections.Generic;
using System.Linq;

namespace SimOCR.Export;

/// <summary>
/// **自检里"两串单元格（TSV）的差异报告"工具的唯一一份实现。**
///
/// 为什么在 <c>src/SimOCR/Export/</c>（**生产**目录）而不是 <c>tests/SimOCR.Tests/PureLogic/</c>：
/// 它是**运行时诊断工具**，不是断言 —— 生产/自检路径真的会调它：
///   ① `OcrResultWindow.DescribeScreenExportFork()`（不变量被破坏时打印分叉点）；
///   ② `Diagnostics/SelfTest.cs` 的"残留格子"判据。
/// 0.10.3…0.10.6 期间它住在 <c>tests/…/PureLogic/</c> 并被 <c>SimOCR.csproj</c> 的
/// <c>&lt;Compile Include&gt;</c> **链接**进 SimOCR.dll：那样"测试目录里的文件进了发布产物"，
/// 而且"删掉全部链接"会连带把生产诊断面一起删掉。
/// 0.10.7 按职责归位：**生产工具回 src/，断言留在 tests/**。
/// 它的可判性由测试侧的 <c>tests/SimOCR.Tests/PureLogic/TableTsvDiffChecks.cs</c> 断言
/// （xUnit <c>[Fact]</c> + 控制台运行器 <c>SimOCR.Tests.exe --case=table-tsv-diff</c>）。
/// 搬运前它们是挂在 <c>OcrResultWindow</c> 上的 4 个 <c>Test*</c> 钩子
/// （`TestDiffDetails` / `TestViewCellsKey` / `TestExportedCellsKey` / `TestScreenExportDiff`）——
/// 那些是**工具**不是**断言**，挂在生产类上只会让 `Test*` 成员继续膨胀。
///
/// 输入口径：**已经按导出口径归一过的**单元格 (行, 列)，即导出的 TSV 按 <c>\t</c> / <c>\n</c>
/// 拆开、格内换行压成空格后的结果（<c>OcrResultWindow</c> 的 <c>ExportCells</c> / <c>Flattened</c>
/// 与自检侧的 `ScreenCellRows` / `ExportedCellRows` 都已经是这个口径）。
/// 之所以吃 **(行, 列) 结构**而不是裸 TSV 字符串：屏幕上的格子可以含换行，
/// 裸 TSV 往返会把"一格多行"与"两行"混起来 —— 那样差异报告会指向错误的格子。
/// </summary>
internal static class TableTsvDiff
{
    /// <summary>差异报告里单元格文本的截断长度（与搬运前的 <c>Cut</c> 完全一致）。</summary>
    private const int CutLength = 24;

    /// <summary>
    /// 所有**非空格子**的文本以 <c>\u0001</c> 相连（"残留格子"判据用它做整体比较）。
    /// 口径与搬运前的 <c>TestViewCellsKey</c> 一致：格内换行/回车压成空格，空串丢掉，顺序不变。
    /// </summary>
    internal static string CellsKey(IEnumerable<IReadOnlyList<string>> rows)
        => string.Join("\u0001", rows.SelectMany(row => row)
            .Select(cell => cell.Replace('\n', ' ').Replace('\r', ' '))
            .Where(cell => cell.Length > 0));

    /// <summary>
    /// 把"导出 vs 屏幕"的差异列成短的几行（**最多 3 处**，与搬运前的 `TestDiffDetails` 逐字一致）。
    /// 逐格一致时返回 <c>"(逐格一致)"</c>。
    /// </summary>
    internal static string Describe(
        IReadOnlyList<IReadOnlyList<string>> screen,
        IReadOnlyList<IReadOnlyList<string>> exported)
    {
        var found = new List<string>();
        var max = Math.Max(screen.Count, exported.Count);
        for (var r = 0; r < max && found.Count < 3; r++)
        {
            var left = r < screen.Count ? screen[r] : [];
            var right = r < exported.Count ? exported[r] : [];
            for (var c = 0; c < Math.Max(left.Count, right.Count); c++)
            {
                var a = c < left.Count ? left[c] : "(无)";
                var b = c < right.Count ? right[c] : "(无)";
                if (string.Equals(a, b, StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add($"第{r + 1}行第{c + 1}列 屏幕「{Cut(a)}」vs 导出「{Cut(b)}」");
                break;
            }
        }

        return found.Count == 0 ? "(逐格一致)" : string.Join("；", found);
    }

    /// <summary>
    /// 分叉点的逐格明细（搬运前 `TestScreenExportDiff` 里那一段的行为**逐字保留**）：
    /// 每行只报**第一处**不同，报满就停，最后若行数不同再补一条。
    ///
    /// <paramref name="alreadyListed"/> 是调用方**在本方法之前已经放进同一行日志的片段数**
    /// —— 搬运前的上限判据是 <c>parts.Count &gt; 5</c>，而 `parts` 里有 10 条表头字段，
    /// 所以那个循环实际上"只报第一处就停"。为了不让"搬走工具"顺手改掉日志的形状，
    /// 这个上限按原样保留（默认 0 时表现为"报满 6 条"）。
    /// </summary>
    internal static IReadOnlyList<string> ForkDetail(
        IReadOnlyList<IReadOnlyList<string>> screen,
        IReadOnlyList<IReadOnlyList<string>> exported,
        int alreadyListed = 0)
    {
        var parts = new List<string>();
        var max = Math.Min(screen.Count, exported.Count);
        for (var r = 0; r < max; r++)
        {
            for (var c = 0; c < Math.Max(screen[r].Count, exported[r].Count); c++)
            {
                var left = c < screen[r].Count ? screen[r][c] : "(无)";
                var right = c < exported[r].Count ? exported[r][c] : "(无)";
                if (!string.Equals(left, right, StringComparison.Ordinal))
                {
                    parts.Add($"第 {r + 1} 行第 {c + 1} 列 不同：屏幕「{left}」/ 导出「{right}」");
                    break;
                }
            }

            if (alreadyListed + parts.Count > 5)
            {
                break;
            }
        }

        if (screen.Count != exported.Count)
        {
            parts.Add($"行数不同：屏幕 {screen.Count} / 导出 {exported.Count}");
        }

        return parts;
    }

    /// <summary>超长单元格截断（与搬运前的 <c>Cut</c> 完全一致）。</summary>
    internal static string Cut(string value)
        => value.Length <= CutLength ? value : value[..CutLength] + "…";
}
