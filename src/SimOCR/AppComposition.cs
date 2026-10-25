using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using SimOCR.Capture;
using SimOCR.Diagnostics;
using SimOCR.Export;
using SimOCR.Interop;
using SimOCR.Ocr;
using SimOCR.Overlay;
using SimOCR.Pinning;
using SimOCR.Settings;
using SimOCR.Startup;
using SimOCR.Storage;
using SimOCR.Tray;

namespace SimOCR;

/// <summary>
/// 组合根：装配所有服务、注册热键、建立托盘、挂全局异常兜底。
/// 生命周期：App.OnStartup → Start()；App.OnExit → Shutdown()。
/// </summary>
internal sealed class AppComposition
{
    private App? _app;
    private PathService? _paths;
    private LogService? _log;
    private SettingsService? _settings;
    private AutoStartService? _autoStart;
    private SingleInstanceGuard? _singleInstance;
    private HotkeyWindow? _hotkeyWindow;
    private TrayService? _tray;
    private ScreenCaptureService? _capture;
    private ImageStorageService? _storage;
    private RecognitionService? _recognition;
    private ExportService? _exporter;
    private PinnedImageService? _pinning;
    private CaptureSessionController? _session;
    private SettingsWindow? _settingsWindow;

    public static AppComposition Current { get; } = new();

    public void Start(App app, string[] args)
    {
        _app = app;

        // 1) 路径 + 日志（先起来，后面任何一步出错都能留下痕迹）
        _paths = PathService.Create();
        _log = new LogService(_paths);
        _log.Info("──────────────────────────────────────────────");
        _log.Info($"SimOCR 启动 v{typeof(AppComposition).Assembly.GetName().Version} pid={Environment.ProcessId}");
        _log.Info($"OS={Environment.OSVersion.VersionString} x64={Environment.Is64BitProcess} " +
                  $"CLR={Environment.Version} 便携模式={_paths.IsPortable} 数据目录={_paths.DataDirectory}");

        InstallGlobalExceptionHandlers(app);

        // 1.5) 剪贴板读取子进程模式：必须在"单实例判定"之前处理，
        //      否则子进程会把自己当成第二个实例、把命令转发给父进程而不去读剪贴板。
        if (TryRunClipboardReader(args, out var readerExitCode))
        {
            app.Shutdown(readerExitCode);
            return;
        }

        // 2) 配置
        _settings = new SettingsService(_paths, _log);
        _log.MinLevel = ParseLogLevel(_settings.Current.Diagnostics.LogLevel);
        _autoStart = new AutoStartService(_log);

        // 2.5) 表格解析配置档：必须在任何解析动作之前加载。
        //      优先级：命令行 --profile=<文件> > 设置里的档名 > Profiles/default.yaml > 内置兜底。
        InitializeTableProfile(args);

        // 3) 业务服务
        _storage = new ImageStorageService(_settings, _log);
        _pinning = new PinnedImageService(_settings, _storage, _log);
        _capture = new ScreenCaptureService(_log);
        _session = new CaptureSessionController(_log, _settings, _capture, _storage, _pinning);
        _session.Notify += (message, isError) => _tray?.ShowBalloon(message, isError);
        _session.OcrRequested += OnOcrRequested;
        _session.LastSelectionChanged += _ => _tray?.UpdateLastCaptureAvailable(true);

        // V1：本地 OCR（PP-OCRv5 中文模型随包）+ TXT/DOCX/XLSX/PDF 四种导出
        _recognition = new RecognitionService(_log, _settings);
        _exporter = new ExportService(_settings, _storage, _log);
        _pinning.Notify += message => _tray?.ShowBalloon(message);

        LogEnvironment();

        // 4) 自检模式（开发/验收用）：跑完即退出，不建托盘、不注册热键。
        //    **必须排在单实例判定之前**：否则当用户已经运行着 SimOCR 时，自检会退化成
        //    "给首实例发一条 capture" —— 既跑不了自检，又会在用户屏幕上弹出覆盖层打扰他。
        if (TryRunSelfTests(args, out var exitCode))
        {
            app.Shutdown(exitCode);
            return;
        }

        // 5) 单实例：非首实例把命令交给首实例后退出
        _singleInstance = new SingleInstanceGuard(_log);
        if (!_singleInstance.IsFirstInstance)
        {
            _log.Info("检测到已有实例，转发命令并退出");
            var ok = SingleInstanceGuard.TrySend("capture");
            if (!ok)
            {
                _log.Warn("转发命令失败（首实例可能正在关闭）");
            }

            app.Shutdown(ok ? 0 : 2);
            return;
        }

        // 6) 托盘（先建托盘，后面热键注册失败才有地方提示）
        _tray = new TrayService(_settings, _log);
        _tray.CaptureRequested += BeginCapture;
        _tray.OcrClipboardRequested += () =>
        {
            if (!TryOcrClipboard())
            {
                _tray.ShowBalloon("剪贴板里没有图片（可先截图或复制一张图片再试）", true);
            }
        };
        _tray.OcrLastCaptureRequested += () =>
        {
            if (!TryOcrLastCapture())
            {
                _tray.ShowBalloon("本次运行还没有截图", true);
            }
        };
        _tray.SettingsRequested += OpenSettings;
        _tray.OpenSaveFolderRequested += OpenSaveFolder;
        _tray.EnabledToggleRequested += ToggleEnabled;
        _tray.ClosePinsRequested += () => _pinning.CloseAll();
        _tray.ExitRequested += ExitApplication;
        _tray.HotkeyPresetSelected += ApplyHotkeyText;
        _pinning.CountChanged += count => _tray.UpdatePinCount(count);

        // 6) 全局热键
        _hotkeyWindow = new HotkeyWindow(_log);
        _hotkeyWindow.Pressed += BeginCapture;
        // 这里不订阅"显示器变化"事件：目前没有任何一处会因它重读 MonitorLayout、重排贴图或处理
        // DpiChanged，订阅了也只会打一条日志，反而让人以为显示器变化已经处理好了。
        // 真要处理（重读布局 + 重排贴图 + DpiChanged）是一次行为变更，留待以后再说。
        _hotkeyWindow.PowerResumed += OnPowerResumed;

        if (_settings.Current.Behavior.Enabled)
        {
            RegisterHotkey(reportToWindow: false);
        }
        else
        {
            _log.Info("当前为暂停状态，不注册全局热键");
            _tray.UpdateEnabledState(false);
        }

        // 6.5) 覆盖层预热：把"首次 F1 才付"的一次性代价提前到启动空闲时。
        //      首次截图的黑屏一瞬主要来自 BAML 解析 / JIT / D3D 初始化 / 大图上传，这里用
        //      "抓一帧 + 造一个 Opacity=0、不抢焦点的覆盖层并立刻关闭"把这些代价跑掉。
        //      诊断开关：设 SIMOCR_NO_WARMUP=1 可跳过（冷/热对比测量用）。
        if (!string.Equals(Environment.GetEnvironmentVariable("SIMOCR_NO_WARMUP"), "1", StringComparison.Ordinal))
        {
            app.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(WarmUpOverlay));
        }
        else
        {
            // 0.10.9：这个诊断开关过去静默生效，日志里看不出"预热到底跑没跑"。
            // 只补一条日志（不新增 CLI、不改行为）。
            _log.Info("SIMOCR_NO_WARMUP=1：已跳过覆盖层预热（诊断开关）");
        }

