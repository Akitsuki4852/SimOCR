<#
.SYNOPSIS
    核心门禁：一次跑完「不需要业务素材」的自检，并用随仓合成夹具做「线格 → 导出 → 对照」回归。

.DESCRIPTION
    本脚本只依赖仓库自带的东西：构建出的 SimOCR.exe + PP-OCRv5 模型（build\fetch-models.ps1）
    + build\fixtures 下的合成夹具。**不含任何内部素材路径与客户单据号**，在任何 checkout 上都能跑。

    跑什么（核心，全部只吃参数、不依赖业务素材）

      **清单共 12 条**：下面 1)~10) 是参数型；另两条真实鼠标 / 排序夹具用例
      （`fixture-accept-enhance` / `fixture-sort-tri-state`）也在同一张 `$coreCases` 表里。
      1) --selftest-snap                 窗口吸附**真实窗口探针**（起两扇已知几何的窗口，核对
         GetWindowRect / DWMWA / 候选枚举三者的几何一致性与 DPI 感知）。
      2) --snap-probe-occlusion=assert   真实双窗口端到端（窗口吸附配对）：先起的小窗被后起的
         大窗整窗盖住 → 被遮盖的四条边都不吸、遮盖者的四条边照常吸，并逐项校验
         targets 与 raw.Candidates 的下标配对不变量（`occluded > 0` 真的是端到端测出来的）。
         * 几何由**真实虚拟屏幕**推导（B ≈ 70%×55% 居中留边距、A 严格内含于 B），所以
           小屏机器（≥800×600）也能跑；屏外装不下的边会被逐条跳过并在日志里写明。
         * **退出码 5 = 用例不成立/环境不支持**（桌面小于 800×600、没有交互桌面、
           遮盖者 B 自己被**第三个窗口**整窗盖住……）：按「跳过（不影响退出码）」处理，
           绝不当成失败；真回归仍然是退出码 4 → 门禁失败。
      3) --selftest-overlay              覆盖层端到端（按钮 → 截图 → 导出）
      4) --selftest-capture=10           连拍压力（10 次）
      5) --selftest-linegrid=<夹具>      合成夹具：线格检测 → 识别 → 导出
         并额外做「导出内容对照」：%TEMP%\simocr-linegrid-selftest.txt 必须与
         build\fixtures\linegrid-expected.txt 逐值一致，且 .xlsx 真实写出。
      6) pure-logic-runner               纯逻辑断言（配置档单一来源 3 条 / 吸附几何 28 条 /
         手工分割线 6 条 / 表格差异工具 10 条 / 导出核心 31 条 = **5 条用例 / 78 条断言**）由**不依赖 testhost** 的
         控制台运行器（tests\SimOCR.Tests\bin\<配置>\net10.0-windows\SimOCR.Tests.exe）执行。
         为什么单列一条：受限环境（CI 沙箱 / 无交互桌面）里 testhost 可能起不来，
         xUnit 那条路就不可执行；运行器与 xUnit 共用同一份断言源码（tests/SimOCR.Tests/PureLogic/*.cs），
         所以覆盖是真的。
         **okRegex 把 `5 条用例 / 78 条断言` 一起钉住**：只钉"全部通过"时，
         运行器少看到一条用例（实测过 4/47 → 3/37）仍然是绿的 —— 那是静默退化。
      7) snap-geometry                   吸附几何 28 条（原 `--selftest-snap` 的前半段），
         由运行器执行；okRegex 逐字保留两条硬判据（`[几何] 用例 28 条` + 生产配对链最后一条
         `[通过] 生产配对：正确配对不吸附`），并补钉 `28 条断言` 的计数。
      8) manual-line-split               手工分割线切分 6 条（原 `--selftest-linegrid` 的前置断言）
         由运行器执行；补钉 `6 条断言` 的计数（`inject-check.ps1` 的 `p04-linegrid-split`
         注入用例也钉这条 runner 路径）。
      9) profile-source                  配置档单一来源 3 条（原 `--selftest-profile`）；
         那个 CLI 开关已退役，判据改钉运行器的 `--case=profile-source`。
      10) xunit-runner                   xUnit 消费路径本身：
          `dotnet test tests\SimOCR.Tests\SimOCR.Tests.csproj -c Release --no-build`；
          okRegex 把 `失败: 0` / `通过: 68` / `已跳过: 0` / `总计: 68` 一起钉死（少一条用例就是红）。
          为什么 `--no-build`：本脚本的构建段已经建过测试工程，再让 `dotnet test` 自己重建会与
          `-SkipBuild` 的语义冲突（它的契约是"不重新编译"）；缺产物时按**失败**记账并提示
          "不带 -SkipBuild 跑一次"，不静默跳过。

    另有一道**前置**自检（见下）：build\*.ps1 里含非 ASCII 字节的脚本必须带 UTF-8 BOM。

    EXIT
      0 = 核心门禁全通过（素材回归按是否提供决定跑或跳过，跳过不影响退出码）
      1 = 有门禁失败（编码前置自检不过 / 日志 / 退出码 / 对照不一致）
      2 = 用法错误（例如 -IncludeMaterial 但没给 -MaterialDir）

      另外：**单个核心用例**可以用退出码 5 表示「用例不成立/环境不支持」（例如真实双窗口用例需要
      一块 ≥800×600 的交互桌面）。这类用例按「跳过」记账，**不影响本脚本的退出码**：
      0 仍然表示"能跑的用例全通过"，而真回归（例如 targets/raw 配对错位）依旧是退出码 4 → 1。
      为什么要有这一条：写死宿主几何的旧用例在小屏机器/小 VM 上会**假失败**（实测 1156×768 的 VM）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build\gate-check.ps1

.EXAMPLE
    # 有素材时追加硬回归基线：素材路径只出现在**调用方**，本脚本里不留任何内部路径
    powershell -NoProfile -ExecutionPolicy Bypass -File build\gate-check.ps1 `
        -MaterialDir <素材目录> `
        -MaterialImage <素材目录>\<单据图.jpg> `
        -MaterialProfile example-invoice-packing `
        -MaterialScript <素材回归脚本.ps1>

.EXAMPLE
    # 强制跳过素材回归（CI 里仓库没有内部素材时）
    powershell -NoProfile -ExecutionPolicy Bypass -File build\gate-check.ps1 -SkipMaterial

.EXAMPLE
    # 顺带证明闸门仍然可信（慢：每个用例都要「注入 → 构建 → 跑 → 还原 → 构建 → 跑」）
    powershell -NoProfile -ExecutionPolicy Bypass -File build\gate-check.ps1 -IncludeInjection

.NOTES
    阶段收尾推荐顺序
      1) build\gate-check.ps1                                  核心门禁（快，秒级~分钟级）
      2) build\gate-check.ps1 -IncludeInjection                证明「断言仍然能失败」（慢，约数分钟）
      3) build\gate-check.ps1 -IncludeMaterial -MaterialDir …  有内部素材时的硬回归基线
      4) build\make-icon.ps1                                   可选：重新生成图标（缺素材时自行跳过）
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 只跑指定 id（调试用，例如 -Case selftest-snap）
    [string[]]$Case = @(),

    # 素材型硬回归基线：素材根目录。**没有默认值**（build\ 不随内部素材发布）
    [string]$MaterialDir,

    # 素材图（绝对路径，或相对 -MaterialDir）。不给则取 -MaterialDir 下第一张 *.jpg / *.png
    [string]$MaterialImage,

    # 传给 --profile=；不给则不传
    [string]$MaterialProfile,

    # 素材自检日志里必须出现的正则（例如某次基线覆盖率）
    [string]$MaterialExpect,

    # dev 侧的素材回归脚本（内部单据号/参考表路径都写在它里面），退出码非 0 即失败
    [string]$MaterialScript,

    # 显式要求跑素材回归（此时必须给 -MaterialDir）
    [switch]$IncludeMaterial,

    # 强制跳过素材回归（即便给了 -MaterialDir）
    [switch]$SkipMaterial,

    # 追加缺陷注入门禁（build\inject-check.ps1）
    [switch]$IncludeInjection,

    # 单个自检入口的超时（秒）；素材自检自动放宽到 3 倍
    [int]$TimeoutSeconds = 300,

    # 不重新构建（默认先构建一次，确保跑的是当前源码）
    [switch]$SkipBuild,

    # 保留临时目录（默认跑完删除；目录在 bin\ 下，不进版本库）
    [switch]$KeepScratch,

    # 把「src/ tests/ 下有未被 git 跟踪的源码文件」从**提醒**升格为**硬失败**。
    # 为什么默认只是提醒：开发期间新写的源码文件本来就是未跟踪的（要等 git add），
    # 默认判死会让"提交前"这段窗口里整条门禁恒红、把其它判据的结论淹掉。
    # **提交前**应当带上这个开关跑一次 —— 它能拦住"漏带文件就提交"这类问题。
    [switch]$StrictUntrackedSources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# stdout 可能被重定向到文件（gate-check -IncludeInjection、CI 日志、人工 > log.txt）：
# 显式设成 UTF-8，否则 Windows PowerShell 5.1 按活动代码页写出，中文与 [失败] 这类标记会乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$script:repoRoot = Split-Path -Parent $PSScriptRoot
$script:exeDir = Join-Path $script:repoRoot "src\SimOCR\bin\$Configuration\net10.0-windows"
$script:exePath = Join-Path $script:exeDir 'SimOCR.exe'
# scratch 目录带上**本进程 PID**。
# 为什么：scratch 原来是固定路径，而本脚本启动时会清空它、收尾时又整目录递归删掉 ——
# 两个门禁并发跑（例如 dev 侧与 build 侧各起一个，或同一工作区里两个代理各跑一次）时会
# **互相删掉对方正在用的包装 .cmd / .ec.txt / 自检 TEMP**，症状是假红：
#   * `native-*.cmd` 在 Set-Content 之后、Start-Process 之前被删 → 子进程报
#     "'…\native-build-slnx-01.cmd' is not recognized as an internal or external command"，
#     于是退出码文件永远不落地 → 判成"编译超时"；
#   * 自检的 TEMP 指向被删掉的目录 → 应用写出失败、退出码异常（实测 -1）。
# 带上 PID 之后两份门禁各用各的目录，互不影响；判据一条都没变。
$script:scratch = Join-Path $script:repoRoot "src\SimOCR\bin\gate-scratch\$Configuration-$PID"
$script:appLog = Join-Path $script:exeDir ("logs\simocr-{0}.log" -f (Get-Date).ToString('yyyyMMdd'))
# 纯逻辑断言的**无 testhost 控制台运行器**（测试项目同时也是控制台程序）。
# 为什么需要它：受限环境（CI 沙箱 / 无交互桌面）里 testhost 可能起不来
# （SetParentProcessExitCallback/OpenProcess → Win32Exception (5)），`dotnet test` 在启动阶段就崩；
# 纯逻辑断言若只有 xUnit 一条路，那些环境里就完全没有覆盖。
$script:testProject = Join-Path $script:repoRoot 'tests\SimOCR.Tests\SimOCR.Tests.csproj'
$script:runnerPath = Join-Path $script:repoRoot "tests\SimOCR.Tests\bin\$Configuration\net10.0-windows\SimOCR.Tests.exe"
$script:runSeq = 0
$script:nativeSeq = 0
$script:problems = New-Object System.Collections.Generic.List[string]

# 本版所有"跳过"的台账（收尾统一打印"跳过 N 条 / 其中受保护断言 M 条"）。
$script:skips = New-Object System.Collections.Generic.List[hashtable]

# ---------------------------------------------------------------------------
# 小工具
# ---------------------------------------------------------------------------

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host "=== $Text ==="
}

# ---------------------------------------------------------------------------
# 给"跳过"记账
#
# 为什么必须有这一步：`[跳过]`（退出码 5 = 用例不成立）的语义本身是对的 —— 虚拟桌面太小、
#   没有交互桌面这类"环境不支持"不该判失败。问题是它同时打开了一条**静默退化路径**：
#   某条受保护的断言用例在某环境里**恒为 5**，于是它**从来没有被验证过**，而门禁仍然绿。
#   实测出现过这个形态：三条真实素材用例**全 `[跳过]`(5)**，退出码仍 0。这一行把覆盖率的
#   真实形状写进日志，避免"绿 = 全跑过"的误读。
#
# 口径（照此记账，不靠印象）：
#   * **受保护断言** = 退出码 5 会让"一条本该守护某不变量的断言"整条不执行的用例。
#     即：真的跑了、返回了 5、且**没有**自己声明 `strict`（自带环境 / 产物的用例声明了 strict，
#     所以它的 5 是打包缺陷 → 直接判红，不会落进跳过台账）。
#   * **非受保护** = 用例**根本没跑**（-SkipMaterial / 未提供素材 / 未启用注入门禁）。
#     这类不影响任何断言的成立与否，只影响覆盖范围。
#
# `$Protected` 只对"真的跑了又被记跳过"的那一类置真；M > 0 时收尾必须逐条打印出来，
#   验收记录里要解释"为什么这条这次没成立"。
# ---------------------------------------------------------------------------

function Register-Skip {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Reason,
        [bool]$Protected = $false
    )

    $script:skips.Add(@{ id = $Id; reason = $Reason; protected = $Protected })
}

# 用 .NET 读文本必须给绝对路径：.NET 的当前目录不等于 PowerShell 的当前目录
function Read-Utf8Text([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
}

# 行尾归一 + 去掉每行尾空白（夹具对照用：不因 CRLF/LF 差异误报）
function Normalize-Text([string]$Text) {
    if ($null -eq $Text) { return '' }
    $t = $Text -replace "`r`n", "`n"
    $lines = $t -split "`n" | ForEach-Object { $_.TrimEnd() }
    return ($lines -join "`n").Trim()
}

function Invoke-NativeCommand {
    # 原生命令（dotnet / powershell）统一走"批处理包装 + 全重定向 + 退出码落盘"：
    #   * PowerShell 5.1 里把原生命令的 stderr 接进管道，会在 $ErrorActionPreference='Stop' 下抛终止错误；
    #   * Start-Process -PassThru 一旦用了 -RedirectStandardOutput，$p.ExitCode 在 5.1 里取不到值
    #     （实测为空），而 -Wait 又给不了超时 —— 所以退出码由批处理写进文件（每行独立解析，%ERRORLEVEL% 可靠）。
    param(
        [Parameter(Mandatory = $true)][string]$CommandLine,
        [Parameter(Mandatory = $true)][string]$LogFile,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$TimeoutSeconds = 900
    )

    $script:nativeSeq++
    $bat = Join-Path $script:scratch ("native-{0}-{1:d2}.cmd" -f $Label, $script:nativeSeq)
    $ec = $bat + '.ec.txt'
    Remove-Item -LiteralPath $ec -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $bat -Encoding ASCII -Value @(
        '@echo off',
        "$CommandLine > `"$LogFile`" 2>&1",
        "> `"$ec`" echo %ERRORLEVEL%"
    )

    $cmdExe = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
    $p = Start-Process -FilePath $cmdExe -ArgumentList '/d', '/c', $bat -PassThru `
        -RedirectStandardOutput ($bat + '.out') -RedirectStandardError ($bat + '.err')

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }

    $code = 'TIMEOUT'
    if (-not $p.HasExited) {
        try { $p.Kill() } catch { }
    } else {
        $raw = (Get-Content -LiteralPath $ec -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($null -ne $raw -and "$raw".Trim() -match '^-?\d+$') { $code = [int]"$raw".Trim() }
    }

    return @{ ExitCode = $code; Text = (Read-Utf8Text $LogFile) }
}

function Get-GitLines {
    param([Parameter(Mandatory = $true)][string[]]$GitArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $script:repoRoot @GitArgs 2>&1
        # ⚠️ **只能 TrimEnd**：`git status --porcelain` 的第一列可以是**空格**
        #    （` M <path>` = "工作区改了、没暂存"），而 `Out-String` 之后的 `.Trim()` 会把那个
        #    前导空格吃掉 → `porcelain` 记录整体左移一位 → 路径少掉首字母（0.10.8 实测踩到过）。
        #    尾部空白（含 CRLF 的 `\r`）照旧去掉；这两个调用点都不需要前导空格。
        return @{ ExitCode = $LASTEXITCODE; Text = (($out | Out-String).TrimEnd()) }
    } finally {
        $ErrorActionPreference = $prev
    }
}

# ---------------------------------------------------------------------------
# 前置自检：脚本编码
#   build\*.ps1 里凡**含非 ASCII 字节**的脚本，必须以 UTF-8 BOM（EF BB BF）开头。
#   为什么：编辑工具会（静默）抹掉 BOM，而 Windows PowerShell 5.1 对**没有 BOM** 的
#   UTF-8 文件按**活动代码页（ANSI/GBK）**解码 —— 中文注释与判据当场变乱码，
#   括号/引号被解成别的字节后脚本**直接解析失败**。门禁自己的通过/失败判据正是
#   中文行（[通过]/[失败]），脚本一挂门禁就**静默失效**（2026-10-13 实测）。
#   纯 ASCII 脚本不受影响（ANSI 与 UTF-8 对 ASCII 字节完全一致），允许不带 BOM。
# ---------------------------------------------------------------------------

function Get-ScriptEncodingInfo {
    # 返回 @{ HasBom; NonAscii; Ok }
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)

    $nonAscii = 0
    foreach ($b in $bytes) { if ($b -gt 127) { $nonAscii++ } }

    return @{ HasBom = $hasBom; NonAscii = $nonAscii; Ok = (($nonAscii -eq 0) -or $hasBom) }
}

function Test-ScriptEncodingBom {
    # 检查 $Directory（默认全部 *.ps1）或显式 $Files 列表；返回问题列表（空 = 通过）
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [string[]]$Files = @()
    )

    $names = if ($Files.Count -gt 0) {
        $Files
    } else {
        @(Get-ChildItem -LiteralPath $Directory -Filter '*.ps1' -File | Sort-Object -Property Name | ForEach-Object { $_.Name })
    }

    $found = New-Object System.Collections.Generic.List[string]
    Write-Host ("  目录：{0}（{1} 个脚本）" -f $Directory, $names.Count)

    foreach ($name in $names) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Host ("  [失败] {0} 不存在" -f $name)
            $found.Add("$name 不存在")
            continue
        }

        $info = Get-ScriptEncodingInfo $path
        if ($info.NonAscii -eq 0) {
            Write-Host ("  [通过] {0}：纯 ASCII（{1} 字节，无 BOM 也安全）" -f $name, (Get-Item -LiteralPath $path).Length)
        } elseif ($info.HasBom) {
            Write-Host ("  [通过] {0}：非 ASCII 字节 {1} 个 + UTF-8 BOM 存在" -f $name, $info.NonAscii)
        } else {
            Write-Host ("  [失败] {0} 缺少 UTF-8 BOM（EF BB BF）：文件含 {1} 个非 ASCII 字节" -f $name, $info.NonAscii)
            Write-Host  '         原因：Windows PowerShell 5.1 对没有 BOM 的 UTF-8 文件按 ANSI（活动代码页）解码，'
            Write-Host  '         中文注释与 [通过]/[失败] 判据会变成乱码，脚本直接解析失败 —— 门禁静默失效。'
            Write-Host  '         修法：用 UTF8Encoding($true) 写回（PowerShell 5.1 的 Set-Content -Encoding UTF8 也行），'
            Write-Host  '         或编辑后用 Get-Content -Encoding Byte -TotalCount 3 确认是 EF BB BF。'
            $found.Add("$name 缺少 UTF-8 BOM")
        }
    }

    return $found
}

function Get-ProcessExePath($Process) {
    # 取进程的可执行文件路径；取不到（已退出 / 权限不足 / 跨位数）返回 $null。
    # 为什么不用 `Path` 一个属性了事：PowerShell 5.1 与 7 上 `Path` 的可用性不同，
    # `MainModule.FileName` 是兜底；两条都失败时**不杀**（宁可留着残留，也不误杀别人的进程）。
    try { if ($Process.Path) { return [string]$Process.Path } } catch { }
    try { return [string]$Process.MainModule.FileName } catch { }
    return $null
}

function Stop-LeftoverApp([string]$Label) {
    # 单实例守卫：留着一个**本 checkout 的** SimOCR 进程会让新的自检被"转发"给首实例后静默退出（假绿），
    # 所以残留仍然要清 —— 但**只清自己的**（0.10.7，清理线 · 测试可信）：
    #
    # 旧写法 `Get-Process -Name SimOCR | Kill` **不区分可执行文件路径**：同一台机器上另一个 checkout
    # （或一个改名副本）里的 SimOCR 正在跑自检时会被连带杀掉，症状是**对方**门禁的退出码落到 -1
    # （批处理包装拿不到 %ERRORLEVEL% → 判成"编译超时/异常"）。实测反证：把副本改名成
    # `SimOCRprobe.exe` 后同一套门禁立刻 8 通过 / 1 跳过 / EXIT=0 —— 说明误杀就是那个 -1 的来源。
    #
    # 判据**没有放松**：**同一个 exe 路径**（本 checkout 的 `src\SimOCR\bin\<配置>\…\SimOCR.exe`）
    # 的残留进程照样全部结束；只是不再动别的路径下的同名进程。
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

# ---------------------------------------------------------------------------
# 启动一次自检入口：批处理包装 + 全重定向（TEMP/TMP 指向可写临时目录、工作目录 = 仓库根）
# ---------------------------------------------------------------------------

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
        ExitCode  = $exitCode
        Seconds   = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Log       = (Get-LogDelta $logOffset)
        StdOut    = (Read-Utf8Text $appOut)
    }
}

# ---------------------------------------------------------------------------
# 启动一次**无 testhost 的控制台运行器**：
#   tests\SimOCR.Tests\bin\<配置>\net10.0-windows\SimOCR.Tests.exe
#   它与 xUnit 事实共用同一份断言源码（tests/SimOCR.Tests/PureLogic/*.cs），
#   只是不经过 testhost —— 受限环境里仍能真正执行那批断言的路径。
#   与 Invoke-GateApp 同样的规矩：TEMP/TMP 指向可写临时目录、工作目录 = 仓库根、退出码落盘。
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

    # 运行器显式按 UTF-8 写 stdout（见 tests/SimOCR.Tests/Program.cs），所以这里同样按 UTF-8 读。
    $text = Read-Utf8Text $runOut
    return @{
        ExitCode = $exitCode
        Seconds  = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Log      = $text
        StdOut   = $text
    }
}
# ---------------------------------------------------------------------------
# 准备
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $script:repoRoot)) { throw "仓库根不存在：$script:repoRoot" }
if (-not (Test-Path -LiteralPath (Join-Path $script:repoRoot '.git'))) { throw "这里看起来不是仓库根：$script:repoRoot" }

Write-Section '前置自检：脚本编码（含非 ASCII 的 *.ps1 必须带 UTF-8 BOM）'
$script:encodingProblems = @(Test-ScriptEncodingBom -Directory $PSScriptRoot)
if ($script:encodingProblems.Count -gt 0) {
    Write-Host ''
    Write-Host ("[门禁] 编码前置自检未通过：{0}" -f ($script:encodingProblems -join '；'))
    Write-Host '[门禁] 这些脚本在 Windows PowerShell 5.1 下会中文乱码 / 直接解析失败，门禁会静默失效。'
    Write-Host '[门禁] 请先补回 UTF-8 BOM 再跑门禁（本自检在任何自检/构建之前执行）。'
    exit 1
}

New-Item -ItemType Directory -Force -Path $script:scratch | Out-Null
Get-ChildItem -LiteralPath $script:scratch -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# 工作区编译通过
#
# 为什么要有这一步：一次提交的自述是「…需要 TableGrid.SetCells；
#   **上一个提交漏带该文件**」—— 说明提交序列里存在"**未编译通过即提交**"。
#   本地工作区里文件在、构建也过（因为它还在工作区里），但那一次提交**不带它**，
#   于是那个提交点本身编译不过；而前面所有门禁都是在**工作区**上跑的，抓不到这一类。
#
# 另外，本脚本原先只构建 `src\SimOCR` 与 `tests\SimOCR.Tests`：
#   **安装器工程（src\SimOCR.Installer）从来没有被任何核心门禁构建过**
#   （`SimOCR.slnx` 里也还没登记它）。它编译不过时门禁照样会报"通过"。
#
# 两条判据：
#   ① 工作区**整体**编译通过：`SimOCR.slnx`（主程序 + 测试工程）与安装器工程，
#      都要 0 警告 / 0 错误；
#   ② **未纳入版本库的源文件**：`git status --porcelain` 里凡 `src/`、`tests/` 下
#      **未跟踪（??）** 的源码文件（.cs/.xaml/.csproj/.props/.targets/.yaml/.ps1）一律记账 ——
#      这正是"文件在本地存在、编译也过，但没被 git 跟踪，提交时漏带"的失败模式。
#      未跟踪的素材 / 输出目录不在扫描范围内，不会误报。
#   ③ **已跟踪文件的未提交改动** —— ② 只扫未跟踪文件，给了**假安全感**：
#      它防不住"改了**已跟踪**文件却没提交"。0.10.6 与 0.10.7 连续两次栽在这一类上，
#      两次都是**根目录文件**、都因 `git add` 的 pathspec 没列到它而留在工作区：
#        * `SimOCR.slnx`（0.10.5 的安装器工程：改了但没提交 → 从 HEAD 检出的人构建不出版本）；
#        * `Directory.Build.props`（0.10.6 的版本号改动至今未提交，HEAD 仍是旧值）。
#      所以 ③ 的扫描范围**不只**限于 `src/` / `tests/`，还包含**仓库根的工程 / 清单文件**
#      （`*.slnx` / `*.props` / `*.targets` / `*.csproj` / `*.sln` / `*.md`）——
#      这正是上面两次事故的形态。
#
# ⚠️ **提交脚本 / 检查的 pathspec 纪律（0.10.7 的过程教训，§9 P-28）**：
#   pathspec **不得包含已删除或未跟踪的文件**。0.10.7 本轮第一次提交因 pathspec 里写了
#   **已被删除的** `debug.log` → `fatal: pathspec did not match` → **整笔提交中止**
#   （随后 `inject-check` 因树脏被前置拒绝，看起来像"门禁坏了"，其实是提交没成功）。
#   正确做法：**先 `git status --porcelain` 推导改动集**，再据此拼 pathspec；
#   提交失败后先看 `git log`（是否真的多了一笔），再怀疑门禁。
#
# 抓不住什么（写清楚，免得当万能药）：**暂存区与工作区都与 HEAD 不同、但两处相同**的情形
#   （即"改了并已 `git add`，但那次提交没带它"）在**提交之前**本地无法判定 —— 那要靠提交后
#   用干净 checkout 再构建一次（本门禁不做：它需要"完整 checkout"的假设，而 build\ 会随开源仓发布）。
#
# `-SkipBuild` 只跳过 ①（构建），**不跳过 ② / ③**（它们是纯 git 检查，与有没有构建无关）。
# ② / ③ 默认都按 `[提醒]` 记账，带 `-StrictUntrackedSources` 时升格为**硬失败**。
# ---------------------------------------------------------------------------

# 单条 `git status --porcelain`（v1）记录的解析：
#   格式 = `XY <path>`（`??` = 未跟踪；`R`/`C` 另有 ` -> ` 分隔的重命名目标）。
#   判据用的是 XY 相对 **HEAD** 的语义：只要 X（暂存区 vs HEAD）或 Y（工作区 vs 暂存区）
#   有一个不是空格，就说明"HEAD 里没有这次改动" → 属于"提交前必须清零"的未提交改动。
function Split-PorcelainEntry {
    param([Parameter(Mandatory = $true)][string]$Line)

    # ⚠️ **只能用 TrimEnd，不能用 TrimStart**：porcelain v1 的第一列是"暂存区 vs HEAD"，
    #    ` M <path>` 的**第一个字符就是空格**（= 未暂存）。TrimStart 会把那个空格也吃掉，
    #    于是 `Substring(3)` 从路径的**第二个字符**开始取 —— 实测后果是路径少掉首字母
    #    （`build\gate-check.ps1` → `uild\gate-check.ps1`），而 `?? <path>`（以 `?` 开头）
    #    恰好不受影响，所以**只有"已跟踪文件"那一组会静默变错**。0.10.8 实测踩到过。
    $entry = $Line.TrimEnd()
    if ($entry.Length -lt 4) { return $null }

    $xy = $entry.Substring(0, 2)
    $rest = $entry.Substring(3).Trim()
    # 重命名 / 拷贝：`R  old -> new` —— 取**新路径**（它才是提交时要带上的名字）
    if ($xy -match '[RC]' -and $rest.Contains(' -> ')) {
        $rest = $rest.Substring($rest.LastIndexOf(' -> ', [System.StringComparison]::Ordinal) + 4).Trim()
    }

    return @{ XY = $xy; Path = $rest.Trim('"').Replace('/', '\') }
}

# `git status --porcelain` 的归类：返回 @{ Untracked; TrackedDirty; Errors }
#   * Untracked    = 字符串列表（**源码扩展名**的未跟踪文件 —— 提交时会漏带）
#   * TrackedDirty = **记录列表** @{ Path; XY }（已跟踪且有未提交改动）
#     ⚠️ 这里刻意**不再把 XY 与 Path 拼成一个字符串**：上一版把它们拼成 `"path  [XY]"`，
#        下游为了拿回路径又得从拼接串里反解 —— 那种"先拼再拆"的写法一旦格式与分隔符对不上，
#        就会静默把路径的第一个字符切掉（0.10.8 实测踩到过）。字段与字符串分离，路径永远是原文。
function Get-WorkspaceVcsState {
    $srcPatterns = @('*.cs', '*.xaml', '*.csproj', '*.props', '*.targets', '*.yaml', '*.ps1')
    $rootPatterns = @('*.slnx', '*.sln', '*.props', '*.targets', '*.csproj', '*.md')
    $pathspec = @('src', 'tests', 'build') + $srcPatterns + $rootPatterns

    $untracked = New-Object System.Collections.Generic.List[string]
    $dirty = New-Object System.Collections.Generic.List[hashtable]
    $errors = New-Object System.Collections.Generic.List[string]

    $status = Get-GitLines (@('status', '--porcelain', '--untracked-files=all', '--') + $pathspec)
    if ($status.ExitCode -ne 0) {
        $errors.Add("git status 失败（退出码 $($status.ExitCode)）：$($status.Text)")
        return @{ Untracked = $untracked; TrackedDirty = $dirty; Errors = $errors }
    }

    foreach ($line in ($status.Text -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parsed = Split-PorcelainEntry $line
        if ($null -eq $parsed) { continue }

        $path = [string]$parsed['Path']
        if ([string]$parsed['XY'] -eq '??') {
            # 未跟踪：**扫描范围与原有判据逐字一致**（只看 `src/` / `tests/` 下的源码文件）——
            # 这一半不是本次要改的东西；把范围放大到整个 pathspec 会把仓库外的一次性脚本也列进来
            # （那是"提醒"该给的信号被淹没的典型）。
            # ⚠️ 为什么 pathspec 里仍然带着 `build` + 根目录模式：那是为了 **已跟踪** 那一侧，
            #    而 `??` 这一侧在这里按目录前缀再收一次窄。
            if ($path -notlike 'src\*' -and $path -notlike 'tests\*') { continue }
            foreach ($pattern in $srcPatterns) {
                if ($path -like $pattern) { $untracked.Add($path); break }
            }
            continue
        }

        # 已跟踪且有未提交改动 —— 全部记账（不按扩展名过滤：改了就该提交）
        $dirty.Add(@{ Path = $path; XY = [string]$parsed['XY'] })
    }

    return @{ Untracked = $untracked; TrackedDirty = $dirty; Errors = $errors }
}

Write-Section '工作区编译通过（防「未编译通过即提交」）'

if ($SkipBuild) {
    Write-Host '  [跳过] -SkipBuild：不重新编译（下面的「漏带文件」检查照跑）'
} else {
    # 工程清单：**每一份会被提交的工程**都要在这里编译一次，否则"没被任何门禁构建过"的工程
    # 编译不过时门禁仍是绿的（安装器工程就是这样一个洞，见上面的说明）。
    $workspaceTargets = @(
        @{ label = 'build-slnx'; proj = (Join-Path $script:repoRoot 'SimOCR.slnx'); note = 'SimOCR.slnx（主程序 + 测试工程）' },
        @{ label = 'build-installer'; proj = (Join-Path $script:repoRoot 'src\SimOCR.Installer\SimOCR.Installer.csproj'); note = '安装器工程（原先没有任何核心门禁构建过）' }
    )

    foreach ($target in $workspaceTargets) {
        $proj = [string]$target['proj']
        $leaf = Split-Path -Leaf $proj
        if (-not (Test-Path -LiteralPath $proj)) {
            Write-Host ("  [失败] 找不到工程：{0}" -f $proj)
            $script:problems.Add("工作区编译通过：找不到 $proj")
            continue
        }

        Write-Host ("  [构建] {0} —— {1}" -f $leaf, [string]$target['note'])
        $wsLog = Join-Path $script:scratch ("{0}.log" -f [string]$target['label'])
        $ws = Invoke-NativeCommand -CommandLine "dotnet build `"$proj`" -c $Configuration --nologo -v:m" `
            -LogFile $wsLog -Label ([string]$target['label']) -TimeoutSeconds 900
        $wsText = [string]$ws.Text
        ($wsText -split "`n" | Select-Object -Last 4) | ForEach-Object { Write-Host "    $_" }

        if ("$($ws.ExitCode)" -eq 'TIMEOUT') {
            Write-Host ("  [失败] {0} 编译超时" -f $leaf)
            $script:problems.Add("工作区编译超时：$leaf")
            continue
        }

        if ([int]$ws.ExitCode -ne 0) {
            Write-Host ("  [失败] {0} 编译失败：退出码 {1}" -f $leaf, $ws.ExitCode)
            $script:problems.Add("工作区编译不过：$leaf（退出码 $($ws.ExitCode)）")
            continue
        }

        foreach ($pair in @(@('个警告', '警告'), @('个错误', '错误'))) {
            $wm = [regex]::Match($wsText, "(\d+)\s*$($pair[0])")
            if ($wm.Success -and [int]$wm.Groups[1].Value -ne 0) {
                $script:problems.Add("工作区编译产物有 $($wm.Groups[1].Value) $($pair[1])：$leaf")
            }
        }
    }
}

$vcsState = Get-WorkspaceVcsState

foreach ($vcsError in $vcsState['Errors']) { $script:problems.Add($vcsError) }

$untrackedSources = @($vcsState['Untracked'])
if ($untrackedSources.Count -eq 0) {
    Write-Host '  [通过] src/ tests/ 下没有被 git 漏带的源文件（未跟踪）'
} else {
    $auditTag = if ($StrictUntrackedSources) { '失败' } else { '提醒' }
    Write-Host ("  [$auditTag] 以下源文件**没有被 git 跟踪**（提交时会漏带）：")
    foreach ($missing in $untrackedSources) { Write-Host "        $missing" }
    if ($StrictUntrackedSources) {
        Write-Host '         修法：`git add` 它们（或加进 .gitignore —— 但源码文件通常不该被忽略）。'
        $script:problems.Add("工作区有未被 git 跟踪的源文件 $($untrackedSources.Count) 个：$($untrackedSources -join '；')")
    } else {
        Write-Host '         本次按**提醒**记账（不影响退出码）。**提交前**请带 -StrictUntrackedSources 再跑一次，'
        Write-Host '         那才是这一步的正面判据：新写的源码文件必须先 git add，否则那一次提交会漏带它们。'
    }
}

# ---------------------------------------------------------------------------
# 已跟踪文件的未提交改动
#
# 为什么单独一条：上面那条只看 `??`（未跟踪），所以"**改了已跟踪的文件却没提交**"它一条也抓不到，
#   而这一类已经真实发生两次（`SimOCR.slnx` / `Directory.Build.props`，两次都是**根目录文件**、
#   都因为 `git add` 的 pathspec 没列到它）。这条检查把"HEAD 与工作区/暂存区不一致"的已跟踪文件
#   全部列出来，并额外提示"根目录工程 / 清单文件必须显式列入提交路径"。
# ---------------------------------------------------------------------------

Write-Section '已跟踪文件的未提交改动（防「改了已跟踪文件却没提交」）'

$dirtyTracked = @($vcsState['TrackedDirty'])
$dirtyRootFiles = @($dirtyTracked | Where-Object { [string]$_['Path'] -notmatch '\\' })

if ($dirtyTracked.Count -eq 0) {
    Write-Host '  [通过] 没有「已跟踪但未提交」的改动（src/ tests/ build/ 与根目录工程 / 清单文件）'
} else {
    $auditTag = if ($StrictUntrackedSources) { '失败' } else { '提醒' }
    Write-Host ("  [$auditTag] 以下**已跟踪文件**有未提交改动（`git status` 的 XY 码写在后面）：")
    foreach ($entry in $dirtyTracked) {
        Write-Host ("        {0}    [XY={1}]" -f [string]$entry['Path'], [string]$entry['XY'])
    }
    Write-Host '         XY 语义：X = 暂存区 vs HEAD，Y = 工作区 vs 暂存区；两者任一非空都说明 HEAD 里没有这次改动。'
    Write-Host '         修法：把**上面列出的每一个路径**显式写进 `git add` 的 pathspec（不要用 -A 兜底，'
    Write-Host '               也不要写已删除 / 未跟踪的路径 —— 那会让 `git add` 整笔中止，见 P-28）。'
    if ($dirtyRootFiles.Count -gt 0) {
        Write-Host ("         ⚠️ 其中 {0} 个是**仓库根**的工程 / 清单文件 —— 历史两次事故（SimOCR.slnx、" -f $dirtyRootFiles.Count)
        Write-Host '            Directory.Build.props）都是根目录文件被 `git add` 的 pathspec 漏掉：'
        foreach ($entry in $dirtyRootFiles) { Write-Host ("              {0}" -f [string]$entry['Path']) }
        Write-Host '            提交前请逐条确认这些根目录文件已列进提交路径。'
    }

    if ($StrictUntrackedSources) {
        $script:problems.Add("工作区有 $($dirtyTracked.Count) 个已跟踪文件未提交改动：$((@($dirtyTracked | ForEach-Object { [string]$_['Path'] })) -join '；')")
    } else {
        Write-Host '         本次按**提醒**记账（不影响退出码）。**提交前**请带 -StrictUntrackedSources 再跑一次。'
    }
}


Write-Host "SimOCR 核心门禁"
Write-Host ("  仓库     : {0}" -f $script:repoRoot)
Write-Host ("  配置     : {0}" -f $Configuration)
Write-Host ("  可执行   : {0}" -f $script:exePath)
Write-Host ("  临时目录 : {0}" -f $script:scratch)

if (-not $SkipBuild) {
    Write-Section "构建 $Configuration（0 警告 / 0 错误）"
    $buildLog = Join-Path $script:scratch 'build.log'
    $proj = Join-Path $script:repoRoot 'src\SimOCR\SimOCR.csproj'
    $r = Invoke-NativeCommand -CommandLine "dotnet build `"$proj`" -c $Configuration --nologo -v:m" -LogFile $buildLog -Label 'build' -TimeoutSeconds 900
    $text = [string]$r.Text
    ($text -split "`n" | Select-Object -Last 6) | ForEach-Object { Write-Host "  $_" }

    if ($r.ExitCode -ne 0) {
        Write-Host "[门禁] 构建失败：退出码 $($r.ExitCode)"
        $script:problems.Add("构建失败（退出码 $($r.ExitCode)）")
    } else {
        foreach ($pair in @(@('个警告', '警告'), @('个错误', '错误'))) {
            $m = [regex]::Match($text, "(\d+)\s*$($pair[0])")
            if ($m.Success -and [int]$m.Groups[1].Value -ne 0) {
                $script:problems.Add("产物有 $($m.Groups[1].Value) $($pair[1])")
            }
        }
    }

    # 测试项目（xUnit 事实 + **无 testhost 的控制台运行器**）也必须 0 警告 / 0 错误。
    # 它同时是运行器的产物来源，所以门禁要在这里把它建出来（-SkipBuild 时用上次构建的产物）。
    if (Test-Path -LiteralPath $script:testProject) {
        Write-Host "  （测试项目：纯逻辑断言 + 控制台运行器）"
        $testsLog = Join-Path $script:scratch 'build-tests.log'
        $tr = Invoke-NativeCommand -CommandLine "dotnet build `"$script:testProject`" -c $Configuration --nologo -v:m" -LogFile $testsLog -Label 'build-tests' -TimeoutSeconds 900
        $testsText = [string]$tr.Text
        ($testsText -split "`n" | Select-Object -Last 6) | ForEach-Object { Write-Host "  $_" }

        if ($tr.ExitCode -ne 0) {
            Write-Host "[门禁] 测试项目构建失败：退出码 $($tr.ExitCode)"
            $script:problems.Add("测试项目构建失败（退出码 $($tr.ExitCode)）")
        } else {
            foreach ($pair in @(@('个警告', '警告'), @('个错误', '错误'))) {
                $tm = [regex]::Match($testsText, "(\d+)\s*$($pair[0])")
                if ($tm.Success -and [int]$tm.Groups[1].Value -ne 0) {
                    $script:problems.Add("测试项目产物有 $($tm.Groups[1].Value) $($pair[1])")
                }
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $script:exePath)) {
    Write-Host "[门禁] 缺少可执行文件：$script:exePath（先构建，或检查 -Configuration）"
    exit 1
}

$fixtureDir = Join-Path $script:repoRoot 'build\fixtures'
$fixturePng = Join-Path $fixtureDir 'linegrid.png'
$fixtureExpected = Join-Path $fixtureDir 'linegrid-expected.txt'
if (-not (Test-Path -LiteralPath $fixturePng)) {
    Write-Host "[门禁] 缺少合成夹具：$fixturePng"
    exit 1
}

# ---------------------------------------------------------------------------
# 核心用例表（12 条，只吃参数、不依赖业务素材）
# ---------------------------------------------------------------------------

$coreCases = @(
    @{
        id      = 'selftest-snap'
        args    = @('--selftest-snap')
        timeout = 180
        note    = '窗口吸附**真实窗口探针**（纯几何用例由 runner 的 snap-geometry 用例承担）'
        # 这条 app CLI 只跑"真窗口探针"；纯几何用例表已经由下面的 `snap-geometry` runner 用例执行，
        # 原来两条硬判据一条没丢。这里只钉**本入口自己那一半**：
        #   ① `[真实窗口] 枚举` —— 真的枚举了（含本进程），不是被短路掉；
        #   ② `[无边框窗口` / `[普通边框窗口` —— 两条探针窗口都真的起了并被逐项核对；
        #   ③ 收尾三态文案 `自检结果：(全部)?通过` —— 只有真全过才是这句。
        okRegex = '\[真实窗口\] 枚举[\s\S]*\[无边框窗口[\s\S]*\[普通边框窗口[\s\S]*自检结果：(全部)?通过'
    },
    @{
        id      = 'snap-probe-occlusion'
        args    = @('--snap-probe-occlusion=assert')
        timeout = 180
        note    = '真实双窗口端到端：被整窗遮盖的边不吸 + 遮盖者可见边照常吸 + targets/raw 配对不变量'
        # 自适应几何在 ≥800×600 的桌面上必然把 B 整块摆在屏内 → 四条可见边都会断言；
        # 这里要求 4/4 是为了**防止悄悄缩水**（真回归仍然走退出码 4，见下面的判定顺序）。
        okRegex = '\[结论\] OK.*可见边=4/4'
    },
    @{
        id      = 'selftest-overlay'
        args    = @('--selftest-overlay')
        timeout = 240
        note    = '覆盖层端到端：按钮 → 截图 → 导出'
        okRegex = '自检结果：通过'
    },
    @{
        id      = 'selftest-capture'
        args    = @('--selftest-capture=10')
        timeout = 240
        note    = '连拍压力（10 次）'
        okRegex = '自检结果：通过'
    },
    # ------------------------------------------------------------------
    # 纯逻辑断言：**唯一的执行路径是控制台运行器**（0.10.7 起）
    #
    # 这四条 runner 用例取代了原来钉在 app CLI 上的两条用例：
    #   * 原 `selftest-profile`（--selftest-profile，配置档单一来源）→ `profile-source`
    #     （那个 CLI 开关已退役：断言源码移出发布产物，见 src/SimOCR/SimOCR.csproj）
    #   * 原 `selftest-snap` 的**几何表那一半**（[几何] 用例 28 条 + 生产配对链）→ `snap-geometry`
    #     （app 的 --selftest-snap 保留，但只剩"真实窗口探针"，见上面那条用例）
    #   * 原 `--selftest-linegrid` 的**前置断言**（手工分割线 6 条）→ `manual-line-split`
    #     （app 的 --selftest-linegrid 保留，继续跑线格→识别→导出→对照）
    # **判据没有一条被放松**：原来的正则逐字搬过来（含条数），另外把"用例数/断言数"也钉住。
    # ------------------------------------------------------------------
    @{
        id      = 'pure-logic-runner'
        kind    = 'runner'
        args    = @('--case=all')
        timeout = 120
        note    = '纯逻辑断言全集（配置档单一来源 3 + 吸附几何 28 + 手工分割线 6 + 表格差异工具 10 + 导出核心 31）由不依赖 testhost 的控制台运行器执行'
        # 只钉 `纯逻辑断言：全部通过` 时，"运行器少看到一条用例"仍然是绿的
        # （实测：源文件不链接时从 4 用例/47 断言静默掉到 3 用例/37 断言）。
        # 现在把口径数字一起钉住：少一条用例、少一条断言都会红。
        okRegex = '纯逻辑断言：全部通过（5 条用例 / 78 条断言）'
        # 运行器自带 Profiles 拷贝（SimOCR.Tests.csproj 里显式拷的）：
        # 退出码 5（用例不成立）只可能是打包漏了，不许按"环境不支持"跳过。
        strict  = $true
    },
    # ------------------------------------------------------------------
    # xUnit 消费路径。
    #
    # 控制台运行器证明"断言源码在跑"，这条证明"xUnit 事实确实被发现并执行"——
    # 两条路共用同一份断言源码，缺一条都不是真实覆盖。
    #   `dotnet test ... -c Release --no-build` → 失败 0 / 通过 67 / 跳过 0 / 总计 67（约 0.4~0.5s）。
    #
    # 为什么用 `--no-build`：本脚本**构建段**（`$workspaceTargets` 之后的测试工程构建）已经把它
    # 建好了；`dotnet test` 默认会先构建，而 `-SkipBuild` 的契约是"不重新编译"——让它在
    # `-SkipBuild` 下偷偷重建就破坏了这个契约（也会掩盖"产物缺失"这个真实问题）。
    # 产物缺失时**按失败记账**并提示"不带 -SkipBuild 跑一次"，不当成"环境不支持(5)"跳过。
    #
    # okRegex 钉死四个数字（失败 / 通过 / 已跳过 / 总计），并同时接受中英文 CLI：
    # 只看"退出码 0"是不够的 —— 测试被过滤成 0 条时退出码同样是 0，那正是"空断言"。
    # ------------------------------------------------------------------
    @{
        id      = 'xunit-runner'
        kind    = 'dotnet-test'
        command = "dotnet test `"$script:testProject`" -c $Configuration --no-build --nologo -v:q"
        timeout = 180
        note    = 'xUnit 消费路径（dotnet test --no-build，68 passed / 0 failed 全部钉死）'
        okRegex = '(已通过!\s*-\s*失败:\s*0[，,]\s*通过:\s*68[，,]\s*已跳过:\s*0[，,]\s*总计:\s*68)|(Passed!\s*-\s*Failed:\s*0,\s*Passed:\s*68,\s*Skipped:\s*0,\s*Total:\s*68)'
        strict  = $true
    },
    @{
        id      = 'profile-source'
        kind    = 'runner'
        args    = @('--case=profile-source')
        timeout = 120
        note    = '配置档单一来源（原 --selftest-profile，断言移出发布产物后改钉运行器）'
        okRegex = '\[通过\] profile-source：3 条断言[\s\S]*纯逻辑断言：全部通过'
        strict  = $true
    },
    @{
        id      = 'snap-geometry'
        kind    = 'runner'
        args    = @('--case=snap-geometry')
        timeout = 120
        note    = '吸附几何 28 条（原 --selftest-snap 的前半段）—— 生产配对链 23a~23e 的守门用例'
        # 这两条判据从原来 `selftest-snap` 的 okRegex **逐字**搬过来（一条都没放松），
        # 另补 `28 条断言` 的计数：几何用例表悄悄缩水就红。
        okRegex = '\[几何\] 用例 28 条[\s\S]*\[通过\] 生产配对：正确配对不吸附[\s\S]*\[通过\] snap-geometry：28 条断言'
        strict  = $true
    },
    @{
        id      = 'manual-line-split'
        kind    = 'runner'
        args    = @('--case=manual-line-split')
        timeout = 120
        note    = '手工分割线切分 6 条（原 --selftest-linegrid 的前置断言）'
        okRegex = '\[通过\] manual-line-split：6 条断言[\s\S]*纯逻辑断言：全部通过'
        strict  = $true
    },
    @{
        id         = 'fixture-linegrid'
        args       = @("--selftest-linegrid=$fixturePng")
        timeout    = 240
        note       = '合成夹具：线格 → 识别 → 导出 → 对照'
        okRegex    = '\[结论\] OK'
        expected   = $fixtureExpected
        reportName = 'simocr-linegrid-selftest.txt'
        xlsxName   = 'simocr-linegrid-selftest.xlsx'
    },
    @{
        id      = 'fixture-accept-enhance'
        args    = @("--accept-table=$fixturePng", '--accept-enhance')
        timeout = 240
        # 0.10.2 回归（状态入口）：识别收尾与用户点「刷新」交错时，"当前表"被清掉而文本框还停在旧内容
        # → 导出退回"编辑后的文本"口径 → 屏幕 3 行 / 导出 1 行，不变量在 ShowTablePreview 里炸掉并把
        # `_stateInvariantBroken` 粘住，于是「增强识别」即使真的切成了语义表也永远读成 False、
        # 「编辑分割线」还亮着 → `0/1 张通过`。
        # 这条用例用**随仓合成夹具**就能复现（修复前实测 2/2 退出码 1 + 上述日志），所以按"日志无 [失败]"
        # 判定足够把这条回归钉死在门禁里 —— 它同时覆盖「增强识别后按钮必须禁用」与「刷新回通用识别后必须恢复」。
        note    = '真实鼠标：快速识别 → 编辑分割线 → 刷新 → 增强识别 → 再刷新（状态不变量）'
        okRegex = '验收结束：1/1 张通过'
    },
    @{
        id      = 'fixture-sort-tri-state'
        args    = @("--selftest-ocr-window=$fixturePng")
        timeout = 180
        # 0.10.5：表头排序三态（升 → 降 → 取消）。
        #
        # 为什么需要单独一条：`SelfTest.RunOcrWindow` 里那三行过去**只 log.Info、没有任何断言**，
        # 于是 0.10.3 把三态机退化成"永远升序"（`BindView` 每次重绑都清排序状态 → 第 2 次点击
        # 又被当成第 1 次）**溜过了全部门禁** —— 日志里留下的 `[排序] 第 2 次：asc` 看起来很正常。
        # 0.10.4 修好了逻辑却没补判据，所以同一个回归可以再发生一次；这条用例就是那条判据的闸门。
        #
        # 只用**随仓合成夹具**（build\fixtures\linegrid.png，3 列 3 行：Item/Widget A/Gear C），
        # 不碰鼠标、不需要内部素材，因此可以进 build\（随公仓发布）也能在别的 checkout 上跑。
        # okRegex 同时钉住"第 2 次真的是 desc"与"第 3 次真的是 none"这两行日志：
        # 光有退出码 0 还不够 —— 把整段断言删掉也会得到退出码 0（那就是"断言被静默移除"）。
        note    = '表头排序三态（第 2 次必须 desc / 第 3 次必须 none 且回到识别顺序）'
        okRegex = '\[排序\] 第 2 次：desc[\s\S]*\[排序\] 第 3 次（应取消）：none[\s\S]*\[排序\] 取消排序后整表 == 识别顺序：True[\s\S]*\[结论\] OK'
    }
)

if ($Case.Count -gt 0) {
    $coreCases = @($coreCases | Where-Object { $Case -contains $_['id'] })
    if ($coreCases.Count -eq 0) {
        Write-Host "[用法] -Case 没有匹配任何核心用例：$($Case -join ', ')"
        exit 2
    }
}

Write-Section "核心门禁（不需要业务素材）"
$results = New-Object System.Collections.Generic.List[hashtable]

foreach ($c in $coreCases) {
    if ([string]$c['kind'] -eq 'runner') {
        $r = Invoke-GateRunner -RunnerArgs $c['args'] -Label $c['id'] -TimeoutSeconds ([int]$c['timeout'])
        if ("$($r.ExitCode)" -eq 'MISSING') {
            Write-Host ("  [失败] {0}：找不到控制台运行器 {1}" -f $c['id'], $script:runnerPath)
            Write-Host  '         修法：不带 -SkipBuild 跑一次门禁（会先构建测试项目）。'
            $script:problems.Add("核心门禁 $($c['id'])：找不到控制台运行器 $($script:runnerPath)")
            $results.Add(@{ id = $c['id']; exit = 'MISSING'; seconds = 0; verdict = '失败' })
            continue
        }
    } elseif ([string]$c['kind'] -eq 'dotnet-test') {
        # xUnit 消费路径（dotnet test --no-build，见上面用例定义处的说明）。
        # 缺产物时**直接按失败记账**，不落进"退出码非 0 → 也许是环境问题"的模糊地带。
        $testDll = Join-Path (Split-Path -Parent $script:testProject) "bin\$Configuration\net10.0-windows\SimOCR.Tests.dll"
        if (-not (Test-Path -LiteralPath $testDll)) {
            Write-Host ("  [失败] {0}：找不到测试程序集 {1}" -f $c['id'], $testDll)
            Write-Host  '         修法：不带 -SkipBuild 跑一次门禁（构建段会先建测试工程）。'
            $script:problems.Add("核心门禁 $($c['id'])：找不到测试程序集 $testDll")
            $results.Add(@{ id = $c['id']; exit = 'MISSING'; seconds = 0; verdict = '失败' })
            continue
        }

        $swTest = [Diagnostics.Stopwatch]::StartNew()
        $tr = Invoke-NativeCommand -CommandLine ([string]$c['command']) `
            -LogFile (Join-Path $script:scratch ("{0}.log" -f $c['id'])) `
            -Label ([string]$c['id']) -TimeoutSeconds ([int]$c['timeout'])
        $swTest.Stop()
        $r = @{
            ExitCode = $tr.ExitCode
            Seconds  = [math]::Round($swTest.Elapsed.TotalSeconds, 1)
            Log      = [string]$tr.Text
            StdOut   = [string]$tr.Text
        }
    } else {
        $r = Invoke-GateApp -AppArgs $c['args'] -Label $c['id'] -TimeoutSeconds ([int]$c['timeout'])
    }

    $verdict = '通过'
    $why = @()
    $isSkip = $false

    # 退出码 5 = **用例不成立 / 环境不支持**（不是缺陷）。典型：真实双窗口用例需要一块
    # ≥800×600 的交互桌面；小屏 VM / headless 会话装不下这个场景。
    # 判定顺序很重要：先认 5（跳过），再认其它非 0（失败）—— 否则小屏机器会假失败。
    if ("$($r.ExitCode)" -eq 'TIMEOUT') {
        $verdict = '失败'
        $why += 'TIMEOUT'
    } elseif ([int]$r.ExitCode -eq 5 -and -not $c['strict']) {
        # 退出码 5 = 用例不成立，按"跳过"记账。例外：用例自己声明 strict（自带环境/产物，
        # 例如纯逻辑运行器自己带着 Profiles 拷贝）时，"不成立"是打包缺陷，必须红。
        $verdict = '跳过'
        $isSkip = $true
        $why += '用例不成立/环境不支持（退出码 5，非缺陷；例如虚拟桌面太小或无交互桌面）'
        # 这条**真的跑了却整条没执行** → 它的断言这次没有被验证过（受保护断言）。
        Register-Skip -Id ([string]$c['id']) -Protected $true `
            -Reason '用例不成立/环境不支持（退出码 5）：本次一条断言都没有执行'
    } elseif ([int]$r.ExitCode -ne 0) {
        $verdict = '失败'
        $why += "退出码=$($r.ExitCode)"
    }

    if (-not $isSkip) {
        if ([string]$r.Log -match '\[失败\]') {
            $verdict = '失败'
            $why += '日志含 [失败]'
        }

        if ($null -ne $c['okRegex'] -and [string]$r.Log -notmatch [string]$c['okRegex']) {
            $verdict = '失败'
            $why += "日志缺少「$($c['okRegex'])」"
        }
    }

    # 夹具用例：额外做「导出 → 对照」
    if ($null -ne $c['expected']) {
        $report = Join-Path $script:scratch $c['reportName']
        $xlsx = Join-Path $script:scratch $c['xlsxName']
        if (-not (Test-Path -LiteralPath $report)) {
            $verdict = '失败'
            $why += "没有产出 $($c['reportName'])"
        } else {
            $got = Normalize-Text (Read-Utf8Text $report)
            $want = Normalize-Text (Read-Utf8Text $c['expected'])
            if ($got -ne $want) {
                $verdict = '失败'
                $why += "导出内容与夹具基线不一致（期望 $($c['expected'])）"
                Write-Host '  [对照] 期望：'
                ($want -split "`n") | ForEach-Object { Write-Host "    | $_" }
                Write-Host '  [对照] 实际：'
                ($got -split "`n") | ForEach-Object { Write-Host "    | $_" }
            }
        }

        if (-not (Test-Path -LiteralPath $xlsx) -or (Get-Item -LiteralPath $xlsx).Length -le 0) {
            $verdict = '失败'
            $why += "没有写出 $($c['xlsxName'])"
        }
    }

    Write-Host ("  [{0}] {1} 退出码={2} 用时={3}s {4}" -f $verdict, $c['id'], $r.ExitCode, $r.Seconds, $c['note'])
    if (([string]$c['kind'] -eq 'runner' -or [string]$c['kind'] -eq 'dotnet-test') -and $r.StdOut) {
        ($r.StdOut -split "`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 4) |
            ForEach-Object { Write-Host "        $_" }
    }
    if ($why.Count -gt 0) {
        Write-Host ("       理由：{0}" -f ($why -join '；'))
        if (-not $isSkip) { $script:problems.Add("核心门禁 $($c['id'])：$($why -join '；')") }
    }

    $results.Add(@{ id = $c['id']; exit = $r.ExitCode; seconds = $r.Seconds; verdict = $verdict })
}

# ---------------------------------------------------------------------------
# 素材型硬回归基线（可选）
# ---------------------------------------------------------------------------

$materialAsked = ($IncludeMaterial -or $MaterialDir) -and (-not $SkipMaterial)
if ($SkipMaterial) {
    Write-Host ''
    Write-Host '[跳过] -SkipMaterial：未跑回归素材（显式跳过；核心门禁不受影响）'
    Register-Skip -Id 'material-ocr-window' -Protected $false -Reason '-SkipMaterial：用例根本没跑（非受保护）'
} elseif (-not $materialAsked) {
    Write-Host ''
    Write-Host '[跳过] 未提供回归素材（-MaterialDir），仅跑核心门禁'
    Register-Skip -Id 'material-ocr-window' -Protected $false -Reason '未提供 -MaterialDir：用例根本没跑（非受保护）'
} else {
    if (-not $MaterialDir) {
        Write-Host ''
        Write-Host '[用法] -IncludeMaterial 需要同时给出 -MaterialDir <素材目录>'
        exit 2
    }

    Write-Section "素材型硬回归基线"
    $materialRoot = $MaterialDir
    if (-not [System.IO.Path]::IsPathRooted($materialRoot)) {
        $materialRoot = Join-Path $script:repoRoot $materialRoot
    }

    # 素材缺失**不是**代码缺陷：别的 checkout、开源仓、CI 里都没有内部素材。
    # 所以默认只打印一行 [跳过] 并保持退出码 0；只有显式 -IncludeMaterial（"强制要求跑"）
    # 才把素材缺失当用法错误非 0 退出 —— 两条路都留着，CI 可以自己选。
    $imagePath = $null
    $materialMissing = ''
    if (-not (Test-Path -LiteralPath $materialRoot)) {
        $materialMissing = "素材目录不存在：$materialRoot"
    } elseif ($MaterialImage) {
        $imagePath = $MaterialImage
        if (-not [System.IO.Path]::IsPathRooted($imagePath)) { $imagePath = Join-Path $materialRoot $imagePath }
        if (-not (Test-Path -LiteralPath $imagePath)) {
            $materialMissing = "素材图不存在：$imagePath"
            $imagePath = $null
        }
    } else {
        $found = @(Get-ChildItem -LiteralPath $materialRoot -File -ErrorAction SilentlyContinue |
            Where-Object { @('.jpg', '.jpeg', '.png') -contains $_.Extension.ToLowerInvariant() } |
            Sort-Object -Property Name)
        if ($found.Count -eq 0) {
            $materialMissing = "素材目录里没有 *.jpg / *.png：$materialRoot（可用 -MaterialImage 指定）"
        } else {
            $imagePath = $found[0].FullName
            Write-Host ("  [素材] 自动选中：{0}（可用 -MaterialImage 覆盖）" -f $imagePath)
        }
    }

    if ($null -eq $imagePath) {
        if ($IncludeMaterial) {
            Write-Host "[用法] -IncludeMaterial 要求素材齐备，但 $materialMissing"
            exit 2
        }

        Write-Host "[跳过] $materialMissing（不影响退出码）"
        Register-Skip -Id 'material-ocr-window' -Protected $false -Reason "$materialMissing（用例根本没跑，非受保护）"
        $materialAsked = $false
    }

    if ($null -ne $imagePath) {
        $materialArgs = @()
        if ($MaterialProfile) { $materialArgs += "--profile=$MaterialProfile" }
        $materialArgs += "--selftest-ocr-window=$imagePath"

        $r = Invoke-GateApp -AppArgs $materialArgs -Label 'material-ocr-window' -TimeoutSeconds ([int]$TimeoutSeconds * 3)
        $ok = ("$($r.ExitCode)" -ne 'TIMEOUT') -and ([int]$r.ExitCode -eq 0) -and ([string]$r.Log -notmatch '\[失败\]')
        if ($MaterialExpect -and [string]$r.Log -notmatch $MaterialExpect) {
            $ok = $false
            Write-Host ("  [素材] 日志里没出现期望的「{0}」" -f $MaterialExpect)
        }

        Write-Host ("  [{0}] material-ocr-window 退出码={1} 用时={2}s" -f $(if ($ok) { '通过' } else { '失败' }), $r.ExitCode, $r.Seconds)
        if (-not $ok) { $script:problems.Add("素材自检 material-ocr-window 失败（退出码 $($r.ExitCode)）") }

        if ($MaterialScript) {
            $scriptPath = $MaterialScript
            if (-not [System.IO.Path]::IsPathRooted($scriptPath)) { $scriptPath = Join-Path $script:repoRoot $scriptPath }
            if (-not (Test-Path -LiteralPath $scriptPath)) {
                Write-Host "[用法] 素材脚本不存在：$scriptPath"
                exit 2
            }

            $scriptLog = Join-Path $script:scratch 'material-script.log'
            $env:SIMOCR_GATE_TEMP = $script:scratch
            $sr = Invoke-NativeCommand `
                -CommandLine ("powershell -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Configuration $Configuration") `
                -LogFile $scriptLog -Label 'material' -TimeoutSeconds ([int]$TimeoutSeconds * 3)
            $sText = [string]$sr.Text
            if ($sText) { ($sText -split "`n" | Select-Object -Last 12) | ForEach-Object { Write-Host "    $_" } }

            $sOk = ("$($sr.ExitCode)" -ne 'TIMEOUT') -and ([int]$sr.ExitCode -eq 0)
            Write-Host ("  [{0}] {1} 退出码={2}" -f $(if ($sOk) { '通过' } else { '失败' }), (Split-Path -Leaf $scriptPath), $sr.ExitCode)
            if (-not $sOk) { $script:problems.Add("素材脚本 $(Split-Path -Leaf $scriptPath) 失败（退出码 $($sr.ExitCode)）") }
        } else {
            Write-Host '  [素材] 未提供 -MaterialScript：只验证了素材自检入口，未跑内部对照链'
        }
    }
    }

# ---------------------------------------------------------------------------
# 缺陷注入门禁（可选，慢）
# ---------------------------------------------------------------------------

if ($IncludeInjection) {
    Write-Section "缺陷注入门禁（build\inject-check.ps1）"
    $injLog = Join-Path $script:scratch 'inject.log'
    $injScript = Join-Path $PSScriptRoot 'inject-check.ps1'
    $ir = Invoke-NativeCommand `
        -CommandLine ("powershell -NoProfile -ExecutionPolicy Bypass -File `"$injScript`" -Configuration $Configuration") `
        -LogFile $injLog -Label 'inject' -TimeoutSeconds 3600
    $iText = [string]$ir.Text
    if ($iText) { ($iText -split "`n" | Select-Object -Last 14) | ForEach-Object { Write-Host "    $_" } }

    $iOk = ("$($ir.ExitCode)" -ne 'TIMEOUT') -and ([int]$ir.ExitCode -eq 0)
    Write-Host ("  [{0}] inject-check 退出码={1}" -f $(if ($iOk) { '通过' } else { '失败' }), $ir.ExitCode)
    if (-not $iOk) { $script:problems.Add("注入门禁失败（退出码 $($ir.ExitCode)）") }
} else {
    Write-Host ''
    Write-Host '[跳过] 未跑缺陷注入门禁（加 -IncludeInjection 启用；它较慢，默认不跑）'
    Register-Skip -Id 'inject-check（1 条受保护用例）' -Protected $false `
        -Reason '未加 -IncludeInjection：注入门禁根本没跑（非受保护）'
}

# ---------------------------------------------------------------------------
# 汇总
# ---------------------------------------------------------------------------

Write-Section "核心门禁结果"
Write-Host '  id                  | 退出码 | 用时 s | 结论'
Write-Host '  --------------------+--------+--------+------'
foreach ($x in $results) {
    Write-Host ("  {0,-19} | {1,6} | {2,6} | {3}" -f $x['id'], $x['exit'], $x['seconds'], $x['verdict'])
}

# 退出码 5 的用例：本机环境不支持这个场景（不是缺陷）。单独列出来，避免"跳过"被当成"通过"。
$skippedCases = @($results | Where-Object { $_['verdict'] -eq '跳过' })
if ($skippedCases.Count -gt 0) {
    Write-Host ''
    foreach ($s in $skippedCases) {
        Write-Host ("  [跳过] {0} 退出码={1}：本机环境不支持这个场景（不影响退出码）" -f $s['id'], $s['exit'])
    }
}

# ---------------------------------------------------------------------------
# 跳过记账（"本版跳过 N 条 / 其中受保护断言 M 条"）
#
# 为什么放在收尾：`[跳过]` 不影响退出码，所以它是唯一一条"门禁绿但断言其实没跑"的路径。
#   这一行把覆盖率的真实形状写进日志，**M > 0 时必须解释**"为什么这条受保护的断言这次没成立"。
# ---------------------------------------------------------------------------
$allSkips = @($script:skips)
$protectedSkips = @($allSkips | Where-Object { $_['protected'] })
Write-Host ''
Write-Host ("  [跳过记账] 本版跳过 {0} 条 / 其中受保护断言 {1} 条" -f $allSkips.Count, $protectedSkips.Count)
if ($allSkips.Count -gt 0) {
    Write-Host '  （受保护断言 = 用例真的跑了、却因"用例不成立"整条没执行 → 它的断言本次没有被验证过；'
    Write-Host '    M > 0 必须在验收记录里解释。用例根本没跑的不计受保护，只影响覆盖范围。）'
    foreach ($s in $allSkips) {
        Write-Host ("    {0} {1}：{2}" -f $(if ($s['protected']) { '[受保护]' } else { '[未跑]  ' }), $s['id'], $s['reason'])
    }
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
    Write-Host '=== 核心门禁：未通过 ==='
    exit 1
}

Write-Host ''
Write-Host '=== 核心门禁：通过 ==='
exit 0
