---
name: plan-sprint
description: "Use when planning a Humans sprint — grooming the backlog into work batches and publishing the Peter-approved subset as a cloud sprint tracking issue. Runs locally with gh access to both repos."
argument-hint: "[quick] [section:<name>] [migrations]"
---

# Plan Sprint (Humans)

Groom open issues into assessed, file-disjoint work batches; write the plan to
`local/sprint-{date}.md`; then — **only for the batches Peter explicitly approves** — publish a
tracking issue per [`docs/sprints/TRACKING-ISSUE-TEMPLATE.md`](../../../docs/sprints/TRACKING-ISSUE-TEMPLATE.md)
that fires the cloud [`execute-sprint`](../execute-sprint/SKILL.md) routine.

Project-specific replacement for Peter's global `/sprint` (peterdrier/Humans#1468) — do not use
the global one on this repo. Runs locally: it needs `gh` against both repos, which cloud
sessions don't have.

## Arguments

- *(none)* — full backlog analysis with batch proposals
- `quick` — skip research, use each issue's existing `## Sprint Metadata` / labels only
- `section:<name>` — focus on one section (bare name accepted)
- `migrations` — only show items that need EF Core migrations

## Step 1: Gather data

Issues live on two repos (`memory/process/issue-home-routing.md`): **upstream**
`nobodies-collective/Humans` (community feedback/direction) and **origin** `peterdrier/Humans`
(bugs/tech-debt/agent-created). Sweep both; qualify every ref (`owner/repo#N`) in all output.

```bash
gh issue list --repo nobodies-collective/Humans --state open --limit 200 --json number,title,body,labels,createdAt,comments,author
gh issue list --repo peterdrier/Humans --state open --limit 200 --json number,title,body,labels,createdAt,comments,author
# --limit is a hard cap, not a page size — if a repo ever returns exactly 200, raise it and re-run

```

**Pipeline status** (origin = QA, upstream = production):

```bash
git fetch origin --quiet; git fetch upstream --quiet
git log --oneline upstream/main..origin/main
gh pr list --repo peterdrier/Humans --state open --limit 200 --json number,title,headRefName
gh pr list --repo peterdrier/Humans --state merged --limit 50 --json number,title,mergedAt,headRefName
# default --limit is 30 — too low to classify the pipeline; raise if either returns its cap
```

| Stage | Detection | For planning |
|-------|-----------|--------------|
| **Open** | No PR, not in any commit | Available — batch it |
| **In Review** | Open PR on origin | In-flight — don't re-batch |
| **In QA** | Merged to origin, not in upstream | Done coding — don't batch |
| **Shipped** | In upstream/main or issue closed | Done — surface for closing |

**Only a closing verb counts** (`Closes|Fixes|Resolves|Implements|Finishes #NNN`). A bare
`#NNN`, `Refs`, `Part of`, "9 of 10 criteria; issue stays open" — the issue is still open; when
verb and disclaimer disagree, the disclaimer wins. Upstream issues never auto-close from fork
merges — list finished-but-open ones as **Done but still open — close these**, with evidence.

**Sequence and pace:** `blocked-by: owner/repo#NNN` in a body excludes the issue until the
blocker closes (at most one issue per sequence per sprint). `pace:N` labels cap that group at N
per sprint; overflow goes to Unbatched with the reason.

## Step 2: Assess each item

Use an existing non-stale `## Sprint Metadata` section if the body has one; otherwise assess.

**Business importance:** Critical (blocking users / data loss / security / prod broken) · High
(active user pain, coordinator blocker, deadline) · Medium (UX, requested feature, prevention) ·
Low (nice-to-have, tech debt). Weight `/triage`-originated community reports higher.

**Size:** XS (<30 min) · S (≤2 h) · M (2–6 h) · L (6–16 h) · XL (16+ h).

**Tier** (drives worker model + review depth): XS→`direct`, S→`lightweight`, M→`standard`,
L/XL→`thorough`; override when complexity doesn't match size.

**Author + gate status** — record for every issue (`memory/process/issue-fetch-protocol.md`):
`peterdrier`/`swombat` → `gate: clear`; anyone else → `gate: GATED:unauthorized-author`. GATED
items batch normally but never dispatch unattended; a comment from Peter giving direction counts
as approval — cite it and mark `gate: clear (per comment YYYY-MM-DD)`.

