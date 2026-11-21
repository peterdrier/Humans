// Screenshots a few named pages at desktop and phone width, with axe findings, for looking at a change.
// Usage: LOOK_AUDIT_BASE_URL=https://<pr>.n.burn.camp node shoot.mjs <outName> <persona|anon> <name=/path>...
// Signs in through /dev/login/<persona> (previews and local Development only). Writes out/<outName>/.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// ESM import ignores NODE_PATH, which is how the globally installed playwright resolves here.
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const axeSource = fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');

const BASE = (process.env.LOOK_AUDIT_BASE_URL || 'http://localhost:5080').replace(/\/$/, '');
const [outName, persona, ...pages] = process.argv.slice(2);
if (!outName || !persona || pages.length === 0 || pages.some(p => !/^[^=/]+=\//.test(p))) {
  console.error('Usage: node shoot.mjs <outName> <persona|anon> <name=/path>...');
  process.exit(1);
}
const out = path.join(path.dirname(fileURLToPath(import.meta.url)), 'out', outName);
fs.mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
const report = [];
for (const [width, height, label] of [[1440, 900, 'desktop'], [390, 844, 'mobile']]) {
  // Same contexts as capture.mjs, so these shots compare with the audit's anchors.
  const mobile = label === 'mobile';
  const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1, isMobile: mobile, hasTouch: mobile });
  const page = await context.newPage();
  if (persona !== 'anon') await page.goto(`${BASE}/dev/login/${persona}`, { waitUntil: 'load' });
  for (const entry of pages) {
    const cut = entry.indexOf('=');
    const [name, route] = [entry.slice(0, cut), entry.slice(cut + 1)];
    const response = await page.goto(BASE + route, { waitUntil: 'load' });
    // A page that never goes idle (polling, long-lived requests) is still shot.
    await page.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});
    await page.screenshot({ path: path.join(out, `${name}-${label}-fold.png`) });
    await page.screenshot({ path: path.join(out, `${name}-${label}.png`), fullPage: true });
    await page.evaluate(axeSource);
    const violations = await page.evaluate(async () => (await window.axe.run()).violations
      .filter(v => v.impact === 'serious' || v.impact === 'critical')
      .map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length, targets: v.nodes.slice(0, 10).map(n => n.target.join(' ')) })));
    const overflow = await page.evaluate(() => Math.max(0, document.documentElement.scrollWidth - document.documentElement.clientWidth));
    report.push({ name, label, status: response?.status(), landed: page.url().replace(BASE, ''), overflow, axe: violations });
  }
  await context.close();
}
await browser.close();
fs.writeFileSync(path.join(out, 'report.json'), JSON.stringify(report, null, 1));
for (const r of report) console.log(r.name, r.label, r.status, r.landed, 'overflow', r.overflow, JSON.stringify(r.axe));
