using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SimOCR.Diagnostics;

/// <summary>
/// 自检共用的 Dispatcher 泵。
///
/// 收敛前这套东西在 `Diagnostics/` 里有 **5 份**（`SelfTest.Pump`、`SelfTestSnap.Pump(int)` /
/// `Pump(Dispatcher)` / `WaitUntil`、`SelfTestSnapOcclusion.Pump` ×2、`SelfTestPinClose.Pump`、
/// `TableAcceptance.WaitUntil` / `WaitFor&lt;T&gt;` / `Pump`），各写一遍同一段
/// `DispatcherFrame + DispatcherTimer` 循环，节拍还各不相同（50 / 100 / 120ms）。
///
/// 为什么必须是"泵"而不是 `Thread.Sleep`：WPF 的窗口消息（`Loaded`、布局、渲染、
/// `PostMessage` 投递过来的 `WM_*`）只有跑消息循环才会被派发 —— `DispatcherPriority.Background`
/// 的 `Invoke` 会 `PushFrame`，正好把"比 Background 更高优先级"的任务跑完。
///
/// 节拍统一取 **50ms**（原先最细的那一档）：判据是**状态**不是事件，查得更频繁只会让"超时"
/// 更接近真实时刻，不会漏掉任何一条断言。
/// </summary>
internal static class UiPump
{
    /// <summary>轮询节拍（ms）：取原先五份实现里最细的一档。</summary>
    private const int TickMs = 50;

    /// <summary>把比 <see cref="DispatcherPriority.Background"/> 更高优先级的任务跑完。</summary>
    internal static void UntilIdle(Dispatcher dispatcher)
        => dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    /// <summary>泵 <paramref name="milliseconds"/> 毫秒（期间 UI 保持响应）。</summary>
    internal static void For(int milliseconds) => Until(() => false, milliseconds);

    /// <summary>泵到 <paramref name="condition"/> 成立或超时。</summary>
    internal static void Until(Func<bool> condition, int timeoutMs)
    {
        var frame = new DispatcherFrame();
        DispatcherTimer? timer = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(TickMs),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (condition() || DateTime.UtcNow > deadline)
                {
                    timer!.Stop();
                    frame.Continue = false;
                }
            },
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>泵到 <paramref name="probe"/> 探到非 null 的值（或超时），返回最后探到的那个值。</summary>
    internal static T? Value<T>(Func<T?> probe, int timeoutMs)
        where T : class
    {
        T? result = null;
        Until(() => (result = probe()) is not null, timeoutMs);
        return result;
    }
}

/// <summary>
/// 自检 scratch 目录的健壮化 + "环境问题"与"产品缺陷"分码。
///
/// 背景：<see cref="SelfTestEvidence.Ensure"/> 过去直接
/// <c>Directory.CreateDirectory(directory)</c>，建 / 删目录**都没有 try/catch** ——
/// 任何环境问题（%TEMP% 不可写、被 ACL 挡住、路径被独占）都会变成**未捕获异常** →
/// 自检收尾报「自检运行异常」→ **exit 2**，日志里**一条断言输出都没有**。
/// 这与三态语义不一致：环境问题本该是 `[用例不成立]`（`exit 5`）+ 写明原因，不是"产品崩了"。
///
/// 现在的口径（两步降级，任何一步都不改判据）：
///   ① 先试**主目录**（`%TEMP%\SimOCR-selftest…`）；失败则
///   ② 降级到**带唯一后缀的目录**（`%TEMP%\SimOCR-selftest-fallback-&lt;pid&gt;-&lt;guid&gt;`），
///      它用的是同一套 `%TEMP%` 规则、但**不依赖**原目录的任何权限；
///   ③ 两步都失败 → 记下原因并让 <see cref="SelfTestEvidence.Ensure"/> 返回 <c>null</c>；
///      调用方据此立即返回 **exit 5** 并写明原因（**绝不**伪装成 exit 2）。
///
/// 判据：日志里出现 `[用例不成立] 自检 scratch 目录建不出来` ⇔ 走的是环境降级 / 失败分支。
/// </summary>
internal static class SelfTestScratch
{
    /// <summary>最近一次目录降级 / 失败的原因（成功时为空串）。</summary>
    internal static string LastFailureReason { get; private set; } = string.Empty;

