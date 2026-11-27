#!/usr/bin/env node
// Horizontal slop & drift telemetry for the Saturday Drift Radar (docs/maintenance/drift-radar.md).
// Zero external dependencies; reads git trees directly via `git cat-file --batch` so baseline
// comparisons never touch or modify the working tree.

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'Migrations', 'wwwroot']);
const ANALYZER_HOOK_NAMES = new Set([
  'OnCompilationStart',
  'AnalyzeInvocation',
  'AnalyzeNamedType',
  'AnalyzeOperation',
  'AnalyzePropertyReference',
  'AnalyzeAssignment',
  'Report',
]);

export function shouldAnalyzePath(relPath) {
  const norm = relPath.replace(/\\/g, '/');
  if (norm === 'AGENTS.md') return true;
  if (norm.startsWith('memory/') && norm.endsWith('.md')) return true;
  if (!norm.startsWith('src/') && !norm.startsWith('tests/')) return false;
  const parts = norm.split('/');
  if (parts.some(p => SKIP_DIRS.has(p))) return false;
  if (norm.endsWith('.Designer.cs') || norm.endsWith('.g.cs')) return false;
  return norm.endsWith('.cs') || norm.endsWith('.cshtml');
}

function countParams(paramText) {
  const inner = paramText.trim();
  if (!inner) return 0;
  let depth = 0;
  let commas = 0;
  for (const ch of inner) {
    if (ch === '<' || ch === '(' || ch === '[') depth++;
    else if (ch === '>' || ch === ')' || ch === ']') depth--;
    else if (ch === ',' && depth === 0) commas++;
  }
  return commas + 1;
}

function stripCommentsAndStrings(line) {
  return line.replace(/\/\/.*$/, '').replace(/"(?:\\.|[^"\\])*"/g, '""');
}

function topN(items, keyFn, limit = 5) {
  return [...items].sort((a, b) => keyFn(b) - keyFn(a)).slice(0, limit);
}

/**
 * Pure metric analyzer over an iterable of `{ path, content }` entries.
 */
