# 插件目录

把插件目录直接放进这里，重启 `DSH Desktop.exe` 即生效。每个子目录一个插件，目录名即插件 id。

```
plugins/
└── my-plugin/
    ├── launcher.json      # 必需：启动器插件清单
    ├── inject/
    │   ├── theme.css      # 可选：注入到 DSH 页面的样式
    │   └── script.js      # 可选：注入到 DSH 页面的脚本
    └── dsh.patch.yml      # 可选：传给 dsh web 的 --patch 清单，用来挂载 DSH 宿主插件
```

## launcher.json 字段

| 字段 | 类型 | 作用 |
| --- | --- | --- |
| `name` | string | 展示名，缺省用目录名 |
| `description` | string | “已加载插件”对话框里的说明 |
| `enabled` | bool | `false` 时整个插件跳过 |
| `title` | string | 覆盖窗口标题 |
| `width` / `height` | int | 覆盖初始窗口尺寸 |
| `minWidth` / `minHeight` | int | 覆盖最小尺寸 |
| `zoomFactor` | number | 覆盖初始缩放（1.0 = 100%） |
| `injectCss` | string[] | 相对插件目录的 CSS 文件，注入 `<style>` |
| `injectJs` | string[] | 相对插件目录的 JS 文件，在每个文档创建时执行 |
| `env` | object | 追加到 `dsh web` 子进程的环境变量 |
| `args` | string[] | 追加到 `dsh web` 命令行的参数（如 `--trusted-host`） |
| `trustedHosts` | string[] | 追加到 `--trusted-host` 白名单 |
| `dshPatch` | string[] | 相对插件目录的 patch 清单，逐个作为 `--patch` 传入 |
| `dshLinkModules` | string | 插件内的目录名（如 `dsh`）；启动器把内置运行时 `node_modules` 以目录联接挂到 `<该目录>\node_modules`，使插件可直接 import `@deepseek-ai/*` 而无需 npm 安装 |
| `dshPlugins` | string[] | 预留：随包分发的 DSH 插件模块名 |
| `userAgentSuffix` | string | 追加到 WebView2 的 User-Agent，便于服务端识别桌面端 |

## 优先级

`插件 > config.json > 内置默认`。多个插件同时设置同一字段时，按目录名字母序后者覆盖前者。

## 注入脚本可用的上下文

启动器在每个文档创建前注入：

```js
window.__DSH_DESKTOP__ = {
  version: '1.0.0',
  root: 'D:\\...\\DSH Desktop\\app',
  plugins: ['example-branding'],
  lan: false,
  host: '127.0.0.1',
  platform: 'windows'
};
```

## 安全边界

`injectCss` / `injectJs` / `dshPatch` 只接受插件目录内的相对路径，越界路径会被忽略并写入日志。
插件脚本运行在 DSH 页面上下文，等同于本地可信代码，只应安装你自己写的插件。
