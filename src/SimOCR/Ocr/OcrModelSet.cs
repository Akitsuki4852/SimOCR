using SimOCR.Diagnostics;

namespace SimOCR.Ocr;

/// <summary>模型目录是怎么定下来的（设置页要把来源显示给用户，避免"它到底用了哪份模型"说不清）。</summary>
internal enum OcrModelSource
{
    /// <summary>设置里显式指定的目录（<c>OcrSettings.ModelDirectory</c>）。</summary>
    Settings,

    /// <summary>按约定在 exe 同目录的 <c>models\v5-cn</c> 找到的（随包那份）。</summary>
    Convention,
}

/// <summary>
/// 中文 OCR 模型集合（PP-OCRv5）：det / rec / cls + 字典。
///
/// 模型**随包**放在 exe 同目录的 <c>models\v5-cn\</c>（由 csproj 的 CopyOcrModels* 目标从仓库根
/// <c>models\v5</c> 拷来；仓库根那份由 <c>build/fetch-models.ps1</c> 下载并校验 SHA256）。
/// 为什么不与 RapidOcrNet 包自带的 latin 模型共用 <c>models\v5</c>：避免混淆，且我们总是显式传路径。
///
/// 0.8.1 起用户可以在设置页指定别的模型目录（<see cref="Locate"/> 的
/// <c>configuredDirectory</c>）：设置值不可用时**回退到上面的约定目录**并记警告，
/// 保证"设置写坏了"只会退回随包模型，不会变成 OCR 直接不可用。
/// </summary>
internal sealed class OcrModelSet
{
    /// <summary>约定的子目录名：exe 同目录 <c>models\&lt;这个名字&gt;</c>。</summary>
    internal const string ConventionSubdirectory = "v5-cn";

    /// <summary>模型根目录名（相对 exe / 程序集目录）。</summary>
    internal const string ModelsFolderName = "models";

    /// <summary>判断"这份目录能不能用"的探针文件（其余文件缺失由 <see cref="Missing"/> 报出）。</summary>
    private const string ProbeFileName = "det.onnx";

    private OcrModelSet(string directory, OcrModelSource source, string? fallbackReason)
    {
        Directory = directory;
        Source = source;
        FallbackReason = fallbackReason;
        DetPath = Path.Combine(directory, "det.onnx");
        RecPath = Path.Combine(directory, "rec.onnx");
        ClsPath = Path.Combine(directory, "cls.onnx");
        DictPath = Path.Combine(directory, "dict.txt");

        Missing = new[] { DetPath, RecPath, ClsPath, DictPath }
            .Where(path => !File.Exists(path))
            .ToArray();
    }

    internal string Directory { get; }

    internal string DetPath { get; }

    internal string RecPath { get; }

    internal string ClsPath { get; }

    internal string DictPath { get; }

    /// <summary>缺失的模型文件（空数组表示可用）。</summary>
    internal IReadOnlyList<string> Missing { get; }

    internal bool IsComplete => Missing.Count == 0;

    /// <summary>这个目录是"设置里指定的"还是"按约定找到的"。</summary>
    internal OcrModelSource Source { get; }

    /// <summary>
    /// 设置里指定的目录被忽略时的**给用户看**的原因（没有忽略则为 null）。
    /// 设置页拿它显示一条显著提示：用户选了目录却看不出为什么没生效是最糟的体验。
    /// 缺哪个文件名、完整路径这些排查信息只写日志，不放在这里。
    /// </summary>
    internal string? FallbackReason { get; }

    /// <summary>给用户看的来源说明。</summary>
    internal string SourceDescription => Source == OcrModelSource.Settings ? "来自设置" : "程序自带";

    /// <summary>给用户看的说明（缺失时指出怎么办，不出现构建脚本/路径之类的内容）。</summary>
    internal string Describe()
        => IsComplete
            ? "OCR 中文模型已就绪"
            : $"缺少 {Missing.Count} 个 OCR 模型文件，暂时无法识别文字。请重新安装 SimOCR（安装包已自带模型）；" +
              "如果用便携版，请重新解压完整的压缩包。";

    /// <summary>日志/排查用：带目录、缺失清单与构建脚本提示。</summary>
    internal string DescribeForLog()
        => IsComplete
            ? $"中文模型就绪（{Directory}）"
            : $"缺少 {Missing.Count} 个中文模型文件（{Directory}）：请运行 build/fetch-models.ps1 后重新发布，" +
              $"或把模型放到该目录。缺失：{string.Join("、", Missing.Select(Path.GetFileName))}";

