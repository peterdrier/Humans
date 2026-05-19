<!--
Thanks for the PR! The checklist below mirrors the durable rules in `memory/INDEX.md`.
Tick items as you go, or strike through (`~~item~~`) with a reason if a row genuinely does not apply.
-->

## What

<!-- One or two sentences. Link to the issue: "Closes peterdrier/Humans#NNN" or "nobodies-collective/Humans#NNN" — see memory/process/issue-refs-qualified.md -->

## Why

<!-- The motivation. Skip if obvious from the linked issue. -->

## UI changes / screenshots

<!-- Drop screenshots or short clips for any user-visible change. Delete this section if there are none. -->

## Checklist

- [ ] **Section labeled** — issue and PR carry a section label (Camps / Teams / Legal / Shifts / Tickets / Board / Onboarding / Admin / etc.). See `memory/process/issues-need-section.md`.
- [ ] **Targeting `main` on `peterdrier/Humans`** (the QA fork). No direct commits to `main` — `memory/process/no-direct-to-main.md`.
- [ ] **Branched off `origin/main`**, not `upstream/main` — `memory/process/worktrees-off-origin-main.md`.
- [ ] **Issue refs are qualified** (`owner/repo#N`) when crossing repo boundaries — `memory/process/issue-refs-qualified.md`.
- [ ] **EF migrations** (if any) live in the correct directory and were generated, not hand-edited — `memory/architecture/migration-*` and `memory/process/never-hand-edit-migrations.md`.
- [ ] **New project rule?** Captured as a `memory/<bucket>/<name>.md` atom **in this PR**, with a one-line entry added to `memory/INDEX.md`. See `memory/META.md`.
- [ ] **Build + test pass locally**: `dotnet build Humans.slnx -v quiet` and `dotnet test Humans.slnx -v quiet`.
- [ ] **Nav coverage** — any new page is reachable from navigation (no orphan pages).
- [ ] **No magic strings** — `nameof()` / constants used where applicable.
- [ ] **Dates/times via NodaTime**, icons via Font Awesome 6.

## Reviewer notes

<!-- Anything that would help review: areas that look bigger than they are, risky touch points, things you want a second opinion on. Delete if none. -->
