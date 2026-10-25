# ---------------------------------------------------------------------------
# Fetch the PP-OCRv5 Chinese ONNX models used by SimOCR V1 (offline OCR).
#
# Source: RapidAI/RapidOCR v3.9.2 on ModelScope (Apache-2.0; models from PaddleOCR,
# also Apache-2.0). Every download is verified against the SHA256 published in
# RapidAI's default_models.yaml.
#
#   models/v5/det.onnx   ch_PP-OCRv5_det_mobile        4.6 MB
#   models/v5/rec.onnx   ch_PP-OCRv5_rec_mobile       15.9 MB
#   models/v5/cls.onnx   ch_ppocr_mobile_v2.0_cls      0.6 MB  (matches RapidOcrNet's TextClassifier)
#   models/v5/dict.txt   ppocrv5_dict.txt            18383 chars
#
# models/v5 is committed to this repository (~21 MB), so a fresh clone can build
# and run offline; this script re-downloads or verifies those files.
#
# This file is intentionally ASCII-only: Windows PowerShell 5.1 reads BOM-less
# script files as ANSI, which corrupts non-ASCII literals.
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$Destination = '',
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$root = Split-Path -Parent $scriptDir
if (-not $Destination) { $Destination = Join-Path $root 'models\v5' }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$base = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2'
$items = @(
    @{
        Name = 'det.onnx'
        Url  = "$base/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx"
        Sha  = '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae'
        Note = 'ch_PP-OCRv5_det_mobile'
    },
    @{
        Name = 'rec.onnx'
        Url  = "$base/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"
        Sha  = '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5'
        Note = 'ch_PP-OCRv5_rec_mobile'
    },
    @{
        Name = 'cls.onnx'
        Url  = "$base/onnx/PP-OCRv4/cls/ch_ppocr_mobile_v2.0_cls_mobile.onnx"
        Sha  = 'e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c'
        Note = 'ch_ppocr_mobile_v2.0_cls'
    },
    @{
        Name = 'dict.txt'
        Url  = "$base/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt"
        Sha  = 'd1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b'
        Note = 'ppocrv5_dict'
    }
)

$failed = 0
foreach ($item in $items) {
    $target = Join-Path $Destination $item.Name
    if ((Test-Path -LiteralPath $target) -and -not $Force) {
        Write-Host ("have   {0,-9} {1}" -f $item.Name, $item.Note)
    }
    else {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            Invoke-WebRequest -Uri $item.Url -OutFile $target -TimeoutSec 900 -UseBasicParsing
        }
        catch {
            Write-Warning ("download failed for {0}: {1}" -f $item.Name, $_.Exception.Message)
            $failed++
            continue
        }
        $watch.Stop()
        $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 2)
        Write-Host ("fetched {0,-9} {1,6} MB in {2,3}s  {3}" -f $item.Name, $mb, [int]$watch.Elapsed.TotalSeconds, $item.Note)
    }

    if ($item.Sha) {
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $item.Sha) {
            Write-Warning ("SHA256 mismatch for {0}`n  expected {1}`n  actual   {2}" -f $item.Name, $item.Sha, $actual)
            $failed++
        }
        else {
            Write-Host ("  sha256 ok {0}" -f $item.Name)
        }
    }
}

$dict = Join-Path $Destination 'dict.txt'
if (Test-Path -LiteralPath $dict) {
    # NOTE: use the absolute path here. [System.IO.File] resolves relative paths against the
    # *process* working directory, which in PowerShell is not necessarily the current location
    # (a relative read once produced a bogus "1553 lines" warning for a correct 18383-line file).
    $lines = [System.IO.File]::ReadAllLines($dict, [System.Text.Encoding]::UTF8).Length
    Write-Host ("dict.txt lines = {0} (expected 18383)" -f $lines)
    if ($lines -ne 18383) { Write-Warning 'dict.txt line count is unexpected - check the model/dict pairing' }
}

Write-Host ''
Write-Host ("Models in {0}:" -f $Destination)
Get-ChildItem -LiteralPath $Destination -File | ForEach-Object {
    Write-Host ("  {0,-9} {1,7:N2} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host 'License: Apache-2.0 (RapidAI/RapidOCR, models from PaddleOCR). Keep NOTICE entries in the shipped docs.'

if ($failed -gt 0) { throw "$failed model step(s) failed" }
