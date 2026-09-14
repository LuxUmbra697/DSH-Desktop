# DSH Desktop end-to-end smoke test.
#
# Starts the built launcher the way a user would (double-click equivalent),
# proves the bundled server answers, proves the app rendered inside its own
# WebView2 window, captures a screenshot as evidence, then proves closing the
# window releases the port and leaves no orphaned node or WebView2 process.
#
# Usage: pwsh -File tests\smoke.ps1 [-KeepRunning] [-TimeoutSeconds 240] [-Exe <path>]

[CmdletBinding()]
param(
    [switch]$KeepRunning,
    [int]$TimeoutSeconds = 240,
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'app\DSH Desktop.exe' }
$logFile = Join-Path $root 'app\logs\dsh-desktop.log'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

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

function Wait-ForLogMatch([string]$Pattern, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $logFile) {
            $match = Select-String -Path $logFile -Pattern $Pattern -ErrorAction SilentlyContinue | Select-Object -Last 1
            if ($match) { return $match }
        }
        Start-Sleep -Milliseconds 600
    }
    return $null
}

if (-not ('AppWindows' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class AppWindows {
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr hWnd, IntPtr param);

    /// <summary>Widest visible top-level window of the process, as "width|handle|title".</summary>
    public static string Widest(uint target, string titlePattern) {
        List<string> found = new List<string>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr param) {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != target || !IsWindowVisible(hWnd)) return true;
            RECT rect;
            GetWindowRect(hWnd, out rect);
            StringBuilder title = new StringBuilder(512);
            GetWindowTextW(hWnd, title, 512);
            string text = title.ToString();
            if (titlePattern.Length > 0 && text.IndexOf(titlePattern, StringComparison.OrdinalIgnoreCase) < 0) return true;
            found.Add((rect.Right - rect.Left) + "|" + hWnd.ToInt64() + "|" + text);
            return true;
        }, IntPtr.Zero);
        string best = "";
        int bestWidth = -1;
        foreach (string entry in found) {
            int width = int.Parse(entry.Split('|')[0]);
            if (width > bestWidth) { bestWidth = width; best = entry; }
        }
        return best;
    }
}
'@
}

function Get-AppWindow([int]$ProcessId, [string]$TitlePattern = 'DSH Desktop') {
    $entry = [AppWindows]::Widest([uint32]$ProcessId, $TitlePattern)
    if (-not $entry) { return $null }
    $parts = $entry -split '\|'
    return [pscustomobject]@{ Width = [int]$parts[0]; Handle = [IntPtr][int64]$parts[1]; Title = $parts[2] }
}

function Test-PortOpen([int]$Port) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        $open = $task.Wait(1500) -and $client.Connected
        $client.Close()
        return $open
    } catch {
        return $false
    }
}

function Get-Count([string]$Name) {
    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue).Count
}

Write-Host '== DSH Desktop 端到端自测 ==' -ForegroundColor Cyan
Write-Host "   exe: $Exe"

if (-not (Test-Path $Exe)) { throw "未找到可执行文件: $Exe（先运行 build.ps1）" }

# Preconditions ------------------------------------------------------------
$existing = Get-Process -Name 'DSH Desktop' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '   关闭已在运行的 DSH Desktop 实例' -ForegroundColor DarkGray
    foreach ($p in $existing) { $null = $p.CloseMainWindow() }
    Start-Sleep -Seconds 3
}
if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
# Start from the configured size every run: a remembered window would make the
# layout assertions depend on whatever the last session left behind.
$windowState = Join-Path $root 'app\data\window.json'
if (Test-Path $windowState) { Remove-Item $windowState -Force }

$nodeBefore = Get-Count 'node'
$webviewBefore = Get-Count 'msedgewebview2'
$msedgeBefore = Get-Count 'msedge'
Write-Host "   基线: node=$nodeBefore msedgewebview2=$webviewBefore msedge=$msedgeBefore" -ForegroundColor DarkGray

# Launch ------------------------------------------------------------------
Write-Host '-> 启动启动器（等同双击）' -ForegroundColor Cyan
$launcher = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
Assert-True '启动器进程已创建' ($null -ne $launcher) ("pid=" + $launcher.Id)

$windowReady = $false
$window = $null
for ($i = 0; $i -lt 40; $i++) {
    $window = Get-AppWindow $launcher.Id
    if ($window -and $window.Width -ge 400) { $windowReady = $true; break }
    if ($launcher.HasExited) { break }
    Start-Sleep -Milliseconds 500
}
$handle = 'n/a'
if ($window) { $handle = $window.Handle }
Assert-True '启动器独立窗口已出现' $windowReady ("handle=" + $handle)

