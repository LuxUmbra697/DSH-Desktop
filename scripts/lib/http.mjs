/**
 * HTTPS helper with redirect handling for the build scripts.
 *
 * Some locked down Windows hosts cannot negotiate TLS through schannel, so the
 * build never relies on curl.exe or the Windows certificate store: downloads go
 * through Node's own TLS stack.
 */
import https from 'node:https';
import http from 'node:http';
import { createWriteStream } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';

export function fetchBuffer(url, options) {
  const settings = options || {};
  const redirects = settings.redirects === undefined ? 6 : settings.redirects;
  const headers = settings.headers || {};
  return new Promise((resolve, reject) => {
    const mod = url.startsWith('http://') ? http : https;
    const req = mod.get(
      url,
      { timeout: 120000, headers: Object.assign({ 'User-Agent': 'dsh-desktop-build' }, headers) },
      (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          if (redirects <= 0) {
            reject(new Error('too many redirects for ' + url));
            return;
          }
          const next = new URL(res.headers.location, url).toString();
          fetchBuffer(next, { redirects: redirects - 1, headers: headers }).then(resolve, reject);
          return;
        }
        if (res.statusCode !== 200) {
          res.resume();
          reject(new Error('HTTP ' + res.statusCode + ' for ' + url));
          return;
        }
        const chunks = [];
        res.on('data', (chunk) => chunks.push(chunk));
        res.on('end', () => resolve(Buffer.concat(chunks)));
        res.on('error', reject);
      },
    );
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('timeout for ' + url)));
  });
}

export async function fetchJson(url) {
  return JSON.parse((await fetchBuffer(url)).toString('utf8'));
}

export async function downloadTo(url, target) {
  const buffer = await fetchBuffer(url);
  await mkdir(dirname(target), { recursive: true });
  await new Promise((resolve, reject) => {
    const stream = createWriteStream(target);
    stream.on('error', reject);
    stream.on('finish', resolve);
    stream.end(buffer);
  });
  return buffer.length;
}
