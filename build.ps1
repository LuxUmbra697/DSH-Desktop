# DSH Desktop build pipeline.
#
# Produces a self-contained, portable application folder under app\ :
#   app\DSH Desktop.exe     launcher (C# 5, in-box compiler)
#   app\runtime\node.exe    bundled Node runtime
#   app\runtime\npm\        bundled npm, so DSH can be updated in place
#   app\runtime\app\        bundled @deepseek-ai/dsh production dependencies
#   app\webview2\           Microsoft WebView2 SDK assemblies
#   app\plugins\            launcher plugins
#   app\config.json         user settings
#
# Steps are idempotent: existing payloads are reused unless -Force is passed.

[CmdletBinding()]
param(
    [string]$DshVersion = '0.1.5-rc.2',
    [string]$AppVersion = '1.1.3',
    [string]$NodeExe = '',
    [switch]$SkipRuntime,
    [switch]$SkipWebView2,
    [switch]$SkipIcon,
    [switch]$SkipNpm,
    [switch]$NoPrune,
    [switch]$Force,
    [switch]$ForceConfig,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'app'
$runtimeDir = Join-Path $app 'runtime'
$runtimeApp = Join-Path $runtimeDir 'app'
$runtimeNpm = Join-Path $runtimeDir 'npm'
$webviewDir = Join-Path $app 'webview2'
$assetsDir = Join-Path $root 'assets'
$iconPath = Join-Path $assetsDir 'icon.ico'
$npmCache = Join-Path $root '.cache\npm'
$exePath = Join-Path $app 'DSH Desktop.exe'
$pruneScript = Join-Path $root 'scripts\prune-runtime.mjs'
$appScripts = Join-Path $app 'scripts'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor DarkGray }
function Get-DirSizeMb([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    $sum = (Get-ChildItem $Path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if (-not $sum) { return 0 }
    return [math]::Round($sum / 1MB, 1)
}

foreach ($dir in @($app, $runtimeDir, $runtimeApp, $webviewDir, $assetsDir, $appScripts,
        (Join-Path $app 'plugins'), (Join-Path $app 'data'), (Join-Path $app 'logs'), $npmCache,
        (Join-Path $root 'tests\artifacts'))) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

Write-Step 'DSH Desktop 构建开始'
Write-Ok "应用版本: $AppVersion / DSH 版本: $DshVersion"
Write-Ok "项目根目录: $root"
Write-Ok "目标应用目录: $app"

# ---------------------------------------------------------------- icon
if (-not $SkipIcon) {
    Write-Step '生成图标'
    $iconArgs = @{ Output = $iconPath }
    if ($Force -or -not (Test-Path $iconPath)) { $iconArgs['Force'] = $true }
    & (Join-Path $root 'scripts\make-icon.ps1') @iconArgs | ForEach-Object { Write-Ok $_ }
}

# ---------------------------------------------------------------- WebView2
if (-not $SkipWebView2) {
    Write-Step '准备 WebView2 SDK'
    $webviewArgs = @((Join-Path $root 'scripts\fetch-webview2.mjs'), '--target', $webviewDir)
    if ($Force) { $webviewArgs += '--force' }
    & node @webviewArgs
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 SDK 下载失败' }
} else {
    Write-Step '跳过 WebView2 SDK（-SkipWebView2）'
}
# The launcher links these three assemblies and the .NET Framework loader probes
# the application directory: keep flattened copies beside the exe.
foreach ($dll in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
    if (Test-Path (Join-Path $webviewDir $dll)) { Copy-Item (Join-Path $webviewDir $dll) $app -Force }
}
Write-Ok 'WebView2 SDK 已同时放置在 exe 同级目录'

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
Set-Content -Path (Join-Path $runtimeDir 'node-version.txt') -Value "$nodeVersion" -Encoding ASCII
Write-Ok "node 版本: $nodeVersion（已记录到 runtime\node-version.txt，供“重新下载内置 Node”使用）"

# ---------------------------------------------------------------- bundled npm
if (-not $SkipNpm) {
    Write-Step '打包内置 npm（让 DSH 可以就地更新）'
    $npmSource = Join-Path (Split-Path $bundledNode) 'node_modules\npm'
    if (-not (Test-Path $npmSource)) {
        $candidate = Get-Command npm -ErrorAction SilentlyContinue
        if ($candidate) {
            $npmSource = Join-Path (Split-Path $candidate.Source) 'node_modules\npm'
        }
    }
    if (Test-Path $npmSource) {
        if ($Force -or -not (Test-Path (Join-Path $runtimeNpm 'bin\npm-cli.js'))) {
            if (Test-Path $runtimeNpm) { Remove-Item -Recurse -Force $runtimeNpm }
            Copy-Item $npmSource $runtimeNpm -Recurse -Force
            Write-Ok "npm: $npmSource -> $runtimeNpm"
        } else {
            Write-Ok 'npm 已存在（-Force 可覆盖）'
        }
        if (-not $NoPrune) {
            & node $pruneScript --root $runtimeNpm 2>&1 | Out-Null
        }
        Write-Ok "npm 体积: $(Get-DirSizeMb $runtimeNpm) MB"
    } else {
        Write-Ok '未找到 npm 源目录，跳过（更新功能将依赖系统 npm）'
    }
} else {
    Write-Step '跳过内置 npm（-SkipNpm）'
}

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

# ---------------------------------------------------------------- prune
$runtimeModules = Join-Path $runtimeApp 'node_modules'
if (-not $NoPrune -and (Test-Path $runtimeModules)) {
    Write-Step '精简运行时依赖（源映射、类型声明、调试符号、其它平台预编译）'
    $reportJson = & node $pruneScript --root $runtimeModules 2>&1 | Out-String
    $report = $reportJson | ConvertFrom-Json
    Write-Ok ("删除 {0} 项，释放 {1} MB" -f $report.deleted, $report.megabytes)
    Write-Ok ("运行时依赖体积: {0} MB" -f (Get-DirSizeMb $runtimeModules))
} elseif ($NoPrune) {
    Write-Step '跳过精简（-NoPrune）'
}
Copy-Item $pruneScript $appScripts -Force
Write-Ok '已放置 app\scripts\prune-runtime.mjs（更新后会再次精简）'

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
foreach ($doc in @('docs\PLUGIN-GUIDE.zh.md')) {
    if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $app -Force }
}

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
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll',
    (Join-Path $webviewDir 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $webviewDir 'Microsoft.Web.WebView2.WinForms.dll')
)
$sources = @('DesktopSupport.cs', 'RuntimeManager.cs', 'Updates.cs', 'Dialogs.cs', 'DshDesktop.cs') |
    ForEach-Object { Join-Path $root "src\$_" }

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/langversion:5', '/optimize+',
    "/out:$exePath",
    "/win32manifest:$(Join-Path $root 'src\app.manifest')"
)
foreach ($reference in $references) { $cscArgs += "/reference:$reference" }
if (Test-Path $iconPath) { $cscArgs += "/win32icon:$iconPath" }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw '启动器编译失败' }
if (-not (Test-Path $exePath)) { throw "未生成 $exePath" }
Write-Ok "exe: $exePath"

