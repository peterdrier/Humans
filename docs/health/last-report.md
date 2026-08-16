# Section Doctor — Last Run Report

**Run:** 2026-08-16, Containers (shakedown — `--section=Containers`, no plan existed). Anchor `4603b8839`. Budget 2.5h; used ~35 min wall clock.

## Assessment summary

Containers is close to its ideal shape — small G5 section done right (internal sealed
service/repo, own context, contracts folder sized to its one consumer, resource-based auth,
invariant-driven tests). Full scorecard: `src/Sections/Humans.Containers/Docs/health.md`.

## Worked

- Doc-code alignment: invariants doc claimed lead container CRUD was phase-gated; code
  deliberately gates only `Place`. Doc now matches code (see Needs Peter). Stale
  `HumansDbContext` sentence and a contradicting controller comment removed. (`2d65eb9f2`)
- Auth handler test gaps pinned: Manage-not-phase-gated, wrong-camp lead denied, CampAdmin and
  city-planning-team bypasses. (`e63593c0f`)
- Dead cross-section surface: `IContainerService.GetPlacementAsync` removed (no production
  caller; repo-level lookup stays). Opus reviewer: ACCEPT. Added the previously-untested
  `ClearPlacementAsync` row-preserving branch test. (`3f24389fe`)
- i18n: `ContainerMap_ClearPlacement` / `ContainerMap_ConfirmClear` existed fully translated but
  were shadowed by hardcoded English in `sidebar.js`/`main.js` — wired through the `data-*`
  CONFIG bridge. 3 dead resx keys deleted from all 6 resource files. (`d5f8ed184`)
- Test classes renamed to match file names (reviewer caveat). (`26e3220d1`)

## Skipped / rejected

- Read-split (`missingReadSurface`, reforge's top item): rejected — both external consumers also
  write; no read-only consumer exists. Score-chasing.
- Hardcoded English flash messages in `ContainerController`: app-wide convention (Camps
  identical); not a section defect.
- Sidebar tooltip localization: needs 2 new keys + 5 translations — left as a ranked
  opportunity in health.md.

## Retro

- **What the rubric missed:** nothing structural; the surface lane found the only actionable
  slop (resx). The by-hand C# read found the doc-code contradiction — keep humans-level reading
  of auth paths in the assessment, it caught what grep couldn't.
- **Wasted motion:** line-based `sed` corrupted 5 multi-line resx variants (build caught it) —
  redone XML-aware. Skill amended: resx/XML edits must be structure-aware, never line-based.
- **Skill gaps found while running** (amended on the skill branch): reforge needs a built
  solution — the baseline build must precede the assessment, not the strike; `--section` runs
  have no plan row to tick, so bookkeeping should say "if a plan exists".

## Needs Peter

1. **Phase-gate policy** — should camp-lead container *CRUD* be gated by the placement phase,
   like placements are? The invariants doc (and a stale comment) said yes; code deliberately
   says no (`ContainerOperation.Manage` vs `.Place` doc-comments). Doc now matches code. If the
   answer is "gate it", that's a behavior change → file an issue.
