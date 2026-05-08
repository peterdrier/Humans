---
name: pr-prod
description: "Promote QA → production by opening a PR from peterdrier/Humans:main to nobodies-collective/Humans:main with a properly-qualified commit summary. Use when the user says \"PR to production\", \"promote to prod\", \"PR from origin to upstream\", \"prod PR\", or any variation of pushing batched fork changes upstream. Always use this skill for the two-remote promotion flow even if the user doesn't say \"prod\" explicitly."
argument-hint: "(no args)"
---

# Promote QA → production

Opens a single batched PR from `peterdrier/Humans:main` (peter's fork, QA-deployed) to `nobodies-collective/Humans:main` (production). Per `CLAUDE.md`, the upstream merge strategy is **rebase merge** — individual PRs were already squashed on the fork.

## The ref-qualification trap (read this first)

GitHub resolves bare `#NNN` against the repo where the comment lives. The PR body lives on **nobodies-collective/Humans**, so:

| Ref kind | What it means | How to write it in the PR body |
|---|---|---|
| Trailing `(#NNN)` in commit subject | peter-fork squash-merge PR number | **`peterdrier/Humans#NNN`** (cross-repo, qualified) |
| `issue-NNN:` prefix in commit subject | nobodies-collective issue number | `#NNN` (bare — resolves against nobodies) |
| Inline `#NNN` in commit body | Almost always a nobodies-collective issue | `#NNN` (bare) |

Getting this wrong cross-links to whatever PR/issue happens to share that number on nobodies-collective — silently wrong, hard to spot. The repo's `memory/process/issue-refs-qualified.md` rule applies here.

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

### 4. Build the PR body

For each commit, transform the subject as follows:

- Strip the trailing `(#NNN)` peter-fork PR ref and re-emit at the end as `(peterdrier/Humans#NNN)`.
- Convert `issue-NNN:` prefix to `issue #NNN:` (the bare `#NNN` auto-links to the nobodies issue).
- Inline `#NNN` references in the subject (e.g. "codex findings on #667") stay bare.
- Commits without a peter-fork PR ref (direct-to-main commits, rare) just get listed without the trailing parenthetical.

**Worked example** — given this commit:

```
8508e353 issue-673: consolidate person-search with PersonSearchFields bit-flag API (#455)
```

emit this bullet:

```
- `8508e353` issue #673: consolidate person-search with PersonSearchFields bit-flag API (peterdrier/Humans#455)
```

### 5. Write the PR

Use `gh pr create` with `--head peterdrier:main`. Pass the body via heredoc to preserve formatting:

```bash
gh pr create --repo nobodies-collective/Humans \
  --base main --head peterdrier:main \
  --title "Promote QA → production (N commits)" \
  --body "$(cat <<'EOF'
## Summary

Batched promotion of QA-tested changes from `peterdrier/Humans:main` to production.

PR refs are qualified to `peterdrier/Humans` (peter-fork PR numbers); bare `#NNN` refs are nobodies-collective issues.

## Commits

- `<sha>` <transformed subject>
- ...

## Test plan

- [ ] Rebase merge (each PR is already squashed)
- [ ] CI green on production
- [ ] Verify EF migrations apply cleanly
EOF
)"
```

Substitute `N` with the actual commit count.

### 6. Return the PR URL

`gh pr create` prints the URL on success — surface it to the user. Don't merge; promotion is Peter's call.

## Sanity checks before submitting

- [ ] Every `(#NNN)` from a commit subject is rewritten as `(peterdrier/Humans#NNN)` in the body.
- [ ] Every `issue-NNN:` prefix is rewritten as `issue #NNN:`.
- [ ] No bare `#NNN` in the body refers to a peter-fork PR. (If unsure about a number, check `gh pr view NNN --repo peterdrier/Humans` vs `gh issue view NNN --repo nobodies-collective/Humans`.)
- [ ] Title is `Promote QA → production (<N> commits)` with the correct count.
- [ ] No existing open PR was overlooked (step 2).

## What this skill does NOT do

- Merge the PR. Peter does that manually with rebase merge.
- Update fork main after merge. The `memory/process/after-prod-merge-reset.md` rule covers post-merge.
- Open or modify per-feature PRs on `peterdrier/Humans`. Those land on the fork before promotion runs.
