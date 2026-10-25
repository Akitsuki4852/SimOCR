using System.Diagnostics;

namespace SimOCR.Installer;

internal enum UninstallOutcome
{
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// 卸载：删快捷方式、删"应用和功能"里的注册表项，然后<b>延迟</b>删除安装目录。
///
/// 为什么要延迟删目录：卸载器自己就躺在安装目录里，Windows 不允许删除正在运行的可执行文件。
/// 标准做法是另起一个脱离的 cmd，等本进程退出后再 rmdir /s /q。
/// 因此卸载分成两步：Prepare()（可交互）→ 调用方收尾 → ScheduleRemoval()。
/// </summary>
internal sealed class UninstallService
{
    private readonly InstallLayout _layout;
    private Logger _log;

    internal UninstallService(InstallLayout layout, Logger log)
    {
        _layout = layout;
        _log = log;
    }

    /// <summary>
    /// 换用另一份日志（<see cref="Program"/> 在 <see cref="ReleaseLogHandle"/> 之后调用）。
    ///
    /// 为什么必须换：本类在构造时就抓住了当时的 Logger；而 <c>--purge-data</c> 会把
    /// 那份日志 <see cref="Logger.Dispose"/> 掉、另开一份到 %TEMP%。不换的话，
    /// 之后所有日志都写进已 Dispose 的实例（<c>_disposed</c> 为真，静默丢弃）——
    /// 实测就踩到了：数据目录删除失败的全部证据行凭空消失，只剩 Program 自己写的那几行。
    /// </summary>
    internal void AttachLog(Logger log) => _log = log;

    /// <summary>确认、请走正在运行的主程序、删快捷方式与注册表项。</summary>
    internal UninstallOutcome Prepare(bool silent, bool purgeData, out string detail)
    {
        detail = string.Empty;

        if (!silent)
        {
            var message = new StringBuilder()
                .AppendLine("确定要卸载 SimOCR 吗？")
                .AppendLine()
                .AppendLine($"程序目录：{_layout.InstallDir}")
                .AppendLine(purgeData
                    ? "用户配置与日志（%LOCALAPPDATA%\\SimOCR）也会一并删除。"
                    : "用户配置与日志（%LOCALAPPDATA%\\SimOCR）会保留。")
                .ToString();

            if (NativeMethods.MessageBox(message, "卸载 SimOCR", NativeMethods.MB_YESNO | NativeMethods.MB_ICONWARNING)
                != NativeMethods.IDYES)
            {
                detail = "已取消卸载。";
                _log.Info(detail);
                return UninstallOutcome.Cancelled;
            }
        }

        // 正在运行的主程序必须先退出，否则目录删不掉。
        // 状态机本体在 ProcessUtil.EnsureAppNotRunning（与安装**共用一份**）；
        // 卸载这一档的结果语义是：静默被挡住 = **失败**（目录删不掉，卸载目标没达成）、
        // 用户点「否」= **取消**。安装那边把"被挡住"当取消 —— 差别用参数表达，判据只有一处。
        var runningAction = ProcessUtil.EnsureAppNotRunning(
            _layout.InstallDir,
            silent,
            _log,
            title: "卸载 SimOCR",
            question: "SimOCR 正在运行，需要先退出它才能卸载。\n\n现在自动退出并继续吗？",
            silentBlocked: RunningAppAction.SilentBlocked,
            cancelled: RunningAppAction.Cancelled,
            silentDetail: "SimOCR 正在运行，静默卸载不会自动结束进程。",
            cancelledDetail: "已取消卸载：SimOCR 仍在运行。",
            out var runningDetail);

        if (runningAction == RunningAppAction.SilentBlocked)
        {
            detail = runningDetail;
            _log.Warn(detail);
            return UninstallOutcome.Failed;
        }

        if (runningAction == RunningAppAction.Cancelled)
        {
            detail = runningDetail;
            return UninstallOutcome.Cancelled;
        }

        ShortcutService.Delete(_layout, _log);
        UninstallRegistry.Delete(_log, _layout.InstallDir);
        InstallRegistry.Delete(_log, _layout.InstallDir);
        AppPathsRegistry.Delete(_log, _layout.InstallDir);

        detail = "SimOCR 已卸载。";
        _log.Info("EVENT uninstall-prepared");
        return UninstallOutcome.Completed;
    }

