using System.Reflection;
using System.Security.Cryptography;

namespace SimOCR.Installer;

/// <summary>
/// 读取编译进 exe 的嵌入资源：便携 zip（SimOCR 本体）与卸载器。
/// 资源名由 csproj 的 LogicalName 固定，见 SimOCR.Installer.csproj。
/// </summary>
internal static class PayloadReader
{
    /// <summary>SimOCR 便携包（zip）的资源名。</summary>
    internal const string PayloadResourceName = "SimOCR.payload.zip";

    /// <summary>卸载器的资源名（同一个工程不带 payload 的构建结果）。</summary>
    internal const string UninstallerResourceName = "SimOCR.uninstaller.exe";

    /// <summary>本 exe 里是否带着便携包（卸载器版本不带）。</summary>
    internal static bool HasPayload
    {
        get
        {
            // （CA2000）：探测用的资源流不向外传递，读到即释放；只回答"存在与否"。
            using var resource = OpenResource(PayloadResourceName);
            return resource is not null;
        }
    }

    /// <summary>本 exe 里是否带着卸载器（卸载器版本自身不带）。</summary>
    internal static bool HasUninstaller
    {
        get
        {
            // （CA2000）：同上，探测完立即释放。
            using var resource = OpenResource(UninstallerResourceName);
            return resource is not null;
        }
    }

    internal static Stream? OpenPayload() => OpenResource(PayloadResourceName);

    internal static Stream? OpenUninstaller() => OpenResource(UninstallerResourceName);

    private static Stream? OpenResource(string logicalName)
    {
        try
        {
            return typeof(PayloadReader).Assembly.GetManifestResourceStream(logicalName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>整包 SHA-256（十六进制大写）。用于判断目标目录里的版本是不是同一份 payload。</summary>
    internal static string ComputeSha256(Stream stream)
    {
        var origin = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = origin;
            }
        }
    }

    /// <summary>
    /// 把卸载器释放到安装目录。返回是否成功（失败不算致命：只是"应用和功能"里少了卸载入口）。
    /// </summary>
    internal static bool ExtractUninstaller(string destinationPath)
    {
        using var source = OpenUninstaller();
        if (source is null)
        {
            return false;
        }

        using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        return true;
    }

    /// <summary>安装器自身可执行文件路径（NativeAOT 下 Environment.ProcessPath 一定可用）。</summary>
    internal static string SelfPath
        => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "SimOCR-Setup.exe");

    /// <summary>本安装器的版本（取自程序集信息版本，去掉 build 号）。</summary>
    internal static string SelfVersion
    {
        get
        {
            var assembly = typeof(PayloadReader).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // SourceLink 之类的工具会往后面挂 "+<commit>"，截掉
                var plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
