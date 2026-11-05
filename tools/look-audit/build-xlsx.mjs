// Builds out/<runId>/look-audit.xlsx from pages.json plus optional scores/*.json.
// Usage: node build-xlsx.mjs <runId>   (defaults to the newest run in out/)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ExcelJS from 'exceljs';

const here = path.dirname(fileURLToPath(import.meta.url));
const outRoot = path.join(here, 'out');
const runId = process.argv[2] || fs.readdirSync(outRoot).filter(d => fs.existsSync(path.join(outRoot, d, 'pages.json'))).sort().at(-1);
if (!runId) { console.error('no run with pages.json found'); process.exit(1); }
const runDir = path.join(outRoot, runId);
const pages = JSON.parse(fs.readFileSync(path.join(runDir, 'pages.json'), 'utf8'));
const scoreDir = path.join(runDir, 'scores');
const scores = new Map(fs.existsSync(scoreDir)
  ? fs.readdirSync(scoreDir).filter(f => f.endsWith('.json')).map(f => JSON.parse(fs.readFileSync(path.join(scoreDir, f), 'utf8'))).map(s => [s.id, s])
  : []);

const SCORE_KEYS = ['calm', 'flow', 'hierarchy', 'scannability', 'consistency', 'overall'];
const pick = (o, p) => p.split('.').reduce((v, k) => v?.[k], o) ?? null;
const metric = (label, p, fn) => ({ label, get: (d) => { const v = pick(d, p); return fn && v !== null ? fn(v) : v; } });

const metricCols = [
  metric('screens', 'screens'), metric('words fold', 'wordsAboveFold'), metric('words', 'wordsTotal'), metric('ink', 'inkDensity'),
  metric('interactive', 'interactive'), metric('actions fold', 'actionsAboveFold'), metric('tables', 'tables'), metric('max cols', 'maxTableCols'),
  metric('font sizes', 'fontSizes', v => v.length), metric('colors', 'textColors', v => v.length),
  metric('h-overflow', 'hOverflow', v => (v ? 'yes' : 'no')), metric('overflow els', 'overflowCount'), metric('wrapped cells', 'wrappedCells'),
  metric('shared ratio', 'shared.sharedRatio'), metric('shared tables', 'shared.tablesShared'), metric('other tables', 'shared.tablesOther'),
  metric('shared h1', 'shared.headersShared'), metric('other h1', 'shared.headersOther'),
  metric('console errors', 'consoleErrors.count'), metric('failed reqs', 'failedRequests.count'),
];
const desktopOnly = ['critical', 'serious', 'moderate', 'minor'].map(k => metric(`axe ${k}`, `axe.${k}`));
const mobileOnly = [metric('small taps', 'smallTapTargets')];

const columns = [
  { header: 'id', get: r => r.id, width: 24 },
  { header: 'section', get: r => r.section, width: 14 },
  { header: 'template', get: r => r.template, width: 30 },
  { header: 'sampled URL', get: r => r.sampledUrl ?? null, width: 30 },
  { header: 'status', get: r => (r.reason ? `${r.status} (${r.reason})` : r.status), width: 12 },
  { header: 'admin layout', get: r => (r.adminLayout == null ? null : r.adminLayout ? 'yes' : 'no'), width: 8 },
  ...[...metricCols.slice(0, 4), ...desktopOnly, ...metricCols.slice(4)].map(m => ({ header: `D ${m.label}`, get: r => m.get(r.desktop), kind: m.label })),
  ...[...metricCols.slice(0, 4), ...mobileOnly, ...metricCols.slice(4)].map(m => ({ header: `M ${m.label}`, get: r => m.get(r.mobile), kind: m.label })),
  ...SCORE_KEYS.map(k => ({ header: `D ${k}`, get: r => scores.get(r.id)?.desktop?.[k] ?? null, score: true })),
  ...[...SCORE_KEYS, 'mobile'].map(k => ({ header: `M ${k}`, get: r => scores.get(r.id)?.mobile?.[k] ?? null, score: true })),
  ...['bug', 'ux', 'polish'].map(t => ({ header: `${t}s`, get: r => (scores.has(r.id) ? problems(r.id).filter(p => p.tag === t).length : null), width: 6 })),
  { header: 'problems', get: r => (scores.has(r.id) ? problems(r.id).map(p => `[${p.w}/${p.tag}] ${p.text}`).join('\n') : null), width: 80 },
];

function problems(id) {
  const s = scores.get(id);
  return [['d', s.desktop], ['m', s.mobile]].flatMap(([w, x]) => (x?.problems || []).map(p => ({ ...p, w })));
}