export function analyzeFiles(files) {
  let srcCsLoc = 0;
  let srcCshtmlLoc = 0;
  let testCsLoc = 0;

  const longLocalVars = [];
  const longMethodNames = [];
  const longTestNames60 = [];
  const longTestNames80 = [];
  const longMethods = [];
  const bigServiceCtors = [];
  const bigPositionalRecords = [];
  const bespokeResultTypes = [];
  const giantTestFiles = [];
  const localizerStringFormat = [];
  const controllerCatches = [];
  const manualCurrency = [];
  const staticHelperFiles = new Map(); // methodName -> Set<path>
  const serviceGroups = new Map(); // dir/BaseName -> { files: [{path, lines}], totalLines }

  let totalTestMethods = 0;
  let totalBroadCatch = 0;
  const broadCatchFiles = [];
  const testScaffoldingFiles = [];
  const testMixedAssertFiles = [];
  let razorInlineJsLines = 0;
  let razorInlineStyleLines = 0;
  const razorBigJsFiles = [];
  const razorBigStyleFiles = [];

  let memoryAtoms = 0;
  let memoryBytes = 0;
  let memoryIndexBytes = 0;
  let memoryHardRules = 0;
  let agentsMdLines = 0;

  for (const file of files) {
    const rf = file.path.replace(/\\/g, '/');
    const content = file.content;
    const lines = content.split('\n');
    const lineCount = lines.length;

    if (rf === 'AGENTS.md') {
      agentsMdLines = lineCount;
      continue;
    }

    if (rf.startsWith('memory/') && rf.endsWith('.md')) {
      const bytes = Buffer.byteLength(content, 'utf8');
      if (rf === 'memory/INDEX.md') {
        memoryIndexBytes = bytes;
      } else if (rf !== 'memory/META.md') {
        memoryAtoms++;
        memoryBytes += bytes;
        if (/HARD RULE/i.test(content)) memoryHardRules++;
      }
      continue;
    }

    if (rf.startsWith('tests/') && rf.endsWith('.cs')) {
      testCsLoc += lineCount;
      if (lineCount >= 1000) {
        giantTestFiles.push({ path: rf, lines: lineCount });
      }
      const scaffoldHits = (content.match(/UseInMemoryDatabase\(|new\s+DefaultHttpContext\b|ControllerContext\s*=\s*new\s+ControllerContext|Substitute\.For<IStringLocalizer</g) || []).length;
      if (scaffoldHits > 0) {
        testScaffoldingFiles.push({ path: rf, count: scaffoldHits });
      }
      const assertCount = (content.match(/\bAssert\./g) || []).length;
      const shouldCount = (content.match(/\.Should\(\)/g) || []).length;
      if (assertCount > 0 && shouldCount > 0) {
        testMixedAssertFiles.push({ path: rf, count: assertCount + shouldCount, assertCount, shouldCount });
      }
      for (let i = 0; i < lines.length; i++) {
        const m = lines[i].match(/^\s*public\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z0-9_]+)\s*\(/);
        if (!m) continue;
        totalTestMethods++;
        const name = m[1];
        if (name.length >= 60) {
          const item = { path: rf, line: i + 1, name, len: name.length };
          longTestNames60.push(item);
          if (name.length >= 80) longTestNames80.push(item);
        }
      }
      continue;
    }

    if (rf.startsWith('src/') && rf.endsWith('.cshtml')) {
      srcCshtmlLoc += lineCount;
      let jsLines = 0;
      for (const m of content.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
        if (/\bsrc=|\bapplication\/(?:json|ld\+json)/i.test(m[1])) continue;
        const body = m[2].trim();
        if (body) jsLines += body.split('\n').length;
      }
      if (jsLines > 0) {
        razorInlineJsLines += jsLines;
        if (jsLines >= 80) razorBigJsFiles.push({ path: rf, lines: jsLines });
      }

      let cssLines = 0;
      for (const m of content.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)) {
        const body = m[1].trim();
        if (body) cssLines += body.split('\n').length;
      }
      if (cssLines > 0) {
        razorInlineStyleLines += cssLines;
        if (cssLines >= 30) razorBigStyleFiles.push({ path: rf, lines: cssLines });
      }

      for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (/string\.Format\s*\([^)]*[Ll]ocalizer\[/.test(line)) {
          localizerStringFormat.push({ path: rf, line: i + 1 });
        }
        if (/\.ToString\s*\(\s*"(?:N2|F2|C2|0\.00)"/.test(line)) {
          manualCurrency.push({ path: rf, line: i + 1 });
        }
      }
      continue;
    }

    if (!rf.startsWith('src/') || !rf.endsWith('.cs')) continue;
    srcCsLoc += lineCount;

    // Logical service grouping (summing partial files like FooService.Admin.cs into FooService).
    const baseName = path.posix.basename(rf);
    if (/\/Services\//.test(rf) && !baseName.startsWith('I') && /Service(?:\.[A-Za-z0-9]+)?\.cs$/.test(baseName)) {
      const rootName = baseName.split('.')[0];
      const groupKey = `${path.posix.dirname(rf)}/${rootName}`;
      if (!serviceGroups.has(groupKey)) serviceGroups.set(groupKey, { key: groupKey, files: [], totalLines: 0 });
      const group = serviceGroups.get(groupKey);
      group.files.push({ path: rf, lines: lineCount });
      group.totalLines += lineCount;
    }

    // Positional records with >= 12 parameters.
    for (const m of content.matchAll(/\b(?:public|internal|private|protected)?\s*(?:sealed\s+|readonly\s+)?record(?:\s+(?:class|struct))?\s+([A-Z][A-Za-z0-9_]*)\s*\(([\s\S]*?)\)\s*(?::|;|where|\{)/g)) {
      const name = m[1];
      const body = m[2];
      if (body.includes('{')) continue;
      const params = countParams(body);
      if (params >= 12) {
        bigPositionalRecords.push({ path: rf, name, params });
      }
    }

    // Primary constructors on classes with >= 10 injected dependencies.
    for (const m of content.matchAll(/\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+)*class\s+([A-Z][A-Za-z0-9_]*)\s*\(([\s\S]{80,2500}?)\)\s*(?::|\{)/g)) {
      const name = m[1];
      const body = m[2];
      if (body.includes('{') || body.includes(';')) continue;
      const deps = countParams(body);
      if (deps >= 10) bigServiceCtors.push({ path: rf, name, deps });
    }

    // Bespoke *Result / *Outcome types.
    for (const m of content.matchAll(/\b(?:public|internal)\s+(?:sealed\s+|readonly\s+)?(?:record(?:\s+(?:class|struct))?|class|struct)\s+([A-Z][A-Za-z0-9_]*(?:Result|Outcome))\b/g)) {
      bespokeResultTypes.push({ path: rf, name: m[1] });
    }

    // Duplicate private/internal static helper names.
    for (const m of content.matchAll(/^\s*(?:private|internal)\s+static\s+[\w<>\?\s]+\s+([A-Z][A-Za-z0-9_]{4,})\s*\(/gm)) {
      const name = m[1];
      if (ANALYZER_HOOK_NAMES.has(name)) continue;
      if (!staticHelperFiles.has(name)) staticHelperFiles.set(name, new Set());
      staticHelperFiles.get(name).add(rf);
    }

    const isController = /\/Controllers\//.test(rf);
    let fileCatchHits = 0;

    // Line-level & method-body scan.
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const trimmed = line.trim();
      if (trimmed.startsWith('//')) continue;

      for (const vm of line.matchAll(/\bvar\s+([a-z_][A-Za-z0-9_]{27,})\s*=/g)) {
        longLocalVars.push({ path: rf, line: i + 1, name: vm[1], len: vm[1].length });
      }

      if (/string\.Format\s*\([^)]*[Ll]ocalizer\[/.test(line)) {
        localizerStringFormat.push({ path: rf, line: i + 1 });
      }
      if (/\.ToString\s*\(\s*"(?:N2|F2|C2|0\.00)"/.test(line)) {
        manualCurrency.push({ path: rf, line: i + 1 });
      }
      if (/\bcatch\s*\([^)]*Exception\b/.test(line)) {
        totalBroadCatch++;
        fileCatchHits++;
      }

      // Traditional constructors with >= 10 deps & methods >= 120 lines / >= 45-char names.
      if (/\b(class|record|struct|interface|enum|namespace)\b/.test(line)) continue;
      const ctorMatch = line.match(/^(\s+)(?:public|internal|private|protected)\s+([A-Z][A-Za-z0-9_]*)\s*\(/);
      if (ctorMatch) {
        let j = i;
        let sig = '';
        let foundBrace = false;
        while (j < Math.min(i + 25, lines.length)) {
          sig += ' ' + lines[j].trim();
          if (lines[j].includes('{')) { foundBrace = true; break; }
          if (lines[j].includes(';') || lines[j].includes('=>')) break;
          j++;
        }
        if (foundBrace) {
          const ps = sig.indexOf('(');
          const pe = sig.lastIndexOf(')');
          if (ps >= 0 && pe > ps) {
            const deps = countParams(sig.slice(ps + 1, pe));
            if (deps >= 10) bigServiceCtors.push({ path: rf, line: i + 1, name: ctorMatch[2], deps });
          }
        }
        continue;
      }

      const methodMatch = line.match(/^(\s+)(?:public|internal|private|protected)\s+(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+|new\s+|unsafe\s+)*([\w\.<>\[\]\?,\(\)\s]+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(/);
      if (!methodMatch) continue;
      const mName = methodMatch[3];
      if (['if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'lock', 'return', 'new', 'throw', 'when'].includes(mName)) continue;

      if (mName.length >= 45) {
        longMethodNames.push({ path: rf, line: i + 1, name: mName, len: mName.length });
      }

      let j = i;
      let foundBrace = false;
      while (j < Math.min(i + 20, lines.length)) {
        if (lines[j].includes('{')) { foundBrace = true; break; }
        if (lines[j].includes(';') || lines[j].includes('=>')) break;
        j++;
      }
      if (!foundBrace) continue;

      let braceDepth = 0;
      let endLine = j;
      for (let k = j; k < lines.length; k++) {
        const clean = stripCommentsAndStrings(lines[k]);
        for (const ch of clean) {
          if (ch === '{') braceDepth++;
          else if (ch === '}') braceDepth--;
        }
        endLine = k;
        if (braceDepth <= 0) break;
      }
      const mLen = endLine - i + 1;
      if (mLen >= 120) {
        longMethods.push({ path: rf, line: i + 1, name: mName, lines: mLen });
      }
    }

    if (fileCatchHits > 0) {
      broadCatchFiles.push({ path: rf, count: fileCatchHits });
      if (isController) controllerCatches.push({ path: rf, count: fileCatchHits });
    }
  }

  const allServices = [...serviceGroups.values()];
  const partialSplitServices = allServices.filter(g => g.files.length >= 2);
  const giantServices = allServices.filter(g => g.totalLines >= 1200);
  const duplicatedHelpers = [...staticHelperFiles.entries()]
    .filter(([, set]) => set.size >= 4)
    .map(([name, set]) => ({ name, files: set.size, sample: [...set].slice(0, 3) }));

  const testToSrcRatio = srcCsLoc > 0 ? Number((testCsLoc / srcCsLoc).toFixed(3)) : 0;

  return {
    totals: { srcCsLoc, srcCshtmlLoc, testCsLoc, totalTestMethods },
    metrics: {
      long_local_vars: {
        label: 'Local `var` names ≥ 28 chars (src)',
        value: longLocalVars.length,
        max: longLocalVars.reduce((m, x) => Math.max(m, x.len), 0),
        outliers: topN(longLocalVars, x => x.len).map(x => `${x.path}:${x.line} (${x.len}c) ${x.name}`),
      },
      long_method_names: {
        label: 'Method names ≥ 45 chars (src)',
        value: longMethodNames.length,
        max: longMethodNames.reduce((m, x) => Math.max(m, x.len), 0),
        outliers: topN(longMethodNames, x => x.len).map(x => `${x.path}:${x.line} (${x.len}c) ${x.name}`),
      },
      long_test_names_80: {
        label: 'Test method names ≥ 80 chars',
        value: longTestNames80.length,
        max: longTestNames80.reduce((m, x) => Math.max(m, x.len), 0),
        outliers: topN(longTestNames80, x => x.len).map(x => `${x.path}:${x.line} (${x.len}c) ${x.name}`),
      },
      long_test_names_60: {
        label: 'Test method names ≥ 60 chars',
        value: longTestNames60.length,
        max: longTestNames60.reduce((m, x) => Math.max(m, x.len), 0),
        outliers: topN(longTestNames60, x => x.len).map(x => `${x.path}:${x.line} (${x.len}c) ${x.name}`),
      },
      partial_split_services: {
        label: 'Services split across `partial` files',
        value: partialSplitServices.length,
        max: partialSplitServices.reduce((m, x) => Math.max(m, x.files.length), 0),
        outliers: topN(partialSplitServices, x => x.totalLines).map(x => `${x.key} (${x.files.length} files, ${x.totalLines}L)`),
      },
      giant_services: {
        label: 'Logical services ≥ 1,200 LOC (summing partials)',
        value: giantServices.length,
        max: giantServices.reduce((m, x) => Math.max(m, x.totalLines), 0),
        outliers: topN(giantServices, x => x.totalLines).map(x => `${x.key} (${x.totalLines}L, ${x.files.length}f)`),
      },
      service_ctor_deps: {
        label: 'Classes with ≥ 10 constructor deps (src)',
        value: bigServiceCtors.length,
        max: bigServiceCtors.reduce((m, x) => Math.max(m, x.deps), 0),
        outliers: topN(bigServiceCtors, x => x.deps).map(x => `${x.path} ${x.name} (${x.deps} deps)`),
      },
      long_methods: {
        label: 'Methods ≥ 120 lines (src)',
        value: longMethods.length,
        max: longMethods.reduce((m, x) => Math.max(m, x.lines), 0),
        outliers: topN(longMethods, x => x.lines).map(x => `${x.path}:${x.line} ${x.name} (${x.lines}L)`),
      },
      big_positional_records: {
        label: 'Positional records ≥ 12 params (src)',
        value: bigPositionalRecords.length,
        max: bigPositionalRecords.reduce((m, x) => Math.max(m, x.params), 0),
        outliers: topN(bigPositionalRecords, x => x.params).map(x => `${x.path} ${x.name} (${x.params}p)`),
      },
      bespoke_result_types: {
        label: 'Bespoke `*Result` / `*Outcome` types (src)',
        value: bespokeResultTypes.length,
        max: bespokeResultTypes.length,
        outliers: bespokeResultTypes.slice(0, 5).map(x => `${x.path} ${x.name}`),
      },
      test_to_src_ratio: {
        label: 'Test-to-src C# LOC ratio',
        value: testToSrcRatio,
        isRatio: true,
        outliers: [`tests=${testCsLoc}L vs src=${srcCsLoc}L`],
      },
      giant_test_files: {
        label: 'Test files ≥ 1,000 lines',
        value: giantTestFiles.length,
        max: giantTestFiles.reduce((m, x) => Math.max(m, x.lines), 0),
        outliers: topN(giantTestFiles, x => x.lines).map(x => `${x.path} (${x.lines}L)`),
      },
      test_scaffolding_dup: {
        label: 'Test files hand-rolling DB/Http/Localizer harness',
        value: testScaffoldingFiles.length,
        outliers: topN(testScaffoldingFiles, x => x.count).map(x => `${x.path} (${x.count}x)`),
      },
      test_assert_mixed_files: {
        label: 'Test files mixing `Assert.*` and `.Should()`',
        value: testMixedAssertFiles.length,
        outliers: topN(testMixedAssertFiles, x => x.count).map(x => `${x.path} (${x.assertCount} Assert, ${x.shouldCount} Should)`),
      },
      localizer_string_format: {
        label: '`string.Format(Localizer[...])` sites (src)',
        value: localizerStringFormat.length,
        outliers: localizerStringFormat.slice(0, 5).map(x => `${x.path}:${x.line}`),
      },
      broad_catch_controllers: {
        label: 'Broad `catch (Exception)` in controllers',
        value: controllerCatches.reduce((s, x) => s + x.count, 0),
        outliers: topN(controllerCatches, x => x.count).map(x => `${x.path} (${x.count}x)`),
      },
      broad_catch_total: {
        label: 'Broad `catch (Exception)` total (src)',
        value: totalBroadCatch,
        outliers: topN(broadCatchFiles, x => x.count).map(x => `${x.path} (${x.count}x)`),
      },
      manual_currency_format: {
        label: 'Manual `.ToString("N2"/"F2"/"C2"/"0.00")` (src)',
        value: manualCurrency.length,
        outliers: manualCurrency.slice(0, 5).map(x => `${x.path}:${x.line}`),
      },
      duplicated_static_helpers: {
        label: 'Private/internal static helpers repeated in ≥ 4 files',
        value: duplicatedHelpers.length,
        max: duplicatedHelpers.reduce((m, x) => Math.max(m, x.files), 0),
        outliers: topN(duplicatedHelpers, x => x.files).map(x => `${x.name} (${x.files} files: ${x.sample.join(', ')})`),
      },
      razor_inline_js_lines: {
        label: 'Inline `<script>` lines in `.cshtml`',
        value: razorInlineJsLines,
        outliers: topN(razorBigJsFiles, x => x.lines).map(x => `${x.path} (${x.lines}L JS)`),
      },
      razor_inline_style_lines: {
        label: 'Inline `<style>` lines in `.cshtml`',
        value: razorInlineStyleLines,
        outliers: topN(razorBigStyleFiles, x => x.lines).map(x => `${x.path} (${x.lines}L CSS)`),
      },
      memory_atoms: {
        label: '`memory/` atom count',
        value: memoryAtoms,
        outliers: [`INDEX.md=${(memoryIndexBytes / 1024).toFixed(1)}KB`, `total=${(memoryBytes / 1024).toFixed(1)}KB`, `AGENTS.md=${agentsMdLines}L`],
      },
      memory_hard_rules: {
        label: '`memory/` atoms claiming `HARD RULE`',
        value: memoryHardRules,
        outliers: [],
      },
      memory_index_kb: {
        label: '`memory/INDEX.md` size (KB)',
        value: Number((memoryIndexBytes / 1024).toFixed(1)),
        isRatio: true,
        outliers: [`AGENTS.md=${agentsMdLines}L`],
      },
    },
  };
}

/**
 * Compare a current snapshot against a baseline snapshot and flag accelerating drift.
 */
export function compareSnapshots(current, baseline) {
  const rows = [];
  for (const [id, cur] of Object.entries(current.metrics)) {
    const prev = baseline?.metrics?.[id];
    const prevVal = prev ? prev.value : null;
    const delta = prevVal === null ? null : Number((cur.value - prevVal).toFixed(3));
    const pct = prevVal && prevVal > 0 ? Number(((delta / prevVal) * 100).toFixed(1)) : null;

    let flagged = false;
    let flagReason = null;
    if (prevVal !== null && delta > 0) {
      if (id === 'test_to_src_ratio' && delta >= 0.04) {
        flagged = true;
        flagReason = `ratio +${delta.toFixed(3)}`;
      } else if (id === 'memory_index_kb' && delta >= 4.0) {
        flagged = true;
        flagReason = `+${delta.toFixed(1)} KB`;
      } else if (id === 'razor_inline_js_lines' && delta >= 300) {
        flagged = true;
        flagReason = `+${delta} lines`;
      } else if (id === 'razor_inline_style_lines' && delta >= 100) {
        flagged = true;
        flagReason = `+${delta} lines`;
      } else if (!cur.isRatio) {
        if (prevVal === 0 && delta >= 2) {
          flagged = true;
          flagReason = `0 -> ${cur.value}`;
        } else if (pct !== null && pct >= 15 && delta >= 3) {
          flagged = true;
          flagReason = `+${delta} (+${pct}%)`;
        } else if (delta >= 15) {
          flagged = true;
          flagReason = `+${delta}`;
        } else if (cur.max && prev.max && cur.max - prev.max >= Math.max(10, Math.round(prev.max * 0.15))) {
          flagged = true;
          flagReason = `max ${prev.max} -> ${cur.max}`;
        }
      }
    }

    rows.push({
      id,
      label: cur.label,
      now: cur.value,
      prev: prevVal,
      delta,
      pct,
      max: cur.max ?? null,
      prevMax: prev?.max ?? null,
      flagged,
      flagReason,
      outliers: cur.outliers ?? [],
    });
  }
  return rows;
}

export function formatMarkdown(report) {
  const lines = [];
  lines.push(`# Drift Metrics (${report.headShort} vs ${report.baseShort ?? 'none'})`);
  lines.push('');
  if (report.baseDate) {
    lines.push(`Window: \`${report.baseShort}\` (${report.baseDate}) -> \`${report.headShort}\` (${report.headDate})`);
    lines.push('');
  }
  lines.push('| Metric | Now | Baseline | Delta | Max | Flag |');
  lines.push('|---|---:|---:|---:|---:|---|');
  for (const r of report.rows) {
    const deltaStr = r.delta === null
      ? '—'
      : `${r.delta > 0 ? '+' : ''}${r.delta}${r.pct !== null ? ` (${r.delta > 0 ? '+' : ''}${r.pct}%)` : ''}`;
    const maxStr = r.max !== null ? String(r.max) : '—';
    const flagStr = r.flagged ? `**FLAG** (${r.flagReason})` : '';
    lines.push(`| ${r.label} | ${r.now} | ${r.prev ?? '—'} | ${deltaStr} | ${maxStr} | ${flagStr} |`);
  }

  const flaggedRows = report.rows.filter(r => r.flagged);
  lines.push('');
  lines.push(`## Flagged Accelerations (${flaggedRows.length})`);
  if (flaggedRows.length === 0) {
    lines.push('');
    lines.push('None tripped the acceleration thresholds in this window.');
  } else {
    for (const r of flaggedRows) {
      lines.push('');
      lines.push(`### ${r.label} — ${r.flagReason}`);
      for (const o of r.outliers) lines.push(`- ${o}`);
    }
  }
  return lines.join('\n') + '\n';
}

function runGit(repoRoot, args, options = {}) {
  return execFileSync('git', args, {
    cwd: repoRoot,
    maxBuffer: 200 * 1024 * 1024,
    ...options,
  });
}

function readFilesAtGitRef(repoRoot, ref) {
  const lsOut = runGit(repoRoot, ['ls-tree', '-r', '-z', '--name-only', ref, '--', 'src', 'tests', 'memory', 'AGENTS.md'], {
    encoding: 'utf8',
  });
  const paths = lsOut.split('\0').filter(Boolean).filter(shouldAnalyzePath);
  if (paths.length === 0) return [];

  const batchInput = paths.map(p => `${ref}:${p}\n`).join('');
  const buf = runGit(repoRoot, ['cat-file', '--batch'], { input: batchInput });

  const files = [];
  let offset = 0;
  for (const relPath of paths) {
    const nl = buf.indexOf(0x0a, offset);
    if (nl === -1) break;
    const header = buf.subarray(offset, nl).toString('utf8');
    offset = nl + 1;
    if (header.endsWith(' missing')) continue;
    const parts = header.split(' ');
    const size = Number(parts[2]);
    const content = buf.subarray(offset, offset + size).toString('utf8');
    offset += size + 1; // trailing newline
    files.push({ path: relPath, content });
  }
  return files;
}

function readWorkingTreeFiles(repoRoot) {
  const results = [];
  function walk(dir) {
    if (!fs.existsSync(dir)) return;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (SKIP_DIRS.has(entry.name)) continue;
        walk(full);
      } else {
        const rel = path.relative(repoRoot, full).replace(/\\/g, '/');
        if (shouldAnalyzePath(rel)) {
          results.push({ path: rel, content: fs.readFileSync(full, 'utf8') });
        }
      }
    }
  }
  for (const top of ['src', 'tests', 'memory']) walk(path.join(repoRoot, top));
  const agentsPath = path.join(repoRoot, 'AGENTS.md');
  if (fs.existsSync(agentsPath)) {
    results.push({ path: 'AGENTS.md', content: fs.readFileSync(agentsPath, 'utf8') });
  }
  return results;
}

function mulberry32(seed) {
  let a = seed >>> 0;
  return function next() {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function hashSeed(text) {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return h >>> 0;
}

function sampleRecentHunks(repoRoot, { days = 14, count = 18, seedText }) {
  const baseRef = runGit(repoRoot, ['rev-list', '-n', '1', `--before=${days} days ago`, 'HEAD'], { encoding: 'utf8' }).trim();
  if (!baseRef) {
    return 'No baseline commit found for hunk sampling.\n';
  }
  const changed = runGit(repoRoot, ['diff', '--name-only', '--diff-filter=AM', `${baseRef}..HEAD`, '--', 'src', 'tests', 'memory', 'docs/architecture'], {
    encoding: 'utf8',
  })
    .split('\n')
    .map(s => s.trim())
    .filter(Boolean)
    .filter(p => !p.includes('/Migrations/') && !p.endsWith('.Designer.cs') && !p.endsWith('.resx'));

  const buckets = {
    services: changed.filter(p => p.startsWith('src/') && p.includes('/Services/') && p.endsWith('.cs')),
    controllers: changed.filter(p => p.startsWith('src/') && p.includes('/Controllers/') && p.endsWith('.cs')),
    views: changed.filter(p => p.startsWith('src/') && p.endsWith('.cshtml')),
    tests: changed.filter(p => p.startsWith('tests/') && p.endsWith('.cs')),
    rules: changed.filter(p => p.startsWith('memory/') || p.startsWith('docs/architecture/')),
  };

  const rng = mulberry32(hashSeed(seedText));
  const shuffle = arr => {
    const copy = [...arr];
    for (let i = copy.length - 1; i > 0; i--) {
      const j = Math.floor(rng() * (i + 1));
      [copy[i], copy[j]] = [copy[j], copy[i]];
    }
    return copy;
  };

  const perBucket = Math.max(2, Math.ceil(count / Object.keys(buckets).length));
  const picked = [];
  for (const [bucket, list] of Object.entries(buckets)) {
    let taken = 0;
    for (const file of shuffle(list)) {
      if (taken >= perBucket) break;
      const diff = runGit(repoRoot, ['diff', '-U2', `${baseRef}..HEAD`, '--', file], { encoding: 'utf8' });
      const substantiveAdds = diff
        .split('\n')
        .filter(l => l.startsWith('+') && !l.startsWith('+++') && !/^\+\s*(?:using\s|$)/.test(l));
      if (substantiveAdds.length < 6) continue;
      picked.push({ bucket, file, diff });
      taken++;
    }
  }

  const out = [`# Sampled Recent Hunks (last ${days} days, seed ${seedText}, base ${baseRef.slice(0, 9)})`, ''];
  for (const { bucket, file, diff } of picked.slice(0, count)) {
    const addedLines = diff.split('\n').filter(l => l.startsWith('@@') || l.startsWith('+') || l.startsWith(' ')).slice(0, 45);
    out.push(`## [${bucket}] ${file}`);
    out.push('```diff');
    out.push(...addedLines);
    out.push('```');
    out.push('');
  }
  return out.join('\n');
}

function parseArgs(argv) {
  const opts = {
    days: null,
    since: null,
    noBaseline: false,
    workingTree: false,
    format: 'markdown',
    sampleHunks: false,
    count: 18,
    seed: new Date().toISOString().slice(0, 10),
  };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--days') opts.days = Number(argv[++i]);
    else if (arg === '--since') opts.since = argv[++i];
    else if (arg === '--no-baseline') opts.noBaseline = true;
    else if (arg === '--working-tree') opts.workingTree = true;
    else if (arg === '--format') opts.format = argv[++i];
    else if (arg.startsWith('--format=')) opts.format = arg.split('=')[1];
    else if (arg === '--sample-hunks') opts.sampleHunks = true;
    else if (arg === '--count') opts.count = Number(argv[++i]);
    else if (arg === '--seed') opts.seed = argv[++i];
  }
  opts.days ??= opts.sampleHunks ? 14 : 30;
  return opts;
}

function main() {
  const opts = parseArgs(process.argv.slice(2));
  const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();

  if (opts.sampleHunks) {
    process.stdout.write(sampleRecentHunks(repoRoot, { days: opts.days, count: opts.count, seedText: opts.seed }));
    return;
  }

  const headShort = runGit(repoRoot, ['rev-parse', '--short', 'HEAD'], { encoding: 'utf8' }).trim();
  const headDate = runGit(repoRoot, ['show', '-s', '--format=%cs', 'HEAD'], { encoding: 'utf8' }).trim();
  const currentFiles = opts.workingTree ? readWorkingTreeFiles(repoRoot) : readFilesAtGitRef(repoRoot, 'HEAD');
  const current = analyzeFiles(currentFiles);

  let baseline = null;
  let baseShort = null;
  let baseDate = null;
  if (!opts.noBaseline) {
    const baseRef = opts.since
      ? runGit(repoRoot, ['rev-parse', opts.since], { encoding: 'utf8' }).trim()
      : runGit(repoRoot, ['rev-list', '-n', '1', `--before=${opts.days} days ago`, 'HEAD'], { encoding: 'utf8' }).trim();
    if (baseRef) {
      baseShort = runGit(repoRoot, ['rev-parse', '--short', baseRef], { encoding: 'utf8' }).trim();
      baseDate = runGit(repoRoot, ['show', '-s', '--format=%cs', baseRef], { encoding: 'utf8' }).trim();
      baseline = analyzeFiles(readFilesAtGitRef(repoRoot, baseRef));
    }
  }

  const rows = compareSnapshots(current, baseline);
  const report = { headShort, headDate, baseShort, baseDate, totals: current.totals, rows };

  if (opts.format === 'json') {
    process.stdout.write(JSON.stringify(report, null, 2) + '\n');
  } else {
    process.stdout.write(formatMarkdown(report));
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  main();
}
