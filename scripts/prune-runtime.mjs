/**
 * Shrinks the bundled DSH runtime by removing files that a running process never
 * reads: source maps, TypeScript declarations, native debug symbols, prebuilt
 * binaries for other platforms, and development-only metadata.
 *
 * Anything the runtime could load is kept: package.json, README and i18n
 * metadata (DSH reads package documentation), every .js/.mjs/.cjs/.json data
 * file, native binaries for win32-x64, and all license files.
 *
 * Usage:
 *   node prune-runtime.mjs --root <dir> [--report] [--json] [--keep-maps]
 *
 * Exit codes: 0 = pruned or reported, 1 = nothing to prune / bad arguments.
 */
import { readdirSync, statSync, rmSync, existsSync } from 'node:fs';
import { join, extname, basename } from 'node:path';

function parseArgs(argv) {
  const args = { root: '', report: false, json: false, keepMaps: false };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--root') args.root = argv[++i];
    else if (argv[i] === '--report') args.report = true;
    else if (argv[i] === '--json') args.json = true;
    else if (argv[i] === '--keep-maps') args.keepMaps = true;
  }
  if (!args.root) throw new Error('usage: node prune-runtime.mjs --root <node_modules> [--report] [--json]');
  if (!existsSync(args.root)) throw new Error('not found: ' + args.root);
  return args;
}

const KEEP_NAMES = new Set([
  'package.json', 'package-lock.json', 'npm-shrinkwrap.json', 'binding.gyp',
]);

/** Files whose only purpose is development, testing, or editor integration. */
// Plain .ts files stay: Node 24 can execute TypeScript, and a few hundred packages
// name a .ts file in their entry fields.
const DROP_EXTENSIONS = new Set(['.map', '.pdb', '.tsbuildinfo', '.flow', '.coffee']);
const CONFIG_NAMES = new Set([
  'rollup.config.js', 'rollup.config.mjs', 'tsup.config.js', 'tsdown.config.ts',
  'vitest.config.js', 'vitest.config.ts', 'jest.config.js', 'jest.config.ts',
  'karma.conf.js', 'webpack.config.js', 'vite.config.js', 'vite.config.ts',
  'babel.config.js', 'babel.config.cjs', 'postcss.config.js', 'tailwind.config.js',
  'commitlint.config.js', 'lint-staged.config.js', 'typedoc.config.js',
]);
const DROP_EXACT = new Set([
  '.eslintrc', '.eslintrc.js', '.eslintrc.cjs', '.eslintrc.json', '.eslintignore',
  '.prettierrc', '.prettierrc.js', '.prettierrc.json', '.prettierignore',
  '.editorconfig', '.npmignore', '.gitattributes', '.gitignore', '.travis.yml',
  '.nycrc', '.nycrc.json', '.babelrc', '.babelrc.js', '.browserslistrc',
  'tsconfig.json', 'tsconfig.build.json', 'tsconfig.base.json', 'tsconfig.cjs.json',
]);
const DROP_DIRS = new Set([
  'test', 'tests', '__tests__', '__snapshots__', 'spec', 'benchmark', 'benchmarks',
  'coverage', '.github', '.vscode', '.idea', '.circleci', '.devcontainer', 'example', 'examples',
]);

function isDroppableFile(path, name, keepMaps) {
  if (KEEP_NAMES.has(name)) return false;
  if (name.startsWith('LICENSE') || name.startsWith('NOTICE') || name.startsWith('CHANGELOG')) return false;
  if (name.startsWith('tsconfig') && extname(name) === '.json') return true;
  if (CONFIG_NAMES.has(name)) return true;
  if (name.endsWith('.d.ts') || name.endsWith('.d.mts') || name.endsWith('.d.cts')) return true;
  if (DROP_EXACT.has(name)) return true;
  const ext = extname(name).toLowerCase();
  if (ext === '.map') return !keepMaps;
  return DROP_EXTENSIONS.has(ext);
}

function walk(root, keepMaps, found) {
  let entries;
  try {
    entries = readdirSync(root, { withFileTypes: true });
  } catch (error) {
    return;
  }
  for (const entry of entries) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) {
      // Prebuilt binaries for other platforms are dead weight on Windows x64.
      if (root.endsWith(join('node-pty', 'prebuilds')) && entry.name !== 'win32-x64') {
        found.push({ path, reason: 'foreign-platform-prebuild', directory: true });
        continue;
      }
      if (DROP_DIRS.has(entry.name) && !root.endsWith(entry.name)) {
        found.push({ path, reason: 'dev-directory', directory: true });
        continue;
      }
      walk(path, keepMaps, found);
      continue;
    }
    if (!entry.isFile()) continue;
    if (isDroppableFile(path, entry.name, keepMaps)) {
      found.push({ path, reason: 'dev-file', directory: false });
    }
  }
}

const args = parseArgs(process.argv.slice(2));
const found = [];
walk(args.root, args.keepMaps, found);

const byReason = new Map();
let totalBytes = 0;
const measured = [];
for (const entry of found) {
  let bytes = 0;
  try {
    if (entry.directory) {
      const stack = [entry.path];
      while (stack.length) {
        const current = stack.pop();
        for (const item of readdirSync(current, { withFileTypes: true })) {
          const child = join(current, item.name);
          if (item.isDirectory()) stack.push(child);
          else bytes += statSync(child).size;
        }
      }
    } else {
      bytes = statSync(entry.path).size;
    }
  } catch (error) {
    bytes = 0;
  }
  entry.bytes = bytes;
  totalBytes += bytes;
  byReason.set(entry.reason, (byReason.get(entry.reason) || 0) + bytes);
  measured.push(entry);
}

const summary = {
  root: args.root,
  targets: measured.length,
  megabytes: Number((totalBytes / 1024 / 1024).toFixed(1)),
  byReason: Object.fromEntries([...byReason.entries()].map(([k, v]) => [k, Number((v / 1024 / 1024).toFixed(1))])),
  largest: measured
    .slice()
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, 10)
    .map((entry) => ({ path: entry.path.slice(args.root.length + 1), megabytes: Number((entry.bytes / 1024 / 1024).toFixed(2)) })),
};

if (args.report) {
  console.log('prune report (nothing was deleted)');
  console.log(JSON.stringify(summary, null, 2));
  process.exit(0);
}

let deleted = 0;
let failed = 0;
for (const entry of measured) {
  try {
    rmSync(entry.path, { recursive: entry.directory, force: true });
    deleted += 1;
  } catch (error) {
    failed += 1;
  }
}
summary.deleted = deleted;
summary.failed = failed;
console.log(JSON.stringify(summary, args.json ? null : null, 2));
