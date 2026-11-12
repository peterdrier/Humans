---
name: No direct commits to main (except memory/ atoms)
description: "HARD RULE. Feature branch + PR for all code/docs/config changes, even trivial ones. Narrow exception: `memory/**`-only changes may go direct to `origin/main`."
---

Never commit and push to `main` on `peterdrier/Humans` for code, docs, config or tooling, however small: typo fixes, dev-only scripts, "obviously safe" changes, `AGENTS.md`, `docs/**`, section docs. Always a feature branch and a PR.

**Why:** `origin/main` auto-deploys to QA. A direct push skips review, the preview environment and the bots, and mixes unrelated work into one SHA. Peter, after a dev-login fix went straight to main: "that's exactly why we changed that rule."

**Exception: a change confined to `memory/**`** (atoms, `INDEX.md`, `META.md`). Atoms have no runtime effect and are reviewed in the conversation that produced them (Peter, 2026-05-03). Either:

1. Bundle the atom with the PR whose work surfaced the rule (preferred), or
2. Commit it straight to `origin/main`:

```bash
git checkout main && git pull --ff-only origin main
git add memory/<bucket>/<file>.md memory/INDEX.md
git commit -m "memory: <one-line summary>" && git push origin main
```

If the diff touches anything outside `memory/**`, even one `AGENTS.md` line, the whole change goes through a branch and PR. Locally, push from a worktree, never the main checkout ([[always-use-worktree]]).
