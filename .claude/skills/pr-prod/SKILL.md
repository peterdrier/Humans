---
name: pr-prod
description: "Promote QA → production by opening a PR from peterdrier/Humans:main to nobodies-collective/Humans:main with a properly-qualified commit summary. Use when the user says \"PR to production\", \"promote to prod\", \"PR from origin to upstream\", \"prod PR\", or any variation of pushing batched fork changes upstream. Always use this skill for the two-remote promotion flow even if the user doesn't say \"prod\" explicitly."
argument-hint: "(no args)"
---

# Promote QA → production

Promotes the batch through staging and opens a single batched PR from `peterdrier/Humans:main` (peter's fork, QA-deployed) to `nobodies-collective/Humans:main` (production). Per `CLAUDE.md`, the upstream merge strategy is **rebase merge** — individual PRs were already squashed on the fork.

The batch goes to `upstream/staging` first, where it deploys against a fresh clone of the production database, and only reaches `upstream/main` once that deploy has been verified — `docs/staging-environment.md` (nobodies-collective/Humans#962). The PR records the verified staging SHA, so `upstream/main` never moves to a commit whose migrations have not already run against real production data.

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
gh pr list --repo nobodies-collective/Humans --state open --head peterdrier:main
```

If one exists, **edit it** with `gh pr edit <num> --repo nobodies-collective/Humans --body ...` instead of opening a duplicate.

### 3. Enumerate the commits to promote

```bash
git log --oneline upstream/main..origin/main
git diff --stat upstream/main..origin/main | tail -1
```

If empty: nothing to promote. Tell the user and stop.

### 4. Promote to `upstream/staging`

```bash
git push upstream origin/main:refs/heads/staging
```

This is the deploy trigger: `.github/workflows/staging-db.yml` rebuilds `humans_staging` from the newest production backup artifact, and Coolify deploys the pushed commit against it.

**If the push is rejected as a non-fast-forward, stop and ask Peter.** It means the previous batch was staged and then abandoned rather than promoted. Recovering needs a force-push, which requires Peter's explicit per-instance approval (`memory/process/no-force-push-without-permission.md`) — never pass `--force` or `--force-with-lease` here on your own initiative.

### 5. Verify on staging

Wait for the refresh workflow and the Coolify deploy, then run the checks in `docs/staging-environment.md` §8. The load-bearing ones:

```bash
curl -s https://staging.nobodies.team/api/version     # commit == the SHA you pushed
```

- Migrations applied cleanly to the fresh clone — this is the dress rehearsal, and a migration that would break production breaks here instead.
- Real Google sign-in works.
- Whatever this batch changed, exercised against real-shaped data.

**If anything fails, stop.** The bad commit is on `staging` only; fix it on the fork and re-run from step 1. Do not open the production PR.

Record the verified SHA — step 6 puts it in the PR body.

### 6. Build the PR body

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

### 7. Write the PR

Use `gh pr create` with `--head peterdrier:main`. Pass the body via heredoc to preserve formatting:

```bash
gh pr create --repo nobodies-collective/Humans \
  --base main --head peterdrier:main \
  --title "Promote QA → production (N commits)" \
  --body "$(cat <<'EOF'
## Summary

Batched promotion of QA-tested changes from `peterdrier/Humans:main` to production.

Verified on staging at `<staging sha>` — `https://staging.nobodies.team` deployed this batch against a fresh clone of the production database, migrations applied cleanly, real sign-in works.

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

Substitute `N` with the actual commit count and `<staging sha>` with the SHA verified in step 5.

### 8. Return the PR URL

`gh pr create` prints the URL on success — surface it to the user. Don't merge; promotion is Peter's call. Merging fast-forwards `upstream/main` onto the commit staging already ran.

### 9. Discord release notes

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
- [ ] Discord release notes drafted (step 9), dated, member-features first, ≤ 2,000 characters.

## What this skill does NOT do

- Merge the PR. Peter does that manually with rebase merge.
- Force-push `upstream/staging`, or skip the staging verification because the batch "looks safe". Both need Peter.
- Post to Discord. The release notes are drafted in-conversation for Peter to paste.
- Update fork main after merge. The `memory/process/after-prod-merge-reset.md` rule covers post-merge.
- Open or modify per-feature PRs on `peterdrier/Humans`. Those land on the fork before promotion runs.
