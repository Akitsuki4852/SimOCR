<#
.SYNOPSIS
    Publish SimOCR as a portable, self-contained, single-file Windows exe.

.DESCRIPTION
    Produces dist/SimOCR-<version>-win-x64.exe plus a .sha256 file.

    Notes:
      * WPF does not support PublishTrimmed or NativeAOT on .NET, so the app is
        published self-contained + single-file with self-extraction.
      * EnableCompressionInSingleFile trades first-start time for size; pass
        -NoCompression to compare.
      * If dist contains an exe that is still running, it cannot be deleted;
        it is parked under an "inuse-*" name instead of failing the publish.
      * This file CONTAINS non-ASCII text (Chinese comments and messages) and must
        therefore keep its UTF-8 BOM. Windows PowerShell 5.1 reads BOM-less script
        files as ANSI, which turns the Chinese into mojibake and makes the script
        fail to parse at all. If you edit this file, save it as UTF-8 *with* BOM.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build/publish.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build/publish.ps1 -Version 0.1.2 -NoCompression
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.4',
    [switch]$NoCompression,
    [switch]$NoReadyToRun,
    [switch]$FrameworkDependent,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$root = Split-Path -Parent $scriptDir
$project = Join-Path $root 'src\SimOCR\SimOCR.csproj'
$dist = Join-Path $root 'dist'

if (-not (Test-Path -LiteralPath $project)) { throw "Project not found: $project" }

# VC++ v14 runtime (app-local): onnxruntime.dll needs these; a clean machine without the
# redistributable cannot load it (acceptance risk R-17). They live in runtime\vcredist-x64
# and are copied into every published artifact by the csproj targets. Fail loudly here so a
# release can never silently ship a package whose OCR does not start on a clean machine.
$vcRuntime = Join-Path $root 'runtime\vcredist-x64'
$vcFiles = @()
if (Test-Path -LiteralPath $vcRuntime) {
    $vcFiles = @(Get-ChildItem -LiteralPath $vcRuntime -Filter *.dll -File -ErrorAction SilentlyContinue)
}
if ($vcFiles.Count -lt 4) {
    throw "VC++ runtime DLLs missing in $vcRuntime (found $($vcFiles.Count)/4). Run build\fetch-vcredist.ps1 first - without them OCR cannot load on a machine that has no VC++ redistributable."
}

if (Test-Path -LiteralPath $dist) {
    Get-ChildItem -LiteralPath $dist -File | ForEach-Object {
        $file = $_
        try {
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        }
        catch {
            $parked = "inuse-" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + "-" + $file.Name
            try {
                Rename-Item -LiteralPath $file.FullName -NewName $parked -ErrorAction Stop
                Write-Warning "Old artifact was locked; parked as $parked (a SimOCR instance is probably running - delete it later)"
            }
            catch {
                Write-Warning "Could not clean old artifact $($file.Name): $($_.Exception.Message)"
            }
        }
    }
} else {
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
}

$compression = if ($NoCompression) { 'false' } else { 'true' }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$r2r = if ($NoReadyToRun) { 'false' } else { 'true' }

Write-Host "Publishing SimOCR $Version ($Runtime, self-contained=$selfContained, compression=$compression, r2r=$r2r)"

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    "--self-contained=$selfContained",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:EnableCompressionInSingleFile=$compression",
    "-p:PublishReadyToRun=$r2r",
    '-p:DebugType=none',
    "-p:Version=$Version",
    '-p:SatelliteResourceLanguages=en',
    '-o', $dist,
    '--nologo'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $dist 'SimOCR.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish produced no SimOCR.exe in $dist" }

$finalName = "SimOCR-$Version-$Runtime.exe"
$finalPath = Join-Path $dist $finalName
Move-Item -LiteralPath $exe -Destination $finalPath -Force

Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Name -ne $finalName } | ForEach-Object {
    $stale = $_
    try {
        Remove-Item -LiteralPath $stale.FullName -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "Left behind $($stale.Name) (locked by a running instance?)"
    }
}

$hash = (Get-FileHash -LiteralPath $finalPath -Algorithm SHA256).Hash
"$hash  $finalName" | Set-Content -LiteralPath (Join-Path $dist "$finalName.sha256") -Encoding ASCII

$sizeMb = [math]::Round((Get-Item -LiteralPath $finalPath).Length / 1MB, 2)
Write-Host ""
Write-Host "Artifact : $finalPath"
Write-Host "Size     : $sizeMb MB"
Write-Host "SHA256   : $hash"

