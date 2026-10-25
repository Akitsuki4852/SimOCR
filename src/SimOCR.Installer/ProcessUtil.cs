using System.Diagnostics;

namespace SimOCR.Installer;

/// <summary>进程相关的小工具：判断主程序是否正在运行、必要时把它请出去。</summary>
internal static class ProcessUtil
{
    /// <summary>
    /// 找出"正在运行、且可执行文件就在安装目录里"的 SimOCR 进程。
    /// 只认路径，避免误伤用户从别处（比如便携 zip）跑的另一个 SimOCR。
    /// </summary>
    internal static List<Process> FindRunningFrom(string installDir)
    {
        var found = new List<Process>();
        var root = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcessesByName("SimOCR"))
        {
            try
            {
                var imagePath = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(imagePath))
                {
                    continue;
                }

                if (Path.GetFullPath(imagePath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process);
                }
            }
            catch (Exception)
            {
                // 访问不了 MainModule 的进程（权限/已退出）直接忽略
                process.Dispose();
            }
        }

        return found;
    }

    /// <summary>结束这些进程；返回成功结束的数量。</summary>
    internal static int Stop(IEnumerable<Process> processes)
    {
        var stopped = 0;
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    stopped++;
                    continue;
                }

                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
                stopped++;
            }
            catch (Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return stopped;
    }

    /// <summary>
    /// 「应用在运行吗」这个状态机的唯一一份实现。
    ///
    /// 安装（<see cref="InstallService.EnsureAppNotRunning"/>）与卸载
    /// （<see cref="UninstallService.Prepare"/>）原先各写了一遍同样的四步：
    ///   ① <see cref="FindRunningFrom"/> 按**可执行文件路径**找本安装目录里的主程序；
    ///   ② 静默模式：既不弹窗也不结束进程，直接把句柄 Dispose 掉并返回"被挡住"；
    ///   ③ 交互模式：弹一次「现在自动退出吗？」的 Yes/No；
    ///   ④ 点「是」→ <see cref="Stop"/> + 记日志 + 等 500ms 让文件锁释放。
    ///
    /// 两份唯一的差别是**结果语义**（0.10.2 审查的原话）：
    ///   * 安装：静默被挡住 = **取消**（用户没做错事，只是得先退出再装）；
    ///   * 卸载：静默被挡住 = **失败**（目录删不掉，卸载目标没达成）。
    /// 这个差别用 <paramref name="silentBlocked"/> 参数表达，**不再靠两份实现各写一遍**；
    /// 判据（找谁 / 什么时候弹窗 / 弹什么 / 怎么结束）因此只可能改一处。
    /// </summary>
    /// <param name="installDir">安装目录（只认这个目录里的 SimOCR 进程）。</param>
    /// <param name="silent">静默模式：不弹窗、不自动结束进程。</param>
    /// <param name="log">日志（调用方已经 Attach 过自己的那份）。</param>
    /// <param name="title">消息框标题。</param>
    /// <param name="question">消息框正文。</param>
    /// <param name="silentBlocked">静默模式下被挡住时返回哪个结果（安装=取消 / 卸载=失败）。</param>
    /// <param name="cancelled">用户点「否」时返回的结果（两边都是"取消"，但语义词不同）。</param>
    /// <param name="silentDetail">静默被挡住时回填的说明。</param>
    /// <param name="cancelledDetail">用户取消时回填的说明。</param>
    /// <returns>调用方该以哪个结果收场。</returns>
    internal static RunningAppAction EnsureAppNotRunning(
        string installDir,
        bool silent,
        Logger log,
        string title,
        string question,
        RunningAppAction silentBlocked,
        RunningAppAction cancelled,
        string silentDetail,
        string cancelledDetail,
        out string detail)
    {
        detail = string.Empty;
        var running = FindRunningFrom(installDir);
        if (running.Count == 0)
        {
            return RunningAppAction.Continue;
        }

        log.Warn($"检测到 {running.Count} 个正在运行的 SimOCR（来自安装目录 {installDir}）");

        if (silent)
        {
            DisposeAll(running);
            detail = silentDetail;
            return silentBlocked;
        }

        if (NativeMethods.MessageBox(
                question,
                title,
                NativeMethods.MB_YESNO | NativeMethods.MB_ICONWARNING) != NativeMethods.IDYES)
        {
            DisposeAll(running);
            detail = cancelledDetail;
            log.Info(detail);
            return cancelled;
        }

        var stopped = Stop(running);
        log.Info($"已结束 {stopped} 个 SimOCR 进程");
        Thread.Sleep(500);
        return RunningAppAction.Continue;
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// 「应用在运行吗」状态机的结果。三种结果对应调用方的两种语义映射：
/// **安装**把 <see cref="SilentBlocked"/> 当"取消"、**卸载**把它当"失败"。
/// </summary>
internal enum RunningAppAction
{
    /// <summary>没在运行，或已经在用户同意后结束掉 —— 继续。</summary>
    Continue,

    /// <summary>用户明确选择不继续（点了「否」）。</summary>
    Cancelled,

    /// <summary>静默模式下检测到在运行：不弹窗、不自动结束进程。</summary>
    SilentBlocked,
}
