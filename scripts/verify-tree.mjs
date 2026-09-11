/**
 * Compares every path in the local git tree against GitHub's authoritative
 * master tree, so a CDN-reconstructed checkout can be verified byte for byte
 * instead of trusted.
 *
 * Usage: node verify-tree.mjs <repo-dir>
 *        node verify-tree.mjs --ls-tree <file> --head <sha>
 *
 * The second form exists for hosts where Node may not spawn a subprocess (a
 * sandbox that denies piped stdio): generate the listing and head outside and
 * pass them in.
 *
 * Requires: git on PATH unless --ls-tree is given; network access to api.github.com.
 */
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fetchJson } from './lib/http.mjs';

const argv = process.argv.slice(2);
let repo = null;
let listingFile = null;
let headOverride = null;
for (let i = 0; i < argv.length; i += 1) {
  if (argv[i] === '--ls-tree') listingFile = argv[++i];
  else if (argv[i] === '--head') headOverride = argv[++i];
  else repo = argv[i];
}
if (!repo && !listingFile) throw new Error('usage: node verify-tree.mjs <repo-dir> | --ls-tree <file> [--head <sha>]');

const OWNER = 'deepseek-ai';
const REPO = 'deepseek-harness';

function git(args) {
  return execFileSync('git', ['-c', 'core.longpaths=true', ...args], { cwd: repo, maxBuffer: 256 * 1024 * 1024 })
    .toString('utf8');
}

let localHead;
let localLines;
if (listingFile) {
  localHead = headOverride || '(from listing)';
  localLines = readFileSync(listingFile, 'utf8').split('\0').filter(Boolean);
} else {
  localHead = git(['rev-parse', 'HEAD']).trim();
  localLines = git(['ls-tree', '-r', '-z', 'HEAD']).split('\0').filter(Boolean);
}

const commit = await fetchJson(`https://api.github.com/repos/${OWNER}/${REPO}/commits/master`);
const remoteHead = commit.sha;
const remoteTree = commit.commit.tree.sha;

console.log('local  HEAD', localHead);
console.log('remote HEAD', remoteHead, 'tree', remoteTree);

const remote = await fetchJson(`https://api.github.com/repos/${OWNER}/${REPO}/git/trees/${remoteTree}?recursive=1`);
if (remote.truncated) throw new Error('remote tree listing truncated');

const remoteBlobs = new Map();
for (const entry of remote.tree) {
  if (entry.type === 'blob' || entry.type === 'commit') remoteBlobs.set(entry.path, entry.sha);
}

const localBlobs = new Map();
for (const line of localLines) {
  const match = /^\d+ blob ([0-9a-f]{40})\t(.+)$/.exec(line);
  if (match) localBlobs.set(match[2], match[1]);
}

const missing = [];
const differing = [];
for (const [path, sha] of remoteBlobs) {
  if (!localBlobs.has(path)) missing.push(path);
  else if (localBlobs.get(path) !== sha) differing.push(path);
}
const extra = [...localBlobs.keys()].filter((path) => !remoteBlobs.has(path));

console.log('remote blobs:', remoteBlobs.size, 'local blobs:', localBlobs.size);
console.log('missing locally:', missing.length);
console.log('content differs:', differing.length);
console.log('extra locally:', extra.length);
const sample = (label, list) => {
  if (list.length) console.log(label + ' (first 25):\n  ' + list.slice(0, 25).join('\n  '));
};
sample('MISSING', missing);
sample('DIFFERING', differing);
sample('EXTRA', extra);
process.exit(missing.length || differing.length ? 1 : 0);
