using System.Security;

namespace SimOCR.Diagnostics;

/// <summary>
/// 把异常翻成一句用户能照着做的提示。
///
/// <para>
/// 为什么需要：<c>ex.Message</c> 是英文的 OS 句子、还带完整路径（例如
/// <c>Access to the path 'C:\…\a.png' is denied.</c>），直接拼进界面等于把日志当文案 ——
/// 中文界面里突然冒出一句英文，用户只会当成程序出错。
/// 技术细节统一留给 <see cref="LogService"/>（调用方照旧 <c>_log.Error(msg, ex)</c>）。
/// </para>
/// </summary>
internal static class FriendlyError
{
    /// <summary>
    /// 文件读写类失败的统一措辞。分支顺序有讲究：
    /// <see cref="DirectoryNotFoundException"/> 与 <see cref="PathTooLongException"/>
    /// 都派生自 <see cref="IOException"/>，必须排在它前面才不会被吞掉。
    /// </summary>
    internal static string ForIo(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有写入权限，请换一个保存位置",
        SecurityException => "没有写入权限，请换一个保存位置",
        DirectoryNotFoundException => "这个文件夹不存在，请重新选一个",
        PathTooLongException => "文件路径太长，请换一个更短的名字",
        IOException => "文件正被其他程序占用，请稍后再试",
        NotSupportedException => "这个位置不能保存文件，请换一个文件夹",
        _ => "保存失败，详情见「设置 → 运行信息 → 打开日志目录」",
    };
}
