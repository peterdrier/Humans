# Containers — Health

Last assessed: 2026-08-16 @ 4603b8839 (section-doctor shakedown run)

## Scorecard

| Axis | State |
|---|---|
| Reforge (section) | 237 — top item `missingReadSurface`; read-split rejected: both external consumers (CityPlanning's two controllers) also write |
| Tests | 21 (16 before run), fast (<1s). Auth handler branches now pinned; placement metadata branch covered. Remaining gaps: placement image replace/remove, content-type + oversize image validation |
| Docs vs code | Strong invariants doc. Two drift fixes this run: phase-gate scope (doc claimed CRUD was gated; code gates only Place), stale `HumansDbContext` reference |
| Comments / slop | Clean — comments are terse and constraint-stating; one stale/contradicting comment removed this run |
| GUI / nav | Clean — `/Camp/{slug}/Containers` reached from Camp Edit/Details and the container map; index links back to map + camp. No dead ends |
| Translations | 26 keys, fully translated (ca/de/es/fr/it). This run: 3 dead keys deleted; 2 keys were shadowed by hardcoded English in container-map JS — now wired through the `data-*` CONFIG bridge |
| Arch conformance | Clean: internal sealed service/repo, own DbContext, bare-Guid FKs, no grandfathers, no obsolete. `Service <- IAuditLogService` is the standard horizontal contract |

## Ideal shape

Containers is close to the from-scratch ideal for a small G5 section: one internal service +
repository over two tables, a Contracts folder sized to its single consumer (CityPlanning),
resource-based auth with the phase gate isolated to one operation, and invariant-driven tests.
A rewrite today would produce nearly this code. The two deliberate oddities are documented and
defensible: CityPlanning hosts the placement API (URL ownership), and the Contracts folder is
wide because CityPlanning drives container CRUD from its own pages. No structural work is
warranted; remaining value is small-grain (test gaps, the phase-gate policy question).

## Opportunities (ranked by value)

1. ~~Phase-gate policy decision~~ — resolved 2026-08-16: current behavior (lead CRUD year-round,
   only map placement phase-gated) is intended. Peter: re-evaluate for next year in December 2026.
2. Test gaps: placement image replace/remove branches of `UpdatePlacementNotesAsync`; image
   content-type and oversize rejections (only extension rejection is covered).
3. Two sidebar button `title` tooltips in `container-map/sidebar.js` are hardcoded English
   ("Center map on this container", "Placement notes") with no existing resx keys — localizing
   them means 2 new keys + 5 translations.
4. (long-term, low value now) Placement API endpoints could move from CityPlanning into this
   section if the `/api/city-planning/containers/*` URLs are ever allowed to change — doc §
   Architecture explicitly scoped this out at G5.

## History

| Date | Reforge | Tests | Outcome | PR |
|---|---|---|---|---|
| 2026-08-16 | 237 | 21 | Doc-code alignment (phase gate, HumansDbContext), dead surface `GetPlacementAsync` removed from `IContainerService`, 5 tests added, clear-placement flow localized, 3 dead resx keys deleted | (this run) |
