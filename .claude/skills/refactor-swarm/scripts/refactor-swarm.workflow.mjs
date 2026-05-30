export const meta = {
  name: 'humans-reforge-section-refactor',
  description: 'Parallel Reforge-guided section refactors (N lanes, score-blind adversarial review)',
  whenToUse: 'Reduce Reforge surface/internal in Humans sections via architecturally-correct refactors, one branch+PR per section.',
  phases: [
    { title: 'Scan', detail: 'Per-lane section thesis + >=8 candidate ledger' },
    { title: 'Implement', detail: 'One coherent structural change; build+tests+Reforge' },
    { title: 'Review', detail: 'Adversarial score-blind panel (lenses per intensity)' },
    { title: 'Commit', detail: 'Commit with score+review appendix, push' },
    { title: 'PR', detail: 'Open draft PR per lane with progress' },
  ],
}

// ===========================================================================
// TEMPLATE — the controller fills the block below AFTER Phase-0 recon.
// DO NOT ship frozen values. Specifically:
//   • BASE  — resolve at recon time: `git rev-parse origin/main`. Never a stale sha.
//   • LANES — DERIVED from the recomputed Reforge rank minus in-flight sections
//             (SKILL Phase 0). Not a fixed list. Create the worktrees during recon,
//             then paste the resulting block here. Lane COUNT comes from --lanes /
//             intensity; WHICH sections comes from the score + conflict scan.
//   • RUN / BASELINE_JSON — this run's scratch dir + the BUILT baseline score.
// The harness `args` global is unreliable, so the controller hardcodes this block
// per run rather than passing args.
// ===========================================================================
const ROOT          = '<repo root, e.g. H:/source/Humans>'
const DLL           = '<built Reforge.dll, e.g. H:/source/reforge/src/Reforge/bin/Release/net10.0/Reforge.dll>'
const BASE          = '<origin/main sha resolved during recon>'
const RUN           = '<run id, e.g. 2026-05-30-parallel>'
const BASELINE_JSON = '<BUILT baseline all-groups json, e.g. {ROOT}/local/refactor-runs/{RUN}/baseline-all.json>'
const INTENSITY     = 'standard'   // 'light' | 'standard' | 'deep' — set from the skill arg
const LANES = [
  // One entry per SELECTED lane (from recon). Example shape:
  // { section: 'Users', group: 'Users', n: 1,
  //   branch: 'refactor/<date>-users-workflow-1',
  //   wt: '<abs worktree path>' },
]
const DELTA = '.codex/skills/humans-refactor/scripts/reforge_delta.py'

// Termination is STASIS-driven, not a fixed iteration count. A lane runs its
// candidate ledger until the implementer reports no remaining high-confidence
// candidate (stasis), OR `dryStreak` consecutive iterations land nothing, OR the
// token budget runs low. SAFETY_CAP is only a runaway backstop, never a target.
// Intensity trades autonomy-per-output against token burn: deeper = more review
// lenses + more persistence through dry rounds.
const PROFILE = ({
  light:    { lenses: ['A'],           dryStreak: 1 },
  standard: { lenses: ['A', 'B'],      dryStreak: 2 },
  deep:     { lenses: ['A', 'B', 'C'], dryStreak: 3 },
})[INTENSITY]
const SAFETY_CAP = 40   // runaway backstop only — real stop is stasis / dryStreak / budget

const safe = async (fn) => { try { return await fn() } catch (e) { return { __error: String(e && e.message || e) } } }
const ok = (x) => x && !x.__error