    // -----------------------------------------------------------------------
    // 删用户数据目录（--purge-data）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 关闭安装器自己的日志句柄，并把它安顿到 %TEMP% 里的继任日志。
    ///
    /// 为什么必须先关：日志在 <c>&lt;DataDir&gt;\logs\installer-*.log</c>，而
    /// <see cref="Logger"/> 用 <c>FileShare.Read</c> 打开（<b>没有</b> FileShare.Delete）。
    /// 只要这个句柄还在，数据目录的 rmdir 一定失败 —— 实测：「循环退出码 0、
    /// 安装目录已删、数据目录仍在」。所以删数据目录的第一步是
    /// <b>放弃</b>这份句柄，再在新位置另起一份，保证后半程的失败仍然有地方写。
    /// </summary>
    internal Logger ReleaseLogHandle(Logger log)
    {
        var previous = log.Path;
        log.Dispose();

        var recovery = Logger.CreateInTemp();
        recovery.Warn($"日志已从即将删除的数据目录移出：{previous}");
        recovery.Info(
            "继续写这里；如果要保留这份日志，请在本进程结束后把它从 %TEMP% 复制出去"
            + "（%TEMP% 是临时目录，系统可能清理它）。");
        return recovery;
    }

    /// <summary>
    /// 现在就删掉数据目录。返回 true = 目录已经不存在。
    ///
    /// 为什么改成"同步删 + 查证"，而不是继续交给那个脱离的批处理：
    /// 批处理是分离并发的（<c>Process.Start</c> 之后本进程立刻往下走，甚至先弹"已卸载"
    /// 对话框），它的结果<b>无法</b>回传到本进程的退出码；而当时实测的老行为正是
    /// 「循环 exit 0、数据目录仍在、无任何失败记录」。同步删才能：
    ///   ① 在句柄释放之后立刻删（本进程不再挡自己）；
    ///   ② 用 <c>Directory.Exists</c> 查证结果，而不是相信一个只查安装目录的判据；
    ///   ③ 失败时写明确日志并让退出码非 0。
    /// 安装目录仍然只能交给那个批处理（卸载器 exe 自己就在里面）。
    /// </summary>
    internal bool PurgeDataDirectory()
    {
        var data = _layout.DataDir.TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(data))
        {
            _log.Info($"数据目录本来就不存在：{data}");
            return true;
        }

        _log.Info($"开始删除数据目录：{data}");

        // 重试是为了"主程序刚刚被请走、句柄还没完全释放"这种瞬时占用：
        // Prepare 里 Stop() 之后只等了 500 ms，剩下的交给这里。
        const int attempts = 20;
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (!Directory.Exists(data))
            {
                _log.Info(
                    $"EVENT purge-data ok=True path={data} attempts={attempt}"
                    + (attempt > 1 ? $"（前 {attempt - 1} 次被占用）" : string.Empty));
                return true;
            }

            if (attempt == 1)
            {
                // 第一次就知道结局：要么是瞬时占用（下面重试），要么是有别人拿着句柄
                _log.Warn($"数据目录暂时删不掉（{last?.GetType().Name}），最多重试 {attempts} 次");
            }

            if (attempt == attempts || attempt == 1)
            {
                // 把到底为什么删不掉写清楚：失败原因不该只留在代码推理里
                _log.Warn(
                    $"删除数据目录第 {attempt} 次失败：{last?.GetType().Name}: {last?.Message}"
                    + $"（仍在={Directory.Exists(data)}）");
            }

            Thread.Sleep(250);
        }

