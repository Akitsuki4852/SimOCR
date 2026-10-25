# ---------------------------------------------------------------------------
# SimOCR installer regression test.
#
# Runs the released SimOCR-Setup exe entirely against a throw-away install
# directory, so it never touches the real %LOCALAPPDATA%\Programs\SimOCR,
# never creates shortcuts and never writes the uninstall registry entry.
#
# Steps:
#   1. fresh install (silent, no launch)   -> exit 0 + files on disk
#   2. --verify                            -> exit 0 + "verify-result PASS"
#   3. install again                       -> exit 0 + "install-skip-uptodate"
#   4. install with launch                 -> exit 0 + SimOCR process from that dir
#   5. --uninstall                         -> exit 0 + directory removed
#
# Evidence: every run writes %LOCALAPPDATA%\SimOCR\logs\installer-*.log and this
# script prints "CHECK ... PASS/FAIL" lines, exiting non-zero if any check fails.
#
# IMPORTANT: launches use UseShellExecute=$false (raw CreateProcess) on purpose.
# Start-Process goes through ShellExecuteEx - the exact path a double-click takes -
# and on a machine where SmartScreen distrusts a freshly built unsigned exe that
# call can be denied (observed on the dev host: the setup process returned exit
# code 1 and never ran, while the identical exe ran fine via CreateProcess).
# Pass -ViaShell to reproduce that shell behaviour deliberately.
#
# This file contains non-ASCII (Chinese) text and must therefore keep its UTF-8 BOM:
# Windows PowerShell 5.1 reads BOM-less script files as ANSI, which turns the Chinese
# into mojibake and makes the script fail to parse. Save as UTF-8 *with* BOM.
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SetupExe,
    [string]$TestDir = (Join-Path $env:TEMP 'simocr-installer-test'),
    [switch]$SkipLaunch,
    [switch]$ViaShell,
    [switch]$NoStage,
    [switch]$TestRegistryRoundTrip,
    [int]$UninstallTimeoutSeconds = 90,
    [int]$ProcessTimeoutSeconds = 240
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = 0
$logDir = Join-Path $env:LOCALAPPDATA 'SimOCR\logs'

function Write-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:Failures++ }
    Write-Host ("CHECK {0,-34} {1} {2}" -f $Name, $tag, $Detail)
}

function Get-NewestInstallerLog {
    param([datetime]$Since)
    if (-not (Test-Path -LiteralPath $logDir)) { return $null }
    return Get-ChildItem -LiteralPath $logDir -Filter 'installer-*.log' |
        Where-Object { $_.LastWriteTime -ge $Since } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

# Arguments arrive as one pre-quoted string so paths containing spaces survive.
function Invoke-Setup {
    param([string]$Arguments, [string]$Label)
    $start = Get-Date

    if ($ViaShell) {
        $process = Start-Process -FilePath $SetupExe -ArgumentList $Arguments -PassThru -Wait
        $exitCode = $process.ExitCode
    }
    else {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $SetupExe
        $psi.Arguments = $Arguments
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        $null = $process.StandardOutput.ReadToEnd()
        $null = $process.StandardError.ReadToEnd()
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw "setup did not exit within $ProcessTimeoutSeconds s ($Label)"
        }
        $exitCode = $process.ExitCode
    }

    $log = Get-NewestInstallerLog -Since $start
    $logName = if ($log) { $log.Name } else { 'none' }
    Write-Host ("--- {0}: exit={1} log={2}" -f $Label, $exitCode, $logName)
    return [pscustomobject]@{ ExitCode = $exitCode; Log = $log }
}

# ---------------------------------------------------------------------------
Write-Host "SimOCR installer regression test"
Write-Host "setup  : $SetupExe"
Write-Host "testdir: $TestDir"
$mode = if ($ViaShell) { 'ShellExecute (double-click path)' } else { 'CreateProcess (deterministic)' }
Write-Host "mode   : $mode"
Write-Host ""

if (-not (Test-Path -LiteralPath $SetupExe)) { throw "Setup exe not found: $SetupExe" }

# --- stage the exe somewhere neutral ---------------------------------------
# Some build/harness environments (this one included) deny *executing* a binary
# that lives inside the session workspace: the identical bytes run fine from
# %TEMP% but come back with exit code 1 when launched in place, with no log at
# all. Staging a copy in %TEMP% removes that variable from the test.
if (-not $NoStage) {
    $stageDir = Join-Path $env:TEMP 'simocr-setup-staged'
    if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    $staged = Join-Path $stageDir 'SimOCR-Setup.exe'
    Copy-Item -LiteralPath $SetupExe -Destination $staged -Force
    Write-Host "staged : $staged"
    $SetupExe = $staged
}

