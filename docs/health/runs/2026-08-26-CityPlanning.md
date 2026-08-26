# doctor(CityPlanning) — 2026-08-26

## Header

- Invocation: unattended daily run, no arguments (routine-fired)
- Anchor commit: `origin/main` @ `290422cb`
- Budget: 2.5h
- Branch: `section-doctor/2026-08-26T071428Z`
- PR: peterdrier/Humans#1525
- Session: .NET SDK, `dotnet-ef` and reforge available; full
  `dotnet build Humans.slnx` green from a clean tree (0 errors, 33 pre-existing
  warnings). Stryker not installed — Tests-thread mutation-score half skipped
  with reason, out of scope by instruction (finding #23).
- GitHub scope is `peterdrier/Humans` only. Reach probe: fork issues read OK;
  `nobodies-collective/Humans` denied — **upstream issue half suspended**, see
  `## Threads` and finding #24.
- Phase 8 (inline round) skipped by instruction — unattended run.

## Assessment summary

City Planning is a **first-time doctor target**: it had no `Docs/health.md`, so
this run derived the target shape from scratch (reforge 210 / loc 1947 /
cogP95 4 / cogMax 6). Structurally it is in good order and the score reflects
breadth, not mess: ten question-shapes, one page controller, one API
controller, one service, one repository over the section's tables, and the
narrowest cross-section contract in the repo.

The structure needed almost nothing. **The value was in what the section
*claimed* about itself**, and in three untested paths that each carry a real
invariant.

The claims cluster was large and consistent. `ICityPlanningRepository`
promised an ordering guarantee its query does not make (#1). The section doc
described a `HasOne<CampSeason>()` relationship that does not exist, omitted
the three container CRUD posts and the entire bulk-import path, and had the
SignalR method signatures wrong in both directions (#6, #8, #9).
`authorization.md` named the wrong guard on **both** of its runtime-guard rows
(#7). The Contracts csproj named two projects that are not in the solution
(#15). And two comments pointed at `CityPlanningPageRenderTests` as the guard
for the page routes and the `_ViewImports` set — a test that lives in
`Humans.Integration.Tests` and therefore never runs on a PR (#13).

The tests cluster was sharper than the count suggests. The one method Camps
calls across the section boundary when it deletes a camp —
`DeletePolygonsForCampSeasonsAsync` — had **zero** coverage (#5). The single
settings field keyed differently from every other (`RegistrationInfo`, keyed
to the highest open season rather than `PublicYear`) had nothing holding the
read and the write to the same key (#11). Two of the three upload guards were
unreached (#12).

Three behaviour bugs surfaced that a doctor run must not fix, and went to
`Docs/debt.yml` instead (#20–#22). The sharpest is #22: the city-planning
team's slug is lower-cased and then compared `Ordinal`, so a team whose stored
slug carries any uppercase silently loses its map-admin exemption entirely.

Three subagent findings were **refuted** against the code and declined:
a live resx key reported as dead (`CityPlanning_PlacementPhaseInfo`, used by
`Humans.Camps/Views/Camp/Details.cshtml:223`), a claim that `ContainerMap.cshtml`
has no back-navigation (it has two, lines 57 and 63), and a claim that the
`camp_leads` table was dropped (still referenced by `Humans.Camps`).

## Ranked findings

1. **`GetHistoryForCampSeasonAsync` documents an ordering it does not
   provide.** Its XML doc promised newest-first; display ordering moved to the
   controller under `memory/architecture/display-sort-in-controllers.md` and
   the query orders nothing. A caller trusting the comment ships a
   randomly-ordered history list. — **doc fix on production code** — *struck*
2. **Three dead front-end assets.** `wwwroot/img/city-planning/barrio-add-button.png`
   and `barrio-edit-buttton.png` (note the typo) referenced by nothing, and
   `container-map/measure.js` a pure re-export of `shared/measure.js`. —
   **delete** — *struck*
3. **Dead resx key in all six cultures.** `CityPlanning_CampLimits` defined
   six times, read nowhere. — **delete** — *struck*
4. **Unused project reference.** `tests/Humans.CityPlanning.Tests` referenced
   `Humans.Shifts.Contracts`; nothing in the project uses it. — **delete** —
   *struck*
5. **`DeletePolygonsForCampSeasonsAsync` had zero coverage.** This is the
   cross-section contract `Humans.Camps` calls inside its own camp delete —
   the one place another section's data disappears through this section's
   service. Nothing asserted that it removes history with the polygon, that
   it leaves other seasons alone, or that either zero-return path holds. —
   **add tests** — *struck*
6. **The section doc claimed an EF relationship that does not exist.**
   `CityPlanning.md` described `HasOne<CampSeason>()` on `CampPolygon`. The
   column is a bare `Guid` with no FK — which is precisely why nothing
   cascades and why `DeletePolygonsForCampSeasonsAsync` has to exist. The
   doc's version made that method look redundant. — **doc fix** — *struck*
7. **`authorization.md` named the wrong guard on both runtime-guard rows.**
   The map-admin gate is `RoleChecks` **or** team membership, not the single
   check documented; and `AuthorizeAsync(ContainerOperationRequirement.Place)`
   guards the three *container* endpoints, not polygon edit/restore. Both rows
   pointed a reader at the wrong deny path. — **doc fix** — *struck*
8. **The bulk-import path was undocumented.** `barrio-map/admin-import.js`
   matches surveyor GeoJSON features to camps by lower-cased name or slug and
   issues one ordinary `PUT` per match — so an import is N saves and gets N
   history rows for free, and there is deliberately no server-side import
   endpoint. Nothing said so anywhere. — **doc fix** — *struck*
9. **SignalR signatures wrong in two docs.** Both the section doc and the
   feature spec had the hub's methods wrong in both directions; the truth is
   outbound `CursorMoved(connectionId, displayName, lat, lng)` / `CursorLeft`
   and inbound `UpdateCursor(lat, lng)`. — **doc fix** — *struck*
10. **Six hardcoded English `alt=` strings.** `_PlacementHelpModal.cshtml` is
    member-facing (barrio map), so the admin exemption in
    `memory/code/localization-admin-exempt.md` does not reach it. Every
    visible string in the modal was localized; the six image descriptions
    were not. — **localize across six cultures** — *struck*
11. **The `RegistrationInfo` year key was untested.** It is the one settings
    field keyed to the highest open season rather than `PublicYear`. Nothing
    held the read and the write to the same key, so a divergence would show
    up only as an empty blurb on the Register page. — **add tests** — *struck*
12. **Two of three upload guards unreached.** Only `InvalidGeoJson` was
    tested; `MissingFile` and `FileTooLarge` had no coverage on either upload
    endpoint. — **add tests** — *struck*
13. **Two comments cite a guard that never runs in CI.**
    `Views/_ViewImports.cshtml` and `CityPlanningArchitectureTests` both named
    `CityPlanningPageRenderTests` as the thing that catches a missing
    `_ViewImports` line or a changed page route. It lives in
    `tests/Humans.Integration.Tests`, which `build.yml` filters out by design
    (`memory/process/integration-tests-are-not-ci-tests.md`). The comments are
    now honest; **the coverage gap itself is real and is Needs Peter.** —
    **comment fix + Needs Peter** — *struck (comments only)*
14. **Test-file duplication and over-wide substitutes.**
    `CityPlanningRepositoryTests` built its own context and clock instead of
    using `CityPlanningTestBase`; `UpdatePlacementDatesAsync_SetsBothDates`
    existed twice; two suites substituted whole service interfaces where the
    read-only ones suffice. — **cut** — *struck*
15. **The Contracts csproj named two projects that do not exist.** Its comment
    justified the project's existence in terms of `Humans.Application` and
    `Humans.Interfaces`. The real reason is `Humans.Camps` and
    `Humans.Containers`. — **comment fix** — *struck*
16. **Stale history citations in comments.** `#858`, `#750`, `#1075`, `design
    §5/§7a/§15 step 1` and a G5 migration narrative in `CityPlanningHub`,
    carrying facts that are still true wrapped in a story that is over. Facts
    kept, story cut. — **comment fix** — *struck*
17. **Guide page drift.** `docs/guide/CityPlanning.md` was missing
    `src/Humans.Base/Authorization/RoleChecks.cs` from its
    `freshness:triggers` — the file that decides map-admin — and claimed
    Glossary.md defines "limit zone", which it does not (it has `## Barrio`
    and `## Sound zone`). — **doc fix** — *struck*
18. **Eight-plus service methods take a `userId` that goes nowhere.** Every
    settings write — phase open/close, zone upload, zone delete, placement
    dates — accepts a `userId` parameter and drops it. `CityPlanningSettings`
    records *when* a phase changed but not *who* changed it. The repo-wide
    rule is that admin actions on members' behalf leave audit entries. Either
    those parameters carry an audit write or they should not be parameters —
    and both directions are behaviour changes. — **Needs Peter #18**
19. **Container placement history asymmetry.** Barrio polygons keep every
    version and can be restored; container placements keep none. Nothing in
    the section states that asymmetry as a decision, so a reader cannot tell
    whether it is a design call or an omission. — **Needs Peter #19**
20. **Container CRUD posts redirect to the wrong year.** `CreateBarrioContainer`,
    `EditContainer` and `DeleteContainer` — and the `ModelState`-invalid
    branches of the first two — redirect with `settings.Year` —
    `CampSettings.PublicYear` — instead of the `{year}` route value the admin
    was on, bouncing anyone curating a non-public year back to the public
    year's list after every operation. The private helpers already thread the
    right year; only the callers pass the wrong one. Behaviour fix. —
    **`Docs/debt.yml`**
21. **`MutateSettingsAsync` returns a value no production caller reads.** All
    ten callers in `CityPlanningService` discard it; only two repository tests
    use it. — **`Docs/debt.yml`**
22. **The `Ordinal` slug comparison strips the map-admin exemption.**
    `IsCityPlanningTeamMemberAsync` lower-cases the configured
    `CityPlanningTeamSlug` and compares it to `Team.Slug` / `Team.CustomSlug`
    with `StringComparison.Ordinal`, so a team whose stored slug carries any
    uppercase never matches and **every city-planning team member silently
    loses their map-admin exemption**. Whether the fix is to normalize both
    sides or to compare `OrdinalIgnoreCase` is a call, and either changes who
    can edit. — **`Docs/debt.yml`**
23. **Mutation score not measured.** Stryker is not installed in this
    environment and installing it is out of scope by instruction. The Tests
    thread's mutation half did not run; the invariant matrix was walked by
    hand instead. — **skipped with reason**
24. **The upstream issue half of the Inbox thread could not run.**
    `nobodies-collective/Humans` is not configured for this session; the probe
    returned an access denial, not an empty result. Issues filed upstream
    against City Planning were not read. — **skipped with reason**

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/CityPlanning.md` | changed |
| `src/Sections/Humans.CityPlanning.Contracts/CityPlanningOptions.cs` | reviewed |
| `src/Sections/Humans.CityPlanning.Contracts/Humans.CityPlanning.Contracts.csproj` | changed |
| `src/Sections/Humans.CityPlanning.Contracts/ICityPlanningService.cs` | reviewed |
| `src/Sections/Humans.CityPlanning.Contracts/ICityPlanningServiceRead.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.ca.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.de.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.es.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.fr.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.it.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.resx` | changed |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningDbContext.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningRepository.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CampPolygonConfiguration.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CampPolygonHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CityPlanningSettingsConfiguration.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/ICityPlanningRepository.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20260809142312_BaselineCityPlanning.Designer.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20260809142312_BaselineCityPlanning.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/CityPlanningDbContextModelSnapshot.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Docs/CityPlanning.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/authorization.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.CityPlanning/Docs/features/city-planning.md` | changed |
| `src/Sections/Humans.CityPlanning/Domain/CampPolygon.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Domain/CampPolygonHistory.cs` | changed |
| `src/Sections/Humans.CityPlanning/Domain/CityPlanningSettings.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Humans.CityPlanning.csproj` | changed |
| `src/Sections/Humans.CityPlanning/Models/CityPlanningMeasurePanelViewModel.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Models/CityPlanningViewModels.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Section.cs` | changed |
| `src/Sections/Humans.CityPlanning/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/SectionEndpoints.cs` | changed |
| `src/Sections/Humans.CityPlanning/SectionNav.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningDtos.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningHub.cs` | changed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningService.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Admin.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/BarrioMap.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/ContainerMap.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Containers.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Index.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/_HistoryOffcanvas.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/_MeasurePanel.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/Shared/_PlacementHelpModal.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/Views/_ViewImports.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-add-button.png` | changed (deleted) |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-colors.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-draw.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-edit-buttton.png` | changed (deleted) |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-edit.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-history.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-outside-limits.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-overlap.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/admin-import.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/config.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/edit.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/geometry.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/layers.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/main.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/marquee-direct-select.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/signalr.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/state.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/config.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/api.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/config.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/geometry.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/interaction.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/layers.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/main.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/measure.js` | changed (deleted) |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/placement-notes.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/sidebar.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/main.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/map-constants.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/measure.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/official-zones-layer.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/sound-zone-colors.js` | reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningArchitectureTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningRepositoryTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningServiceTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningTestBase.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/ContainerPlacementPhaseTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/Humans.CityPlanning.Tests.csproj` | changed |
| `src/Sections/Humans.CityPlanning/Docs/debt.yml` | changed (new) |
| `src/Sections/Humans.CityPlanning/Docs/health.md` | changed (new) |
| `docs/health/runs/2026-08-26-CityPlanning.md` | changed (new; this file) |
| `docs/architecture/debt-ledger.yml` | changed (sweep commit only) |
| `src/Sections/Humans.MailerLite/Docs/debt.yml` | changed (sweep commit only) |

## Threads

| Thread | How | Model | Findings | Cost | Notes |
|---|---|---|---|---|---|
| Spine + Shape + Behavior & bugs (3a–3c, 3e) | main | opus-5 | #1,#5,#8,#11,#12,#18,#19,#20,#21,#22 | $11.69 (`assess` bucket, shared) | Phase 3 marked once — the shared bucket for every main-thread reading lens. Derived `Docs/health.md` from scratch: the section had no prior target |
| Freshness | dispatched sub-agent | sonnet | #6,#7,#9,#17 | $4.87 | `CityPlanning.md`, `authorization.md`, `features/city-planning.md` and `docs/guide/CityPlanning.md` read against the code |
| Tests | dispatched sub-agent | sonnet | #14 | $2.29 | **Stryker skipped — not installed and out of scope by instruction (finding #23).** Mutation half not run; the invariant matrix was walked by hand instead |
| Prose & surface | dispatched sub-agent | haiku | #2,#3,#10 | $0.55 | Six `.cshtml` + the six-language resx set. **Two of its verdicts were wrong** and were refuted against the code before acting — see the assessment summary and finding #26 |
| Comments & history | dispatched sub-agent | sonnet | #15,#16 | $4.42 | Full comment walk with an explicit KEEP list. One verdict (the `camp_leads` table "dropped") was wrong and was refuted; those comments were kept |
| Conformance | main | opus-5 | none | in `assess` | Both `section-conformance.yml` rules pass, verified on the main thread rather than dispatched: layer folders only at the top level, `Docs/CityPlanning.md` present, migrations under `Data/Migrations/`, resource class + six `.resx` at the project root, and every key this run added carries the `CityPlanning_` prefix |
| Inbox | main | opus-5 | none | in `assess` | **Partial — upstream issues unreachable (scope: `peterdrier/Humans`), finding #24.** Reach proved per repo: fork reads OK, `nobodies-collective/Humans` denied. No open CityPlanning issues on the fork; no CityPlanning rows in `docs/architecture/debt-ledger.yml`; no `Docs/debt.yml` existed before this run |

## Worked

- **Strike 1 (delete — findings #2, #3, #4, #14):** two unreferenced PNGs and
  the `container-map/measure.js` re-export shim deleted (`container-map/main.js`
  now imports `'../shared/measure.js'`); `CityPlanning_CampLimits` removed from
  all six cultures; the `Humans.Shifts.Contracts` reference dropped from the
  test csproj; `CityPlanningRepositoryTests` moved onto `CityPlanningTestBase`;
  the duplicate `UpdatePlacementDatesAsync_SetsBothDates` dropped and the
  substitutes narrowed to `ITeamServiceRead` / `IUserServiceRead`. Also carries
  the new `Docs/health.md`. Commit `1606b1da`.
- **Strike 2 (doc + comment fix — findings #1, #6, #7, #8, #9, #15, #16, #17):**
  the ordering contract on `GetHistoryForCampSeasonAsync` corrected; the false
  `HasOne<CampSeason>()` claim, the missing container posts, the missing bulk
  import and the wrong SignalR signatures fixed in `CityPlanning.md` and
  `features/city-planning.md`; both runtime-guard rows corrected in
  `authorization.md`; the Contracts csproj comment renamed to the projects that
  exist; history citations cut from six production files; `docs/guide/CityPlanning.md`
  triggers and Glossary claim corrected. Commit `0294aa9b`.
- **Strike 3 (add tests — findings #5, #11, #12):** new tests over the
  season-scoped delete (removal of polygon *and* history, other seasons
  untouched, both zero-return paths), the `RegistrationInfo` highest-open-season
  key (write target, `PublicYear` fallback, trim-and-null, and that the read
  keys the same year the write used), and the `MissingFile` / `FileTooLarge`
  upload guards on both endpoints. 45 → 57 passing. Commit `25da30db`.
- **Strike 4 (localize — finding #10):** six new `CityPlanning_Help_*ImageAlt`
  keys in all six cultures, wired into `_PlacementHelpModal.cshtml`. The
  existing card titles were rejected as a reuse candidate: they are
  instructions ("Overlaps? Talk it out!"), not descriptions of the screenshot,
  so they read wrong to a screen reader. resx parity tests green. Commit `2cb4bd95`.
- **Sweep:** 21 sweepable items across the nine merged run files on
  `origin/main`; 19 were already present in their targets and were skipped
  (idempotence). Two applied: the stale 2026-08-21 MailerLite entry removed
  from `docs/architecture/debt-ledger.yml` (verified false —
  `MailerLiteGdprContributor.cs` exists and `MailerLiteClient.DeleteSubscriberAsync`
  is at line 143), and the `MailerLiteDateConverter` question recorded in
  `src/Sections/Humans.MailerLite/Docs/debt.yml` (verified — still registered at
  `MailerLiteClient.cs:354`, and `MailerLiteGroup` no longer carries `CreatedAt`).
  One item declined; see `## Skipped`. Own commit.
- **Strike 5 (comment fix + debt — findings #13, #20, #21, #22):** both
  `CityPlanningPageRenderTests` guard claims now say the test never runs in CI;
  new `Docs/debt.yml` records the three behaviour bugs. Commit `bfda6315`.
- **Phase 4 step 6, runtime verification (deferred to after Phase 7):** the run's
  one user-visible change — the six help-modal `alt` attributes — verified on the
  PR preview at `https://1525.n.burn.camp`, serving the branch head. Signed in via
  `/dev/login/city-planning`, then switched language through the app's own
  `POST /Language/SetLanguage`: `/CityPlanning/BarrioMap` renders all six alt
  strings from the resx in English, Spanish and German. Verification only, no code
  change. **The route matters:** `Program.cs` registers an initial culture provider
  that returns the signed-in user's stored `PreferredLanguage`, so it outranks both
  `Accept-Language` and a hand-set `.AspNetCore.Culture` cookie — a first attempt
  through either rendered English for *every* culture, including pre-existing keys
  that are certainly translated. That looked like a defect and was not one.

## Skipped

- **Sections passed over as blocked:** Store — open doctor PR
  peterdrier/Humans#1520.
- **Finding #18** (settings writes drop their `userId`) — adding audit writes
  or removing public parameters are both behaviour changes; Needs Peter.
- **Finding #19** (container placement keeps no history) — a design question,
  not a defect; recorded as a seam in `health.md` and Needs Peter.
- **Findings #20, #21, #22** — behaviour fixes, out of a doctor run's lane.
  Recorded in the section's new `Docs/debt.yml`.
- **Finding #23** (mutation score) — Stryker not installed; out of scope by
  instruction.
- **Finding #24** (upstream issues) — repository not configured for this
  session.
- **Phase 8 (inline round)** — skipped by instruction; unattended run.
- **Sweep item `memory: process/section-doctor-no-sdk`** (queued by the Cantina
  run, 2026-08-22) — **not applied.** Two reasons, either sufficient. Its
  content is entirely a rule about how a section-doctor run should behave, and
  Phase 5 is explicit that the sweep never carries a lesson about this skill —
  writing it as a `memory/` atom would route one around that rule. And its
  premise ("a run that cannot build") is the same premise
  `memory/process/cloud-run-dotnet-bootstrap.md` carried before
  nobodies-collective/Humans#1456 deliberately deleted it once the environment
  started shipping the toolchain, as it does in this session. Creating the atom
  now would re-add guidance for a condition that was retired on purpose.
- **Three refuted subagent findings** — a live resx key reported dead, a
  present back-navigation reported missing, a live table reported dropped. Each
  was checked against the code and declined; no change was made.

## Retro

**What did the selector/rubric get wrong?** Nothing about the pick itself —
CityPlanning sat mid-pool at score 210 in the never-doctored tier and was a
good target. But it was a *first* run on the section, and that is worth more
than a repeat run on a similarly-scored one: a first run produces the target
shape every later run diffs against. The rubric got there by score, not
because it values that. See finding #25.

**What was wasted motion?** The Prose & surface thread. On haiku it returned
three usable findings and two wrong ones — a live resx key called dead and a
back-navigation that exists called missing — and refuting them cost more than
the thread saved on a section with one small resx set and six views. See
finding #26.

**What did the assessment miss that striking revealed?** Finding #1 — the
ordering guarantee that `ICityPlanningRepository` promises and the query does
not make — was surfaced by no thread. It came out while writing `health.md`'s
invariant list, when "history is append-only, and ordered how?" had no answer
in the code matching the comment. No scanner flags a doc comment that promises
something the code does not do; only re-deriving the target from scratch does.
That is direct evidence for 3c preceding the scan, which the skill already
requires.

Phase 7's self-review earned its keep on the same file: `health.md`'s opening
paragraph said the overview map "sees every placed container, plus the official
zone overlays and the site boundary". `main.js` adds official zones and camp
polygons always, and containers and the limit zone as layers that are **off by
default**. Corrected before the PR.

**What does the target diff say?** Nothing — CityPlanning had no
`Docs/health.md` before this run, so there is no prior target to diff against.
The question is structurally unanswerable on a section's first run, and the
skill does not say so. See finding #27.

25. **The selector has no tie-break for a never-targeted section.** A section
    with no `Docs/health.md` yields a target shape every later run compares
    against; a repeat run yields a diff. The pool ranks by score within the
    never-doctored tier and treats the two as equal value. — **Needs Peter #25
    (Phase 2)**
26. **The Prose & surface thread's model floor is too low for verdicts of
    absence.** Two of its five findings this run asserted something was dead or
    missing when it was live and present, and both took a full verification
    round to refute. Either raise its floor above haiku or require every
    "dead"/"missing" verdict to cite the call site that proves it. — **Needs
    Peter #26 (Phase 3 dispatch)**
27. **Phase 6's fourth question has no answer on a first run.** With no prior
    `health.md` there is no target diff, and the run file reads as if the
    question were skipped rather than inapplicable. — **Needs Peter #27
    (Phase 6)**

## Needs Peter

- [ ] #13 — nothing CI runs guards the page routes or the `_ViewImports` set: move `CityPlanningPageRenderTests` into the section's own project, or accept the gap?
- [ ] #18 — the settings writes take a `userId` and drop it: add the audit entries, or drop the parameters?
- [ ] #19 — container placement keeps no history while polygons keep every version: a decision to write down, or an omission to fix?
- [ ] #23 — Stryker is not installed here: provision it, or leave the mutation half permanently skipped in this environment?
- [ ] #24 — `nobodies-collective/Humans` is out of session scope: widen it, or accept the Inbox thread running fork-only?
- [ ] #25 — Phase 2: tie-break the selector toward a section with no `Docs/health.md`?
- [ ] #26 — Phase 3 dispatch: raise the Prose & surface thread's model floor, or require a proving call site per absence verdict?
- [ ] #27 — Phase 6: state that a first run answers the target-diff question with "no prior target"?

## Sweep queue

- `memory: code/img-alt-is-a-user-facing-string` — an `<img alt>` on a member-facing view is a user-facing string and needs a resx key in all six cultures; `localization-admin-exempt` covers `/Admin/*`, `/TeamAdmin/*` and `/Shifts/Dashboard` only, and does not reach a member-facing modal that happens to be opened from an admin page.
- `memory: process/verify-culture-via-setlanguage` — to check a non-English culture on a preview deploy, drive `POST /Language/SetLanguage` with the page's `__RequestVerificationToken` (it is `[HttpPost] [ValidateAntiForgeryToken]`, and `curl -L` on it yields 405 after the 302 — the cookie is still set). `Accept-Language` and a hand-set `.AspNetCore.Culture` cookie do **not** work while signed in: `Program.cs` adds an initial culture provider returning the user's stored `PreferredLanguage`, which outranks both, so every culture renders English and a correct change looks broken.

## Cost

| Component | Phase | Model | Fresh in | Out | Cache write | Cache read | ~$ |
|---|---|---|---|---|---|---|---|
| worktree | phase1 | opus | 22 | 3,432 | 14,407 | 1,055,253 | 0.70 |
| select section | phase2 | opus | 6 | 1,055 | 3,678 | 304,124 | 0.20 |
| assess | phase3 | opus | 176 | 58,736 | 284,708 | 16,885,005 | 11.69 |
| Freshness (subagent) | phase3 | sonnet | 144 | 2,003 | 630,844 | 8,250,443 | 4.87 |
| Tests (subagent) | phase3 | sonnet | 106 | 317 | 193,595 | 5,196,007 | 2.29 |
| Prose & surface (subagent) | phase3 | haiku | 400 | 2,600 | 218,980 | 2,641,056 | 0.55 |
| Comments (subagent) | phase3 | sonnet | 146 | 217 | 447,237 | 9,123,376 | 4.42 |
| strike: delete dead assets, dead resx key, unused project reference | phase4 | opus | 70 | 33,767 | 151,162 | 7,400,541 | 5.49 |
| strike: correct doc and comment claims | phase4 | opus | 60 | 12,703 | 47,227 | 3,171,273 | 2.20 |
| strike: add tests for delete, registration-info year key, upload guards | phase4 | opus | 48 | 12,761 | 25,841 | 3,114,878 | 2.04 |
| strike: localize help-modal alt texts | phase4 | opus | 40 | 7,546 | 22,264 | 2,855,269 | 1.76 |
| strike: fix guard claims, add debt.yml | phase4 | opus | 16 | 1,971 | 10,239 | 1,233,666 | 0.73 |
| **total** | | | 1,234 | 137,108 | 2,050,182 | 61,230,891 | **36.94** |

API-equivalent $, list rates; run under subscription quota. Measured Phase 1 to
PR creation; PR create/backfill and Phase 8 excluded.
