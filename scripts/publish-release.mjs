/**
 * Publishes a GitHub release for DSH Desktop and uploads the portable zip.
 *
 * Usage: node publish-release.mjs --token <PAT> [--tag v1.1.0] [--asset <file>]
 */
import { readFileSync, statSync } from 'node:fs';
import { basename } from 'node:path';

function parseArgs(argv) {
  const args = { token: '', tag: 'v1.1.0', asset: '', repo: 'LuxUmbra697/DSH-Desktop' };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--token') args.token = argv[++i];
    else if (argv[i] === '--tag') args.tag = argv[++i];
    else if (argv[i] === '--asset') args.asset = argv[++i];
    else if (argv[i] === '--repo') args.repo = argv[++i];
  }
  if (!args.token) throw new Error('--token is required');
  return args;
}

const args = parseArgs(process.argv.slice(2));
const headers = {
  authorization: 'Bearer ' + args.token,
  accept: 'application/vnd.github+json',
  'user-agent': 'dsh-desktop-release',
  'x-github-api-version': '2022-11-28',
};

async function json(method, url, body, extraHeaders) {
  const response = await fetch(url, {
    method,
    headers: { ...headers, ...(body ? { 'content-type': 'application/json' } : {}), ...(extraHeaders || {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  let parsed = null;
  try {
    parsed = text ? JSON.parse(text) : null;
  } catch (error) {
    parsed = { raw: text.slice(0, 300) };
  }
  if (!response.ok) {
    throw new Error(method + ' ' + url + ' -> ' + response.status + ' ' + (parsed.message || text.slice(0, 200)));
  }
  return parsed;
}

const notes = `## DSH Desktop 1.1.0

把 DeepSeek Harness 装进一个独立窗口：双击即用，不开终端，不开浏览器。

### 本次更新

**体积：可分发载荷 314.6 MB → 210.9 MB，便携包 74.8 MB**

- 新增精简步骤，只删除运行时不会读取的文件（源映射、类型声明、调试符号、其它平台预编译、开发目录）：DSH 运行时依赖 212.4 MB → 108.7 MB，并已验证精简后仍能正常启动、加载插件、通过 18 项自测。
- 随包分发 npm（11.8 MB），使 DSH 可以就地更新。
- \`build.ps1 -Package\` 产出便携 ZIP；构建汇总把「可分发载荷」与「用户数据」分开统计。

**新增：运行环境管理（\`文件 → 运行环境管理…\`，\`Alt+R\`）**

- 检测内置/系统 Node、npm、WebView2 运行时的路径与版本，并显示各部分占用。
- 系统 Node 满足 DSH 要求 \`^22.19.0 || >=24.0.0\` 时，可一键删除内置 Node（省 89.2 MB）；版本过低或找不到时按钮置灰并写明原因。
- 可从系统 Node 复制或从 nodejs.org 重新下载内置 Node（版本在构建时记录）。

**新增：检查更新（\`帮助 → 检查更新…\`，\`Alt+U\`）**

- 读取 npm dist-tags，与随包版本对比，显示 \`next\`/\`latest\` 与发布时间。
- 「更新并重启」用随包 npm 在 \`runtime\\app\` 原地更新、自动精简、重启服务；插件、配置与会话不受影响。
- \`checkUpdatesOnStartup\`（默认开）与 \`autoUpdate\`（默认关）把「是否自动更新」变成显式选择——官方 npx 每次启动都会跟随最新版本，版本会悄悄变化。

**窗口与体验**

- 页面获得焦点时快捷键依然有效（注入的页面监听器经 WebView2 宿主消息回传）。
- \`--open runtime|update|plugins|diagnostics\` 启动后直接打开指定面板。
- 容错：\`config.json\` / \`launcher.json\` 带 UTF-8 BOM 也能正常加载。

**图标与文档**

- 用 Qwen 图像模型生成鲸鱼娘图标，裁切圆角后输出 16–256 共 7 个尺寸的 \`.ico\`，并生成 README 横幅。
- 新增 6 项截图自动采集（\`tests\\capture-tour.ps1\`），README 图文并茂（中文 + English）。

### 下载

- \`DSH-Desktop-1.1.0-win-x64.zip\`（74.8 MB）：解压到任意目录，双击 \`DSH Desktop.exe\` 即可。首次启动会在 \`data\\dsh-home\` 建立运行环境（10–40 秒）。

### 运行要求

Windows 10/11 x64；WebView2 运行时（Win11 与近年 Win10 预装，缺失时自动降级为 Edge 独立窗口）。不需要预装 Node。

### 验证

\`tests\\smoke.ps1\` 18 项断言全部通过：进程与窗口、服务地址带 token、匿名 401、握手 303 + Cookie、应用文档、API 网关、DSH 宿主插件注册工具、WebView2 渲染进程、未打开系统浏览器、截图取证、关闭后 node 与端口释放、WebView2 回收、日志与窗口状态落盘。
`;

let release = null;
try {
  release = await json('GET', 'https://api.github.com/repos/' + args.repo + '/releases/tags/' + args.tag);
  console.log('release exists:', release.tag_name, 'id', release.id);
} catch (error) {
  const response = await fetch('https://api.github.com/repos/' + args.repo + '/releases', {
    method: 'POST',
    headers: { ...headers, 'content-type': 'application/json' },
    body: JSON.stringify({
      tag_name: args.tag,
      target_commitish: 'main',
      name: 'DSH Desktop ' + args.tag.replace(/^v/, ''),
      body: notes,
      draft: false,
      prerelease: false,
    }),
  });
  const text = await response.text();
  if (!response.ok) throw new Error('create release -> ' + response.status + ' ' + text.slice(0, 300));
  release = JSON.parse(text);
  console.log('release created:', release.tag_name, 'id', release.id);
}

if (args.asset) {
  const size = statSync(args.asset).size;
  const name = basename(args.asset);
  const existing = (release.assets || []).find((asset) => asset.name === name);
  if (existing) {
    console.log('asset already uploaded:', name, existing.size, 'bytes');
  } else {
    console.log('uploading', name, (size / 1024 / 1024).toFixed(1), 'MB ...');
    // A read stream avoids holding the archive in memory while GitHub receives it.
    const { createReadStream } = await import('node:fs');
    const { Readable } = await import('node:stream');
    const upload = await fetch(
      'https://uploads.github.com/repos/' + args.repo + '/releases/' + release.id + '/assets?name=' + encodeURIComponent(name),
      {
        method: 'POST',
        headers: { ...headers, 'content-type': 'application/zip', 'content-length': String(size) },
        body: Readable.toWeb(createReadStream(args.asset)),
        duplex: 'half',
      },
    );
    const text = await upload.text();
    if (!upload.ok) throw new Error('upload -> ' + upload.status + ' ' + text.slice(0, 300));
    const asset = JSON.parse(text);
    console.log('uploaded:', asset.name, asset.size, 'bytes');
  }
}

const final = await json('GET', 'https://api.github.com/repos/' + args.repo + '/releases/tags/' + args.tag);
console.log('release url:', final.html_url);
for (const asset of final.assets || []) {
  console.log('asset:', asset.name, (asset.size / 1024 / 1024).toFixed(1), 'MB', 'downloads:', asset.download_count);
}