const wb = new ExcelJS.Workbook();
const ws = wb.addWorksheet('Pages', { views: [{ state: 'frozen', xSplit: 2, ySplit: 1 }] });
ws.columns = columns.map(c => ({ header: c.header, width: c.width ?? 10 }));
for (const r of pages) ws.addRow(columns.map(c => c.get(r)));
ws.getRow(1).font = { bold: true };
ws.getRow(1).alignment = { wrapText: true, vertical: 'top' };
ws.autoFilter = { from: { row: 1, column: 1 }, to: { row: 1, column: columns.length } };
ws.getColumn(columns.length).alignment = { wrapText: true, vertical: 'top' };

const lastRow = Math.max(2, pages.length + 1);
const colRef = i => { const l = ws.getColumn(i + 1).letter; return `${l}2:${l}${lastRow}`; };
columns.forEach((c, i) => {
  if (c.score) {
    ws.addConditionalFormatting({ ref: colRef(i), rules: [{ type: 'colorScale', cfvo: [{ type: 'num', value: 1 }, { type: 'num', value: 3 }, { type: 'num', value: 5 }], color: [{ argb: 'FFF8696B' }, { argb: 'FFFFEB84' }, { argb: 'FF63BE7B' }] }] });
  } else if (c.kind === 'screens') {
    ws.addConditionalFormatting({ ref: colRef(i), rules: [{ type: 'colorScale', cfvo: [{ type: 'min' }, { type: 'max' }], color: [{ argb: 'FF63BE7B' }, { argb: 'FFF8696B' }] }] });
  }
});

// By section
const median = xs => { const v = xs.filter(x => typeof x === 'number').sort((a, b) => a - b); if (!v.length) return null; const m = v.length >> 1; return v.length % 2 ? v[m] : +((v[m - 1] + v[m]) / 2).toFixed(2); };
const bySection = Map.groupBy(pages, p => p.section || '(none)');
const cols = [
  ['pages', rs => rs.length],
  ['captured', rs => rs.filter(r => r.status === 'ok').length],
  ...SCORE_KEYS.map(k => [`D ${k}`, rs => median(rs.map(r => scores.get(r.id)?.desktop?.[k]))]),
  ...[...SCORE_KEYS, 'mobile'].map(k => [`M ${k}`, rs => median(rs.map(r => scores.get(r.id)?.mobile?.[k]))]),
  ['D screens', rs => median(rs.map(r => r.desktop?.screens))],
  ['D words fold', rs => median(rs.map(r => r.desktop?.wordsAboveFold))],
  ['D ink', rs => median(rs.map(r => r.desktop?.inkDensity))],
  ['M screens', rs => median(rs.map(r => r.mobile?.screens))],
  ['M words fold', rs => median(rs.map(r => r.mobile?.wordsAboveFold))],
  ['M ink', rs => median(rs.map(r => r.mobile?.inkDensity))],
];
const ss = wb.addWorksheet('By section', { views: [{ state: 'frozen', xSplit: 1, ySplit: 1 }] });
ss.columns = [{ header: 'section', width: 18 }, ...cols.map(([h]) => ({ header: h, width: 10 }))];
for (const [sec, rs] of [...bySection].sort((a, b) => a[0].localeCompare(b[0]))) ss.addRow([sec, ...cols.map(([, f]) => f(rs))]);
ss.getRow(1).font = { bold: true };
ss.autoFilter = { from: { row: 1, column: 1 }, to: { row: 1, column: cols.length + 1 } };
cols.forEach(([h], i) => {
  if (!/^[DM] (calm|flow|hierarchy|scannability|consistency|overall|mobile)$/.test(h)) return;
  const l = ss.getColumn(i + 2).letter;
  ss.addConditionalFormatting({ ref: `${l}2:${l}${Math.max(2, bySection.size + 1)}`, rules: [{ type: 'colorScale', cfvo: [{ type: 'num', value: 1 }, { type: 'num', value: 3 }, { type: 'num', value: 5 }], color: [{ argb: 'FFF8696B' }, { argb: 'FFFFEB84' }, { argb: 'FF63BE7B' }] }] });
});

// Rubric
const rs = wb.addWorksheet('Rubric');
rs.getColumn(1).width = 120;
const rubricFile = path.join(here, 'rubric.md');
const lines = fs.existsSync(rubricFile) ? fs.readFileSync(rubricFile, 'utf8').split('\n') : ['No rubric found: add tools/look-audit/rubric.md and rebuild.'];
for (const l of lines) rs.addRow([l]).alignment = { wrapText: true, vertical: 'top' };

const file = path.join(runDir, 'look-audit.xlsx');
await wb.xlsx.writeFile(file);
console.log(`${file}: ${pages.length} pages, ${scores.size} scored`);
