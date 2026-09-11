/**
 * Downloads the Microsoft WebView2 SDK from NuGet and unpacks the three files
 * the launcher links against:
 *
 *   Microsoft.Web.WebView2.Core.dll      - the managed Core assembly (net462)
 *   Microsoft.Web.WebView2.WinForms.dll  - the WinForms control (net462)
 *   WebView2Loader.dll                   - the native loader (win-x64)
 *
 * The nupkg is a zip file, and this script reads it directly so the build has no
 * dependency on Expand-Archive, tar, or any installed archiver.
 *
 * Usage: node fetch-webview2.mjs --target <dir> [--version latest] [--force]
 */
import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join } from 'node:path';
import zlib from 'node:zlib';
import { fetchJson, fetchBuffer } from './lib/http.mjs';

const FEED = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2';

function parseArgs(argv) {
  const args = { target: null, version: 'latest', force: false };
  for (let i = 0; i < argv.length; i += 1) {
    const value = argv[i];
    if (value === '--target') args.target = argv[++i];
    else if (value === '--version') args.version = argv[++i];
    else if (value === '--force') args.force = true;
  }
  if (!args.target) throw new Error('usage: node fetch-webview2.mjs --target <dir> [--version latest] [--force]');
  return args;
}

/** Reads the zip central directory of the given buffer. */
function readZipEntries(buffer) {
  let eocd = -1;
  const floor = Math.max(0, buffer.length - 22 - 65536);
  for (let i = buffer.length - 22; i >= floor; i -= 1) {
    if (buffer.readUInt32LE(i) === 0x06054b50) {
      eocd = i;
      break;
    }
  }
  if (eocd < 0) throw new Error('zip end-of-central-directory record not found');
  const count = buffer.readUInt16LE(eocd + 10);
  let offset = buffer.readUInt32LE(eocd + 16);
  const entries = [];
  for (let i = 0; i < count; i += 1) {
    if (buffer.readUInt32LE(offset) !== 0x02014b50) throw new Error('corrupt zip central directory');
    const method = buffer.readUInt16LE(offset + 10);
    const compressedSize = buffer.readUInt32LE(offset + 20);
    const nameLength = buffer.readUInt16LE(offset + 28);
    const extraLength = buffer.readUInt16LE(offset + 30);
    const commentLength = buffer.readUInt16LE(offset + 32);
    const localOffset = buffer.readUInt32LE(offset + 42);
    const name = buffer.toString('utf8', offset + 46, offset + 46 + nameLength);
    entries.push({ name, method, compressedSize, localOffset });
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}

function extractEntry(buffer, entry) {
  const base = entry.localOffset;
  if (buffer.readUInt32LE(base) !== 0x04034b50) throw new Error('corrupt zip local header for ' + entry.name);
  const nameLength = buffer.readUInt16LE(base + 26);
  const extraLength = buffer.readUInt16LE(base + 28);
  const start = base + 30 + nameLength + extraLength;
  const data = buffer.subarray(start, start + entry.compressedSize);
  if (entry.method === 0) return data;
  if (entry.method === 8) return zlib.inflateRawSync(data);
  throw new Error('unsupported zip compression method ' + entry.method + ' for ' + entry.name);
}

async function resolveVersion(requested) {
  if (requested && requested !== 'latest') return requested;
  const index = await fetchJson(FEED + '/index.json');
  const versions = index.versions.filter((v) => v.indexOf('-') < 0);
  if (versions.length === 0) throw new Error('no stable WebView2 version published');
  return versions[versions.length - 1];
}

const args = parseArgs(process.argv.slice(2));
const marker = join(args.target, 'webview2-version.json');

if (!args.force && existsSync(join(args.target, 'Microsoft.Web.WebView2.Core.dll'))) {
  let current = 'unknown';
  try {
    current = JSON.parse(await readFile(marker, 'utf8')).version;
  } catch (error) {
    // A missing marker only makes the log line less precise.
  }
  console.log('webview2: already present (' + current + '), pass --force to refresh');
  process.exit(0);
}

const version = await resolveVersion(args.version);
console.log('webview2: downloading Microsoft.Web.WebView2 ' + version);
const nupkg = await fetchBuffer(FEED + '/' + version + '/microsoft.web.webview2.' + version + '.nupkg');
console.log('webview2: ' + (nupkg.length / 1024 / 1024).toFixed(1) + ' MB, unpacking');

const wanted = {
  'lib/net462/Microsoft.Web.WebView2.Core.dll': 'Microsoft.Web.WebView2.Core.dll',
  'lib/net462/Microsoft.Web.WebView2.WinForms.dll': 'Microsoft.Web.WebView2.WinForms.dll',
  'runtimes/win-x64/native/WebView2Loader.dll': 'WebView2Loader.dll',
  'runtimes/win-x86/native/WebView2Loader.dll': 'WebView2Loader.x86.dll',
};

const entries = readZipEntries(nupkg);
await mkdir(args.target, { recursive: true });
const written = [];
for (const entry of entries) {
  const target = wanted[entry.name];
  if (!target) continue;
  const data = extractEntry(nupkg, entry);
  await writeFile(join(args.target, target), data);
  written.push(target + ' (' + data.length + ' bytes)');
}

for (const required of ['Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll']) {
  if (!existsSync(join(args.target, required))) {
    throw new Error('package did not provide ' + required);
  }
}

await writeFile(marker, JSON.stringify({ version: version, unpackedAt: new Date().toISOString() }, null, 2) + '\n');

// NuGet drops license files beside the package; keep the notice with the payload.
for (const candidate of ['LICENSE.txt', 'NOTICE.txt', 'ThirdPartyNotices.txt']) {
  const entry = entries.find((e) => e.name === candidate);
  if (entry) {
    await writeFile(join(args.target, candidate), extractEntry(nupkg, entry));
  }
}

const extra = await readdir(args.target);
console.log('webview2: ' + version + ' -> ' + args.target);
console.log('webview2: files ' + extra.join(', '));
