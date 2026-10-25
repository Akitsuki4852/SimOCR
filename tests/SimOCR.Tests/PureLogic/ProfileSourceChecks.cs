using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimOCR.Export;
using SimOCR.Ocr;

namespace SimOCR.PureLogic;

/// <summary>
/// 验收（「配置档：两份活动副本 + 构建期换档」）：
///
///   ① **一张表只用一个档**：构建开始时取一次档位快照，整张表（行覆盖 / 语义骨架 /
///      列展开 / 按键分组 / 派生列与算术校验）都用那一份。这里在"快照已取、构建还没开始"
///      的那一刻把全局档位换成另一个档，结果必须仍与开始时的档**逐字段一致**。
///      只要构建链上还有任何一处重读 <see cref="TableProfiles.Current"/>，这张表就会
///      变成两个档的混合体，下面的相等断言立刻失败。
///   ② **非空洞**：先证明这张合成件在另一个档下确实产出不同的结果 ——
///      否则 ① 的相等断言永远成立，等于空心通过。
///
/// 两个档都来自程序目录的 <c>Profiles/*.yaml</c>（与生产同一条加载路径）。
///
/// **位置沿革**：这段断言原本是
/// <c>src/SimOCR/Diagnostics/SelfTestProfileSource.cs</c>（138 行，无条件编译进发布产物、
/// 却只由 <c>--selftest-profile</c> 调一次）。0.10.2 审查 §R4 点名它是"最可惜的一条"：
/// 纯逻辑、不需要模型、不需要素材，最适合进 xUnit。现在它是**这一份**源码。
/// **0.10.7**：它只编进测试程序集（发布产物 `SimOCR.dll` 里 0 命中），
/// 由 xUnit 的 <c>[Fact]</c> 与控制台运行器 <c>SimOCR.Tests.exe --case=profile-source</c> 执行；
/// app 侧的 <c>--selftest-profile</c> 薄壳与那个 CLI 开关**已退役**（见 AppComposition 的说明）。
/// </summary>
internal static class ProfileSourceChecks
{
    private const string IndustryProfile = "example-invoice-packing";
    private const string GenericProfile = "default";

    /// <summary>0 = 通过；1 = 断言失败；5 = 用例不成立（配置档文件不在，环境/产物问题）。</summary>
    internal static PureCheckOutcome Run(PureCheckContext ctx)
    {
        ctx.Info("=== 自检：配置档单一来源（构建中途换档不混档）===");

        var missing = new[] { IndustryProfile, GenericProfile }
            .Where(name => !File.Exists(Path.Combine(TableProfile.ProfileDirectory(), name + ".yaml")))
            .ToList();
        if (missing.Count > 0)
        {
            var files = string.Join(", ", missing.Select(n => $"Profiles/{n}.yaml"));
            ctx.Info($"  [不成立] 缺少配置档文件：{files}" +
                     $"（目录 {TableProfile.ProfileDirectory()}）—— 用例不成立，不作为失败");
            ctx.MarkNotApplicable($"缺少配置档文件：{files}（目录 {TableProfile.ProfileDirectory()}）");
            return ctx.Outcome();
        }

        // 这段断言过去会把全局档位留在"被注入的那个档"上（应用跑完就退出，所以看不出来）。
        // 搬进 tests/ 之后同一条用例会与其它测试类**并行**跑，所以跑完必须还原（搬运注记）。
        var original = TableProfiles.Current;
        var restoreName = original.SourcePath is { } source ? Path.GetFileNameWithoutExtension(source) : null;
        try
        {
            return RunCore(ctx);
        }
        finally
        {
            TableProfiles.Initialize(restoreName);
        }
    }