        // 失败必须留痕：这正是其中"静默存活"的反面
        _log.Error(
            $"EVENT purge-data ok=False path={data} attempts={attempts} error="
            + $"{last?.GetType().Name}: {last?.Message}");
        _log.Error($"用户数据目录删除失败，仍在：{data}（请手动删除，或退出占用它的程序后重试）");
        return false;
    }

    /// <summary>
    /// 另起一个脱离的 cmd，等本进程退出后删除安装目录（<c>--purge-data</c> 时数据目录也带上，
    /// 作为兜底）。
    ///
    /// 命令是"重试循环"而不是"睡固定几秒再删"：卸载器自己就在要删的目录里，
    /// Windows 不允许删除正在运行的可执行文件，而我们无法预知调用方何时退出
    /// （比如"已卸载"消息框可能被晾在那里很久）。循环最多重试 30 次，每次之间
    /// 用 <c>ping 127.0.0.1</c> 拖约 1 秒（实测本机回环：一轮 ≈1.03 s，30 轮 ≈31 s）。
    ///
    /// 成功判据（修复点）：<b>安装目录和数据目录都得消失</b>才 exit 0。
    /// 老写法只查安装目录，于是"数据目录仍在"被当成成功、静默通过。
    ///
    /// 为什么写成临时 .cmd 文件而不是 <c>cmd /c "&lt;一整行&gt;"</c>：
    /// 判据要的是"两个目录都查"+"失败立刻退 3"+"成功才退 0"，直写成一行时
    /// cmd 的括号解析会把复合语句吃进 for 的循环体（实测：`if (...exit /b 0)`
    /// 只跑 1 圈；`goto`/`if defined` 组合甚至一次 rmdir 都不执行）。落到带标签的
    /// .cmd 里以后，实测三种场景都对：都删掉 → 0；数据目录被占 → 31 秒后 3；
    /// 安装目录被占（本进程自己的 exe）→ 31 秒后 3。
    ///
    /// 时序（为了不白等 31 秒）：先跑一份"两个目录都删"的脚本，并<b>只等它几秒</b>；
    /// 等到了就直接汇报结果。等不到说明安装目录被本进程自己的 exe 占着（这是
    /// 正常卸载路径），于是收回等待，另起一份<b>只删安装目录</b>的脱离脚本来干这件事。
    ///
    /// 数据目录的删除在 <see cref="PurgeDataDirectory"/> 里已经同步做过一次并查证过；
    /// 这里的 rmdir 是"兜底"——同步删失败（当时还有别的进程占着）时，
    /// 等一下之后很可能就删得掉了。
    ///
    /// 返回 false = 连兜底都没起来（cmd 启动失败，或临时 .cmd 写不出来）。
    /// </summary>
    internal bool ScheduleRemoval(bool purgeData)
    {
        var install = _layout.InstallDir.TrimEnd(Path.DirectorySeparatorChar);
        var data = _layout.DataDir.TrimEnd(Path.DirectorySeparatorChar);

        var firstPassSeconds = 5;
        var result = StartCleanup(BuildCleanupScript(install, data, purgeData), firstPassSeconds);
        if (!result.Started)
        {
            return false;
        }

        // 第一轮等到了、而且两个目录都真的没了：直接汇报
        if (result.ExitCode == 0)
        {
            var dataThereNow = Directory.Exists(data);
            _log.Info("EVENT uninstall-scheduled-result exit=0");
            // 只报"真的没了"的目录：脚本退 0 就是两个判据都过了，但日志不该比查证更乐观
            _log.Info(!Directory.Exists(install)
                ? (purgeData && !dataThereNow ? "安装目录与数据目录均已删除" : "安装目录已删除")
                : "目录已删除");
            _log.Info(purgeData
                ? "用户数据目录的删除是按删除结果查证的（--purge-data）"
                : "用户数据目录保留");
            return true;
        }

        if (result.ExitCode is null)
        {
            // 第一轮还在跑：原因基本只有一个 —— 安装目录里开着本进程自己的 exe，
            // 它要等本进程退出。别在这里耗满 31 秒：收回等待，交一份只删安装目录的脚本。
            _log.Info(
                "清理脚本仍在重试（多半是安装目录里还开着本进程的 exe），"
                + "本进程不再等待，另起一份只删安装目录的脱离脚本");
            var detached = StartCleanup(BuildInstallOnlyCleanupScript(install, data, purgeData), null);
            if (!detached.Started)
            {
                return false;
            }
        }
        var installThere = Directory.Exists(install);
        var dataThere = Directory.Exists(data);

        // 数据目录的最终判据（核心）：只要 --purge-data 给了、它还在，就是失败。
        // 这里必须再查一次：同步那一步可能失败，而这个脱离脚本的结果没人能回传。
        if (purgeData && dataThere)
        {
            _log.Error(
                $"EVENT uninstall-scheduled-result data-dir-survived"
                + $"（安装目录存在={installThere}，数据目录存在={dataThere}，"
                + $"本进程在安装目录里={IsInsideOurProcessPath(install)}）");
            _log.Error($"目录删除失败：数据目录仍在 {data}（请手动删除）");
            return false;
        }

        if (installThere)
        {
            _log.Warn(
                $"EVENT uninstall-scheduled-result pending"
                + $"（安装目录存在={installThere}，本进程在安装目录里={IsInsideOurProcessPath(install)}）");
            _log.Warn("这是预期行为：脱离的脚本会在本进程退出后继续删安装目录");
        }
        else
        {
            _log.Info("EVENT uninstall-scheduled-result exit=0（安装目录已删除）");
        }

        _log.Info(purgeData
            ? "用户数据目录的删除是按删除结果查证的（--purge-data）"
            : "用户数据目录保留");
        return true;
    }

    /// <summary>启动一份清理脚本。<paramref name="waitSeconds"/> 为 null 时不等待（脱离）。</summary>
    private CleanupRun StartCleanup(CleanupScript script, int? waitSeconds)
    {
        var scriptPath = WriteCleanupScript(script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                // 必须用 Arguments（原样命令行）而不是 ArgumentList：
                // ArgumentList 会按 CRT 规则把内嵌的 " 转义成 \"，而 cmd.exe 不认这种转义，
                // 结果 rmdir 拿到一个带反斜杠的假路径、静默失败（实测踩到过）。
                // 临时目录路径可能带空格，所以这里手动补一对引号。
                Arguments = scriptPath is null ? "/c " + script.Command : "/c \"" + scriptPath + "\"",
            };

            var process = Process.Start(startInfo);
            _log.Info($"EVENT uninstall-scheduled cmd pid={process?.Id}");
            _log.Info(scriptPath is null
                ? $"已安排目录删除（临时脚本不可写，改用单行命令，失败时不会重试）：cmd /c {script.Command}"
                : $"已安排目录删除：cmd /c \"{scriptPath}\"");
            if (scriptPath is not null)
            {
                // 脚本内容进日志：以后要复盘"判据到底是什么"不用去翻 %TEMP%
                _log.Info("清理脚本内容：" + script.Text.Replace("\r\n", " | ").TrimEnd(' ', '|'));
            }

            if (process is null)
            {
                _log.Error("cmd 未能启动，安装目录/数据目录都不会被删除");
                return new CleanupRun(false, null);
            }

            if (waitSeconds is null)
            {
                return new CleanupRun(true, null);
            }

            var exited = process.WaitForExit(waitSeconds.Value * 1000);
            return new CleanupRun(true, exited ? process.ExitCode : null);
        }
        catch (Exception ex)
        {
            _log.Error(
                $"安排目录删除失败：{ex.GetType().Name} {ex.Message}，请手动删除 {_layout.InstallDir}");
            return new CleanupRun(false, null);
        }
    }

    private readonly record struct CleanupRun(bool Started, int? ExitCode);

    /// <summary>
    /// 本进程的可执行文件是不是就在安装目录里。
    ///
    /// 用来区分"目录删不掉"的两种原因：① 本进程自己还开着（预期，退出后循环会删掉）；
    /// ② 别人占着（要人工处理）。前者按警告记，后者按错误记。
    /// </summary>
    private static bool IsInsideOurProcessPath(string installDir)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
            {
                return false;
            }

            var root = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(self!).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private readonly record struct CleanupScript(string Text, string Command);

    /// <summary>
    /// 生成"删目录"批处理文本。
    ///
    /// 形状（实测过三种场景，见类注释）：
    ///   for %%i in (1..30):
    ///     rmdir 安装目录; rmdir 数据目录
    ///     两个都不存在 → goto done      ← 成功判据：两个都查
    ///     ping 约 1 秒                  ← 等占用者松手
    ///   exit /b 3                       ← 30 轮还没删净
    ///   :done / exit /b 0
    ///
    /// <paramref name="purgeData"/> 为 false 时不删数据目录（也不查它）：这是
    /// <c>--purge-data</c> 的 opt-in 语义，没给参数就绝不碰用户数据。
    /// </summary>
    private static CleanupScript BuildCleanupScript(string install, string data, bool purgeData)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine("for /l %%i in (1,1,30) do (");
        script.AppendLine($"  rmdir /s /q \"{install}\" >nul 2>&1");
        if (purgeData)
        {
            script.AppendLine($"  rmdir /s /q \"{data}\" >nul 2>&1");
        }

        script.AppendLine(purgeData
            ? $"  if not exist \"{install}\" if not exist \"{data}\" goto done"
            : $"  if not exist \"{install}\" goto done");
        script.AppendLine("  ping -n 2 127.0.0.1 >nul");
        script.AppendLine(")");
        script.AppendLine("endlocal & exit /b 3");
        script.AppendLine(":done");
        script.AppendLine("endlocal & exit /b 0");
        return new CleanupScript(script.ToString(), BuildInlineFallback(install, data, purgeData));
    }

    /// <summary>
    /// "脱离的那一份"要删的东西：先删安装目录，<b>再</b>删数据目录（顺序不能反，
    /// 不然安装目录里还开着的东西会顺带把数据目录的判据搅乱），判据同样是两个都要消失。
    ///
    /// 为什么不是第一次就删两个：从 %LOCALAPPDATA%\Programs\SimOCR\SimOCR-Uninstall.exe
    /// 卸载时，我们自己的 exe 正开在安装目录里 → 循环必然跑满 30 轮（实测 ≈31 s）才退 3。
    /// 要是那次就把数据目录一起带上，就得为了拿一个已知的失败结果白等 31 秒。
    /// 现在：数据目录由同步那一步负责（快、结果写进退出码），安装目录交给这一份。
    /// </summary>
    private static CleanupScript BuildInstallOnlyCleanupScript(string install, string data, bool purgeData)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine("for /l %%i in (1,1,30) do (");
        script.AppendLine($"  rmdir /s /q \"{install}\" >nul 2>&1");
        if (purgeData)
        {
            // 兜底：同步那一步没删掉数据目录时，这里再试（等 1 秒后占用者可能就松手了）
            script.AppendLine($"  rmdir /s /q \"{data}\" >nul 2>&1");
        }

        script.AppendLine(purgeData
            ? $"  if not exist \"{install}\" if not exist \"{data}\" goto done"
            : $"  if not exist \"{install}\" goto done");
        script.AppendLine("  ping -n 2 127.0.0.1 >nul");
        script.AppendLine(")");
        script.AppendLine("endlocal & exit /b 3");
        script.AppendLine(":done");
        script.AppendLine("endlocal & exit /b 0");
        return new CleanupScript(script.ToString(), BuildInlineFallback(install, data, purgeData));
    }

    /// <summary>
    /// 连脚本文件都写不出来时的最后退路（一行命令）。
    ///
    /// 只敢保证一件事：<b>绝不谎报成功</b>。cmd 的括号解析让"两个目录都查"的判据
    /// 在一行里无法可靠表达（实测 `if not exist A if not exist B (exit /b 0)` 在
    /// "A 没了、B 还在"时会错误地退 0 —— 那正是老毛病），所以
    /// <paramref name="purgeData"/> 为真时这里一律退 3：不确定就报失败，
    /// 让上层写日志、给非 0 退出码，由用户自己看一眼那个目录。
    /// 没给 <c>--purge-data</c> 时判据只有一个目录，一行是可靠的。
    /// </summary>
    private static string BuildInlineFallback(string install, string data, bool purgeData)
    {
        var inline = new StringBuilder();
        inline.Append("rmdir /s /q \"").Append(install).Append("\" >nul 2>&1");
        if (purgeData)
        {
            inline.Append(" & rmdir /s /q \"").Append(data).Append("\" >nul 2>&1");
            inline.Append(" & exit /b 3");
        }
        else
        {
            inline.Append(" & if exist \"").Append(install)
                .Append("\" (exit /b 3) else (exit /b 0)");
        }

        return inline.ToString();
    }

    /// <summary>
    /// 把清理脚本写到磁盘，返回路径；两处都写不出来返回 null。
    ///
    /// 先写 %TEMP%，写不进去再写安装目录（反正它马上就要被删掉，脚本跟着一起没）。
    /// 为什么不删掉这个 .cmd：cmd.exe 是按需重读批处理文件的，脚本运行中删自己
    /// 有机会读到半截内容；%TEMP% 那份交给系统清理，安装目录那份会被 rmdir 带走。
    /// </summary>
    private string? WriteCleanupScript(CleanupScript script)
    {
        var errors = new List<string>();

        foreach (var directory in new[] { Path.GetTempPath(), _layout.InstallDir })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"simocr-cleanup-{Environment.ProcessId}.cmd");
                // 纯 ASCII 写盘：cmd.exe 会把 UTF-8 BOM 当成命令的一部分（脚本里只有
                // @echo off / rmdir / for / if / ping / exit 这些 ASCII 记号）
                File.WriteAllText(path, script.Text, Encoding.ASCII);
                return path;
            }
            catch (Exception ex)
            {
                errors.Add($"{directory}: {ex.GetType().Name} {ex.Message}");
            }
        }

        _log.Warn("临时清理脚本写不出来（" + string.Join("；", errors) + "），改用单行命令（没有重试窗口）");
        return null;
    }
}
