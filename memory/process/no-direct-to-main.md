---
name: No direct commits to main on peterdrier/Humans (except memory/ atoms)
description: HARD RULE. Always use a feature branch + PR for code/docs/config changes, even for one-line / dev-only / "obviously safe" ones. Narrow exception — `memory/**` atom changes go direct to `origin/main`.
---

Never `git commit` + `git push origin main` on `peterdrier/Humans` for code, docs, or config changes, regardless of how small the change is. Always create a feature branch and open a PR.

**Narrow exception — `memory/**` atom changes don't need their own PR.** Two acceptable paths, pick whichever fits the situation:

1. **Bundle with the work that discovered the rule** (preferred when applicable, per `CLAUDE.md`'s "in the same commit as the work that discovered it" guidance). The atom + `INDEX.md` line ride along in the feature PR for the change that surfaced the rule. PR review covers both the work and the atom.
2. **Standalone direct-to-`origin/main`.** When the rule wasn't surfaced by an in-flight PR (e.g. a retroactive lesson, a follow-up after a PR already merged, a meta-rule about process), commit the `memory/**` change on `main` and push directly. No branch, no PR, no preview env. The diff must be confined to `memory/**` (atoms + `INDEX.md` + `META.md`). If it touches anything outside `memory/**` — even one CLAUDE.md line or a referenced section doc — fall back to the standard branch+PR flow for the whole change.

Peter approved this carve-out 2026-05-03 because memory atoms have no runtime effect and no deploy surface; review happens in the conversation that produced them, and a separate PR + Codex pass for a doc-only memory change is pure ceremony.

**Why:** Direct-to-main pushes for code/docs/config auto-deploy to QA without review, mix unrelated work into the same SHA, and bypass the preview-environment + Codex review loop. Peter explicitly said "that's exactly why we changed that rule" after a dev login fix was committed directly to main. The old "small changes commit directly to main" rule is dead for code. Memory atoms are different: they have no runtime effect, no deploy surface, and the review happens in the conversation that produced them — running them through PR + Codex is pure ceremony.

**How to apply:**

For ANY change to `peterdrier/Humans` that touches code, docs (other than `memory/**`), config, or tooling, default to `git checkout -b <branch>` → push → `gh pr create`. This includes:

- Tooling/dev-only fixes (DevLogin, scripts, .editorconfig)
- One-line typo fixes
- "Obviously safe" changes
- Anything that feels too small for a PR
- Changes to `docs/architecture/`, `docs/sections/`, `docs/features/`, `CLAUDE.md`, `README.md`

For `memory/**`-only changes (new atoms, atom edits, INDEX.md updates, META.md tweaks):

```
git checkout main
git pull --ff-only origin main
# write the atom(s), edit INDEX.md
git add memory/<bucket>/<file>.md memory/INDEX.md
git commit -m "memory: <one-line summary>"
git push origin main
```

If `CLAUDE.md` still says "small changes commit directly to main" anywhere, treat that as stale. If unsure, ask before pushing to main.
