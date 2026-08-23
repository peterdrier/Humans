---
name: pr-prod
description: "Promote QA → production by opening a PR from peterdrier/Humans:main to nobodies-collective/Humans:main with a properly-qualified commit summary. Use when the user says \"PR to production\", \"promote to prod\", \"PR from origin to upstream\", \"prod PR\", or any variation of pushing batched fork changes upstream. Always use this skill for the two-remote promotion flow even if the user doesn't say \"prod\" explicitly."
argument-hint: "(no args)"
---

# Promote QA → production

Opens a single batched PR from `peterdrier/Humans:main` (peter's fork, QA-deployed) to `nobodies-collective/Humans:main` (production). Per `CLAUDE.md`, the upstream merge strategy is **rebase merge** — individual PRs were already squashed on the fork.

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

### 4. Check EF migration sequencing

Enumerate the migrations being promoted:

```bash
git diff --name-status upstream/main..origin/main -- '*/Migrations/*'
```

Group added files by folder — each folder is one DbContext chain (`src/Sections/Humans.<Section>/Data/Migrations/` per section, `src/Humans.Web/Migrations/System/` for the platform context). No added migrations → record "None" for the **DB changes** section and move on. For every folder with added migrations, verify:

1. **End-of-chain** — every new migration's timestamp prefix sorts after every migration already on `upstream/main` in that folder:

   ```bash
   git ls-tree -r --name-only upstream/main -- <folder>
   ```

   (`-r` is required — without it, an existing folder prints as a single tree entry and there are no timestamps to compare.)

   If `<folder>` doesn't exist on `upstream/main` because the batch renames it (`R` entries in the step-4 diff, e.g. SystemSettings→Settings), run the same command against the **old** path from the rename entry — the chain continues across the rename. Only a genuinely new context has no upstream chain to compare.

   A new migration sorting before an existing upstream one is mid-chain (`memory/architecture/migration-regen-after-rebase.md`).

2. **Snapshot updated** — the folder's `*DbContextModelSnapshot.cs` appears in the same diff as `M`, as `A` (new folder), or as the destination of an `R` entry (renamed folder — a rename with a small content change is reported as `R<score>`, not `M`). New migrations with an untouched snapshot mean the snapshot wasn't regenerated.

3. **Chain converges** — the critical case is ≥2 new migrations in one folder (two fork PRs each added one): the second must have been generated on top of the first, not off the same base in parallel. Two-part check; **both** must pass:

   a. The model body between the `#pragma warning` markers is identical in the **newest** migration's `.Designer.cs` and the folder's snapshot:

   ```bash
   export MSYS_NO_PATHCONV=1   # must be exported — a diff-local prefix doesn't reach the process substitutions
   a=$(git show origin/main:<newest>.Designer.cs | awk '/#pragma warning disable/,/#pragma warning restore/')
   b=$(git show origin/main:<folder>/<Context>ModelSnapshot.cs | awk '/#pragma warning disable/,/#pragma warning restore/')
   [ -n "$a" ] && [ -n "$b" ] && diff <(echo "$a") <(echo "$b") && echo CHAIN-OK
   ```

   `CHAIN-OK` required. Anything else is a failure: a diff means the merge kept the older branch's snapshot and lost the newer's model, and an empty `$a`/`$b` means `git show` failed (wrong path, or Git Bash mangled the `ref:path` argument) — never treat empty-vs-empty as a pass. Reading via `git show origin/main:` makes the check correct from any checkout; plain paths would silently compare whatever branch the current worktree happens to be on.

   b. The snapshot matches the compiled model — catches the other fork resolution, where the merge kept the newer branch's snapshot: Designer and snapshot are then byte-identical but *both* omit the earlier migration's model changes, so (a) alone passes. On a checkout of `origin/main` (the fork tip being promoted):

   ```bash
   dotnet build Humans.slnx -v quiet
   dotnet ef migrations has-pending-model-changes --context <Context> \
     --project src/Sections/Humans.<Section> --startup-project src/Humans.Web --no-build
   ```

   Must report no pending model changes (`--project` per `memory/process/ef-multi-context-commands.md`; the platform context uses `--project src/Humans.Web`). Together, (a) + (b) prove the newest Designer's model incorporates every promoted migration's entity changes.

**Any check fails → STOP. Do not open the PR.** Report which context and which check. The fix happens on the fork before promotion (stop-and-ask per the atom above; `/ef-regen` is the sanctioned recovery) — a broken chain promoted to prod is a deploy incident, not a PR-body footnote.

### 5. Build the PR body

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

### 6. Write the PR

Use `gh pr create` with `--head peterdrier:main`. Pass the body via heredoc to preserve formatting:

```bash
gh pr create --repo nobodies-collective/Humans \
  --base main --head peterdrier:main \
  --title "Promote QA → production (N commits)" \
  --body "$(cat <<'EOF'
## Summary

Batched promotion of QA-tested changes from `peterdrier/Humans:main` to production.

All issue/PR refs are qualified per `memory/process/issue-refs-qualified.md` — `peterdrier/Humans#NNN` for peter-fork PRs, `nobodies-collective/Humans#NNN` for upstream issues.

## Commits

- `<sha>` <transformed subject>
- ...

## DB changes

<!-- REQUIRED section, from step 4. "None." if no migrations in the batch. -->
- `<Context>`: `<migration id + name>`, `<migration id + name>` (apply order) — sequencing verified

## Test plan

- [ ] Rebase merge (each PR is already squashed)
- [ ] CI green on production
- [ ] Verify EF migrations apply cleanly
EOF
)"
```

Substitute `N` with the actual commit count.

### 7. Return the PR URL

`gh pr create` prints the URL on success — surface it to the user. Don't merge; promotion is Peter's call.

### 8. Discord release notes

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
- [ ] Migration sequencing checks (step 4) ran for every context with new migrations; **DB changes** section lists them per context in apply order, or says "None."
- [ ] Discord release notes drafted (step 8), dated, member-features first, ≤ 2,000 characters.

## What this skill does NOT do

- Merge the PR. Peter does that manually with rebase merge.
- Post to Discord. The release notes are drafted in-conversation for Peter to paste.
- Update fork main after merge. The `memory/process/after-prod-merge-reset.md` rule covers post-merge.
- Open or modify per-feature PRs on `peterdrier/Humans`. Those land on the fork before promotion runs.
