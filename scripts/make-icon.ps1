# Builds the DSH Desktop icon.
#
# Source art: assets\icon-source.png (the whale-girl illustration). The script
# crops it to the tile, rounds the corners onto transparency, and writes a
# multi-size .ico (PNG entry for 128 and 256, 32-bit DIB entries below, the same
# layout Windows itself produces). With no source art it draws a vector fallback
# so the build never fails.

[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$Output = '',
    [int]$CornerRadiusPercent = 24,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'assets\icon-source.png' }
if (-not $Output) { $Output = Join-Path $root 'assets\icon.ico' }
$Source = [System.IO.Path]::GetFullPath($Source)
$Output = [System.IO.Path]::GetFullPath($Output)
$preview = Join-Path (Split-Path $Output) 'icon.png'

if ((Test-Path $Output) -and -not $Force) {
    Write-Host "icon: $Output already exists (use -Force to regenerate)"
    exit 0
}

# ---------------------------------------------------------------- ICO writer
if (-not ('IcoWriter' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IcoWriter
{
    /// <summary>Writes an ICO containing one entry per size: PNG at 128+, DIB below.</summary>
    public static void Write(string output, Bitmap source, int[] sizes)
    {
        List<byte[]> payloads = new List<byte[]>();
        List<int> dimensions = new List<int>();
        foreach (int size in sizes)
        {
            using (Bitmap scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, size, size));
                }
                payloads.Add(size >= 128 ? EncodePng(scaled) : EncodeDib(scaled));
                dimensions.Add(size);
            }
        }

        using (FileStream stream = File.Create(output))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)payloads.Count);
            int offset = 6 + 16 * payloads.Count;
            for (int i = 0; i < payloads.Count; i++)
            {
                int size = dimensions[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)payloads[i].Length);
                writer.Write((uint)offset);
                offset += payloads[i].Length;
            }
            foreach (byte[] payload in payloads)
            {
                writer.Write(payload);
            }
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using (MemoryStream memory = new MemoryStream())
        {
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
    }

    private static byte[] EncodeDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        using (MemoryStream memory = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(memory))
        {
            writer.Write(40);
            writer.Write(width);
            writer.Write(height * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(width * height * 4);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[Math.Abs(data.Stride)];
                for (int y = height - 1; y >= 0; y--)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    writer.Write(row);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            int maskStride = ((width + 31) / 32) * 4;
            writer.Write(new byte[maskStride * height]);
            writer.Flush();
            return memory.ToArray();
        }
    }

    /// <summary>Rounds the corners of a square bitmap onto transparency.</summary>
    public static Bitmap RoundedCorners(Bitmap square, int radiusPercent)
    {
        int size = square.Width;
        int radius = Math.Max(2, size * radiusPercent / 100);
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        using (GraphicsPath path = new GraphicsPath())
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(size - radius - 1, 0, radius, radius, 270, 90);
            path.AddArc(size - radius - 1, size - radius - 1, radius, radius, 0, 90);
            path.AddArc(0, size - radius - 1, radius, radius, 90, 90);
            path.CloseFigure();
            graphics.SetClip(path);
            graphics.DrawImage(square, new Rectangle(0, 0, size, size));
            graphics.ResetClip();
        }
        return result;
    }

    /// <summary>Largest centred square of pixels that are not near-white background.</summary>
    public static Rectangle ContentBounds(Bitmap bitmap, int threshold)
    {
        int minX = bitmap.Width;
        int minY = bitmap.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.A < 16)
                {
                    continue;
                }
                if (color.R > threshold && color.G > threshold && color.B > threshold)
                {
                    continue;
                }
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0 || maxY < 0)
        {
            return new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        }
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        int side = Math.Min(Math.Max(width, height), Math.Min(bitmap.Width, bitmap.Height));
        int centreX = minX + width / 2;
        int centreY = minY + height / 2;
        int left = Math.Max(0, Math.Min(bitmap.Width - side, centreX - side / 2));
        int top = Math.Max(0, Math.Min(bitmap.Height - side, centreY - side / 2));
        return new Rectangle(left, top, side, side);
    }
}
'@
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$square = $null

if (Test-Path $Source) {
    $loaded = [System.Drawing.Image]::FromFile($Source)
    $flat = New-Object System.Drawing.Bitmap($loaded.Width, $loaded.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($flat)
    $graphics.DrawImage($loaded, 0, 0, $loaded.Width, $loaded.Height)
    $graphics.Dispose()
    $loaded.Dispose()

    $bounds = [IcoWriter]::ContentBounds($flat, 242)
    Write-Host "icon: source $($flat.Width)x$($flat.Height), tile $($bounds.Width)x$($bounds.Height) at ($($bounds.X),$($bounds.Y))"
    $square = New-Object System.Drawing.Bitmap(512, 512)
    $graphics = [System.Drawing.Graphics]::FromImage($square)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $target = New-Object System.Drawing.Rectangle(0, 0, 512, 512)
    $graphics.DrawImage($flat, $target, $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height, [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()
    $flat.Dispose()
} else {
    Write-Host "icon: no art at $Source, drawing the vector fallback"
    $square = New-Object System.Drawing.Bitmap(512, 512)
    $graphics = [System.Drawing.Graphics]::FromImage($square)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $plate = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 17, 24, 39))
    $graphics.FillRectangle($plate, 0, 0, 512, 512)
    $accent = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 34, 211, 166), 20)
    $accent.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawArc($accent, 80, 80, 352, 352, 200, 250)
    $font = New-Object System.Drawing.Font('Segoe UI', 120, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $text = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 236, 240, 246))
    $graphics.DrawString('DSH', $font, $text, (New-Object System.Drawing.RectangleF(0, 0, 512, 490)), $format)
    $graphics.Dispose()
}

$rounded = [IcoWriter]::RoundedCorners($square, $CornerRadiusPercent)
$square.Dispose()

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Output)) | Out-Null
[IcoWriter]::Write($Output, $rounded, $sizes)

$previewBitmap = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($previewBitmap)
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.DrawImage($rounded, (New-Object System.Drawing.Rectangle(0, 0, 256, 256)))
$graphics.Dispose()
$previewBitmap.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$previewBitmap.Dispose()
$rounded.Dispose()

$info = Get-Item $Output
Write-Host ("icon: wrote {0} ({1} bytes, sizes {2})" -f $info.FullName, $info.Length, ($sizes -join '/'))
Write-Host ("icon: preview {0}" -f $preview)
