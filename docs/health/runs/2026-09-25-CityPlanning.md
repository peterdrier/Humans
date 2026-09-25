# section-doctor — CityPlanning — 2026-09-25

- Invocation: scheduled unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `45a3a8d7a` (origin/main at branch point); branch `section-doctor/2026-09-25T011609Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1824

## Assessment summary

Re-doctor from the 2026-08-26 run. Since then the section gained a /Settings tab, a bulk polygon import and its own help content, and the placement toggles left the barrio-map admin panel. The code held its shape; what it told people did not keep up. The in-app help, the member guide and the section docs described a read-only container map for everyone, a restore that saves the current shape first, dates that open and close placement by themselves and an admin panel that toggles placement — none of which the code does. The target was regenerated from behaviour: invariants now cite their enforcing lines, "map admin" is one definition, and the registration blurb's second front door is recorded as a seam.

Independence check: pass — items 2 and 8 come from the target (one definition of map admin; the registration blurb's front doors).

- 2026-09-25 session: no live render happened — an unattended cloud run never boots the app. The page-title and settings-tab comment edits are covered by the build and `.claude/razor-lint.sh`; the preview deploy is where a render is checked.
- 2026-09-25 session: the branch is named `section-doctor/<timestamp>` as the skill directs, not the routine's generic branch name.
- 2026-09-25 session: the routine prompt said to skip Phase 8; this run followed it.
- 2026-09-25 session: one full-solution test run failed on Razor errors in Workgroups, a section this run did not touch; a fresh build of `origin/main` failed the same way until `dotnet build-server shutdown`, after which everything built and passed. A stale compiler server, not code.

## Findings

1. The in-app help (barrio map, container map, overview guides and glossaries) and the member guide describe behaviour the code does not have: a read-only container map for everyone, a restore that writes the current shape to history first, placement dates that open and close the phase, an admin panel that toggles placement, and cursors and warnings on the overview map.
2. The map-admin rule (Admin or CampAdmin, else city-planning team member) is written three times: `CityPlanningMapAdminHandler` behind `PolicyNames.CityPlanningMapAdmin`, and a private `IsMapAdminAsync` in each controller.
3. Invariants with no pinning test: restore and export are map-admin only; a broadcast failure never fails the save. The Tests thread also lists the container-map gate and the CampAdmin exemption at the API as unpinned.
4. Doc comments name the wrong gate (`CampAdminOrAdmin` for the settings tab), the wrong owner (Shell for Camps' view and Development's seeder), the wrong path (migrations folder, barrio-map layers file), a hub consumer that does not exist (the container map), say the registration write is reachable only from Camps, and narrate history.
5. The section invariant doc and feature doc: BarrioMap access, the architecture-test pin, the admin panel's contents, restore, FK annotations that the configuration does not declare, the measure tool and container photos are stale; freshness triggers miss the Containers authorization and controller files the docs depend on.
6. Polygon save and restore each carry their own copy of the broadcast-then-answer tail.
7. `IsValidJson` is copied in the API controller and the service; a private placement-date overload has one caller; `GetCampPolygonsAsync` re-filters rows its query already scoped.
8. The registration blurb has two front doors in two sections: CityPlanning's settings tab and Camps' `CampAdminController.UpdateRegistrationInfo`.
9. Container placement is authorised on the public year but saved on the route year: `ContainerAuthorizationHandler` checks the phase and the lead's season against `settings.Year`.
10. Restoring a history id that does not belong to the season throws and surfaces as a 500, not a 404.
11. The settings tab's registration card and the controller's TempData messages rely on the admin/operator localization exemption, but city-planning team members — who may hold no admin role — reach them.
12. Inbox — nobodies-collective/Humans#523: its paths predate the move into `src/Sections/Humans.CityPlanning/wwwroot`, it says `AreaSqm` is recomputed server-side (it is not), and its acceptance line names `SharedResource` where the section uses `CityPlanningResource`.
13. Inbox — `CITY-1` (container CRUD redirects use the public year) is still true.
14. The overview and container-map page titles are hardcoded English.

## Debt verified

- CITY-1 — still-true — container CRUD redirects use the public year, not the route year — `src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs` `CreateBarrioContainer`, `EditContainer`, `DeleteContainer`.

Every row in the section ledger was verified; the cap was not reached.

## Worked

- Finding 1: help guides, glossaries and the member guide rewritten to what the maps do; `SectionHelpTests` expectations follow the new last lines. Sonnet executor; every edit checked against the code.
- Finding 2: both controllers ask `IAuthorizationService` for `CityPlanningMapAdmin`; tests build the section's real policy and handler. Reviewer (light tier) approved with doc corrections, applied (`Docs/authorization.md`, `Docs/health.md`, the feature doc, `docs/authorization-inventory.md`, and passing the token through `RequireCurrentUserAsync`); it checked role and user-id equivalence, every call site, production registration, and that no handler can fail the policy.
- Finding 3: `CityPlanningApiControllerTests` pins restore and export as map-admin only, a city-planning team member restoring, save broadcasting the saved shape, and a failed broadcast leaving the save and an OK.
- Finding 4: comments corrected and history narration cut. Sonnet executor; its present-tense rewrite of the Camps-delete contract and the registration comment were reworded on main to keep the facts they carried.
- Finding 5: section and feature docs corrected; triggers added. Sonnet executor; the measure-tool line was narrowed on main (right-click deletes only in measure mode) and the admin-containers line brought in line with the photo gallery.
- Finding 6: one `BroadcastAndReturnAsync` helper.
- Finding 7: the placement-date overload inlined and the redundant filter cut. Reviewer (light tier) approved; it checked the write is identical and that the repository query already scopes to the display keys.
- Finding 14: page titles localized in all six cultures.
- Findings 7 (the `IsValidJson` copy), 8, 9 and 10: ledgered as `CITY-2`, `CAMPS-3`, `CONTAINERS-3` (root `CITY-1`) and `CITY-3`.

## Skipped

- Finding 7, the `IsValidJson` copy: folding it into the service means a result-returning save in place of the throw, a change to `ICityPlanningService`; ledgered as `CITY-2`.
- Findings 9 and 10: behaviour changes; ledgered, and finding 10 is in Needs Peter.
- Finding 11: whether the exemption covers team members is Peter's call (Needs Peter).
- Finding 12: existing issues are read-only to a run; the recommended edit is in Needs Peter.
- Finding 13: deferred by Peter to the winter year-specific work.
- Finding 3, the container-map gate and the CampAdmin exemption at the API: not pinned this run; the restore/export and broadcast invariants took the budget.
- No section was blocked this run.

## Retro

**Selector.** CityPlanning was a fair pick: its churn since the last run was feature work (the settings tab, bulk import, help content) that had outrun the prose describing it.

**Wasted motion.** The help-guide strike broke `SectionHelpTests`, which pin each guide's last line; reading the tests before dispatching the executor would have folded the test update into its brief. A full-suite run was lost to a stale compiler server; shutting the build server down before the first full run would have avoided it.

**What striking revealed.** The executors' present-tense rewrites were mostly right but twice dropped or bent a fact (the Camps-delete contract, the measure tool's right-click), so verifying every executor edit against the code earned its cost. Collapsing the map-admin rule surfaced four more docs restating the old inline check.

**Target diff.** The earlier target was not wrong; the section grew a second front door (the settings tab) for the registration blurb and a policy the controllers did not use. The new target records both, and cites each invariant at its enforcing line.

## Needs Peter

- [ ] 10 — return 404 for a restore of an unknown history id?
- [ ] 11 — do city-planning team members count as operators for the localization exemption?
- [ ] 12 — edit nobodies-collective/Humans#523 (paths, `AreaSqm` claim, resource name)?

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/CityPlanning.md` | changed |
| `src/Sections/Humans.CityPlanning.Contracts/CityPlanningOptions.cs` | changed |
| `src/Sections/Humans.CityPlanning.Contracts/Humans.CityPlanning.Contracts.csproj` | reviewed |
| `src/Sections/Humans.CityPlanning.Contracts/ICityPlanningService.cs` | changed |
| `src/Sections/Humans.CityPlanning.Contracts/ICityPlanningServiceRead.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Authorization/CityPlanningMapAdminHandler.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Authorization/CityPlanningMapAdminRequirement.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.ca.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.cs` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.de.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.es.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.fr.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.it.resx` | changed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.resx` | changed |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | changed |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningDbContext.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningRepository.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CampPolygonConfiguration.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CampPolygonHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CityPlanningSettingsConfiguration.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/ICityPlanningRepository.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20260809142312_BaselineCityPlanning.Designer.cs` | generated |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20260809142312_BaselineCityPlanning.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/CityPlanningDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.CityPlanning/Docs/CityPlanning.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/authorization.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.CityPlanning/Docs/debt.yml` | changed |
| `src/Sections/Humans.CityPlanning/Docs/features/city-planning.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/health.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningBarrioMap.glossary.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningBarrioMap.guide.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningOverview.glossary.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningOverview.guide.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/ContainerMap.glossary.md` | reviewed |
| `src/Sections/Humans.CityPlanning/Docs/help/ContainerMap.guide.md` | changed |
| `src/Sections/Humans.CityPlanning/Domain/CampPolygon.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Domain/CampPolygonHistory.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Domain/CityPlanningSettings.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Humans.CityPlanning.csproj` | reviewed |
| `src/Sections/Humans.CityPlanning/Models/CityPlanningMeasurePanelViewModel.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Models/CityPlanningViewModels.cs` | changed |
| `src/Sections/Humans.CityPlanning/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Section.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/SectionAccessMatrix.cs` | changed |
| `src/Sections/Humans.CityPlanning/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/SectionConfiguration.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/SectionEndpoints.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/SectionHelp.cs` | changed |
| `src/Sections/Humans.CityPlanning/SectionNav.cs` | changed |
| `src/Sections/Humans.CityPlanning/SectionPolicies.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/SectionSettings.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningDtos.cs` | reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningHub.cs` | changed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningService.cs` | changed |
| `src/Sections/Humans.CityPlanning/ViewComponents/CityPlanningSettingsTabViewComponent.cs` | changed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Admin.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/BarrioMap.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/ContainerMap.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Containers.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Index.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/_HistoryOffcanvas.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/_MeasurePanel.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/Shared/Components/CityPlanningSettingsTab/Default.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/Views/Shared/_PlacementHelpModal.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/Views/_ViewImports.cshtml` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-colors.png` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-draw.png` | reviewed |
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
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/layers.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/main.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/placement-notes.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/sidebar.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/main.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/map-constants.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/measure.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/official-zones-layer.js` | reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/sound-zone-colors.js` | reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningApiControllerTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningArchitectureTests.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningControllerSettingsRedirectTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningRepositoryTests.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningServiceTests.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningSettingsTabViewComponentTests.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningTestBase.cs` | changed |
| `tests/Humans.CityPlanning.Tests/ContainerPlacementPhaseTests.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/Humans.CityPlanning.Tests.csproj` | reviewed |
| `tests/Humans.CityPlanning.Tests/SectionAccessMatrixTests.cs` | reviewed |
| `tests/Humans.CityPlanning.Tests/SectionHelpTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/SectionSettingsTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus | 2, 6, 7, 8 |
| Behavior & bugs | main | opus | 9, 10, 11 |
| Freshness | subagent (`doctor-reader`) | opus low | 1, 5 |
| Conformance | subagent (`general-purpose`) | haiku | none — every detector clean |
| Tests | subagent (`doctor-reader`) | opus low | 3 |
| Prose & surface | subagent (`general-purpose`) | haiku | 14; no dead keys, parity and prefixes clean; InspectCode not run (tool absent) |
| History | subagent (`doctor-reader`) | opus low | 4 |
| Comments | subagent (`doctor-reader`) | opus low | 4, 11 |
| Inbox | subagent (`doctor-reader`) | opus low | 12, 13 |
