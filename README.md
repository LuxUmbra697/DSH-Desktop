<div align="center">

<img src="docs/images/banner.png" alt="DSH Desktop" width="100%" />

# DSH Desktop

**把 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 装进一个独立窗口：双击即用，不开终端，不开浏览器。**

[![Release](https://img.shields.io/github/v/release/LuxUmbra697/DSH-Desktop?style=flat-square&color=22d3a6)](https://github.com/LuxUmbra697/DSH-Desktop/releases)
[![Stars](https://img.shields.io/github/stars/LuxUmbra697/DSH-Desktop?style=flat-square&color=22d3a6)](https://github.com/LuxUmbra697/DSH-Desktop/stargazers)
[![License](https://img.shields.io/github/license/LuxUmbra697/DSH-Desktop?style=flat-square&color=22d3a6)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=flat-square)](#快速开始)
[![Payload](https://img.shields.io/badge/payload-210%20MB-22d3a6?style=flat-square)](#体积可以裁到多小)
[![DSH](https://img.shields.io/badge/DSH-0.1.5--rc.2-4d6bfe?style=flat-square)](https://github.com/deepseek-ai/deepseek-harness)

[简体中文](README.md) · [English](README.en.md) · [插件开发指南](docs/PLUGIN-GUIDE.zh.md)

</div>

---

## 界面预览

| 主窗口：独立页面，与浏览器无关 | 小窗口：900×620 下的响应式布局 |
| --- | --- |
| ![主窗口](docs/images/01-main-window.png) | ![小窗口](docs/images/02-compact-window.png) |
| 内置 Node 与 DSH 运行时启动 `dsh web`，WebView2 在**自己的窗口**里承载界面。窗口标题由示例插件改写为「DSH Desktop · 我的软件 AI 助手」，品牌色由插件覆盖 DSH 自己的主题变量得到，底部状态栏显示服务地址与缩放。 | 窗口最小 420×320，小屏自动最大化，状态栏在窄窗口下自动隐藏；DSH 页面自身会折叠侧栏。同一套界面在 125%/150%/200% 缩放下按显示器切换清晰度。 |

| 运行环境管理：本机有什么、能省多少 | 检查更新：跟官方 npx 的差别写在界面上 |
| --- | --- |
| ![运行环境管理](docs/images/03-runtime-manager.png) | ![检查更新](docs/images/04-update-check.png) |
| 一次列出内置 Node / 系统 Node / npm / DSH 运行时 / WebView2 运行时的来源与版本，并算出可分发载荷与实际占用。系统 Node 满足 DSH 要求时，可以直接删除内置 Node，省 89 MB；版本过低时按钮不可点，避免删完启动不了。 | 显示当前版本、npm `next`/`latest` 两个标签、发布时间与结论。点「更新并重启」会在原地把 `runtime\app` 里的 DSH 依赖换成新版本，然后自动精简并重启服务——插件、配置与会话都不受影响。 |

| 插件列表：两层插件一目了然 | 诊断信息：排障时先看这里 |
| --- | --- |
| ![插件](docs/images/05-plugins.png) | ![诊断](docs/images/06-diagnostics.png) |
| 启动器插件改窗口、品牌与注入；DSH 宿主插件通过 `--patch` 挂进 DSH 插件树。两个示例插件都在这张表里。 | 版本、安装目录、DSH_HOME、工作区、访问地址、插件数、日志路径、WebView2 状态，以及服务端输出尾部——出问题时把这张图发出来就能定位大半。 |

---

## 与同类项目的区别

同类项目 [anywhere-labs/dsh-desktop](https://github.com/anywhere-labs/dsh-desktop) 也是把 DSH 做成桌面端，值得尊重。两者路线不同，差异如下（数据来自各自仓库与 release，2026-09-11 核对）：

| 维度 | DSH Desktop（本项目） | anywhere-labs/dsh-desktop |
| --- | --- | --- |
| 外壳技术 | **WebView2 + 系统自带 .NET 编译器**，启动器本体 0.2 MB | Electron 44，安装包 128.7 MB |
| 分发形态 | 便携目录 / ZIP，解压即用，无安装器 | NSIS 安装器（`DSH-Desktop-2.0.9-x64-Setup.exe`） |
| 可分发载荷 | **210.9 MB**，裁掉内置 Node 后约 121 MB | 安装包含 Electron + Node + pnpm + 固定依赖 |
| 运行环境探测 | **会检测本机 Node / npm / WebView2**，版本合格即可删除内置副本 | 不探测系统 Node，自带运行时并自造 shim |
| DSH 就地更新 | **支持**：检查 npm 版本 → 原地更新 → 自动精简 → 重启 | 不支持上游就地更新，只能整体升级应用 |
| 构建依赖 | .NET Framework 4.8（Windows 自带）+ Node（拉依赖） | pnpm 全量仓库 + Electron 二进制 + 签名工具链 |
| 插件模型 | 启动器插件（窗口/品牌/注入）+ DSH 宿主插件（`--patch`），**零 npm 安装** | 插件市场 + 内置 pnpm 安装 |
| 自动化自测 | **18 项端到端断言 + 6 张截图自动采集**（`tests/`） | CI 跳过部分 smoke（见其 `ci.yml`） |

一句话：**它给你一个成品安装包，本项目给你一个可裁剪、可更新、可插件化的底座。**

## 主要功能

- **独立窗口**：内置 Node 运行时启动 `dsh web`，启动器解析带 token 的地址（`GET /?token=…` 换 303 + Cookie），用 WebView2 承载——全程不调用系统浏览器，也不需要你在终端敲 `npx @deepseek-ai/dsh web`。
- **零安装、可携带**：整个 `app\` 目录复制走就能用；`data\` 里是 `DSH_HOME`、工作区、WebView2 用户数据与窗口状态，删掉即恢复出厂。
- **体积可控**：构建时自动精简（源映射、类型声明、调试符号、其它平台预编译，共约 104 MB），运行时还能按需删除内置 Node / 内置 npm。
- **运行环境自检**：检测系统 Node（PATH / 注册表安装位置 / nvm 目录）、npm、WebView2 运行时；版本不合格只提醒、不允许删除内置副本。
- **就地更新 DSH**：查询 npm registry 的 `next`/`latest`，在界面里点一下就更新 `runtime\app` 并重启；可切换渠道，可选开机自动检查（`checkUpdatesOnStartup`）与自动更新（`autoUpdate`）。
- **两层插件**：`plugins\` 下一个目录即可改窗口标题/尺寸、注入 CSS/JS、加环境变量与命令行参数，或用 `dsh.patch.yml` 把自定义 DSH 宿主插件（工具、命令、Agent preset）挂进插件树。
- **进程树回收**：Windows Job Object + `taskkill /T`，关窗即回收 node 及其派生的 shell / 子代理，不留孤儿进程。
- **窗口体验**：PerMonitorV2 DPI、窗口尺寸位置记忆与多显示器校正、`Ctrl +/-/0` 缩放、F11 全屏、F12 开发者工具、Alt 快捷键面板。
- **失败降级**：WebView2 运行时缺失时自动改用 Edge 独立窗口模式，再不行才退回系统浏览器，并且始终把服务留在自己手里。

## 快速开始

**方式一：便携包（推荐）**

1. 从 [Releases](https://github.com/LuxUmbra697/DSH-Desktop/releases) 下载 `DSH-Desktop-<版本>-win-x64.zip` 并解压到任意目录。
2. 双击 `DSH Desktop.exe`。首次启动会在 `data\dsh-home` 建立运行环境（约 10–40 秒），窗口里出现 DSH 界面后即可使用。
3. 在 DSH 的「设置 → 模型」里配置模型凭据。

**方式二：从源码构建**

```powershell
git clone https://github.com/LuxUmbra697/DSH-Desktop.git
cd DSH-Desktop
.\build.ps1 -Package      # 图标 + WebView2 SDK + Node + npm + DSH 依赖 + 精简 + 编译 + 打 ZIP
```

构建只需要 Windows 10/11、自带的 .NET Framework 4.8（提供 `csc.exe`）和 Node.js（用于拉依赖）。

**运行要求**：Windows 10/11 x64；WebView2 运行时（Win11 与近年 Win10 预装，缺失时自动降级为 Edge 独立窗口）。

## 体积可以裁到多小

| 组成 | 精简前 | 精简后 | 说明 |
| --- | --- | --- | --- |
| DSH 运行时依赖 | 212.4 MB | **108.7 MB** | 删除源映射 35.8 MB、类型声明 33 MB、调试符号 19.8 MB、其它平台预编译 11.6 MB、开发目录 9.3 MB |
| Node 运行时 | 89.2 MB | 89.2 MB | 系统已有合格 Node 时可删除 |
| 内置 npm | 11.8 MB | 11.8 MB | 保证「更新 DSH」可用；系统有 npm 时可省 |
| WebView2 SDK | 1.0 MB | 1.0 MB | 必需：这是托管 SDK，不是系统运行时 |
| 启动器 | 0.2 MB | 0.2 MB | C# 5，系统编译器产物 |
| **合计** | **314.6 MB** | **210.9 MB** | 删除内置 Node 与 npm 后约 **110 MB** |

裁剪规则是「运行时不会读的文件才删」：`package.json`、README 与 i18n 元数据（DSH 会读包文档）、所有 `.js/.mjs/.cjs`、数据 `.json`、win32-x64 原生二进制、许可证全部保留。删除后有一套完整回归：精简后的运行时仍要能启动、加载插件、通过 18 项自测。

打成便携 ZIP 后是 **74.8 MB**（`build.ps1 -Package` 产出），解压即用、无需安装器。

## 运行环境管理

`文件 → 运行环境管理…`（快捷键 `Alt+R`）打开这张表，它做三件事：

1. **说清现状**：内置/系统 Node 的路径与版本、npm 位置、DSH 运行时版本、WebView2 运行时版本，以及各部分占用与「可分发载荷合计」。
2. **安全地省空间**：只有当系统 Node 存在**且满足 DSH 的引擎要求 `^22.19.0 || >=24.0.0`** 时，「删除内置 Node」才可点；版本过低或找不到系统 Node 时按钮置灰并写明原因——宁可不让删，也不让你删完打不开。
3. **可逆**：删掉的内置 Node 可以用「从系统 Node 复制」或「重新下载内置 Node」（从 nodejs.org 取构建时记录的版本）恢复，也可以重跑 `build.ps1`。

## DSH 会自动更新吗

官方 `npx @deepseek-ai/dsh@next web` 每次启动都会解析 npm 上的最新版本，**版本会悄悄变化**——这对想要稳定复现的产品是风险。本项目的策略是「默认固定、显式更新」：

| 行为 | 默认 | 配置项 |
| --- | --- | --- |
| 启动后静默检查新版本，发现后只在状态栏提示 | 开 | `checkUpdatesOnStartup` |
| 发现新版本后自动更新并重启 | 关 | `autoUpdate` |
| 更新渠道（`next` 含预发布 / `latest` 仅正式版） | `next` | `channel` |
| 手动检查与更新 | 随时 | `帮助 → 检查更新…`（`Alt+U`） |

更新过程用随包分发的 npm 在 `runtime\app` 里原地安装，随后自动精简依赖再重启服务；DSH 的会话记录、你的插件与 `config.json` 都不受影响。

## 插件系统

两层，都是「放进去、重启即生效」，不需要 npm install：

**1. 启动器插件**（`plugins\<id>\launcher.json`）：改窗口标题与尺寸、注入 CSS/JS、追加环境变量与命令行参数、指定 DSH patch 文件。示例 [example-branding](plugins/example-branding/launcher.json) 同时演示了标题、尺寸、品牌样式与注入脚本。

**2. DSH 宿主插件**（`plugins\<id>\dsh.patch.yml` + `dsh\*.mjs`）：用 DSH 原生的 patch 层机制插入插件行。示例 [example-tool](plugins/example-tool/) 注册了两个模型可见工具（`myapp_lookup_ticket`、`myapp_ticket_playbook`），并用 `dshLinkModules` 让插件直接 `import '@deepseek-ai/dsh-tools'`——启动器会把内置依赖树以目录联接挂进插件目录。

```jsonc
// plugins\my-plugin\launcher.json
{
  "name": "my-plugin",
  "title": "XXX 智能助手",          // 改窗口标题
  "width": 1360, "height": 880,      // 改窗口尺寸（优先于 config.json）
  "injectCss": ["inject/theme.css"], // 注入样式
  "injectJs": ["inject/shortcuts.js"],// 注入脚本
  "dshPatch": ["dsh.patch.yml"],     // 挂载 DSH 宿主插件
  "dshLinkModules": "dsh",           // 让插件能解析 @deepseek-ai/* 依赖
  "env": { "MY_MODE": "desktop" }    // 传给 dsh 进程的环境变量
}
```

完整字段、优先级、注入上下文与排障见 [插件开发指南](docs/PLUGIN-GUIDE.zh.md)；把 DSH 当外部软件 AI 底座的系统性方案见上游仓库的 `docs/ai-backend/`。

## 命令行参数与快捷键

```powershell
"DSH Desktop.exe"                 # 正常启动
"DSH Desktop.exe" --open runtime  # 启动后直接打开某个面板：runtime | update | plugins | diagnostics
```

| 快捷键 | 作用 | 快捷键 | 作用 |
| --- | --- | --- | --- |
| `Alt+R` | 运行环境管理 | `F5` | 重新加载页面 |
| `Alt+U` | 检查更新 | `F11` / `Esc` | 全屏 / 退出全屏 |
| `Alt+P` | 已加载插件 | `F12` | 开发者工具 |
| `Alt+D` | 诊断信息 | `Ctrl +` / `Ctrl -` / `Ctrl 0` | 放大 / 缩小 / 重置缩放 |
| `Alt+L` | 复制访问地址 | `Alt+M` / `F10` | 显示 / 隐藏窗口工具栏 |

> 页面获得焦点时按键由注入的页面监听器捕获并通过宿主消息回传（WebView2 的标准通道），所以快捷键在页面内也有效。
>
> 想让 DSH 界面独占整个窗口，按 `Alt+M`（或 `F10`）隐藏菜单栏与状态栏，状态会写回 `config.json` 的 `showMenuBar` / `showStatusBar`。页面区域严格等于「客户区 − 菜单栏 − 状态栏」，工具栏不会盖住 DSH 自己的标题栏与输入区——`tests\smoke.ps1` 中有 4 项断言专门校验这一点。

## 构建、自测与截图

```powershell
.\build.ps1                       # 完整构建（幂等，已存在的产物会复用）
.\build.ps1 -SkipRuntime          # 只重编译启动器
.\build.ps1 -Force -Package       # 强制刷新所有载荷并打便携 ZIP
.\build.ps1 -NoPrune              # 不做精简（用于对照体积）

.\tests\smoke.ps1                 # 18 项端到端自测
.\tests\smoke.ps1 -KeepRunning    # 保留窗口人工检查
.\tests\capture-tour.ps1          # 采集 README 的 6 张界面截图
node tests\lan-check.mjs --url <带 token 的地址> --authority <外部 authority>   # 信任围栏检查
node scripts\prune-runtime.mjs --root app\runtime\app\node_modules --report    # 只看精简报告，不删除
```

`tests\smoke.ps1` 覆盖：启动器进程、独立窗口出现、服务地址带 token、匿名请求被拒（401）、token 握手（303 + Cookie）、应用文档、API 网关、**DSH 宿主插件注册工具**、WebView2 渲染进程、未打开系统浏览器、`PrintWindow` 截图取证、关闭后 node 子进程与端口释放、WebView2 进程回收、日志与窗口状态落盘。

## 目录结构

```
DSH-Desktop\
├── app\                        # 构建产物，可整个复制分发
│   ├── DSH Desktop.exe         # 启动器（C# 5 / WebView2）
│   ├── config.json             # 用户配置
│   ├── runtime\node.exe        # 内置 Node（可删）
│   ├── runtime\npm\            # 内置 npm（更新 DSH 用）
│   ├── runtime\app\            # 内置 @deepseek-ai/dsh 生产依赖（已精简）
│   ├── webview2\               # WebView2 托管 SDK + 原生加载器
│   ├── plugins\                # 启动器插件（由工程 plugins\ 同步）
│   ├── scripts\                # 精简脚本（供运行时就地更新后调用）
│   ├── data\                   # DSH_HOME / 工作区 / 缓存 / 窗口状态（不随包分发）
│   └── logs\dsh-desktop.log    # 启动器与服务端日志（4 MB 轮转，保留 3 份）
├── src\                        # 启动器源码：DesktopSupport / RuntimeManager / Updates / Dialogs / DshDesktop
├── scripts\                    # 图标与横幅生成、WebView2 SDK 获取、精简、上游树校验
├── plugins\                    # 插件源码：example-branding、example-tool
├── tests\                      # smoke.ps1 / capture-tour.ps1 / http-check.mjs / lan-check.mjs / artifacts
├── docs\                       # 插件开发指南、界面截图
└── build.ps1
```

## 常见问题

<details>
<summary><b>需要预先安装 Node 吗？</b></summary>

不需要，包里带 Node。但如果机器上已经有满足 `^22.19.0 || >=24.0.0` 的 Node，可以在「运行环境管理」里删掉内置的那份，省 89 MB。
</details>

<details>
<summary><b>需要预先安装 WebView2 运行时吗？</b></summary>

Win11 和多数 Win10 已经预装。没有时启动器会自动改用 Edge 独立窗口模式（仍然是独立窗口，不是浏览器标签页）；两者都不可用才退回系统浏览器。注意随包分发的 1 MB 是 WebView2 **托管 SDK**，不是系统运行时，不能删。
</details>

<details>
<summary><b>首次启动为什么要几十秒？</b></summary>

DSH 会在 `$DSH_HOME` 下建立 profile，并把依赖以目录联接链接到运行时目录，然后加载前端资源。第二次启动是秒级。窗口在这段时间显示进度提示，日志同步写入 `app\logs\dsh-desktop.log`。
</details>

<details>
<summary><b>能用手机或平板访问吗？</b></summary>

不能直连：本版 DSH 的 webserver 只接受 `127.0.0.1` 与 `0.0.0.0`，并对 `0.0.0.0` 显式报错（理由是把远程代码执行能力暴露到网络）。正确做法是保持回环绑定，用隧道把端口映射出去（例如在另一台设备上 `ssh -N -L 3080:127.0.0.1:3080 user@this-pc`），需要时再用 `trustedHosts` 放行隧道呈现的 authority。
</details>

<details>
<summary><b>更新失败怎么办？</b></summary>

更新走 npm registry，失败通常是网络或代理问题，界面会显示原始错误。失败不影响当前版本；也可以手动执行 `node runtime\npm\bin\npm-cli.js install @deepseek-ai/dsh@<版本> --prefix runtime\app --omit=dev --ignore-scripts`，再跑一次 `node app\scripts\prune-runtime.mjs --root app\runtime\app\node_modules`。
</details>

<details>
<summary><b>怎么彻底重置？</b></summary>

删掉 `app\data\` 即可：DSH_HOME、会话、工作区、WebView2 用户数据与窗口记忆都在里面。要恢复出厂体积就重跑 `build.ps1 -Force`。
</details>

## 安全与隐私

- **只监听回环**：服务默认绑定 `127.0.0.1` 并由 token 换取 HttpOnly Cookie，匿名请求返回 401，来源不匹配返回 403。本项目不会替你打开网络暴露。
- **凭据由 DSH 管理**：模型密钥放在 `$DSH_HOME/.credentials.yaml` 或环境变量，启动器不读取、不打印、不写日志。
- **进程边界**：关窗即通过 Job Object 结束整棵进程树；插件与工具受 DSH 自身的权限预设与沙箱策略约束。
- **插件即可信代码**：注入的 CSS/JS 运行在页面上下文，`dsh.patch.yml` 挂载的插件运行在 DSH 进程内——只安装你自己写的插件。路径参数仅允许插件目录内的相对路径，越界会被忽略并记日志。
- **无遥测**：本启动器不发送任何统计；唯一的出站请求是 npm registry 的版本查询与 nodejs.org 的可选 Node 下载。

## 路线图

- [ ] 便携模式与服务模式切换（把 `DSH_HOME` 指到用户目录、多用户隔离）
- [ ] 系统托盘：常驻后台、气泡提示更新
- [ ] 增量更新：只替换变化文件，缩短更新耗时
- [ ] 插件市场的本地索引：从 `plugins\` 一键安装/禁用/排序
- [ ] 多窗口：同一 DSH 实例开多个工作区窗口
- [ ] 简体中文以外的界面语言（启动器文案走资源文件）

## 致谢

- [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)：本项目的底座，MIT 许可。所有 Agent 能力都来自它。
- [Cordis](https://github.com/cordiverse/cordis)：DSH 的插件运行时。
- [Microsoft WebView2](https://learn.microsoft.com/microsoft-edge/webview2/)：窗口内嵌渲染。
- 图标与横幅由 Qwen 图像生成模型产出，再用 GDI+ 做圆角遮罩与文字排版。

## 许可

本项目以 MIT 许可发布，见 [LICENSE](LICENSE)。随包分发的第三方组件：Node.js（Node 许可）、Microsoft WebView2 SDK（见 `app\webview2\LICENSE.txt`）、DeepSeek Harness 及其依赖（MIT 及其各自许可）。

本项目是社区项目，与 DeepSeek 官方无隶属关系；“DeepSeek”“DeepSeek Harness” 商标归其所有者。
