using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SimOCR.Diagnostics;
using SimOCR.Export;
using SimOCR.Interop;
using SimOCR.Ocr;
using SimOCR.Startup;
using SimOCR.Storage;

namespace SimOCR.Settings;

/// <summary>
/// 设置窗口：改动即时生效并落盘（<c>settings.Update</c> → 落盘；真正的生效方式见下）。
///
/// **0.10.5**：这里过去还有一个 <c>SettingsChanged</c> 事件，在每个设置变更后
/// raise 一次（共 18 处），但**订阅者恒为 0** —— 组合根从不订阅它。真实机制是各服务在需要时
/// **主动读** <c>settings.Current</c>（拉模型），而覆盖层/保存这类设置在下一次截图或下次保存时
/// 自然读到新值。既然"推送"这条机制从未接上，就删掉事件与 18 处空转 raise（而不是补一个
/// 只为了让它有订阅者而存在的消费者）。需要"立即生效"的三项走的是专用请求事件：
/// <see cref="HotkeyChangeRequested"/> / <see cref="EnabledChangeRequested"/> /
/// <see cref="StartWithWindowsChangeRequested"/>，它们都**真的有订阅者**。
/// </summary>
internal partial class SettingsWindow : Window
{
    /// <summary>「自定义 YAML…」这一项在下拉里的哨兵 Tag（真实档名不会长这样）。</summary>
    private const string CustomProfileTag = "__custom_yaml__";

    /// <summary>「自动（按约定）」这一项在下拉里的哨兵 Tag（目录名不会长这样）。</summary>
    private const string AutoModelTag = "__auto_model__";

    private readonly SettingsService _settings;
    private readonly AutoStartService _autoStart;
    private readonly PathService _paths;
    private readonly LogService _log;
    private readonly ImageStorageService _storage;

    /// <summary>本次运行真正加载的模型目录（改设置后要能说清"现在跑的还是旧的那份"）。</summary>
    private string _loadedModelDirectory = string.Empty;

    private bool _loading;

    public SettingsWindow(
        SettingsService settings,
        AutoStartService autoStart,
        PathService paths,
        LogService log,
        ImageStorageService storage)
    {
        _settings = settings;
        _autoStart = autoStart;
        _paths = paths;
        _log = log;
        _storage = storage;

        InitializeComponent();
        WireEvents();
        Loaded += (_, _) => LoadFromSettings();
    }

