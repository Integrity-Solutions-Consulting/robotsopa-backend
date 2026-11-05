// Public, unauthenticated diagnostic. Run inside the affected Worker image.
const https = require('https');
const { chromium } = require('/app/.playwright/package');
const target = 'https://srienlinea.sri.gob.ec/sri-en-linea/inicio/NAT';
const timeout = 45000;
const emit = (event, data = {}) => console.log(JSON.stringify({ event, ...data }));
const allow = new Set(['host', 'connection', 'accept', 'accept-encoding', 'accept-language', 'user-agent', 'upgrade-insecure-requests', 'sec-fetch-site', 'sec-fetch-mode', 'sec-fetch-user', 'sec-fetch-dest', 'sec-ch-ua', 'sec-ch-ua-mobile', 'sec-ch-ua-platform']);
function headersSafe(headers) {
  return Object.fromEntries(Object.entries(headers).filter(([k]) => allow.has(k.toLowerCase())));
}
async function nodeRequest(test, headers, agent) {
  return new Promise(resolve => {
    const start = Date.now(); let settled = false;
    const finish = data => { if (settled) return; settled = true; clearTimeout(timer); emit('node_result', { test, ms: Date.now() - start, ...data }); resolve(data); };
    const req = https.get(target, { family: 4, agent, headers, ALPNProtocols: ['http/1.1'] }, res => {
      const socket = res.socket;
      finish({ outcome: 'response', status: res.statusCode, httpVersion: res.httpVersion, remoteAddress: socket.remoteAddress, tls: socket.getProtocol(), alpn: socket.alpnProtocol || 'none', authorized: socket.authorized, reusedSocket: req.reusedSocket });
      res.resume();
    });
    req.on('socket', socket => socket.once('secureConnect', () => emit('node_tls', { test, remoteAddress: socket.remoteAddress, tls: socket.getProtocol(), alpn: socket.alpnProtocol || 'none', authorized: socket.authorized })));
    req.on('finish', () => emit('node_request', { test, headers: headersSafe(req.getHeaders()) }));
    req.on('error', e => finish({ outcome: 'error', code: e.code || 'error' }));
    const timer = setTimeout(() => { finish({ outcome: 'timeout' }); req.destroy(); }, timeout);
  });
}
async function browserRequest(round) {
  const start = Date.now(); const browser = await chromium.launch({ headless: true, timeout: 30000 });
  try {
    const context = await browser.newContext(); const page = await context.newPage();
    const cdp = await context.newCDPSession(page); await cdp.send('Network.enable');
    let requestId;
    cdp.on('Network.requestWillBeSent', p => { if (p.type === 'Document' && p.request.url === target && !requestId) { requestId = p.requestId; emit('browser_request', { round, headers: headersSafe(p.request.headers) }); } });
    // ExtraInfo can precede requestWillBeSent. Collect then correlate by requestId.
    const extra = new Map();
    cdp.on('Network.requestWillBeSentExtraInfo', p => { extra.set(p.requestId, headersSafe(p.headers)); });
    cdp.on('Network.responseReceived', p => { if (p.requestId !== requestId) return; const r = p.response; emit('browser_response', { round, status: r.status, protocol: r.protocol, remoteAddress: r.remoteIPAddress, tls: r.securityDetails && r.securityDetails.protocol, connectionReused: r.connectionReused }); });
    try { const response = await page.goto(target, { waitUntil: 'commit', timeout }); emit('browser_result', { round, outcome: 'response', status: response && response.status(), ms: Date.now() - start }); }
    catch (e) { emit('browser_result', { round, outcome: e.name === 'TimeoutError' ? 'timeout' : 'error', code: /net::ERR_[A-Z0-9_]+/.exec(e.message)?.[0] || null, ms: Date.now() - start }); }
    if (requestId && extra.has(requestId)) emit('browser_wire_headers', { round, headers: extra.get(requestId) });
    else emit('browser_wire_headers_unavailable', { round });
  } finally { await browser.close(); }
}
(async () => {
  emit('start', { node: process.version, timeoutMs: timeout, note: 'only first public response; no login, redirects or challenge interaction' });
  // Independent one-variable comparisons; no browser identity or security header spoofing.
  const cases = [
    ['node_default', {}],
    ['node_accept', { Accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' }],
    ['node_encoding', { 'Accept-Encoding': 'gzip, deflate, br' }],
    ['node_language', { 'Accept-Language': 'en-US,en;q=0.9' }],
    ['node_keepalive_first', {}], ['node_keepalive_second', {}]
  ];
  for (let round = 1; round <= 2; round++) {
    const agent = new https.Agent({ keepAlive: true, maxSockets: 1, maxCachedSessions: 0 });
    try {
      // Alternate order to expose transient service or rate changes.
      if (round === 2) await browserRequest(round);
      for (const [name, headers] of cases) await nodeRequest(`${round}_${name}`, headers, name.startsWith('node_keepalive') ? agent : false);
      if (round === 1) await browserRequest(round);
    } finally { agent.destroy(); }
  }
  emit('complete');
})().catch(e => { emit('fatal', { name: e.name, code: e.code || null }); process.exitCode = 1; });
