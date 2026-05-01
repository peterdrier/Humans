---
name: No direct commits to main on peterdrier/Humans
description: HARD RULE. Always use a feature branch + PR, even for one-line / dev-only / "obviously safe" changes. Direct-to-main bypasses preview environments and Codex review.
---

Never `git commit` + `git push origin main` on `peterdrier/Humans`, regardless of how small the change is. Always create a feature branch and open a PR.

**Why:** Direct-to-main pushes auto-deploy to QA without review, mix unrelated work into the same SHA, and bypass the preview-environment + Codex review loop. Peter explicitly said "that's exactly why we changed that rule" after a dev login fix was committed directly to main. The old "small changes commit directly to main" rule is dead.

**How to apply:**

Default to `git checkout -b <branch>` → push → `gh pr create` for ANY change to `peterdrier/Humans`, including:

- Tooling/dev-only fixes (DevLogin, scripts, .editorconfig)
- One-line typo fixes
- "Obviously safe" changes
- Anything that feels too small for a PR

If `CLAUDE.md` still says "small changes commit directly to main" anywhere, treat that as stale and follow this rule instead. If unsure, ask before pushing to main.