    /// <summary>XAML 里只挂了按钮的 Click，其余控件在这里统一接线，避免 XAML 与代码两处维护。</summary>
    private void WireEvents()
    {
        HotkeyCombo.SelectionChanged += OnHotkeyChanged;
        HotkeyCombo.LostFocus += OnHotkeyChanged;
        HotkeyCombo.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                OnHotkeyChanged(s, e);
            }
        };

        AutoSaveCheck.Checked += OnAutoSaveChanged;
        AutoSaveCheck.Unchecked += OnAutoSaveChanged;
        CloseAfterActionCheck.Checked += OnCloseAfterActionChanged;
        CloseAfterActionCheck.Unchecked += OnCloseAfterActionChanged;

        // 窗口吸附
        SnapToWindowsCheck.Checked += OnSnapToWindowsChanged;
        SnapToWindowsCheck.Unchecked += OnSnapToWindowsChanged;
        SnapToleranceCombo.SelectionChanged += OnSnapToleranceChanged;

        DoubleClickCombo.SelectionChanged += OnDoubleClickChanged;

        SaveDirBox.LostFocus += (_, _) => ApplySaveDirectory();
        FileNameTemplateBox.LostFocus += OnFileNameTemplateChanged;

        PlaySoundCheck.Checked += OnPlaySoundChanged;
        PlaySoundCheck.Unchecked += OnPlaySoundChanged;
        BalloonCheck.Checked += OnBalloonChanged;
        BalloonCheck.Unchecked += OnBalloonChanged;

        MagnifierCheck.Checked += OnMagnifierChanged;
        MagnifierCheck.Unchecked += OnMagnifierChanged;
        SizeLabelCheck.Checked += OnSizeLabelChanged;
        SizeLabelCheck.Unchecked += OnSizeLabelChanged;
        ResizeSelectionCheck.Checked += OnResizeSelectionChanged;
        ResizeSelectionCheck.Unchecked += OnResizeSelectionChanged;

        ZoomCombo.SelectionChanged += OnZoomChanged;
        SampleCombo.SelectionChanged += OnSampleChanged;

        EnabledCheck.Checked += OnEnabledChanged;
        EnabledCheck.Unchecked += OnEnabledChanged;
        StartWithWindowsCheck.Checked += OnStartWithWindowsChanged;
        StartWithWindowsCheck.Unchecked += OnStartWithWindowsChanged;
    }

    /// <summary>请求改热键（新的 "Ctrl+Alt+A" 形式字符串）。</summary>
    public event Action<string>? HotkeyChangeRequested;

    /// <summary>请求切换启用状态。</summary>
    public event Action<bool>? EnabledChangeRequested;

    /// <summary>请求切换开机自启。</summary>
    public event Action<bool>? StartWithWindowsChangeRequested;

    /// <summary>由组合根注入：尝试注册该热键，返回 null 表示成功，否则返回错误描述。</summary>
    public Func<string, string?>? HotkeyProbe { get; set; }

    // ---------------------------------------------------------------- 载入

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;

            HotkeyCombo.ItemsSource = s.Hotkey.Presets;
            HotkeyCombo.Text = s.Hotkey.ToGestureText();
            HotkeyStatusText.Text = $"当前生效：{HotkeyGesture.Parse(s.Hotkey.ToGestureText()).Display}";

            AutoSaveCheck.IsChecked = s.Save.AutoSaveAfterRelease;
            CloseAfterActionCheck.IsChecked = s.Save.CloseOverlayAfterAction;

            // 窗口吸附（老配置没有这两个键时就是这里的默认值：开 / 8px）
            SnapToWindowsCheck.IsChecked = s.Behavior.SnapToWindows;
            SelectComboByTag(SnapToleranceCombo, s.Behavior.SnapTolerancePx.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SnapToleranceCombo.IsEnabled = s.Behavior.SnapToWindows;

            DoubleClickCombo.SelectedIndex =
                string.Equals(s.Overlay.DoubleClickAction, "save", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            SaveDirBox.Text = _storage.ResolveSaveDirectory(s);
            SaveDirHint.Text = string.IsNullOrWhiteSpace(s.Save.Directory)
                ? "当前为系统「下载」文件夹；填写自定义目录可覆盖"
                : "自定义目录";

            FileNameTemplateBox.Text = s.Save.FileNameTemplate;
            PlaySoundCheck.IsChecked = s.Behavior.PlaySoundOnSave;
            BalloonCheck.IsChecked = s.Behavior.ShowTrayBalloon;

            MagnifierCheck.IsChecked = s.Overlay.ShowMagnifier;
            SizeLabelCheck.IsChecked = s.Overlay.ShowSizeLabel;
            ResizeSelectionCheck.IsChecked = s.Overlay.AllowResizeSelection;
            SelectComboByTag(ZoomCombo, s.Overlay.MagnifierZoom.ToString());
            SelectComboByTag(SampleCombo, s.Overlay.MagnifierSampleSize.ToString());

            EnabledCheck.IsChecked = s.Behavior.Enabled;

            // 表格解析配置档（0.8.1 起可在设置页切换；选中项以"当前生效"为准，可能是命令行指定的）
            LoadTableProfileCombo();
            RefreshTableProfileStatus();
            // 启动时因为"设置里那个档加载不上"已经回退过：把原因显出来，别让用户以为它在生效
            ShowTableProfileError(DescribeProfileFailure(s.Tables.Profile, s.Tables.ProfilePath));

            // OCR 参数（0.2.3 起可在设置页调整，改完立即生效）
            SelectComboByTag(
                TextScoreCombo,
                s.Ocr.TextScore.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture));
            SelectComboByTag(
                ImgResizeCombo,
                s.Ocr.ImgResize.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // OCR 模型目录（0.8.1 起可改；模型只在启动时加载一次，所以这里记下"实际加载的那份"）
            LoadOcrModelCombo();
            _loadedModelDirectory = OcrModelSet.Locate(s.Ocr.ModelDirectory, _log).Directory;
            RefreshOcrModelStatus(pendingRestart: false);

            // 自启以注册表实际状态为准
            var registered = _autoStart.IsEnabled();
            StartWithWindowsCheck.IsChecked = registered;
            AutoStartStatusText.Text = registered
                ? (_autoStart.IsRegisteredPathStale()
                    ? "注意：开机自启指向的路径与当前程序不一致（程序被移动过），保存设置时会自动修复。"
                    : "已注册开机自启（当前用户）")
                : "未注册开机自启";

            var version = typeof(SettingsWindow).Assembly.GetName().Version;
            var versionText = version is null ? "0.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
            FooterText.Text = "设置改动会立即保存";
            FooterText.ToolTip = _settings.ConfigPath;
            AboutText.Text =
                $"SimOCR v{versionText}\n" +
                $"数据目录：{_paths.DataDirectory}（{(_paths.IsPortable ? "便携模式" : "当前用户")}）\n" +
                $"日志目录：{_paths.LogDirectory}\n" +
                "许可证：Apache-2.0（第三方组件声明见下方「开源许可」）\n" +
                "所有识别都在本机完成，不联网、不上传。";
        }
        catch (Exception ex)
        {
            _log.Error("设置窗口载入失败", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem { Tag: string t } && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------- 热键

    private void OnHotkeyChanged(object sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var text = HotkeyCombo.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var gesture = HotkeyGesture.Parse(text);
        if (gesture.IsEmpty)
        {
            HotkeyStatusText.Text = $"无法识别：{text}（示例：F1、Ctrl+Alt+A、Ctrl+Shift+X）";
            return;
        }

        HotkeyStatusText.Text = $"正在应用：{gesture.Display} …";
        HotkeyChangeRequested?.Invoke(text);
    }

    private void OnCheckHotkeyClick(object sender, RoutedEventArgs e)
    {
        var text = HotkeyCombo.Text?.Trim() ?? string.Empty;
        var gesture = HotkeyGesture.Parse(text);
        if (gesture.IsEmpty)
        {
            HotkeyStatusText.Text = "请先选择一个有效的热键组合";
            return;
        }

        var probe = HotkeyProbe;
        if (probe is null)
        {
            HotkeyStatusText.Text = "检测不可用";
            return;
        }

        var error = probe(text);
        HotkeyStatusText.Text = error is null
            ? $"{gesture.Display} 可用，未被其他程序占用"
            : $"{gesture.Display} 不可用：{error}";
    }

    /// <summary>由组合根在热键应用后调用，回显结果。</summary>
    public void ReportHotkeyResult(string gestureText, string? error)
    {
        HotkeyStatusText.Text = error is null
            ? $"已生效：{gestureText}"
            : $"{gestureText} 注册失败：{error}（请换一个组合）";
    }

    // ---------------------------------------------------------------- 保存

    private void OnBrowseSaveDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = SaveDirBox.Text,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            SaveDirBox.Text = dialog.FolderName;
            ApplySaveDirectory();
        }
    }

    private void OnOpenSaveDirClick(object sender, RoutedEventArgs e)
    {
        OpenInExplorer(_storage.ResolveSaveDirectory());
    }

    private void ApplySaveDirectory()
    {
        if (_loading)
        {
            return;
        }

        var dir = SaveDirBox.Text?.Trim();
        var defaultDir = _storage.ResolveSaveDirectory(new AppSettings());

        _settings.Update(s => s.Save.Directory =
            string.IsNullOrWhiteSpace(dir) || string.Equals(dir, defaultDir, StringComparison.OrdinalIgnoreCase)
                ? null
                : dir);

        SaveDirHint.Text = string.IsNullOrWhiteSpace(_settings.Current.Save.Directory)
            ? "当前为系统「下载」文件夹；填写自定义目录可覆盖"
            : "自定义目录";

    }

    private void OnFileNameTemplateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var template = FileNameTemplateBox.Text?.Trim();
        _settings.Update(s => s.Save.FileNameTemplate = string.IsNullOrWhiteSpace(template)
            ? "SimOCR_{yyyy}{MM}{dd}_{HH}{mm}{ss}_{fff}"
            : template);

    }

    // ---------------------------------------------------------------- 开关

    private void OnAutoSaveChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = AutoSaveCheck.IsChecked == true;
        _settings.Update(s => s.Save.AutoSaveAfterRelease = value);
    }

    private void OnCloseAfterActionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = CloseAfterActionCheck.IsChecked == true;
        _settings.Update(s => s.Save.CloseOverlayAfterAction = value);
    }

    private void OnDoubleClickChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var tag = (DoubleClickCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "copy";
        _settings.Update(s => s.Overlay.DoubleClickAction = tag);
    }

    // ------------------------------------------------- 窗口吸附

    private void OnSnapToWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = SnapToWindowsCheck.IsChecked == true;
        _settings.Update(s => s.Behavior.SnapToWindows = value);
        SnapToleranceCombo.IsEnabled = value;
    }

    private void OnSnapToleranceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if ((SnapToleranceCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var tolerance))
        {
            _settings.Update(s => s.Behavior.SnapTolerancePx = Math.Clamp(tolerance, 0, 40));
        }
    }

    private void OnPlaySoundChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = PlaySoundCheck.IsChecked == true;
        _settings.Update(s => s.Behavior.PlaySoundOnSave = value);
    }

    private void OnBalloonChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = BalloonCheck.IsChecked == true;
        _settings.Update(s => s.Behavior.ShowTrayBalloon = value);
    }

    private void OnMagnifierChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = MagnifierCheck.IsChecked == true;
        _settings.Update(s => s.Overlay.ShowMagnifier = value);
    }

    private void OnSizeLabelChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = SizeLabelCheck.IsChecked == true;
        _settings.Update(s => s.Overlay.ShowSizeLabel = value);
    }

    private void OnResizeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = ResizeSelectionCheck.IsChecked == true;
        _settings.Update(s => s.Overlay.AllowResizeSelection = value);
    }

    private void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if ((ZoomCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var zoom))
        {
            _settings.Update(s => s.Overlay.MagnifierZoom = zoom);
        }
    }

    private void OnSampleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if ((SampleCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var size))
        {
            _settings.Update(s => s.Overlay.MagnifierSampleSize = size);
        }
    }

    private void OnTextScoreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if ((TextScoreCombo.SelectedItem as ComboBoxItem)?.Tag is string tag &&
            double.TryParse(
                tag,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var score))
        {
            _settings.Update(s => s.Ocr.TextScore = score);
        }
    }

    private void OnImgResizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if ((ImgResizeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var resize))
        {
            _settings.Update(s => s.Ocr.ImgResize = resize);
        }
    }

    // ---------------------------------------------------------------- 表格配置档

    /// <summary>
    /// 灌下拉：可用档名（= <c>Profiles/*.yaml</c> 的文件主名）+ 一项「自定义 YAML…」。
    /// 选中的是**当前生效**的那一档（<see cref="TableProfiles.Current"/> 的来源路径）而不是设置里的值 ——
    /// 命令行 <c>--profile=</c> 会盖过设置，照设置选会显示成"你没选的那个"。
    /// </summary>
    private void LoadTableProfileCombo()
    {
        TableProfileCombo.Items.Clear();
        foreach (var name in TableProfiles.AvailableProfiles())
        {
            TableProfileCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }

        TableProfileCombo.Items.Add(new ComboBoxItem { Content = "自定义 YAML…", Tag = CustomProfileTag });

        var effective = TableProfiles.Current.SourcePath;
        if (effective is null)
        {
            // 内置兜底：下拉里没有任何一项代表它，宁可不选，也不假装选了 default
            TableProfileCombo.SelectedIndex = -1;
        }
        else if (IsFromProfilesDirectory(effective))
        {
            SelectComboByTag(TableProfileCombo, Path.GetFileNameWithoutExtension(effective));
        }
        else
        {
            SelectComboByTag(TableProfileCombo, CustomProfileTag);
        }
    }

    /// <summary>这个 YAML 是不是随包的 <c>Profiles/</c> 里的（决定下拉该显示档名还是"自定义"）。</summary>
    private static bool IsFromProfilesDirectory(string path)
        => string.Equals(
            Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            TableProfile.ProfileDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个"想要的配置档"能不能真的加载？不能就给出要显示的错误文本，能就返回 null。
    ///
    /// 为什么要自己先探一次：<see cref="TableProfiles.Initialize"/> 失败时会**静默回退**到默认档，
    /// 状态行只会说"已加载…（default.yaml）"，用户看不出自己选的那档没生效 ——
    /// 这里按同一套查找规则（ProfilePath 优先于档名、档名在 Profiles 目录里找）先验一遍，
    /// 把"没加载上 + 原因"显式摆到界面上。
    /// </summary>
    private static string? DescribeProfileFailure(string? name, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return TableProfile.TryLoad(explicitPath!, out var pathError) is null
                ? $"指定的配置档无法加载：{pathError}"
                : null;
        }

        var wanted = name?.Trim();
        if (string.IsNullOrWhiteSpace(wanted) || string.Equals(wanted, "default", StringComparison.OrdinalIgnoreCase))
        {
            // 空/ default 就是要用默认档：它真加载不上时状态行自己会说"使用内置兜底"
            return null;
        }

        var candidate = Path.Combine(TableProfile.ProfileDirectory(), wanted + ".yaml");
        return TableProfile.TryLoad(candidate, out var nameError) is null
            ? $"未找到可用的配置档 Profiles/{wanted}.yaml（{nameError}），已回退"
            : null;
    }

    private void OnTableProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var tag = (TableProfileCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        if (tag == CustomProfileTag)
        {
            // 「自定义 YAML…」不是一个档名而是一个动作：立刻弹文件选择；取消就退回当前生效项
            if (!TryPickCustomProfile())
            {
                RestoreTableProfileSelection();
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            ApplyTableProfileName(tag!);
        }
    }

    private void OnBrowseProfileClick(object sender, RoutedEventArgs e) => TryPickCustomProfile();

    private void OnOpenProfileFolderClick(object sender, RoutedEventArgs e)
        => OpenInExplorer(TableProfile.ProfileDirectory());

    /// <summary>选中某个内置档名。档名与自定义路径互斥：选了档名就清掉路径。</summary>
    private void ApplyTableProfileName(string name)
    {
        _settings.Update(s =>
        {
            s.Tables.Profile = name;
            s.Tables.ProfilePath = string.Empty;
        });

        ReloadTableProfile(name, explicitPath: null);
    }

    /// <summary>「浏览…」选中的 YAML。档名清空（它不属于 Profiles 目录），路径记下来供下次启动用。</summary>
    private void ApplyTableProfilePath(string path)
    {
        _settings.Update(s =>
        {
            s.Tables.ProfilePath = path;
            s.Tables.Profile = string.Empty;
        });

        _loading = true;
        SelectComboByTag(TableProfileCombo, CustomProfileTag);
        _loading = false;

        ReloadTableProfile(name: null, explicitPath: path);
    }

    /// <summary>弹 YAML 文件选择。返回 true = 用户选好了并且已经应用。</summary>
    private bool TryPickCustomProfile()
    {
        // 这里用 OpenFileDialog（而不是 OpenFolderDialog）：要选的是单个 .yaml 文件。
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择表格配置档 YAML",
            Filter = "表格配置档 (*.yaml)|*.yaml|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = TableProfile.ProfileDirectory(),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        ApplyTableProfilePath(dialog.FileName);
        return true;
    }

    /// <summary>
    /// 真正切档并回显结果。切档只是换掉 <see cref="TableProfiles"/> 的静态状态，
    /// 之后的识别/导出取到新档，所以是"立即生效"（不需要重启）。
    /// </summary>
    private void ReloadTableProfile(string? name, string? explicitPath)
    {
        // 先探"能不能加载"（Initialize 之后 Current 已被换成回退结果，就查不出原因了）
        var error = DescribeProfileFailure(name, explicitPath);

        try
        {
            TableProfiles.Initialize(name, explicitPath);
        }
        catch (Exception ex)
        {
            _log.Error("切换表格配置档失败", ex);
            error ??= $"切换配置档失败：{ex.Message}";
        }

        RefreshTableProfileStatus();
        ShowTableProfileError(error);
    }

    private void RefreshTableProfileStatus()
    {
        TableProfileStatus.Text = TableProfiles.StatusMessage;
        _log.Info($"表格配置档：{TableProfiles.StatusMessage}");
    }

    /// <summary>错误行必须显著：坏 YAML 静默回退会让人以为配置生效了。</summary>
    private void ShowTableProfileError(string? error)
    {
        TableProfileError.Text = error ?? string.Empty;
        TableProfileError.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(error))
        {
            _log.Warn($"表格配置档回退：{error}");
        }
    }

    private void RestoreTableProfileSelection()
    {
        _loading = true;
        LoadTableProfileCombo();
        _loading = false;
    }

    // ---------------------------------------------------------------- OCR 模型目录

    /// <summary>
    /// 灌下拉：一项「自动（按约定）」+ <c>models\</c> 下真实存在的子目录（缺文件的标出来）。
    /// 设置值不在候选里时（「浏览…」选的别处目录）补一项，否则下拉框里看不到自己选了什么。
    /// </summary>
    private void LoadOcrModelCombo()
    {
        var configured = _settings.Current.Ocr.ModelDirectory?.Trim() ?? string.Empty;
        var available = OcrModelSet.AvailableDirectories();

        OcrModelCombo.Items.Clear();
        OcrModelCombo.Items.Add(new ComboBoxItem
        {
            // 下拉项是给用户读的："按约定 models\v5-cn" 是加载器内部的约定，写在这里没人看得懂。
            Content = "自动（使用程序自带的模型）",
            Tag = AutoModelTag,
        });

        foreach (var name in available)
        {
            var complete = OcrModelSet.IsCompleteDirectory(OcrModelSet.ResolveConfiguredDirectory(name));
            OcrModelCombo.Items.Add(new ComboBoxItem
            {
                Content = complete ? name : $"{name}（缺模型文件）",
                Tag = name,
            });
        }

        if (!string.IsNullOrWhiteSpace(configured) &&
            !available.Any(name => string.Equals(name, configured, StringComparison.OrdinalIgnoreCase)))
        {
            // 长路径塞进下拉项会被截断成看不出是什么，所以只显示文件夹名；完整路径放 ToolTip。
            var folderName = Path.GetFileName(
                configured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            OcrModelCombo.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrEmpty(folderName) ? "自定义…" : $"自定义：{folderName}",
                Tag = configured,
                ToolTip = configured,
            });
        }

        SelectComboByTag(OcrModelCombo, string.IsNullOrWhiteSpace(configured) ? AutoModelTag : configured);
    }

    private void OnOcrModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if ((OcrModelCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag)
        {
            return;
        }

        // 「自动」= 存空值（按约定 models\v5-cn）；其余存目录名或绝对路径
        ApplyOcrModelDirectory(tag == AutoModelTag ? string.Empty : tag);
    }

    private void OnBrowseOcrModelClick(object sender, RoutedEventArgs e)
    {
        // .NET 8+ 的 WPF 自带 OpenFolderDialog（「保存目录」那一行用的就是它），
        // 不必再用 OpenFileDialog + ValidateNames=false/CheckFileExists=false 的老办法。
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            // 标题只说"选哪个文件夹"；"里面该有什么文件"属于安装包的事，写进下面的灰字说明。
            Title = "选择 OCR 模型文件夹",
            InitialDirectory = OcrModelSet.ModelsRoot(),
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ApplyOcrModelDirectory(dialog.FolderName);
    }

    private void OnOpenOcrModelFolderClick(object sender, RoutedEventArgs e)
        => OpenInExplorer(OcrModelSet.Locate(_settings.Current.Ocr.ModelDirectory).Directory);

    /// <summary>
    /// 记录模型目录并回显"下次启动会用哪个"。
    /// 这里**只改设置**，不碰正在跑的识别引擎（ONNX session 是按启动时定位的路径加载的），
    /// 所以识别过程中改它不会崩；但也确实要重启才生效 —— 状态行会把这一点写出来，不假装是实时的。
    /// </summary>
    private void ApplyOcrModelDirectory(string directory)
    {
        _settings.Update(s => s.Ocr.ModelDirectory = directory);

        _loading = true;
        // 新值可能不在候选里（浏览选的），重建一次下拉让当前项显示真实值
        LoadOcrModelCombo();
        _loading = false;

        RefreshOcrModelStatus(pendingRestart: true);
    }

    /// <summary>
    /// 模型状态行：**有效**目录 + 来源（来自设置 / 程序自带）+ 是否要重启才生效；
    /// 设置值被回退时另起一行红字说明原因。
    /// </summary>
    private void RefreshOcrModelStatus(bool pendingRestart)
    {
        var effective = OcrModelSet.Locate(_settings.Current.Ocr.ModelDirectory, _log);
        var text = effective.IsComplete
            ? $"识别模型：PP-OCRv5 中文（{effective.SourceDescription}）"
            : effective.Describe();

        var restartNeeded = pendingRestart &&
                            !string.Equals(effective.Directory, _loadedModelDirectory, StringComparison.OrdinalIgnoreCase);
        OcrModelStatus.Text = restartNeeded
            ? $"{text}（本次运行仍用之前的模型，重启后生效）"
            : text;
        OcrModelStatus.ToolTip = effective.Directory;

        OcrModelError.Text = effective.FallbackReason ?? string.Empty;
        OcrModelError.Visibility = string.IsNullOrEmpty(effective.FallbackReason)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)    {
        if (_loading)
        {
            return;
        }

        var value = EnabledCheck.IsChecked == true;
        EnabledChangeRequested?.Invoke(value);
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = StartWithWindowsCheck.IsChecked == true;
        StartWithWindowsChangeRequested?.Invoke(value);
    }

    /// <summary>由组合根回写启用状态（例如从托盘切换后同步界面）。</summary>
    public void SyncEnabled(bool enabled)
    {
        _loading = true;
        EnabledCheck.IsChecked = enabled;
        _loading = false;
    }

    /// <summary>由组合根回写自启状态与提示文本。</summary>
    public void SyncAutoStart(bool enabled, string statusText)
    {
        _loading = true;
        StartWithWindowsCheck.IsChecked = enabled;
        AutoStartStatusText.Text = statusText;
        _loading = false;
    }

    // ---------------------------------------------------------------- 其他

    private void OnOpenDataDirClick(object sender, RoutedEventArgs e) => OpenInExplorer(_paths.DataDirectory);

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e) => OpenInExplorer(_paths.LogDirectory);

    /// <summary>打开只读的许可证窗（Apache-2.0 全文 + 第三方组件声明），内容来自程序集内的嵌入资源。</summary>
    private void OnShowLicenseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            new LicenseWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.Warn("打开开源许可窗口失败", ex);
            MessageBox.Show(this, $"打开开源许可失败：{ex.Message}", "SimOCR", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"打开目录失败: {path}", ex);
            MessageBox.Show(this, $"打开目录失败：{ex.Message}", "SimOCR", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
