using Microsoft.Win32;

namespace SimOCR.Installer;

/// <summary>
/// 自写安装登记：<c>HKCU\Software\SimOCR</c>。
///
/// 这是常规安装器的通行做法（electron-builder 的 NSIS 模板就写
/// <c>HKCU\Software\&lt;AppGuid&gt;\InstallLocation</c>），作用有两个：
///   1. 下次安装/升级时知道"上次装在哪"，**原地升级**而不是在默认位置再装一份；
///   2. 给排查问题的人一个稳定的"这个版本装在哪儿"的记录。
/// </summary>
internal static class InstallRegistry
{
    internal const string KeyPath = @"Software\SimOCR";

    internal static bool Write(InstallLayout layout, string version, string payloadSha256, Logger log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                log.Warn("安装键：CreateSubKey 返回 null");
                return false;
            }

            key.SetValue("InstallLocation", layout.InstallDir, RegistryValueKind.String);
            key.SetValue("Version", version, RegistryValueKind.String);
            key.SetValue("PayloadSha256", payloadSha256, RegistryValueKind.String);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);

            log.Info($"安装键：已写入 HKCU\\{KeyPath}（InstallLocation={layout.InstallDir}）");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"安装键写入失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 读上次的安装位置。先看自己的键，再看卸载项里的 InstallLocation
    /// （0.1.4 只写了卸载项，这样升级过来也能沿用原位置）。
    /// </summary>
    internal static string? TryReadInstallLocation()
    {
        var recorded = ReadString(KeyPath, "InstallLocation");
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            return recorded;
        }

        return ReadString(UninstallRegistry.KeyPath, "InstallLocation");
    }

    /// <summary>安装键是否已登记（--verify 用）。</summary>
    internal static bool Exists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue("InstallLocation") is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 删除安装键；<paramref name="expectedInstallDir"/> 非空时只在位置匹配时删（避免误删另一个安装）。
    /// （0.10.5 删掉了 <c>Delete(Logger)</c> 单参重载：全仓只调这个两参版本。）
    /// </summary>
    internal static void Delete(Logger log, string? expectedInstallDir)
    {
        if (!MatchesRecordedLocation(KeyPath, expectedInstallDir, log))
        {
            return;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            log.Info($"安装键：已删除 HKCU\\{KeyPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"安装键删除失败：{ex.Message}");
        }
    }

    /// <summary>位置是否与记录一致（记录为空或读不到时按"可以删"处理）。</summary>
    internal static bool MatchesRecordedLocation(string keyPath, string? expectedInstallDir, Logger log)
    {
        if (string.IsNullOrEmpty(expectedInstallDir))
        {
            return true;
        }

        var recorded = ReadString(keyPath, "InstallLocation");
        if (string.IsNullOrWhiteSpace(recorded))
        {
            return true;
        }

        if (PathsEqual(recorded, expectedInstallDir))
        {
            return true;
        }

        log.Info($"注册表：{keyPath} 属于另一个安装目录（{recorded}），保留不动");
        return false;
    }

    internal static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ReadString(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 注册 App Paths：<c>HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\SimOCR.exe</c>。
///
/// 作用：<kbd>Win</kbd>+<kbd>R</kbd> 里直接输 <c>SimOCR</c> 就能启动，ShellExecute 也能按名字找到它
/// （Inno Setup 的 App Paths 选项、各类安装器都会登记这个键）。
/// </summary>
internal static class AppPathsRegistry
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\SimOCR.exe";

    internal static bool Write(InstallLayout layout, Logger log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                log.Warn("App Paths：CreateSubKey 返回 null");
                return false;
            }

            key.SetValue(null, layout.ExePath, RegistryValueKind.String);
            key.SetValue("Path", layout.InstallDir, RegistryValueKind.String);

            log.Info($"App Paths：已写入 HKCU\\{KeyPath}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"App Paths 写入失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    internal static void Delete(Logger log, string? expectedInstallDir)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false))
            {
                var recorded = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(recorded) && !string.IsNullOrEmpty(expectedInstallDir))
                {
                    var recordedDir = Path.GetDirectoryName(recorded);
                    if (!string.IsNullOrEmpty(recordedDir) && !InstallRegistry.PathsEqual(recordedDir, expectedInstallDir))
                    {
                        log.Info($"App Paths：指向另一个安装（{recorded}），保留不动");
                        return;
                    }
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            log.Info($"App Paths：已删除 HKCU\\{KeyPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"App Paths 删除失败：{ex.Message}");
        }
    }

    internal static bool Exists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
