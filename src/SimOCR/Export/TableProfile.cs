using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimOCR.Export;

/// <summary>
/// 表格解析配置档：把"业务词汇 + 取值规则 + 派生列 + 校验规则 + 阈值"从代码里搬出去。
///
/// 设计意图：
///   * **代码里只留算法**（列切分、行聚类、字段绑定、取值骨架），它们对"表格"这件事通用；
///   * **词汇与业务约定全部来自 YAML**（<c>Profiles/*.yaml</c>），因为它们只对某一类表成立。
///   * 没有配置文件时使用内置兜底（<see cref="CreateFallback"/>），程序照常工作。
///
/// 字段名一律用 ASCII 内部名（ITEM / QUANTITY …），显示名（货号 / 数量 …）来自配置。
/// </summary>
internal sealed class TableProfile
{
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RawRuleSet> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>英寸记号的字符集（直引号 + 各种印刷体引号）。</summary>
    private static readonly char[] InchMarks = ['"', '\u201C', '\u201D', '\u2033'];

    /// <summary>配置没声明 <c>nonValues</c> 时的兜底写法。</summary>
    private static readonly string[] DefaultNonValues =
        ["合计", "小计", "总计", "total", "n/a", "na", "-", "—", "–", "/"];

    private TableProfile(RawProfile raw, string? sourcePath)
    {
        SourcePath = sourcePath;
        Name = string.IsNullOrWhiteSpace(raw.Name) ? "generic" : raw.Name!;
        Display = string.IsNullOrWhiteSpace(raw.Display) ? Name : raw.Display!;

        // 兜底正则：配置里没写 number 时，至少还能取数字
        _patterns["number"] = new Regex(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

        foreach (var (key, pattern) in raw.Patterns ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                _patterns[key] = new Regex(pattern, RegexOptions.Compiled);
            }
        }

        UnitWords = (raw.UnitWords ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();

        // 单位词可能写成 "/DZS"（"每打"这类斜杠前缀），做"受控单位后缀"白名单时要去掉斜杠
        UnitSuffixes = UnitWords
            .Select(w => w.Trim().TrimStart('/'))
            .Where(w => w.Length > 0)
            .ToArray();

        CurrencySymbols = string.IsNullOrEmpty(raw.CurrencySymbols) ? "¥￥$€£" : raw.CurrencySymbols!;

        // 噪声字符默认为"OCR 常把货币符号误读成的汉字"；配置里显式写空串表示"只要货币符号"。
        CurrencyNoiseGlyphs = raw.CurrencyNoiseGlyphs ?? "我天";
        LetterSizes = (raw.LetterSizes ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        Groups = (raw.FieldGroups ?? [])
            .Where(g => g.Labels is { Count: > 0 })
            .Select(g => new FieldGroup(
                string.IsNullOrWhiteSpace(g.Name) ? "generic" : g.Name!,
                g.Detect ?? [],
                g.Labels!,
                (g.Infer ?? []).Select(r => new InferRule(
                    r.Kind ?? string.Empty,
                    r.Fields ?? [])).ToArray()))
            .ToArray();

        Roles = raw.Roles ?? new RoleConfig();
        WholeTextFields = raw.WholeTextFields ?? [];
        BlockRowLevelOverride = raw.BlockRowLevelOverride ?? [];
        BorrowLeft = (raw.BorrowLeft ?? []).ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        FallbackGroupName = string.IsNullOrWhiteSpace(raw.FallbackGroup) ? "generic" : raw.FallbackGroup!;

        var fields = new Dictionary<string, FieldSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, spec) in raw.Fields ?? [])
        {
            fields[name] = new FieldSpec(
                name,
                string.IsNullOrWhiteSpace(spec.Display) ? name : spec.Display!,
                string.Equals(spec.Kind, "number", StringComparison.OrdinalIgnoreCase));
        }

        Fields = fields;

        Expand = (raw.Expand ?? []).ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        Skeleton = (raw.Skeleton ?? []).Select(row => (IReadOnlyList<string>)row.ToArray()).ToArray();

        foreach (var (name, ruleSet) in raw.Rules ?? [])
        {
            _rules[name] = ruleSet;
        }

        Derived = (raw.Derived ?? []).Select(d => new DerivedColumn(
            d.Name ?? string.Empty,
            string.IsNullOrWhiteSpace(d.Display) ? d.Name ?? string.Empty : d.Display!,
            string.Equals(d.Kind, "number", StringComparison.OrdinalIgnoreCase),
            d.From ?? [],
            d.Formula ?? string.Empty,
            d.PerDozen <= 0 ? 12 : d.PerDozen)).ToArray();

        Checks = raw.Checks ?? new CheckConfig();
        ContentDetection = raw.ContentDetection ?? new ContentDetectionConfig();
        Thresholds = raw.Thresholds ?? new ThresholdConfig();
        Features = raw.Features ?? new FeatureConfig();

        // "这些格子里写的其实是'没有值'"的写法集合：配置没写时用代码里的通用兜底。
        NonValues = (raw.NonValues ?? [.. DefaultNonValues])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();

        // 内容判定用的标题关键字与规则里引用的正则可能指向 patterns，这里不预先解析。
    }

