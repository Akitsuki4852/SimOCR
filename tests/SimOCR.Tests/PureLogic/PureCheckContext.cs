using System;
using System.Collections.Generic;

namespace SimOCR.PureLogic;

/// <summary>
/// 纯逻辑断言的**唯一接缝**。
///
/// 断言本体（<see cref="ProfileSourceChecks"/> / <see cref="SnapGeometryChecks"/> /
/// <see cref="ManualLineSplitChecks"/>）只认这个上下文，不认任何具体日志实现，
/// 因此同一份断言可以被两条消费路径执行、且只有一份源码。
///
/// **0.10.7 口径变更**：原来还有第 ① 条"应用自检薄壳"路径
/// （<c>--selftest-profile</c> / <c>--selftest-snap</c> / <c>--selftest-linegrid</c> 经
/// <c>Diagnostics/</c> 把 <c>LogService.Info/Error</c> 接进来）。那条路径连同"断言源码被编进
/// 发布产物"一起删除了 —— 断言现在只编进测试程序集（发布产物里 0 命中），"一份源码"反而更干净：
///
///   ① xUnit：<c>tests/SimOCR.Tests/PureLogicTests.cs</c> 的 <c>[Fact]</c>
///      → 输出走 <c>ITestOutputHelper</c>，一条用例 = 一组断言；
///   ② 控制台运行器：<c>tests/SimOCR.Tests/Program.cs</c>（<c>SimOCR.Tests.exe</c>）
///      → 输出走 stdout。**这条是本机唯一可执行的路径**：本环境的 testhost 起不来
///      （<c>Win32Exception (5)</c> in <c>SetParentProcessExitCallback</c>/<c>OpenProcess</c>），
///      <c>dotnet test</c> 会直接崩在启动阶段，所以门禁必须有一条不依赖 testhost 的执行路径。
/// </summary>
internal sealed class PureCheckContext
{
    private readonly Action<string> _info;
    private readonly Action<string, Exception?> _error;

    internal PureCheckContext(Action<string> info, Action<string, Exception?> error)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>已经执行过的断言条数（三条消费路径共用同一份计数，报告"搬了多少条断言"就用它）。</summary>
    internal int Checks { get; private set; }

    internal int Failures { get; private set; }

    /// <summary>用例不成立（环境/产物问题，例如配置档 YAML 不在）——不是缺陷，对应退出码 5。</summary>
    internal bool NotApplicable { get; private set; }

    internal string? NotApplicableReason { get; private set; }

    internal void Info(string message) => _info(message);

    internal void Error(string message, Exception? ex = null) => _error(message, ex);

    /// <summary>
    /// 一条断言，输出 <c>[通过]</c>/<c>[失败]</c> 行（与 <c>Program.cs</c> 的用例级标记同一套字）。
    /// </summary>
    internal bool Check(string label, bool condition, string detail)
    {
        Checks++;
        if (condition)
        {
            _info($"  [通过] {label} {detail}");
            return true;
        }

        Failures++;
        _error($"  [失败] {label} {detail}", null);
        return false;
    }

    /// <summary>
    /// 一条断言，**日志文案由调用方自己写**（保持原文案不变的用例走这条：通过时通常已由调用方
    /// 打过一行说明，失败时才能拼出带上下文的错误行）。
    /// </summary>
    internal bool Assert(bool condition, string failMessage)
    {
        Checks++;
        if (condition)
        {
            return true;
        }

        Failures++;
        _error(failMessage, null);
        return false;
    }

    /// <summary>已经由调用方自行输出过错误行（例如用例抛异常）的一条失败断言：只记账，不重复输出。</summary>
    internal void FailWithoutMessage()
    {
        Checks++;
        Failures++;
    }

    /// <summary>用例不成立：只有**环境/产物**问题才允许走这里，绝不用来掩盖断言失败。</summary>
    internal void MarkNotApplicable(string reason)
    {
        NotApplicable = true;
        NotApplicableReason ??= reason;
    }

    internal PureCheckOutcome Outcome() => new(Checks, Failures, NotApplicable, NotApplicableReason);
}

/// <summary>
/// 一次纯逻辑自检的结果。<see cref="ExitCode"/> 沿用既有口径「自检全集退出码语义不变」：
/// 0 = 通过；1 = 有断言失败；5 = 用例不成立（环境/产物问题，不是缺陷）。
/// </summary>
internal readonly record struct PureCheckOutcome(
    int Checks,
    int Failures,
    bool NotApplicable,
    string? NotApplicableReason)
{
    internal bool Passed => Failures == 0 && !NotApplicable;

    internal int ExitCode => NotApplicable ? 5 : (Failures == 0 ? 0 : 1);

    internal string Describe()
        => NotApplicable
            ? $"用例不成立（环境/产物）：{NotApplicableReason ?? "(未说明)"}"
            : Failures == 0
                ? $"通过（{Checks} 条断言）"
                : $"未通过（{Failures}/{Checks} 条断言失败）";
}

/// <summary>用例名的唯一清单：控制台运行器、xUnit 报告与门禁都用它，避免三处各写一份名字。</summary>
internal static class PureLogicChecks
{
    internal const string ProfileSource = "profile-source";

    internal const string SnapGeometry = "snap-geometry";

    internal const string ManualLineSplit = "manual-line-split";

    /// <summary>屏幕/导出差异工具（<see cref="TableTsvDiff"/>）自身的可判性。</summary>
    internal const string TableTsvDiff = "table-tsv-diff";

    /// <summary>表格还原与导出核心（区间合并 / 行聚类 / 阅读顺序 / 单元格归一 / 四种导出）。</summary>
    internal const string ExportCore = "export-core";

    /// <summary>
    /// 用例名的唯一清单：控制台运行器、xUnit 报告与门禁都用它。
    ///
    /// 这张清单**不能**按编译目标裁剪：控制台运行器（<c>tests/SimOCR.Tests/Program.cs</c>）
    /// 与 xUnit 读的是**同一个程序集**（SimOCR.Tests）里的这一份清单；应用侧的 internal 类型
    /// 经 <c>InternalsVisibleTo</c> 可见。所以任何形如 <c>#if</c> 的裁剪都会让运行器少看到用例
    /// （实测：加了 <c>SIMOCR_NO_TEST_ONLY_CHECKS</c> 之后运行器从 4 用例 / 47 断言掉到
    /// 3 用例 / 37 断言）—— 那是"悄悄少测了一条"的静默退化，正是门禁要防的东西，所以**不裁剪**。
    /// 需要断言不进发布产物时，唯一的正道是让它**只存在于测试程序集**（本文件与
    /// <c>PureLogic/*.cs</c> 都不被 <c>SimOCR.csproj</c> 链接），而不是在这里按编译目标改清单。
    /// </summary>
    internal static readonly IReadOnlyList<string> All =
        [ProfileSource, SnapGeometry, ManualLineSplit, TableTsvDiff, ExportCore];

    /// <summary>按名字跑一条用例（控制台运行器与 xUnit 都走这里，不各写一份 switch）。</summary>
    internal static PureCheckOutcome Run(string name, PureCheckContext ctx) => name switch
    {
        ProfileSource => ProfileSourceChecks.Run(ctx),
        SnapGeometry => SnapGeometryChecks.Run(ctx),
        ManualLineSplit => ManualLineSplitChecks.Run(ctx),
        TableTsvDiff => TableTsvDiffChecks.Run(ctx),
        ExportCore => ExportCoreChecks.Run(ctx),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "未知的纯逻辑用例名"),
    };
}
