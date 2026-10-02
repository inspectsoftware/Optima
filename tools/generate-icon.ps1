# Generates the Optima icon set from the design mark:
#
#   design/optima-mark.png  ->  src/Optima.App/Assets/optima.ico
#                               src/Optima.App/Assets/optima-presence.png
#
# Dev-time script, run from anywhere and commit the output:
#
#   powershell -File tools\generate-icon.ps1
#
# The source art is a light mark on a transparent canvas with uneven margins, so
# the script crops to the mark's opaque bounds, centres it in a square, and pads
# each size by a fixed fraction of the canvas. Small frames are downscaled in
# halving steps (bicubic from 900px straight to 16px samples far too little), and
# fully transparent source pixels are tinted with the mark's own average colour
# first, which is what keeps hard edges from developing a dark halo once scaled.
#
# Frames <= 48 are stored as classic BMP frames (32bpp ARGB plus AND mask)
# because GDI+ and some shell consumers reject PNG-compressed frames at small
# sizes; the larger frames use PNG, per the usual .ico convention. Inno Setup
# reads the same .ico as its SetupIconFile, which is why a plain 32px BMP frame
# has to stay in the set.

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\design\optima-mark.png'),
    [string]$AssetDir = (Join-Path $PSScriptRoot '..\src\Optima.App\Assets'),
    # Fraction of the canvas kept clear on each side, so no icon size is
    # edge-to-edge. 0.07 gives the mark ~86% of the frame.
    [double]$Padding = 0.07
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Design mark not found: $Source" }

# Windows PowerShell 5.1 compiles this with the .NET Framework C# 5 provider: no
# `using var`, no string interpolation, and System.Drawing needs an explicit reference.
Add-Type -ReferencedAssemblies @('System.Drawing') -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>Turns one design PNG into the square frames every icon size is composited from.</summary>
public static class IconForge
{
    /// <summary>A loaded mark: opaque content cropped to a centred square, plus its average colour.</summary>
    public sealed class Mark
    {
        public Bitmap Image;
        public Color Average;
        public int SourceWidth, SourceHeight, CroppedSize;
    }

    /// <summary>Loads the art, tints transparent pixels and crops the opaque content to a centred square.</summary>
    public static Mark Load(string path, int alphaThreshold)
    {
        var canvas = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var file = new Bitmap(path))
        {
            canvas = new Bitmap(file.Width, file.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(file, 0, 0);
            }
        }

