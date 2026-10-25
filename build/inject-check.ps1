<#
.SYNOPSIS
    缺陷注入门禁：对每个受保护断言做「注入 → 构建 → 跑门禁（必须失败）→ 还原 → 构建 → 跑门禁（必须通过）」。

.DESCRIPTION
    为什么存在
      * 0.10.0 之前，同一份日志里可以同时出现「[失败] 手动编辑的文本没有进导出文件」与「[结论] OK」，
        进程退出码 0（三处）。阶段一把这些断言接进了退出码，但**"闸门可信"本身没有闸门**：
        只要有人再把断言改成只打日志、或者把 targets 换回过滤列表，退化会**静默**发生，
        而且没人会注意到 —— 这正是最开始那三处 [失败] 却退出 0 的成因。
      * 所以本脚本把"注入缺陷 → 断言必须炸"这件事自动化：每个用例都往**当前源码**里塞一个
        真实缺陷（锚点精确替换），跑对应门禁，要求它**非 0 退出**且日志里出现预期的失败标记；
        然后**按字节还原**，再跑一次，要求它**恢复绿色**。任何一步不成立，本脚本非 0 退出。

    安全
      * 每个被改动的文件在注入前**按字节备份**，在 finally 里**按字节还原**（不是"再替换回去"）。
      * 目标文件在开工前必须干净：`git diff --quiet -- <file>` 与 `git diff --cached --quiet -- <file>`
        都为空才动；否则直接拒绝启动（绝不覆盖用户未提交的工作）。
      * 收尾时打印 `git status --porcelain -- <file>`，并断言它为空。
      * 锚点在文件里必须**恰好命中 1 次**：命中 0 次说明行漂了（会报错而不是"悄悄注错地方"），
        命中多次说明锚点选得太宽 —— 两种情况都算用例失败。

    启动方式（沙箱/CI 友好）
      * 用 SimOCR.exe 启动（不是 dotnet <dll>），工作目录 = 仓库根，图片路径用绝对路径；
      * 子进程的 TEMP/TMP 指向仓库内可写临时目录（有些沙箱不允许本进程写 %TEMP%）；
      * 每次运行前清掉残留的 SimOCR 进程（单实例守卫会把新自检转发给首实例后静默退出 → 假绿）；
      * 每个用例有超时，超时按失败记账（打印 TIMEOUT）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\inject-check.ps1

.EXAMPLE
    # 只跑一个用例（调试锚点/门禁时用）
    powershell -NoProfile -ExecutionPolicy Bypass -File build\inject-check.ps1 -Case p01-snap-pairing

.EXAMPLE
    # 用真实单据图跑验收型用例（默认用随仓合成夹具，已足够跑通数据链路）
    powershell -NoProfile -ExecutionPolicy Bypass -File build\inject-check.ps1 -AcceptImage <单据图.jpg>

.NOTES
    阶段收尾推荐顺序
      1) build\gate-check.ps1                                  核心门禁（快）
      2) build\gate-check.ps1 -IncludeInjection                本脚本（慢：每用例 2 次构建 + 2 次门禁）
      3) build\gate-check.ps1 -IncludeMaterial -MaterialDir …  有内部素材时的硬回归基线
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 只跑指定 id
    [string[]]$Case = @(),

    # --selftest-ocr-window 的输入图（默认用随仓合成夹具：只要求"图里有文字"）
    [string]$OcrWindowImage,

    # --accept-table 的输入图（默认用随仓合成夹具；要跑内部硬基线就传真实单据图）
    [string]$AcceptImage,

    # 单个门禁的超时（秒）
    [int]$TimeoutSeconds = 300,

    # 不先构建一次（默认先构建，确保注入前的基线是绿的）
    [switch]$SkipBuild,

    # 保留临时目录
    [switch]$KeepScratch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# stdout 可能被重定向到文件（gate-check -IncludeInjection、CI 日志、人工 > log.txt）：
# 显式设成 UTF-8，否则 Windows PowerShell 5.1 按活动代码页写出，中文与 [失败] 这类标记会乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$script:repoRoot = Split-Path -Parent $PSScriptRoot
$script:exeDir = Join-Path $script:repoRoot "src\SimOCR\bin\$Configuration\net10.0-windows"
$script:exePath = Join-Path $script:exeDir 'SimOCR.exe'
# 0.10.7 起：纯逻辑断言已经移出发布产物，注入用例的门禁改走**控制台运行器**
# （与 build\gate-check.ps1 的同名路径一字不差）。
$script:testProject = Join-Path $script:repoRoot 'tests\SimOCR.Tests\SimOCR.Tests.csproj'
$script:runnerPath = Join-Path $script:repoRoot "tests\SimOCR.Tests\bin\$Configuration\net10.0-windows\SimOCR.Tests.exe"
$script:scratch = Join-Path $script:repoRoot "src\SimOCR\bin\inject-scratch\$Configuration"
$script:appLog = Join-Path $script:exeDir ("logs\simocr-{0}.log" -f (Get-Date).ToString('yyyyMMdd'))
$script:runSeq = 0
$script:backups = @{}          # 绝对路径 -> 原始字节
$script:problems = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host "=== $Text ==="
}

