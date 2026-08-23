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

## Batch selection

**The batch is an input, not a discovery.** The orchestrator passes the worker its batch number
and work order (see [`batch-worker`](../../.claude/agents/batch-worker.md) § Input); a run never
scans the tracking issue for the next unclaimed batch. So there is no claim, no lock, no blocked
set, and no reclaim rule — two runs cannot race for the same batch, and a batch that stops for a
human is simply not dispatched again until someone dispatches it.

## Gates — unattended

No one is at the gate: a routine run has no permission prompts. **Pre-implementation** gates in
[`batch-worker`](../../.claude/agents/batch-worker.md) degrade to **skip the item, append to
Needs-Peter, keep going** — never to shipping the change. **Post-commit review failures stay hard
stops:** a batch whose spec, reuse or code review is still failing after 3 iterations is blocked,
opens no PR, and waits for a human.

- **Unauthorized author** (not `peterdrier` / `swombat`) — `/sprint` marks the item `GATED`
  at plan time; the run skips it. Never worked unattended without per-issue approval.
- **Privilege change** —
  [`privilege-changes-need-explicit-approval`](../../memory/process/privilege-changes-need-explicit-approval.md).
  Skip, record which users would gain what.
- **Spec change from `fb:` feedback** —
  [`triage-protocol`](../../memory/process/triage-protocol.md). Skip, record the delta.

A skipped item leaves its batch checkbox unticked and names the skip in the PR body. If a gate
fires after the item already has a commit, its commits are reverted before the run continues.

## Completion

One PR per batch → `main` on `peterdrier/Humans`, branch `claude/sprint-<date>-batch-<n>`
(the `claude/` prefix is always accepted; other names are checked against protection and
others' commits). Tick the box on merge-ready, not on push. Sprint issue closes when every
box is ticked or every remainder sits in Needs-Peter.
