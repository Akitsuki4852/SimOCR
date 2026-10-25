using SimOCR.Interop;

namespace SimOCR.Settings;

/// <summary>
/// 应用配置模型（便携模式下存为 exe 同目录的 config.json）。
/// 读取时缺失字段用这里的默认值补齐。
/// </summary>
internal sealed class AppSettings
{
    public int Version { get; set; } = 2;

    public HotkeySettings Hotkey { get; set; } = new();

    public SaveSettings Save { get; set; } = new();

    public OverlaySettings Overlay { get; set; } = new();

    public PinSettings Pin { get; set; } = new();

    public BehaviorSettings Behavior { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    public OcrSettings Ocr { get; set; } = new();

    public TableSettings Tables { get; set; } = new();
}

/// <summary>
/// 表格解析配置档。配置本身放在 <c>Profiles/*.yaml</c>（随程序分发），
/// 这里只记"用哪一档"，所以加档位不需要改代码、也不用升级程序。
/// </summary>
internal sealed class TableSettings
{
    /// <summary>配置档名（= Profiles/&lt;名字&gt;.yaml 的**文件主名**）；留空表示用 default。</summary>
    public string Profile { get; set; } = "default";

    /// <summary>
    /// 自定义配置档 YAML 的完整路径（设置页「浏览…」选的）。
    /// 为什么和 <see cref="Profile"/> 并存而不是二选一：档名只能指到随包的 <c>Profiles/</c> 目录，
    /// 用户自备的 YAML 常放在别处（共享盘、桌面……）。两者语义上互斥 —— 选了内置档名就把这里清空，
    /// 选了自定义文件就把档名清空，免得"到底用了哪一档"说不清。
    /// </summary>
    public string ProfilePath { get; set; } = string.Empty;
}

internal sealed class HotkeySettings
{
    public string Modifiers { get; set; } = "None";

    public string Key { get; set; } = "F1";

    public List<string> Presets { get; set; } = ["F1", "Ctrl+Alt+A", "F4", "Ctrl+Shift+X"];

    /// <summary>组合成 HotkeyGesture.Parse 能吃的字符串。</summary>
    public string ToGestureText()
        => string.Equals(Modifiers, "None", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(Modifiers)
            ? Key
            : $"{Modifiers}+{Key}";

    public void FromGestureText(string text)
    {
        // 统一规范化成 "Ctrl+Alt" + "A"，避免手工写成 "Control, Alt" 之类导致解析偏差
        var gesture = HotkeyGesture.Parse(text);
        if (gesture.IsEmpty)
        {
            return;
        }

        var (modifiers, key) = gesture.ToSettingsParts();
        Modifiers = modifiers;
        Key = key;
    }
}

internal sealed class SaveSettings
{
    /// <summary>null = 使用系统"下载"已知文件夹。</summary>
    public string? Directory { get; set; }

    public string FileNameTemplate { get; set; } = "SimOCR_{yyyy}{MM}{dd}_{HH}{mm}{ss}_{fff}";

    public string Format { get; set; } = "Png";

    /// <summary>松开左键后是否直接保存（跳过工具条）。</summary>
    public bool AutoSaveAfterRelease { get; set; }

    /// <summary>保存/复制/置顶后是否关闭覆盖层（默认：是）。</summary>
    public bool CloseOverlayAfterAction { get; set; } = true;
}

internal sealed class OverlaySettings
{
    public bool ShowMagnifier { get; set; } = true;

    public int MagnifierZoom { get; set; } = 8;

    public int MagnifierSampleSize { get; set; } = 15;

    public bool ShowSizeLabel { get; set; } = true;

    public double MaskOpacity { get; set; } = 0.4;

    public bool AllowResizeSelection { get; set; } = true;

    public List<string> ToolbarButtons { get; set; } = ["cancel", "pin", "save", "copy"];

    /// <summary>双击选区时执行的动作："copy"（默认）或 "save"。</summary>
    public string DoubleClickAction { get; set; } = "copy";
}

internal sealed class PinSettings
{
    public double DefaultZoom { get; set; } = 1.0;

    public double MinZoom { get; set; } = 0.1;

    public double MaxZoom { get; set; } = 4.0;

    public bool CloseOnEscape { get; set; } = true;
}

internal sealed class BehaviorSettings
{
    /// <summary>启用状态：暂停时不响应全局热键。</summary>
    public bool Enabled { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool SingleInstanceActivateAndCapture { get; set; } = true;

    public bool PlaySoundOnSave { get; set; }

    public bool ShowTrayBalloon { get; set; } = true;

    public bool HideOnCapture { get; set; } = true;

    /// <summary>
    /// 窗口吸附：框选时把边吸附到附近窗口/显示器/屏幕边缘，悬停高亮并可单击抓取整个窗口。
    /// 老 config.json 没有这个键 → 走这里的默认值（开）。
    /// </summary>
    public bool SnapToWindows { get; set; } = true;

    /// <summary>吸附容差（物理像素，0 = 关闭；SettingsService 夹到 0~40）。</summary>
    public int SnapTolerancePx { get; set; } = 8;
}

internal sealed class AppearanceSettings
{
    /// <summary>System / Light / Dark</summary>
    public string Theme { get; set; } = "System";
}

internal sealed class DiagnosticsSettings
{
    public string LogLevel { get; set; } = "Information";

    public int KeepLogDays { get; set; } = 7;
}

/// <summary>
/// OCR 参数（V1 起可在设置页调整，改完**立即生效**，不需要重启）。
/// 取值依据：同一批内部样本上"行数召回 + 文本一致率"两个指标的实测对比
/// （结论保留在这两个字段的注释里，删掉了指向仓库外报告的路径）。
/// </summary>
internal sealed class OcrSettings
{
    /// <summary>
    /// 识别置信度阈值（RapidOcrNet 的 TextScore）：
    /// 越低保留越多低置信行（召回高、可能带噪），越高越严格（准确高、可能漏）。
    /// 实测 0.3 在真实表格上召回已经很高；0 还能再多约 17 行，但噪声明显。
    /// </summary>
    public double TextScore { get; set; } = 0.3;

    /// <summary>检测阶段输入的最长边上限（越大越利于"大图里的小字"，也越慢）。</summary>
    public int ImgResize { get; set; } = 2000;

    /// <summary>
    /// OCR 模型目录。
    /// 空 = 按约定用 exe 同目录的 <c>models\v5-cn</c>（随包那份，与旧行为一致）；
    /// 相对路径按 <c>models\</c> 解析（下拉里选的是目录名），绝对路径直接当目录用（「浏览…」选的）。
    /// 目录不存在或缺 det.onnx 时**自动回退到约定目录**并写一条警告日志，绝不静默用一个坏目录。
    /// </summary>
    /// <remarks>
    /// 改这里**需要重启**才会重新加载：模型路径在启动时定位一次，
    /// 识别引擎（ONNX session）也是按那次定位懒加载的，改设置不动正在跑的引擎。
    /// </remarks>
    public string ModelDirectory { get; set; } = string.Empty;
}
