# Sprint Tracking Issue — Template

The queue for an unattended sprint run. `/sprint` (local) writes one of these to
`peterdrier/Humans`; a routine fires a cloud session that works it.

Modelled on [`section-doctor`](../../.claude/skills/section-doctor/SKILL.md) — claim via
blocked set, escalate via Needs-Peter, one PR per batch. Deviate only with a reason.

## Issue shape

- **Title:** `sprint(YYYY-MM-DD): <n> batches, <m> issues`
- **Labels:** `sprint`, `sprint:YYYY-MM-DD`
- **Repo:** `peterdrier/Humans` only. Upstream issues are mirrored in by `/sprint`
  (body **and** comments verbatim, per
  [`issue-fetch-protocol`](../../memory/process/issue-fetch-protocol.md)) with a
  `nobodies-collective/Humans#N` backref. Closing the upstream original stays manual.

## Body

```markdown
## Batches

- [ ] **Batch 1 — <name>** · `claude/sprint-YYYY-MM-DD-batch-1`
  1. peterdrier/Humans#NNN — <title> · author: <login> · gate: <clear|GATED:reason>
  2. peterdrier/Humans#NNN — <title> · author: <login> · gate: clear
- [ ] **Batch 2 — <name>** · `claude/sprint-YYYY-MM-DD-batch-2`
  1. ...

## File partition

Batch 1: src/Sections/Humans.<X>/**
Batch 2: src/Sections/Humans.<Y>/**

## Needs-Peter

_(appended by runs; each entry dated, one line of context + the question)_
```

Every issue reference is qualified — [`issue-refs-qualified`](../../memory/process/issue-refs-qualified.md).

## Batching rules

**Batches must be file-disjoint.** Concurrent batches open concurrent PRs that must merge in
any order (`nobodies-collective/Humans#1069`). Two batches that touch the same paths belong in
one batch, sequenced. Record the partition in the body so a run can verify it.

Size to the existing shape: small related issues group into one batch; a large issue is its own
batch, split across subagents by the worker.

## Claiming

A run picks the **first unchecked batch whose branch has no open PR**. Compute the blocked set
exactly as `section-doctor` Phase 2 does — from open PRs, not from a label, so a died-mid-run
batch is reclaimable without cleanup. Never work a batch whose PR is already open.

## Gates — unattended

No one is at the gate: a routine run has no permission prompts. Every stop in
[`batch-worker`](../../.claude/agents/batch-worker.md) degrades to **skip the item, append to
Needs-Peter, keep going** — never to shipping the change.

- **Unauthorized author** (not `peterdrier` / `swombat`) — `/sprint` marks the item `GATED`
  at plan time; the run skips it. Never worked unattended without per-issue approval.
- **Privilege change** —
  [`privilege-changes-need-explicit-approval`](../../memory/process/privilege-changes-need-explicit-approval.md).
  Skip, record which users would gain what.
- **Spec change from `fb:` feedback** —
  [`triage-protocol`](../../memory/process/triage-protocol.md). Skip, record the delta.

A skipped item leaves its batch checkbox unticked and names the skip in the PR body.

## Completion

One PR per batch → `main` on `peterdrier/Humans`, branch `claude/sprint-<date>-batch-<n>`
(the `claude/` prefix is always accepted; other names are checked against protection and
others' commits). Tick the box on merge-ready, not on push. Sprint issue closes when every
box is ticked or every remainder sits in Needs-Peter.