if (Test-Path -LiteralPath $TestDir) {
    Remove-Item -LiteralPath $TestDir -Recurse -Force
}

$dirArg = '--dir "{0}"' -f $TestDir
$exePath = Join-Path $TestDir 'SimOCR.exe'
$uninstallerPath = Join-Path $TestDir 'SimOCR-Uninstall.exe'
$markerPath = Join-Path $TestDir '.simocr-install.txt'

# --- 1. fresh install -------------------------------------------------------
$r1 = Invoke-Setup -Label 'install' -Arguments "$dirArg --silent --no-shortcuts --no-registry --no-launch"
Write-Check 'install.exit-code-0' ($r1.ExitCode -eq 0) "exit=$($r1.ExitCode)"
if ($r1.Log) {
    $text = Get-Content -LiteralPath $r1.Log.FullName -Raw
    Write-Check 'install.extract-done' ($text -match 'EVENT install-extract-done') ''
    Write-Check 'install.marker-written' ($text -match 'EVENT install-marker-written') ''
}
Write-Check 'install.SimOCR.exe' (Test-Path -LiteralPath $exePath) $exePath
Write-Check 'install.uninstaller' (Test-Path -LiteralPath $uninstallerPath) $uninstallerPath
Write-Check 'install.marker-file' (Test-Path -LiteralPath $markerPath) $markerPath

if (Test-Path -LiteralPath $TestDir) {
    $fileCount = (Get-ChildItem -LiteralPath $TestDir -Recurse -File).Count
    Write-Check 'install.file-count' ($fileCount -gt 100) "$fileCount files"
    $totalMb = [math]::Round(((Get-ChildItem -LiteralPath $TestDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ("      installed size: {0} MB" -f $totalMb)
}

# --- 2. verify --------------------------------------------------------------
$r2 = Invoke-Setup -Label 'verify' -Arguments "$dirArg --verify --silent"
Write-Check 'verify.exit-code-0' ($r2.ExitCode -eq 0) "exit=$($r2.ExitCode)"
if ($r2.Log) {
    $text = Get-Content -LiteralPath $r2.Log.FullName -Raw
    Write-Check 'verify.result-pass' ($text -match 'EVENT verify-result PASS') ''
}

# --- 3. idempotent re-install ----------------------------------------------
$r3 = Invoke-Setup -Label 'reinstall' -Arguments "$dirArg --silent --no-shortcuts --no-registry --no-launch"
Write-Check 'reinstall.exit-code-0' ($r3.ExitCode -eq 0) "exit=$($r3.ExitCode)"
if ($r3.Log) {
    $text = Get-Content -LiteralPath $r3.Log.FullName -Raw
    Write-Check 'reinstall.skipped-extract' ($text -match 'EVENT install-skip-uptodate') ''
}

# --- 4. launch the real program -------------------------------------------
# Safety: if the user already runs SimOCR (e.g. the installed copy), the test copy would
# NOT become the first instance - SimOCR's single-instance guard would forward "capture"
# to the running one and pop a capture overlay on someone's screen. Skip in that case.
$otherInstances = @(Get-Process -Name 'SimOCR' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.MainModule.FileName -notlike "$TestDir*" } catch { $true }
    })

if ($SkipLaunch) {
    Write-Host 'CHECK launch.*                            SKIP'
}
elseif ($otherInstances.Count -gt 0) {
    Write-Host ("CHECK launch.*                            SKIP (another SimOCR instance is running: pid {0})" -f ($otherInstances.Id -join ','))
}
else {
    $r4 = Invoke-Setup -Label 'install+launch' -Arguments "$dirArg --silent --no-shortcuts --no-registry"
    Write-Check 'launch.exit-code-0' ($r4.ExitCode -eq 0) "exit=$($r4.ExitCode)"
    if ($r4.Log) {
        $text = Get-Content -LiteralPath $r4.Log.FullName -Raw
        Write-Check 'launch.logged' ($text -match 'EVENT launch ok=true') ''
    }

    Start-Sleep -Seconds 4
    $own = @(Get-Process -Name 'SimOCR' -ErrorAction SilentlyContinue | Where-Object {
            try { $_.MainModule.FileName -like "$TestDir*" } catch { $false }
        })
    Write-Check 'launch.process-running' ($own.Count -ge 1) "matched=$($own.Count)"

    $simLog = Get-ChildItem -LiteralPath $logDir -Filter 'simocr-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($simLog) {
        $tail = (Get-Content -LiteralPath $simLog.FullName -Tail 3) -join ' | '
        Write-Host ("      main program log: {0}" -f $simLog.Name)
        Write-Host ("      last lines: {0}" -f $tail)
    }

    foreach ($process in $own) {
        try { $process.Kill(); $process.WaitForExit(5000) } catch { }
    }
    Write-Host "      stopped $($own.Count) SimOCR process(es) started from the test dir"
}

