<!--
Thanks for the PR! The checklist below mirrors the durable rules in `memory/INDEX.md`.
Tick items as you go, or strike through (`~~item~~`) with a reason if a row genuinely does not apply.
-->

## What

<!-- One or two sentences. Link to the issue: "Closes peterdrier/Humans#NNN" or "nobodies-collective/Humans#NNN" — see memory/process/issue-refs-qualified.md -->

## Why

<!-- The motivation. Skip if obvious from the linked issue. -->

## Existing surface checked

<!-- List existing services/components/helpers/routes/DTOs considered before adding new surface. Use "No new durable surface" when true. -->

## UI changes / screenshots

<!-- Drop screenshots or short clips for any user-visible change. Delete this section if there are none. -->

## Checklist

- [ ] **Section labeled** — issue and PR carry a section label (Camps / Teams / Legal / Shifts / Tickets / Board / Onboarding / Admin / etc.).
- [ ] **Targeting `main` on `peterdrier/Humans`** (the QA fork). No direct commits to `main` — `memory/process/no-direct-to-main.md`.
- [ ] **Branched off `origin/main`**, not `upstream/main`.
- [ ] **Issue refs are qualified** (`owner/repo#N`) when crossing repo boundaries — `memory/process/issue-refs-qualified.md`.
- [ ] **EF migrations** (if any) auto-generated, not hand-edited — `memory/architecture/no-hand-edited-migrations.md` and `memory/architecture/migration-regen-after-rebase.md`.
- [ ] **NuGet packages updated?** If yes, `Views/About/Index.cshtml` updated with new versions + licenses — `memory/process/about-page-license-attribution.md`.
- [ ] **New project rule?** Captured as a `memory/<bucket>/<name>.md` atom **in this PR**, with a one-line entry added to `memory/INDEX.md`. See `memory/META.md`.
- [ ] **Reuse-first checked** — no unnecessary new files, public types, interface methods, service/repository methods, DTOs/view models, helpers, endpoints, dependencies, or DI registrations. See `memory/process/reuse-first-change-discipline.md`.
- [ ] **Build + test pass locally**: `dotnet build Humans.slnx -v quiet` and `dotnet test Humans.slnx -v quiet`.
- [ ] **Nav coverage** — any new page is reachable from navigation (no orphan pages).
- [ ] **No magic strings** — `nameof()` / constants used where applicable.
- [ ] **Dates/times via NodaTime**, icons via Font Awesome 6.

## Reviewer notes

<!-- Anything that would help review: areas that look bigger than they are, risky touch points, things you want a second opinion on. Delete if none. -->
