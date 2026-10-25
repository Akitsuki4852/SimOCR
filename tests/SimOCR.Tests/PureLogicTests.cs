using System;
using SimOCR.PureLogic;
using Xunit.Abstractions;

namespace SimOCR.Tests;

/// <summary>
/// 把**纯逻辑**自检搬进 xUnit 后的三条事实。
///
/// 这里**一行断言逻辑都没有** —— 每条 <c>[Fact]</c> 只是把同一份共享断言
/// （<c>PureLogic/*.cs</c>）跑起来、把输出接到 <see cref="ITestOutputHelper"/>、
/// 然后把结果转成 xUnit 断言。同一份共享断言也被
/// <c>Program.cs</c> 的控制台运行器调用（本机 testhost 起不来，门禁走那条路）。
///
/// 与"用例不成立"（退出码 5）的关系：控制台运行器保留 5（与 <c>--selftest-profile</c> 同义，
/// 门禁按"跳过"记账）；这里把"不成立"当**失败** —— 测试程序集自己就把
/// <c>Profiles/*.yaml</c> 拷到输出目录（见 SimOCR.Tests.csproj），"不成立"只可能是打包漏了，
/// 那不是环境限制而是缺陷，不该在 CI 里静默变绿。
/// </summary>
public sealed class PureLogicTests
{
    private readonly ITestOutputHelper _output;

    public PureLogicTests(ITestOutputHelper output) => _output = output;

    /// <summary>配置档单一来源：构建中途换档不会让同一张表混用两个档（原 <c>--selftest-profile</c>）。</summary>
    [Fact]
    public void 配置档单一来源_构建中途换档不混档() => RunShared(PureLogicChecks.ProfileSource);

    /// <summary>窗口吸附几何：28 条纯几何用例（原 <c>--selftest-snap</c> 的前半段）。</summary>
    [Fact]
    public void 吸附几何用例表_28条全通过() => RunShared(PureLogicChecks.SnapGeometry);

    /// <summary>手工分割线切分：6 条纯 <c>GridTableFiller</c> 断言（原 <c>--selftest-linegrid</c> 的前置断言）。</summary>
    [Fact]
    public void 手工分割线切分_6条全通过() => RunShared(PureLogicChecks.ManualLineSplit);

    /// <summary>屏幕/导出差异工具（从 <c>OcrResultWindow</c> 抽到测试侧的共享真源）自身的可判性。</summary>
    [Fact]
    public void 屏幕导出差异工具_可判() => RunShared(PureLogicChecks.TableTsvDiff);

    /// <summary>表格还原与导出核心：区间合并 / 行聚类 / 阅读顺序 / 单元格归一 / 四种导出的产物结构。</summary>
    [Fact]
    public void 表格还原与导出核心_可判() => RunShared(PureLogicChecks.ExportCore);

    private void RunShared(string name)
    {
        var ctx = new PureCheckContext(
            _output.WriteLine,
            (message, ex) => _output.WriteLine(ex is null ? message : $"{message} :: {ex}"));

        var outcome = PureLogicChecks.Run(name, ctx);

        Assert.False(
            outcome.NotApplicable,
            $"{name}：用例不成立（环境/产物问题，不是断言失败）：{outcome.NotApplicableReason ?? "(未说明)"}");
        Assert.True(
            outcome.Failures == 0,
            $"{name}：{outcome.Failures}/{outcome.Checks} 条断言失败（逐条细节见本测试的输出）");
    }
}
