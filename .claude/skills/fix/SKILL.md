---
name: fix
description: "Adversarial triage of open review findings on a PR. Every finding (Codex, Claude bot, Gemini) is a hostile hypothesis to REFUTE — fix only what is proven real, reachable, in the PR's scope, and above the round's severity bar. The bar rises every review round. Everything else is declined or filed as an issue. Use for 'fix the PR findings', '/fix', '/fix 123', or before any fix commit in a review loop."
argument-hint: "(none — current branch's PR) | 123 | #123 | URL"
allowed-tools: "Bash(git:*), Bash(gh:*), Bash(jq:*), Bash(dotnet:*), Read, Edit, Write, Grep, Glob, Agent"
---

# Fix — refute first, fix last

The default verdict is **REFUTED**. A finding earns a fix only by surviving every gate below. Reviewer bots are wrong more often than right on this codebase; treat their output as borderline hostile. Blind-fixing has been piling guards for impossible states on top of each other and turning clean PRs into a mess — that stops here.

Mechanics (fetch from both repos, per-thread reply, resolve, 👍/👎, no `@codex review`): [`memory/process/pr-review-feedback-handling.md`](../../../memory/process/pr-review-feedback-handling.md). Posture: [`memory/process/review-finding-triage.md`](../../../memory/process/review-finding-triage.md). This skill is the judgment step those atoms defer to.

## 1. Resolve the PR and the round

`$ARGUMENTS` → PR number / current branch / URL via `gh pr view`. Record owner, repo, base, head SHA.

**Round** = number of bot review submissions on the PR so far (both repos):

```bash
gh api repos/<owner>/Humans/pulls/<N>/reviews --paginate \
  --jq '[.[] | select(.user.type=="Bot" or (.user.login|test("codex|claude|gemini";"i")))] | length'
```

Zero bot reviews but inline comments exist → round 1. The round is stated in the triage table and drives the severity bar in §4.

## 2. Fetch unresolved findings

Per the feedback-handling atom: three endpoints + GraphQL `reviewThreads`, both repos, skip `isResolved` / `isOutdated`. Also `gh pr diff <N> --repo <owner>/Humans` and `gh pr view <N> --repo <owner>/Humans --json body,title` (owner from §1 — fork and upstream reuse PR numbers) — the diff hunks define "in scope", the body defines the PR's intent.

## 3. Gates — run in order, first failure ends the finding

For **each** finding, write the verdict before touching code. Burden of proof is on the finding, not on you.

| # | Gate | Fails when | Verdict |
|---|------|-----------|---------|
| 1 | **True?** Read the actual code at the cited lines, not the diff context. | The claim about what the code does is false. | `INVALID` 👎 |
| 2 | **Reachable?** Write the concrete path: entry point → inputs → state → wrong outcome, using the app's real UI/routes/jobs. | The path needs a state the app never produces: violates an invariant enforced elsewhere, emailless user, concurrent writers on a small single-server deployment, malformed rows the app never writes, a `null` the constructor/EF/`[Required]` already excludes, a rollback path that never runs. | `IMPOSSIBLE` 👎 |
| 3 | **Posture?** Cross-check `memory/INDEX.md` HARD RULE / false-positive atoms. | It asks for pagination, concurrency tokens, check constraints, startup guards, defensive nulls, emailless handling, perf guards, or anything a hard-rule atom already rejects. | `WONTFIX` 👍 |
| 4 | **Non-trivial?** | Style, naming, "consider", "might be clearer", log wording, comment text, a null-check on a value that can't be null, micro-perf, "add a test for X" where X is already covered by behaviour. | `WONTFIX` 👍 — no issue |
| 5 | **In scope?** The cited lines are in this PR's diff hunks, or the behaviour was introduced by this PR. | Pre-existing code the PR only sits next to, moved, or renamed; a different section; a "while you're here". | `OUT-OF-SCOPE` 👍 → §5 unless P0/P1 |
| 6 | **Above the round's bar?** (§4) | Severity is below the bar for this round. | `DEFER` 👍 → §5 |
| 7 | **Clean fix?** The fix removes or corrects logic; it does not add a guard, branch, try/catch, fallback, or new type for a state the app can't reach. | The only fix you can see is a new defensive branch. That is evidence the finding failed gate 2 — go back and decline it. | `IMPOSSIBLE` 👎 |

