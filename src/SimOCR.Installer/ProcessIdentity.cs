using System.Runtime.InteropServices;

namespace SimOCR.Installer;

/// <summary>
/// 打印"当前进程是谁、在什么受限环境里"。
///
/// 为什么要它：实测遇到过一种失败 —— 双击（通过沙箱/自动化工具的文件卡片）启动时，
/// 进程连 <c>%LOCALAPPDATA%</c> 都写不进去（日志文件都建不出来），报错却说
/// "Access to the path ... is denied"，看起来像目录权限问题，实际是**启动上下文受限**。
/// 把 user / SID / AppContainer / 完整性级别记进日志，这类问题下次一眼可辨。
///
/// 全部用 P/Invoke 实现（不用 WindowsIdentity），保证 NativeAOT 下行为确定。
/// </summary>
internal static class ProcessIdentity
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;

    internal static string Describe()
    {
        var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var sid = ReadSid(TokenUser);
        var appContainer = ReadBool(TokenIsAppContainer);
        var integrity = ReadIntegrityLevel();
        return $"account={account} sid={sid} appcontainer={appContainer} integrity={integrity}";
    }

    private static string ReadSid(int infoClass)
    {
        var buffer = ReadTokenInfo(infoClass);
        if (buffer == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            // TOKEN_USER / TOKEN_MANDATORY_LABEL 的第一个成员都是 SID_AND_ATTRIBUTES（Sid 指针在偏移 0）
            var sidPointer = Marshal.ReadIntPtr(buffer);
            if (sidPointer == IntPtr.Zero || !ConvertSidToStringSidW(sidPointer, out var text))
            {
                return "?";
            }

            try
            {
                return Marshal.PtrToStringUni(text) ?? "?";
            }
            finally
            {
                LocalFree(text);
            }
        }
        catch (Exception)
        {
            return "?";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadBool(int infoClass)
    {
        var buffer = ReadTokenInfo(infoClass);
        if (buffer == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            return Marshal.ReadInt32(buffer) != 0 ? "true" : "false";
        }
        catch (Exception)
        {
            return "?";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadIntegrityLevel()
    {
        var buffer = ReadTokenInfo(TokenIntegrityLevel);
        if (buffer == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return "?";
            }

            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero)
            {
                return "?";
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return "?";
            }

            var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
            if (ridPointer == IntPtr.Zero)
            {
                return "?";
            }

            var rid = (uint)Marshal.ReadInt32(ridPointer);
            return rid switch
            {
                0x0000 => "Untrusted(0)",
                0x1000 => "Low(0x1000)",
                0x2000 => "Medium(0x2000)",
                0x3000 => "High(0x3000)",
                0x4000 => "System(0x4000)",
                _ => $"0x{rid:X}",
            };
        }
        catch (Exception)
        {
            return "?";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>取一段 token 信息到非托管内存；调用方负责 FreeHGlobal。失败返回 IntPtr.Zero。</summary>
    private static IntPtr ReadTokenInfo(int infoClass)
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            {
                return IntPtr.Zero;
            }

            GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out var size);
            if (size <= 0)
            {
                return IntPtr.Zero;
            }

            var buffer = Marshal.AllocHGlobal(size);
            if (GetTokenInformation(token, infoClass, buffer, size, out _))
            {
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);
}
