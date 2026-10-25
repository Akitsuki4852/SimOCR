namespace SimOCR.Installer;

/// <summary>
/// 安装标记文件（install marker）。纯 key=value 文本，刻意不用 JSON：
/// NativeAOT 下 System.Text.Json 的反射序列化需要额外配置，这里没必要。
///
/// 用途：双击安装器时先看标记 —— 版本与 payload 哈希都对得上就直接启动主程序，
/// 不再解压 60 多 MB（第二次以后双击都是"秒开"）。
/// </summary>
internal sealed class MarkerFile
{
    internal const string FileName = ".simocr-install.txt";

    internal string Version { get; init; } = string.Empty;

    internal string PayloadSha256 { get; init; } = string.Empty;

    internal int FileCount { get; init; }

    internal long TotalBytes { get; init; }

    internal DateTime InstalledUtc { get; init; }

    /// <summary>本次安装是否创建了快捷方式（verify 据此决定要不要把"快捷方式存在"当必检项）。</summary>
    internal bool Shortcuts { get; init; } = true;

    /// <summary>本次安装是否登记了"应用和功能"卸载项。</summary>
    internal bool Registry { get; init; } = true;

    internal static MarkerFile? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            return new MarkerFile
            {
                Version = values.TryGetValue("version", out var v) ? v : string.Empty,
                PayloadSha256 = values.TryGetValue("payloadSha256", out var h) ? h : string.Empty,
                FileCount = values.TryGetValue("files", out var f) && int.TryParse(f, out var fc) ? fc : 0,
                TotalBytes = values.TryGetValue("bytes", out var b) && long.TryParse(b, out var tb) ? tb : 0,
                InstalledUtc = values.TryGetValue("installedUtc", out var t) &&
                               DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt
                    : DateTime.MinValue,
                Shortcuts = !values.TryGetValue("shortcuts", out var sc) || sc != "0",
                Registry = !values.TryGetValue("registry", out var rg) || rg != "0",
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 写标记文件。返回**是否真的落盘**（0.10.5：调用方要用这个结果决定"标记文件本身要不要
    /// 计进 files" —— 它也是安装目录里的一个文件，而 <c>--verify</c> 现在用精确 == 比对文件数）。
    /// 写不进去不算安装失败（只影响"下次是否重新解压"）。
    /// </summary>
    internal bool Write(string path)
    {
        var lines = new[]
        {
            "# SimOCR 安装标记 —— 由 SimOCR-Setup.exe 写入，删除它可强制重新解压",
            $"version={Version}",
            $"payloadSha256={PayloadSha256}",
            $"files={FileCount}",
            $"bytes={TotalBytes}",
            $"shortcuts={(Shortcuts ? 1 : 0)}",
            $"registry={(Registry ? 1 : 0)}",
            $"installedUtc={InstalledUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}",
        };

        try
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception)
        {
            // 写不进标记只影响"下次是否重新解压"，不影响本次安装
            return false;
        }
    }
}