Only a finding that passes all seven is `FIX`.

**Severity, for gates 5–6** (assign once, from the reachable path in gate 2):

- **P0** — data loss/corruption, money wrong, auth/authz bypass, sign-in blocked, prod won't boot, PII exposed.
- **P1** — the PR's stated feature doesn't work for a normal user on the happy path, or a hard-rule violation.
- **P2** — real bug on a path a user hits rarely, or degraded-but-working behaviour.
- **P3** — cosmetic, edge-of-edge, theoretical.

## 4. The bar rises every round

| Round | Fix only if | Everything else |
|-------|-------------|-----------------|
| 1–2 | P0, P1, or an in-scope P2 with a one-file clean fix | decline / file |
| 3–5 | P0 or P1 | file if P2, decline if P3 |
| 6–9 | P0 | file if P1–P2, decline the rest |
| 10+ | P0 that would hurt a real person or lose real data | file P0/P1 for a separate PR; decline the rest; state in the summary that the PR is done being reviewed |

A re-flag of a finding already declined in an earlier round is not new evidence — repeat the disposition, resolve, move on. A finding whose fix would touch more than one file or add a new public surface is one severity level lower for bar purposes: at round ≥3 that means it doesn't get fixed.

## 5. File an issue — only when reasonable

`OUT-OF-SCOPE` and `DEFER` findings become issues **only** if all hold: real (passed gates 1–2), P2 or higher, and not already tracked (`gh issue list --repo nobodies-collective/Humans --search "<keywords>" --state all`). Otherwise decline with a one-line reason and no issue.

Issues live on `nobodies-collective/Humans` (search and create with `--repo`); the thread reply cites the owner-qualified ref (`nobodies-collective/Humans#N`, per `issue-refs-qualified`). Issue body: section label/line (`**Section:** X`), the reachable path from gate 2, link to the PR thread. Title states the bug, not the finding ("Camp roster export drops members without a ticket", not "Codex finding on PR 1234"). Never file P3s. Never file "consider refactoring".

## 6. Emit the triage table, then act

Print the table **before any edit**, one row per finding. In unattended runs this table is the audit trail; a run that fixes without having printed it is wrong.

```
Round 4 · PR #1234 · bar: P0/P1

| # | file:line | claim (≤12 words) | gate failed | sev | verdict | action |
|---|-----------|-------------------|-------------|-----|---------|--------|
| 1 | Foo.cs:88 | null ref if team has no lead | 2 impossible | — | IMPOSSIBLE | reply + resolve + 👎 |
| 2 | Bar.cs:12 | export skips ticketless members | — | P2 | DEFER | issue #N + reply + resolve + 👍 |
| 3 | Baz.cs:40 | wrong role checked on POST | — | P0 | FIX | fix + reply + resolve + 👍 |
```

Then:

1. **FIX rows only:** apply the fix; one commit per run, message listing each fixed finding, ending with a `Review-round: <n>` trailer — that trailer is what the five-round commit budget counts, so a round commit without one is a round spent invisibly (`memory/process/review-round-budget.md`). `<n>` is this commit's budget round (spent + 1, from the steward count), which is not §1's round: §1 counts bot review submissions to set the severity bar, the budget counts round commits. Build: `dotnet build Humans.slnx -v quiet`. Push (no force).
2. **Every row:** reply in-thread with the verdict and one line of reasoning (`INVALID — line 88 is inside the `if (lead != null)` block`; `IMPOSSIBLE — a team without a lead can't be saved, see TeamService.ValidateAsync`; `DEFER — P2, out of round-4 bar, opened #N`), react, resolve. Nothing left open unless a live disagreement with Peter is pending.
3. **Summary** to the user: round, counts per verdict, SHA pushed (if any), issues opened. If round ≥ 10, end with: "PR has hit the review ceiling — further bot rounds get no fixes."

## What this skill never does

- Work through reviewer output as a checklist.
- Add a guard, fallback, or branch for a state the app cannot reach.
- Fix something outside the diff because it was "easy".
- Fix a P2 after round 2, or anything but a P0 after round 5.
- File an issue for a P3 or a style opinion.
- Ping `@codex review` or ask for a re-review.
- Ask Peter whether to fix a finding — the gates decide; if the gates genuinely can't, it's `WONTFIX` with the reason stated.
