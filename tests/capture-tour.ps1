# Captures the screenshot gallery used by the README.
#
# Each shot comes from a real launch of the built launcher: the panels are opened
# with the documented `--open <panel>` flag (a background process cannot reliably
# steal focus, so driving the menus from a script is not reproducible), and every
# window is captured with PrintWindow, which renders the WebView2 surface even
# when another window overlaps.
#
# Usage: pwsh -File tests\capture-tour.ps1

[CmdletBinding()]
param(
    [int]$ReadyTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'app\DSH Desktop.exe'
$logFile = Join-Path $root 'app\logs\dsh-desktop.log'
$configFile = Join-Path $root 'app\config.json'
$windowState = Join-Path $root 'app\data\window.json'
$images = Join-Path $root 'docs\images'
New-Item -ItemType Directory -Force -Path $images | Out-Null

$script:failures = 0
$script:checks = 0

function Assert-True([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $script:checks += 1
    $suffix = ''
    if ($Detail) { $suffix = "  ($Detail)" }
    if ($Condition) {
        Write-Host ("  PASS  " + $Name + $suffix) -ForegroundColor Green
    } else {
        $script:failures += 1
        Write-Host ("  FAIL  " + $Name + $suffix) -ForegroundColor Red
    }
}

if (-not ('WindowCapture' -as [type])) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class WindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    private delegate bool EnumProc(IntPtr hWnd, IntPtr param);

    /// <summary>Visible top-level windows of one process, with their sizes.</summary>
    public static List<string> ListWindows(uint target) {
        List<string> found = new List<string>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr param) {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == target && IsWindowVisible(hWnd)) {
                RECT rect;
                GetWindowRect(hWnd, out rect);
                StringBuilder title = new StringBuilder(512);
                GetWindowTextW(hWnd, title, 512);
                found.Add(hWnd.ToInt64() + "|" + title.ToString() + "|" + (rect.Right - rect.Left) + "|" + (rect.Bottom - rect.Top));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Capture(IntPtr hWnd, string path) {
        RECT rect;
        if (!GetWindowRect(hWnd, out rect)) return "GetWindowRect failed";
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 80 || height < 60) return "window too small " + width + "x" + height;
        using (Bitmap bitmap = new Bitmap(width, height))
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
            IntPtr hdc = graphics.GetHdc();
            bool printed = PrintWindow(hWnd, hdc, 2);
            graphics.ReleaseHdc(hdc);
            if (!printed) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            bitmap.Save(path, ImageFormat.Png);
            return (printed ? "PrintWindow " : "CopyFromScreen ") + width + "x" + height;
        }
    }
}
'@
}

function Get-ProcessWindows([int]$ProcessId) {
    $list = @()
    foreach ($entry in [WindowCapture]::ListWindows([uint32]$ProcessId)) {
        $parts = $entry -split '\|'
        $list += [pscustomobject]@{
            Handle = [IntPtr][int64]$parts[0]
            Name   = $parts[1]
            Width  = [int]$parts[2]
            Height = [int]$parts[3]
        }
    }
    return $list
}

function Get-ProcessWindowsUnused([int]$ProcessId) {
    return @()
}