    /// <summary>这些写法表示"这一格没有值"（数值列里不算异常）；来自配置 <c>nonValues</c>。</summary>
    internal IReadOnlyList<string> NonValues { get; }

    /// <summary>配置来源文件（null 表示用的内置兜底）。</summary>
    internal string? SourcePath { get; }

    internal string Name { get; }

    internal string Display { get; }

    internal IReadOnlyList<FieldGroup> Groups { get; }

    /// <summary>语义角色：算法里需要"认出某一类列"的地方（size 列、carton 类列、item 列……）。</summary>
    internal RoleConfig Roles { get; }

    internal string FallbackGroupName { get; }

    internal IReadOnlyDictionary<string, FieldSpec> Fields { get; private set; }

    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Expand { get; }

    internal IReadOnlyList<IReadOnlyList<string>> Skeleton { get; }

    internal IReadOnlyList<DerivedColumn> Derived { get; }

    internal CheckConfig Checks { get; }

    internal ContentDetectionConfig ContentDetection { get; }

    internal ThresholdConfig Thresholds { get; }

    internal FeatureConfig Features { get; }

    internal IReadOnlyList<string> UnitWords { get; }

    /// <summary>单位词去掉斜杠前缀后的形态（供"受控单位后缀"白名单用），如 <c>/DZS</c> → <c>DZS</c>。</summary>
    internal IReadOnlyList<string> UnitSuffixes { get; }

    /// <summary>取值时用整段原文的字段（其余字段先遮蔽再取）。</summary>
    internal IReadOnlyList<string> WholeTextFields { get; private set; } = [];

    /// <summary>出现这些字段时禁用"整行数值覆盖"。</summary>
    internal IReadOnlyList<string> BlockRowLevelOverride { get; private set; } = [];

    /// <summary>字段取不到值时，允许从左边哪些字段借文字再取一次。</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> BorrowLeft { get; private set; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);


    internal string CurrencySymbols { get; }

    /// <summary>除货币符号外，OCR 常把符号误读成的字符（引擎相关，可由配置覆盖）。</summary>
    internal string CurrencyNoiseGlyphs { get; }

    /// <summary>数值净化时要视为"货币噪声"的全部字符（货币符号 + 误读字符）。</summary>
    internal string CurrencyNoise => CurrencySymbols + CurrencyNoiseGlyphs;

    internal IReadOnlyList<string> LetterSizes { get; }

    // -------------------------------------------------------------------------
    // 加载
    // -------------------------------------------------------------------------

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>从 YAML 文件加载；文件不存在或解析失败时返回 null（由调用方决定兜底）。</summary>
    internal static TableProfile? TryLoad(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"配置文件不存在：{path}";
                return null;
            }

            var raw = Deserializer.Deserialize<RawProfile>(File.ReadAllText(path));
            if (raw is null)
            {
                error = "配置文件为空";
                return null;
            }

            return new TableProfile(raw, path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>内置兜底配置：不含任何业务词汇，只有最通用的"数字列"骨架。</summary>
    internal static TableProfile CreateFallback()
    {
        return new TableProfile(new RawProfile { Name = "fallback", Display = "内置兜底" }, null);
    }

    /// <summary>配置目录：优先跟随 exe（便携形态），再退回程序集目录。</summary>
    internal static string ProfileDirectory()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Profiles");
        return local;
    }

