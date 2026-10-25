using System.IO.Compression;

namespace SimOCR.Installer;

internal enum InstallOutcome
{
    /// <summary>本次真的解压写入了一份新版本。</summary>
    Installed,

    /// <summary>目标目录里已经是同一份 payload，跳过解压。</summary>
    UpToDate,

    /// <summary>用户取消（或静默模式下需要用户介入）。</summary>
    Cancelled,

    /// <summary>失败，detail 里是原因。</summary>
    Failed,
}

internal sealed class InstallRequest
{
    /// <summary>即使标记一致也强制重新解压。</summary>
    internal bool Force { get; init; }

    /// <summary>不弹任何对话框（无人值守 / 验收用）。</summary>
    internal bool Silent { get; init; }

    internal bool CreateShortcuts { get; init; } = true;

    internal bool WriteRegistry { get; init; } = true;

    /// <summary>显示原生进度对话框。</summary>
    internal bool ShowProgress { get; init; } = true;
}

/// <summary>
/// 安装的真正实现：把嵌入的便携 zip 解压到安装目录、释放卸载器、写标记、建快捷方式。
/// </summary>
internal sealed class InstallService
{
    private readonly InstallLayout _layout;
    private readonly Logger _log;

    internal InstallService(InstallLayout layout, Logger log)
    {
        _layout = layout;
        _log = log;
    }

