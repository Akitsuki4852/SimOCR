# Measures idle RSS / private bytes of a SimOCR build (tray-only, no capture).
# ASCII-only on purpose.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$WaitSeconds = 15
)

# 0.10.7：这里过去传 `--minimized`，而应用**早已不再解析**该参数
#（AutoStartService 也不再写它）—— 传一个不存在的开关只会误导读者。
# 参数一个都不需要：托盘启动就是本脚本要测的形态。
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds $WaitSeconds

$proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Host "process exited early (blocked by SmartScreen?)"
    exit 2
}

$ws = [math]::Round($proc.WorkingSet64 / 1MB, 1)
$priv = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
$handles = $proc.HandleCount
$threads = $proc.Threads.Count

Write-Host ("idle after {0}s: WorkingSet={1} MB  Private={2} MB  Handles={3}  Threads={4}" -f $WaitSeconds, $ws, $priv, $handles, $threads)

$p.Kill()
$p.WaitForExit(5000)
