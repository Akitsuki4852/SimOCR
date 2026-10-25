namespace SimOCR.Installer;

/// <summary>命令行解析。刻意支持 --x 与 --x=value 两种写法（用户和脚本都可能随手写）。</summary>
internal sealed class CommandLine
{
    private CommandLine()
    {
    }

    internal bool Verify { get; private set; }

    internal bool Uninstall { get; private set; }

    internal bool Silent { get; private set; }

    internal bool Force { get; private set; }

    internal bool NoLaunch { get; private set; }

    internal bool NoShortcuts { get; private set; }

    internal bool NoRegistry { get; private set; }

    internal bool NoProgress { get; private set; }

    internal bool PurgeData { get; private set; }

    internal bool Help { get; private set; }

    /// <summary>隐藏自检：只把进度窗口跑一遍（验收要截图为证）。</summary>
    internal bool SelfTestUi { get; private set; }

    internal string? InstallDirectory { get; private set; }

    internal static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            // --dir=<路径>
            if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                result.InstallDirectory = arg["--dir=".Length..];
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--dir":
                    if (i + 1 < args.Length)
                    {
                        result.InstallDirectory = args[++i];
                    }

                    break;
                case "--verify":
                    result.Verify = true;
                    break;
                case "--uninstall":
                    result.Uninstall = true;
                    break;
                case "--silent":
                    result.Silent = true;
                    break;
                case "--force":
                    result.Force = true;
                    break;
                case "--no-launch":
                    result.NoLaunch = true;
                    break;
                case "--no-shortcuts":
                    result.NoShortcuts = true;
                    break;
                case "--no-registry":
                    result.NoRegistry = true;
                    break;
                case "--no-progress":
                    result.NoProgress = true;
                    break;
                case "--purge-data":
                    result.PurgeData = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.Help = true;
                    break;
                case "--selftest-ui":
                    result.SelfTestUi = true;
                    break;
            }
        }

        return result;
    }

    internal const string Usage = """
        SimOCR-Setup —— SimOCR 安装器（自解压，双击即用）

        用法：
          SimOCR-Setup.exe                安装（已安装则直接启动 SimOCR）
          SimOCR-Setup.exe --verify       检查安装是否完整（写日志，退出码 0=正常）
          SimOCR-Setup.exe --uninstall    卸载（SimOCR-Uninstall.exe 等价）

        （从 Release 下载的文件名带版本号，如 SimOCR-0.10.10-setup-win-x64.exe，参数完全相同。）

        可选参数：
          --silent            不弹任何对话框（配合 --verify / --uninstall 做无人值守）
          --force             即使版本相同也强制重新解压
          --no-launch         安装完不自动启动主程序
          --no-shortcuts      不创建开始菜单/桌面快捷方式
          --no-registry       不写“应用和功能”里的卸载项
          --no-progress       不显示进度对话框
          --dir <路径>        指定安装目录（默认 %LOCALAPPDATA%\Programs\SimOCR）
          --purge-data        卸载时一并删除用户配置与日志
          --help              显示本帮助

        退出码：0=成功 1=失败 2=安装包异常 3=被占用/需要用户介入 4=用户取消
        """;
}