**Privilege / spec-change carve-out.** Capability grants ([`memory/process/privilege-changes-need-explicit-approval.md`](../../../memory/process/privilege-changes-need-explicit-approval.md))
and `fb:`-derived spec changes ([`memory/process/triage-protocol.md`](../../../memory/process/triage-protocol.md))
are never `direct`/`lightweight`: owner-authored with explicit approval → `thorough` +
`🔒 privilege change — owner-authored` flag; otherwise → a separate **Needs Owner Review**
section, body verbatim, never batched.

**Owner-operated work** (host/environment/third-party console/manual ops — nothing would show in
a diff) gets its own section *above* the batches, never a lane.

**Section:** the `section:*` labels are authoritative (`gh label list --repo
nobodies-collective/Humans --limit 200 --search "section:"`). First label = primary; extras =
secondary (the batch touches all of them). Missing label = infer for grouping + list in a
**Missing section label** footer. Cross-cutting buckets (`section:infra`, `section:ui`) don't
predict file overlap — check bodies.

**Migration?** `yes`/`no`/`maybe`. Every section owns its own DbContext and migrations folder,
so migrations serialize **within** a section only — two sections' migrations are independent.

## Step 3: Group and batch

Bucket by primary section; sort by importance then size. **Default one section per batch** —
same project, same DbContext, file-disjoint by construction. Mix sections only for: a
cross-cutting sweep, a seam (two sections meeting at one interface), or the `direct`-tier quick-
fixes batch.

**A lane is a queue of PRs, not issues.** Bundle by default — one unit = one agent session = one
PR = as many issues as sensibly fit; multiple schema changes in one section are ONE migration.
Split only for: independently rejectable decisions, ship-first bugs, L/XL items, a deliberate
two-step rollout, or different subsystems inside one section. State units per batch:
`**Units:** U1 = #a + #b · U2 = #c`.

**Batch ordering:** Critical/High first; `direct` issues collapse into one early "Quick fixes"
batch; migrations serialize within a section only; **never plan stacked branches** — every batch
branches off `origin/main`; if one needs another's code, sequence it across sprints or design it
file-disjoint.

Batch format:

```
### Batch {N}: {name}
**Priority:** … · **Total effort:** … · **Migration:** … · **Section:** … (+secondaries)

| # | Title | Author | Gate | Biz | Size | Tier | Migration |
|---|-------|--------|------|-----|------|------|-----------|

**Work order:** 1. … 2. …
**Units:** …
**Files:** {the path globs this batch owns — feeds the tracking issue's partition block}
**Rationale / Parallel-safe / Depends on:** …
```

## Step 4: Write the plan

`local/sprint-{YYYY-MM-DD}.md` (gitignored): summary (totals, pipeline counts, by-section),
in-flight list, batches in execution order, Unbatched (every open item appears somewhere, each
with a reason — owner-operated, blocked, sequenced, paced, needs investigation, in-flight, on
hold; "too large" is not a reason), Needs Design Session footer, sequence chains.

## Step 5: Publish the cloud queue (Peter-gated — HARD GATE)

**The plan is a proposal; the queue is Peter's decision.** Never publish every batch by default,
never treat silence as a go — creating the labeled issue IS the dispatch trigger, so nothing is
created until the selection is explicit.

1. Present the batch list — one line per batch (number, name, issues, size, gate flags) — and
   ask which batches go to the cloud queue. Recommend the smallest low-risk batch(es) to start.
   Gating question: its own message, banner it in long sessions.
2. For each approved batch:
   - **Mirror upstream issues into the fork** — the cloud session cannot reach
     `nobodies-collective` issues. `gh api` the body and every comment; create the mirror on
     `peterdrier/Humans` verbatim (body, then a `## Comments (mirrored)` section), first line
     `Mirrored from nobodies-collective/Humans#N.` The tracking issue references the mirror's
     number. Mirrors never get the `sprint` label.
   - **Re-verify gate status** per issue; Peter can flip a GATED item here — record
     `gate: clear (approved YYYY-MM-DD)`.
   - **Verify approved batches are file-disjoint** (their `**Files:**` globs must not overlap —
     concurrent PRs must merge in any order) and build the `## File partition` block.
3. Ensure labels: `gh label create sprint --repo peterdrier/Humans` and
   `gh label create "sprint:{date}" --repo peterdrier/Humans` (ignore already-exists).
4. Create the tracking issue per the template — title `sprint(YYYY-MM-DD): <n> batches, <m>
   issues`, both labels, complete body — in ONE `gh issue create` call. The routine fires on
   creation of the labeled issue, so the body must be finished at creation.
5. Report the issue URL. Unapproved batches stay local-only in the plan file — never carried
   into the tracking issue in any form.