function Read-Utf8Text([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$GitArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $script:repoRoot @GitArgs 2>&1
        return @{ ExitCode = $LASTEXITCODE; Text = (($out | Out-String).Trim()) }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Test-FileClean {
    # 未提交改动（暂存或未暂存）都不许动：我们不碰用户的工作
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $a = Invoke-Git @('diff', '--quiet', '--', $RelativePath)
    if ($a.ExitCode -ne 0) { return @{ Clean = $false; Why = '工作区有未暂存改动' } }
    $b = Invoke-Git @('diff', '--cached', '--quiet', '--', $RelativePath)
    if ($b.ExitCode -ne 0) { return @{ Clean = $false; Why = '有已暂存但未提交的改动' } }
    $c = Invoke-Git @('status', '--porcelain', '--', $RelativePath)
    if ($c.Text.Length -gt 0) { return @{ Clean = $false; Why = "git status 非空：$($c.Text)" } }
    return @{ Clean = $true; Why = '' }
}

function Get-TextInfo {
    # 返回 @{ Text; Encoding; HasBom }：注入后按同一编码写回，保证 BOM 与字节序不变
    param([Parameter(Mandatory = $true)][byte[]]$Bytes, [Parameter(Mandatory = $true)][string]$Path)
    $hasBom = ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF)
    if ($Bytes.Length -ge 2 -and (($Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) -or ($Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF))) {
        throw "不支持 UTF-16 源文件（本门禁只注入 UTF-8）：$Path"
    }

    $enc = New-Object System.Text.UTF8Encoding($hasBom)
    return @{ Text = [System.IO.File]::ReadAllText($Path, $enc); Encoding = $enc; HasBom = $hasBom }
}

function Restore-All {
    # 按字节还原所有备份；返回仍未还原的文件列表（应为空）
    $left = New-Object System.Collections.Generic.List[string]
    foreach ($path in $script:backups.Keys) {
        $orig = $script:backups[$path]
        try {
            if (Test-Path -LiteralPath $path) {
                $now = [System.IO.File]::ReadAllBytes($path)
                if ($now.Length -eq $orig.Length) {
                    $same = $true
                    for ($i = 0; $i -lt $now.Length; $i++) { if ($now[$i] -ne $orig[$i]) { $same = $false; break } }
                    if ($same) { continue }
                }
            }

            [System.IO.File]::WriteAllBytes($path, $orig)
            $back = [System.IO.File]::ReadAllBytes($path)
            if ($back.Length -ne $orig.Length) { $left.Add($path); continue }
            for ($i = 0; $i -lt $back.Length; $i++) { if ($back[$i] -ne $orig[$i]) { $left.Add($path); break } }
        } catch {
            $left.Add($path)
        }
    }

    return $left
}

function Invoke-Build {
    # 构建**测试工程**（它 ProjectReference 应用工程 → 一次构建同时覆盖两类注入点）。
    # 为什么不是只构建 src\SimOCR：0.10.7 起 `p01-snap-pairing` /
    # `p04-linegrid-split` 的门禁是控制台运行器，而运行器读的是**测试工程输出目录**里的
    # SimOCR.dll / 测试程序集 —— 只构建应用工程，运行器仍会跑旧代码（注入不进判据＝假绿）。
    # 返回 @{ Ok; ExitCode; Text }。
    # 退出码由批处理写进文件：PowerShell 5.1 里 Start-Process 一旦带 -RedirectStandardOutput，
    # $p.ExitCode 就取不到值（实测为空），而 -Wait 给不了超时。
    param([Parameter(Mandatory = $true)][string]$Label)
    $proj = $script:testProject
    $log = Join-Path $script:scratch ("build-{0}.log" -f $Label)
    $bat = Join-Path $script:scratch ("build-{0}.cmd" -f $Label)
    $ec = $bat + '.ec.txt'
    Remove-Item -LiteralPath $ec -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $bat -Encoding ASCII -Value @(
        '@echo off',
        "dotnet build `"$proj`" -c $Configuration --nologo -v:m > `"$log`" 2>&1",
        "> `"$ec`" echo %ERRORLEVEL%"
    )

    $cmdExe = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $p = Start-Process -FilePath $cmdExe -ArgumentList '/d', '/c', $bat -PassThru `
        -RedirectStandardOutput ($bat + '.out') -RedirectStandardError ($bat + '.err')
    $deadline = (Get-Date).AddSeconds(900)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }

    $text = [string](Read-Utf8Text $log)
    if (-not $p.HasExited) {
        try { $p.Kill() } catch { }
        return @{ Ok = $false; ExitCode = 'TIMEOUT'; Text = $text }
    }

    $raw = (Get-Content -LiteralPath $ec -ErrorAction SilentlyContinue | Select-Object -First 1)
    $code = $null
    if ($null -ne $raw -and "$raw".Trim() -match '^-?\d+$') { $code = [int]"$raw".Trim() }
    return @{ Ok = ($code -eq 0); ExitCode = $code; Text = $text }
}

function Get-ProcessExePath($Process) {
    # 取进程的可执行文件路径；取不到（已退出 / 权限不足）返回 $null（**不杀**）。
    try { if ($Process.Path) { return [string]$Process.Path } } catch { }
    try { return [string]$Process.MainModule.FileName } catch { }
    return $null
}

function Stop-LeftoverApp([string]$Label) {
    # 只清**本 checkout 那个 exe 路径**的残留（0.10.7，清理线 · 测试可信）：
    # 旧写法按进程名全杀，会把同一台机器上另一个 checkout 正在跑的自检一起杀掉（对方退出码 -1）。
    # 本路径的残留照样全清 —— 判据没有放松，只是不再跨 checkout 误杀。
    $selfExe = [System.IO.Path]::GetFullPath($script:exePath)
    $procs = @(Get-Process -Name 'SimOCR' -ErrorAction SilentlyContinue | Where-Object {
        $path = Get-ProcessExePath $_
        if ([string]::IsNullOrWhiteSpace($path)) { return $false }
        return ([System.IO.Path]::GetFullPath($path) -ieq $selfExe)
    })

    foreach ($p in $procs) {
        Write-Host ("  [清理] 结束残留进程 {0}（pid {1}，{2}）" -f $p.ProcessName, $p.Id, $Label)
        try { $p.Kill() } catch { }
    }

    if ($procs.Count -gt 0) { Start-Sleep -Milliseconds 400 }
}

function Get-LogDelta([long]$Offset) {
    if (-not (Test-Path -LiteralPath $script:appLog)) { return '' }
    $fs = [System.IO.File]::Open($script:appLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($Offset -gt $fs.Length) { $Offset = 0 }
        [void]$fs.Seek($Offset, [System.IO.SeekOrigin]::Begin)
        $reader = New-Object System.IO.StreamReader($fs, (New-Object System.Text.UTF8Encoding($false)))
        return $reader.ReadToEnd()
    } finally {
        $fs.Dispose()
    }
}

function Invoke-GateApp {
    param(
        [Parameter(Mandatory = $true)][string[]]$AppArgs,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$TimeoutSeconds = 300
    )

    $script:runSeq++
    $tag = "{0}-{1:d2}" -f $Label, $script:runSeq
    $bat = Join-Path $script:scratch "$tag.cmd"
    $appOut = Join-Path $script:scratch "$tag.appout.txt"
    $ecFile = Join-Path $script:scratch "$tag.ec.txt"
    Remove-Item -LiteralPath $ecFile -Force -ErrorAction SilentlyContinue

    $argLine = ($AppArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $lines = @(
        '@echo off',
        "set `"TEMP=$script:scratch`"",
        "set `"TMP=$script:scratch`"",
        "set `"SIMOCR_GATE_TEMP=$script:scratch`"",
        "cd /d `"$script:repoRoot`"",
        "`"$script:exePath`" $argLine > `"$appOut`" 2>&1",
        "> `"$ecFile`" echo %ERRORLEVEL%"
    )
    Set-Content -LiteralPath $bat -Value $lines -Encoding ASCII

    Stop-LeftoverApp $tag
    $logOffset = 0L
    if (Test-Path -LiteralPath $script:appLog) { $logOffset = (Get-Item -LiteralPath $script:appLog).Length }

    $cmdExe = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $cmdExe -ArgumentList '/d', '/c', $bat -PassThru `
        -RedirectStandardOutput (Join-Path $script:scratch "$tag.cmdout.txt") `
        -RedirectStandardError (Join-Path $script:scratch "$tag.cmderr.txt")

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    $watch.Stop()

    $exitCode = 'TIMEOUT'
    if (-not $p.HasExited) {
        Stop-LeftoverApp "$tag（超时）"
        try { $p.Kill() } catch { }
    } else {
        $raw = (Get-Content -LiteralPath $ecFile -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($null -ne $raw -and "$raw".Trim() -match '^-?\d+$') { $exitCode = [int]"$raw".Trim() }
    }

    return @{
        ExitCode = $exitCode
        Seconds  = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Log      = (Get-LogDelta $logOffset)
        StdOut   = (Read-Utf8Text $appOut)
    }
}

# ---------------------------------------------------------------------------
# 启动一次**无 testhost 的控制台运行器**（0.10.7 起）：
#   tests\SimOCR.Tests\bin\<配置>\net10.0-windows\SimOCR.Tests.exe
#   与 gate-check.ps1 的 Invoke-GateRunner 同规矩：TEMP/TMP 指向可写临时目录、
#   工作目录 = 仓库根、退出码落盘；stdout 就是判据（运行器显式按 UTF-8 写）。
#   为什么要它：`p01-snap-pairing`（吸附几何 28 条）与 `p04-linegrid-split`
#   （手工分割线 6 条）的断言本体已经移出发布产物，只剩运行器这一条执行路径。
# ---------------------------------------------------------------------------

function Invoke-GateRunner {
    param(
        [Parameter(Mandatory = $true)][string[]]$RunnerArgs,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$TimeoutSeconds = 300
    )

    if (-not (Test-Path -LiteralPath $script:runnerPath)) {
        return @{ ExitCode = 'MISSING'; Seconds = 0; Log = ''; StdOut = '' }
    }

    $script:runSeq++
    $tag = "{0}-{1:d2}" -f $Label, $script:runSeq
    $bat = Join-Path $script:scratch "$tag.cmd"
    $runOut = Join-Path $script:scratch "$tag.runout.txt"
    $ecFile = Join-Path $script:scratch "$tag.ec.txt"
    Remove-Item -LiteralPath $ecFile -Force -ErrorAction SilentlyContinue

    $argLine = ($RunnerArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $lines = @(
        '@echo off',
        "set `"TEMP=$script:scratch`"",
        "set `"TMP=$script:scratch`"",
        "cd /d `"$script:repoRoot`"",
        "`"$script:runnerPath`" $argLine > `"$runOut`" 2>&1",
        "> `"$ecFile`" echo %ERRORLEVEL%"
    )
    Set-Content -LiteralPath $bat -Value $lines -Encoding ASCII

    $cmdExe = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $cmdExe -ArgumentList '/d', '/c', $bat -PassThru `
        -RedirectStandardOutput (Join-Path $script:scratch "$tag.cmdout.txt") `
        -RedirectStandardError (Join-Path $script:scratch "$tag.cmderr.txt")

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    $watch.Stop()

    $exitCode = 'TIMEOUT'
    if (-not $p.HasExited) {
        try { $p.Kill() } catch { }
    } else {
        $raw = (Get-Content -LiteralPath $ecFile -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($null -ne $raw -and "$raw".Trim() -match '^-?\d+$') { $exitCode = [int]"$raw".Trim() }
    }

    $text = Read-Utf8Text $runOut
    return @{
        ExitCode = $exitCode
        Seconds  = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Log      = $text
        StdOut   = $text
    }
}

# 按用例的 kind 走对应入口（app CLI 或控制台运行器）——注入态与还原态共用同一份分派，
# 免得"注入态钉 runner、还原态却跑 app"这种两侧不一致的假绿。
function Invoke-CaseGate {
    param(
        [Parameter(Mandatory = $true)][hashtable]$CaseDef,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$GateArgs
    )

    if ([string]$CaseDef['kind'] -eq 'runner') {
        return Invoke-GateRunner -RunnerArgs $GateArgs -Label $Label -TimeoutSeconds ([int]$CaseDef['timeout'])
    }

    return Invoke-GateApp -AppArgs $GateArgs -Label $Label -TimeoutSeconds ([int]$CaseDef['timeout'])
}

# ---------------------------------------------------------------------------
# 注入用例表
#   id / file / anchor / replacement / gate（占位符 {fixture} {ocrwindow} {accept}）
#   expect = 注入后日志里必须出现的失败标记；ok = 还原后日志里必须出现的通过标记
# ---------------------------------------------------------------------------

$fixturePng = Join-Path $script:repoRoot 'build\fixtures\linegrid.png'
if (-not $OcrWindowImage) { $OcrWindowImage = $fixturePng }
if (-not $AcceptImage) { $AcceptImage = $fixturePng }

$script:cases = @(
    @{
        id          = 'p01-snap-pairing'
        file        = 'src\SimOCR\Interop\WindowBounds.cs'
        anchor      = 'foreach (var candidate in rawCandidates)'
        replacement = 'foreach (var candidate in DropFullyOccluded(rawCandidates).Visible)'
        # 0.10.7：这条注入打的是 `WindowBounds.BuildSnapTargets` 的配对链，
        # 断言本体（SnapGeometryChecks 的 28 条，含 23e 的"错位报警器"）已经**移出发布产物**，
        # 所以门禁从 app CLI（--selftest-snap）改成控制台运行器（--case=snap-geometry）。
        # 被测代码没变（注入的仍是生产 src/Interop/WindowBounds.cs），判据也没变松：
        # 下面 ok 里的 `[几何] 用例 28 条` + `[通过] 生产配对：正确配对不吸附`
        # 与 build\gate-check.ps1 的 `snap-geometry` 用例**同一口径**。
        kind        = 'runner'
        gate        = @('--case=snap-geometry')
        expect      = '\[FAIL\]'
        ok          = '\[几何\] 用例 28 条[\s\S]*\[通过\] 生产配对：正确配对不吸附[\s\S]*\[通过\] snap-geometry：28 条断言'
        timeout     = 180
        why         = 'targets 由「过滤后的可见列表」构建 → 与 raw.Candidates 下标配对错位 → 吸附几何断言必须炸（0.10.7：断言移出发布产物，注入用例改钉 runner）'
    },
    @{
        # 另一半：上面那条走的是**合成**用例表（MispairedScene），这条走**真实窗口**：
        # 同一个锚点、同一种错位，但门禁是 --snap-probe-occlusion=assert（起两扇真实宿主窗口，
        # 实测 z 序 → occluded > 0 → 逐项校验 targets 与 raw.Candidates 的下标配对不变量
        # + 被遮盖的四条边都不吸）。合成用例证明"逻辑能测出错位"，这条证明"真实枚举路径也测得出"。
        id          = 'p01b-snap-probe-pairing'
        file        = 'src\SimOCR\Interop\WindowBounds.cs'
        anchor      = 'foreach (var candidate in rawCandidates)'
        replacement = 'foreach (var candidate in DropFullyOccluded(rawCandidates).Visible)'
        gate        = @('--snap-probe-occlusion=assert')
        expect      = '\[失败\]'
        ok          = '\[结论\] OK'
        timeout     = 300
        why         = '（真实窗口端到端）：targets 由过滤列表构建 → 真实候选列表下标错位 → --snap-probe-occlusion=assert 必须非 0'
    },
    @{
        id          = 'p04-ocr-window-edit'
        file        = 'src\SimOCR\Diagnostics\SelfTest.cs'
        anchor      = 'window.TestSetText(editedText);'
        replacement = 'window.TestSetText(string.Empty);'
        gate        = @('--selftest-ocr-window={ocrwindow}')
        image       = 'ocrwindow'
        expect      = '\[失败\] 手动编辑的文本没有进导出文件'
        ok          = '\[结论\] OK'
        timeout     = 240
        why         = '编辑文本没进导出这条断言必须影响退出码'
    },
    @{
        id          = 'p04-linegrid-split'
        # 此后这条断言搬到了共享源码（tests/SimOCR.Tests/PureLogic/ManualLineSplitChecks.cs）；
        # **0.10.7**：那份源码只编进**测试程序集**（不再链接进 SimOCR.exe），
        # 所以门禁从 app CLI（--selftest-linegrid）改成控制台运行器（--case=manual-line-split）。
        # **锚点一个字没动**（仍然是那一行 `rebuilt == "166s166s"`），注入后运行器必须变红。
        file        = 'tests\SimOCR.Tests\PureLogic\ManualLineSplitChecks.cs'
        anchor      = 'ctx.Assert(rebuilt == "166s166s"'
        replacement = 'ctx.Assert(rebuilt != "166s166s"'
        kind        = 'runner'
        gate        = @('--case=manual-line-split')
        expect      = '字符不允许被切开或丢失'
        ok          = '\[通过\] manual-line-split：6 条断言'
        timeout     = 180
        why         = '切分断言的字符不变量失败必须进退出码（该断言走 Error 通道，行首是 [切分②b] 而不是 [失败]；0.10.7 起在 runner 的退出码里）'
    },
    @{
        id          = 'p04-accept-state'
        file        = 'src\SimOCR\Diagnostics\TableAcceptance.cs'
        anchor      = 'if (!window.TestStateConsistent())'
        replacement = 'if (window.TestStateConsistent())'
        gate        = @('--accept-table={accept}', '--accept-enhance')
        image       = 'accept'
        expect      = '\[失败\] 状态不自洽'
        ok          = '验收结束：1/1 张通过'
        timeout     = 420
        why         = 'RunOne 里 TestStateConsistent 失败必须让验收非 0 退出'
    }
)

if ($Case.Count -gt 0) {
    $script:cases = @($script:cases | Where-Object { $Case -contains $_['id'] })
    if ($script:cases.Count -eq 0) {
        Write-Host "[用法] -Case 没有匹配任何用例。可用：p01-snap-pairing / p01b-snap-probe-pairing / p04-ocr-window-edit / p04-linegrid-split / p04-accept-state"
        exit 2
    }
}

# ---------------------------------------------------------------------------
# 开工前检查
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath (Join-Path $script:repoRoot '.git'))) { throw "这里看起来不是仓库根：$script:repoRoot" }
New-Item -ItemType Directory -Force -Path $script:scratch | Out-Null

Write-Host 'SimOCR 缺陷注入门禁'
Write-Host ("  仓库     : {0}" -f $script:repoRoot)
Write-Host ("  配置     : {0}" -f $Configuration)
Write-Host ("  用例数   : {0}" -f $script:cases.Count)
Write-Host ("  临时目录 : {0}" -f $script:scratch)

Write-Section '开工前检查（目标文件必须干净，否则拒绝启动）'
$refused = New-Object System.Collections.Generic.List[string]
$touchedRelative = New-Object System.Collections.Generic.List[string]
$skipCases = @{}
foreach ($c in $script:cases) {
    $rel = [string]$c['file']
    if (-not $touchedRelative.Contains($rel)) { $touchedRelative.Add($rel) }
    $target = Join-Path $script:repoRoot $rel
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "  [错误] 目标文件不存在：$rel"
        $refused.Add("$rel 不存在")
        continue
    }

    $clean = Test-FileClean $rel
    if ($clean.Clean) {
        Write-Host "  [干净] $rel"
    } else {
        Write-Host "  [拒绝] $rel —— $($clean.Why)"
        $refused.Add("$rel 有未提交改动（$($clean.Why)）")
    }

    # 素材是否齐（缺素材的用例跳过，不算失败）
    if ($null -ne $c['image']) {
        $imgPath = switch ([string]$c['image']) {
            'fixture' { $fixturePng }
            'ocrwindow' { $OcrWindowImage }
            'accept' { $AcceptImage }
            default { $null }
        }
        if ($null -eq $imgPath -or -not (Test-Path -LiteralPath $imgPath)) {
            Write-Host "  [跳过] 用例 $($c['id'])：输入图缺失 $imgPath（不影响退出码）"
            $skipCases[[string]$c['id']] = $true
        }
    }
}

if ($refused.Count -gt 0) {
    Write-Host ''
    Write-Host '[中止] 目标文件不干净，未做任何修改。请先提交或 stash 你的改动，再跑注入门禁：'
    foreach ($r in $refused) { Write-Host "  - $r" }
    exit 2
}

if (-not $SkipBuild) {
    Write-Section '基线构建（注入前必须是绿的）'
    $b = Invoke-Build -Label 'baseline'
    ($b.Text -split "`n" | Select-Object -Last 6) | ForEach-Object { Write-Host "  $_" }
    if (-not $b.Ok) {
        Write-Host "[中止] 基线构建失败（退出码 $($b.ExitCode)）——先把仓库构建修好再跑注入门禁"
        exit 2
    }
}

# ---------------------------------------------------------------------------
# 主循环
# ---------------------------------------------------------------------------

$results = New-Object System.Collections.Generic.List[hashtable]
$injectedAny = $false

try {
    foreach ($c in $script:cases) {
        $id = [string]$c['id']
        Write-Section "用例 $id"

        if ($skipCases.ContainsKey($id)) {
            Write-Host '  [跳过] 输入素材缺失'
            $results.Add(@{ id = $id; injected = '—'; restored = '—'; verdict = '跳过'; why = [string]$c['why'] })
            continue
        }

        $rel = [string]$c['file']
        $target = Join-Path $script:repoRoot $rel
        $orig = [System.IO.File]::ReadAllBytes($target)
        $script:backups[$target] = $orig

        $info = Get-TextInfo -Bytes $orig -Path $target
        $text = [string]$info['Text']
        $anchor = [string]$c['anchor']

        $hits = [regex]::Matches($text, [regex]::Escape($anchor)).Count
        Write-Host ("  [锚点] {0} :: 「{1}」命中 {2} 次（要求恰好 1 次）" -f $rel, $anchor, $hits)
        if ($hits -ne 1) {
            $why = if ($hits -eq 0) { '锚点缺失（源码漂了，或锚点写错）' } else { "锚点不唯一（命中 $hits 次）" }
            Write-Host "  [错误] $why —— 本用例未注入，按失败记账"
            $script:problems.Add("$id：$why")
            $results.Add(@{ id = $id; injected = '—'; restored = '—'; verdict = '未通过'; why = $why })
            continue
        }

        $injected = $text.Replace($anchor, [string]$c['replacement'])
        if ($injected -eq $text) {
            Write-Host '  [错误] 替换后文本没有变化 —— 本用例未注入，按失败记账'
            $script:problems.Add("$id：替换后文本没有变化")
            $results.Add(@{ id = $id; injected = '—'; restored = '—'; verdict = '未通过'; why = '替换无效果' })
            continue
        }

        [System.IO.File]::WriteAllText($target, $injected, $info['Encoding'])
        $injectedAny = $true

        # 注入必须仍然可编译：不然"构建失败"会被误当成"门禁抓住了缺陷"
        $b = Invoke-Build -Label "$id-injected"
        if (-not $b.Ok) {
            Write-Host "  [错误] 注入后构建失败（退出码 $($b.ExitCode)）—— 注入本身有语法/类型问题"
            ($b.Text -split "`n" | Where-Object { $_ -match 'error' } | Select-Object -First 5) | ForEach-Object { Write-Host "        $_" }
            $script:problems.Add("$id：注入后构建失败（退出码 $($b.ExitCode)）")
            $results.Add(@{ id = $id; injected = 'BUILD'; restored = '—'; verdict = '未通过'; why = '注入后构建失败' })
            continue
        }

        Write-Host '  [构建] 注入态编译通过'

        $gateArgs = @()
        foreach ($a in $c['gate']) {
            $gateArgs += ([string]$a).Replace('{fixture}', $fixturePng).Replace('{ocrwindow}', $OcrWindowImage).Replace('{accept}', $AcceptImage)
        }

        Write-Host ("  [门禁] {0} {1}" -f $(if ([string]$c['kind'] -eq 'runner') { $script:runnerPath } else { $script:exePath }), ($gateArgs -join ' '))
        $inj = Invoke-CaseGate -CaseDef $c -Label "$id-injected-gate" -GateArgs $gateArgs
        $detected = ("$($inj.ExitCode)" -ne 'TIMEOUT') -and ([int]$inj.ExitCode -ne 0)
        $logHit = ([string]$inj.Log -match [string]$c['expect'])
        # 0.10.5（与 TableAcceptance / 遮挡探针**同一口径**）：注入那次若返回 **5（用例不成立）**，
        # 说明这次**根本没跑到被测代码**（真实鼠标前置没发生 / 双窗口场景装不下）→ **无法判定**。
        # 既不能算"注入被检出"（那是假绿），也不能算"注入未检出"（那是假红）→ 记 [跳过]。
        $injInconclusive = ("$($inj.ExitCode)" -ne 'TIMEOUT') -and ([int]$inj.ExitCode -eq 5)
        Write-Host ("  [注入] 退出码={0} 用时={1}s 日志命中「{2}」={3}" -f $inj.ExitCode, $inj.Seconds, $c['expect'], $logHit)
        if ($injInconclusive) {
            Write-Host '       退出码 5 = 用例不成立（本机这次没能让前置步骤发生）→ 本次无法判定注入是否被检出'
        } else {
            if (-not $detected) { Write-Host '       期望非 0 退出码（0 或 TIMEOUT 都算未检出）' }
            if (-not $logHit) { Write-Host ("       日志里没有「{0}」" -f $c['expect']) }
        }

        # 还原（按字节）并验证
        $left = @(Restore-All)
        $restoredOk = ($left.Count -eq 0)
        Write-Host ("  [还原] 字节级还原一致={0}" -f $restoredOk)

        $rb = Invoke-Build -Label "$id-restored"
        if (-not $rb.Ok) {
            Write-Host "  [错误] 还原后构建失败（退出码 $($rb.ExitCode)）"
            $script:problems.Add("$id：还原后构建失败（退出码 $($rb.ExitCode)）")
            $results.Add(@{ id = $id; injected = $inj.ExitCode; restored = 'BUILD'; verdict = '未通过'; why = '还原后构建失败' })
            continue
        }

        # 还原态：**有限重试**（最多 3 次）。只有返回 5（用例不成立）才重试 ——
        # 真失败（非 0/非 5，或日志命中失败标记）与真通过都不重试，免得把"真失败"重试成"看起来像抖动"。
        # `Invoke-GateApp` 每次进来都会先 `Stop-LeftoverApp`（单实例守卫会让新进程被转发后静默退出＝假绿），
        # 两次之间再显式杀一次 + 等 4s。
        $maxRestoreAttempts = 3
        $restoreAttempt = 0
        $res = $null
        while ($restoreAttempt -lt $maxRestoreAttempts) {
            $restoreAttempt++
            $res = Invoke-CaseGate -CaseDef $c -Label "$id-restored-gate" -GateArgs $gateArgs
            $resInconclusive = ("$($res.ExitCode)" -ne 'TIMEOUT') -and ([int]$res.ExitCode -eq 5)
            if (-not $resInconclusive) { break }
            Write-Host ("  [还原] 第 {0}/{1} 次返回 5（用例不成立）—— 杀残留 + 等待后再试" -f $restoreAttempt, $maxRestoreAttempts)
            if ($restoreAttempt -lt $maxRestoreAttempts) {
                Stop-LeftoverApp "$id-restored-retry"
                Start-Sleep -Seconds 4
            }
        }

        $restoredGreen = ("$($res.ExitCode)" -ne 'TIMEOUT') -and ([int]$res.ExitCode -eq 0)
        $okHit = ([string]$res.Log -match [string]$c['ok'])
        $noFail = -not ([string]$res.Log -match [string]$c['expect'])
        $resInconclusive = ("$($res.ExitCode)" -ne 'TIMEOUT') -and ([int]$res.ExitCode -eq 5)
        Write-Host ("  [还原] 退出码={0} 用时={1}s（共试 {2} 次）日志命中「{3}」={4} 不含失败标记={5}" -f `
            $res.ExitCode, $res.Seconds, $restoreAttempt, $c['ok'], $okHit, $noFail)

        $verdict = '通过'
        $why = ''
        # 判定顺序（**真失败永远优先，不被 5/跳过吞掉**）：
        #   ① 还原不净 / 还原态日志出现失败标记 → 未通过
        #   ② 注入态或还原态是 5（用例不成立）    → [跳过]（不计入通过，也不影响退出码）
        #   ③ 注入未被检出                        → 未通过
        #   ④ 还原后没恢复绿色                    → 未通过
        if (-not $restoredOk) {
            $verdict = '未通过'
            $why = "还原不净（$($left -join '；')）"
        } elseif (-not $noFail) {
            $verdict = '未通过'
            $why = "还原态日志里仍有失败标记「$($c['expect'])」"
        } elseif ($injInconclusive -or $resInconclusive) {
            $verdict = '跳过'
            $why = if ($injInconclusive) {
                '用例不成立：注入态返回 5（本机这次没能让前置步骤发生）→ 无法判定注入是否被检出'
            } else {
                "用例不成立：还原态连续 $restoreAttempt 次返回 5（环境无法让前置发生）"
            }
        } elseif (-not $detected -or -not $logHit) {
            $verdict = '未通过'
            $why = '注入未被门禁检出'
        } elseif (-not $restoredGreen -or -not $okHit) {
            $verdict = '未通过'
            $why = '还原后没有恢复绿色'
        }

        if ($verdict -eq '未通过') {
            $script:problems.Add("$id：$why（注入退出码=$($inj.ExitCode)，还原退出码=$($res.ExitCode)）")
        } elseif ($verdict -eq '跳过') {
            Write-Host ("  [跳过] {0}" -f $why)
            Write-Host '         （用例不成立 → 不影响本脚本退出码；**不计入通过**）'
        }

        $results.Add(@{ id = $id; injected = $inj.ExitCode; restored = $res.ExitCode; verdict = $verdict; why = $why })
    }
} finally {
    try {
    # 无论怎么退出：先按字节还原所有备份，再跑一次干净构建，最后断言 git 工作区没留痕迹
    Write-Section '收尾（还原 + 校验）'
    $left = @(Restore-All)
    if ($left.Count -gt 0) {
        Write-Host '[严重] 以下文件未能按字节还原：'
        foreach ($p in $left) { Write-Host "  - $p" }
        $script:problems.Add("有文件未能按字节还原：$($left -join '；')")
    } else {
        Write-Host ("  [还原] {0} 个文件全部按字节还原" -f $script:backups.Count)
    }

    if ($injectedAny) {
        $fb = Invoke-Build -Label 'final-clean'
        if ($fb.Ok) {
            Write-Host '  [构建] 已按当前源码重新构建（避免留下"注入态"的可执行文件）'
        } else {
            Write-Host "  [严重] 收尾构建失败（退出码 $($fb.ExitCode)）"
            $script:problems.Add("收尾构建失败（退出码 $($fb.ExitCode)）")
        }
    }

    $relList = @($touchedRelative)
    if ($relList.Count -gt 0) {
        $st = Invoke-Git (@('status', '--porcelain', '--') + $relList)
        if ($st.Text.Length -eq 0) {
            Write-Host ("  [git] status --porcelain -- <{0} 个目标文件> 为空（没有注入残留）" -f $relList.Count)
        } else {
            Write-Host '  [严重] git status 非空，说明有注入残留：'
            Write-Host $st.Text
            $script:problems.Add("git status 非空：$($st.Text)")
        }
    }
    } catch {
        Write-Host "[严重] 收尾异常："
        $script:problems.Add("收尾异常：\")
    }
}

# ---------------------------------------------------------------------------
# 汇总
# ---------------------------------------------------------------------------

Write-Section '注入门禁结果'
Write-Host '  id                    | 注入退出码 | 还原退出码 | 结论'
Write-Host '  ----------------------+------------+------------+--------'
foreach ($x in $results) {
    Write-Host ("  {0,-21} | {1,10} | {2,10} | {3}" -f $x['id'], $x['injected'], $x['restored'], $x['verdict'])
    if ($x['why']) { Write-Host ("        {0}" -f $x['why']) }
}

if (-not $KeepScratch) {
    Remove-Item -LiteralPath $script:scratch -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "  临时目录已保留：$script:scratch"
}

if ($script:problems.Count -gt 0) {
    Write-Host ''
    foreach ($p in $script:problems) { Write-Host "  [问题] $p" }
    Write-Host ''
    Write-Host '=== 注入门禁：未通过 ==='
    exit 1
}

Write-Host ''
Write-Host '=== 注入门禁：通过 ==='
exit 0
