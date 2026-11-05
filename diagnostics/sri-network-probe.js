// Run with the Node bundled in the Worker image; no credentials or saved session.
const { chromium } = require('/app/.playwright/package');
const https = require('https');
const dns = require('dns').promises;
const crypto = require('crypto');
const url = 'https://srienlinea.sri.gob.ec/sri-en-linea/inicio/NAT';
const fingerprint = value => crypto.createHash('sha256').update(value).digest('hex').slice(0, 16);
const safeUrl = value => { try { const u = new URL(value); return { origin: u.origin === 'https://srienlinea.sri.gob.ec' ? u.origin : 'other', urlHash: fingerprint(value), isInitial: value === url }; } catch { return { origin: 'unavailable' }; } };
const emit = (event, data = {}) => console.log(JSON.stringify({ event, ...data }));
async function direct(family) {
  return new Promise(resolve => {
    const started = Date.now();
    const req = https.get(url, { family, agent: false, timeout: 45000 }, res => {
      const s = res.socket;
      emit('direct_response', { family, ms: Date.now()-started, status: res.statusCode, httpVersion: res.httpVersion, remoteAddress: s.remoteAddress, tls: s.getProtocol(), alpn: s.alpnProtocol || 'none', authorized: s.authorized, location: res.headers.location ? safeUrl(new URL(res.headers.location, url).href) : null });
      res.resume(); resolve();
    });
    req.on('socket', s => { s.on('secureConnect', () => emit('direct_tls', { family, ms: Date.now()-started, remoteAddress: s.remoteAddress, tls: s.getProtocol(), alpn: s.alpnProtocol || 'none', authorized: s.authorized })); });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', e => { emit('direct_error', { family, ms: Date.now()-started, code: e.code || e.message }); resolve(); });
  });
}
async function browserProbe(name, args) {
  const started = Date.now();
  const netlog = `/tmp/sri-chromium-${name}-${Date.now()}.json`;
  const browser = await chromium.launch({ headless: true, timeout: 30000, args: [...args, `--log-net-log=${netlog}`] });
  const context = await browser.newContext();
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  let mainFrame;
  cdp.on('Page.frameNavigated', p => { if (!p.frame.parentId) mainFrame = p.frame.id; });
  await cdp.send('Page.enable');
  mainFrame = (await cdp.send('Page.getFrameTree')).frameTree.frame.id;
  cdp.on('Network.requestWillBeSent', p => { if (p.type !== 'Document' || p.frameId !== mainFrame) return; emit('browser_request', { test: name, ms: Date.now()-started, url: safeUrl(p.request.url), redirect: p.redirectResponse ? { status: p.redirectResponse.status, protocol: p.redirectResponse.protocol, url: safeUrl(p.redirectResponse.url) } : null }); });
  cdp.on('Network.responseReceived', p => { if (p.type !== 'Document' || p.frameId !== mainFrame) return; const r = p.response; emit('browser_response', { test: name, ms: Date.now()-started, status: r.status, url: safeUrl(r.url), protocol: r.protocol, remoteAddress: r.remoteIPAddress, remotePort: r.remotePort, securityState: r.securityState, tls: r.securityDetails && r.securityDetails.protocol, connectionReused: r.connectionReused }); });
  cdp.on('Network.loadingFailed', p => { if (p.type === 'Document') emit('browser_failure', { test: name, ms: Date.now()-started, error: p.errorText, canceled: p.canceled, blockedReason: p.blockedReason }); });
  try { const response = await page.goto(url, { waitUntil: 'commit', timeout: 45000 }); emit('browser_done', { test: name, ms: Date.now()-started, status: response && response.status(), finalUrl: safeUrl(page.url()) }); }
  catch (e) { emit('browser_error', { test: name, ms: Date.now()-started, error: /net::ERR_[A-Z0-9_]+/.exec(e.message)?.[0] || (e.name === 'TimeoutError' ? 'timeout' : 'other'), finalUrl: safeUrl(page.url()) }); }
  finally { await browser.close(); emit('browser_netlog', { test: name, path: netlog }); }
}
(async () => {
  emit('start', { initialUrl: url, initialHash: fingerprint(url), proxyEnvironmentPresent: Object.fromEntries(['HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','http_proxy','https_proxy','all_proxy','no_proxy'].map(k => [k, !!process.env[k]])) });
  try { emit('dns', { addresses: await dns.lookup('srienlinea.sri.gob.ec', { all: true, verbatim: true }) }); } catch(e) { emit('dns_error', { code: e.code }); }
  await direct(4); await direct(6);
  for (const [name, args] of [['baseline', []], ['http1', ['--disable-http2', '--disable-quic']], ['ipv4', ['--host-resolver-rules=MAP srienlinea.sri.gob.ec ~NOTFOUND , EXCLUDE localhost']], ['direct', ['--no-proxy-server']]]) {
    // IPv4 is tested separately below using the actual DNS result.
    if (name === 'ipv4') {
      const addresses = await dns.lookup('srienlinea.sri.gob.ec', { family: 4, all: true }).catch(() => []);
      if (!addresses.length) { emit('browser_skipped', { test: name, reason: 'no_ipv4' }); continue; }
      args[0] = `--host-resolver-rules=MAP srienlinea.sri.gob.ec ${addresses[0].address}`;
    }
    await browserProbe(name, args);
  }
})().catch(e => { emit('fatal', { name: e.name, message: e.message }); process.exitCode = 1; });
