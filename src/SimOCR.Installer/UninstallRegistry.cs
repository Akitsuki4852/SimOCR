using Microsoft.Win32;

namespace SimOCR.Installer;

/// <summary>
/// "应用和功能"里的卸载入口（HKCU，不需要管理员权限）。
/// 卸载器路径指向安装目录里的 SimOCR-Uninstall.exe —— 它是安装器同一个工程
/// 不带 payload 的构建结果（约 1.5 MB）。
/// </summary>
internal static class UninstallRegistry
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SimOCR";

    internal static bool Write(InstallLayout layout, string version, long estimatedBytes, Logger log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                log.Warn("注册表：CreateSubKey 返回 null");
                return false;
            }

            key.SetValue("DisplayName", "SimOCR（截图 / OCR）", RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", "SimOCR", RegistryValueKind.String);
            key.SetValue("InstallLocation", layout.InstallDir, RegistryValueKind.String);
            key.SetValue("DisplayIcon", layout.ExePath, RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{layout.UninstallerPath}\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{layout.UninstallerPath}\" --uninstall --silent", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, estimatedBytes / 1024), RegistryValueKind.DWord);

            log.Info($"注册表：已写入 {KeyPath}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"注册表写入失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>卸载入口是否已登记（--verify 用）。</summary>
    internal static bool Exists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 删除卸载入口。
    /// <paramref name="expectedInstallDir"/> 非空时，只有当注册表里记录的安装目录与它一致才删 ——
    /// 避免"卸载临时目录里的一份安装"顺手把真实安装的条目也删掉。
    /// （0.10.5 删掉了 <c>Delete(Logger)</c> 单参重载：全仓只调这个两参版本。）
    /// </summary>
    internal static void Delete(Logger log, string? expectedInstallDir)
    {
        try
        {
            if (!InstallRegistry.MatchesRecordedLocation(KeyPath, expectedInstallDir, log))
            {
                return;
            }

            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            log.Info($"注册表：已删除 {KeyPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"注册表删除失败：{ex.Message}");
        }
    }
}
