using System.Diagnostics;

namespace SimOCR.Installer;

/// <summary>
/// 创建开始菜单 / 桌面快捷方式。
///
/// 为什么不用 C# 直接写 .lnk：Shell Link 二进制格式要自己拼 IDList，代码量大且容易出错；
/// 而 NativeAOT 下 COM 互操作（IShellLink）虽然可行但没必要冒险。
/// 这里走系统自带的 Windows PowerShell + WScript.Shell —— Win11 上永远存在。
///
/// 中文/空格路径的处理：脚本正文是纯 ASCII，路径全部通过<b>环境变量</b>传入
/// （CreateProcessW 传的是 UTF-16，不经 cmd 转义），所以用户名里有中文也不会乱码。
/// </summary>
internal static class ShortcutService
{
    private const string Script = """
        $ErrorActionPreference='Stop';
        $w=New-Object -ComObject WScript.Shell;
        foreach($p in ($env:SIMOCR_LNK_PATHS -split '\|')){
          if([string]::IsNullOrWhiteSpace($p)){continue}
          $s=$w.CreateShortcut($p);
          $s.TargetPath=$env:SIMOCR_LNK_TARGET;
          $s.WorkingDirectory=$env:SIMOCR_LNK_WORKDIR;
          $s.IconLocation=$env:SIMOCR_LNK_ICON;
          $s.Description=$env:SIMOCR_LNK_DESC;
          $s.Save()
        }
        Write-Output 'SIMOCR-LNK-OK'
        """;

    /// <summary>创建快捷方式。返回是否全部创建成功（失败只记日志，不影响安装结果）。</summary>
    internal static bool TryCreate(InstallLayout layout, Logger log)
    {
        var paths = new[] { layout.StartMenuShortcut, layout.DesktopShortcut };
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!File.Exists(startInfo.FileName))
            {
                startInfo.FileName = "powershell.exe";
            }

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(Script);

            startInfo.Environment["SIMOCR_LNK_PATHS"] = string.Join("|", paths);
            startInfo.Environment["SIMOCR_LNK_TARGET"] = layout.ExePath;
            startInfo.Environment["SIMOCR_LNK_WORKDIR"] = layout.InstallDir;
            startInfo.Environment["SIMOCR_LNK_ICON"] = layout.ExePath + ",0";
            startInfo.Environment["SIMOCR_LNK_DESC"] = "SimOCR - 截图 / OCR（F1 截图）";

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                log.Warn("快捷方式：无法启动 powershell.exe");
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                log.Warn("快捷方式：powershell 超时");
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                return false;
            }

            var ok = process.ExitCode == 0 && File.Exists(layout.StartMenuShortcut);
            log.Info(
                ok
                    ? $"快捷方式：已创建 {string.Join(" / ", paths.Where(File.Exists))}"
                    : $"快捷方式：创建失败 exit={process.ExitCode} out={stdout.Trim()} err={stderr.Trim()}");
            return ok;
        }
        catch (Exception ex)
        {
            log.Warn($"快捷方式：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除快捷方式 —— 但只删"指向本次安装目录"的那些。
    ///
    /// 为什么必须判断目标：快捷方式路径是固定的（开始菜单/桌面都叫 SimOCR.lnk），
    /// 本机可能装着另一个位置的 SimOCR（比如用户自己解压的版本、或另一个账户装的）。
    /// 早先无条件删固定路径的写法实测把**真实安装**的快捷方式删掉过（回归测试第 5 步抓到的）。
    /// 读 .lnk 目标同样走 PowerShell + WScript.Shell（读不到就保留，宁可不删）。
    ///
    /// **0.10.5**：判据由"原始字符串前缀"改为**归一化后的路径相等**。
    /// 旧写法 <c>$target.StartsWith($expect)</c> 会把 <c>C:\Apps\SimOCR-old\SimOCR.exe</c>
    /// 误判成 <c>C:\Apps\SimOCR</c> 下的安装（于是删掉别人的快捷方式）；
    /// 归一化 = 去掉尾部分隔符、去掉尾部 <c>\</c>、去引号、右斜杠统一、大小写无关，
    /// 与 <see cref="InstallRegistry.MatchesRecordedLocation"/> 那一类比较同一口径。
    /// 目录本身（而不是"某个 exe 的上级目录"）也要能匹配，所以对两者都取一次父目录再比。
    /// </summary>
    internal static void Delete(InstallLayout layout, Logger log)
    {
        const string script = """
            $ErrorActionPreference='Continue';

            function Get-Norm([string]$value) {
              if ([string]::IsNullOrWhiteSpace($value)) { return '' }
              $v = $value.Trim().Trim('"')
              try { $v = [System.IO.Path]::GetFullPath($v) } catch { }
              $v = $v.Replace('/', '\')
              return $v.TrimEnd('\')
            }

            function Test-SamePath([string]$a, [string]$b) {
              $na = Get-Norm $a
              $nb = Get-Norm $b
              if ($na -eq '' -or $nb -eq '') { return $false }
              if ([string]::Equals($na, $nb, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
              # .lnk 的 TargetPath 是 exe；安装目录记的是目录 → 再比一次父目录
              try { $pa = Get-Norm ([System.IO.Path]::GetDirectoryName($na)) } catch { $pa = '' }
              try { $pb = Get-Norm ([System.IO.Path]::GetDirectoryName($nb)) } catch { $pb = '' }
              return ($pa -ne '' -and ([string]::Equals($pa, $nb, [System.StringComparison]::OrdinalIgnoreCase) -or
                                       [string]::Equals($pa, $pb, [System.StringComparison]::OrdinalIgnoreCase))) -or
                     ($pb -ne '' -and [string]::Equals($na, $pb, [System.StringComparison]::OrdinalIgnoreCase))
            }

            $w=New-Object -ComObject WScript.Shell;
            foreach($p in ($env:SIMOCR_LNK_PATHS -split '\|')){
              if([string]::IsNullOrWhiteSpace($p)){continue}
              if(-not (Test-Path -LiteralPath $p)){Write-Output ('NONE ' + $p); continue}
              $target='';
              try{ $target=$w.CreateShortcut($p).TargetPath }catch{ $target='' }
              if($target -and (Test-SamePath $target $env:SIMOCR_LNK_EXPECT)){
                Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue;
                Write-Output ('DELETED ' + $p)
              } else {
                Write-Output ('KEPT ' + $p + ' -> ' + $target)
              }
            }
            """;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (!File.Exists(startInfo.FileName))
            {
                startInfo.FileName = "powershell.exe";
            }

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(script);
            startInfo.Environment["SIMOCR_LNK_PATHS"] = string.Join(
                "|", layout.StartMenuShortcut, layout.DesktopShortcut);
            startInfo.Environment["SIMOCR_LNK_EXPECT"] = layout.InstallDir;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                log.Warn("快捷方式：无法启动 powershell.exe，保留现有快捷方式");
                return;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                log.Warn("快捷方式：删除超时（保留现有快捷方式）");
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                return;
            }

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                log.Info($"快捷方式：{line.Trim()}");
            }
        }
        catch (Exception ex)
        {
            log.Warn($"快捷方式删除失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}