    /// <summary>本次进程里是否发生过降级或失败（用于日志与记账）。</summary>
    internal static bool Degraded { get; private set; }

    internal static void RecordDegraded(string reason)
    {
        Degraded = true;
        LastFailureReason = reason;
    }

    /// <summary>把"建不出来"的原因整理成一行可直接写进日志的文案。</summary>
    internal static string Describe(string primary, Exception primaryError) =>
        $"自检 scratch 目录建不出来：主目录「{primary}」失败（{primaryError.GetType().Name}: {primaryError.Message}），"
        + "降级目录也失败 —— 这是**环境问题**（%TEMP% 不可写 / ACL / 路径被占用），不是产品缺陷；"
        + "本用例按「用例不成立」记 exit 5。";

    /// <summary>`%TEMP%` 下的唯一降级目录（只算路径，不负责创建）。</summary>
    internal static string FallbackDirectory(string leaf) =>
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"SimOCR-selftest-fallback-{Environment.ProcessId}-{Guid.NewGuid():N}",
            leaf);

    /// <summary>建目录并确认它真的可写。返回 null = 不可用。</summary>
    internal static string? TryUse(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            return DirectoryIsUsable(directory) ? directory : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 目录存在且可用（存在 + 能取到句柄）。
    ///
    /// ⚠️ **0.10.8 实测记录（不要改回"写一个探针文件再删掉"）**：本方法的初版用
    /// <c>File.WriteAllText</c> 写一个 `.write-probe-&lt;guid&gt;` 再删掉来验证"真的能写"。
    /// 那样写在**调用它的那台机器 / 那个沙箱里**会让紧接着的 `Directory.Exists` 返回 false，
    /// 于是自检的 scratch 目录在 100~200ms 内"消失"，`Directory.GetFiles` 直接抛
    /// `DirectoryNotFoundException`（`fixture-sort-tri-state` 因此在门禁里整条红）。
    /// 具体机制未定位（`Directory.Exists` 在创建后 165ms 内返回 false，而事后磁盘上目录确实在），
    /// 因此这里**退回"只 CreateDirectory + 存在性检查"**：够用（`Ensure` 的调用方紧接着就会自己
    /// 建一次目录并写文件），且不会引入那类副作用。**降级与 exit 5 的语义不变**。
    /// </summary>
    private static bool DirectoryIsUsable(string directory) => System.IO.Directory.Exists(directory);
}

/// <summary>
/// 自检/验收的证据产物（窗口截图、尺寸描述、新文件 diff）的唯一一份实现。
/// 收敛掉的重复：`SelfTest.SaveWindowShot` 与 `SelfTestSnap.SaveEvidence`（两份"把窗口自己渲染成
/// PNG"）、两份 `DescribePng`、散在 6 处的证据目录字面量、以及"运行后多出哪些 PNG"这个 diff。
///
/// 口径：证据图是**本机临时产物**，一律放 `%TEMP%` 下，不依赖任何仓库的目录布局 ——
/// 与开源仓库的实现保持一致，同步时本文件可整份复制。
/// </summary>
internal static class SelfTestEvidence
{
    private const string AcceptanceFolder = "SimOCR-acceptance";
    private const string SelfTestFolder = "SimOCR-selftest";

    /// <summary>`--accept-*` 系列的证据目录（`%TEMP%\SimOCR-acceptance`），建目录并写一行日志。
    /// **建不出来时返回 null**（调用方据此记 exit 5 并写明原因）。</summary>
    internal static string? AcceptanceDirectory(LogService? log = null)
    {
        var directory = Ensure(System.IO.Path.Combine(System.IO.Path.GetTempPath(), AcceptanceFolder));
        if (directory is null)
        {
            log?.Error($"  [用例不成立] {SelfTestScratch.LastFailureReason}");
            return null;
        }

        log?.Info($"  [证据] 输出目录：{directory}");
        return directory;
    }