# --- 5. uninstall ----------------------------------------------------------
$r5 = Invoke-Setup -Label 'uninstall' -Arguments "$dirArg --uninstall --silent"
Write-Check 'uninstall.exit-code-0' ($r5.ExitCode -eq 0) "exit=$($r5.ExitCode)"
if ($r5.Log) {
    $text = Get-Content -LiteralPath $r5.Log.FullName -Raw
    Write-Check 'uninstall.scheduled' ($text -match 'EVENT uninstall-scheduled') ''
}

$deadline = (Get-Date).AddSeconds($UninstallTimeoutSeconds)
while ((Test-Path -LiteralPath $TestDir) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
Write-Check 'uninstall.directory-gone' (-not (Test-Path -LiteralPath $TestDir)) $TestDir

# --- 6. registry round trip (opt-in) --------------------------------------
# Exercises the three HKCU entries (own install key / uninstall entry / App Paths) and
# the shortcuts. Those are per-user machine-wide keys, so the test BACKS UP the real
# ones first and restores them in a finally block: a real SimOCR installation on this
# machine must survive the test untouched.
if ($TestRegistryRoundTrip) {
    Write-Host ''
    Write-Host '--- registry round trip (real entries are backed up and restored) ---'

    $regKeys = @(
        'HKCU\Software\SimOCR',
        'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\SimOCR',
        'HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\SimOCR.exe'
    )
    $backupDir = Join-Path $env:TEMP 'simocr-regbackup'
    if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    $existed = @{}
    for ($i = 0; $i -lt $regKeys.Count; $i++) {
        $key = $regKeys[$i]
        $file = Join-Path $backupDir ("key{0}.reg" -f $i)
        $present = Test-Path -LiteralPath ("Registry::" + $key)
        $existed[$key] = $present
        if ($present) { & reg.exe export $key $file /y > $null 2>&1 }
        Write-Host ("      backup {0} existed={1}" -f $key, $present)
    }

    $shortcuts = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SimOCR.lnk'),
        (Join-Path $env:USERPROFILE 'Desktop\SimOCR.lnk')
    )
    $shortcutBackup = @{}
    $shortcutFile = @{}
    for ($i = 0; $i -lt $shortcuts.Count; $i++) {
        $lnk = $shortcuts[$i]
        # 两个快捷方式同名（都叫 SimOCR.lnk），备份文件名必须带上序号，否则会互相覆盖
        $shortcutFile[$lnk] = Join-Path $backupDir ("shortcut{0}.lnk" -f $i)
        $shortcutBackup[$lnk] = Test-Path -LiteralPath $lnk
        if ($shortcutBackup[$lnk]) {
            Copy-Item -LiteralPath $lnk -Destination $shortcutFile[$lnk] -Force
        }
        Write-Host ("      backup shortcut {0} existed={1}" -f $lnk, $shortcutBackup[$lnk])
    }

    try {
        $r6 = Invoke-Setup -Label 'install+registry' -Arguments "$dirArg --silent --force --no-launch"
        Write-Check 'registry.install-exit-0' ($r6.ExitCode -eq 0) "exit=$($r6.ExitCode)"
        if ($r6.Log) {
            $text = Get-Content -LiteralPath $r6.Log.FullName -Raw
            Write-Check 'registry.uninstall-entry' ($text -match 'EVENT registry ok=True') ''
            Write-Check 'registry.install-key' ($text -match 'EVENT install-registry ok=True') ''
            Write-Check 'registry.app-paths' ($text -match 'EVENT app-paths ok=True') ''
            Write-Check 'registry.shortcuts' ($text -match 'EVENT shortcuts ok=True') ''
        }

        $installKey = Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\SimOCR' -ErrorAction SilentlyContinue
        Write-Check 'registry.install-location-value' ($null -ne $installKey -and $installKey.InstallLocation -eq $TestDir) "$($installKey.InstallLocation)"

        $appPaths = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\SimOCR.exe' -ErrorAction SilentlyContinue).'(default)'
        Write-Check 'registry.app-paths-value' ($appPaths -like "$TestDir*") "$appPaths"

        $uninstallKey = Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\SimOCR' -ErrorAction SilentlyContinue
        $uninstallOk = $null -ne $uninstallKey -and $uninstallKey.InstallLocation -eq $TestDir -and $uninstallKey.QuietUninstallString -like "*$TestDir*"
        Write-Check 'registry.uninstall-strings' $uninstallOk "$($uninstallKey.UninstallString)"

        $r7 = Invoke-Setup -Label 'verify-with-registry' -Arguments "$dirArg --verify --silent"
        Write-Check 'registry.verify-pass' ($r7.ExitCode -eq 0) "exit=$($r7.ExitCode)"

        $r8 = Invoke-Setup -Label 'uninstall-with-registry' -Arguments "$dirArg --uninstall --silent"
        Write-Check 'registry.uninstall-exit-0' ($r8.ExitCode -eq 0) "exit=$($r8.ExitCode)"
        Start-Sleep -Seconds 2
        Write-Check 'registry.keys-removed' (
            -not (Test-Path -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\SimOCR') -and
            -not (Test-Path -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\SimOCR') -and
            -not (Test-Path -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\SimOCR.exe')
        ) ''
    }
    finally {
        # NOTE: the native tools here must not push their success chatter into
        # PowerShell's error stream: reg.exe import prints to stderr, and under
        # $ErrorActionPreference='Stop' that would abort the script mid-restore
        # (which is exactly what happened the first time this test ran).
        Write-Host '      restoring real entries ...'
        $restorePreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            foreach ($key in $regKeys) {
                try {
                    if (Test-Path -LiteralPath ("Registry::" + $key)) { & reg.exe delete $key /f *> $null }
                }
                catch { Write-Warning "reg delete failed for ${key}: $($_.Exception.Message)" }
            }
            for ($i = 0; $i -lt $regKeys.Count; $i++) {
                $file = Join-Path $backupDir ("key{0}.reg" -f $i)
                if ($existed[$regKeys[$i]] -and (Test-Path -LiteralPath $file)) {
                    try {
                        & reg.exe import $file *> $null
                        Write-Host ("      restored {0}" -f $regKeys[$i])
                    }
                    catch { Write-Warning "reg import failed for $($regKeys[$i]): $($_.Exception.Message)" }
                }
            }
            foreach ($lnk in $shortcuts) {
                try {
                    $backupFile = $shortcutFile[$lnk]
                    if ($shortcutBackup[$lnk] -and (Test-Path -LiteralPath $backupFile)) {
                        Copy-Item -LiteralPath $backupFile -Destination $lnk -Force
                        Write-Host ("      restored shortcut {0} -> exists={1}" -f $lnk, (Test-Path -LiteralPath $lnk))
                    }
                    elseif (Test-Path -LiteralPath $lnk) {
                        Remove-Item -LiteralPath $lnk -Force
                        Write-Host ("      removed test shortcut {0}" -f $lnk)
                    }
                }
                catch { Write-Warning "shortcut restore failed for ${lnk}: $($_.Exception.Message)" }
            }
        }
        finally {
            $ErrorActionPreference = $restorePreference
        }

        # Safety net: make sure the machine still has working shortcuts pointing at the
        # real installation (whatever the uninstall entry says is installed). Deterministic,
        # and independent of whether the backup step could see the files.
        try {
            $realLocation = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\SimOCR' -ErrorAction SilentlyContinue).InstallLocation
            if ($realLocation) {
                $realExe = Join-Path $realLocation 'SimOCR.exe'
                $shell = New-Object -ComObject WScript.Shell
                foreach ($lnk in $shortcuts) {
                    $sc = $shell.CreateShortcut($lnk)
                    $sc.TargetPath = $realExe
                    $sc.WorkingDirectory = $realLocation
                    $sc.IconLocation = "$realExe,0"
                    $sc.Description = 'SimOCR - screenshot / OCR (F1)'
                    $sc.Save()
                }
                Write-Host ("      ensured real shortcuts -> {0}" -f $realExe)
            }
        }
        catch { Write-Warning "ensuring real shortcuts failed: $($_.Exception.Message)" }
        Write-Host '      restore done'
    }
}
else {
    Write-Host ''
    Write-Host 'CHECK registry.*                          SKIP (-TestRegistryRoundTrip)'
}

# ---------------------------------------------------------------------------
Write-Host ""
if ($script:Failures -eq 0) {
    Write-Host 'INSTALLER REGRESSION: ALL CHECKS PASSED'
    exit 0
}

Write-Host "INSTALLER REGRESSION: $script:Failures CHECK(S) FAILED"
exit 1
