# ---------------------------------------------------------------------------
# fetch-vcredist.ps1 - stage the Visual C++ v14 runtime DLLs next to the app.
#
# WHY THIS EXISTS (R-17):
#   RapidOcrNet loads onnxruntime.dll, which is a NATIVE library built with the
#   MSVC toolchain. Its import table needs VCRUNTIME140.dll / VCRUNTIME140_1.dll /
#   MSVCP140.dll / MSVCP140_1.dll. Those files are NOT part of Windows: they are
#   present only when the "Microsoft Visual C++ v14 Redistributable" is installed
#   (on the dev host it is installed, which is why the app worked there).
#   On a clean Windows machine without the redistributable, loading onnxruntime.dll
#   fails and OCR is unusable - exactly the "no extra VC++ components" case that
#   acceptance risk R-17 is about.
#
#   Fix: ship the DLLs app-local (next to SimOCR.exe). Microsoft's redistributable
#   terms allow app-local redistribution of these files with your application, and
#   app-local wins over System32 in the loader search order, so the app is then
#   independent of whatever the target machine has installed.
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$Destination = '',
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$root = Split-Path -Parent $scriptDir
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $root 'runtime\vcredist-x64'
}

# Import table of onnxruntime.dll / onnxruntime_providers_shared.dll (measured with a
# string scan of the shipped binaries - see docs/acceptance record V1).
$required = @(
    'vcruntime140.dll',
    'vcruntime140_1.dll',
    'msvcp140.dll',
    'msvcp140_1.dll'
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$system32 = Join-Path $env:WINDIR 'System32'
$staged = @()

foreach ($name in $required) {
    $source = Join-Path $system32 $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing $name in $system32. Install the Microsoft Visual C++ v14 Redistributable (x64) on the build machine first."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if ($signature.Status -ne 'Valid') {
        throw "$name is not validly signed (status=$($signature.Status)); refusing to ship it."
    }
    if ($signature.SignerCertificate.Subject -notlike '*Microsoft*') {
        throw "$name is signed by '$($signature.SignerCertificate.Subject)', not Microsoft; refusing to ship it."
    }

    $target = Join-Path $Destination $name
    Copy-Item -LiteralPath $source -Destination $target -Force

    $info = Get-Item -LiteralPath $target
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $staged += [pscustomobject]@{
        Name    = $name
        Version = $info.VersionInfo.FileVersion
        Bytes   = $info.Length
        Sha256  = $hash
    }

    if (-not $Quiet) {
        Write-Host ("  {0,-22} v{1,-16} {2,9:N0} B  {3}" -f $name, $info.VersionInfo.FileVersion, $info.Length, $hash.Substring(0, 16))
    }
}

# Write provenance next to the DLLs so the shipped set is traceable.
$manifest = Join-Path $Destination 'PROVENANCE.txt'
$lines = @()
$lines += 'Visual C++ v14 runtime DLLs staged for app-local deployment (SimOCR).'
$lines += 'Source: ' + $system32 + ' (Microsoft Visual C++ v14 Redistributable, x64)'
$lines += 'Staged: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
$lines += 'Reason: onnxruntime.dll (ONNX Runtime, used by RapidOcrNet) imports these;'
$lines += '        a clean Windows without the redistributable cannot load it, so OCR would fail.'
$lines += 'License: Microsoft Visual C++ Redistributable terms - app-local redistribution with the application.'
$lines += ''
foreach ($item in $staged) {
    $lines += ("{0}  v{1}  {2} bytes  SHA256={3}" -f $item.Name, $item.Version, $item.Bytes, $item.Sha256)
}
[System.IO.File]::WriteAllLines($manifest, $lines, (New-Object System.Text.UTF8Encoding($false)))

if (-not $Quiet) {
    Write-Host "Staged $($staged.Count) DLLs to $Destination"
}
