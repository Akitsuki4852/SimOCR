using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SimOCR.PureLogic;
using Xunit;

namespace SimOCR.Tests;

/// <summary>
/// 纯逻辑断言的**控制台运行器**：<c>tests\SimOCR.Tests\bin\&lt;配置&gt;\net10.0-windows\SimOCR.Tests.exe</c>。
///
/// 为什么需要它：某些受限环境（CI 沙箱、没有交互桌面的会话）里 testhost 可能在启动阶段失败
/// （testhost 调 <c>SetParentProcessExitCallback</c>/<c>OpenProcess</c> 被拒 → <c>Win32Exception (5)</c>）。
/// 如果纯逻辑断言只以 xUnit <c>[Fact]</c> 的形式存在，这些环境里就完全没有覆盖。
///
/// 做法：**同一个程序集**既是 xUnit 测试程序集（<c>dotnet test</c> 用 vstest 加载它，
/// 见 <see cref="SimOCR.Tests.PureLogicTests"/>），也是一个普通控制台程序（这里的 <c>Main</c>）。
/// 断言本体只有一份（<c>PureLogic/*.cs</c>），两条路径都调
/// <see cref="PureLogicChecks.Run"/>，因此不存在"xUnit 一份、运行器一份"的复制。
///
/// 退出码：0 = 全部通过；1 = 有断言失败；2 = 用法错误；5 = 用例不成立（环境/产物问题，不是缺陷）。
/// </summary>
internal static class Program
{
    // 0.10.6：可用用例清单**不再手写**，直接来自唯一的用例名清单 `PureLogicChecks.All`。
    // 手写清单在 0.10.3…0.10.5 期间漏掉了 `table-tsv-diff`（新增的用例）——
    // 用法文案与实际能力不一致，是"两份手写清单迟早漂移"的又一次实证。
    private static readonly string Usage =
        $"用法：SimOCR.Tests.exe [--case=all|{string.Join(",", PureLogicChecks.All)}] [--list-facts]\n" +
        "      --case     要跑的纯逻辑用例（默认 all）\n" +
        "      --list-facts 列出本程序集里的测试方法（[Fact]/[Theory]）与展开后的用例数\n" +
        "      退出码：0 = 全部通过；1 = 有断言失败；2 = 用法错误；5 = 用例不成立（环境/产物）";

    [STAThread]
    private static int Main(string[] args)
    {
        // stdout 可能被门禁重定向到文件（gate-check.ps1 用 UTF-8 读回）：显式写成 UTF-8（不带 BOM），
        // 否则中文判据会在活动代码页（GBK）与 UTF-8 之间乱码，门禁的 okRegex 直接失配。
        // （CA2000）：Main 内是唯一持有者，用 using var 保证所有 return 路径都释放；
        // AutoFlush 已开，且关闭发生在进程退出前，输出内容与顺序不变。
        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        Console.SetOut(stdout);

        var requested = new List<string>();
        var listFacts = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--list-facts", StringComparison.Ordinal))
            {
                listFacts = true;
                continue;
            }

            if (arg.StartsWith("--case=", StringComparison.Ordinal))
            {
                foreach (var name in arg["--case=".Length..]
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.Equals(name, "all", StringComparison.Ordinal) &&
                        !PureLogicChecks.All.Contains(name, StringComparer.Ordinal))
                    {
                        Console.Error.WriteLine($"[用法] 未知用例：{name}（可用：all / {string.Join(", ", PureLogicChecks.All)}）");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }

                    if (string.Equals(name, "all", StringComparison.Ordinal))
                    {
                        requested.Clear();
                        requested.AddRange(PureLogicChecks.All);
                        break;
                    }

                    requested.Add(name);
                }

                continue;
            }

            Console.Error.WriteLine($"[用法] 无法识别的参数：{arg}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (listFacts)
        {
            ListFacts();
        }

        var cases = requested.Count > 0 ? requested : (listFacts ? [] : [.. PureLogicChecks.All]);
        if (cases.Count == 0)
        {
            return 0;
        }

        Console.WriteLine($"纯逻辑断言（无 testhost 的控制台运行器）：{string.Join(", ", cases)}");

        var failures = 0;
        var checks = 0;
        var notApplicable = new List<string>();

        foreach (var name in cases)
        {
            Console.WriteLine($"=== 用例：{name} ===");
            var ctx = new PureCheckContext(
                Console.WriteLine,
                (message, ex) => Console.WriteLine(ex is null ? message : $"{message} :: {ex}"));

            PureCheckOutcome outcome;
            try
            {
                outcome = PureLogicChecks.Run(name, ctx);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [失败] {name} 抛异常：{ex}");
                failures++;
                continue;
            }

            checks += outcome.Checks;

            if (outcome.NotApplicable)
            {
                notApplicable.Add($"{name}：{outcome.NotApplicableReason ?? "(未说明)"}");
                Console.WriteLine($"  [不成立] {name}：{outcome.NotApplicableReason ?? "(未说明)"}");
                continue;
            }

            if (outcome.Failures > 0)
            {
                failures += outcome.Failures;
                Console.WriteLine($"  [失败] {name}：{outcome.Failures}/{outcome.Checks} 条断言失败");
            }
            else
            {
                Console.WriteLine($"  [通过] {name}：{outcome.Checks} 条断言");
            }
        }

        Console.WriteLine();

        if (failures > 0)
        {
            Console.WriteLine($"=== 纯逻辑断言：未通过（{failures} 项失败 / 共 {checks} 条断言）===");
            return 1;
        }

        if (notApplicable.Count > 0)
        {
            Console.WriteLine($"=== 纯逻辑断言：用例不成立（{string.Join("；", notApplicable)}）===");
            return 5;
        }

        Console.WriteLine($"=== 纯逻辑断言：全部通过（{cases.Count} 条用例 / {checks} 条断言）===");
        return 0;
    }

    /// <summary>
    /// 列出本程序集里带 <see cref="FactAttribute"/> 的方法，以及 Theory 展开后的用例数。
    /// vstest 的发现规则就是"扫特性"，所以这份列表等价于 vstest 能发现的事实集合。
    /// </summary>
    private static void ListFacts()
    {
        var facts = typeof(Program).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes().Any(attribute => attribute is FactAttribute))
            .OrderBy(method => method.DeclaringType?.Name, StringComparer.Ordinal)
            .ThenBy(method => method.Name, StringComparer.Ordinal)
            .Select(method => (Method: method, Cases: CountCases(method)))
            .ToList();

        var totalCases = facts.Sum(fact => fact.Cases);
        Console.WriteLine($"  [xUnit] 测试方法：{facts.Count} 条（Theory 展开后 {totalCases} 个用例）");
        foreach (var fact in facts)
        {
            var suffix = fact.Cases > 1 ? $"（{fact.Cases} 例）" : string.Empty;
            Console.WriteLine($"    {fact.Method.DeclaringType?.FullName}.{fact.Method.Name}{suffix}");
        }
    }

    /// <summary>一条测试方法展开后的用例数：普通 <c>[Fact]</c> 为 1，<c>[Theory]</c> 为 InlineData 行数。</summary>
    private static int CountCases(MethodInfo method)
    {
        if (!method.GetCustomAttributes().Any(attribute => attribute is TheoryAttribute))
        {
            return 1;
        }

        var rows = method.GetCustomAttributes<InlineDataAttribute>().Count();
        return rows > 0 ? rows : 1;
    }
}