# ---------------------------------------------------------------- optional package
$zipPath = ''
if ($Package) {
    Write-Step '打包便携版 ZIP（只含可分发载荷，不含 data\）'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $distDir = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $zipPath = Join-Path $distDir "DSH-Desktop-$AppVersion-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $staging = Join-Path $env:TEMP ("dsh-desktop-package-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    foreach ($item in @('DSH Desktop.exe', 'config.json', 'README.md', 'PLUGIN-GUIDE.zh.md',
            'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebViewerLoader.dll')) {
        $source = Join-Path $app $item
        if (Test-Path $source) { Copy-Item $source $staging -Force }
    }
    if (Test-Path (Join-Path $app 'WebView2Loader.dll')) { Copy-Item (Join-Path $app 'WebView2Loader.dll') $staging -Force }
    foreach ($dir in @('runtime', 'webview2', 'plugins', 'scripts')) {
        $source = Join-Path $app $dir
        if (Test-Path $source) { Copy-Item $source (Join-Path $staging $dir) -Recurse -Force }
    }
    # data\ stays out; the runtime recreates it on first launch.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $staging -Recurse -Force
    Write-Ok ("便携包: {0} ({1} MB)" -f $zipPath, [math]::Round((Get-Item $zipPath).Length / 1MB, 1))
}

# ---------------------------------------------------------------- summary
Write-Step '构建结果'
$nodeSize = if (Test-Path $bundledNode) { [math]::Round((Get-Item $bundledNode).Length / 1MB, 1) } else { 0 }
$runtimeSize = Get-DirSizeMb $runtimeModules
$npmSize = Get-DirSizeMb $runtimeNpm
$webviewSize = Get-DirSizeMb $webviewDir
$exeSize = if (Test-Path $exePath) { [math]::Round((Get-Item $exePath).Length / 1MB, 2) } else { 0 }
$dataSize = Get-DirSizeMb (Join-Path $app 'data')
$payload = $nodeSize + $runtimeSize + $npmSize + $webviewSize + $exeSize

$rows = @(
    [pscustomobject]@{ 组件 = 'DSH Desktop.exe'; 路径 = $exePath; MB = $exeSize },
    [pscustomobject]@{ 组件 = 'node.exe'; 路径 = $bundledNode; MB = $nodeSize },
    [pscustomobject]@{ 组件 = 'DSH 运行时依赖'; 路径 = $runtimeModules; MB = $runtimeSize },
    [pscustomobject]@{ 组件 = '内置 npm'; 路径 = $runtimeNpm; MB = $npmSize },
    [pscustomobject]@{ 组件 = 'WebView2 SDK'; 路径 = $webviewDir; MB = $webviewSize },
    [pscustomobject]@{ 组件 = '可分发载荷合计'; 路径 = '(不含 data\)'; MB = [math]::Round($payload, 1) },
    [pscustomobject]@{ 组件 = '用户数据 data\'; 路径 = '(会话/缓存，不分发)'; MB = $dataSize }
)
if ($zipPath) { $rows += [pscustomobject]@{ 组件 = '便携 ZIP'; 路径 = $zipPath; MB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1) } }
$rows | Format-Table -AutoSize
Write-Host '构建完成：双击 app\DSH Desktop.exe 即可启动。' -ForegroundColor Green
Write-Host '提示：若本机已有合适的 Node，可在「文件 → 运行环境管理」里删除内置 Node，再省约 89 MB。' -ForegroundColor DarkGray