    /// <summary>
    /// 定位模型目录。
    /// 顺序：设置里显式指定的目录（存在且含 <c>det.onnx</c>）→ exe 同目录的 <c>models\v5-cn</c>
    /// → <see cref="AppContext.BaseDirectory"/> 下的 <c>models\v5-cn</c>（单文件版的自解压目录也在这里）
    /// → 返回第一个候选，让 <see cref="Describe"/> 报出应该把模型放哪。
    /// 设置值不可用时记一条警告日志，并把原因留在 <see cref="FallbackReason"/>。
    /// </summary>
    internal static OcrModelSet Locate(string? configuredDirectory = null, LogService? log = null)
    {
        string? fallbackReason = null;

        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var wanted = ResolveConfiguredDirectory(configuredDirectory!.Trim());
            // 注意：本类有个实例属性 Directory，静态方法里必须写全 System.IO.Directory 才能拿到 BCL 类型
            if (System.IO.Directory.Exists(wanted) && File.Exists(Path.Combine(wanted, ProbeFileName)))
            {
                log?.Info($"OCR 模型目录：{wanted}（来自设置）");
                return new OcrModelSet(wanted, OcrModelSource.Settings, null);
            }

            // 技术细节（缺哪个文件、完整路径）只进日志：
            // fallbackReason 会显示在设置页的红字提示里，那里需要一句用户能懂、也知道怎么办的话。
            var detail = System.IO.Directory.Exists(wanted)
                ? $"设置里的模型目录缺少 {ProbeFileName}：{wanted}"
                : $"设置里的模型目录不存在：{wanted}";
            fallbackReason = "设置里指定的模型文件夹不可用，已改用程序自带的模型。";
            log?.Warn($"{detail}，已回退到约定目录 {ModelsFolderName}\\{ConventionSubdirectory}");
        }

        var candidates = ModelsRootCandidates()
            .Select(root => Path.Combine(root, ConventionSubdirectory))
            .ToList();

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, ProbeFileName)))
            {
                return new OcrModelSet(candidate, OcrModelSource.Convention, fallbackReason);
            }
        }

        // 一个都没找到：返回第一个候选，让 Describe() 报出应该放哪
        return new OcrModelSet(candidates[0], OcrModelSource.Convention, fallbackReason);
    }

    /// <summary>
    /// <c>models</c> 目录（与模型定位用同一套候选：exe 同目录优先）。
    /// 都不存在时返回第一个候选（调用方可以据此提示"该建在哪"）。
    /// </summary>
    internal static string ModelsRoot()
    {
        var candidates = ModelsRootCandidates();
        foreach (var candidate in candidates)
        {
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// 把设置里的模型目录解析成绝对路径：绝对路径直接用；相对路径当作 <c>models\</c> 下的子目录
    /// （下拉里选的是目录名，这样配置跟着 exe 走、换机器不用改）。
    /// </summary>
    internal static string ResolveConfiguredDirectory(string configuredDirectory)
        => Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(ModelsRoot(), configuredDirectory);

    /// <summary>
    /// <c>models\</c> 下的真实子目录名（设置页下拉的候选项，模型齐全的排最前、约定目录次优先）。
    /// 目录不存在或一个子目录都没有时返回空表 —— 只列真实存在的目录名，
    /// 不塞"（缺失）"这种说明性条目，否则调用方拿它拼路径会拼出一个假目录。
    /// </summary>
    internal static IReadOnlyList<string> AvailableDirectories()
    {
        var root = ModelsRoot();
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        return System.IO.Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderByDescending(name => IsCompleteDirectory(Path.Combine(root, name)))
            .ThenBy(name => string.Equals(name, ConventionSubdirectory, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>目录里四个模型文件是否齐全（下拉里标注"缺文件"用）。</summary>
    internal static bool IsCompleteDirectory(string directory)
        => !string.IsNullOrWhiteSpace(directory) &&
           new OcrModelSet(directory, OcrModelSource.Convention, null).IsComplete;

    private static List<string> ModelsRootCandidates()
    {
        var candidates = new List<string>();
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var exeDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                candidates.Add(Path.Combine(exeDir, ModelsFolderName));
            }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, ModelsFolderName));
        return candidates;
    }
}
