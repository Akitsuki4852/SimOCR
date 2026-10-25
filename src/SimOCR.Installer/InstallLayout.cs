namespace SimOCR.Installer;

/// <summary>
/// 安装位置约定（全部在用户目录下，不写 HKLM、不弹 UAC）。
///
///   %LOCALAPPDATA%\Programs\SimOCR\              程序本体（解压自 payload zip）
///   %LOCALAPPDATA%\Programs\SimOCR\SimOCR.exe    主程序
///   %LOCALAPPDATA%\Programs\SimOCR\SimOCR-Uninstall.exe
///   %LOCALAPPDATA%\Programs\SimOCR\.simocr-install.txt   安装标记（版本 + payload 哈希）
///   %LOCALAPPDATA%\SimOCR\                       用户数据（配置/日志/默认保存位置），卸载时保留
/// </summary>
internal sealed class InstallLayout
{
    private InstallLayout(string installDir)
    {
        InstallDir = installDir;
        ExePath = System.IO.Path.Combine(installDir, "SimOCR.exe");
        UninstallerPath = System.IO.Path.Combine(installDir, "SimOCR-Uninstall.exe");
        MarkerPath = System.IO.Path.Combine(installDir, ".simocr-install.txt");
        DataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimOCR");
        LogDir = System.IO.Path.Combine(DataDir, "logs");
    }

    internal string InstallDir { get; }

    internal string ExePath { get; }

    internal string UninstallerPath { get; }

    internal string MarkerPath { get; }

    /// <summary>用户数据目录。安装器只用来放日志；卸载时不动它。</summary>
    internal string DataDir { get; }

    internal string LogDir { get; }

    internal static InstallLayout Create(string? overrideDir)
    {
        var dir = string.IsNullOrWhiteSpace(overrideDir)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SimOCR")
            : System.IO.Path.GetFullPath(overrideDir!);

        return new InstallLayout(dir);
    }

    /// <summary>开始菜单快捷方式：%APPDATA%\Microsoft\Windows\Start Menu\Programs\SimOCR.lnk</summary>
    internal string StartMenuShortcut => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "SimOCR.lnk");

    /// <summary>桌面快捷方式：%USERPROFILE%\Desktop\SimOCR.lnk</summary>
    internal string DesktopShortcut => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SimOCR.lnk");
}
