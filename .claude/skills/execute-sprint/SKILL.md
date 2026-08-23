---
name: execute-sprint
description: "Use when a sprint tracking issue (label `sprint`) on peterdrier/Humans needs working — routine-fired cloud runs, or any unattended session told to execute the sprint queue."
---

# Execute Sprint — Unattended Cloud Runs

Cloud-side half of the sprint process (peterdrier/Humans#1468). `/sprint` runs locally,
Peter approves which batches go up, and it publishes a tracking issue per
[`docs/sprints/TRACKING-ISSUE-TEMPLATE.md`](../../../docs/sprints/TRACKING-ISSUE-TEMPLATE.md).
A routine fires a .NET cloud session on that issue; this skill claims and works batches.
The local swarm variant is Peter's personal skill — this file is the unattended path only.

Ground rules (template + [`batch-worker`](../../agents/batch-worker.md) Unattended Mode):

- **No one is at the gate.** Every stop degrades to *skip the item, append to Needs-Peter,
  keep going* — never to shipping the change.
- **`gh` is unavailable.** GitHub MCP tools for all issue/PR API work; plain git for the tree.
- **Branches are `claude/sprint-<date>-batch-<n>`** — the `claude/` prefix is always accepted
  on push; other names are rejected on branch protection, others' commits, or others' open PRs.
- **Never merge anything.** The run ends at PRs open, boxes ticked, report posted.
- **Only `gate: clear` items are worked.** A `GATED` item is skipped and named in the PR body,
  whatever the reason on it says.

## Step 0: Setup

1. `mkdir -p local` (gitignored, absent in a fresh clone) and record the start:
   `date -u +%Y-%m-%dT%H:%M:%SZ > local/.run-start`.
2. `git fetch origin main --quiet`.
3. Toolchain gate: `dotnet build Humans.slnx -v quiet` must pass on `origin/main` before any
   batch is claimed. If the build cannot run at all, comment that on the tracking issue and
   stop — an unattended run cannot fix its own environment, and there is no docs-only fallback
   for implementation work.

## Step 1: Find the tracking issue

- Routine-fired: the issue arrives wrapped in `<routine-fire-payload>` — use that issue number.
- Otherwise: the newest open issue labeled `sprint` on `peterdrier/Humans`.

Parse the body: batches (checkbox, name, branch, numbered issues with author + gate status),
the `## File partition` block, the `## Needs-Peter` block. All refs are qualified
(`owner/repo#N`); an unqualified ref is a plan error — skip that item to Needs-Peter.

## Step 2: Claim a batch

1. Open-PR list via MCP, as the JSON shape section-doctor Phase 2 uses:
   `[{number, headRefName, title, files: [paths]}]`.
2. Blocked set = batches whose branch already has an open PR. **Claim = first unchecked,
   unblocked batch.** A batch that died mid-run has no open PR and is reclaimable with no
   cleanup; never work a batch whose PR is already open.
3. Nothing claimable → comment on the tracking issue (one line per batch: ticked / PR #N open /
   all-GATED) and stop.

## Step 3: Work the claimed batch

One batch at a time; when a batch's PR is open, return to Step 2 for the next claimable one.
Parallel batches are a growth step — do not add them until several sequential runs are clean.

1. `git checkout -B claude/sprint-<date>-batch-<n> origin/main`.
2. Fetch every `gate: clear` issue in the batch — body **and** comments — via MCP into
   `local/issue-<N>.txt` / `local/issue-<N>-comments.txt`. Comments are part of the spec;
   Peter's comments override the OP body.
3. **Staleness pre-check per issue:** `git log origin/main --oneline --grep "#<N>"` plus a
   merged-PR search via MCP. A hit means *probably shipped* — verify the acceptance criteria
   against the tree; if all are met, comment the per-criterion file:line evidence on the issue
   and close it via MCP. Never silently skip, never burn a worker discovering it.
4. Dispatch **one batch-worker subagent** for the batch, per
   [`.claude/agents/batch-worker.md`](../../agents/batch-worker.md) — Unattended Mode applies.
   Prompt contains: batch number/name, work order, spec file paths (worker reads the files),
   branch name, sprint date, and the instruction that every escape valve degrades to
   skip + record. Model explicit and tagged in the name (`batch-<n>-sonnet`): sonnet by
   default, opus-tier only where the batch carries architecture or deletion judgment.
   The worker implements and commits sequentially, one commit per issue; it never pushes.
5. Orchestrator pushes and opens the PR via MCP:
   - Title: `sprint(<date>) batch <n>: <name>`.
   - Body: one line per issue — completed issues get the qualified closing keyword
     `Fixes peterdrier/Humans#<N>`; skipped items are named with the reason; the batch's cost
     row (Step 4) is appended once measured.
6. Update the tracking issue via MCP: tick the batch checkbox once the PR is open and the
   worker's review gates passed; append any new Needs-Peter entries (dated, one line of
   context + the question).

**Migration-flagged batches:** work only if `dotnet ef` runs in this environment and the
migration generates cleanly; otherwise skip the whole batch to Needs-Peter. Never hand-edit a
migration, never author a data migration.

## Step 4: Expense report

Work is done in subagents, so the accounting is per batch. After the last batch:

```bash
python .claude/skills/execute-sprint/cost-report.py <date> "$(cat local/.run-start)"
```

The script finds this session's transcript, buckets the main thread as `orchestrator`, and
assigns each subagent transcript to its batch (the batch branch name appears in the worker's
prompt). It prints the component table with API-equivalent $. It never fails the run — on any
discovery problem it prints `Cost: unmeasured (...)`; cloud transcript layout is unverified,
so if the first runs report unmeasured, add that to Needs-Peter.

Post one **run report comment** on the tracking issue:

1. Headline table — one row per batch:

   | Batch | Issues | ~$ |
   |---|---|---|
   | 1 | peterdrier/Humans#1401, #1405, #1406 | 12.45 |
   | orchestrator | — | 1.10 |
   | **total** | | **13.55** |

2. The script's component/token-class table.
3. **Mini cost analysis — a short paragraph, not an essay:** where the money went (cache read
   vs output vs fresh input; which component dominated), and any glaring issue — a worker
   costing >2× the batch median, cache-write share suggesting context churn or re-reads,
   orchestrator cost rivaling a worker (too much in-band work), anything that looks like
   polling. If there is nothing glaring, say so in one line.

Also append each batch's own row to its PR body.

## Step 5: End of run

The run is complete when every batch is ticked, PR-blocked, or GATED/Needs-Peter — and the run
report comment is posted. Leftover Needs-Peter items are worked later by a `resume` run
applying Peter's answers, as in section-doctor. Do not loop waiting for CI; do not merge.

## Routine setup (documentation — configured on claude.ai, not in this repo)

- **Trigger:** new issue on `peterdrier/Humans` with label `sprint`.
- **Environment:** .NET cloud instance (the build gate in Step 0 depends on it).
- **Prompt:** must reference the payload explicitly or the fire text is treated as inert, e.g.:
  *"A sprint tracking issue was just created — its number is in `<routine-fire-payload>`.
  Invoke the `execute-sprint` project skill on it."*
