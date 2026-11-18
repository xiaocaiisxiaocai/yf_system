const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
(async () => {
  // 默认使用已安装的 Chrome；本机只装了 Edge 时用 YF_BROWSER_CHANNEL=msedge 复用同一套步骤。
  const channel = process.env.YF_BROWSER_CHANNEL || 'chrome';
  const server = await chromium.launchServer({ channel, headless: true, args: [
    '--auth-server-allowlist=__yf_no_integrated_auth.invalid',
    '--auth-negotiate-delegate-allowlist=__yf_no_integrated_auth.invalid',
  ] });
  fs.writeFileSync(path.join(process.env.YF_BROWSER_EVIDENCE_DIR, 'browser-host.private.json'), JSON.stringify({ endpoint: server.wsEndpoint() }));
  let closing = false;
  const close = async () => { if (closing) return; closing = true; await server.close(); process.exit(0); };
  process.stdin.resume();
  process.stdin.on('end', close);
  process.on('SIGTERM', close);
  process.on('SIGINT', close);
})().catch(error => { console.error(error.message); process.exitCode = 1; });