    internal InstallOutcome EnsureInstalled(InstallRequest request, out string detail)
    {
        detail = string.Empty;

        using var payload = PayloadReader.OpenPayload();
        if (payload is null)
        {
            detail = "这个安装包不完整（缺少程序本体），请重新下载后再运行。";
            _log.Error("payload 缺失，拒绝安装");
            return InstallOutcome.Failed;
        }

        var version = PayloadReader.SelfVersion;
        var payloadSize = payload.Length;
        var payloadHash = PayloadReader.ComputeSha256(payload);
        _log.Info($"payload: {payloadSize / 1048576.0:F2}MB sha256={payloadHash} version={version}");
        _log.Info($"安装目录: {_layout.InstallDir}");

        var marker = MarkerFile.TryRead(_layout.MarkerPath);
        if (!request.Force && marker is not null &&
            string.Equals(marker.PayloadSha256, payloadHash, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(_layout.ExePath))
        {
            _log.Info($"目标目录已是同一份 payload（安装于 {marker.InstalledUtc:u}），跳过解压");
            _log.Info($"EVENT install-skip-uptodate version={marker.Version} files={marker.FileCount}");
            detail = $"已经是 {marker.Version}，无需重新安装";
            return InstallOutcome.UpToDate;
        }

        // 更新前必须让旧版本退出，否则 exe/dll 被占用导致解压失败
        if (!EnsureAppNotRunning(request, out detail))
        {
            return InstallOutcome.Cancelled;
        }

        try
        {
            Directory.CreateDirectory(_layout.InstallDir);
        }
        catch (Exception ex)
        {
            detail = BuildDirectoryFailureMessage(ex);
            _log.Error(detail);
            return InstallOutcome.Failed;
        }

        var progress = BuildProgressSink(request);
        var outcome = Extract(payload, payloadSize, progress, out var fileCount, out var totalBytes, out detail);
        progress.Stop();
        if (outcome != InstallOutcome.Installed)
        {
            return outcome;
        }

        if (!PayloadReader.ExtractUninstaller(_layout.UninstallerPath))
        {
            _log.Warn("卸载器资源缺失，'应用和功能'里的卸载入口将不可用");
        }
        else
        {
            _log.Info($"卸载器已释放: {_layout.UninstallerPath}");

            // 0.10.5：把卸载器本身也计进标记的 files/bytes —— 它会真的落在安装目录里，
            // 而标记记录的是"这次安装后安装目录应有的文件数"（--verify 用它做**精确**比对）。
            // 不这么算的话标记恒少 1 个文件，`--verify` 就永远无法用 == 判据。
            fileCount++;
            try
            {
                totalBytes += new FileInfo(_layout.UninstallerPath).Length;
            }
            catch (Exception ex)
            {
                _log.Warn($"卸载器大小读取失败（不影响安装）：{ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // 标记文件（0.10.5）
        //
        // 标记里的 files / bytes 是"**这次安装结束后安装目录里应有的文件数 / 总字节**"，
        // `--verify` 用它做**精确**（==）比对 —— 旧判据是 `>=`，只能发现"少装了很多"，
        // 发现不了"多了 / 少了几个"的部分安装。
        //
        // 难点在于标记文件**自己**也在安装目录里（它是 1 个文件），所以计数必须把它算进去；
        // 而"写进去了没有"只有写完才知道。做法：
        //   1) 先按已知内容写一次（先用临时计数），拿到"是否真的落盘"；
        //   2) 落盘成功 → 计数 +1、字节 +标记大小，再写一次把真实数字记下来；
        //   3) 落盘失败（只读目录等极端情况）→ 保持第一次的数字，安装流程继续
        //      （`Write` 本来就吞异常：写不进标记只影响"下次是否重新解压"）。
        // 为什么敢写两次：同一个路径、同一个进程、间隔毫秒级 —— 第一次能写进去，
        // 第二次一定也能（第二次只是把同一份内容换成新数字）。
        // -------------------------------------------------------------------
        MarkerFile BuildMarker() => new()
        {
            Version = version,
            PayloadSha256 = payloadHash,
            FileCount = fileCount,
            TotalBytes = totalBytes,
            InstalledUtc = DateTime.UtcNow,
            Shortcuts = request.CreateShortcuts,
            Registry = request.WriteRegistry,
        };

        if (BuildMarker().Write(_layout.MarkerPath))
        {
            fileCount++;
            try
            {
                totalBytes += new FileInfo(_layout.MarkerPath).Length;
            }
            catch (Exception ex)
            {
                _log.Warn($"标记文件大小读取失败（不影响安装）：{ex.Message}");
            }

            BuildMarker().Write(_layout.MarkerPath);
        }

        _log.Info($"EVENT install-marker-written path={_layout.MarkerPath} files={fileCount} bytes={totalBytes}");

        if (request.CreateShortcuts)
        {
            var shortcutsOk = ShortcutService.TryCreate(_layout, _log);
            _log.Info($"EVENT shortcuts ok={shortcutsOk}");
        }
        else
        {
            _log.Info("按参数要求跳过快捷方式");
        }

        if (request.WriteRegistry)
        {
            var registryOk = UninstallRegistry.Write(_layout, version, totalBytes, _log);
            _log.Info($"EVENT registry ok={registryOk}");

            var installKeyOk = InstallRegistry.Write(_layout, version, payloadHash, _log);
            _log.Info($"EVENT install-registry ok={installKeyOk}");

            var appPathsOk = AppPathsRegistry.Write(_layout, _log);
            _log.Info($"EVENT app-paths ok={appPathsOk}");
        }
        else
        {
            _log.Info("按参数要求跳过注册表卸载项");
        }

        detail = $"已安装 {version}：{fileCount} 个文件 / {totalBytes / 1048576.0:F1} MB";
        _log.Info($"EVENT install-complete version={version} files={fileCount} bytes={totalBytes}");
        _log.Info(detail);
        return InstallOutcome.Installed;
    }

    /// <summary>
    /// 把"建不了安装目录"翻译成用户能照着做的说明。
    /// 实测最容易被误判成"目录权限问题"的情况：exe 是从受限/沙箱上下文启动的，
    /// 进程连 <c>%LOCALAPPDATA%</c> 都写不进去（连日志文件都建不出来）。
    /// </summary>
    private string BuildDirectoryFailureMessage(Exception ex)
    {
        var text = new StringBuilder()
            .Append("无法创建安装目录 ").Append(_layout.InstallDir).Append('：').Append(ex.Message);

        if (ex is UnauthorizedAccessException)
        {
            _log.Info($"安装目录创建失败的进程身份：{ProcessIdentity.Describe()}");
            text.AppendLine().AppendLine()
                .AppendLine("常见原因（按可能性排序）：")
                .AppendLine("1) 这个 exe 是从受限/沙箱环境启动的（自动化工具、沙箱浏览器、界面里的文件卡片等），")
                .AppendLine("   进程没有写 %LOCALAPPDATA% 的权限。")
                .AppendLine("   → 请在资源管理器里直接双击这个 exe（或先把它复制到桌面/下载目录再双击）。")
                .AppendLine("2) 该目录已存在但当前账户没有写权限，或被安全软件锁定。")
                .AppendLine("   → 手动删除该目录后重试。")
                .AppendLine()
                .Append("详细诊断信息已写入安装日志（默认在 %LOCALAPPDATA%\\SimOCR\\logs，写不进去时在 %TEMP%）。");
            // 提示框会自己再附一行"日志：…"，这里不重复
        }

        return text.ToString();
    }

    private IProgressSink BuildProgressSink(InstallRequest request)    {
        var logSink = new LogProgressSink(_log, "解压");
        if (request.Silent || !request.ShowProgress)
        {
            return logSink;
        }

        var window = InstallProgressWindow.TryCreate();
        if (window is null)
        {
            _log.Warn("进度窗口创建失败，改为静默解压");
            return logSink;
        }

        _log.Info("进度窗口已显示");
        return new CompositeProgressSink(window, logSink);
    }

    /// <summary>解压便携 zip。返回 Installed 表示全部写完。</summary>
    private InstallOutcome Extract(
        Stream payload,
        long payloadSize,
        IProgressSink progress,
        out int fileCount,
        out long totalBytes,
        out string detail)
    {
        fileCount = 0;
        totalBytes = 0;
        detail = string.Empty;

        var root = Path.GetFullPath(_layout.InstallDir)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        try
        {
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var grandTotal = entries.Sum(e => e.Length);

            progress.Start("正在安装 SimOCR", $"解压 {entries.Count} 个文件", grandTotal);
            _log.Info($"EVENT install-extract-start files={entries.Count} bytes={grandTotal}");

            long done = 0;
            foreach (var entry in entries)
            {
                if (progress.Cancelled)
                {
                    detail = "安装已取消";
                    _log.Warn($"用户取消解压（已写 {fileCount} 个文件）");
                    _log.Info($"EVENT install-extract-cancelled files={fileCount}");
                    return InstallOutcome.Cancelled;
                }

                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relative));

                // 防目录穿越：zip 里出现 ..\..\ 这类路径时直接跳过
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Warn($"跳过异常的压缩包路径：{entry.FullName}");
                    continue;
                }

                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                entry.ExtractToFile(destination, overwrite: true);

                done += entry.Length;
                fileCount++;
                totalBytes += entry.Length;
                progress.Report(done, grandTotal, relative);
            }

            _log.Info($"解压完成：{fileCount} 个文件 / {totalBytes / 1048576.0:F1} MB（payload {payloadSize / 1048576.0:F2}MB）");
            _log.Info($"EVENT install-extract-done files={fileCount} bytes={totalBytes}");
            return InstallOutcome.Installed;
        }
        catch (IOException ex)
        {
            detail = $"{ex.Message}\n\n可能是 SimOCR 仍在运行、或杀毒软件正在扫描安装目录。请退出 SimOCR 后重试。";
            _log.Error($"解压失败(IO)：{ex.Message}");
            return InstallOutcome.Failed;
        }
        catch (UnauthorizedAccessException ex)
        {
            detail = $"没有写入权限：{ex.Message}";
            _log.Error($"解压失败(权限)：{ex.Message}");
            return InstallOutcome.Failed;
        }
        catch (InvalidDataException ex)
        {
            detail = $"安装包已损坏：{ex.Message}";
            _log.Error($"解压失败(数据)：{ex.Message}");
            return InstallOutcome.Failed;
        }
    }

    /// <summary>
    /// 确保安装目录里的 SimOCR 没在跑。返回 false 表示**用户选择不继续**。
    ///
    /// 状态机本体已合并进 <see cref="ProcessUtil.EnsureAppNotRunning"/>（安装与卸载共用一份），
    /// 这里只剩"安装这一档的结果语义" —— 静默被挡住 = **取消**（不是失败：用户没做错事，
    /// 只是得先退出再装），所以两种非 Continue 结果都映射成 false。
    /// </summary>
    private bool EnsureAppNotRunning(InstallRequest request, out string detail)
    {
        var action = ProcessUtil.EnsureAppNotRunning(
            _layout.InstallDir,
            request.Silent,
            _log,
            title: "SimOCR 安装",
            question: "SimOCR 正在运行，安装新版本需要先退出它。\n\n现在自动退出吗？",
            silentBlocked: RunningAppAction.SilentBlocked,
            cancelled: RunningAppAction.Cancelled,
            silentDetail: "SimOCR 正在运行，安装需要先退出它（静默模式下不会自动结束进程）。",
            cancelledDetail: "已取消：SimOCR 仍在运行。",
            out detail);
        return action == RunningAppAction.Continue;
    }
}