    /// <summary>自检的固定产物目录（`%TEMP%\SimOCR-selftest[\子目录]`）。
    /// **建不出来时返回 null**（调用方据此记 exit 5 并写明原因）。</summary>
    internal static string? SelfTestDirectory(string? sub = null)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), SelfTestFolder);
        return sub is null ? Ensure(root) : Ensure(System.IO.Path.Combine(root, sub));
    }

    /// <summary>
    /// 确保目录存在并返回它；**失败返回 null**（不再把"建不出来"变成未捕获异常）。
    /// 降级顺序：主目录 → `%TEMP%` 下带唯一后缀的目录 → null。
    /// </summary>
    internal static string? Ensure(string directory)
    {
        var primary = SelfTestScratch.TryUse(directory);
        if (primary is not null)
        {
            return primary;
        }

        // 复现一次主目录的失败原因：TryUse 已经把异常吞掉了，这里只为把原因写清楚。
        Exception primaryError;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            primaryError = new IOException("目录创建后仍不可写（只读 / ACL 拒绝写入）");
        }
        catch (Exception ex)
        {
            primaryError = ex;
        }

        // 第一步降级：同盘、同 %TEMP% 规则，但路径唯一且不依赖原目录的任何权限。
        var leaf = System.IO.Path.GetFileName(directory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        var degraded = SelfTestScratch.TryUse(SelfTestScratch.FallbackDirectory(leaf));
        if (degraded is not null)
        {
            SelfTestScratch.RecordDegraded(
                $"主目录「{directory}」不可用（{primaryError.GetType().Name}: {primaryError.Message}），"
                + $"已降级到唯一目录「{degraded}」");
            return degraded;
        }

        SelfTestScratch.RecordDegraded(SelfTestScratch.Describe(directory, primaryError));
        return null;
    }

    /// <summary>把窗口**自身**渲染成 PNG（不依赖桌面或前台窗口：用桌面截图核对 UI 时窗口被全屏
    /// 程序挡在后面就抓不到，2026-10-07 实测踩到）。失败只告警，绝不让自检本身炸掉。</summary>
    internal static void SaveWindowPng(Window window, string path, LogService log, string label = "证据")
    {
        try
        {
            var width = (int)Math.Max(window.ActualWidth, 1);
            var height = (int)Math.Max(window.ActualHeight, 1);
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);

            log.Info($"  [{label}] 已保存 {path}（{width}x{height}）");
        }
        catch (Exception ex)
        {
            log.Warn($"  [{label}] 保存失败 {path}: {ex.Message}");
        }
    }

    /// <summary>PNG 的 `宽x高`（解不开时返回 `(解码失败)`，与调用方判据口径一致）。</summary>
    internal static string DescribePng(string path)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return $"{frame.PixelWidth}x{frame.PixelHeight}";
        }
        catch (Exception)
        {
            return "(解码失败)";
        }
    }

    /// <summary>目录下现有 `*.png` 的快照（用于"运行后多了哪些文件"的 diff）。</summary>
    internal static HashSet<string> SnapshotPngs(string directory)
        => [.. System.IO.Directory.GetFiles(directory, "*.png").Select(System.IO.Path.GetFullPath)];

    /// <summary>相对 <paramref name="before"/> **最新出现**的那个 PNG（口径同搬运前的
    /// `OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()`）。</summary>
    internal static string? NewestNewPng(string directory, HashSet<string> before)
        => System.IO.Directory.GetFiles(directory, "*.png")
            .Where(file => !before.Contains(System.IO.Path.GetFullPath(file)))
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>目录下 `*.png` 的个数（"取消不应产生文件"这类判据用它）。</summary>
    internal static int CountPngs(string directory)
        => System.IO.Directory.GetFiles(directory, "*.png").Length;
}

/// <summary>
/// 字节数 → MB 的唯一一份换算。 收敛前 `SelfTest` 的采样记录里
/// `PrivateMemorySize64 / 1024 / 1024` 与 `GC.GetTotalMemory(...) / 1024 / 1024` 各写了一遍，
/// 日志行里又是第三处 `{x}MB` —— 泄漏判据（阈值 60MB）与日志读的是同一个换算。
/// </summary>
internal static class SizeFormat
{
    internal const long BytesPerMb = 1024 * 1024;

    /// <summary>字节 → MB（整数，与历史日志口径一致）。</summary>
    internal static long ToMb(long bytes) => bytes / BytesPerMb;

    /// <summary>字节 → `"123MB"`。</summary>
    internal static string Mb(long bytes) => $"{ToMb(bytes)}MB";
}