# ---------------------------------------------------------------------------
# 便携文件夹 + zip（推荐分发形态）
#
# 为什么推荐 zip 而不是压缩单文件（0.10.10 实测）：
#   * 压缩单文件（约 85 MB）：运行时要把压缩包里的程序集解压进内存 → 空闲 WorkingSet 明显更高
#     （历史实测约 310 MB）
#   * 解压版（约 208 MB 目录 / 约 96 MB zip）：程序集按文件映射 → 空闲 WorkingSet 约 106~108 MB
#   * 单文件还需要往 %TEMP%\.net\<name> 自解压：实测出现过 "Failed to create directory ... Error code: 5"
#   * 所以即使 zip 比压缩单文件大 ~11 MB，仍以解压版作为推荐分发形态（省内存、启动更稳）
# ---------------------------------------------------------------------------
if (-not $FrameworkDependent) {
    # 发布到 %TEMP% 下的干净目录再打成 zip，而不是直接写 dist\portable。
    # 原因：在仓库内反复 publish 到同一个 dist\portable 不稳 —— 实测出现过
    #   ① 上一次发布被中断后目录清不干净（本工作区 ACL 含 Everyone Deny DeleteSubdirectoriesAndFiles），
    #   ② 结果打出"框架依赖"的便携包（zip 从 ~95MB 掉到 ~31MB，装到干净机器起不来）。
    # 每次用全新临时目录就与上一轮的残留完全无关，产物可复现。
    $portable = Join-Path $env:TEMP "simocr-portable-$Version-$Runtime"
    if (Test-Path -LiteralPath $portable) {
        Remove-Item -LiteralPath $portable -Recurse -Force
        if (Test-Path -LiteralPath $portable) {
            throw "Could not clear the previous portable staging folder: $portable"
        }
    }

    Write-Host ""
    Write-Host "Publishing portable folder build ..."
    $folderArgs = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', $Runtime,
        # .NET 6+ 里指定 -r 不再隐含 self-contained，必须显式写死；
        # 否则会打出一个 0.5 MB 的框架依赖包（要求用户预装 .NET），不是我们要的便携版。
        '--self-contained=true',
        '-p:SelfContained=true',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=true',
        '-p:DebugType=none',
        "-p:Version=$Version",
        '-p:SatelliteResourceLanguages=en',
        '-o', $portable,
        '--nologo'
    )

    & dotnet @folderArgs
    if ($LASTEXITCODE -ne 0) { throw "portable folder publish failed with exit code $LASTEXITCODE" }

    # 发版闸门：便携包必须是"自包含 + 带模型 + 带 VC++ 运行时"，否则直接失败（宁可不出包）
    $missing = @()
    if (-not (Test-Path -LiteralPath (Join-Path $portable 'coreclr.dll'))) { $missing += 'coreclr.dll(自包含运行时)' }
    if (-not (Test-Path -LiteralPath (Join-Path $portable 'hostfxr.dll'))) { $missing += 'hostfxr.dll(自包含运行时)' }
    foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $portable $dll))) { $missing += "$dll(VC++ 运行时/app-local)" }
    }
    foreach ($model in @('det.onnx', 'rec.onnx', 'cls.onnx', 'dict.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $portable "models\v5-cn\$model"))) { $missing += "models\v5-cn\$model" }
    }
    # 表格解析配置档也必须进包：运行时按 AppContext.BaseDirectory\Profiles 去找，
    # 缺了它语义层会静默退回内置兜底（表面能跑，但字段词表全部失效）。
    $profileDir = Join-Path $portable 'Profiles'
    if (-not (Test-Path -LiteralPath $profileDir)) {
        $missing += 'Profiles\(表格解析配置档目录)'
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $profileDir 'default.yaml'))) {
        $missing += 'Profiles\default.yaml(默认档，必须有)'
    }
    # 许可证与第三方声明也必须进包（Apache-2.0 §4(a)/(d)）：只写在仓库里，下载用户是拿不到的。
    foreach ($legal in @('LICENSE', 'NOTICE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $portable $legal))) {
            $missing += "$legal(Apache-2.0 许可证/第三方组件声明)"
        }
    }
    $runtimeConfig = Join-Path $portable 'SimOCR.runtimeconfig.json'
    if ((Test-Path -LiteralPath $runtimeConfig) -and
        -not ((Get-Content -LiteralPath $runtimeConfig -Raw) -match 'includedFrameworks')) {
        $missing += 'runtimeconfig.json(includedFrameworks: 说明打成了框架依赖包)'
    }
    if ($missing.Count -gt 0) {
        throw ("便携包内容不合格，已阻止出包：" + ($missing -join '; ') + "  （目录：$portable）")
    }

    $zipName = "SimOCR-$Version-portable-$Runtime.zip"
    $zipPath = Join-Path $dist $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $portable, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    "$zipHash  $zipName" | Set-Content -LiteralPath (Join-Path $dist "$zipName.sha256") -Encoding ASCII

    $zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
    $folderMb = [math]::Round((Get-ChildItem -LiteralPath $portable -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 2)
    Write-Host "Portable : $zipPath"
    Write-Host "Zip size : $zipMb MB  (unzipped $folderMb MB)"
    Write-Host "SHA256   : $zipHash"
}