// ---------------------------------------------------------------------------
// Shared hard rules (no escape-hatch language)
// ---------------------------------------------------------------------------
const HARD_RULES = (L) => `
ABSOLUTE CONSTRAINTS (Humans repo, peters-hard-rules.md is the constitution):
- Worktree: operate ONLY inside ${L.wt} (branch ${L.branch}). Every git command MUST be \`git -C "${L.wt}" ...\`. Every file you Read/Edit/Write/Grep MUST be under ${L.wt}. Never read, edit, or commit anything under ${ROOT}/src or any OTHER worktree. Never run a bare \`git\` (no -C) — it could hit the main checkout / main branch which auto-deploys.
- NO database/persistence changes of any kind: no EF migrations, no migration edits, no DbContext/model/schema/OnModelCreating changes, no entity persistence-shape changes, no JSON serialization attribute changes. This refactor is repository-layer-and-above ONLY.
- NO \`rm -rf\` and no destructive shell deletes. To discard an experiment use \`git -C "${L.wt}" reset --hard HEAD\` then \`git -C "${L.wt}" clean -fd\` (git-native, worktree-scoped). To remove a worktree use \`git worktree remove\`.
- NO bypass flags: never --no-verify, never suppress analyzers, never delete/weaken tests to make something pass.
- Layering (DbContext -> Repository -> Service -> Controller): only the repository touches its tables; services derive from IApplicationService and call only their own repositories + other sections' public read interfaces; controllers hold no logic beyond parse/call/format; caching decorators call the inner service, never the repository.
- Section isolation: never reach into another section's repository, DbContext, or entity graph. Cross-section reads go through I<Section>ServiceRead and the canonical <Section>Info DTO facts. Cross-section dependence on a write/full service is high debt — only legitimate when truly orchestrating a mutation.
- Surgical to the ${L.section} section. Do not "improve" unrelated code, reformat, or refactor things that are not broken. Do not move debt into another section or into shared/helper code to win points.
- \`[SurfaceBudget]\` does NOT constrain this process — you MAY raise/extend a read interface's budget when routing a consumer onto the read surface. The panel still rejects bespoke projection/predicate methods.
- Do NOT edit: reforge.surface-score.json, Humans.slnx, CLAUDE.md, anything under memory/ or docs/ (your section invariant doc may be updated only if a change makes it factually wrong — otherwise leave docs alone).
- Scratch (JSON, messages, notes) goes under ${ROOT}/local/refactor-runs/${RUN}/${L.section}/ which is OUTSIDE the worktree, so it is never committed. Create it with mkdir -p.
`

const ANTI_GAMING = `
THESE MOVES ARE FORBIDDEN (an adversarial score-blind panel will reject them; do not waste an iteration producing one):
- Moving methods into a new helper/extension/static class to lower interface or type counts. Relocation is not deletion.
- Adding a bespoke projection/predicate/scalar query method to a read interface (e.g. "GetUsersNamedPeter", "GetActiveX", "IsY"). Those must be LINQ over the canonical <Section>Info / read shard at the call site, not interface surface.
- Narrowing accessibility (public -> internal, or hiding behind internal namespaces) when the main effect is dodging the surface score and no external callers were actually deleted. \`internal\`-to-skirt-points is treated as cheating.
- Parameter-bag / options / input DTOs whose only purpose is hiding a long method signature.
- Generic action/mode dispatchers replacing explicit domain verbs.
- Removing or weakening tests without equivalent behavioral coverage.

GENUINE IMPROVEMENTS TO SEEK (real structure, deletes a concept):
- Route cross-section consumers that only read from a full/write service onto I<Section>ServiceRead, or onto canonical <Section>Info DTO facts, and drop the full-service dependency.
- DELETE a read-surface projection/predicate/scalar method because callers now derive it via LINQ over <Section>Info (add a fact to the canonical DTO ONLY when that genuinely lets you delete a read method / DB read — never as a one-off "bag" field).
- Collapse duplicated include/query/call shapes into one cohesive read.
- Delete genuinely dead public surface (verify zero production callers first via Reforge \`callers\`/grep).
- Centralize a real lifecycle state machine (validates current state, owns transitions, centralizes side effects) instead of scattered toggles — only when one genuinely exists.
`