# Serving URL -------------------------------------------------------------
Write-Host '-> 等待 DSH 服务就绪' -ForegroundColor Cyan
$serving = Wait-ForLogMatch 'serving url: (\S+)' $TimeoutSeconds
$url = $null
if ($serving) { $url = $serving.Matches[0].Groups[1].Value }
$detailUrl = 'log 中没有 serving url'
if ($url) { $detailUrl = $url }
Assert-True '服务在超时前就绪' ($null -ne $url) $detailUrl

$port = 0
$serverPid = 0
if ($url) {
    Assert-True '访问地址带 token' ($url -match '\?token=') $url.Substring(0, [Math]::Min(64, $url.Length))
    $port = [int]([uri]$url).Port

    Write-Host '-> 校验 HTTP 表面' -ForegroundColor Cyan
    $httpOutput = & node (Join-Path $PSScriptRoot 'http-check.mjs') --url $url 2>&1
    $httpExit = $LASTEXITCODE
    foreach ($line in $httpOutput) { Write-Host "    $line" }
    Assert-True 'HTTP 表面全部通过' ($httpExit -eq 0) "exit=$httpExit"

    $spawn = Select-String -Path $logFile -Pattern 'spawn pid=(\d+)' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($spawn) { $serverPid = [int]$spawn.Matches[0].Groups[1].Value }
    Assert-True '捆绑运行时的 node 子进程已记录' ($serverPid -gt 0) ("pid=" + $serverPid)
    if ($serverPid -gt 0) {
        $alive = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
        Assert-True 'node 子进程正在运行' ($null -ne $alive) ''
    }
}

# Window content ----------------------------------------------------------
Write-Host '-> 检查 DSH 宿主插件是否真的挂载' -ForegroundColor Cyan
$pluginLog = Wait-ForLogMatch '\[myapp-tools\] applied' 30
$pluginDetail = 'plugin boot marker missing'
if ($pluginLog) { $pluginDetail = 'myapp_lookup_ticket + myapp_ticket_playbook' }
Assert-True 'DSH 宿主插件已加载并注册工具' ($null -ne $pluginLog) $pluginDetail

Write-Host '-> 等待独立窗口内渲染完成' -ForegroundColor Cyan
$nav = Wait-ForLogMatch 'webview2 ready:' 120
$navDetail = 'no readiness line'
if ($nav) { $navDetail = 'navigation completed' }
Assert-True 'WebView2 已加载 DSH 界面' ($null -ne $nav) $navDetail

# The page must own the whole area between the menu and status bars: any gap here
# is what makes the DSH interface look cut off.
$layout = Wait-ForLogMatch 'layout: client=(\d+)x(\d+) webview=(\d+)x(\d+) at (-?\d+),(-?\d+) host=(\d+)x(\d+) at (-?\d+),(-?\d+) menu=(\d+) status=(\d+)' 30
if ($layout) {
    $clientW = [int]$layout.Matches[0].Groups[1].Value
    $clientH = [int]$layout.Matches[0].Groups[2].Value
    $viewW = [int]$layout.Matches[0].Groups[3].Value
    $viewH = [int]$layout.Matches[0].Groups[4].Value
    $viewX = [int]$layout.Matches[0].Groups[5].Value
    $viewY = [int]$layout.Matches[0].Groups[6].Value
    $hostW = [int]$layout.Matches[0].Groups[7].Value
    $hostH = [int]$layout.Matches[0].Groups[8].Value
    $hostX = [int]$layout.Matches[0].Groups[9].Value
    $hostY = [int]$layout.Matches[0].Groups[10].Value
    $menuH = [int]$layout.Matches[0].Groups[11].Value
    $statusH = [int]$layout.Matches[0].Groups[12].Value
    Assert-True '窗口布局：页面占满可用宽度' ($hostW -eq $clientW -and $hostX -eq 0) "client=$clientW host=$hostW x=$hostX"
    Assert-True '窗口布局：页面高度 = 客户区 − 菜单 − 状态栏' ($hostH -eq ($clientH - $menuH - $statusH)) "client=$clientH menu=$menuH status=$statusH host=$hostH"
    Assert-True '窗口布局：页面紧贴菜单栏下方，不被菜单覆盖' ($hostY -eq $menuH) "y=$hostY menu=$menuH"
    Assert-True '窗口布局：WebView 填满页面区域' ($viewW -eq $hostW -and $viewH -eq $hostH -and $viewX -eq 0 -and $viewY -eq 0) "host=$hostW`x$hostH view=$viewW`x$viewH at $viewX,$viewY"
} else {
    Assert-True '窗口布局标记' $false 'no layout line in log'
}

