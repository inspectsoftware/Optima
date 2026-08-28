# Generates src/Optima.App/Assets/optima.ico: a white square ring (block "O") on
# transparent, matching the app's monochrome terminal aesthetic. Dev-time script,
# run once from the repo root and commit the output:
#
#   powershell -File tools\generate-icon.ps1
#
# Entries 16-48 are stored as classic BMP frames (32bpp ARGB plus AND mask) because
# GDI+ and some shell consumers reject PNG-compressed frames at small sizes; only
# the 256 entry uses PNG, per the usual .ico convention. Rectangles are filled
# (outer white, inner punched back to transparent) instead of stroked, so edges
# stay pixel-crisp at 16px.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outPath = Join-Path $PSScriptRoot '..\src\Optima.App\Assets\optima.ico'
$outDir = Split-Path $outPath -Parent
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-GlyphBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $inset = [Math]::Floor($size / 8)
        $thickness = [Math]::Max(2, [Math]::Floor($size / 6))
        $outer = $size - 2 * $inset
        $g.FillRectangle([System.Drawing.Brushes]::White, $inset, $inset, $outer, $outer)
        # SourceCopy replaces pixels outright, so filling with Transparent punches the hole.
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $hole = $outer - 2 * $thickness
        $g.FillRectangle([System.Drawing.Brushes]::Transparent, $inset + $thickness, $inset + $thickness, $hole, $hole)
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

# 32bpp BMP icon frame: BITMAPINFOHEADER (doubled height), XOR rows bottom-up in
# BGRA, then an all-zero 1bpp AND mask (alpha channel does the masking).
function ConvertTo-BmpFrame([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $maskRow = [Math]::Ceiling($s / 32) * 4
    $w.Write([UInt32]40)                              # biSize
    $w.Write([Int32]$s)                               # biWidth
    $w.Write([Int32]($s * 2))                         # biHeight (XOR + AND)
    $w.Write([UInt16]1)                               # biPlanes
    $w.Write([UInt16]32)                              # biBitCount
    $w.Write([UInt32]0)                               # biCompression (BI_RGB)
    $w.Write([UInt32]($s * $s * 4 + $maskRow * $s))   # biSizeImage
    $w.Write([Int32]0); $w.Write([Int32]0)            # resolution
    $w.Write([UInt32]0); $w.Write([UInt32]0)          # colors used / important
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $w.Write([Int32]$bmp.GetPixel($x, $y).ToArgb())
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$frames = foreach ($size in $sizes) {
    $bmp = New-GlyphBitmap $size
    if ($size -lt 256) {
        $frame = ConvertTo-BmpFrame $bmp
    }
    else {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frame = $ms.ToArray()
    }
    $bmp.Dispose()
    , $frame
}

$stream = [System.IO.File]::Create($outPath)
try {
    $writer = New-Object System.IO.BinaryWriter($stream)
    # ICONDIR: reserved, type 1 (icon), image count.
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)
    # ICONDIRENTRY per image; width/height bytes use 0 to mean 256.
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([Byte]$dim)
        $writer.Write([Byte]$dim)
        $writer.Write([Byte]0)      # palette size (none)
        $writer.Write([Byte]0)      # reserved
        $writer.Write([UInt16]1)    # color planes
        $writer.Write([UInt16]32)   # bits per pixel
        $writer.Write([UInt32]$frames[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
    $writer.Flush()
}
finally {
    $stream.Dispose()
}

Write-Host "Wrote $outPath ($((Get-Item $outPath).Length) bytes, sizes: $($sizes -join ', '))"
