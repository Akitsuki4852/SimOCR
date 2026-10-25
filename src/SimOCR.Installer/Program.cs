using System.Diagnostics;

namespace SimOCR.Installer;

/// <summary>
/// SimOCR-Setup 入口。
///
/// 三种模式：
///   默认      自解压安装 + 启动主程序（双击即用；已安装则直接启动）
///   --verify  只检查安装完整性，把结果写进日志并给出退出码（自动化验收用）
///   --uninstall  卸载（由安装目录里的 SimOCR-Uninstall.exe 调用）
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitPayloadBroken = 2;
    private const int ExitNeedsUser = 3;
    private const int ExitCancelled = 4;

    [STAThread]
    private static int Main(string[] args)
    {
        var commandLine = CommandLine.Parse(args);
        var layout = InstallLayout.Create(commandLine.InstallDirectory);
        // 注意：这个句柄是"可移交"的 —— --uninstall --purge-data 时 RunUninstall 会先关掉它
        // （否则安装器自己的 <DataDir>\logs\installer-*.log 就是数据目录删不掉的元凶，
        // 见 UninstallService.PurgeDataDirectory 的注释），改开一份 %TEMP% 里的继任日志；
        // 那时这个局部变量会被指向新实例，旧实例已在移交时 Dispose 过（Dispose 幂等）。
        var log = Logger.Create(layout.LogDir);

        log.Info($"===== SimOCR-Setup {PayloadReader.SelfVersion} pid={Environment.ProcessId} =====");
        log.Info($"self={PayloadReader.SelfPath}");
        log.Info($"args={string.Join(' ', args)}");
        log.Info($"installDir={layout.InstallDir}");
        log.Info($"hasPayload={PayloadReader.HasPayload} hasUninstaller={PayloadReader.HasUninstaller}");
        log.Info($"identity: {ProcessIdentity.Describe()}");

        // 常规安装器的通行行为：用户没显式指定目录时，升级就地在"上次装的位置"，
        // 而不是在默认位置再装一份（Inno/NSIS/Velopack/electron-builder 都这样）。
        layout = ApplyPreviousInstallLocation(commandLine, layout, log);
        log.Info($"effectiveInstallDir={layout.InstallDir}");

        var exitCode = ExitFailed;
        try
        {
            if (commandLine.Help)
            {
                NativeMethods.MessageBox(
                    CommandLine.Usage, "SimOCR 安装器", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
                exitCode = ExitOk;
            }
            else if (commandLine.Uninstall)
            {
                exitCode = RunUninstall(commandLine, layout, log);
            }
            else if (commandLine.Verify)
            {
                exitCode = RunVerify(commandLine, layout, log);
            }
            else if (commandLine.SelfTestUi)
            {
                exitCode = RunSelfTestUi(log);
            }
            else
            {
                exitCode = RunInstall(commandLine, layout, log);
            }
        }
        catch (Exception ex)
        {
            log.Error($"未捕获异常：{ex}");
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(
                    $"安装器发生异常：\n\n{ex.Message}\n\n日志：{log.Path}",
                    "SimOCR 安装器",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            }

            exitCode = ExitFailed;
        }
        finally
        {
            log.Info($"===== 退出 exitCode={exitCode} 日志={log.Path} =====");
            log.Dispose();
        }

        return exitCode;
    }

    // -----------------------------------------------------------------------
    // 安装
    // -----------------------------------------------------------------------
    /// <summary>
    /// 没给 <c>--dir</c> 时沿用上次的安装位置。只在"记录的驱动器还在"时沿用，
    /// 免得上次装在已经拔掉的移动盘上导致安装失败。
    /// </summary>
    private static InstallLayout ApplyPreviousInstallLocation(
        CommandLine commandLine, InstallLayout layout, Logger log)
    {
        if (!string.IsNullOrWhiteSpace(commandLine.InstallDirectory))
        {
            return layout;
        }

        var previous = InstallRegistry.TryReadInstallLocation();
        if (string.IsNullOrWhiteSpace(previous))
        {
            return layout;
        }

        if (InstallRegistry.PathsEqual(previous!, layout.InstallDir))
        {
            return layout;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(previous!));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                log.Warn($"上次安装位置 {previous} 所在驱动器不存在，改用默认位置 {layout.InstallDir}");
                return layout;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"上次安装位置 {previous} 无效（{ex.Message}），改用默认位置 {layout.InstallDir}");
            return layout;
        }

        // 记录的安装位置必须"确实还在，而且看起来是一次真安装"才沿用。
        // 否则会出现很难查的故障：某次安装到临时目录（或用户手工删了安装目录）之后，
        // 注册表里那个路径一直留着，之后每次双击安装器都往那个已经不存在的路径装，
        // 而且 --verify 也拿它当基准去校验 → 表现为"安装器静默失败、还没有日志"。
        if (!Directory.Exists(previous))
        {
            log.Warn($"上次安装位置 {previous} 已不存在，改用默认位置 {layout.InstallDir}");
            return layout;
        }

        var previousMarker = Path.Combine(previous!, MarkerFile.FileName);
        if (!File.Exists(previousMarker))
        {
            log.Warn($"上次安装位置 {previous} 里没有安装标记，视为遗留记录，改用默认位置 {layout.InstallDir}");
            return layout;
        }

        log.Info($"EVENT install-reuse-location previous={previous}");
        log.Info($"上次安装在 {previous}，本次沿用该位置（如需强制默认位置请显式指定 --dir）");
        return InstallLayout.Create(previous);
    }

    private static int RunInstall(CommandLine commandLine, InstallLayout layout, Logger log)
    {
        if (!PayloadReader.HasPayload)
        {
            const string broken =
                "这个 exe 里没有程序本体（它是安装目录里的卸载器副本）。\n\n" +
                "请双击从 Releases 页下载的安装包（文件名以 -setup-win-x64.exe 结尾）来安装。";
            log.Error("payload 缺失，拒绝安装");
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(broken, "SimOCR 安装器", NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            }

            return ExitPayloadBroken;
        }

        // 同一个用户同时只能有一个安装进程在写安装目录
        using var mutex = new Mutex(false, @"Local\SimOCR.Setup.v1");
        var owned = false;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(180));
        }
        catch (AbandonedMutexException)
        {
            // 上一个安装进程异常退出，锁被放弃 —— 我们接管
            owned = true;
        }

        if (!owned)
        {
            const string busy = "另一个 SimOCR 安装进程正在运行，请等它结束后重试。";
            log.Warn(busy);
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(busy, "SimOCR 安装器", NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
            }

            return ExitNeedsUser;
        }

        var request = new InstallRequest
        {
            Force = commandLine.Force,
            Silent = commandLine.Silent,
            CreateShortcuts = !commandLine.NoShortcuts,
            WriteRegistry = !commandLine.NoRegistry,
            ShowProgress = !commandLine.NoProgress,
        };

        var service = new InstallService(layout, log);
        var outcome = service.EnsureInstalled(request, out var detail);
        log.Info($"EVENT install-outcome {outcome}");

        if (outcome == InstallOutcome.Failed)
        {
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(
                    $"{detail}\n\n日志：{log.Path}", "SimOCR 安装失败", NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            }

            return ExitFailed;
        }

        if (outcome == InstallOutcome.Cancelled)
        {
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(
                    detail, "SimOCR 安装器", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
            }

            return commandLine.Silent ? ExitNeedsUser : ExitCancelled;
        }

        if (commandLine.NoLaunch)
        {
            log.Info("按参数要求不启动主程序");
            return ExitOk;
        }

        // 已经在跑就不再启动（SimOCR 是单实例，重复启动会退化成一键截图，容易吓到用户）
        var running = ProcessUtil.FindRunningFrom(layout.InstallDir);
        if (running.Count > 0)
        {
            foreach (var process in running)
            {
                process.Dispose();
            }

            log.Info("SimOCR 已在运行，跳过启动");
            if (!commandLine.Silent && outcome == InstallOutcome.UpToDate)
            {
                NativeMethods.MessageBox(
                    "SimOCR 已经在运行了。\n\n它常驻在右下角托盘里，按 F1 就能截图。",
                    "SimOCR",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
            }

            return ExitOk;
        }

        return Launch(layout, commandLine, log);
    }

    private static int Launch(InstallLayout layout, CommandLine commandLine, Logger log)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = layout.ExePath,
                WorkingDirectory = layout.InstallDir,
                UseShellExecute = true,
            });

            log.Info($"已启动主程序：{layout.ExePath}");
            log.Info($"EVENT launch ok=true path={layout.ExePath}");
            return ExitOk;
        }
        catch (Exception ex)
        {
            log.Error($"启动主程序失败：{ex.GetType().Name} {ex.Message}");
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(
                    $"安装完成，但启动 SimOCR 失败：\n\n{ex.Message}\n\n可以直接双击：{layout.ExePath}",
                    "SimOCR 安装器",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            }

            return ExitFailed;
        }
    }

    // -----------------------------------------------------------------------
    // 校验
    // -----------------------------------------------------------------------
    private static int RunVerify(CommandLine commandLine, InstallLayout layout, Logger log)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        var marker = MarkerFile.TryRead(layout.MarkerPath);

        checks.Add((
            "marker",
            marker is not null,
            marker is null
                ? $"缺少 {InstallLayout.Create(null).MarkerPath}"
                : $"version={marker.Version} files={marker.FileCount} bytes={marker.TotalBytes} installedUtc={marker.InstalledUtc:u}"));

        var exeOk = File.Exists(layout.ExePath) && new FileInfo(layout.ExePath).Length > 0;
        checks.Add(("SimOCR.exe", exeOk, exeOk ? Describe(layout.ExePath) : $"缺失 {layout.ExePath}"));

        var uninstallerOk = File.Exists(layout.UninstallerPath) && new FileInfo(layout.UninstallerPath).Length > 0;
        checks.Add((
            "SimOCR-Uninstall.exe",
            uninstallerOk,
            uninstallerOk ? Describe(layout.UninstallerPath) : $"缺失 {layout.UninstallerPath}"));

        var fileCount = Directory.Exists(layout.InstallDir)
            ? Directory.GetFiles(layout.InstallDir, "*", SearchOption.AllDirectories).Length
            : 0;

        // 0.10.5：判据由 `>=` 改为 **精确相等**。
        // 旧判据只能发现"少装了很多"，发现不了"多了 / 少了几个"的部分安装；
        // 标记里的 files 由 InstallService 写成"这次安装后安装目录应有的文件总数"
        // （含随后释放的卸载器），因此 == 是可判定的。
        checks.Add((
            "fileCount",
            marker is null || fileCount == marker.FileCount,
            $"{fileCount} 个文件（标记记录 {marker?.FileCount ?? 0}）"));

        // 0.10.5：只有**真的没做**这项检查时才说"跳过"。
        // 旧写法在"标记说该有注册表项"的正常安装里也照样打印"--no-registry 跳过（不检查）"，
        // 而进程刚刚才查过注册表 —— 那是**报告了它没执行的检查**，会误导读日志的人。
        var registryExpected = marker?.Registry ?? true;
        var registryChecked = marker is not null && registryExpected;
        var registryOk = registryChecked && UninstallRegistry.Exists();
        checks.Add((
            "registry",
            !registryExpected || registryOk,
            !registryChecked
                ? "未检查：本次安装按 --no-registry 跳过（标记未记录注册表项）"
                : registryOk ? "HKCU Uninstall\\SimOCR 存在" : "缺少卸载注册表项"));

        var shortcutsExpected = marker?.Shortcuts ?? true;
        var shortcutsChecked = marker is not null && shortcutsExpected;
        var startMenu = shortcutsChecked && File.Exists(layout.StartMenuShortcut);
        var desktop = shortcutsChecked && File.Exists(layout.DesktopShortcut);
        var shortcutsOk = startMenu && desktop;
        checks.Add((
            "shortcuts",
            !shortcutsExpected || shortcutsOk,
            !shortcutsChecked
                ? "未检查：本次安装按 --no-shortcuts 跳过（标记未记录快捷方式）"
                : $"开始菜单={(startMenu ? "有" : "无")} 桌面={(desktop ? "有" : "无")}"));

        // 自写安装键 + App Paths（0.1.5 起）：让"升级沿用原位置"和 Win+R 输 SimOCR 可用
        var installKeyOk = InstallRegistry.Exists();
        checks.Add((
            "installKey",
            !registryExpected || installKeyOk,
            installKeyOk
                ? $"HKCU\\{InstallRegistry.KeyPath} InstallLocation={InstallRegistry.TryReadInstallLocation()}"
                : registryChecked
                    ? $"缺少 HKCU\\{InstallRegistry.KeyPath}"
                    : $"未检查：本次安装按 --no-registry 跳过（HKCU\\{InstallRegistry.KeyPath}）"));

        var appPathsOk = AppPathsRegistry.Exists();
        checks.Add((
            "appPaths",
            !registryExpected || appPathsOk,
            appPathsOk
                ? $"HKCU\\{AppPathsRegistry.KeyPath} 存在"
                : registryChecked
                    ? $"缺少 HKCU\\{AppPathsRegistry.KeyPath}"
                    : $"未检查：本次安装按 --no-registry 跳过（HKCU\\{AppPathsRegistry.KeyPath}）"));

        if (PayloadReader.HasPayload && marker is not null)
        {
            using var payload = PayloadReader.OpenPayload();
            var hash = payload is null ? string.Empty : PayloadReader.ComputeSha256(payload);
            var same = hash.Length > 0 && string.Equals(hash, marker.PayloadSha256, StringComparison.OrdinalIgnoreCase);
            checks.Add((
                "payloadSha256",
                same,
                $"本安装包的 payload={Short(hash)} 已安装={Short(marker.PayloadSha256)}"));
        }
        else
        {
            checks.Add(("payloadSha256", true, "跳过（当前 exe 不含 payload）"));
        }

        var failed = checks.Count(c => !c.Ok);
        foreach (var check in checks)
        {
            log.Info($"VERIFY {(check.Ok ? "OK  " : "FAIL")} {check.Name} = {check.Detail}");
        }

        var verdict = failed == 0 ? "PASS" : "FAIL";
        log.Info($"VERIFY RESULT {verdict} ({checks.Count - failed}/{checks.Count})");
        log.Info($"EVENT verify-result {verdict} {checks.Count - failed}/{checks.Count}");

        if (!commandLine.Silent)
        {
            var text = new StringBuilder()
                .AppendLine(failed == 0 ? "SimOCR 安装检查通过。" : "SimOCR 安装检查发现问题：")
                .AppendLine();
            foreach (var check in checks)
            {
                text.AppendLine($"{(check.Ok ? "✔" : "✘")} {check.Name}：{check.Detail}");
            }

            text.AppendLine().Append("日志：").Append(log.Path);
            NativeMethods.MessageBox(
                text.ToString(),
                "SimOCR 安装检查",
                NativeMethods.MB_OK | (failed == 0 ? NativeMethods.MB_ICONINFORMATION : NativeMethods.MB_ICONERROR));
        }

        return failed == 0 ? ExitOk : ExitFailed;
    }

    private static string Describe(string path)
    {
        try
        {
            return $"{new FileInfo(path).Length / 1048576.0:F2}MB @ {path}";
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string Short(string hash) => hash.Length >= 16 ? hash[..16] + "…" : hash;

    // -----------------------------------------------------------------------
    // 进度窗口自检（自动化验收：跑起来后从外部截图，证明窗口真的出现了）
    // -----------------------------------------------------------------------
    private static int RunSelfTestUi(Logger log)
    {
        var sink = InstallProgressWindow.TryCreate();
        if (sink is null)
        {
            log.Error("SELFTEST-UI FAIL 无法创建进度窗口");
            return ExitFailed;
        }

        log.Info("SELFTEST-UI OK 进度窗口已创建");
        sink.Start("正在安装 SimOCR（自检）", "自检：模拟解压", 100);
        for (var i = 1; i <= 100; i++)
        {
            Thread.Sleep(80);
            sink.Report(i, 100, $"自检文件 {i:D3}/100：SimOCR.runtime.dll");
            if (sink.Cancelled)
            {
                log.Warn("SELFTEST-UI 用户点了取消");
                break;
            }
        }

        sink.Stop();
        log.Info("SELFTEST-UI DONE");
        return ExitOk;
    }

    // -----------------------------------------------------------------------
    // 卸载
    // -----------------------------------------------------------------------
    private static int RunUninstall(CommandLine commandLine, InstallLayout layout, Logger log)
    {
        var service = new UninstallService(layout, log);
        var outcome = service.Prepare(commandLine.Silent, commandLine.PurgeData, out var detail);
        log.Info($"EVENT uninstall-outcome {outcome}");

        if (outcome == UninstallOutcome.Failed)
        {
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(
                    $"{detail}\n\n日志：{log.Path}", "卸载 SimOCR", NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            }

            return ExitNeedsUser;
        }

        if (outcome == UninstallOutcome.Cancelled)
        {
            return ExitCancelled;
        }

        // ---- --purge-data：先把数据目录删掉并查证，再决定怎么向用户汇报 ----
        // 顺序是关键：① 释放本进程的日志句柄（它就开在 <DataDir>\logs 里，
        // FileShare.Read 意味着"能读不能删"，句柄活着 rmdir 必然失败）；
        // ② 同步删除并查证（失败必须可见，见 PurgeDataDirectory）；
        // ③ 之后所有日志走 %TEMP% 里的继任文件，因为原位置正是我们要删的东西。
        var purged = true;
        if (commandLine.PurgeData)
        {
            log = service.ReleaseLogHandle(log);
            // 关键：把新日志交给 service。否则 purge/schedule 的日志会写进已 Dispose
            // 的旧实例而被静默丢弃（实测：失败原因一行都留不下来）。
            service.AttachLog(log);
            purged = service.PurgeDataDirectory();
            log.Info($"EVENT uninstall-purge-data ok={purged}");
        }

        if (!commandLine.Silent)
        {
            var tail = purged
                ? "\n\n如果以后还需要，重新双击安装包即可。"
                : $"\n\n注意：用户数据目录没有删掉，仍在 {layout.DataDir}。"
                  + "\n请在退出占用它的程序后手动删除。";
            NativeMethods.MessageBox(
                $"{detail}{tail}",
                "卸载 SimOCR",
                NativeMethods.MB_OK | (purged ? NativeMethods.MB_ICONINFORMATION : NativeMethods.MB_ICONWARNING));
        }

        // 重要：删除目录必须排在这个消息框之后 ——
        // 我们自己的 exe 就在安装目录里，没退出就删不掉。
        var scheduled = service.ScheduleRemoval(commandLine.PurgeData);

        // 退出码：数据目录的删除已经在上面同步查证过（修复点），
        // 它失败就返回 ExitFailed —— 老代码这里无条件 ExitOk，所以"数据目录静默存活"。
        if (!purged || !scheduled)
        {
            var hint = purged
                ? "安排安装目录删除失败"
                : $"用户数据目录删除失败，仍在 {layout.DataDir}";
            log.Error($"卸载未完全成功：{hint}；日志：{log.Path}");
            if (!commandLine.Silent)
            {
                NativeMethods.MessageBox(
                    $"卸载未完全成功：\n\n{hint}\n\n日志：{log.Path}",
                    "卸载 SimOCR",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            }

            log.Info($"===== 退出 exitCode={ExitFailed} 日志={log.Path} action=uninstall =====");
            log.Dispose();
            return ExitFailed;
        }

        log.Info($"===== 退出 exitCode={ExitOk} 日志={log.Path} action=uninstall =====");
        log.Dispose();
        return ExitOk;
    }
}
