---
name: refactor-swarm
description: "Run multiple Reforge-guided section-refactor lanes in parallel, each in its own worktree/branch off origin/main, with a score-blind adversarial review panel gating every commit, one draft PR per section. Use when the user wants to reduce Reforge surface/internal score across several sections at once via architecturally-correct deletions (dead surface, entity-leak removal, cross-section read-splits) — not by relocation, parameter bags, or accessibility-dodging. Wraps the per-lane process in .codex/skills/humans-refactor. Triggers: 'refactor swarm', 'parallel section refactors', 'run reforge refactor on Users/Tickets/...', 'burn down surface score across sections'. Has an intensity dial (light|standard|deep) and a workflow|solo execution mode to trade token burn against autonomy per unit of output."
argument-hint: "[sections...] [--intensity=light|standard|deep] [--mode=workflow|solo] [--lanes=N] [--cross-section]"
---

# Refactor Swarm

## Purpose

Drive **several sections at once** toward the target Reforge shape — one small read surface, one write/full surface, one canonical `<Section>Info`, fewer cross-section/write-surface dependencies — by running independent refactor **lanes** concurrently. Each lane is the single-section [`humans-refactor`](../../../.codex/skills/humans-refactor/SKILL.md) loop (that skill is the per-lane source of truth for section-shape discovery, the candidate ledger, scoring, and stasis). This skill is the **orchestrator**: lane selection, conflict avoidance, the review panel, and the intensity/mode dials.

Reference run: 2026-05-29 (Users/Tickets/GoogleIntegration/Budget/Email) — 25 commits, 0 rejected, −1,073 combined section points, ~3.85× speedup vs sequential, 5 draft PRs.

## Hard rules (non-negotiable)

- **No DB/persistence changes of any kind** — no EF migrations, schema, DbContext/model, persistence-shape, or JSON-serialization-attribute edits. Repository-layer-and-above only.
- **Never `rm -rf`** — discard experiments with `git -C <wt> reset --hard HEAD` + `git -C <wt> clean -fd`; remove worktrees with `git worktree remove`. No bypass flags (`--no-verify`, suppressing analyzers, deleting tests to pass).
- **`[SurfaceBudget]` does NOT constrain this process.** The budget is a guardrail against *ad-hoc* surface growth during ordinary feature work; here the Reforge point system plus the score-blind panel already govern surface, so it is redundant. A lane MAY raise or extend a section's `[SurfaceBudget(n)]` when that is the correct way to route a consumer onto a read surface or expose a needed read fact — the panel still rejects bespoke projection/predicate methods, so this can't be abused. (Outside this process the budget remains user-controlled — never expand it during normal feature work.)
- Every git command is `git -C <abs-worktree> …`; every file path is under that worktree. Never operate on the main checkout (it auto-deploys).

## BUILD-FIRST (mandatory — see reforge#9)

`reforge surface-score` **silently under-reports ~4%** on a solution that has not had a real `dotnet build` — the cross-section / DI-registration / entity-return rules under-fire with no diagnostic (`diagnostics:0`, `typesAnalyzed` unchanged). Therefore:

1. The **baseline** score and **every lane's** scores MUST be taken on a worktree that has been `dotnet build`-ed first.
2. Build the baseline worktree before the baseline `surface-score`. Each lane builds before its first score (it builds anyway to verify).
3. **Never compare scores taken in different build states.** If baseline was unbuilt, re-measure it built before reporting any movement.

## Inputs / dials

- **sections** — explicit list, or empty to auto-select by recomputed Reforge rank.
- **`--intensity`** — trades token burn against autonomy-per-unit-output. **There is no fixed iteration cap.** Each lane runs its candidate ledger until *stasis* — the implementer reports no remaining high-confidence candidate — regardless of intensity. Intensity only sets how many review lenses gate each commit and how long a lane pushes through non-accepts (`dryStreak`) before giving up; a large `SAFETY_CAP` exists purely as a runaway backstop, never a target.

  | intensity | lanes | review lenses | persistence (`dryStreak`) | use when |
  |---|---|---|---|---|
  | `light` | 2–3 | 1 (A) | give up after 1 dry round | quick cheap sweep |
  | `standard` (default) | 4–5 | 2 (A, B) | give up after 2 consecutive dry rounds | the reference run |
  | `deep` | 5–6 | 3 (A, B, C) | persist through 3 dry rounds + completeness critic | overnight, maximize deletion |

  (Lane *count* is a dial; *which* sections is always derived from the Phase-0 Reforge rank minus in-flight work — never a fixed list.)

