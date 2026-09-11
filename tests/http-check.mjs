/**
 * Verifies the DSH web surface that DSH Desktop hosts:
 *   1. a bare request is refused (the token fence exists),
 *   2. the tokenized URL sets the auth cookie,
 *   3. the cookie yields the real application document,
 *   4. the API gateway answers on the same origin.
 *
 * Usage: node http-check.mjs --url "http://127.0.0.1:PORT/?token=..." [--json]
 */
import http from 'node:http';
import { URL } from 'node:url';

function parseArgs(argv) {
  const args = { url: null, json: false };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--url') args.url = argv[++i];
    else if (argv[i] === '--json') args.json = true;
  }
  if (!args.url) throw new Error('usage: node http-check.mjs --url <tokenized-url>');
  return args;
}

function get(target, cookie) {
  return new Promise((resolve, reject) => {
    const url = new URL(target);
    const headers = { 'User-Agent': 'dsh-desktop-smoke' };
    if (cookie) headers.Cookie = cookie;
    const req = http.get(
      { hostname: url.hostname, port: url.port, path: url.pathname + url.search, headers, timeout: 20000 },
      (res) => {
        const chunks = [];
        res.on('data', (chunk) => chunks.push(chunk));
        res.on('end', () =>
          resolve({
            status: res.statusCode,
            location: res.headers.location,
            cookies: res.headers['set-cookie'] || [],
            contentType: res.headers['content-type'] || '',
            body: Buffer.concat(chunks),
          }),
        );
      },
    );
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('request timed out')));
  });
}

function cookieHeader(setCookies) {
  return setCookies.map((value) => value.split(';')[0]).join('; ');
}

const args = parseArgs(process.argv.slice(2));
const checks = [];

function record(name, ok, detail) {
  checks.push({ name, ok, detail });
}

const base = new URL(args.url);
const origin = base.origin;

const anonymous = await get(origin + '/');
record('匿名请求被拒绝', anonymous.status === 401, 'status=' + anonymous.status);

const handshake = await get(args.url);
record('token 握手返回跳转', handshake.status === 301 || handshake.status === 302 || handshake.status === 303,
  'status=' + handshake.status + ' location=' + (handshake.location || '-'));
const cookie = cookieHeader(handshake.cookies);
record('握手下发会话 Cookie', cookie.length > 0 && /dsh-auth-/.test(cookie), cookie ? cookie.split('=')[0] + '=...' : '(none)');

const authorized = await get(origin + '/', cookie);
const html = authorized.body.toString('utf8');
record('Cookie 换取应用文档', authorized.status === 200 && html.length > 500,
  'status=' + authorized.status + ' bytes=' + authorized.body.length);
record('文档包含前端引导标记', /__DSH_BOOT__|<title>/.test(html),
  (html.match(/<title>([^<]*)<\/title>/) || [])[1] || 'no title tag');

const api = await get(origin + '/api', cookie);
record('API 网关可达', api.status < 500, 'status=' + api.status + ' type=' + api.contentType);

const failed = checks.filter((check) => !check.ok);
if (args.json) {
  console.log(JSON.stringify({ checks, ok: failed.length === 0 }, null, 2));
} else {
  for (const check of checks) {
    console.log((check.ok ? '  PASS  ' : '  FAIL  ') + check.name + '  (' + check.detail + ')');
  }
}
process.exit(failed.length === 0 ? 0 : 1);
