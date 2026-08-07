---
name: pr-prod
description: "Promote QA → production by opening a PR from peterdrier/Humans:main to nobodies-collective/Humans:main with a properly-qualified commit summary. Use when the user says \"PR to production\", \"promote to prod\", \"PR from origin to upstream\", \"prod PR\", or any variation of pushing batched fork changes upstream. Always use this skill for the two-remote promotion flow even if the user doesn't say \"prod\" explicitly."
argument-hint: "(no args)"
---

# Promote QA → production

Promotes the batch through staging and opens a single batched PR from `peterdrier/Humans:main` (peter's fork, QA-deployed) to `nobodies-collective/Humans:main` (production). Per `CLAUDE.md`, the upstream merge strategy is **rebase merge** — individual PRs were already squashed on the fork.

The batch goes to `upstream/staging` first, where it deploys against a fresh clone of the production database, and only reaches `upstream/main` once that deploy has been verified — `docs/staging-environment.md` (nobodies-collective/Humans#962). The PR is opened **from the verified commit itself**, on a one-shot `promote/*` branch rather than from the fork's moving `main`, so `upstream/main` never moves to a commit whose migrations have not already run against real production data.

## The ref-qualification rule (read this first)

The repo has two GitHub remotes with overlapping issue/PR numbers — `peterdrier/Humans` (fork, where QA PRs land) and `nobodies-collective/Humans` (upstream, where issues are tracked and production lives). `memory/process/issue-refs-qualified.md` requires **every** ref to be qualified with its repo. Bare `#NNN` is banned everywhere — PR bodies, commit messages, comments, chat. The reason is human disambiguation, not GitHub auto-linking: a reader (or a future agent) seeing `#673` in any context cannot tell which tracker it points at, and historical mixups have closed wrong issues and linked wrong PRs.

| Ref kind in commit subject | What it means | How to write it |
|---|---|---|
| Trailing `(#NNN)` | peter-fork squash-merge PR number | `peterdrier/Humans#NNN` |
| `issue-NNN:` prefix | nobodies-collective issue number | `nobodies-collective/Humans#NNN` |
| Inline `#NNN` in commit body/subject | Almost always a nobodies-collective issue (verify) | `nobodies-collective/Humans#NNN` |

Short forms `peterdrier#NNN` and `nobodies-collective#NNN` are also accepted by the rule, but the full `owner/repo#NNN` form is unambiguous and is what this skill emits.

## Steps

### 1. Fetch both remotes

```bash
git fetch origin main
git fetch upstream main
```

`origin` is `peterdrier/Humans` (the fork). `upstream` is `nobodies-collective/Humans` (production). Don't assume — verify with `git remote -v` if there's any chance the convention has drifted.

### 2. Check for an existing open PR

```bash
gh pr list --repo nobodies-collective/Humans --state open --base main \
  --json number,title,headRefName --jq '.[] | "\(.number) \(.headRefName) — \(.title)"'
```

Don't filter on `--head peterdrier:main` — since step 6 each promotion opens from its own `promote/*` branch, so an exact-head filter matches nothing.

If an open promotion is for **this** batch, **edit it** with `gh pr edit <num> --repo nobodies-collective/Humans --body ...` instead of opening a duplicate. If it is for an earlier batch that never merged, stop and ask Peter — two open promotions racing the same base is his call, not a thing to resolve by opening a third.

### 3. Enumerate the commits to promote, and pin the SHA

```bash
git log --oneline upstream/main..origin/main
git diff --stat upstream/main..origin/main | tail -1
PROMOTE_SHA=$(git rev-parse origin/main)
```

If empty: nothing to promote. Tell the user and stop.

`$PROMOTE_SHA` **is** the batch, and every step from here names that commit rather than the `origin/main` branch. The branch moves — feature PRs land on the fork continuously — and a promotion that tracked it would quietly grow past the commit staging verified.

### 4. Promote to `upstream/staging`

```bash
git push upstream "$PROMOTE_SHA":refs/heads/staging
```

This is the deploy trigger: `.github/workflows/staging-db.yml` rebuilds `humans_staging` from the newest production backup artifact, and Coolify deploys the pushed commit against it.

**A non-fast-forward rejection here is expected from the second cycle onwards, and does not mean a batch was abandoned.** Upstream merges by rebase, so last cycle's promotion sits on `upstream/main` under rewritten SHAs, and `memory/process/after-prod-merge-reset.md` then resets `origin/main` onto that rewritten history. `upstream/staging` still points at the pre-rebase commit, which is no longer an ancestor of anything on the fork. Realigning it is a forced update, and forced updates need Peter's explicit per-instance approval (`memory/process/no-destructive-actions-without-approval.md`) — **stop and ask; never pass `--force` or `--force-with-lease` here on your own initiative.** See [Open question](#open-question--realigning-upstreamstaging) below.

### 5. Verify on staging

Wait for the refresh workflow and the Coolify deploy, then run the checks in `docs/staging-environment.md` §8. The load-bearing ones:

```bash
curl -s https://staging.nobodies.team/api/version     # commit == the SHA you pushed
```

- Migrations applied cleanly to the fresh clone — this is the dress rehearsal, and a migration that would break production breaks here instead.
- Real Google sign-in works.
- Whatever this batch changed, exercised against real-shaped data.

**If anything fails, stop.** The bad commit is on `staging` only; fix it on the fork and re-run from step 1. Do not open the production PR.

`/api/version` must report `$PROMOTE_SHA` itself, not merely "something recent" — that equality is what ties the verification to the commit the next step promotes.

### 6. Publish the promotion head

```bash
PROMOTE_BRANCH="promote/$(date -u +%Y-%m-%d)-$(git rev-parse --short "$PROMOTE_SHA")"
git push origin "$PROMOTE_SHA":"refs/heads/$PROMOTE_BRANCH"
```

A fresh branch per promotion, pointed at the verified commit and written exactly once. **The PR opens from this, never from `peterdrier:main`.** A PR whose head is `main` follows the branch: any feature PR that lands on the fork between staging verification and Peter's merge is carried into production without ever having deployed against the cloned database, while the PR body goes on naming the older SHA as verified. That is the whole guarantee this skill exists to provide, and a branch head silently voids it.

Nothing else writes to `$PROMOTE_BRANCH`, so it is immutable in practice and never needs a force-push. Leave it in place after the merge — it is the record of exactly what was promoted, and deleting branches needs Peter's approval anyway (`memory/process/no-destructive-actions-without-approval.md`).

### 7. Build the PR body

For each commit, transform the subject as follows:

- Strip the trailing `(#NNN)` peter-fork PR ref and re-emit at the end as `(peterdrier/Humans#NNN)`.
- Convert `issue-NNN:` prefix to `nobodies-collective/Humans#NNN:`.
- Inline `#NNN` references in the subject (e.g. "codex findings on #667") become `nobodies-collective/Humans#NNN` after verifying it's a nobodies issue. If unsure, check both repos with `gh pr view NNN --repo peterdrier/Humans` and `gh issue view NNN --repo nobodies-collective/Humans` (the rule applies regardless: don't emit bare `#NNN`).
- Commits without a peter-fork PR ref (direct-to-main commits, rare) just get listed without the trailing parenthetical.

**Worked example** — given this commit:

```
8508e353 issue-673: consolidate person-search with PersonSearchFields bit-flag API (#455)
```

emit this bullet:

```
- `8508e353` nobodies-collective/Humans#673: consolidate person-search with PersonSearchFields bit-flag API (peterdrier/Humans#455)
```

### 8. Write the PR

Use `gh pr create` with `--head peterdrier:$PROMOTE_BRANCH`. Pass the body via heredoc to preserve formatting:

```bash
gh pr create --repo nobodies-collective/Humans \
  --base main --head "peterdrier:$PROMOTE_BRANCH" \
  --title "Promote QA → production (N commits)" \
  --body "$(cat <<'EOF'
## Summary

Batched promotion of QA-tested changes from `peterdrier/Humans` to production.

Verified on staging at `<staging sha>` — `https://staging.nobodies.team` deployed this batch against a fresh clone of the production database, migrations applied cleanly, real sign-in works.

**This PR's head is that commit**, not the fork's moving `main`, so what merges is what staging ran.

All issue/PR refs are qualified per `memory/process/issue-refs-qualified.md` — `peterdrier/Humans#NNN` for peter-fork PRs, `nobodies-collective/Humans#NNN` for upstream issues.

## Commits

- `<sha>` <transformed subject>
- ...

## Test plan

- [ ] Rebase merge (each PR is already squashed)
- [ ] CI green on production
- [x] EF migrations verified against a production-data clone on staging
EOF
)"
```

Substitute `N` with the actual commit count and `<staging sha>` with `$PROMOTE_SHA`, the SHA verified in step 5.

### 9. Return the PR URL

`gh pr create` prints the URL on success — surface it to the user. Don't merge; promotion is Peter's call. Rebase merge replays these commits onto `upstream/main` under new SHAs — same trees, different history, which is the reason step 4 stops being a fast-forward next cycle.

### 10. Discord release notes

After surfacing the PR URL, draft member-facing release notes for Discord and present them in the conversation as a single copy-paste-ready ```markdown code block (Claude does NOT post to Discord — Peter pastes it).

Rules:

- **Header**: `**🚀 Humans update — YYYY-MM-DD**` (today's date).
- **Audience is regular members.** Lead with features regular users can see and use (paying for orders, shifts, events, tickets, profile, help). Internal/admin-only changes may be mentioned, but lower in the post and briefer.
- **Translate, don't transcribe.** Rewrite commit subjects as benefits in plain language ("You can now pay camp orders with SEPA…"), never jargon ("async-payment state machine"). No SHAs, no PR/issue refs, no section names from the codebase.
- **Skip entirely**: docs/maintenance commits, refactors with no behavior change, test-infra changes. A closing "🔧 Under the hood" bullet or two may summarize the invisible work in one breath.
- **Group with emoji headers** by user-facing area (e.g. 🛒 Store & payments, 📅 Shifts & events, 🎟️ Tickets & door, 🧭 Admin & navigation, 🔧 Under the hood) — pick groups that fit the batch, don't force empty ones.
- **Hard limit: 2,000 characters** (Discord's per-message cap). Count the draft; if over, cut admin/under-the-hood detail first, never the member-facing items.

## Sanity checks before submitting

- [ ] Every `(#NNN)` from a commit subject is rewritten as `(peterdrier/Humans#NNN)` in the body.
- [ ] Every `issue-NNN:` prefix is rewritten as `nobodies-collective/Humans#NNN:`.
- [ ] Every inline `#NNN` reference is qualified (`peterdrier/Humans#NNN` or `nobodies-collective/Humans#NNN`) — no bare refs anywhere in the body.
- [ ] Title is `Promote QA → production (<N> commits)` with the correct count.
- [ ] No existing open PR was overlooked (step 2).
- [ ] The batch deployed to staging and passed step 5, and the body names the verified SHA.
- [ ] The PR head is `peterdrier:promote/<date>-<short sha>`, **not** `peterdrier:main`, and `gh pr view <num> --repo nobodies-collective/Humans --json headRefOid` returns `$PROMOTE_SHA`.
- [ ] Discord release notes drafted (step 10), dated, member-features first, ≤ 2,000 characters.

## Open question — realigning `upstream/staging`

**For Peter. Until it is answered, step 4 stops and asks on every cycle after the first.**

The promotion cycle structurally requires one forced update of `upstream/staging` per cycle. Rebase merge rewrites the batch's SHAs onto `upstream/main`, `after-prod-merge-reset` resets the fork onto that rewritten history, and `upstream/staging` is left holding a commit that is nobody's ancestor. There is no non-forced way out: the branch has to be rewritten because upstream rewrote the commits under it.

The argument for making it standing rather than per-instance: `upstream/staging` is a disposable pointer at "the batch currently under verification". It is never merged anywhere, its database is dropped between cycles by design (`docs/staging-environment.md` §6), and its history carries nothing that could be lost. `--force-with-lease` would still refuse if someone else had moved it.

The argument against: `memory/process/no-destructive-actions-without-approval.md` is a hard rule with exactly one standing exception today (the squash-merge button), and `after-prod-merge-reset`'s `--force-with-lease` on `origin/main` is a documented procedure Peter wrote, not a precedent an agent gets to extend to a second remote on its own.

Recommended: realign as part of the post-merge reset, when the two commits have identical trees and the operation is unambiguous —

```bash
git push upstream upstream/main:refs/heads/staging --force-with-lease
```

— added to `memory/process/after-prod-merge-reset.md` so it is Peter's standing instruction rather than an agent's judgement call. **Not made here**, for the reason in the paragraph above.

## What this skill does NOT do

- Merge the PR. Peter does that manually with rebase merge.
- Force-push `upstream/staging`, or skip the staging verification because the batch "looks safe". Both need Peter — see the open question above.
- Delete the `promote/*` branch after the merge. It stays as the record of what was promoted.
- Post to Discord. The release notes are drafted in-conversation for Peter to paste.
- Update fork main after merge. The `memory/process/after-prod-merge-reset.md` rule covers post-merge.
- Open or modify per-feature PRs on `peterdrier/Humans`. Those land on the fork before promotion runs.