    internal static string DefaultProfilePath() => Path.Combine(ProfileDirectory(), "default.yaml");

    // -------------------------------------------------------------------------
    // 查询辅助
    // -------------------------------------------------------------------------

    internal bool TryGetField(string? name, out FieldSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(name) && Fields.TryGetValue(name, out var found))
        {
            spec = found;
            return true;
        }

        spec = default!;
        return false;
    }

    internal bool IsNumeric(string? name) => TryGetField(name, out var spec) && spec.Numeric;

    internal string DisplayOf(string? name)
        => TryGetField(name, out var spec) ? spec.Display : name ?? string.Empty;

    /// <summary>
    /// 把"列标签"归一成规范字段名（找不到返回 null）。
    ///
    /// 为什么需要：同一张表的列标签在不同路径下来源不同 ——
    ///   * 表头路径给出的是**显示名**（配置里的 <c>display</c>）；
    ///   * 列内容推断与语义骨架路径给出的是**字段名**（ASCII id）；
    ///   * 派生列的名字不在 <see cref="Fields"/> 里，但有自己的显示名。
    /// 因此内部逻辑（派生列、算术校验、取值规则、列展开、按键分组）一律先用它归一，
    /// 再按字段名查表；对外展示（界面、导出表头）继续用显示名。
    /// </summary>
    internal string? CanonicalId(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        if (Fields.ContainsKey(label))
        {
            return label;
        }

        foreach (var (name, spec) in Fields)
        {
            if (string.Equals(spec.Display, label, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        foreach (var derived in Derived)
        {
            if (string.Equals(derived.Display, label, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(derived.Name, label, StringComparison.OrdinalIgnoreCase))
            {
                return derived.Name;
            }
        }

        return null;
    }

    internal Regex? Pattern(string key) => _patterns.TryGetValue(key, out var regex) ? regex : null;

    /// <summary>item 角色字段名（配置里没有则返回 null）。</summary>
    internal string? ItemField => Roles.Item;

    /// <summary>size 角色字段名（配置里没有则返回 null）。</summary>
    internal string? SizeField => Roles.Size;

    /// <summary>整张表可以出现的所有字段名（按配置顺序）。</summary>
    internal IReadOnlyList<string> AllFields => Fields.Keys.ToArray();

    /// <summary>
    /// 某一列的"表头文字"是否就是该字段。
    /// 表头行既是对外显示名（界面/导出用），又是内部定位键（派生列、算术校验、按键分组用），
    /// 因此**内部名与显示名都算命中** —— 这样算法里永远不必写死任何一种语言的名字。
    /// </summary>
    internal bool LabelIsField(string? label, string? field)
        => !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(field)
           && string.Equals(CanonicalId(label), CanonicalId(field), StringComparison.OrdinalIgnoreCase);

    /// <summary>在表头文字列表里找该字段所在的列（找不到返回 -1）。</summary>
    internal int IndexOfLabel(IReadOnlyList<string> labels, string? field)
    {
        for (var i = 0; i < labels.Count; i++)
        {
            if (LabelIsField(labels[i], field))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>多值列表版式（需要互补行合并 / 按键分组的那类版式）。由配置开关决定，不靠组名猜测。</summary>
    internal bool IsMultiValueLayout => Features.MergeComplementaryRows || Features.GroupByKey;

    /// <summary>
    /// 该组可用的语义骨架（列结构塌陷时用）。
    /// <paramref name="preferSmallest"/> 为真时选该组字段能命中的最短一条。
    /// </summary>
    internal IReadOnlyList<string> SkeletonFor(FieldGroup group, bool preferSmallest)
    {
        if (Skeleton.Count == 0)
        {
            return [];
        }

        var ordered = Skeleton.OrderBy(s => s.Count).ToList();
        if (!preferSmallest)
        {
            return ordered[^1];
        }

        var groupFields = group.Labels.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var match = ordered.FirstOrDefault(s => s.Any(groupFields.Contains));
        return match ?? ordered[0];
    }

    /// <summary>按内容特征判定版式（规则来自配置；通用档无规则即恒为 false）。</summary>
    internal bool DetectByContent(string ruleName, IReadOnlyList<Ocr.OcrLine> lines)
        => new ContentDetector(this).Matches(ruleName, lines);

    internal bool ContainsLetterSize(string token)
        => LetterSizes.Any(s => string.Equals(s, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>这些字段取值时用**整段原文**（而不是遮蔽后的残文）。标签可以是字段名或显示名。</summary>
    internal bool UsesWholeText(string? name)
        => CanonicalId(name) is { } id && WholeTextFields.Contains(id);

    /// <summary>出现这些字段时不做"整行数值覆盖"（多值/多列表格会把数字误分配）。标签可以是字段名或显示名。</summary>
    internal bool BlocksRowLevelOverride(IReadOnlyList<string> labels)
        => labels.Any(l => CanonicalId(l) is { } id &&
                           BlockRowLevelOverride.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 只补充"有意义"的内容（竖带补充识别用它筛新增行）：
    ///   * 含数字的一律算 —— 表格里的值绝大多数都是数字；
    ///   * **英寸记号只有在配置档声明了尺寸记号时才算**：`32"` 这种纯记号行是服装/鞋类的尺码写法，
    ///     对不声明尺寸记号的表格只会带进噪声，因此不能写死在代码里。
    /// </summary>
    internal bool HasMeaningfulValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Any(char.IsDigit) || (DeclaresSizeNotation && text.IndexOfAny(InchMarks) >= 0);
    }

    /// <summary>配置档是否声明了"尺寸记号"（英寸写法或字母尺码词表）。</summary>
    private bool DeclaresSizeNotation
        => LetterSizes.Count > 0 || Pattern("inch") is not null || Pattern("letterSize") is not null;


    /// <summary>某字段是否有专门的取值规则（没有则调用方退回"按坐标取整段文字"）。标签可以是字段名或显示名。</summary>
    internal bool HasRule(string? name)
        => CanonicalId(name) is { } id && _rules.ContainsKey(id);

    /// <summary>挑出与该页文字匹配的标签组；都不匹配时返回 fallback 组。</summary>
    internal FieldGroup ResolveGroup(string normalizedPageText)
    {
        foreach (var group in Groups)
        {
            if (group.Detect.Count == 0)
            {
                continue;
            }

            foreach (var condition in group.Detect)
            {
                if (MatchesDetect(normalizedPageText, condition))
                {
                    return group;
                }
            }
        }

        return Groups.FirstOrDefault(g => string.Equals(g.Name, FallbackGroupName, StringComparison.OrdinalIgnoreCase))
               ?? Groups.FirstOrDefault()
               ?? new FieldGroup("generic", [], new Dictionary<string, List<string>>(), []);
    }

    private static bool MatchesDetect(string squashedText, string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return false;
        }

        // 形如 "A+B"：两个子串都要出现
        var parts = condition.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.All(p => squashedText.Contains(TableFieldBinder.Squash(p), StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------
    // 取值：与算法版本行为一致，规则来自配置
    // -------------------------------------------------------------------------

    /// <summary>
    /// 把"已经属于别的字段"的部分遮蔽掉：按配置里的 token 型 pattern 与单位词做替换。
    /// 这是**行级**语义抽取的前提 —— 一行常被识别成一个大框，若不遮蔽，
    /// item / size 这类字段里的数字会被当成数值字段的值。
    /// 按**模式**遮蔽（而不是按取出来的值替换）才稳，因为原文的引号/全角写法可能与取值结果不一致。
    /// </summary>
    internal string MaskClaimed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var masked = text;
        foreach (var key in new[] { "itemCode", "inch", "letterSize" })
        {
            var regex = Pattern(key);
            if (regex is not null)
            {
                masked = regex.Replace(masked, " ");
            }
        }

        foreach (var word in UnitWords)
        {
            masked = Regex.Replace(masked, Regex.Escape(word), " ", RegexOptions.IgnoreCase);
        }

        return string.IsNullOrEmpty(CurrencySymbols)
            ? masked
            : Regex.Replace(masked, $"[{Regex.Escape(CurrencySymbols)}]", " ");
    }

    /// <summary>从"遮蔽后的残文"里按字段取数（使用规则里的 masked 段）。</summary>
    internal string ExtractFromMasked(string? name, string masked)
        => ExtractBy(name, masked, maskedSection: true);

    /// <summary>按字段名从"这一段文字"里取值；取不到返回空串（使用规则里的 whole 段）。</summary>
    internal string Extract(string? name, string text)
        => ExtractBy(name, text, maskedSection: false);

    private string ExtractBy(string? name, string text, bool maskedSection)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // 调用方可能传字段名，也可能传显示名（列标签就是显示名），这里统一归一
        var fieldId = CanonicalId(name);
        if (fieldId is null || !_rules.TryGetValue(fieldId, out var ruleSet))
        {
            return string.Empty;
        }

        var rule = maskedSection ? ruleSet.Masked : ruleSet.Whole;
        if (rule is null)
        {
            return string.Empty;
        }

        var normalized = Normalize(text);

        if (!string.IsNullOrWhiteSpace(rule.Regex))
        {
            return ExtractByRegex(rule, normalized);
        }

        if (rule.Combine is { Count: > 0 })
        {
            return ExtractCombined(rule.Combine, normalized);
        }

        if (!string.IsNullOrWhiteSpace(rule.Number))
        {
            // "none" 表示"这个字段不从文字里按数字位次取值"（例如纯文本列）
            if (string.Equals(rule.Number, "none", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return ExtractByNumber(rule, normalized);
        }

        return string.Empty;
    }

    private string ExtractByRegex(RawRule rule, string text)
    {
        var pattern = Pattern(rule.Regex!);
        if (pattern is null)
        {
            return string.Empty;
        }

        var best = string.Empty;
        foreach (Match match in pattern.Matches(text))
        {
            var token = match.Value.Trim();
            if (rule.MinLength > 0 && token.Length < rule.MinLength)
            {
                continue;
            }

            if (rule.RequireLetterAndDigit && !(token.Any(char.IsDigit) && token.Any(char.IsLetter)))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.ExcludeIfIn) &&
                string.Equals(rule.ExcludeIfIn, "letterSizes", StringComparison.OrdinalIgnoreCase) &&
                ContainsLetterSize(token))
            {
                continue;
            }

            if (rule.PreferParenthesised && token.Contains('('))
            {
                return token;
            }

            if (token.Length > best.Length)
            {
                best = token;
            }
        }

        return best;
    }

    private string ExtractCombined(IReadOnlyList<string> patterns, string text)
    {
        var parts = new List<string>();
        foreach (var key in patterns)
        {
            var pattern = Pattern(key);
            if (pattern is null)
            {
                continue;
            }

            if (string.Equals(key, "letterSize", StringComparison.OrdinalIgnoreCase))
            {
                // 字母尺码必须按 token 整体匹配，否则 item 里的 M / L 会被误判
                foreach (var token in text.Split([' ', ',', '\t', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleaned = token.Trim().Trim('"', '\u201C', '\u201D', '\u2033', '\'', '/');
                    if (cleaned.Length > 0 && ContainsLetterSize(cleaned))
                    {
                        parts.Add(cleaned);
                    }
                }

                continue;
            }

            foreach (Match match in pattern.Matches(text))
            {
                var value = match.Value.Trim();
                if (value.Length > 0)
                {
                    parts.Add(value);
                }
            }
        }

        return string.Join(" ", parts.Distinct());
    }

    private string ExtractByNumber(RawRule rule, string text)
    {
        var numbers = Numbers(text);
        if (rule.IntsOnly)
        {
            numbers = numbers.Where(n => !n.Contains('.')).ToList();
        }
        else if (rule.DecimalsOnly)
        {
            numbers = numbers.Where(n => n.Contains('.')).ToList();
        }

        if (numbers.Count == 0)
        {
            return string.Empty;
        }

        string candidate = rule.Number!.ToLowerInvariant() switch
        {
            "first" => numbers[0],
            "last" => numbers[^1],
            "second" => numbers.Count >= 2 ? numbers[1] : string.Empty,
            "all" => string.Join(" ", numbers),
            _ => int.TryParse(rule.Number, out var index) && index >= 0 && index < numbers.Count
                ? numbers[index]
                : string.Empty,
        };

        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (rule.Max > 0 &&
            double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            value > rule.Max)
        {
            return string.Empty;
        }

        return candidate;
    }

    /// <summary>
    /// 全角数字/符号拉回半角（识别结果里常见）。
    ///
    /// 这里改为调用强版 <see cref="CellValidator.Normalize"/>，本方法不再自带一份弱实现。
    /// 收敛前两条路径的清洗强度不同：显示路径走强版（去零宽/软连字符/BOM、
    /// 统一各种连字符与小数点、全角 ASCII `！-～`、全角空格、首尾 Trim），导出路径走这里的弱版
    /// （只映射全角数字、`．`、`，`）→ **同一格在屏幕与导出里清洗结果不同**。
    /// 现在只剩一份实现，强版为准；本方法保留为语义别名，调用点（<see cref="FieldSemantics.CleanNumeric"/>、
    /// <see cref="FieldSemantics.Normalize"/> 的删除）无需改动。
    /// </summary>
    internal static string Normalize(string text) => CellValidator.Normalize(text);

    /// <summary>文本里出现的所有数字（保留千分位写法，去掉逗号）。</summary>
    internal List<string> Numbers(string text)
    {
        var result = new List<string>();
        var regex = Pattern("number");
        if (regex is null)
        {
            return result;
        }

        foreach (Match match in regex.Matches(text))
        {
            var value = match.Value.Replace(",", string.Empty);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                result.Add(value);
            }
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // YAML 反序列化用的原始类型（宽松：字段缺失即用默认值）
    // -------------------------------------------------------------------------

    internal sealed class RawProfile
    {
        public int Version { get; set; } = 1;
        public string? Name { get; set; }
        public string? Display { get; set; }
        public List<RawFieldGroup>? FieldGroups { get; set; }
        public string? FallbackGroup { get; set; }
        public Dictionary<string, RawFieldSpec>? Fields { get; set; }
        public Dictionary<string, List<string>>? Expand { get; set; }
        public List<List<string>>? Skeleton { get; set; }
        public Dictionary<string, string>? Patterns { get; set; }
        public List<string>? UnitWords { get; set; }
        public string? CurrencySymbols { get; set; }
        public string? CurrencyNoiseGlyphs { get; set; }
        public List<string>? NonValues { get; set; }
        public List<string>? LetterSizes { get; set; }
        public Dictionary<string, RawRuleSet>? Rules { get; set; }
        public RoleConfig? Roles { get; set; }
        public List<string>? WholeTextFields { get; set; }
        public List<string>? BlockRowLevelOverride { get; set; }
        public Dictionary<string, List<string>>? BorrowLeft { get; set; }
        public List<RawDerived>? Derived { get; set; }
        public CheckConfig? Checks { get; set; }
        public ContentDetectionConfig? ContentDetection { get; set; }
        public ThresholdConfig? Thresholds { get; set; }
        public FeatureConfig? Features { get; set; }
    }

    internal sealed class RawFieldGroup
    {
        public string? Name { get; set; }
        public List<string>? Detect { get; set; }
        public Dictionary<string, List<string>>? Labels { get; set; }
        public List<RawInferRule>? Infer { get; set; }
    }

    internal sealed class RawInferRule
    {
        public string? Kind { get; set; }
        public List<string>? Fields { get; set; }
    }

    internal sealed class RawFieldSpec
    {
        public string? Display { get; set; }
        public string? Kind { get; set; }
    }

    internal sealed class RawRuleSet
    {
        public RawRule? Whole { get; set; }
        public RawRule? Masked { get; set; }
    }

    internal sealed class RawRule
    {
        public string? Regex { get; set; }
        public string? Number { get; set; }
        public bool IntsOnly { get; set; }
        public bool DecimalsOnly { get; set; }
        public double Max { get; set; }
        public bool RequireLetterAndDigit { get; set; }
        public string? ExcludeIfIn { get; set; }
        public bool PreferParenthesised { get; set; }
        public int MinLength { get; set; }
        public List<string>? Combine { get; set; }
    }

    internal sealed class RawDerived
    {
        public string? Name { get; set; }
        public string? Display { get; set; }
        public string? Kind { get; set; }
        public List<string>? From { get; set; }
        public string? Formula { get; set; }
        public double PerDozen { get; set; }
    }
}

/// <summary>一组表头标签。</summary>
/// <param name="Name">组名。</param>
/// <param name="Detect">命中条件（归一化文字里的子串；<c>A+B</c> 表示两者都要出现）。</param>
/// <param name="Labels">字段名 → 该字段的所有标签写法。</param>
/// <param name="Infer">无表头时，该组额外按列内容补字段的规则（按声明顺序执行）。</param>
internal sealed record FieldGroup(
    string Name,
    IReadOnlyList<string> Detect,
    IReadOnlyDictionary<string, List<string>> Labels,
    IReadOnlyList<InferRule> Infer);

/// <summary>无表头时的一条列内容推断规则。</summary>
/// <param name="Kind">规则种类：<c>multi</c>（多值列）/ <c>decimal</c>（小数列）/ <c>integerSlots</c>（整数列）/ <c>afterItem</c>（紧跟 item 之后）。</param>
/// <param name="Fields">按顺序要填的字段名。</param>
internal sealed record InferRule(string Kind, IReadOnlyList<string> Fields);

/// <summary>语义角色：算法里"认出某一类列"的地方从这里取字段名，避免把业务词写进代码。</summary>
internal sealed class RoleConfig
{
    /// <summary>item 角色字段（按列内容推断时最左优先）。</summary>
    public string? Item { get; set; } = "ITEM";

    /// <summary>size 角色字段。</summary>
    public string? Size { get; set; } = "SIZE";

    /// <summary>carton 类字段：表头标签可能偏到该列右侧，需要靠"内容像 size"来纠正归属；也是按键分组的键列候选。</summary>
    public List<string> CartonCandidates { get; set; } = ["CARTON", "CARTON_COUNT"];

    /// <summary>整数列按位置依次填的字段（配置档声明有哪些整数槽位）。</summary>
    public List<string> IntegerSlots { get; set; } = ["DOZENS", "PIECES"];

    /// <summary>price 角色字段。</summary>
    public string? Price { get; set; } = "UNIT_PRICE";

    /// <summary>amount 角色字段。</summary>
    public string? Amount { get; set; } = "AMOUNT";
}

/// <summary>一个语义字段的显示信息。</summary>
internal sealed record FieldSpec(string Name, string Display, bool Numeric);

/// <summary>派生列定义。</summary>
internal sealed record DerivedColumn(
    string Name,
    string Display,
    bool Numeric,
    IReadOnlyList<string> From,
    string Formula,
    double PerDozen);

/// <summary>校验规则。</summary>
internal sealed class CheckConfig
{
    public LineAmountCheck LineAmount { get; set; } = new();
    public DocumentTotalCheck DocumentTotal { get; set; } = new();
}

internal sealed class LineAmountCheck
{
    public bool Enabled { get; set; }

    public string Amount { get; set; } = "AMOUNT";

    public List<string> Quantity { get; set; } = ["QUANTITY"];

    public string Price { get; set; } = "UNIT_PRICE";

    public double ToleranceAbs { get; set; } = 0.02;

    public double ToleranceRatio { get; set; } = 0.005;

    /// <summary>对不上时是否按金额反算并**写回**数量列。会改动识别结果，默认关闭。</summary>
    public bool ReverseWriteBack { get; set; }

    public double PerDozen { get; set; } = 1;
}

internal sealed class DocumentTotalCheck
{
    public bool Enabled { get; set; } = true;

    public string Amount { get; set; } = "AMOUNT";

    public List<string> TotalKeywords { get; set; } = ["TOTAL", "合计"];

    public double ToleranceAbs { get; set; } = 0.05;

    public double ToleranceRatio { get; set; } = 0.01;
}

/// <summary>不依赖表头、只看内容的结构判定（通用档留空）。</summary>
internal sealed class ContentDetectionConfig
{
    public List<string> TitleKeywords { get; set; } = [];
    public List<ContentRule> Rules { get; set; } = [];
}

internal sealed class ContentRule
{
    public string? Name { get; set; }
    public string? Pattern { get; set; }
    public int MinMatchesPerRow { get; set; }
    public int MinRows { get; set; }
    public List<string>? RowTailPattern { get; set; }
    public string? Group { get; set; }

    /// <summary>计数前是否先把"区间写法"（如 22"-32"）剔除，只留独立记号。</summary>
    public bool ExcludeRanges { get; set; }
}

/// <summary>阈值。</summary>
internal sealed class ThresholdConfig
{
    public int MinHeaderLabels { get; set; } = 2;
    public double ItemColumnRatio { get; set; } = 0.4;
    public double SizeColumnRatio { get; set; } = 0.3;
    public int MinColumnsForInference { get; set; } = 3;

    /// <summary>一列里数字内容占比达到多少才算"数值列"。</summary>
    public double NumericColumnRatio { get; set; } = 0.6;

    /// <summary>数值列里"整数占比"达到多少算整数列，否则算小数列。</summary>
    public double IntegerColumnRatio { get; set; } = 0.8;

    /// <summary>小数列占比达到多少才认作小数列。</summary>
    public double DecimalColumnRatio { get; set; } = 0.6;

    /// <summary>一行含多个数字的占比达到多少才算"多值列"。</summary>
    public double MultiValueRatio { get; set; } = 0.5;

    /// <summary>「行里能抽出的字段数」达到多少才算这一行有结构（用于结构塌陷判定）。</summary>
    public int MinSemanticFieldsPerRow { get; set; } = 4;

    /// <summary>列间隙阈值 = 字高 × 该系数。<b>不要调大</b>：过大或按表格宽度百分比会把相邻字段粘成一列。</summary>
    public double GapFactor { get; set; } = 0.22;

    /// <summary>列间隙阈值的像素下限。</summary>
    public double MinGapPixels { get; set; } = 10;
}

/// <summary>功能开关。</summary>
internal sealed class FeatureConfig
{
    public bool MultiValueCells { get; set; } = true;
    public bool MergeAdjacentSameLabel { get; set; } = true;
    public bool RowLevelExtraction { get; set; } = true;

    /// <summary>派生列是否写进导出结果。通用档没有派生列，行业档自行打开。</summary>
    public bool DerivedColumnsInExport { get; set; }

    /// <summary>
    /// 列名是否改用**表头原文**（第二列起加序号 `(2)`/`(3)`），而不是配置里的字段显示名。
    ///
    /// 为什么要这个开关：表头一格可能**横跨多个物理列**（`TableField.Sub.Count &gt; 1`），
    /// 而配置没有为它声明语义子列时，旧行为是把父字段的显示名重复 N 遍 —— 通用档下就出现
    /// `描述|描述`。实测这两列的数据**并不相同**（左列是货号 `SO-4530K(7902)`、右列是尺码 `22"-32"`），
    /// 即"把尺码叫成描述"，既没有区分度也是错的。
    ///
    /// 通用档打开这条：通用档不猜业务含义，最诚实的做法就是把表头文字原样放上去（通用表兜底的口径）。
    /// 序号后缀不在 <c>Fields</c> 里，因此 <c>CanonicalId</c> 返回 null —— 这些列不会被当成语义字段，
    /// 不会参与派生列与算术校验。
    /// 行业档不开：它用 <c>expand</c> 声明语义子列（DESCRIPTION→货号+尺码、QUANTITY→打数+件数），
    /// 而且多出来的那一列常常是"只有货币符号"的列，会随后被当成全空列整列删除。
    /// </summary>
    public bool HeaderNamesForMultiColumn { get; set; }

    /// <summary>是否合并"内容互补"的相邻行（多值版式才需要）。</summary>
    public bool MergeComplementaryRows { get; set; }

    /// <summary>是否按键列把连续行合并成一行（多值版式才需要）。</summary>
    public bool GroupByKey { get; set; }

    /// <summary>
    /// 按键分组合并时，组内做"去重后空格拼接"的列（其余列取首个非空值）。
    /// 字段名用内部 ASCII 名；键列本身由 <see cref="RoleConfig.CartonCandidates"/> 决定。
    /// </summary>
    public List<string> GroupByKeyListFields { get; set; } = [];
}