// ---------------------------------------------------------------------------
// Schemas
// ---------------------------------------------------------------------------
const LEDGER_SCHEMA = {
  type: 'object',
  required: ['thesis', 'sectionShape', 'candidates'],
  properties: {
    thesis: { type: 'string', description: 'Section architecture thesis (shape, read/write surfaces, primary DTO, cross-section links).' },
    sectionShape: {
      type: 'object',
      properties: {
        repoBacked: { type: 'boolean' },
        repositories: { type: 'array', items: { type: 'string' } },
        primaryDto: { type: 'string' },
        settingsDto: { type: 'string' },
        readShards: { type: 'array', items: { type: 'string' } },
        readSurface: { type: 'string' },
        writeSurface: { type: 'string' },
        crossSectionCallers: { type: 'array', items: { type: 'string' } },
      },
    },
    candidates: {
      type: 'array',
      minItems: 8,
      items: {
        type: 'object',
        required: ['id', 'title', 'category', 'rationale', 'conceptDeleted', 'risk', 'files'],
        properties: {
          id: { type: 'string' },
          title: { type: 'string' },
          category: { type: 'string' },
          rationale: { type: 'string' },
          conceptDeleted: { type: 'string', description: 'The concept this DELETES (not relocates).' },
          conceptAdded: { type: 'string' },
          expectedTargetGain: { type: 'number' },
          risk: { type: 'string', enum: ['low', 'medium', 'high'] },
          files: { type: 'array', items: { type: 'string' } },
        },
      },
    },
  },
}

