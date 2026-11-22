# section-doctor — CityPlanning — 2026-11-20

- Invocation: unattended (daily routine, cloud thread)
- Anchor commit: `a8099cba0` (origin/main at branch point); branch `section-doctor/2026-11-20T061015Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#2464

## Assessment summary

Re-doctor from `BASE: af0c123e97dd` (last run 2026-09-25). The section's structure held; what drifted was what it says about itself. The previous target claimed stored GeoJSON was checked as parseable JSON only, while outlines are now checked as closed polygons in range and overlays as GeoJSON; its year-correct container admin seam had been built. The invariant doc told readers container create, edit and delete are phase-gated, when only placement is. The polygon save restated "map admin" with a role predicate beside the policy that defines it. Since the last run a tech-debt sweep split reads into `CityPlanningReader`, which the service constructs and forwards to; that split is put to Peter rather than undone.

- 2026-11-20 session: no live render happened. An unattended cloud run never boots the app; the one cshtml text edit is covered by the build and `.claude/razor-lint.sh`, and the preview deploy is where a render is checked.
- 2026-11-20 session: reforge `surface-score` refused to print, because it ran before the first restore finished and saw an uncompiled solution; it was rerun after the build and wrote nothing before this file was written. No score is quoted.
- 2026-11-20 session: nobodies-collective/Humans is not readable from this session, so upstream issues were not reviewed.

Independence check: pass. Findings 1, 2, 3 and 13 came from the target: a spec-vs-reality delta, a stale target, a shape mismatch against one definition of map admin, and a partial abstraction.

Target diff: the section moved (validation tightened, the year-correct seam built, the reader split) and the earlier target was also wrong in one place (it cited container-shape validation in this section's controller after it had moved to Containers).

## Findings

1. `Docs/CityPlanning.md` said leads may add, edit or delete containers only while container placement is open; the phase gates only placement (place, annotate, clear).
2. The previous target was false on GeoJSON validation and on the year-correct container admin seam, and its cites had drifted.
3. `SaveCampPolygon` exempted `CampsRoles.IsCampsAdmin` beside the `CityPlanningMapAdmin` policy; the `Saved` fallback note was stated in the controller and as the service default.
4. No controller test pinned `SaveCampPolygon` refusing a user who may not edit.
5. The Map Admin definition in three glossaries, the barrio map guide and the member guide left out Admin.
6. `Docs/CityPlanning.md` named a schedule method that does not exist; the method is `UpdatePlacementScheduleAsync`.
7. The feature spec claimed a database fallback for container photos and an FK from container to camp, and left out the photo entity.
8. The container map help never mentioned placement notes or the sketch image.
9. Comments and docs that disagreed with the code or narrated history: the `_ViewImports` note said the page-render test never runs in CI; issue-number and lane narration across the docs.
10. Comments that restated the next line, across the JS bundles, one view and the tests.
11. Tests that pinned a behaviour already pinned at another layer.
12. Freshness triggers watched `RoleChecks.cs` for a CampsAdmin claim it does not carry, and missed the Containers handler and photo entity the docs describe.
13. `CityPlanningService` forwards its reads to `CityPlanningReader`, a helper it constructs itself with the same dependencies.
14. `ExportContainersGeoJson` decides in the controller which containers a viewer may export, with a gate unlike the Place rule (CITY-9).
15. The settings tab view component re-derives whether registration info has a year to write to (CITY-10).
16. Which camp the current user leads this year is composed separately in `FindUserLeadCampAsync`, `FindUserLeadSeasonIdAsync` and `FindUserLeadCampIdAsync` (CITY-11).
17. The container map page has no in-page link back to the overview; the shell nav is the way out.
18. The settings tab told admins to use the "Event tab"; it is labelled Events.
19. The unique index on `CampSeasonId` is pinned by no test.

## Debt verified

- `Docs/debt.yml` had no open rows at the branch point; nothing to verify. The cap was not reached.

## Worked

- Issue comments: none (no open section issues readable).

- 1, 5, 6, 7, 8, 12, 18: docs and help corrected (sonnet executor; its additions were two more `#2102` references in the feature spec's stories, kept).
- 2: target regenerated in `Docs/health.md`.
- 9, 10: comments rewritten or cut (sonnet executor). Main reverted the cut of the `edit.js` History banner, which a browser test slices on, and restored the sidebar callback comments as carrying when each fires.
- 11: four tests removed (repository first save, history-entry null with random ids, service second save, official-zones missing file); reviewer approved.
- 3, 4: polygon save asks the map-admin policy; the note fallback lives in the service; deny-path test added and shown to fail with the gate disabled. Reviewer rejected the first pack over three stale doc claims (`Docs/authorization.md`, `docs/authorization-inventory.md`, the health cite), approved the rework.
- 14, 15, 16: ledgered as CITY-9, CITY-10, CITY-11.

