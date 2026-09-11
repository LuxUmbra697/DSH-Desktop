# Generates assets/icon.ico for the launcher.
#
# Draws a 256x256 PNG and wraps it in a single-entry ICO container (PNG-in-ICO,
# supported since Windows Vista), so the build needs no icon editor and no
# external asset pipeline.

[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\assets\icon.ico'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outputPath = [System.IO.Path]::GetFullPath($Output)
if ((Test-Path $outputPath) -and -not $Force) {
    Write-Host "icon: $outputPath already exists (use -Force to regenerate)"
    exit 0
}

$size = 256
$bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

# Rounded dark plate.
$rect = New-Object System.Drawing.Rectangle(8, 8, ($size - 16), ($size - 16))
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$radius = 48
$path.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
$path.AddArc(($rect.Right - $radius), $rect.Y, $radius, $radius, 270, 90)
$path.AddArc(($rect.Right - $radius), ($rect.Bottom - $radius), $radius, $radius, 0, 90)
$path.AddArc($rect.X, ($rect.Bottom - $radius), $radius, $radius, 90, 90)
$path.CloseFigure()

$plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 17, 21, 30),
    [System.Drawing.Color]::FromArgb(255, 26, 34, 48),
    [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
$graphics.FillPath($plateBrush, $path)

# Accent arc.
$accentPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 34, 211, 166), 10)
$accentPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$accentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($accentPen, 40, 40, 176, 176, 200, 250)

# Wordmark.
$font = New-Object System.Drawing.Font('Segoe UI', 62, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 236, 240, 246))
$graphics.DrawString('DSH', $font, $textBrush, (New-Object System.Drawing.RectangleF(0, 0, $size, ($size - 12))), $format)

$graphics.Dispose()

# Encode the PNG payload.
$stream = New-Object System.IO.MemoryStream
$bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $stream.ToArray()
$stream.Dispose()
$bitmap.Dispose()

# ICONDIR + one ICONDIRENTRY + PNG payload.
$directoryPath = [System.IO.Path]::GetFullPath($outputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($directoryPath)) | Out-Null
$file = [System.IO.File]::Create($directoryPath)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]1)                 # image count
$writer.Write([Byte]0)                   # width 0 => 256
$writer.Write([Byte]0)                   # height 0 => 256
$writer.Write([Byte]0)                   # palette size
$writer.Write([Byte]0)                   # reserved
$writer.Write([UInt16]1)                 # color planes
$writer.Write([UInt16]32)                # bits per pixel
$writer.Write([UInt32]$png.Length)       # payload size
$writer.Write([UInt32]22)                # payload offset
$writer.Write($png)
$writer.Flush()
$writer.Dispose()
$file.Dispose()

Write-Host "icon: wrote $directoryPath ($($png.Length) bytes payload)"
