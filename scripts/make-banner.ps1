# Composes the README hero banner from the generated art plus crisp overlay text.
#
# Usage: pwsh -File scripts\make-banner.ps1 [-Source <png>] [-Output <png>]
#
# The generated illustration carries no text (diffusion models render lettering
# unreliably); the wordmark is drawn here with GDI+ so it stays sharp.

[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'assets\banner-source.png' }
if (-not $Output) { $Output = Join-Path $root 'docs\images\banner.png' }
$Source = [System.IO.Path]::GetFullPath($Source)
$Output = [System.IO.Path]::GetFullPath($Output)

if (-not (Test-Path $Source)) {
    Write-Host "banner: no art at $Source, skipped"
    exit 0
}

$width = 1280
$height = 400
$image = [System.Drawing.Image]::FromFile($Source)

# Cover-crop the art into the banner rectangle.
$scale = [Math]::Max($width / $image.Width, $height / $image.Height)
$drawWidth = [int]($image.Width * $scale)
$drawHeight = [int]($image.Height * $scale)
$offsetX = [int](($width - $drawWidth) / 2)
$offsetY = [int](($height - $drawHeight) / 2)

$banner = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($banner)
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.DrawImage($image, (New-Object System.Drawing.Rectangle($offsetX, $offsetY, $drawWidth, $drawHeight)))
$image.Dispose()

# Darken the left half so the wordmark always has contrast.
$shade = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Rectangle(0, 0, $width, $height)),
    [System.Drawing.Color]::FromArgb(240, 12, 16, 24),
    [System.Drawing.Color]::FromArgb(0, 12, 16, 24),
    [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
$graphics.FillRectangle($shade, 0, 0, $width, $height)
$shade.Dispose()

# Bottom hairline in the product accent colour.
$accent = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 34, 211, 166), 4)
$graphics.DrawLine($accent, 0, $height - 2, $width, $height - 2)
$accent.Dispose()

$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$titleFont = New-Object System.Drawing.Font('Segoe UI', 54, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 20, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$tagFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$titleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 245, 250))
$subBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 150, 226, 205))
$tagBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 200, 210, 225))

$graphics.DrawString('DSH Desktop', $titleFont, $titleBrush, 56, 96)
$graphics.DrawString('把 DeepSeek Harness 装进一个独立窗口', $subFont, $subBrush, 60, 172)
$graphics.DrawString('双击即用 · 免终端 · 免浏览器 · 支持插件改造 · 体积可裁剪', $tagFont, $tagBrush, 60, 214)
$graphics.DrawString('Windows 10/11 · WebView2 · 约 210 MB 可分发载荷', $tagFont, $tagBrush, 60, 248)

$graphics.Dispose()

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Output)) | Out-Null
$banner.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$banner.Dispose()

$info = Get-Item $Output
Write-Host ("banner: wrote {0} ({1} KB, {2}x{3})" -f $info.FullName, [math]::Round($info.Length / 1KB, 1), $width, $height)
