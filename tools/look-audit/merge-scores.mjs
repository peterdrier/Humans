// Merges independent scoring passes (out/<runId>/scores-<pass>/*.json) into out/<runId>/scores/*.json.
// Each metric becomes the median across passes. A page where two passes differ by more than 1 point
// on any metric is listed as unstable, so a further pass can be run for it before merging again.
// Problems come from the first pass that has them, to avoid near-duplicate wording across passes.
// Usage: node merge-scores.mjs <runId>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runDir = path.join(path.dirname(fileURLToPath(import.meta.url)), 'out', process.argv[2] ?? '');
const passDirs = fs.readdirSync(runDir).filter(d => /^scores-/.test(d)).sort().map(d => path.join(runDir, d));
if (!passDirs.length) { console.error('no scores-<pass> folders in', runDir); process.exit(1); }

const median = xs => {
  const s = xs.filter(x => typeof x === 'number').sort((a, b) => a - b);
  if (!s.length) return undefined;
  const m = Math.floor(s.length / 2);
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
};

const ids = new Set(passDirs.flatMap(d => fs.readdirSync(d).filter(f => f.endsWith('.json')).map(f => f.slice(0, -5))));
const outDir = path.join(runDir, 'scores');
fs.mkdirSync(outDir, { recursive: true });
const unstable = [];

for (const id of ids) {
  const passes = passDirs.map(d => path.join(d, `${id}.json`)).filter(fs.existsSync).map(f => JSON.parse(fs.readFileSync(f, 'utf8')));
  const merged = { id, passes: passes.length };
  for (const w of ['desktop', 'mobile']) {
    const ws = passes.map(p => p[w]).filter(Boolean);
    if (!ws.length) continue;
    const keys = [...new Set(ws.flatMap(x => Object.keys(x)))].filter(k => k !== 'problems');
    merged[w] = Object.fromEntries(keys.map(k => [k, median(ws.map(x => x[k]))]));
    merged[w].problems = ws.find(x => x.problems?.length)?.problems ?? [];
    const spread = keys.filter(k => {
      const v = ws.map(x => x[k]).filter(x => typeof x === 'number');
      return Math.max(...v) - Math.min(...v) > 1;
    });
    if (spread.length) unstable.push(`${id} ${w}: ${spread.join(', ')}`);
  }
  merged.unstable = unstable.some(u => u.startsWith(`${id} `));
  fs.writeFileSync(path.join(outDir, `${id}.json`), JSON.stringify(merged, null, 2));
}

console.log(`merged ${ids.size} pages from ${passDirs.length} passes; unstable: ${unstable.length}`);
unstable.forEach(u => console.log('  ' + u));
