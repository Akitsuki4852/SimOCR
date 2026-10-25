using System.IO;

namespace SimOCR.Storage;

/// <summary>
/// **重名自增**的唯一实现：同一目录下同名的文件追加 <c>_1</c>、<c>_2</c>…，
/// 并且**用 <see cref="FileMode.CreateNew"/> 抢占文件名**，所以两个并发调用不可能选中同一个名字。
///
/// 收敛前的两份副本：
///   * <c>ImageStorageService.WriteUnique</c> —— **原子**（CreateNew 抢占）；
///   * <c>ExportService.ResolveUniquePath</c> —— **竞态**（`File.Exists` 之后把路径返回给调用方，
///     调用方稍后才 `File.WriteAllText`/导出器打开写 → 典型的 TOCTOU：两个并发导出会互相覆盖）。
///
/// 本类把"原子"这一份做成唯一实现：
///   * <see cref="Write(string,string,byte[])"/> —— 直接把字节写进去（PNG 保存走这条）；
///   * <see cref="Reserve(string,string)"/> —— 只抢占一个**空文件**当占位并返回路径
///     （导出器按格式自己写内容，走这条）。**注意**：占位文件在导出器写完之前是"空文件"，
///     这与收敛前 <c>WriteUnique</c> 的语义一致（它同样是"以 CreateNew 打开、再往里写"）。
/// </summary>
internal static class UniqueFile
{
    /// <summary>自增尝试上限：到顶还不成就抛（不静默改名成随机串）。</summary>
    internal const int MaxAttempts = 1000;

    /// <summary>第 i 次尝试的文件名（i=0 就是原名）。</summary>
    internal static string CandidateName(string fileName, int attempt)
    {
        if (attempt == 0)
        {
            return fileName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return $"{baseName}_{attempt}{extension}";
    }

    /// <summary>把字节原子写进"该目录下第一个可用的 <paramref name="fileName"/> 名字"，返回实际路径。</summary>
    internal static string Write(string directory, string fileName, byte[] bytes)
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            var full = Path.Combine(directory, CandidateName(fileName, i));
            try
            {
                using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
                return full;
            }
            catch (IOException) when (File.Exists(full))
            {
                // 重名，试下一个（唯一"预期内"的失败原因）
            }
        }

        throw new IOException($"无法在 {MaxAttempts} 次尝试内找到可用文件名: {fileName}");
    }

    /// <summary>
    /// 抢占一个可用文件名：以 <see cref="FileMode.CreateNew"/> 建一个**空文件**当占位，
    /// 返回它的完整路径。并发调用者拿到的一定是不同的路径（OS 保证 CreateNew 互斥）。
    /// 写内容由调用方（导出器）在自己的一段时间里完成。
    /// </summary>
    internal static string Reserve(string directory, string fileName)
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            var full = Path.Combine(directory, CandidateName(fileName, i));
            try
            {
                using (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // 只占位，不写内容
                }

                return full;
            }
            catch (IOException) when (File.Exists(full))
            {
                // 重名，试下一个
            }
        }

        throw new IOException($"无法在 {MaxAttempts} 次尝试内找到可用文件名: {fileName}");
    }

    /// <summary>
    /// **只算名字、不落盘**：给 UI 一个默认保存路径（另存为对话框的初始文件名）。
    /// 它必然有 TOCTOU 窗口 —— 用户从看到名字到真的保存之间任何事都可能发生。
    /// 所以**真正的写盘不得用它**：默认路径写盘走 <see cref="Reserve"/> / <see cref="Write"/>
    /// （见 <c>ExportService.Export</c> 的 <c>overwrite: false</c> 分支）。
    /// </summary>
    internal static string Suggest(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        for (var i = 1; i < MaxAttempts; i++)
        {
            var candidate = Path.Combine(directory, CandidateName(fileName, i));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 与收敛前一致：极端情况下给一个随机名（不抛异常 —— 这只是个"建议路径"）
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
    }
}