## Skipped

- 13: Peter ruled no pass-through classes: `CityPlanningReader` merged into `CityPlanningService`. The same pattern elsewhere goes to its own thread.
- 17: judged sound; the shell nav is a way out, and adding a link means new localized copy on a member page.
- 19: no test added; the index is declared in one line of configuration and Peter wants lean tests.
- Prose & surface finding on the settings tab's English copy: not a finding, the tab is a map-admin surface (`memory/code/localization-admin-exempt.md`).
- Tests thread's claim that `CanUserEditAsync_LeadOfDifferentYear_ReturnsFalse` duplicates the other-season test: false, they cover the year check and the lead check, two branches.
- Blocked sections passed over: none.

## Retro

**Selector.** City Planning was the right pick: a large churn since its last run, almost all of it other runs' sweeps (comments, audit, page titles, a reader split), which is exactly the drift a re-read catches.

**Wasted motion.** reforge was started before the restore finished and refused to print; it should start after the first build. The comment executor removed a banner comment a browser test slices the source on; its own search looked for comment text in tests but not for `indexOf` anchors.

**What striking revealed.** The reviewer's rejection found three doc claims about the polygon save gate that the blast grep listed but main had not read as stale; a strike that changes a gate needs the authorization docs read, not only grepped.

**Target diff.** The section moved more than its docs did: validation tightened, a seam was built, and a sweep added a structural split without touching the target's structure section.

## Needs Peter

