namespace SimOCR.Export;

/// <summary>
/// 当前生效的表格解析配置档。**唯一真源**，但**不是"启动时初始化一次之后只读"** ——
/// 它在运行时会被设置窗切换（用户在设置页换档后立刻生效，不需要重启）：
///   * 启动引导：<c>AppComposition</c> 在组合根里调一次 <see cref="Initialize"/>；
///   * 运行中切换：设置窗口的"表格配置档"下拉 → 同一入口 <see cref="Initialize"/>
///     （换档只是换掉这里的静态状态，之后的识别/导出取到新档）；
///   * 纯逻辑断言：<c>tests/SimOCR.Tests/PureLogic/ProfileSourceChecks.cs</c> 也用同一入口换档。
///
/// **0.10.5**：过去的注释写"程序启动时初始化一次…之后全局只读"，
/// 与上面的运行时可切换事实不符（这条自 0.9.1 起被提过两次），已就地更正。
///
/// 查找顺序：
///   1. 设置里显式指定的 YAML 文件（用户配置目录）；
///   2. 程序目录下的 <c>Profiles/{name}.yaml</c>；
///   3. 程序目录下的 <c>Profiles/default.yaml</c>；
///   4. 代码内置兜底（不含任何业务词汇）。
/// </summary>
internal static class TableProfiles
{
    private static TableProfile _current = TableProfile.CreateFallback();

    /// <summary>加载过程中的提示（诊断页/自检会打印）。</summary>
    internal static string StatusMessage { get; private set; } = "尚未加载配置档，使用内置兜底";

    /// <summary>
    /// **当前生效配置档的唯一真源**（过去这里还有一份
    /// <c>TableValueExtractor _extractor</c> 转发壳，与 _current 是同一状态的第二个指针，
    /// 取值规则直接在 <see cref="TableProfile"/> 上调用）。
    /// </summary>
    internal static TableProfile Current => _current;

    /// <summary>列出 <c>Profiles/</c> 下可用的档名（不含扩展名，default 排最前）。</summary>
    internal static IReadOnlyList<string> AvailableProfiles()
    {
        var directory = TableProfile.ProfileDirectory();
        if (!Directory.Exists(directory))
        {
            return ["default"];
        }

        var names = Directory.EnumerateFiles(directory, "*.yaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => string.Equals(n, "default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? ["default"] : names;
    }

    /// <summary>
    /// 选定配置档。<paramref name="explicitPath"/> 非空时优先用它；
    /// 否则按 <paramref name="name"/> 在 Profiles 目录里找，最后回退 default。
    /// </summary>
    internal static void Initialize(string? name = null, string? explicitPath = null)
    {
        var notes = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var profile = TableProfile.TryLoad(explicitPath!, out var error);
            if (profile is not null)
            {
                SetCurrent(profile, $"已加载配置档：{profile.Display}（{explicitPath}）");
                return;
            }

            notes.Add($"指定的配置档无法加载（{error}）");
        }

        var wanted = string.IsNullOrWhiteSpace(name) ? "default" : name!.Trim();
        var candidate = Path.Combine(TableProfile.ProfileDirectory(), wanted + ".yaml");
        var loaded = TableProfile.TryLoad(candidate, out var candidateError);
        if (loaded is not null)
        {
            SetCurrent(loaded, $"已加载配置档：{loaded.Display}（{Path.GetFileName(candidate)}）");
            return;
        }

        notes.Add($"Profiles/{wanted}.yaml 无法加载（{candidateError}）");

        if (!string.Equals(wanted, "default", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackFile = TableProfile.TryLoad(TableProfile.DefaultProfilePath(), out var defaultError);
            if (fallbackFile is not null)
            {
                SetCurrent(fallbackFile, $"已回退到默认配置档（{Path.GetFileName(TableProfile.DefaultProfilePath())}）");
                return;
            }

            notes.Add($"Profiles/default.yaml 无法加载（{defaultError}）");
        }

        SetCurrent(TableProfile.CreateFallback(), "配置档不可用，使用内置兜底（仅通用数字列规则）");
    }

    private static void SetCurrent(TableProfile profile, string status)
    {
        _current = profile;
        StatusMessage = status;
    }
}