function Wait-ForWindow([int]$ProcessId, [string]$TitlePattern, [int]$Seconds = 25, [int]$MinWidth = 200) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($window in Get-ProcessWindows $ProcessId) {
            if ($window.Name -like $TitlePattern -and $window.Handle -ne [IntPtr]::Zero -and $window.Width -ge $MinWidth) {
                return $window
            }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Stop-App($process) {
    if (-not $process) { return }
    try { $null = $process.CloseMainWindow() } catch { }
    if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

function Start-App([string]$Panel, [int]$Width = 0, [int]$Height = 0) {
    # CloseMainWindow cannot dismiss a window that is sitting in a modal loop, so
    # every capture run starts from a forced clean slate.
    Get-Process -Name 'DSH Desktop' -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    $leftover = @(Get-Process -Name 'DSH Desktop' -ErrorAction SilentlyContinue).Count
    if ($leftover -gt 0) { throw "仍有 $leftover 个 DSH Desktop 实例未退出，单实例互斥会让新实例直接退出" }
    if (Test-Path $windowState) { Remove-Item $windowState -Force }
    if ($Width -gt 0) {
        # The example plugin also sets a window size and plugins outrank config.json,
        # so the compact run reshapes the plugin manifest. Node does the edit: this
        # file is BOM-less UTF-8, which Windows PowerShell would mis-decode.
        $script = "const fs=require('fs');const p=process.argv[1];const j=JSON.parse(fs.readFileSync(p,'utf8'));j.width=" + $Width + ";j.height=" + $Height + ";fs.writeFileSync(p,JSON.stringify(j,null,2)+'\n');"
        & node -e $script (Join-Path $root 'app\plugins\example-branding\launcher.json')
        if ($LASTEXITCODE -ne 0) { throw '无法改写插件清单' }
    }
    $process = $null
    if ($Panel) {
        $process = Start-Process -FilePath $exe -ArgumentList @('--open', $Panel) -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    } else {
        $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    }
    Start-Sleep -Seconds 3
    if ($process.HasExited) {
        throw ("启动器在 3 秒内退出（退出码 {0}）：多为单实例互斥或运行环境问题，见 app\logs\dsh-desktop.log" -f $process.ExitCode)
    }
    return $process
}

function Capture-Window([IntPtr]$Handle, [string]$File) {
    $path = Join-Path $images $File
    [WindowCapture]::SetForegroundWindow($Handle) | Out-Null
    Start-Sleep -Milliseconds 1000
    $detail = [WindowCapture]::Capture($Handle, $path)
    return @{ Ok = (Test-Path $path); Detail = $detail }
}

Write-Host '== 截图采集 ==' -ForegroundColor Cyan
if (-not (Test-Path $exe)) { throw "未找到 $exe，先运行 build.ps1" }

# ---------------------------------------------------------------- 1. main window
Write-Host '-> 主窗口' -ForegroundColor Cyan
if (Test-Path $logFile) { Remove-Item $logFile -Force }
$app = Start-App ''
$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $logFile) -and (Select-String -Path $logFile -Pattern 'webview2 ready:' -ErrorAction SilentlyContinue)) { $ready = $true; break }
    if ($app.HasExited) { break }
    Start-Sleep -Milliseconds 700
}
Assert-True 'DSH 界面已加载' $ready ''
$main = Wait-ForWindow $app.Id 'DSH Desktop*' 20 400
if ($main) {
    $result = Capture-Window $main.Handle '01-main-window.png'
    Assert-True '主窗口截图' $result.Ok $result.Detail
} else {
    Assert-True '主窗口截图' $false 'window not found'
}
Stop-App $app

# ---------------------------------------------------------------- 2. compact window
Write-Host '-> 小窗口（响应式）' -ForegroundColor Cyan
$app = Start-App '' 900 620
$compact = Wait-ForWindow $app.Id 'DSH Desktop*' 60 300
if ($compact) {
    Start-Sleep -Seconds 6
    $result = Capture-Window $compact.Handle '02-compact-window.png'
    Assert-True '小窗口截图' $result.Ok ("$($result.Detail)，期望约 900x620")
} else {
    Assert-True '小窗口截图' $false 'window not found'
}
Stop-App $app
Copy-Item (Join-Path $root 'plugins\example-branding\launcher.json') (Join-Path $root 'app\plugins\example-branding\launcher.json') -Force

# ---------------------------------------------------------------- 3..6 panels
$panels = @(
    @{ Panel = 'runtime'; Title = '运行环境管理*'; File = '03-runtime-manager.png'; Caption = '运行环境管理截图'; Wait = 3 },
    @{ Panel = 'update'; Title = '检查更新*'; File = '04-update-check.png'; Caption = '检查更新截图'; Wait = 10 },
    @{ Panel = 'plugins'; Title = '已加载插件*'; File = '05-plugins.png'; Caption = '插件列表截图'; Wait = 2 },
    @{ Panel = 'diagnostics'; Title = '诊断信息*'; File = '06-diagnostics.png'; Caption = '诊断信息截图'; Wait = 3 }
)
foreach ($panel in $panels) {
    Write-Host ("-> " + $panel.Caption) -ForegroundColor Cyan
    $app = Start-App $panel.Panel
    $dialog = Wait-ForWindow $app.Id $panel.Title 60 300
    if ($dialog) {
        Start-Sleep -Seconds $panel.Wait
        $result = Capture-Window $dialog.Handle $panel.File
        Assert-True $panel.Caption $result.Ok $result.Detail
    } else {
        Assert-True $panel.Caption $false 'dialog not found'
    }
    Stop-App $app
}

Get-Process -Name 'node' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$root*" } |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host '生成的文件：' -ForegroundColor Cyan
Get-ChildItem $images -Filter '*.png' | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-28} {1} KB" -f $_.Name, [math]::Round($_.Length / 1KB, 1))
}

if ($script:failures -eq 0) {
    Write-Host ("== 截图采集完成：$script:checks 项全部成功 ==") -ForegroundColor Green
    exit 0
}
Write-Host ("== 截图采集有 $($script:failures)/$script:checks 项失败 ==") -ForegroundColor Red
exit 1