    private static PureCheckOutcome RunCore(PureCheckContext ctx)
    {
        var document = InvoiceDocument();

        // ---- 档 A：行业档（派生列 + 行级算术校验 + 数量列展开 + 按键分组）----
        TableProfiles.Initialize(IndustryProfile);
        var startProfile = TableProfiles.Current;
        if (!LoadedFrom(startProfile, IndustryProfile + ".yaml"))
        {
            ctx.Info($"  [不成立] Profiles/{IndustryProfile}.yaml 没能生效" +
                     $"（当前档={startProfile.Name}，来源={startProfile.SourcePath ?? "(内置兜底)"}）");
            ctx.MarkNotApplicable($"Profiles/{IndustryProfile}.yaml 没能生效（当前档={startProfile.Name}）");
            return ctx.Outcome();
        }

        var baseline = Signature(OcrTableLayout.Build(document));

        // ---- 档 B：通用档（无派生列、不做行级算术校验）----
        TableProfiles.Initialize(GenericProfile);
        var otherProfile = TableProfiles.Current;
        if (!LoadedFrom(otherProfile, GenericProfile + ".yaml"))
        {
            ctx.Info($"  [不成立] Profiles/{GenericProfile}.yaml 没能生效" +
                     $"（当前档={otherProfile.Name}，来源={otherProfile.SourcePath ?? "(内置兜底)"}）");
            ctx.MarkNotApplicable($"Profiles/{GenericProfile}.yaml 没能生效（当前档={otherProfile.Name}）");
            return ctx.Outcome();
        }

        var underOther = Signature(OcrTableLayout.Build(document));

        // ② 非空洞：换档必须真的改变结果
        var differs = !string.Equals(baseline, underOther, StringComparison.Ordinal);
        ctx.Info($"  [非空洞] 档 {startProfile.Name} vs {otherProfile.Name} 的结果不同={differs} → {(differs ? "OK" : "FAIL")}");
        ctx.Assert(differs,
            $"  [失败] 换档没有改变结果：档 {startProfile.Name} 与 {otherProfile.Name} 产出完全相同" +
            "（① 的相等断言等于空心通过，用例不成立）");

        // ---- ① 正题：档 A 的构建，中途（快照已取、构建未开始）把全局换成档 B ----
        TableProfiles.Initialize(IndustryProfile);
        var switchedInsideBuild = false;
        var midBuild = Signature(OcrTableLayout.Build(document, startProfile, () =>
        {
            switchedInsideBuild = string.Equals(TableProfiles.Current.Name, startProfile.Name, StringComparison.Ordinal);
            TableProfiles.Initialize(GenericProfile);
        }));

        var switchStuck = string.Equals(TableProfiles.Current.Name, otherProfile.Name, StringComparison.Ordinal);
        ctx.Info($"  [注入] 换档发生在构建开始前（此时全局仍是 {startProfile.Name}）={switchedInsideBuild}；" +
                 $"构建结束后全局档={TableProfiles.Current.Name} → {((switchedInsideBuild && switchStuck) ? "OK" : "FAIL")}");
        ctx.Assert(switchedInsideBuild && switchStuck,
            $"  [失败] 换档注入没生效（换档前全局仍是开始档={switchedInsideBuild}；" +
            $"换档后全局档={TableProfiles.Current.Name}，期望 {otherProfile.Name}）—— 断言失去意义");

        var sameAsStart = string.Equals(baseline, midBuild, StringComparison.Ordinal);
        ctx.Info($"  [不混档] 中途换成 {otherProfile.Name} 后，结果仍与开始时的档 {startProfile.Name} 一致={sameAsStart}");
        if (!sameAsStart)
        {
            ctx.Info($"    本次（开始时的档）：{baseline.Replace("\n", " / ", StringComparison.Ordinal)}");
            ctx.Info($"    本次（中途换档后）：{midBuild.Replace("\n", " / ", StringComparison.Ordinal)}");
        }

        ctx.Assert(sameAsStart,
            $"  [失败] 构建中途换档把同一张表混成了两个档（结果与开始时的档 {startProfile.Name} 不一致）");

        var outcome = ctx.Outcome();
        ctx.Info($"=== 自检结果：{(outcome.Passed ? "通过" : $"未通过（{outcome.Failures} 项失败）")} ===");
        return outcome;
    }

    /// <summary>这一档是不是从期望的那个 YAML 文件加载的（而不是悄悄回退到内置兜底）。</summary>
    private static bool LoadedFrom(TableProfile profile, string fileName)
        => profile.SourcePath is { } path &&
           string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 整张表的可比签名：行列数、列名/列类型、单元格、合并区、校验摘要。
    /// 只要有两个档混用，这些字段里至少有一项对不上。
    /// </summary>
    private static string Signature(OcrTableLayout layout)
        => string.Join(
            "\n",
            $"rows={layout.RowCount} cols={layout.ColumnCount}",
            $"labels={string.Join(",", layout.ColumnLabels)}",
            $"numeric={string.Join(",", layout.ColumnNumeric)}",
            $"merges={string.Join(";", layout.Merges.Select(m => $"{m.Row},{m.Column},{m.RowSpan},{m.ColumnSpan}"))}",
            $"validation={layout.ValidationSummary ?? "(null)"}",
            $"cells={string.Join(";", layout.Cells.Select(r => string.Join(",", r)))}");

    /// <summary>一张最小的"发票/装箱单"合成件：货号 / 数量 / 单价 / 金额 + 合计行。</summary>
    private static OcrDocument InvoiceDocument()
    {
        var lines = new List<OcrLine>();
        AddRow(lines, 20, ("货号", 20), ("数量", 170), ("单价", 300), ("金额", 430));
        AddRow(lines, 60, ("SKU-A", 20), ("10", 170), ("12.50", 300), ("125.00", 430));
        AddRow(lines, 100, ("SKU-B", 20), ("4", 170), ("7.00", 300), ("28.00", 430));
        AddRow(lines, 140, ("SKU-C", 20), ("6", 170), ("3.00", 300), ("18.00", 430));
        AddRow(lines, 180, ("合计", 20), ("171.00", 430));
        return new OcrDocument(600, 220, lines, 0);
    }

    private static void AddRow(List<OcrLine> lines, double y, params (string Text, double X)[] cells)
    {
        foreach (var (text, x) in cells)
        {
            // 粗略字宽（9px/字符）：本用例只关心"列分得开"，不需要精确排版
            var width = Math.Max(12, text.Length * 9.0);
            lines.Add(new OcrLine(text, 0.99f,
            [
                (x, y),
                (x + width, y),
                (x + width, y + 20),
                (x, y + 20),
            ]));
        }
    }
}