$webviewNow = Get-Count 'msedgewebview2'
Assert-True 'WebView2 渲染进程已启动' ($webviewNow -gt $webviewBefore) ("before=$webviewBefore now=$webviewNow")
$msedgeNow = Get-Count 'msedge'
Assert-True '未打开系统 Edge 浏览器窗口' ($msedgeNow -le $msedgeBefore) ("before=$msedgeBefore now=$msedgeNow")

# Screenshot --------------------------------------------------------------
Write-Host '-> 截屏取证' -ForegroundColor Cyan
$shot = Join-Path $artifacts 'window.png'
$captured = $false
try {
    if (-not ('WindowCapture' -as [type])) {
        Add-Type -AssemblyName System.Drawing
        Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class WindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    public static string Capture(IntPtr hWnd, string path) {
        RECT rect;
        if (!GetWindowRect(hWnd, out rect)) return "GetWindowRect failed";
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 100 || height < 100) return "unexpected window size " + width + "x" + height;
        using (Bitmap bitmap = new Bitmap(width, height))
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
            IntPtr hdc = graphics.GetHdc();
            // PW_RENDERFULLCONTENT renders DirectComposition surfaces such as WebView2.
            bool printed = PrintWindow(hWnd, hdc, 2);
            graphics.ReleaseHdc(hdc);
            if (!printed) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            bitmap.Save(path, ImageFormat.Png);
            return (printed ? "PrintWindow " : "CopyFromScreen ") + width + "x" + height + " @ (" + rect.Left + "," + rect.Top + ")";
        }
    }
}
'@
    }
    $proc = Get-Process -Id $launcher.Id -ErrorAction SilentlyContinue
    if (-not $proc) { throw '启动器进程已不存在' }
    # MainWindowHandle can point at a small auxiliary window, so the widest visible
    # window of the process is the one that actually shows the page.
    $window = Get-AppWindow $launcher.Id
    if (-not $window) { throw '找不到可见窗口' }
    [WindowCapture]::SetForegroundWindow($window.Handle) | Out-Null
    Start-Sleep -Milliseconds 1500
    if (Test-Path $shot) { Remove-Item $shot -Force }
    $method = [WindowCapture]::Capture($window.Handle, $shot)
    $captured = (Test-Path $shot)
    Write-Host ("    " + $method + " — " + $window.Title) -ForegroundColor DarkGray
} catch {
    Write-Host ("    截屏跳过: " + $_.Exception.Message) -ForegroundColor DarkYellow
}
Assert-True '窗口截图已保存' $captured $shot

# Teardown ----------------------------------------------------------------
if ($KeepRunning) {
    Write-Host '-> -KeepRunning：保留运行中的窗口，跳过关闭校验' -ForegroundColor Yellow
} else {
    Write-Host '-> 关闭窗口并检查资源回收' -ForegroundColor Cyan
    $closed = $false
    try { $closed = $launcher.CloseMainWindow() } catch { $closed = $false }
    if (-not $closed) { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue }
    if (-not $launcher.WaitForExit(25000)) { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 5

    Assert-True '启动器进程已退出' ($launcher.HasExited) ''
    if ($serverPid -gt 0) {
        $left = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
        Assert-True 'node 子进程已回收' ($null -eq $left) ("pid=" + $serverPid)
    }
    if ($port -gt 0) {
        Assert-True '监听端口已释放' (-not (Test-PortOpen $port)) ("port=" + $port)
    }
    $webviewLeft = Get-Count 'msedgewebview2'
    Assert-True 'WebView2 进程已回收' ($webviewLeft -le $webviewBefore) ("before=$webviewBefore left=$webviewLeft")

    Assert-True '日志文件已写入' (Test-Path $logFile) $logFile
    Assert-True '窗口状态已持久化' (Test-Path (Join-Path $root 'app\data\window.json')) ''
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host ("== 自测通过：$script:checks 项全部成功 ==") -ForegroundColor Green
    exit 0
}
Write-Host ("== 自测失败：$($script:failures)/$script:checks 项未通过 ==") -ForegroundColor Red
exit 1
