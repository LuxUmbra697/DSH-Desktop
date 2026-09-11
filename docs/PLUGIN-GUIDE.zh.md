# DSH Desktop 插件开发指南

DSH Desktop 有两层插件，可以单独使用也可以组合：

| 层 | 入口 | 能改什么 | 是否需要编译 |
| --- | --- | --- | --- |
| 启动器插件 | `plugins\<id>\launcher.json` | 窗口标题与尺寸、注入 CSS/JS、追加环境变量与命令行参数、挂载 DSH 宿主插件 | 否 |
| DSH 宿主插件 | `plugins\<id>\dsh.patch.yml` + `plugins\<id>\dsh\*.mjs` | DSH 插件树里的一切：工具、命令、技能、模型适配器、Agent preset | 否（写 ESM 即可） |

目录约定：

```
plugins\
└── my-plugin\
    ├── launcher.json          # 必需：启动器插件清单
    ├── inject\theme.css       # 可选：注入页面样式
    ├── inject\script.js       # 可选：注入页面脚本
    ├── dsh.patch.yml          # 可选：传给 dsh 的 --patch 清单
    └── dsh\my-plugin.mjs      # 可选：DSH 宿主插件实现
```

改完插件后重启 `DSH Desktop.exe` 即生效（`app\plugins` 是构建产物，请改工程内的 `plugins\` 再执行 `build.ps1`）。

## 一、启动器插件

`launcher.json` 的字段全部可选：

```json
{
  "name": "my-branding",
  "description": "把窗口改成我们产品的样子",
  "enabled": true,
  "title": "XXX 智能助手",
  "width": 1360,
  "height": 880,
  "minWidth": 420,
  "minHeight": 320,
  "zoomFactor": 1.0,
  "injectCss": ["inject/theme.css"],
  "injectJs": ["inject/script.js"],
  "env": { "MY_PRODUCT_MODE": "desktop" },
  "args": [],
  "trustedHosts": [],
  "dshPatch": ["dsh.patch.yml"],
  "dshLinkModules": "dsh",
  "userAgentSuffix": "MyProduct/1.0"
}
```

行为说明：

- **优先级**：插件 > `config.json` > 内置默认；多个插件写同一字段时按目录名字母序后者覆盖前者。
- **路径安全**：`injectCss`、`injectJs`、`dshPatch` 只接受插件目录内的相对路径，越界会被忽略并写入日志。
- **注入时机**：CSS/JS 在每个文档创建时注入，脚本拿得到 `window.__DSH_DESKTOP__`（版本、安装目录、插件列表、LAN 状态）。
- **`dshLinkModules`**：填一个插件内目录名（示例用 `dsh`），启动器会把内置运行时的 `node_modules` 以目录联接的方式挂到 `plugins\<id>\dsh\node_modules`，这样你的宿主插件可以直接 `import { defineTool } from '@deepseek-ai/dsh-tools'`，不需要 `npm install`。
- **`env` / `args`**：`env` 进入 `dsh` 子进程环境，`args` 追加到命令行（例如额外的 `--trusted-host`）。

## 二、DSH 宿主插件

最小可用示例（`plugins\my-plugin\dsh.patch.yml` + `plugins\my-plugin\dsh\my-plugin.mjs`）：

```yaml
# dsh.patch.yml —— 一个顶层 YAML 数组，语义与 DSH 原生 patch 完全一致
- insert:
    - id: my-tools
      name: ./dsh/my-plugin.mjs
      config:
        apiBaseUrl: ''
```

```js
// dsh/my-plugin.mjs
import z from '@deepseek-ai/schemastery'
import { defineTool } from '@deepseek-ai/dsh-tools'

export const name = 'my-tools'
export const inject = ['tools']
export const Config = z.object({ apiBaseUrl: z.string().default('') })

export function apply(ctx, config) {
  ctx.tools.register(defineTool({
    name: 'my_lookup',
    description: 'Look something up in MyProduct. Call it before answering about a specific record.',
    parameters: { id: { type: 'string', required: true, description: 'Record id.' } },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: { id: { type: 'string', required: true }, summary: { type: 'string', required: true } }
      },
      render: (_args, value) => [{ type: 'text', text: value.id + ': ' + value.summary }]
    },
    async execute(args) {
      // 没配后端时返回演示数据，保证插件可以离线自测
      const summary = config.apiBaseUrl ? await fetchFromBackend(config, args.id) : 'demo row ' + args.id
      return { id: args.id, summary }
    },
    presentCall: (args) => ({ card: 'generic', title: '读取 ' + args.id, kind: 'other', rawInput: args })
  }))

  // 启动标记：用于自测断言插件真的执行了 apply()，而不只是被列进插件树
  console.log('[my-tools] applied: registered my_lookup')
}
```

写完后按顺序验证：

```powershell
# 1) 插件是否进了组合后的插件树
node "app\runtime\app\node_modules\@deepseek-ai\dsh\lib\bin.js" --profile web `
  --patch "app\plugins\my-plugin\dsh.patch.yml" --dump-config

# 2) 服务是否能带着插件正常启动（日志里应出现你的启动标记）
.\tests\smoke.ps1 -KeepRunning
```

## 三、Agent preset（定制 Agent 人格与工具集）

把 preset 放进 `$DSH_HOME\.agent-presets\<id>\`（桌面端默认是 `app\data\dsh-home\.agent-presets\`），两个文件：`preset.yml`（`name`/`description`/`order`）与 `agent.cordis.yml`（行列表）。需要让 DSH 认到你的 preset 时，用 patch 覆盖 `agent-presets` 行：

```yaml
- id: agent-presets
  config:
    default: my-agent
    includeShippedRoot: true
    includeUserRoot: true
    roots:
      - path: ./presets
        trust: true
```

Agent 自有服务必须包在 `cordis:group` + `isolate` 里，否则挂载会被拒绝。详见 [docs\PLUGIN-GUIDE.zh.md](PLUGIN-GUIDE.zh.md) 与仓库 `docs/ai-backend/` 的完整方案。

## 四、调试与排障

| 现象 | 排查 |
| --- | --- |
| 插件没被加载 | “插件(P) → 已加载插件…”看清单是否被读取；`launcher.json` 解析失败会写进 `app\logs\dsh-desktop.log` |
| patch 没生效 | `--dump-config` 对照真实行 `id`；未命中的 patch 只会告警 |
| 插件 import 失败 | 检查 `dshLinkModules` 是否指向了你的插件目录，以及 `app\plugins\<id>\dsh\node_modules` 是否已建立 |
| 工具注册了但模型看不到 | 工具属于 preset 平面，把工具行放进 preset 的 group/isolate |
| 注入脚本报错 | 打开开发者工具（`视图 → 开发者工具` 或 F12）看 Console；注入脚本应写成幂等 |
| 想恢复默认外观 | 把 `pluginsDisabled` 设为 `true`，或删掉 `app\plugins` 下对应目录 |

## 五、从示例开始

- `plugins\example-branding`：改标题、改尺寸、注入品牌样式与脚本、加环境变量。
- `plugins\example-tool`：用 `dsh.patch.yml` 挂两个业务工具，演示 `dshLinkModules` 与启动标记。

把这两个目录复制改名，就是你自己插件的起点。
