# 2026-08-16 — Containers

Run: `--section=Containers` (shakedown, no plan). PR: peterdrier/Humans#1341

*(Migrated from the retired `log.md` / maintenance-log row at #1069; the original run predates
the per-run file format.)*

## Assessment summary

Section near-ideal. Full scorecard: `src/Sections/Humans.Containers/Docs/health.md`.

## Worked

- Doc-code alignment: phase gate scope, stale `HumansDbContext` reference.
- Dead `IContainerService.GetPlacementAsync` removed (opus-reviewed).
- 5 tests added (auth branches + ClearPlacement row-preserving).
- Clear-placement flow localized (2 shadowed keys wired via data-attr bridge).
- 3 dead resx keys deleted ×6 files.

## Retro

- resx edits must be XML-aware — line-based sed corrupted 5 multi-line variants, caught by build.
  (Applied to the skill's Lessons.)

## Needs Peter

- [x] Should lead container CRUD be phase-gated? **Answered in-run:** current split is intended —
      lead CRUD year-round, only map placement phase-gated; re-evaluate December 2026.

## Sweep queue

(none — lessons applied directly, pre-#1069 format)