- **`--mode`** — `workflow` (default): parallel lanes in a background [Workflow](#orchestration-workflow-mode); `solo`: run lanes sequentially in-session (no Workflow tool, lower concurrency, easier to watch, far fewer tokens — good for 1–2 sections).
- **`--lanes=N`** — override lane count.
- **`--cross-section`** — enable [cross-section lanes](#cross-section-lanes) (a lane owns a consumer→provider read-split spanning two sections). Off by default because it widens blast radius and merge-conflict risk.

## Phase 0 — Recon & lane selection (in-session, before any worktree)

1. `git fetch origin`. Branch/baseline off **`origin/main`** (not local main).
2. Build a baseline worktree and score it **built**: `dotnet build` then `reforge surface-score --all --top-symbols 200 --format Json`. Rank sections by group total.
3. **Exclude in-flight sections** — any section with an open PR or active branch touching its service/repository/read interface (`gh pr list`, `git branch -r`). Two lanes must never edit the same files, interfaces, repositories, or DI registration. Confirm per-section DI (`src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs`) so lanes don't collide on registration.
4. Pick the top-N clean sections for the chosen intensity. Prefer repo-backed verticals with a canonical `<Section>Info`; connectors (Google/Email) are fine but yield deletion/read-split wins rather than DTO-fact wins.
5. Create one worktree+branch per lane off the baseline sha: `refactor/<YYYY-MM-DD>-<section>-workflow-N` at `.worktrees/refactor-<YYYY-MM-DD>-<section>-workflow-N`. **Verify each worktree's branch + base sha before fan-out.**

## Per-lane loop (delegated to humans-refactor)

Each lane: section-shape discovery → ≥8-candidate ledger → pick the highest-leverage coherent change → make ONE change → `dotnet build` + targeted tests (section + Architecture) → `reforge surface-score` (built) + `reforge_delta.py` vs the previous accepted stage → **review panel** → commit (with score + verdict appendix) + push → repeat until stasis. Stage scratch lives under `local/refactor-runs/<run>/<section>/` (outside the worktree, so it is never committed).

Seek (deletes a concept): cross-section consumers routed onto `I<Section>ServiceRead`/DTO facts; read projections/predicates/scalars **deleted** because callers derive them via LINQ over `<Section>Info`; duplicate query/include shapes collapsed; genuinely dead public surface removed; a real lifecycle state machine.

## Review panel (the gate — score-BLIND, adversarial)

The reviewer **never sees the score** and must not treat a lower number as a reason to accept. Score is the implementer's search heuristic and goes in the commit message for tracking only. Default verdict is **reject**; the implementer is assumed to be gaming a number until the diff proves a genuine structural improvement nameable in one sentence ("which concept was deleted?"). Commit only on **unanimous accept, zero rejects**.

Lenses (use 1 / 2 / 3 of these per `--intensity` light/standard/deep):

- **A — relocation & `internal`-gaming:** anything merely moved to a helper/extension/static/other section? Accessibility narrowed (`public`→`internal`) to drop the surface count with no callers deleted? If nothing is genuinely deleted → reject.
- **B — read-model purist:** a bespoke projection/predicate/scalar query method added to a read interface (e.g. "GetUsersNamedPeter") → reject (that's LINQ over `<Section>Info`). Consumers moved to `I<Section>ServiceRead`/DTO facts → good. A "new DTO" that's really a bag → reject.
- **C — behavior/contract:** tests/auth/cache/audit/transaction intact; domain verbs preserved (no generic action/mode dispatcher); removed surface verified dead.

Forbidden moves (reject regardless of score): helper/extension extraction for counts, bespoke read-interface query methods, `public`→`internal` to dodge score, parameter/options bags that only hide a signature, generic dispatchers, debt moved to another section, tests removed/weakened.

## Cross-section lanes (`--cross-section`)

The single-section model **cannot** complete a read-split when the consumer lives in section X and the needed read method lives on section Y's interface — both sides span sections, so each lane defers it. With `--cross-section`, a lane may own one such slice end-to-end **provided**: (a) it serializes against any section-level lane touching the same interfaces, and (b) the review panel additionally checks that the move *reduces*, not relocates, cross-section coupling (adding a read method to Y's interface so X can drop a full/write-service dependency is a reduction; growing Y's read surface without removing coupling is not). Expanding Y's `[SurfaceBudget]` to fit the migration is allowed here (see Hard rules).

## Orchestration (workflow mode)

Use `scripts/refactor-swarm.workflow.mjs` as the template. The `args` global is unreliable in the harness — after Phase 0, **hardcode the lane block** (section/group/branch/wt + baseline sha + paths) directly in the script, then launch with `Workflow({scriptPath})`. Lanes run in `parallel(...)`; each lane is internally sequential (scan → iterations → PR); the review panel uses `parallel` over lenses. Wrap every `agent()` in `safe()` so a transient API 500 fails one iteration, not the lane. The script journals each `agent()`, so a dead run resumes from cache via `Workflow({scriptPath, resumeFromRunId})`.

**Watchdog:** after launching, arm a `ScheduleWakeup` (~1200s) that checks transcript recency + lane commits + PRs; re-arm while running, `resumeFromRunId` if it died, stop when all lanes have PRs. This survives transient API 500s that would otherwise strand the run.

## Output

- One draft PR per lane that landed ≥1 commit, with the section thesis, accepted commits (concept deleted + built-baseline→final Reforge delta + panel verdict), cumulative score movement, rejected candidates with reasons, and remaining high-value work.
- A coordinator summary: per-section score movement (built-vs-built), token breakdown, sequential-vs-parallel timing.
- A **`needs-owner-approval` list**: genuine product/design decisions surfaced — overlapping projections to consolidate (which is canonical?), large crosscut restructures, deprecation-program campaigns — i.e. judgment calls, not mechanical refactors. (`[SurfaceBudget]` raises are NOT on this list — they're allowed in-process.)