# ---------------------------------------------------------------------------
# 自解压安装器（SimOCR-Setup，推荐给普通用户的分发形态）
#
# 形态：一个 exe，双击 -> 弹出原生进度窗 -> 解压到 %LOCALAPPDATA%\Programs\SimOCR
#       -> 建开始菜单/桌面快捷方式 + "应用和功能"卸载项 -> 启动主程序。
#
# 实现要点（详见 src/SimOCR.Installer/*.cs 的注释）：
#   * NativeAOT + WinExe：不依赖 .NET 运行时、无控制台窗口，安装器本体只有 ~2.4 MB；
#     WPF/WinForms 在 AOT 下不可用，进度窗是纯 P/Invoke 画的（comctl32 进度条）。
#   * 便携 zip 作为嵌入资源打进 exe，所以 Setup 体积 = 2.4 MB + zip。
#   * 卸载器是"同一个工程、不带 payload"的构建结果，作为第二个嵌入资源被释放到安装目录。
#   * 构建顺序不能颠倒：先便携 zip -> 再卸载器 -> 最后安装器（后两者要嵌入前者的产物）。
# ---------------------------------------------------------------------------
if (-not $SkipInstaller -and -not $FrameworkDependent) {
    $installerProject = Join-Path $root 'src\SimOCR.Installer\SimOCR.Installer.csproj'
    $payloadDir = Join-Path $root 'src\SimOCR.Installer\payload'
    if (-not (Test-Path -LiteralPath $installerProject)) {
        Write-Warning "Installer project not found, skipping: $installerProject"
    }
    else {
        New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

        $zipName = "SimOCR-$Version-portable-$Runtime.zip"
        $zipPath = Join-Path $dist $zipName
        if (-not (Test-Path -LiteralPath $zipPath)) { throw "Portable zip missing: $zipPath" }

        Copy-Item -LiteralPath $zipPath -Destination (Join-Path $payloadDir 'SimOCR-portable.zip') -Force

        Write-Host ""
        Write-Host "Publishing uninstaller (NativeAOT, no payload) ..."
        $uninstallerArgs = @(
            'publish', $installerProject,
            '-c', $Configuration,
            '-r', $Runtime,
            '-p:IncludePayload=false',
            '-p:IncludeUninstaller=false',
            '-p:AssemblyName=SimOCR-Uninstall',
            '-p:DebugType=none',
            "-p:Version=$Version",
            '-p:SatelliteResourceLanguages=en',
            '-o', $payloadDir,
            '--nologo'
        )

        & dotnet @uninstallerArgs
        if ($LASTEXITCODE -ne 0) { throw "uninstaller publish failed with exit code $LASTEXITCODE" }

        $uninstaller = Join-Path $payloadDir 'SimOCR-Uninstall.exe'
        if (-not (Test-Path -LiteralPath $uninstaller)) { throw "uninstaller publish produced no exe" }

        Write-Host ""
        Write-Host "Publishing setup (NativeAOT + embedded payload) ..."
        $setupArgs = @(
            'publish', $installerProject,
            '-c', $Configuration,
            '-r', $Runtime,
            '-p:DebugType=none',
            "-p:Version=$Version",
            '-p:SatelliteResourceLanguages=en',
            '-o', $dist,
            '--nologo'
        )

        & dotnet @setupArgs
        if ($LASTEXITCODE -ne 0) { throw "setup publish failed with exit code $LASTEXITCODE" }

        $setupExe = Join-Path $dist 'SimOCR-Setup.exe'
        if (-not (Test-Path -LiteralPath $setupExe)) { throw "setup publish produced no SimOCR-Setup.exe" }

        $setupName = "SimOCR-$Version-setup-$Runtime.exe"
        $setupPath = Join-Path $dist $setupName
        Move-Item -LiteralPath $setupExe -Destination $setupPath -Force

        $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
        "$setupHash  $setupName" | Set-Content -LiteralPath (Join-Path $dist "$setupName.sha256") -Encoding ASCII

        $setupMb = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
        $uninstallerMb = [math]::Round((Get-Item -LiteralPath $uninstaller).Length / 1MB, 2)
        Write-Host "Setup    : $setupPath"
        Write-Host "Setup size : $setupMb MB  (payload $zipMb MB + installer overhead)"
        Write-Host "Uninstaller: $uninstallerMb MB"
        Write-Host "SHA256   : $setupHash"
    }
}

Write-Host ""
Write-Host "Smoke test hint: unzip the portable build and run SimOCR.exe --bootstrap-selftest"
