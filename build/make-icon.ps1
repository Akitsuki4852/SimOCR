<#
.SYNOPSIS
    Convert a square source PNG into a multi-size Windows .ico (PNG-compressed entries, Vista+).

.DESCRIPTION
    Generates the SimOCR application icon (exe icon + tray icon) from a square source PNG,
    writing src/SimOCR/Assets/SimOCR.ico.

    The source PNG is deliberately NOT hard-coded here: build/ ships with the open-source
    repository, so this file must not contain any internal asset path. The default source is
    build/icon-source.png (next to this script); when it is absent this script prints a
    message and exits 0 WITHOUT regenerating anything - the committed .ico stays as it is.
    Missing artwork must never break a build.

    NOTE: this file is intentionally ASCII-only: Windows PowerShell 5.1 reads BOM-less
    script files as ANSI, which corrupts non-ASCII literals.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build/make-icon.ps1

.EXAMPLE
    # caller-supplied source (a dev-side wrapper passes its own asset path here)
    powershell -NoProfile -ExecutionPolicy Bypass -File build/make-icon.ps1 -SourcePng <square.png>
#>
[CmdletBinding()]
param(
    [string]$SourcePng,
    [string]$OutputIco,
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# NOTE: $PSScriptRoot is not reliably bound inside param() default expressions,
# so paths are resolved here in the body instead.
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$sourceExplicit = [bool]$SourcePng
if (-not $SourcePng) { $SourcePng = Join-Path $scriptDir 'icon-source.png' }
if (-not $OutputIco) { $OutputIco = Join-Path $scriptDir '..\src\SimOCR\Assets\SimOCR.ico' }

# Missing source is NOT an error: the repository ships the generated .ico, and the source
# artwork may legitimately be absent from a given checkout (it is not part of build/).
if (-not (Test-Path -LiteralPath $SourcePng)) {
    if ($sourceExplicit) {
        Write-Host "SKIP: source PNG not found: $SourcePng"
    } else {
        Write-Host "SKIP: no -SourcePng given and the default source is absent: $SourcePng"
    }

    Write-Host "      nothing regenerated; the committed icon is left untouched: $OutputIco"
    Write-Host "      pass -SourcePng <square.png> to regenerate the icon."
    exit 0
}

$outDir = Split-Path -Parent $OutputIco
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$srcPath = (Resolve-Path -LiteralPath $SourcePng).Path
$src = [System.Drawing.Image]::FromFile($srcPath)
try {
    if ($src.Width -ne $src.Height) { throw "Source must be square (got $($src.Width)x$($src.Height))" }

    $payloads = New-Object System.Collections.Generic.List[byte[]]
    foreach ($size in $Sizes) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($src, 0, 0, $size, $size)
            } finally { $g.Dispose() }

            $ms = New-Object System.IO.MemoryStream
            try {
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                $payloads.Add($ms.ToArray())
            } finally { $ms.Dispose() }
        } finally { $bmp.Dispose() }
    }

    $fs = [System.IO.File]::Create($OutputIco)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$Sizes.Count)

        $offset = 6 + (16 * $Sizes.Count)
        for ($i = 0; $i -lt $Sizes.Count; $i++) {
            $s = $Sizes[$i]
            $dim = 0
            if ($s -lt 256) { $dim = $s }
            $bw.Write([byte]$dim)
            $bw.Write([byte]$dim)
            $bw.Write([byte]0)
            $bw.Write([byte]0)
            $bw.Write([uint16]1)
            $bw.Write([uint16]32)
            $bw.Write([uint32]$payloads[$i].Length)
            $bw.Write([uint32]$offset)
            $offset += $payloads[$i].Length
        }
        foreach ($p in $payloads) { $bw.Write($p) }
    } finally { $bw.Dispose(); $fs.Dispose() }
} finally { $src.Dispose() }

$ico = Get-Item -LiteralPath $OutputIco
Write-Host ("Generated: {0} ({1} KB, sizes: {2})" -f $ico.FullName, [math]::Round($ico.Length / 1KB, 1), ($Sizes -join '/'))
