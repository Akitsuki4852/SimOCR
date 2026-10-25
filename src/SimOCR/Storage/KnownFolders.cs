using System.Runtime.InteropServices;
using SimOCR.Interop;

namespace SimOCR.Storage;

/// <summary>
/// 系统"已知文件夹"（不硬编码 %USERPROFILE%\Downloads，用户可能改过位置）。
/// </summary>
internal static class KnownFolders
{
    public static string Downloads() => GetPath(NativeMethods.FOLDERID_Downloads);

    public static string Desktop() => GetPath(NativeMethods.FOLDERID_Desktop);

    public static string GetPath(Guid folderId)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            var hr = NativeMethods.SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero)
            {
                throw new ExternalException($"SHGetKnownFolderPath 失败, HRESULT=0x{hr:X8}");
            }

            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }
}
