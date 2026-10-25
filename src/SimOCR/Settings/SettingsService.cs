using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimOCR.Diagnostics;

namespace SimOCR.Settings;

/// <summary>
/// 配置读写：便携优先 + 原子写 + 损坏自愈。
///
/// **0.10.5**：类注释过去写着"保存成功后触发 <c>Changed</c>，让热键/自启/托盘即时生效"，
/// 但那个事件**订阅者恒为 0** —— 这条推送机制从未接上，注释描述的机制是空头支票。
/// 真实机制是**拉模型**：热键/自启/托盘在需要时主动读 <see cref="Current"/>
/// （覆盖层在下一次截图、保存时在本次保存时读），因此删掉事件、把注释改成事实。
/// 需要"立即生效"的三项走的是组合根的专用路径（<c>ApplyHotkeyText</c> /
/// <c>ApplyEnabled</c> / <c>ApplyStartWithWindows</c>），不经过这里。
/// </summary>
internal sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 手工编辑配置时不区分大小写，省得用户踩坑
        PropertyNameCaseInsensitive = true,
    };

    private readonly LogService _log;
    private readonly string _configPath;
    private readonly object _gate = new();

    /// <summary>
    /// 自检期间不落盘。
    ///
    /// 问题（出口判据 ②「跑完整套自检后用户 `config.json` 逐字节不变」）：自检与验收为了
    /// "别往用户的下载目录写文件"会大量调用 <see cref="Update"/>（自检里一律传 <c>notify:false</c>），
    /// 而 <see cref="Update"/> **无条件** <see cref="Save"/> —— 也就是说跑一次自检就会把
    /// **用户的真实 `config.json` 重写一遍**（便携模式下就是 exe 同目录那份）。
    /// 内存里的改动照旧生效（自检本来也不需要它持久化：跑完即退出），只是**不写盘**。
    ///
    /// 为什么用"抑制写"而不是"写到另一个文件"：后者要改 <see cref="PathService"/> 的构造顺序
    /// （服务在自检模式判定之前就建好了，且 <c>_session</c> 持有同一个 <see cref="SettingsService"/> 实例），
    /// 那是一次跨组合根的重排；抑制写在**可观测结论**上完全等价 —— 用户文件逐字节不变 —— 而且零搬动。
    ///
    /// 置位点：<c>AppComposition.TryRunSelfTests</c> 判定"这是自检运行"之后（见那里的调用）。
    /// 只有自检 / 验收进程会置位；常驻运行**永不**置位。
    /// </summary>
    internal static bool WriteSuspended { get; set; }

    public SettingsService(PathService paths, LogService log)
    {
        _log = log;
        _configPath = paths.ConfigPath;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public string ConfigPath => _configPath;

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _log.Info($"未找到配置，使用默认值（将写入 {_configPath}）");
                return new AppSettings();
            }

            var json = File.ReadAllText(_configPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new InvalidDataException("配置反序列化结果为 null");
            }

            _log.Info($"配置已加载: {_configPath}");
            return Normalize(settings);
        }
        catch (Exception ex)
        {
            _log.Error($"配置读取失败，改用默认值: {_configPath}", ex);
            TryBackupCorrupt();
            return new AppSettings();
        }
    }

    /// <summary>补齐缺失/越界的字段。</summary>
    private static AppSettings Normalize(AppSettings s)
    {
        s.Hotkey ??= new HotkeySettings();
        s.Save ??= new SaveSettings();
        s.Overlay ??= new OverlaySettings();
        s.Pin ??= new PinSettings();
        s.Behavior ??= new BehaviorSettings();
        s.Appearance ??= new AppearanceSettings();
        s.Diagnostics ??= new DiagnosticsSettings();
        s.Ocr ??= new OcrSettings();
        s.Tables ??= new TableSettings();

        // 0.8.1 起新增的两个字段：老配置里没有它们，补成"按约定/内置档"，
        // 语义与升级前完全一致（模型仍从 models\v5-cn 找，档名仍是 default）。
        s.Tables.ProfilePath ??= string.Empty;
        s.Ocr.ModelDirectory ??= string.Empty;

        if (s.Hotkey.Presets is null || s.Hotkey.Presets.Count == 0)
        {
            s.Hotkey.Presets = ["F1", "Ctrl+Alt+A", "F4", "Ctrl+Shift+X"];
        }

        if (s.Overlay.MagnifierZoom is < 2 or > 32)
        {
            s.Overlay.MagnifierZoom = 8;
        }

        if (s.Overlay.MagnifierSampleSize is < 5 or > 51)
        {
            s.Overlay.MagnifierSampleSize = 15;
        }

        if (s.Overlay.MaskOpacity is < 0 or > 0.95)
        {
            s.Overlay.MaskOpacity = 0.4;
        }

        if (string.IsNullOrWhiteSpace(s.Save.FileNameTemplate))
        {
            s.Save.FileNameTemplate = "SimOCR_{yyyy}{MM}{dd}_{HH}{mm}{ss}_{fff}";
        }

        s.Pin.MinZoom = Math.Clamp(s.Pin.MinZoom <= 0 ? 0.1 : s.Pin.MinZoom, 0.05, 1.0);
        s.Pin.MaxZoom = Math.Clamp(s.Pin.MaxZoom <= 1 ? 4.0 : s.Pin.MaxZoom, 1.0, 8.0);
        s.Pin.DefaultZoom = Math.Clamp(s.Pin.DefaultZoom <= 0 ? 1.0 : s.Pin.DefaultZoom, s.Pin.MinZoom, s.Pin.MaxZoom);

        if (string.IsNullOrWhiteSpace(s.Overlay.DoubleClickAction))
        {
            s.Overlay.DoubleClickAction = "copy";
        }

        // 窗口吸附：老配置里没有这两个键 → 保持字段默认（开 / 8px）；
        // 显式写坏的值（负数、超范围）一律夹到 0~40 而不是丢回默认，
        // 因为 0 是"关闭吸附"的合法取值，用户就是想关的时候不该被改回 8。
        s.Behavior.SnapTolerancePx = Math.Clamp(s.Behavior.SnapTolerancePx, 0, 40);

        return s;
    }

    private void TryBackupCorrupt()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return;
            }

            var backup = Path.Combine(
                Path.GetDirectoryName(_configPath)!,
                $"config.corrupt.{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_configPath, backup, overwrite: true);
            _log.Warn($"已备份损坏配置到 {backup}");
        }
        catch (Exception ex)
        {
            _log.Warn("备份损坏配置失败", ex);
        }
    }

    /// <summary>
    /// 修改配置并保存。<paramref name="mutate"/> 在锁内执行。
    ///
    /// <paramref name="notify"/> 是 <c>Changed</c> 事件（0.10.5 已删）留下的**弱用途参数**：
    /// 该事件订阅者恒为 0 之后，两个取值的行为完全一样，还没有任何调用点依赖它的区别。
    /// 之所以这次不一起删：它有 18 个调用点（`notify: false`）分散在自检与验收代码里，
    /// 与并发进行的其它改动同处一片区域，删签名会把 diff 扩到与本任务无关的文件上。
    /// 按"行为等价优先"的原则保留，并明确记成后续清理项（不是忘了）。
    /// </summary>
    public void Update(Action<AppSettings> mutate, bool notify = true)
    {
        _ = notify; // 事件已删：两个取值行为等价（见上方说明）

        lock (_gate)
        {
            mutate(Current);
            Current = Normalize(Current);
            Save();
        }
    }

    private void Save()
    {
        if (WriteSuspended)
        {
            // 自检/验收进程里一律不写盘（用户 config.json 必须逐字节不变）。
            // 用 Debug 级别：自检会调 Update 几十次，逐条 Info 会把日志淹掉。
            _log.Debug($"[配置] 自检期间不写盘：跳过保存 {_configPath}");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Error($"配置写入失败: {_configPath}", ex);
        }
    }
}