- [x] 13 — merge `CityPlanningReader` into `CityPlanningService` (Peter, 2026-11-20: one service, never a pass-through).

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/CityPlanning.md` | changed |
| `src/Sections/Humans.CityPlanning.Contracts/CityPlanningOptions.cs` || reviewed |
| `src/Sections/Humans.CityPlanning.Contracts/Humans.CityPlanning.Contracts.csproj` || reviewed |
| `src/Sections/Humans.CityPlanning.Contracts/ICityPlanningService.cs` || reviewed |
| `src/Sections/Humans.CityPlanning.Contracts/ICityPlanningWrite.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Authorization/CityPlanningMapAdminHandler.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Authorization/CityPlanningMapAdminRequirement.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningAuditActions.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.ca.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.de.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.es.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.fr.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.it.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.nl.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/CityPlanningResource.resx` || reviewed |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs` | changed |
| `src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningDbContext.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningDbContextFactory.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/CityPlanningRepository.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CampPolygonConfiguration.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CampPolygonHistoryConfiguration.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Configurations/CityPlanningSettingsConfiguration.cs` | changed |
| `src/Sections/Humans.CityPlanning/Data/ICityPlanningRepository.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20260809142312_BaselineCityPlanning.Designer.cs` | generated |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20260809142312_BaselineCityPlanning.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20261021171356_PlacementScheduleOffsets.Designer.cs` | generated |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20261021171356_PlacementScheduleOffsets.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20261104165348_DropCityPlanningPlacementDates.Designer.cs` | generated |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20261104165348_DropCityPlanningPlacementDates.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20261112195800_RenameTablesToSectionPrefix.Designer.cs` | generated |
| `src/Sections/Humans.CityPlanning/Data/Migrations/20261112195800_RenameTablesToSectionPrefix.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Data/Migrations/CityPlanningDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.CityPlanning/Docs/CityPlanning.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/authorization.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/data-access.md` || reviewed |
| `src/Sections/Humans.CityPlanning/Docs/debt.yml` | changed |
| `src/Sections/Humans.CityPlanning/Docs/features/city-planning.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/health.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningBarrioMap.glossary.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningBarrioMap.guide.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningOverview.glossary.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/CityPlanningOverview.guide.md` || reviewed |
| `src/Sections/Humans.CityPlanning/Docs/help/ContainerMap.glossary.md` | changed |
| `src/Sections/Humans.CityPlanning/Docs/help/ContainerMap.guide.md` | changed |
| `src/Sections/Humans.CityPlanning/Domain/CampPolygon.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Domain/CampPolygonHistory.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Domain/CityPlanningSettings.cs` | changed |
| `src/Sections/Humans.CityPlanning/Humans.CityPlanning.csproj` | changed |
| `src/Sections/Humans.CityPlanning/Models/CityPlanningMeasurePanelViewModel.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Models/CityPlanningViewModels.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Properties/AssemblyInfo.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Section.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningDtos.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningGeoJsonValidation.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningHub.cs` | changed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningReader.cs` | changed |
| `src/Sections/Humans.CityPlanning/Services/CityPlanningService.cs` | changed |
| `src/Sections/Humans.CityPlanning/ViewComponents/CityPlanningSettingsTabViewComponent.cs` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Admin.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/BarrioMap.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/ContainerMap.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Containers.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/Index.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/_HistoryOffcanvas.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/CityPlanning/_MeasurePanel.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/Shared/Components/CityPlanningSettingsTab/Default.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/Views/Shared/_PlacementHelpModal.cshtml` || reviewed |
| `src/Sections/Humans.CityPlanning/Views/_ViewImports.cshtml` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-colors.png` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-draw.png` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-edit.png` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-history.png` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-outside-limits.png` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/img/city-planning/barrio-overlap.png` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/admin-import.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/config.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/edit.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/geometry.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/layers.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/main.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/marquee-direct-select.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/signalr.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/barrio-map/state.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/config.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/api.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/config.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/geometry.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/interaction.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/layers.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/main.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/placement-notes.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/container-map/sidebar.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/main.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/map-constants.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/measure.js` | changed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/official-zones-layer.js` || reviewed |
| `src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/shared/sound-zone-colors.js` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/barrio-history.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/barrio-import-failures.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/barrio-import-preview.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/barrio-restore-lifecycle.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/barrio-save-lifecycle.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/container-api-errors.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/container-interaction.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/container-sidebar-labels.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Browser/placement-notes.test.cjs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningApiControllerTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningArchitectureTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningContainerAdminYearTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningControllerSettingsRedirectTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningHubTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningMapAdminHandlerTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningRepositoryTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningServiceTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/CityPlanningSettingsTabViewComponentTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/CityPlanningTestBase.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/ContainerPlacementPhaseTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/Humans.CityPlanning.Tests.csproj` || reviewed |
| `tests/Humans.CityPlanning.Tests/PlacementScheduleTests.cs` | changed |
| `tests/Humans.CityPlanning.Tests/SectionAccessMatrixTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/SectionHelpTests.cs` || reviewed |
| `tests/Humans.CityPlanning.Tests/SectionSettingsTests.cs` || reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus 5.5 | 3, 13 |
| Behavior & bugs | main | opus 5.5 | 2 |
| Controller intent | subagent (`doctor-reader`) | opus-low | 3, 14, 15, 16 |
| Freshness | subagent (`doctor-reader`) | opus-low | 1, 2, 5, 6, 7, 8, 12 |
| Conformance | detectors on main, classified on main (self-run: one advisory hit, `CityPlanningAuditActions.cs` at the project root, advisory per the rule's notes) | opus 5.5 | none |
| Tests | subagent (`doctor-reader`) | opus-low | 4, 11, 19 |
| Prose & surface | subagent (`general-purpose`) | haiku | 9, 17, 18 |
| History | subagent (`doctor-reader`) | opus-low | 9 |
| Comments | subagent (`doctor-reader`) | opus-low | 9, 10 |
| Inbox | not dispatched: peterdrier/Humans covered with no open section issues; nobodies-collective/Humans not covered (probe of #1118 denied, repository not configured for this session); section ledger empty | none | none |

