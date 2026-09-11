# DSH Desktop build pipeline.
#
# Produces a self-contained application folder under app\ :
#   app\DSH Desktop.exe     launcher (C# 5, in-box compiler)
#   app\runtime\node.exe    bundled Node runtime
#   app\runtime\app\        bundled @deepseek-ai/dsh production dependencies
#   app\webview2\           Microsoft WebView2 SDK assemblies
#   app\plugins\            launcher plugins
#   app\config.json         user settings
#
# Steps are idempotent: existing payloads are reused unless -Force is passed.

[CmdletBinding()]
param(
    [string]$DshVersion = '0.1.5-rc.2',
    [string]$NodeExe = '',
    [switch]$SkipRuntime,
    [switch]$SkipWebView2,
    [switch]$Force,
    [switch]$ForceConfig,
    [switch]$SkipIcon,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'app'
$runtimeDir = Join-Path $app 'runtime'
$runtimeApp = Join-Path $runtimeDir 'app'
$webviewDir = Join-Path $app 'webview2'
$assetsDir = Join-Path $root 'assets'
$iconPath = Join-Path $assetsDir 'icon.ico'
$npmCache = Join-Path $root '.cache\npm'
$exePath = Join-Path $app 'DSH Desktop.exe'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor DarkGray }
function Get-DirSizeMb([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    $sum = (Get-ChildItem $Path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB, 1)
}

$required = @($app, $runtimeDir, $runtimeApp, $webviewDir, $assetsDir,
    (Join-Path $app 'plugins'), (Join-Path $app 'data'), (Join-Path $app 'logs'), $npmCache,
    (Join-Path $root 'tests\artifacts'))
foreach ($dir in $required) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

Write-Step 'DSH Desktop 构建开始'
Write-Ok "项目根目录: $root"
Write-Ok "目标应用目录: $app"

# ---------------------------------------------------------------- icon
if (-not $SkipIcon) {
    Write-Step '生成图标'
    & (Join-Path $root 'scripts\make-icon.ps1') -Output $iconPath -Force:(($Force) -or (-not (Test-Path $iconPath))) | Out-Null
    Write-Ok "icon: $iconPath"
}

# ---------------------------------------------------------------- WebView2
if (-not $SkipWebView2) {
    Write-Step '准备 WebView2 SDK'
    $webviewArgs = @((Join-Path $root 'scripts\fetch-webview2.mjs'), '--target', $webviewDir)
    if ($Force) { $webviewArgs += '--force' }
    & node @webviewArgs
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 SDK 下载失败' }
    # The launcher links these three assemblies and the .NET Framework loader
    # probes the application directory: keep flattened copies beside the exe.
    foreach ($dll in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
        Copy-Item (Join-Path $webviewDir $dll) $app -Force
    }
    Write-Ok 'WebView2 SDK 已同时放置在 exe 同级目录'
} else {
    Write-Step '跳过 WebView2 SDK（-SkipWebView2）'
}

# ---------------------------------------------------------------- Node runtime
Write-Step '准备 Node 运行时'
$bundledNode = Join-Path $runtimeDir 'node.exe'
if ($Force -or -not (Test-Path $bundledNode)) {
    $source = $NodeExe
    if (-not $source) {
        $command = Get-Command node -ErrorAction SilentlyContinue
        if ($command) { $source = $command.Source }
    }
    if (-not $source -or -not (Test-Path $source)) { throw '未找到 node.exe，请用 -NodeExe 指定路径' }
    Copy-Item $source $bundledNode -Force
    Write-Ok "node: $source -> $bundledNode"
} else {
    Write-Ok 'node.exe 已存在（-Force 可覆盖）'
}
$nodeVersion = (& $bundledNode --version) 2>&1
Write-Ok "node 版本: $nodeVersion"

# ---------------------------------------------------------------- DSH runtime
if (-not $SkipRuntime) {
    Write-Step "准备 DSH 运行时 @deepseek-ai/dsh@$DshVersion"
    $installed = Join-Path $runtimeApp 'node_modules\@deepseek-ai\dsh\package.json'
    $needsInstall = $Force -or -not (Test-Path $installed)
    if (-not $needsInstall) {
        $current = (Get-Content $installed -Raw | ConvertFrom-Json).version
        if ($current -ne $DshVersion) {
            Write-Ok "已安装 $current，需要切换到 $DshVersion"
            $needsInstall = $true
        } else {
            Write-Ok "已安装 $current"
        }
    }
    if ($needsInstall) {
        $packageJson = Join-Path $runtimeApp 'package.json'
        if (-not (Test-Path $packageJson)) {
            '{ "name": "dsh-desktop-runtime", "private": true, "version": "0.0.0" }' | Set-Content -Path $packageJson -Encoding UTF8
        }
        $npmArgs = @('install', "@deepseek-ai/dsh@$DshVersion", '--prefix', $runtimeApp,
            '--omit=dev', '--ignore-scripts', '--no-audit', '--no-fund', '--loglevel=error', '--cache', $npmCache)
        Write-Ok "npm $($npmArgs -join ' ')"
        & npm @npmArgs
        if ($LASTEXITCODE -ne 0) { throw 'npm install 失败' }
    }
    $entry = Join-Path $runtimeApp 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    if (-not (Test-Path $entry)) { throw "DSH 入口文件缺失: $entry" }
    Write-Ok "DSH 入口: $entry"
} else {
    Write-Step '跳过 DSH 运行时（-SkipRuntime）'
}

# ---------------------------------------------------------------- config, plugins, docs
Write-Step '同步配置、插件与文档'
if ($ForceConfig -or -not (Test-Path (Join-Path $app 'config.json'))) {
    Copy-Item (Join-Path $root 'config.json') (Join-Path $app 'config.json') -Force
    Write-Ok 'config.json'
} else {
    Write-Ok 'config.json 已存在，保留用户设置'
}
$pluginSource = Join-Path $root 'plugins'
$pluginTarget = Join-Path $app 'plugins'
# app\plugins is build output: it always mirrors plugins\ in the project. Edit the
# project copy, never the built one.
Get-ChildItem $pluginSource -Directory | ForEach-Object {
    $target = Join-Path $pluginTarget $_.Name
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item $_.FullName $target -Recurse -Force
    Write-Ok "plugin: $($_.Name)"
}
Copy-Item (Join-Path $pluginSource 'README.md') $pluginTarget -Force
Copy-Item (Join-Path $root 'README.md') $app -Force
Copy-Item (Join-Path $root 'docs\PLUGIN-GUIDE.zh.md') $app -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- compile launcher
Write-Step '编译启动器'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '未找到 C# 编译器 csc.exe' }

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Runtime.Serialization.dll',
    (Join-Path $webviewDir 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $webviewDir 'Microsoft.Web.WebView2.WinForms.dll')
)
$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/langversion:5', '/optimize+',
    "/out:$exePath",
    "/win32manifest:$(Join-Path $root 'src\app.manifest')"
)
foreach ($reference in $references) { $cscArgs += "/reference:$reference" }
if (Test-Path $iconPath) { $cscArgs += "/win32icon:$iconPath" }
$cscArgs += (Join-Path $root 'src\DesktopSupport.cs')
$cscArgs += (Join-Path $root 'src\DshDesktop.cs')

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw '启动器编译失败' }
if (-not (Test-Path $exePath)) { throw "未生成 $exePath" }
Write-Ok "exe: $exePath"

# ---------------------------------------------------------------- summary
Write-Step '构建结果'
$rows = @(
    [pscustomobject]@{ 组件 = 'DSH Desktop.exe'; 路径 = $exePath; MB = [math]::Round((Get-Item $exePath).Length / 1MB, 2) },
    [pscustomobject]@{ 组件 = 'node.exe'; 路径 = $bundledNode; MB = [math]::Round((Get-Item $bundledNode).Length / 1MB, 1) },
    [pscustomobject]@{ 组件 = 'DSH 运行时'; 路径 = (Join-Path $runtimeApp 'node_modules'); MB = Get-DirSizeMb (Join-Path $runtimeApp 'node_modules') },
    [pscustomobject]@{ 组件 = 'WebView2 SDK'; 路径 = $webviewDir; MB = Get-DirSizeMb $webviewDir },
    [pscustomobject]@{ 组件 = '应用总计'; 路径 = $app; MB = Get-DirSizeMb $app }
)
$rows | Format-Table -AutoSize
Write-Host '构建完成：双击 app\DSH Desktop.exe 即可启动。' -ForegroundColor Green