const STAGE_SCHEMA = {
  type: 'object',
  required: ['outcome'],
  properties: {
    outcome: { type: 'string', enum: ['changed', 'no-candidate', 'build-or-test-failed'] },
    candidateId: { type: 'string' },
    candidateTitle: { type: 'string' },
    subject: { type: 'string', description: 'Imperative commit subject for this change.' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    conceptDeleted: { type: 'string' },
    conceptAdded: { type: 'string' },
    structuralSummary: { type: 'string', description: 'Plain structural description of what changed, for the reviewer.' },
    sectionBefore: { type: 'number' },
    sectionAfter: { type: 'number' },
    sectionDelta: { type: 'number' },
    overallBefore: { type: 'number' },
    overallAfter: { type: 'number' },
    outsideIncrease: { type: 'number', description: 'outside_after - outside_before; positive = rest of solution grew.' },
    weightedValue: { type: 'number', description: 'sectionImprovement + 2*outsideImprovement (per reforge_delta.py).' },
    deltaText: { type: 'string', description: 'Verbatim stdout of reforge_delta.py.' },
    buildResult: { type: 'string' },
    testResult: { type: 'string' },
    testCommand: { type: 'string' },
    stageJsonPath: { type: 'string' },
    notes: { type: 'string' },
    stasis: { type: 'boolean', description: 'true if no remaining high-confidence candidate exists.' },
  },
}

const REVIEW_SCHEMA = {
  type: 'object',
  required: ['verdict', 'conceptActuallyDeleted', 'reason'],
  properties: {
    verdict: { type: 'string', enum: ['accept', 'rework', 'reject'] },
    conceptActuallyDeleted: { type: 'string', description: 'What concept the diff genuinely deletes; "none" if only moved/renamed/hidden.' },
    relocationDetected: { type: 'boolean' },
    accessibilityGaming: { type: 'boolean' },
    behaviorOrContractRisk: { type: 'boolean' },
    reason: { type: 'string' },
    requiredChanges: { type: 'array', items: { type: 'string' } },
  },
}

const COMMIT_SCHEMA = {
  type: 'object',
  required: ['committed'],
  properties: { committed: { type: 'boolean' }, sha: { type: 'string' }, pushed: { type: 'boolean' }, error: { type: 'string' } },
}

const PR_SCHEMA = {
  type: 'object',
  required: ['opened'],
  properties: { opened: { type: 'boolean' }, url: { type: 'string' }, number: { type: 'number' }, error: { type: 'string' } },
}

// ---------------------------------------------------------------------------
// Prompts
// ---------------------------------------------------------------------------
const scratch = (L) => `${ROOT}/local/refactor-runs/${RUN}/${L.section}`

function scanPrompt(L) {
  return `You are the SCAN agent for a Humans Reforge-guided refactor lane. Target section: ${L.section} (Reforge group "${L.group}").
${HARD_RULES(L)}
First read, inside the worktree, to ground yourself: ${L.wt}/.codex/skills/humans-refactor/SKILL.md, ${L.wt}/docs/architecture/peters-hard-rules.md, ${L.wt}/docs/architecture/design-rules.md (skim relevant sections), ${L.wt}/docs/sections/ for a ${L.section} doc if present, and ${L.wt}/memory/INDEX.md (scan for ${L.section}-relevant atoms).

Do the Section Architecture Discovery pass (about 5 focused minutes). Build the section thesis: repository ownership (is it repo-backed?), primary read model (the cached <Section>Info DTO if any), read shards, the read surface (I${L.section}ServiceRead or equivalent), the write/full surface, settings shape (<Section>SettingsInfo), cache semantics, and cross-section links (anyone depending on the full/write service, repository, or entity graph).

Use Reforge to see point pressure and structure (each call loads the full solution; use sparingly):
- Full section report:  dotnet "${DLL}" surface-score --solution "${L.wt}/Humans.slnx" --format Json --group "${L.group}" --all --top-symbols 200 > "${scratch(L)}/scan-section.json"   (mkdir -p "${scratch(L)}" first)
- Call-graph queries as needed:  dotnet "${DLL}" --help  then use callers / members / dependencies / injected / implementations on key interfaces/services.
Use Grep/git grep for quick call-site checks.

Then build a CANDIDATE LEDGER of at least 8 researched opportunities. For each: id, title, category, rationale, the concept it DELETES (not relocates), conceptAdded if any, expectedTargetGain (rough), risk, and likely files. Search across these categories: cross-section consumers on full/write surfaces that should use the read interface or DTO facts; read methods that are really LINQ projections/predicates/scalars over the primary DTO; facts read through repos/full services that belong on the canonical read DTO; low-caller/overlapping/single-impl interface or repo methods; duplicate query/include shapes; genuinely dead public surface.

${ANTI_GAMING}

Write your thesis to "${scratch(L)}/thesis.md". Return the structured ledger. Do NOT edit any source yet.`
}

function implPrompt(L, i, ledger, accepted, rejected, hint, prevJson) {
  return `You are the IMPLEMENT agent for the ${L.section} refactor lane, iteration ${i}. Worktree: ${L.wt} (branch ${L.branch}).
${HARD_RULES(L)}

STEP 0 (mandatory clean base): run \`git -C "${L.wt}" reset --hard HEAD\` then \`git -C "${L.wt}" clean -fd\`. Then assert \`git -C "${L.wt}" rev-parse --abbrev-ref HEAD\` equals "${L.branch}" — if it does not, return outcome="no-candidate" with notes explaining the mismatch and do nothing else.

Section thesis:
${ledger.thesis}

Candidate ledger (pick the single highest-LEVERAGE coherent candidate NOT already accepted or rejected below; favor genuine structural wins over raw point count):
${JSON.stringify(ledger.candidates, null, 1)}

Already ACCEPTED this run (do not redo): ${JSON.stringify(accepted.map(a => a.candidateTitle))}
Already REJECTED/failed this run (do not repeat as-is): ${JSON.stringify(rejected)}
${hint ? `\nReviewer/loop feedback from last iteration:\n${hint}\n` : ''}
${ANTI_GAMING}

Make EXACTLY ONE coherent change for the chosen candidate. Edit only files under ${L.wt}. Update ALL call sites (use Reforge \`callers\` / grep). Keep it surgical and architecturally honest.

Then verify, all with absolute worktree paths:
1. Build:  dotnet build "${L.wt}/Humans.slnx" --disable-build-servers -v q
2. Targeted tests: run the ${L.section} tests AND the architecture tests (they enforce section/baseline rules), e.g.
   dotnet test "${L.wt}/Humans.slnx" --filter "FullyQualifiedName~${L.section}|FullyQualifiedName~Architecture" -v q --disable-build-servers
   (pick the precise filter/project that compiles fastest while covering ${L.section} + Architecture; record the exact command in testCommand.)
3. Reforge after change (BUILD-FIRST already satisfied by step 1, so the workspace is fully resolved):
   mkdir -p "${scratch(L)}"
   dotnet "${DLL}" surface-score --solution "${L.wt}/Humans.slnx" --format Json --all --top-symbols 200 > "${scratch(L)}/stage${String(i).padStart(2, '0')}-all.json"
4. Score delta vs previous accepted stage:
   python "${L.wt}/${DELTA}" --before-all "${prevJson}" --after-all "${scratch(L)}/stage${String(i).padStart(2, '0')}-all.json" --section "${L.group}"
   Capture its full stdout into deltaText. Set sectionBefore/After/Delta, overallBefore/After, outsideIncrease (= outside_after - outside_before), and weightedValue from that output.

If the build or tests fail and you cannot fix them quickly within this one candidate, reset (\`git -C "${L.wt}" reset --hard HEAD\`; \`git -C "${L.wt}" clean -fd\`) and return outcome="build-or-test-failed" with notes. If no candidate in the ledger is still worth doing (exhausted / only speculative / blocked by hard limits), return outcome="no-candidate" and stasis=true. Otherwise return outcome="changed" with all fields filled and a clear structuralSummary describing WHAT changed structurally (which concept deleted, which consumers re-routed, etc.). LEAVE THE CHANGE UNCOMMITTED — a review panel runs next.`
}

function reviewPrompt(L, stage, lens) {
  const lenses = {
    A: `LENS A — RELOCATION & ACCESSIBILITY-GAMING DETECTOR. Your default verdict is reject. Read the diff and determine: was anything merely MOVED (into a helper/extension/static/partial class, another file, shared code, or another section) rather than deleted? Was any accessibility narrowed (public -> internal, or hidden behind internal namespaces) whose main effect is dropping the durable-surface count without deleting real external callers? Set conceptActuallyDeleted to the concept genuinely removed, or "none" if the change only relocates/renames/hides. relocationDetected and accessibilityGaming must reflect what you find. If nothing is genuinely deleted, or relocation/accessibility-gaming is the main effect, reject.`,
    B: `LENS B — READ-MODEL PURIST. Your default verdict is reject. Read the diff against the section thesis. Reject if a bespoke projection/predicate/scalar query method was ADDED to a read interface (it should be LINQ over the canonical <Section>Info / read shard at the call site). Reject if a "new DTO" is really a parameter/result bag. ACCEPT-worthy: cross-section consumers re-routed onto I<Section>ServiceRead or canonical <Section>Info DTO facts; a read projection/predicate/scalar DELETED because callers now derive it via LINQ over the DTO; a genuinely canonical fact added to <Section>Info that lets a read method / DB read be deleted. Adding interface surface is suspicious; deleting it (with callers correctly migrated) is good.`,
    C: `LENS C — BEHAVIOR & CONTRACT GUARD. Your default verdict is reject. Verify behavior and contracts are preserved: build/tests cover the change (no tests removed/weakened without equivalent coverage); authorization, cache invalidation/cold-key behavior, audit, notification, and transaction boundaries intact; domain vocabulary preserved (no generic action/mode dispatcher replacing explicit verbs); any removed public surface is genuinely dead (no production callers). Set behaviorOrContractRisk accordingly. If behavior/contract integrity is uncertain, reject.`,
  }
  return `You are a READ-ONLY architecture reviewer for the ${L.section} refactor lane. ${lenses[lens]}

You DO NOT receive and MUST NOT consider the Reforge score change. A lower score is NOT a reason to accept. Judge ONLY the structural change. The implementer is incentivized to game a number; assume the change may be gaming until the diff proves a genuine structural improvement you can name in one sentence.

Inspect (read-only — do not edit anything):
- The uncommitted diff:  git -C "${L.wt}" --no-pager diff
- New untracked files:   git -C "${L.wt}" status --porcelain   (read any new files under ${L.wt})
- The section thesis and the implementer's claims below.

Section thesis:
${stage.__thesis}

Implementer claims: deletes "${stage.conceptDeleted}", adds "${stage.conceptAdded || 'nothing'}". Build: ${stage.buildResult}. Tests (${stage.testCommand || 'n/a'}): ${stage.testResult}. Structural summary: ${stage.structuralSummary}
${stage.outsideIncrease > 0 ? `\nNOTE (structural pointer only, not a score judgment): the rest of the solution grew. Inspect the diff for code that moved OUT of the ${L.section} section into other sections/shared/helpers — that is relocation and must be rejected.\n` : ''}
Answer internally: What concept is genuinely deleted? Was anything only moved/renamed/hidden? Does any added read-interface method belong as LINQ over the DTO instead? Is behavior/contract preserved? Then return the JSON verdict. accept only if this lens sees a clean genuine structural improvement; rework if the direction is right but a concrete fix is needed; reject otherwise.`
}

function commitPrompt(L, stage, panel, i) {
  const msgPath = `${scratch(L)}/msg${String(i).padStart(2, '0')}.txt`
  return `You are the COMMIT agent for the ${L.section} lane. The review panel ACCEPTED the change. Commit and push it.
${HARD_RULES(L)}

SAFETY: first assert \`git -C "${L.wt}" rev-parse --abbrev-ref HEAD\` == "${L.branch}". If not, return committed=false with the error. NEVER run git without -C "${L.wt}".

Write this commit message to "${msgPath}" (create dir with mkdir -p), then commit with it:

${stage.subject}

${stage.deltaText}

Architecture-review (score-blind panel):
- Verdict: accept (unanimous across active lenses)
${Object.entries(panel).map(([k, v]) => `- Lens ${k}: ${v}`).join('\n')}
- Concept deleted: ${stage.conceptDeleted}

Verification:
- Build: ${stage.buildResult}
- Tests (${stage.testCommand}): ${stage.testResult}
- Reforge: ${L.group} after = ${stage.sectionAfter}

Then:
  git -C "${L.wt}" add -A
  git -C "${L.wt}" status --porcelain   (confirm ONLY intended ${L.section} source files are staged; nothing under local/ or other sections; if anything unexpected is staged, unstage it and fix before committing)
  git -C "${L.wt}" commit -F "${msgPath}"
  git -C "${L.wt}" push -u origin "${L.branch}"
Return committed/sha/pushed. The sha is \`git -C "${L.wt}" rev-parse HEAD\`.`
}

function prPrompt(L, accepted, rejected, thesis) {
  const bodyPath = `${scratch(L)}/pr-body.md`
  return `You are the PR agent for the ${L.section} lane. Open a DRAFT PR for branch ${L.branch} (already pushed) into main on the fork.
${HARD_RULES(L)}
Confirm the branch is pushed and up to date: \`git -C "${L.wt}" status\`, \`git -C "${L.wt}" log --oneline origin/main..${L.branch}\`. If there are unpushed commits, push them: \`git -C "${L.wt}" push -u origin "${L.branch}"\`.

Write a PR body to "${bodyPath}" summarizing: the section thesis, each accepted commit (subject + concept deleted + Reforge target before->after + weighted value + panel verdict), cumulative ${L.group} score movement (origin/main baseline -> final), candidates rejected and why, and remaining high-value work not done. End the body with the standard footer line.

Section thesis:
${thesis}

Accepted commits (in order):
${JSON.stringify(accepted, null, 1)}

Rejected candidates:
${JSON.stringify(rejected, null, 1)}

Then create the draft PR with gh (base = the fork's main, head = "${L.branch}"). Return opened/url/number. If a PR for this head already exists, return opened=true with its url instead of failing.`
}

// ---------------------------------------------------------------------------
// Verdict combination: every active lens must accept, zero rejects; else reject/rework
// ---------------------------------------------------------------------------
function combine(reviews) {
  const valid = reviews.filter((r) => ok(r) && r.verdict)
  if (valid.length < PROFILE.lenses.length) return { verdict: 'reject', reason: 'insufficient valid reviews', requiredChanges: [] }
  if (valid.some((r) => r.verdict === 'reject')) {
    const rj = valid.filter((r) => r.verdict === 'reject')
    return { verdict: 'reject', reason: rj.map((r) => r.reason).join(' | '), requiredChanges: rj.flatMap((r) => r.requiredChanges || []) }
  }
  if (valid.some((r) => r.verdict === 'rework')) {
    const rw = valid.filter((r) => r.verdict === 'rework')
    return { verdict: 'rework', reason: rw.map((r) => r.reason).join(' | '), requiredChanges: rw.flatMap((r) => r.requiredChanges || []) }
  }
  return { verdict: 'accept', reason: 'unanimous', requiredChanges: [] }
}

async function runPanel(L, stage) {
  const reviews = await parallel(PROFILE.lenses.map((lens) => () =>
    agent(reviewPrompt(L, stage, lens), { label: `review-${lens}:${L.section}`, phase: 'Review', schema: REVIEW_SCHEMA })))
  return { combined: combine(reviews), reviews }
}

function panelNotes(reviews) {
  const notes = {}
  PROFILE.lenses.forEach((lens, idx) => { notes[lens] = ok(reviews[idx]) ? `${reviews[idx].verdict} — ${reviews[idx].reason}` : 'n/a' })
  return notes
}

// ---------------------------------------------------------------------------
// Lane loop — STASIS-driven (ledger exhaustion / dry streak / budget); SAFETY_CAP is a backstop
// ---------------------------------------------------------------------------
async function runLane(L) {
  const tag = L.section
  const ledger = await safe(() => agent(scanPrompt(L), { label: `scan:${tag}`, phase: 'Scan', schema: LEDGER_SCHEMA }))
  if (!ok(ledger)) { log(`[${tag}] scan failed: ${ledger && ledger.__error}`); return { L, error: 'scan-failed' } }
  log(`[${tag}] thesis built, ${ledger.candidates.length} candidates`)

  const accepted = []
  const rejected = []
  let prevJson = BASELINE_JSON
  let hint = ''
  let noAccept = 0
  let i = 0

  // STASIS-driven: stop when the implementer reports no candidate, dryStreak
  // consecutive non-accepts pile up, or budget runs low. SAFETY_CAP is a backstop.
  while (i < SAFETY_CAP) {
    i++
    if (budget.total && budget.remaining() < 80000) { log(`[${tag}] stopping: budget low`); break }

    let stage = await safe(() => agent(implPrompt(L, i, ledger, accepted, rejected, hint, prevJson), { label: `impl:${tag}#${i}`, phase: 'Implement', schema: STAGE_SCHEMA }))
    if (!ok(stage)) { rejected.push({ iter: i, reason: `impl error: ${stage && stage.__error}` }); noAccept++; if (noAccept >= PROFILE.dryStreak) { log(`[${tag}] stopping: dry streak`); break } continue }
    if (stage.outcome === 'no-candidate' || stage.stasis) { log(`[${tag}] stasis: ${stage.notes || ''}`); break }
    if (stage.outcome === 'build-or-test-failed') {
      rejected.push({ iter: i, candidateTitle: stage.candidateTitle, reason: `build/test failed: ${stage.notes || ''}` })
      hint = `Candidate "${stage.candidateTitle}" failed build/test: ${stage.notes || ''}. Pick a DIFFERENT candidate.`
      noAccept++; if (noAccept >= PROFILE.dryStreak) { log(`[${tag}] stopping: dry streak`); break } continue
    }
    stage.__thesis = ledger.thesis

    let { combined, reviews } = await runPanel(L, stage)

    if (combined.verdict === 'rework') {
      hint = `Reviewers asked for rework on "${stage.candidateTitle}": ${combined.reason}. Apply: ${combined.requiredChanges.join('; ')}`
      const fix = await safe(() => agent(`${implPrompt(L, i, ledger, accepted, rejected, hint, prevJson)}\n\nNOTE: A change for this candidate is ALREADY in the worktree (do NOT reset it first this time). Apply ONLY these corrections, then re-run build/tests/Reforge/delta and return the updated stage: ${combined.requiredChanges.join('; ')}`, { label: `rework:${tag}#${i}`, phase: 'Implement', schema: STAGE_SCHEMA }))
      if (ok(fix) && fix.outcome === 'changed') { fix.__thesis = ledger.thesis; stage = fix; ({ combined, reviews } = await runPanel(L, stage)) }
    }

    if (combined.verdict !== 'accept') {
      rejected.push({ iter: i, candidateTitle: stage.candidateTitle, verdict: combined.verdict, reason: combined.reason })
      hint = `Panel ${combined.verdict} "${stage.candidateTitle}": ${combined.reason}. The worktree will be reset; choose a different, more honest candidate.`
      log(`[${tag}] #${i} ${combined.verdict}: ${stage.candidateTitle} — ${combined.reason}`)
      noAccept++; if (noAccept >= PROFILE.dryStreak) { log(`[${tag}] stopping: dry streak (${PROFILE.dryStreak})`); break }
      continue
    }

    const notes = panelNotes(reviews)
    const commit = await safe(() => agent(commitPrompt(L, stage, notes, i), { label: `commit:${tag}#${i}`, phase: 'Commit', schema: COMMIT_SCHEMA }))
    if (ok(commit) && commit.committed) {
      accepted.push({ iter: i, candidateTitle: stage.candidateTitle, conceptDeleted: stage.conceptDeleted, sha: commit.sha, sectionBefore: stage.sectionBefore, sectionAfter: stage.sectionAfter, sectionDelta: stage.sectionDelta, outsideIncrease: stage.outsideIncrease, weightedValue: stage.weightedValue, panel: notes })
      prevJson = stage.stageJsonPath || prevJson
      hint = ''
      noAccept = 0
      log(`[${tag}] ACCEPT #${i}: ${stage.candidateTitle} (${L.group} ${stage.sectionBefore}->${stage.sectionAfter}, weighted ${stage.weightedValue})`)
    } else {
      rejected.push({ iter: i, candidateTitle: stage.candidateTitle, reason: `commit failed: ${(commit && commit.error) || ''}` })
      hint = `Commit failed for "${stage.candidateTitle}". Choose another candidate.`
      noAccept++; if (noAccept >= PROFILE.dryStreak) { log(`[${tag}] stopping: dry streak`); break }
    }
  }
  if (i >= SAFETY_CAP) log(`[${tag}] hit SAFETY_CAP (${SAFETY_CAP}) — backstop, not stasis; ledger may have more`)

  let pr = null
  if (accepted.length > 0) {
    pr = await safe(() => agent(prPrompt(L, accepted, rejected, ledger.thesis), { label: `pr:${tag}`, phase: 'PR', schema: PR_SCHEMA }))
    log(`[${tag}] PR: ${ok(pr) ? (pr.url || pr.opened) : 'failed'}`)
  } else {
    log(`[${tag}] no accepted commits — no PR opened`)
  }
  return { section: tag, branch: L.branch, thesis: ledger.thesis, acceptedCount: accepted.length, accepted, rejected, pr }
}

// ---------------------------------------------------------------------------
// Orchestrate
// ---------------------------------------------------------------------------
phase('Scan')
log(`Launching ${LANES.length} lanes (intensity=${INTENSITY}, lenses=${PROFILE.lenses.join('')}, dryStreak=${PROFILE.dryStreak}) off ${BASE.slice(0, 9)}: ${LANES.map((l) => l.section).join(', ')}`)
const results = await parallel(LANES.map((L) => () => runLane(L)))

return {
  run: RUN,
  base: BASE,
  intensity: INTENSITY,
  lanes: results.map((r) => r && (r.section ? { section: r.section, branch: r.branch, accepted: r.acceptedCount, rejected: (r.rejected || []).length, pr: ok(r.pr) ? (r.pr.url || r.pr.number) : null } : { error: r.error })),
  detail: results,
}
