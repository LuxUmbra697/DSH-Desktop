/**
 * Verifies the browser-trust fence for an authority that is not the bound host,
 * which is what a phone, tablet, or another machine on the network presents.
 *
 *   node lan-check.mjs --url <tokenized-url> --authority <ip:port>
 *
 * Expected outcome against a default DSH Desktop install: the loopback token
 * handshake succeeds, while the same request carrying a foreign Host/Origin is
 * refused. This release rejects wildcard binds, so remote devices are expected to
 * reach a loopback bind through a tunnel or reverse proxy whose authority is
 * listed in `trustedHosts`.
 */
import http from 'node:http';
import { URL } from 'node:url';

function parseArgs(argv) {
  const args = { url: null, authority: null };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--url') args.url = argv[++i];
    else if (argv[i] === '--authority') args.authority = argv[++i];
  }
  if (!args.url || !args.authority) throw new Error('usage: node lan-check.mjs --url <tokenized-url> --authority <ip:port>');
  return args;
}

function request(target, authority, cookie) {
  return new Promise((resolve, reject) => {
    const url = new URL(target);
    const headers = { 'User-Agent': 'dsh-desktop-lan-check' };
    if (authority) {
      headers.Host = authority;
      headers.Origin = 'http://' + authority;
    }
    if (cookie) headers.Cookie = cookie;
    const req = http.get(
      { hostname: url.hostname, port: url.port, path: url.pathname + url.search, headers, timeout: 20000 },
      (res) => {
        const chunks = [];
        res.on('data', (chunk) => chunks.push(chunk));
        res.on('end', () => resolve({ status: res.statusCode, cookies: res.headers['set-cookie'] || [], body: Buffer.concat(chunks) }));
      },
    );
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('timeout')));
  });
}

const args = parseArgs(process.argv.slice(2));
const checks = [];
const record = (name, ok, detail) => checks.push({ name, ok, detail });

const loopback = await request(args.url, null);
record('回环地址上的 token 握手成功', loopback.status === 303 || loopback.status === 302, 'status=' + loopback.status);
const cookie = loopback.cookies.map((value) => value.split(';')[0]).join('; ');
record('握手下发会话 Cookie', /dsh-auth-/.test(cookie), cookie ? cookie.split('=')[0] + '=...' : '(none)');

const foreign = await request(args.url, args.authority);
record('外来 authority 被信任围栏拒绝', foreign.status === 401 || foreign.status === 403, 'status=' + foreign.status);
record('被拒绝时不下发 Cookie', foreign.cookies.length === 0, 'cookies=' + foreign.cookies.length);

if (cookie) {
  const foreignWithCookie = await request(args.url.split('?')[0] + '/', args.authority, cookie);
  record('持 Cookie 的外来 authority 仍被拒绝', foreignWithCookie.status === 401 || foreignWithCookie.status === 403,
    'status=' + foreignWithCookie.status);

  const localWithCookie = await request(args.url.split('?')[0] + '/', null, cookie);
  record('回环地址持 Cookie 可取到应用文档', localWithCookie.status === 200 && localWithCookie.body.length > 500,
    'status=' + localWithCookie.status + ' bytes=' + localWithCookie.body.length);
}

for (const check of checks) {
  console.log((check.ok ? '  PASS  ' : '  FAIL  ') + check.name + '  (' + check.detail + ')');
}
process.exit(checks.some((check) => !check.ok) ? 1 : 0);
