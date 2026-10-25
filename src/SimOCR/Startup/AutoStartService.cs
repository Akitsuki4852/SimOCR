using Microsoft.Win32;
using SimOCR.Diagnostics;

namespace SimOCR.Startup;

/// <summary>
/// 开机自启：写/删 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 免管理员；exe 被移动后能检测出失效路径（常见坑）。
/// </summary>
internal sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimOCR";

    private readonly LogService _log;

    public AutoStartService(LogService log) => _log = log;

    public string ExecutablePath => Environment.ProcessPath ?? AppContext.BaseDirectory + "SimOCR.exe";

    /// <summary>
    /// 注册表里写的启动命令。**不带任何修饰参数** —— 过去这里写 <c>--minimized</c>，
    /// 但全仓没有任何地方解析它（<see cref="SimOCR.AppComposition"/> 只解析
    /// <c>--capture</c> / <c>--bootstrap-selftest</c> 等），等于给注册表塞了个死参数。
    /// 0.10.5 把它删掉（当时的取舍只有两条路：不实现就删掉；启动即成托盘常驻，
    /// 本来也不需要"最小化"这个动作）。
    /// </summary>
    public string ExpectedCommand => $"\"{ExecutablePath}\"";

    /// <summary>读取注册表实际值（开关状态以注册表为准，避免与配置不一致）。</summary>
    public string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            _log.Warn("读取开机自启注册表失败", ex);
            return null;
        }
    }

    public bool IsEnabled() => !string.IsNullOrWhiteSpace(GetRegisteredCommand());

    /// <summary>注册表里的路径是否已失效（exe 被移动/改名）。</summary>
    public bool IsRegisteredPathStale()
    {
        var registered = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(registered))
        {
            return false;
        }

        var path = ExtractPath(registered);
        return !string.Equals(path, ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                _log.Error("无法打开 Run 注册表项");
                return false;
            }

            key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
            _log.Info($"已启用开机自启: {ExpectedCommand}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("启用开机自启失败", ex);
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Info("已关闭开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("关闭开机自启失败", ex);
            return false;
        }
    }

    public bool Apply(bool enabled)
    {
        var ok = enabled ? Enable() : Disable();
        if (!ok)
        {
            _log.Warn($"开机自启设置未生效 (enabled={enabled})");
        }

        return ok;
    }

    private static string ExtractPath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1)
            {
                return trimmed[1..end];
            }
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