        // 7) 单实例命令监听
        _singleInstance.CommandReceived += command =>
        {
            if (string.Equals(command, "capture", StringComparison.OrdinalIgnoreCase) &&
                _settings.Current.Behavior.SingleInstanceActivateAndCapture)
            {
                app.Dispatcher.BeginInvoke(new Action(BeginCapture));
            }
        };
        _singleInstance.StartServer();

        // 8) 开机自启路径校验
        if (_autoStart.IsEnabled() && _autoStart.IsRegisteredPathStale())
        {
            _log.Warn("开机自启的 exe 路径已失效（exe 被移动或改名）");
            _tray.ShowBalloon("开机自启的路径已失效，请到设置里重新保存一次", true);
        }

        // 9) 启动参数
        if (args.Any(a => a.Equals("--capture", StringComparison.OrdinalIgnoreCase)))
        {
            app.Dispatcher.BeginInvoke(new Action(BeginCapture));
        }

        if (args.Any(a => a.Equals("--bootstrap-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _log.Info("自检模式：启动成功，立即退出");
            app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown(0)));
        }
    }

    // ---------------------------------------------------------------- 自检

    /// <summary>--read-clipboard &lt;报告文件&gt;：用 WinForms API 读剪贴板并写报告（跨进程验证用）。</summary>
    private static bool TryRunClipboardReader(string[] args, out int exitCode)
    {
        exitCode = 0;

        var index = Array.FindIndex(args, a => a.Equals("--read-clipboard", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            exitCode = 2;
            return true;
        }

        exitCode = SelfTest.RunReadClipboard(args[index + 1]);
        return true;
    }

    /// <summary>
    /// 命令行自检：--selftest-capture[=N]（连拍压力）、--selftest-clipboard（剪贴板往返 + 跨进程）、
    /// --selftest-clipboard-hold[=N]（复制后保持存活，供外部程序验证粘贴）、
    /// --selftest-overlay（覆盖层端到端）。
    /// 返回 true 表示这是自检运行，调用方应立即按 <paramref name="exitCode"/> 退出。
    /// </summary>
    private bool TryRunSelfTests(string[] args, out int exitCode)
    {
        exitCode = 0;

        var stressArg = args.FirstOrDefault(a => a.StartsWith("--selftest-capture", StringComparison.OrdinalIgnoreCase));
        var clipboard = args.Any(a => a.Equals("--selftest-clipboard", StringComparison.OrdinalIgnoreCase));
        var overlay = args.Any(a => a.Equals("--selftest-overlay", StringComparison.OrdinalIgnoreCase));
        var holdArg = args.FirstOrDefault(a => a.StartsWith("--selftest-clipboard-hold", StringComparison.OrdinalIgnoreCase));
        var visualHoldArg = args.FirstOrDefault(a => a.StartsWith("--selftest-overlay-hold", StringComparison.OrdinalIgnoreCase));
        var ocrArg = args.FirstOrDefault(a => a.StartsWith("--selftest-export", StringComparison.OrdinalIgnoreCase))
                     ?? args.FirstOrDefault(a => a.StartsWith("--selftest-ocr", StringComparison.OrdinalIgnoreCase));
        var overlayOcr = args.Any(a => a.Equals("--selftest-overlay-ocr", StringComparison.OrdinalIgnoreCase));
        var profileSelfTest = args.Any(a => a.Equals("--selftest-profile", StringComparison.OrdinalIgnoreCase));
        var lineGridArg = args.FirstOrDefault(a => a.StartsWith("--selftest-linegrid", StringComparison.OrdinalIgnoreCase));
        var acceptArg = args.FirstOrDefault(a => a.StartsWith("--accept-table", StringComparison.OrdinalIgnoreCase));
        var acceptEnhanced = args.Any(a => a.Equals("--accept-enhance", StringComparison.OrdinalIgnoreCase));
        var acceptTxt = args.Any(a => a.Equals("--accept-txt", StringComparison.OrdinalIgnoreCase));

        // 窗口吸附
        var snapSelfTest = args.Any(a => a.Equals("--selftest-snap", StringComparison.OrdinalIgnoreCase));
        var snapAcceptance = args.Any(a => a.Equals("--accept-snap", StringComparison.OrdinalIgnoreCase));
        var snapHostArg = args.FirstOrDefault(a => a.StartsWith("--snap-host-window", StringComparison.OrdinalIgnoreCase));
        var snapOcclusionArg = args.FirstOrDefault(a => a.StartsWith("--snap-probe-occlusion", StringComparison.OrdinalIgnoreCase));

        // --accept-enhance / --accept-txt 是 --accept-table 的**修饰参数**。
        // 单独给修饰参数时过去会被静默忽略：门禁判"不是自检模式"→ 应用**照常驻留启动**
        // （托盘 + F1 热键，进程永不退出），用户以为跑完验收了。现在报用法错误并非 0 退出。
        var bareModifier = acceptArg is null && (acceptEnhanced || acceptTxt);
        if (bareModifier)
        {
            var names = acceptEnhanced && acceptTxt ? "--accept-enhance 与 --accept-txt"
                : acceptEnhanced ? "--accept-enhance"
                : "--accept-txt";
            var primary = acceptEnhanced ? "--accept-enhance" : "--accept-txt";
            _log?.Error($"  [用法] {names} 是 --accept-table 的修饰参数，不能单独使用：" +
                        $"请写成 --accept-table=<图片1>[;<图片2>…] {primary}");
            exitCode = 2;
            return true;
        }

        // 修饰参数也算"自检意图"：单独出现时在上面的用法错误里退出，不会掉进下面的"正常启动"分支。
        if (stressArg is null && !clipboard && !overlay && holdArg is null && visualHoldArg is null &&
            ocrArg is null && !overlayOcr && !profileSelfTest && lineGridArg is null && acceptArg is null &&
            !acceptEnhanced && !acceptTxt &&
            !snapSelfTest && !snapAcceptance && snapHostArg is null && snapOcclusionArg is null)
        {
            return false;
        }

        var iterations = 200;
        var parts = stressArg?.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts is { Length: 2 } && int.TryParse(parts[1], out var requested) && requested > 0)
        {
            iterations = Math.Min(requested, 5000);
        }

        // 从这一刻起确认"这是自检运行"→ 抑制配置落盘。
        // 自检为了"别往用户下载目录写文件"会大量调用 `SettingsService.Update`，而它**无条件落盘** ——
        // 也就是说跑一次自检就会把用户的真实 config.json 重写一遍，而出口判据 ② 要的是"逐字节不变"。
        // 抑制写在**可观测结论**上完全等价（内存里的改动照旧生效，自检跑完即退出、本来也不需要持久化），
        // 且不需要重排组合根（服务在自检模式判定之前就建好了，`_session` 还持有同一个 SettingsService 实例）。
        // 放在这里而不是方法开头：上面两条 return 都不代表自检运行，不能把常驻运行的写盘也抑制掉。
        // 诊断逃生口 `SIMOCR_SELFTEST_WRITES_CONFIG=1`：为这条抑制本身留一个**反证开关** ——
        // 设上它就照旧落盘，用来证明"文件不变"确实来自这条抑制，而不是"自检本来就不写"。
        SettingsService.WriteSuspended =
            !string.Equals(Environment.GetEnvironmentVariable("SIMOCR_SELFTEST_WRITES_CONFIG"), "1", StringComparison.Ordinal);

        try
        {
            if (stressArg is not null && _capture is not null && _storage is not null && _log is not null)
            {
                exitCode = CombineExitCode(exitCode, SelfTest.RunCaptureStress(_capture, _storage, _log, iterations));
            }

            if (clipboard && _log is not null)
            {
                exitCode = CombineExitCode(exitCode, SelfTest.RunClipboardRoundTrip(_log));
            }

            if (lineGridArg is not null && _recognition is not null && _log is not null)
            {
                exitCode = CombineExitCode(exitCode, SelfTest.RunLineGrid(lineGridArg, _recognition, _log));
            }

            // **0.10.7 起：`--selftest-profile` 退役。**
            //
            // 它过去跑的是配置档单一来源那条纯逻辑断言（ProfileSourceChecks）。0.10.7 把那份源码
            // **彻底移出发布产物**（见 SimOCR.csproj 的说明）：本程序集里已经没有任何一份实现，
            // 保留"能跑"只可能是把断言再抄一遍进产品 —— 那正是要清掉的东西。
            //
            // 判据没有丢：断言本体一个字没改，只换执行路径 ——
            //   `SimOCR.Tests.exe --case=profile-source`（门禁用例 `profile-source` 钉着它；
            //    同一个 `--case=all` 用例继续钉 4 条用例 / 47 条断言）。
            //
            // 为什么这里**保留对这个开关的识别**、而不是让参数落空：本方法的返回值决定
            // "算不算自检模式"（返回 false → 应用按**常驻托盘**启动、永不退出）。旧脚本里还写着
            // `--selftest-profile` 时，静默忽略就是"命令不退出、以为跑完了"——比报错糟得多。
            // 所以按既有口径报**用法错误**(2)：不假装通过，也不假装"环境不支持"(5)。
            if (profileSelfTest)
            {
                _log?.Error("  [用法] --selftest-profile 已退役：该断言已移出发布产物，" +
                            "请改用控制台运行器：tests\\SimOCR.Tests\\bin\\<配置>\\net10.0-windows\\SimOCR.Tests.exe --case=profile-source");
                exitCode = 2;
                return true;
            }

            if (holdArg is not null && _log is not null)
            {
                var seconds = 30;
                var holdParts = holdArg.Split('=', 2, StringSplitOptions.TrimEntries);
                if (holdParts is { Length: 2 } && int.TryParse(holdParts[1], out var holdSeconds) && holdSeconds > 0)
                {
                    seconds = Math.Min(holdSeconds, 600);
                }

                exitCode = CombineExitCode(exitCode, SelfTest.RunClipboardHold(_log, seconds));
            }

            if (visualHoldArg is not null && _session is not null && _log is not null)
            {
                var visualSeconds = 6;
                var visualParts = visualHoldArg.Split('=', 2, StringSplitOptions.TrimEntries);
                if (visualParts is { Length: 2 } && int.TryParse(visualParts[1], out var visualRequested) && visualRequested > 0)
                {
                    visualSeconds = Math.Min(visualRequested, 600);
                }

                exitCode = CombineExitCode(exitCode, SelfTest.RunOverlayVisualHold(_session, _log, visualSeconds));
            }

            if (overlay && _session is not null && _storage is not null && _pinning is not null &&
                _settings is not null && _log is not null)
            {
                exitCode = CombineExitCode(exitCode,
                    SelfTest.RunOverlayEndToEnd(_session, _storage, _pinning, _settings, _log));

                // 贴图窗口"绕过 CloseRequested 的关闭"（Alt+F4 / WM_CLOSE）
                // 之后计数与托盘标签必须跟着走。与覆盖层端到端同一个门禁入口（--selftest-overlay）。
                exitCode = CombineExitCode(exitCode, SelfTestPinClose.Run(_pinning, _log));
            }

            if (overlayOcr && _session is not null && _settings is not null)
            {
                // **只释放自己创建的实例。**
                // 旧写法 `using var recognitionForOverlay = _recognition ?? new RecognitionService(...)`
                // 在 `_recognition` **非 null**（共享实例）时也会把它 Dispose 掉 —— 那个实例已经被
                // 交给每个结果窗（见本文件 _recognition 的构造与结果窗接线），"我创建的才由我释放"
                // 这条规则被破坏，之后任何 Detect 都会抛 ObjectDisposedException。
                // 现在按所有权分成两条互斥的路径：自己 new 的用 using 释放，共享的一律不碰。
                if (_recognition is not null)
                {
                    exitCode = CombineExitCode(exitCode,
                        SelfTest.RunOverlayOcrEndToEnd(_session, _settings, _recognition, _log!));
                }
                else
                {
                    using var ownedRecognition = new RecognitionService(_log!, _settings);
                    exitCode = CombineExitCode(exitCode,
                        SelfTest.RunOverlayOcrEndToEnd(_session, _settings, ownedRecognition, _log!));
                }
            }

            if (ocrArg is not null && _log is not null)
            {
                // 三种 OCR 自检共用一个参数形式：--selftest-xxx[=图片路径]。
                // 不给路径时用当前屏幕当输入，方便在没有样本图的机器上冒烟。
                var ocrParts = ocrArg.Split('=', 2, StringSplitOptions.TrimEntries);
                var ocrImage = ocrParts is { Length: 2 } ? ocrParts[1] : null;
                using var recognition = new RecognitionService(_log, _settings);

                if (ocrArg.StartsWith("--selftest-ocr-window", StringComparison.OrdinalIgnoreCase) &&
                    _settings is not null && _storage is not null)
                {
                    // 识别结果窗口（预览 + 框叠加 + 悬停工具条 + 编辑模式）
                    var exporter = new ExportService(_settings, _storage, _log);
                    exitCode = CombineExitCode(exitCode,
                        SelfTest.RunOcrWindow(recognition, exporter, _settings, _log, ocrImage));
                }
                else if (ocrArg.StartsWith("--selftest-ocr-mouse", StringComparison.OrdinalIgnoreCase) &&
                         _settings is not null && _storage is not null)
                {
                    // 识别结果窗口的鼠标交互（SendInput 真实事件）
                    var exporter = new ExportService(_settings, _storage, _log);
                    exitCode = CombineExitCode(exitCode,
                        SelfTest.RunOcrWindowMouse(recognition, exporter, _settings, _log, ocrImage));
                }
                else if (ocrArg.StartsWith("--selftest-ocr-clipboard", StringComparison.OrdinalIgnoreCase) &&
                         _settings is not null)
                {
                    // 托盘"识别剪贴板图片"整链路（会覆盖剪贴板内容）
                    exitCode = CombineExitCode(exitCode, SelfTest.RunClipboardOcr(TryOcrClipboard, _settings, _log, ocrImage));
                }
                else
                {
                    // 引擎链路
                    exitCode = CombineExitCode(exitCode, SelfTest.RunOcr(recognition, _log, ocrImage));

                    // 四种导出器
                    if (ocrArg.StartsWith("--selftest-export", StringComparison.OrdinalIgnoreCase) &&
                        _settings is not null && _storage is not null)
                    {
                        var exporter = new ExportService(_settings, _storage, _log);
                        var outputDir = Path.Combine(Path.GetTempPath(), "SimOCR-selftest", "export");
                        exitCode = CombineExitCode(exitCode, SelfTest.RunExport(recognition, exporter, _log, ocrImage, outputDir));
                    }
                }
            }

            // 表格识别验收：真实鼠标 框选 → 导出 xlsx（可带 --accept-enhance 走增强识别）
            if (acceptArg is not null && _log is not null && _settings is not null && _session is not null)
            {
                var acceptParts = acceptArg.Split('=', 2, StringSplitOptions.TrimEntries);
                var acceptImages = acceptParts is { Length: 2 } ? acceptParts[1] : string.Empty;
                using var recognition = new RecognitionService(_log, _settings);
                exitCode = CombineExitCode(exitCode, TableAcceptance.Run(
                    _session, _settings, recognition, _log, acceptImages, acceptEnhanced, acceptTxt));
            }

            // 窗口吸附：纯几何自检（含真实窗口边框对照）
            if (snapSelfTest && _log is not null)
            {
                exitCode = CombineExitCode(exitCode, SnapSelfTest.RunSnap(_log));
            }

            // 窗口吸附：真实鼠标验收（悬停高亮 / 单击抓取 / 拖动吸附）
            if (snapAcceptance && _log is not null && _settings is not null && _session is not null)
            {
                exitCode = CombineExitCode(exitCode, SnapSelfTest.RunAcceptance(_session, _settings, _log));
            }

            // 窗口吸附验收辅助：开一扇已知几何的窗口并保持（供 --accept-snap 当"外来窗口"）
            if (snapHostArg is not null && _log is not null)
            {
                exitCode = CombineExitCode(exitCode, SnapSelfTest.RunHostWindow(snapHostArg, _log));
            }

            // 遮盖裁剪缺陷复现：被遮盖窗口的边缘是否被吸附（--snap-probe-occlusion）
            if (snapOcclusionArg is not null && _log is not null && _settings is not null && _session is not null)
            {
                exitCode = CombineExitCode(exitCode, SnapOcclusionProbe.Run(snapOcclusionArg, _session, _settings, _log));
            }
        }
        catch (Exception ex)
        {
            _log?.Error("自检运行异常", ex);
            // 这里过去是无条件 `exitCode = 2` —— 一个子用例已经判定真失败(1/4)时，
            // 后面某个子用例抛异常会把结论改成"用法/环境(2)"，失败又被盖住。组合器只在
            // 还没有真失败时才采用 2（更保守：异常不掩盖已判定的失败）。
            exitCode = CombineExitCode(exitCode, 2);
        }

        return true;
    }

    /// <summary>
    /// **0.10.10：合并多个自检子结果的退出码（不再丢信息）。**
    ///
    /// 旧写法是 `Math.Max(exitCode, next)`，按**数值大小**取胜；数值序恰好是
    /// `5(跳过) &gt; 4(断言失败) &gt; 2(用法/环境) &gt; 1(失败) &gt; 0`，于是：
    ///   * 一个子用例返回 **5**（用例不成立）会盖住另一个子用例的 **1 / 4**（真失败）→
    ///     门禁把整条调用记成 `[跳过]`，**真失败被吞掉**（"退出码组合器丢信息"）；
    ///   * 一个子用例返回 **2** 同样会盖住 **1**。
    ///
    /// 修法只改**组合**语义，不改任何单个子用例的返回值：
    ///   * 真失败（1 / 4）优先于"跳过(5) / 用法(2)"；
    ///   * 两个都是真失败时保留更具体的 **4**（断言失败）；
    ///   * 其余组合按"**用法(2) 优先于 跳过(5)**"—— 2 是阻塞提交的用法/环境拒绝，
    ///     5 不影响退出码，让 5 盖住 2 同样是丢信息（旧 `Math.Max` 的数值序恰好如此）。
    ///
    /// **单开关时与 `Math.Max` 逐值相同**：`current == 0` 直接返回 `next`，
    /// 所以门禁逐条调用的正常路径语义一字未变；只有"一次调用里组合多个自检开关"这条
    /// 边界路径会从"跳过/用法盖住失败"变成"失败优先"——那是**更保守**，不是功能变化。
    /// </summary>
    internal static int CombineExitCode(int current, int next)
    {
        static bool IsFailure(int code) => code is 1 or 4;

        if (current == 0)
        {
            return next;
        }

        if (next == 0)
        {
            return current;
        }

        var currentIsFailure = IsFailure(current);
        var nextIsFailure = IsFailure(next);
        if (currentIsFailure && !nextIsFailure)
        {
            return current;
        }

        if (nextIsFailure && !currentIsFailure)
        {
            return next;
        }

        // 同为真失败：4（断言失败）比 1（一般失败）更具体，保留 4。
        if (currentIsFailure)
        {
            return Math.Max(current, next);
        }

        // 都不是真失败：2（用法/环境拒绝，阻塞提交）优先于 5（用例不成立，不影响退出码）。
        if (current == 2 || next == 2)
        {
            return 2;
        }

        return Math.Max(current, next);
    }

    // ---------------------------------------------------------------- OCR

    /// <summary>
    /// 覆盖层四个导出按钮的处理：识别结果窗口**立即打开**（先显示"正在识别…"），
    /// **不自动保存**；用户校对/编辑后点右下角磁盘按钮才写文件。
    /// </summary>
    private void OnOcrRequested(BitmapSource image, OcrExportFormat format)
        => OpenOcrWindow(image, format, "选区");

    /// <summary>托盘"识别剪贴板图片"：返回 false 表示剪贴板里没有图片。</summary>
    internal bool TryOcrClipboard()
    {
        var image = ReadClipboardImage();
        if (image is null)
        {
            _log?.Info("识别剪贴板图片：剪贴板里没有图片");
            return false;
        }

        OpenOcrWindow(image, OcrExportFormat.Txt, "剪贴板图片");
        return true;
    }

    /// <summary>托盘"识别最近的截图"：返回 false 表示本次运行还没截过图。</summary>
    internal bool TryOcrLastCapture()
    {
        var image = _session?.LastSelectionImage;
        if (image is null)
        {
            _log?.Info("识别最近的截图：本次运行还没有截图");
            return false;
        }

        OpenOcrWindow(image, OcrExportFormat.Txt, "最近的截图");
        return true;
    }

    /// <summary>读剪贴板里的图片（WPF 能处理 PNG / CF_DIB / CF_BITMAP）。</summary>
    internal static BitmapSource? ReadClipboardImage()
    {
        try
        {
            return System.Windows.Clipboard.ContainsImage() ? System.Windows.Clipboard.GetImage() : null;
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占用时偶发，交给调用方提示
            return null;
        }
    }

    /// <summary>打开识别结果窗口（识别在窗口内后台进行）。</summary>
    private void OpenOcrWindow(BitmapSource image, OcrExportFormat format, string sourceLabel)
    {
        if (_log is null || _recognition is null || _exporter is null)
        {
            return;
        }

        if (!_recognition.IsAvailable)
        {
            _log.Warn($"OCR 不可用：{_recognition.StatusMessage}");
            _tray?.ShowBalloon(_recognition.StatusMessage, true);
            return;
        }

        // 覆盖层的 ShowDialog / 托盘菜单回调都还在调用栈上：把窗口创建推到下一次消息循环更稳
        var dispatcher = _app?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _log.Info($"请求 OCR（来源：{sourceLabel}，目标格式 {format.ShortLabel()}，{image.PixelWidth}x{image.PixelHeight}）");

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var window = new OcrResultWindow(image, format, _recognition, _exporter, _log);
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                _log.Error("打开识别结果窗口失败", ex);
                _tray?.ShowBalloon("打开识别结果窗口失败，详情见「设置 → 运行信息 → 打开日志目录」", true);
            }
        }));
    }

    // ---------------------------------------------------------------- 热键

    private void RegisterHotkey(bool reportToWindow = true)
    {
        if (_hotkeyWindow is null || _settings is null)
        {
            return;
        }

        var text = _settings.Current.Hotkey.ToGestureText();
        var gesture = HotkeyGesture.Parse(text);
        var error = _hotkeyWindow.TryRegister(gesture);
        var message = DescribeHotkeyError(error);

        if (message is not null)
        {
            // 首选热键被占用（F1 常被其他软件占用）→ 临时退到下一个可用预设，并明确告知
            var fallback = FindAvailableFallback(text);
            if (fallback is not null && _hotkeyWindow.TryRegister(fallback) == 0)
            {
                _tray?.UpdateHotkeyText(fallback.Display);
                _tray?.ShowBalloon(
                    $"热键 {gesture.Display} 被其他程序占用，已临时改用 {fallback.Display}（可在托盘「快捷键」里修改）",
                    false);
                _settingsWindow?.ReportHotkeyResult(gesture.Display, $"{message}；已临时改用 {fallback.Display}");
                _log?.Warn($"热键回退: {gesture.Display} → {fallback.Display}");
                return;
            }

            _tray?.ShowBalloon($"热键 {gesture.Display} 注册失败：{message}", true);
        }

        _tray?.UpdateHotkeyText();

        if (reportToWindow)
        {
            _settingsWindow?.ReportHotkeyResult(gesture.Display, message);
        }
    }

    /// <summary>在预设列表里找一个当前可用的热键（试完立刻注销，交给调用方正式注册）。</summary>
    private HotkeyGesture? FindAvailableFallback(string currentText)
    {
        if (_settings is null || _hotkeyWindow is null)
        {
            return null;
        }

        foreach (var preset in _settings.Current.Hotkey.Presets)
        {
            if (string.Equals(preset, currentText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = HotkeyGesture.Parse(preset);
            if (candidate.IsEmpty)
            {
                continue;
            }

            // 探测时不打日志：探测失败是正常现象（正在找可用项）
            if (_hotkeyWindow.TryRegister(candidate, quiet: true) == 0)
            {
                _hotkeyWindow.Unregister();
                return candidate;
            }
        }

        return null;
    }

    private static string? DescribeHotkeyError(int error) => error switch
    {
        0 => null,
        HotkeyWindow.UnsafeHotkeyError => "无修饰键时只允许 F1~F24 或 PrintScreen（普通按键会抢占全局输入）",
        NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED => "已被其他程序占用（F1 常被 IDE、游戏、帮助系统占用）",
        _ => $"Win32 错误码 {error}",
    };

    /// <summary>托盘「快捷键」子菜单选择预设。</summary>
    private void ApplyHotkeyText(string gestureText)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Update(s => s.Hotkey.FromGestureText(gestureText));
        RegisterHotkey();
        _log?.Info($"热键已切换为 {gestureText}");
    }

    /// <summary>设置窗口「检测冲突」：临时借用注册接口试探，然后恢复原热键。</summary>
    private string? ProbeHotkey(string gestureText)
    {
        if (_hotkeyWindow is null)
        {
            return "内部状态未就绪";
        }

        var candidate = HotkeyGesture.Parse(gestureText);
        if (candidate.IsEmpty)
        {
            return "无法识别的组合";
        }

        var current = _hotkeyWindow.Current;
        _hotkeyWindow.Unregister();
        var error = _hotkeyWindow.TryRegister(candidate);
        _hotkeyWindow.Unregister();

        if (!current.IsEmpty)
        {
            _hotkeyWindow.TryRegister(current);
        }

        return DescribeHotkeyError(error);
    }

    private void OnPowerResumed()
    {
        _log?.Info("系统从睡眠/休眠恢复");
        if (_settings?.Current.Behavior.Enabled == true)
        {
            RegisterHotkey(reportToWindow: false);
        }
    }

    // ---------------------------------------------------------------- 启用状态

    private void ToggleEnabled() => ApplyEnabled(_settings?.Current.Behavior.Enabled != true);

    private void ApplyEnabled(bool enabled)
    {
        if (_settings is null || _hotkeyWindow is null)
        {
            return;
        }

        _settings.Update(s => s.Behavior.Enabled = enabled, notify: false);

        if (enabled)
        {
            RegisterHotkey();
        }
        else
        {
            _hotkeyWindow.Unregister();
        }

        _tray?.UpdateEnabledState(enabled);
        _settingsWindow?.SyncEnabled(enabled);
        _log?.Info($"启用状态切换为 {(enabled ? "已启用" : "已暂停")}");
    }

    // ---------------------------------------------------------------- 截图

    /// <summary>
    /// 覆盖层预热（启动空闲时跑一次）：抓一帧真屏 → 造一个**不可见且不抢焦点**的覆盖层 → 立刻关闭。
    ///
    /// 目的：把"首次 F1 才付"的一次性代价（XAML/BAML 解析、JIT、D3D 设备与合成初始化、
    /// 大位图上传）提前到启动阶段，之后每次 F1 的显示→首帧都走热路径。
    /// 失败只记警告：预热只是优化，绝不能影响主功能。
    /// </summary>
    private void WarmUpOverlay()
    {
        try
        {
            if (_session is null)
            {
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var frame = _session.CaptureScreenOnly();
            if (frame is null)
            {
                _log?.Warn("覆盖层预热：抓屏失败，跳过");
                return;
            }

            // 意图参数：整窗不可见 + 不抢焦点（不是"测试模式"，见 CreateOverlay 的注释）
            var overlay = _session.CreateOverlay(frame, warmupOffscreen: true);
            overlay.ShowActivated = false;  // 预热绝不允许抢焦点
            overlay.Opacity = 0;            // 且整窗不可见
            overlay.Show();
            overlay.Close();
            _log?.Info($"覆盖层预热完成：{watch.ElapsedMilliseconds}ms（含一次抓屏）");
        }
        catch (Exception ex)
        {
            _log?.Warn($"覆盖层预热失败（不影响使用）：{ex.Message}");
        }
    }

    private void BeginCapture()
    {
        try
        {
            _session?.BeginCapture();
        }
        catch (Exception ex)
        {
            _log?.Error("BeginCapture 异常", ex);
        }
    }

    // ---------------------------------------------------------------- 设置 / 目录

    private void OpenSettings()
    {
        if (_settings is null || _autoStart is null || _paths is null || _log is null || _storage is null)
        {
            return;
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, _autoStart, _paths, _log, _storage)
        {
            HotkeyProbe = ProbeHotkey,
        };

        window.HotkeyChangeRequested += ApplyHotkeyText;
        window.EnabledChangeRequested += ApplyEnabled;
        window.StartWithWindowsChangeRequested += ApplyStartWithWindows;
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
        };

        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private void ApplyStartWithWindows(bool enabled)
    {
        if (_autoStart is null || _settings is null)
        {
            return;
        }

        var ok = _autoStart.Apply(enabled);

        // **0.10.9：注册表写失败时不再把 `enabled` 落进配置。**
        // 旧写法无论 `Apply` 成败都 `_settings.Update(...)` —— 注册表被安全软件拦下时，
        // config.json 会记下一个**没生效**的开机自启值（"落盘撒谎"）。真源始终是注册表
        // （`_autoStart.IsEnabled()`），这里只让配置与真源保持一致；成功路径的行为逐字不变。
        if (ok)
        {
            _settings.Update(s => s.Behavior.StartWithWindows = enabled, notify: false);
        }

        // 状态栏只说"成没成"，与设置页加载时的那套说法保持一致；
        // 注册表里到底写了什么（带引号的完整命令行）只进日志。
        var status = !ok
            ? "写入注册表失败（可能被安全软件拦截），详情见「设置 → 运行信息 → 打开日志目录」"
            : enabled
                ? "已注册开机自启（当前用户）"
                : "未注册开机自启";

        _log?.Info($"开机自启 {enabled} → {(ok ? "成功" : "失败")}");
        if (ok && enabled)
        {
            _log?.Info($"注册表 Run 值：{_autoStart.GetRegisteredCommand()}");
        }
        _settingsWindow?.SyncAutoStart(_autoStart.IsEnabled(), status);
        _tray?.ShowBalloon(enabled ? "已开启开机自启" : "已关闭开机自启", !ok);
    }

    private void OpenSaveFolder()
    {
        if (_storage is null || _log is null)
        {
            return;
        }

        try
        {
            var dir = _storage.ResolveSaveDirectory();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            _log.Info($"打开保存目录: {dir}");
        }
        catch (Exception ex)
        {
            _log.Error("打开保存目录失败", ex);
            _tray?.ShowBalloon($"打开保存目录失败：{FriendlyError.ForIo(ex)}", true);
        }
    }

    // ---------------------------------------------------------------- 退出与异常

    private void ExitApplication()
    {
        _log?.Info("用户请求退出");
        _app?.Shutdown(0);
    }

    private void InstallGlobalExceptionHandlers(App app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            _log?.Error("UI 线程未处理异常", e.Exception);
            _tray?.ShowBalloon("发生异常，详情见「设置 → 运行信息 → 打开日志目录」", true);
            e.Handled = true; // 覆盖层异常不应杀掉托盘常驻进程
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _log?.Error("AppDomain 未处理异常", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.Warn("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    private void LogEnvironment()
    {
        if (_log is null)
        {
            return;
        }

        try
        {
            var vs = MonitorLayout.GetVirtualScreen();
            _log.Info($"虚拟桌面(物理像素): {vs.X},{vs.Y} {vs.Width}x{vs.Height}");

            foreach (var m in MonitorLayout.GetMonitors())
            {
                _log.Info($"  显示器 {m.DeviceName}: {m.X},{m.Y} {m.Width}x{m.Height} " +
                          $"DPI={m.DpiX} 缩放={m.Scale:P0} 主屏={m.IsPrimary}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("记录显示器信息失败", ex);
        }
    }

    private static LogLevel ParseLogLevel(string? text) => text?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    /// <summary>
    /// 加载表格解析配置档。优先级：
    ///   <c>--profile=&lt;YAML 文件路径&gt;</c> &gt; <c>--profile=&lt;档名&gt;</c>
    ///   &gt; 设置里的自定义 YAML 路径（ProfilePath）&gt; 设置里的档名（Profile）
    ///   &gt; <c>Profiles/default.yaml</c> &gt; 代码内置兜底。
    /// 加载结果写进日志，方便"为什么字段没认出来"这类问题的定位。
    /// </summary>
    private void InitializeTableProfile(string[] args)
    {
        var requested = args
            .FirstOrDefault(a => a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase));
        requested = requested is null ? null : requested["--profile=".Length..].Trim('"');

        string? explicitPath = null;
        string? name = null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            // 给的是路径就当路径用；否则当档名（在 Profiles 目录里找）
            explicitPath = requested!.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                           requested.Contains(Path.DirectorySeparatorChar) ||
                           requested.Contains(Path.AltDirectorySeparatorChar)
                ? requested
                : null;
            name = explicitPath is null ? requested : null;
        }

        // 命令行没给就用设置里的（0.8.1 起设置页可以记一个自定义 YAML 路径，它优先于档名；
        // 两个字段由设置页保证互斥，手改配置时同时写了就按"更具体的路径"算）。
        explicitPath ??= _settings?.Current.Tables?.ProfilePath;
        name ??= _settings?.Current.Tables?.Profile;

        try
        {
            TableProfiles.Initialize(name, explicitPath);
            _log?.Info($"表格配置档：{TableProfiles.StatusMessage}");
            // 把"要求的"和"实际加载的"都写出来：配置档加载不上时会静默回退默认档，
            // 只记结果的话日志里看不出"用户选的那档没生效"。
            _log?.Info($"  配置要求：档名={name ?? "(空)"} 自定义路径={explicitPath ?? "(未指定)"}");
            _log?.Info($"  可用档位：{string.Join(", ", TableProfiles.AvailableProfiles())}");
        }
        catch (Exception ex)
        {
            // 配置档坏了不能拖垮整个程序：退回内置兜底并明确告警
            _log?.Warn("表格配置档加载失败，已退回内置兜底", ex);
            TableProfiles.Initialize("__missing__");
        }
    }

    public void Shutdown()
    {
        _log?.Info("SimOCR 正在退出……");

        try
        {
            _settingsWindow?.Close();
        }
        catch
        {
            // ignore
        }

        _tray?.Dispose();
        _hotkeyWindow?.Dispose();
        _pinning?.Dispose();
        _singleInstance?.Dispose();
        // **正常退出时释放 OCR 引擎。**
        // `RecognitionService` 持有原生 RapidOcr 引擎（只在 `Dispose` 里 `_engine?.Dispose()`），
        // 且类内**没有终结器** —— 不显式释放就只能等进程结束（那次是"次优"而非"泄漏"，
        // 但它是 CA2213「未释放 IDisposable 字段」直接点名的那一类）。放在日志 Dispose **之前**，
        // 好让释放过程中的告警还能落盘。
        _recognition?.Dispose();
        _log?.Info("SimOCR 已退出");
        _log?.Dispose();
    }
}