        var rect = new Rectangle(0, 0, canvas.Width, canvas.Height);
        var data = canvas.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var average = Color.Silver;
        long sumR = 0, sumG = 0, sumB = 0, weight = 0;
        int minX = canvas.Width, minY = canvas.Height, maxX = -1, maxY = -1;
        try
        {
            var stride = data.Stride;
            var bytes = new byte[Math.Abs(stride) * canvas.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            for (var y = 0; y < canvas.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < canvas.Width; x++)
                {
                    var i = row + x * 4;
                    var a = bytes[i + 3];
                    if (a <= alphaThreshold)
                    {
                        continue;
                    }
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                    sumB += (long)bytes[i] * a;
                    sumG += (long)bytes[i + 1] * a;
                    sumR += (long)bytes[i + 2] * a;
                    weight += a;
                }
            }

            average = weight == 0
                ? Color.Silver
                : Color.FromArgb(255, (int)(sumR / weight), (int)(sumG / weight), (int)(sumB / weight));

            // The halo fix: every fully transparent pixel carries the mark's own colour
            // instead of black, so interpolating across an edge never darkens it.
            var fillR = average.R;
            var fillG = average.G;
            var fillB = average.B;
            for (var y = 0; y < canvas.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < canvas.Width; x++)
                {
                    var i = row + x * 4;
                    if (bytes[i + 3] != 0)
                    {
                        continue;
                    }
                    bytes[i] = (byte)fillB;
                    bytes[i + 1] = (byte)fillG;
                    bytes[i + 2] = (byte)fillR;
                }
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        if (maxX < 0)
        {
            canvas.Dispose();
            throw new Exception("The design mark is fully transparent; nothing to generate.");
        }

        // Square the bounds around the content's centre and keep them inside the canvas.
        var side = Math.Max(maxX - minX + 1, maxY - minY + 1);
        var centreX = (minX + maxX) / 2;
        var centreY = (minY + maxY) / 2;
        var left = Math.Max(0, Math.Min(canvas.Width - side, centreX - side / 2));
        var top = Math.Max(0, Math.Min(canvas.Height - side, centreY - side / 2));
        side = Math.Min(side, Math.Min(canvas.Width - left, canvas.Height - top));

        var cropped = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(canvas, new Rectangle(0, 0, side, side), left, top, side, side, GraphicsUnit.Pixel);
        }
        var result = new Mark
        {
            Image = cropped,
            Average = average,
            SourceWidth = canvas.Width,
            SourceHeight = canvas.Height,
            CroppedSize = side,
        };
        canvas.Dispose();
        return result;
    }

    /// <summary>Composites one transparent, padded square frame of the requested size.</summary>
    public static Bitmap Frame(Mark mark, int size, double padding)
    {
        var pad = Math.Max(1, (int)Math.Round(size * padding, MidpointRounding.AwayFromZero));
        var inner = Math.Max(1, size - 2 * pad);

        // Halve until the source is within 2x the target, so the final bicubic pass
        // never has to average hundreds of source pixels per destination pixel.
        var current = mark.Image;
        var owned = false;
        while (current.Width / 2 >= inner * 2)
        {
            var half = new Bitmap(Math.Max(1, current.Width / 2), Math.Max(1, current.Height / 2), PixelFormat.Format32bppArgb);
            using (var hg = Graphics.FromImage(half))
            {
                hg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                hg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                hg.CompositingMode = CompositingMode.SourceCopy;
                hg.DrawImage(current, new Rectangle(0, 0, half.Width, half.Height));
            }
            if (owned) { current.Dispose(); }
            current = half;
            owned = true;
        }

        var frame = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(current, new Rectangle(pad, pad, inner, inner));
        }
        if (owned) { current.Dispose(); }
        return frame;
    }

    /// <summary>32bpp BMP frame: BITMAPINFOHEADER (doubled height), bottom-up BGRA rows, then the 1bpp AND mask.</summary>
    public static byte[] BmpFrame(Bitmap bmp)
    {
        var size = bmp.Width;
        var maskRow = (size + 31) / 32 * 4;
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write((uint)40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)0);
        writer.Write((uint)(size * size * 4 + maskRow * size));
        writer.Write(0);
        writer.Write(0);
        writer.Write((uint)0);
        writer.Write((uint)0);

        var rect = new Rectangle(0, 0, size, size);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bytes = new byte[Math.Abs(stride) * size];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var y = size - 1; y >= 0; y--)
            {
                writer.Write(bytes, y * stride, size * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        writer.Write(new byte[maskRow * size]);
        writer.Flush();
        writer.Dispose();
        var frame = stream.ToArray();
        stream.Dispose();
        return frame;
    }

    /// <summary>PNG frame bytes, for the sizes where the shell expects compression.</summary>
    public static byte[] PngFrame(Bitmap bmp)
    {
        var stream = new MemoryStream();
        bmp.Save(stream, ImageFormat.Png);
        var frame = stream.ToArray();
        stream.Dispose();
        return frame;
    }

    /// <summary>Share of the frame actually covered by the mark, for a quick sanity check on padding.</summary>
    public static double Coverage(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            long opaque = 0;
            for (var i = 3; i < bytes.Length; i += 4)
            {
                if (bytes[i] > 8) { opaque++; }
            }
            return (double)opaque / (bmp.Width * bmp.Height);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
'@

$mark = [IconForge]::Load((Resolve-Path $Source).Path, 8)
Write-Host ("Mark: {0}x{1}, opaque square {2}px, average #{3:X2}{4:X2}{5:X2}" -f `
    $mark.SourceWidth, $mark.SourceHeight, $mark.CroppedSize, $mark.Average.R, $mark.Average.G, $mark.Average.B)

# Small frames as BMP, large ones as PNG. 32px must stay BMP for Inno Setup.
$smallSizes = 16, 20, 24, 32, 40, 48
$largeSizes = 64, 96, 128, 256
$sizes = @($smallSizes) + @($largeSizes)

$frames = @()
foreach ($size in $sizes) {
    $frame = [IconForge]::Frame($mark, $size, $Padding)
    $coverage = [IconForge]::Coverage($frame)
    $bytes = if ($size -le 48) { [IconForge]::BmpFrame($frame) } else { [IconForge]::PngFrame($frame) }
    $frames += , $bytes
    Write-Host ("  {0,3}x{0,-3} {1,-4} {2,7} bytes  coverage {3:P1}" -f `
        $size, $(if ($size -le 48) { 'bmp' } else { 'png' }), $bytes.Length, $coverage)
    $frame.Dispose()
}

New-Item -ItemType Directory -Force $AssetDir | Out-Null

$icoPath = Join-Path $AssetDir 'optima.ico'
$stream = [System.IO.File]::Create($icoPath)
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
    $writer.Flush()
    # Written straight to the file stream: BinaryWriter.Write would resolve the
    # byte[] to a scalar overload and emit a single byte per frame.
    foreach ($frame in $frames) {
        $stream.Write($frame, 0, $frame.Length)
    }
    $stream.Flush()
}
finally {
    $stream.Dispose()
}

# The splash window and the Discord rich-presence card share this square mark.
$presencePath = Join-Path $AssetDir 'optima-presence.png'
$presence = [IconForge]::Frame($mark, 512, $Padding)
try {
    $presence.Save($presencePath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $presence.Dispose()
}
$mark.Image.Dispose()

Write-Host ("Wrote {0} ({1} bytes, sizes: {2})" -f $icoPath, (Get-Item $icoPath).Length, ($sizes -join ', '))
Write-Host ("Wrote {0} ({1} bytes, 512x512)" -f $presencePath, (Get-Item $presencePath).Length)
