/**
 * Creates a GitHub fork of an upstream repository and publishes a set of local
 * paths into it, using only the REST API.
 *
 * This exists because some networks allow api.github.com but reset git-over-HTTPS
 * to github.com. The API path reproduces what a push would do: blobs -> tree ->
 * commit -> ref update, on top of the fork's current head. The fork itself is
 * created by GitHub from upstream, so it starts with upstream's full history.
 *
 * Usage:
 *   node push-fork-via-api.mjs --token <PAT> [--paths docs/ai-backend] [--dry-run]
 *
 * Options:
 *   --token <PAT>        GitHub token with public_repo scope (required)
 *   --upstream <o/r>     default deepseek-ai/deepseek-harness
 *   --fork <o/r>         default LuxUmbra697/deepseek-harness
 *   --branch <name>      default master
 *   --paths <p1,p2>      repo-relative files or directories to publish
 *   --message <text>     commit message
 *   --repo <dir>         local repository root (default: two levels up)
 *   --dry-run            resolve everything and report, without writing
 */
import { readFileSync, statSync, readdirSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const API = 'https://api.github.com';

function parseArgs(argv) {
  const args = {
    token: '',
    upstream: 'deepseek-ai/deepseek-harness',
    fork: 'LuxUmbra697/deepseek-harness',
    branch: 'master',
    paths: ['docs/ai-backend'],
    message: 'Add docs/ai-backend: using DSH as an external software AI backend',
    repo: '',
    dryRun: false,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const value = argv[i];
    if (value === '--token') args.token = argv[++i];
    else if (value === '--upstream') args.upstream = argv[++i];
    else if (value === '--fork') args.fork = argv[++i];
    else if (value === '--branch') args.branch = argv[++i];
    else if (value === '--paths') args.paths = argv[++i].split(',');
    else if (value === '--message') args.message = argv[++i];
    else if (value === '--repo') args.repo = argv[++i];
    else if (value === '--dry-run') args.dryRun = true;
  }
  if (!args.token) throw new Error('--token is required');
  if (!args.repo) args.repo = join(import.meta.dirname, '..', '..');
  return args;
}

async function api(args, method, path, body) {
  const response = await fetch(API + path, {
    method,
    headers: {
      accept: 'application/vnd.github+json',
      authorization: 'Bearer ' + args.token,
      'user-agent': 'dsh-fork-publisher',
      'x-github-api-version': '2022-11-28',
      ...(body ? { 'content-type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch (error) {
    json = { raw: text.slice(0, 400) };
  }
  if (!response.ok) {
    const message = json && json.message ? json.message : text.slice(0, 200);
    const error = new Error(method + ' ' + path + ' -> ' + response.status + ' ' + message);
    error.status = response.status;
    error.body = json;
    throw error;
  }
  return json;
}

function listFiles(root, target) {
  const full = join(root, target);
  const stats = statSync(full);
  if (stats.isFile()) return [target.split(sep).join('/')];
  const files = [];
  for (const entry of readdirSync(full, { withFileTypes: true })) {
    const child = join(target, entry.name);
    if (entry.isDirectory()) files.push(...listFiles(root, child));
    else files.push(child.split(sep).join('/'));
  }
  return files;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const args = parseArgs(process.argv.slice(2));

const user = await api(args, 'GET', '/user');
console.log('token: ' + user.login + (user.name ? ' (' + user.name + ')' : ''));
const owner = args.fork.split('/')[0];
if (user.login.toLowerCase() !== owner.toLowerCase()) {
  console.log('warning: token belongs to ' + user.login + ' but the fork owner is ' + owner);
}

let repo = null;
try {
  repo = await api(args, 'GET', '/repos/' + args.fork);
  console.log('fork: exists (' + repo.full_name + ', default branch ' + repo.default_branch + ')');
} catch (error) {
  if (error.status !== 404) throw error;
  console.log('fork: creating from ' + args.upstream + ' ...');
  await api(args, 'POST', '/repos/' + args.upstream + '/forks', { default_branch_only: false });
  for (let attempt = 0; attempt < 40 && !repo; attempt += 1) {
    await sleep(3000);
    try {
      repo = await api(args, 'GET', '/repos/' + args.fork);
    } catch (error) {
      if (error.status !== 404) throw error;
    }
  }
  if (!repo) throw new Error('fork did not become readable in time');
  console.log('fork: ready (' + repo.full_name + ')');
}

const headRef = await api(args, 'GET', '/repos/' + args.fork + '/git/ref/heads/' + args.branch);
const headSha = headRef.object.sha;
const headCommit = await api(args, 'GET', '/repos/' + args.fork + '/git/commits/' + headSha);
const baseTree = headCommit.tree.sha;
const upstreamHead = await api(args, 'GET', '/repos/' + args.upstream + '/commits/' + args.branch);
console.log('fork head : ' + headSha);
console.log('upstream  : ' + upstreamHead.sha + (upstreamHead.sha === headSha ? ' (in sync)' : ' (fork is behind)'));
console.log('base tree : ' + baseTree);

const paths = [];
for (const target of args.paths) paths.push(...listFiles(args.repo, target));
paths.sort();
console.log('publishing ' + paths.length + ' files from ' + args.paths.join(', '));

if (args.dryRun) {
  for (const path of paths) console.log('  would publish ' + path);
  process.exit(0);
}

const treeEntries = [];
for (const path of paths) {
  const content = readFileSync(join(args.repo, path));
  const blob = await api(args, 'POST', '/repos/' + args.fork + '/git/blobs', {
    content: content.toString('base64'),
    encoding: 'base64',
  });
  treeEntries.push({ path, mode: '100644', type: 'blob', sha: blob.sha });
  console.log('  blob ' + path + ' ' + blob.sha.slice(0, 8));
}

const tree = await api(args, 'POST', '/repos/' + args.fork + '/git/trees', { base_tree: baseTree, tree: treeEntries });
const commit = await api(args, 'POST', '/repos/' + args.fork + '/git/commits', {
  message: args.message,
  tree: tree.sha,
  parents: [headSha],
});
await api(args, 'PATCH', '/repos/' + args.fork + '/git/refs/heads/' + args.branch, { sha: commit.sha, force: false });

console.log('published commit ' + commit.sha);
console.log('compare: ' + 'https://github.com/' + args.fork + '/compare/' + headSha + '...' + commit.sha);
console.log('tree   : ' + 'https://github.com/' + args.fork + '/tree/' + args.branch);
