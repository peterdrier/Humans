# section-doctor — Containers — 2026-09-08

- Invocation: unattended daily run (cloud), no arguments; Phase 8 skipped per the stored prompt.
- Anchor commit: `10199a23` (origin/main at branch point); branch `section-doctor/2026-09-08T011600Z`.
- Budget: standard daily (~2.5 h).
- PR: peterdrier/Humans#1620

## Assessment summary

Re-doctor, 23 days after the shakedown run (peterdrier/Humans#1341) and after the five-image
feature (peterdrier/Humans#1433). The selector returned JUDGMENT REQUIRED (every eligible section
previously doctored); Containers was the oldest eligible with the most change since.

The previous `health.md` was in the old scorecard format, so 3c rewrote it as a target shape.
The reading found one structural gap against that target — a view-model layer copying the DTOs
(1–3) — plus member-facing English on the camp container page (4), a batch of unpinned
invariants (6–8), narrating and stale comments (12), and section-doc drift (10–11). Layout and
authorization are clean; the resx set is complete in all six cultures (14 is a naming backlog,
not a gap).

## Findings (ranked, 3e)

1. [collapse, target] View models duplicate DTOs. `ContainerViewModel`, `ContainerPlacementViewModel`
   and `ContainerWithPlacementViewModel` were subsets of `ContainerDto`, `ContainerPlacementDto` and
   `ContainerWithPlacement`; both host controllers (`ContainerController`, `CityPlanningController`)
   carried private copy helpers. Target §3: the DTO is the display shape; `IsPlaced` /
   `HasPlacementInfo` live on the records. Reviewer-gated.
2. [rename, target] `ContainerPlacementDto.PlacementImageStoragePath` carried a URL
   (`Service.ToPlacementDto` prefixes `/`); both consumers copied it into a field named
   `PlacementImageUrl`; the API controller emitted it as `placementImageUrl`. Rename on the DTO.
   Bundled with 1.
3. [delete, shape scan] Dead surface: `ContainerCampGroup.CampSlug` (copied into
   `BarrioContainerGroup.CampSlug`, read by nothing); `ContainerPlacementDto.PlacementImageContentType`
   (no reader); `ContainerIndexViewModel.CampId` (unread); `CanManage` constant `true` at both hosts
   (`ContainerIndexViewModel.CanManage`, `ContainerCardModel.CanManage`, both views'
   `@if (Model.CanManage)`). Reviewer-gated.
4. [fix, Prose 1–6 + main] Member-facing hardcoded English on `/Camp/{slug}/Containers`:
   `Index.cshtml` title, `ContainerController` flash strings, carousel "Previous"/"Next" in
   `_ContainerCardModals`. One answer: inject `IStringLocalizer<ContainersResource>`, keys in six
   cultures, as Governance does.
5. [Needs-Peter, main] `SetError(ex.Message)` surfaces service exception text ("A container can have
   at most 5 images.", "Container name must not contain…") untranslated on the member page. Fork:
   error keys thrown by the service vs. accept English for validation failures.
6. [test, Tests 1] Audit trail unasserted across all seven writes — pin `LogAsync` on one placement
   write.
7. [test, Tests 2–7] Unpinned invariants: placement-image add/replace/remove branches; annotate
   requires an existing row; Save preserves notes; 10 MB reject; Clear keeps an image-only row;
   55 MB `RequestSizeLimit` on the two POSTs.
8. [test, Tests 8–9] Handler tests substituted `ICityPlanningService` where the handler takes
   `ICityPlanningServiceRead`; one test asserted `DidNotReceive().GetSettingsAsync` on
   `ICampServiceRead`, which the handler never calls; the next line restated the one before it.
9. [delete, Tests 12 / Freshness 11] `tests/Humans.Containers.Tests.csproj` referenced
   `Humans.Shifts.Contracts`; nothing used it.
10. [doc, Freshness 5–7, History 10–15] `Docs/Containers.md`: hand-counted key totals that had
    drifted (drop the counts); "Shell's `CityPlanning/Containers`" (it is City Planning's);
    freshness triggers missed the CityPlanning controllers and `_ViewImports` the doc makes claims
    about; narration (rejected-alternative aside, Status line, #858/`holded_*` aside, step/G5
    citations, `_FavouriteButton` parenthetical, "after the move"); bare `#797`; "shared card view
    models" wording changes with 1.
11. [doc, Freshness 1–2] `Docs/data-access.md`: `ContainerService` (type is `Service`),
    `ICampService` (injected is `ICampServiceRead`).
12. [comments, Comments 1–6, 8–16, 20–25, 29; History 1–9, 16–17; Freshness 3–4] Code-comment truth
    batch: `Section.cs` policy comment naming a registration that does not exist and a nonexistent
    "§15 step 6"; `Humans.Containers.csproj` "Humans.Interfaces" (renamed `Humans.Base`), design-§
    refs pointing at unrelated sections, "byte-identical" G5 clause; `ContainersResource.cs` step/§
    citations; `ContainerAuthorizationTarget` cref to an `internal` type in another namespace plus a
    next-agent instruction; `#797`/`#750` unqualified; `ContainersDbContext` migrations path
    `Migrations/Containers/` (actual `Data/Migrations/`); banner dividers; `AuditEntityTypes` /
    `_ViewImports` / arch-test G5 narration; handler summary restating its name;
    `ContainersDbContextFactory` trim.
13. [debt, Tests 15] No controller tests; the Forbid paths and the 403 on the camp page are unpinned
    end to end. Recorded in `Docs/debt.yml`.
14. [conformance, report] Every resx key lacks the `Containers_` prefix (`Container_` /
    `ContainerMap_`) — standing backlog, report only. Layout clean.
15. [sweep, Freshness 8] `Humans.CityPlanning/Docs/CityPlanning.md` :17, :36, :144, :153, :167
    claim lead container CRUD is phase-gated; only `Place` is. Owner: CityPlanning → sweep queue.
16. [sweep, Freshness 10] `docs/architecture/dependency-graph.md` :170 labels the node
    `ContainerService` (type is `Service`) and omits the Users and CityPlanning edges the csproj
    carries. Central → sweep queue.
17. [sweep, Freshness 5–6 hits, 12] `docs/sections/G5-SECTION-TEMPLATE.md` :464, :532
    ("9 `ContainerMap_*`"), :911–917 ("consumers were all in Shell");
    `docs/authorization-inventory.md:364` line drift. Central → sweep queue.
18. [reviewed, keep] Exact-message assertions (Tests 11) pin the message contract; test-file style
    split (Tests 13); `IsLeadButPhaseClosed`'s `&& !isPlacementOpen` is implied by `!canPlace` after
    Manage passed — harmless, left.
19. [inbox] No open Containers issues in peterdrier/Humans; no ledger rows; no `Docs/debt.yml`;
    in-app issues unreachable (no database in the cloud container).
20. [lesson, Phase 4 / Phase 7] The cloud container has no database (no Postgres, no Docker), so
    every view-touching strike this run (1, 3, 4) is verified by the Razor class-library compile
    and the section's tests, never by a rendered page. The skill's Phase 4 does not say what a
    run does with a UI strike it cannot render: state the gap in the run file and PR body and
    point at the preview deploy, or hold view strikes for interactive runs. This run did the
    former.
21. [sweep applied with a reading, Phase 5] The Agent run's `memory:` item on
    `debt-ledger-additions` was a question ("say which reading is intended" for the `tests/` row).
    The sweep has no Peter, so it applied the reading every doctor run already uses — a section's
    own `tests/Humans.<X>.Tests` is section-owned and its gaps go to that section's `debt.yml`;
    `tests/Humans.Testing` and other shared test projects stay central — in the atom and its INDEX
    line. Confirm or revert.
22. [lesson, Phase 4] The doctor-reviewer dispatch prompt did not open with a `thread:` marker, so
    the cost report names its row by a transcript hash instead of "Reviewer". Phase 4's reviewer
    dispatch should carry the marker the way 3d's thread prompts do.

## Worked

- 1, 2, 3, 9 — `d4d40d20`: the three view-model records deleted, `IsPlaced` / `HasPlacementInfo`
  moved onto `ContainerPlacementDto` / `ContainerWithPlacement`, `PlacementImageStoragePath`
  renamed `PlacementImageUrl`, the dead fields and the constant `CanManage` removed, both host
  controllers' copy helpers gone, `BarrioContainerGroup` and `_ContainerFormFields` retyped to the
  DTOs, the unused Shifts.Contracts test reference dropped. doctor-reviewer: APPROVE.
- 12 — `829a8175`: the comment-truth batch across `Section.cs`, the csproj, `ContainersResource.cs`,
  `ContainerAuthorizationTarget`, `IContainerService`, `Service`, `ContainersDbContext`, the
  factory, `IContainerRepository`, `AuditEntityTypes`, `_ViewImports`, the handler and the
  architecture tests.
- 6, 7, 8 — `65c76638`: placement tests rewritten around shared helpers with the audit entity
  type, notes/image preservation, annotate-requires-row, image add/replace/remove, 10 MB reject and
  image-only-row-survives-Clear pinned; the 10 MB reject pinned on Create too; the 55 MB
  `RequestSizeLimit` pinned on both POSTs by reading the attribute's backing field; handler tests
  now substitute `ICityPlanningServiceRead` and the two false assertions are gone.
- 4 — `89034c3e`: `ContainerController` localizes its flash messages through
  `IStringLocalizer<ContainersResource>`, the page title and carousel controls read keys; six keys
  added in all six cultures; parity tests pass.
- 10, 11, 13 — `c33114d7`: `Containers.md` and `data-access.md` realigned, `Docs/debt.yml` created.
- Sweep — `94b5ca98`: the Agent and Settings 2026-08-28 queues applied (two central ledger rows, one
  Agent `debt.yml` row, the `debt-ledger-additions` row per 21). Every earlier merged run's items
  were already present in their targets.

No page was rendered this run (20). The view changes compile as part of the section's Razor
class library and the section's tests pass; the preview deploy is where the camp container page
and City Planning's container pages get looked at.

## Skipped + why

- 5 — a fork with two defensible answers; Needs Peter.
- 14 — report only; the rename touches every consumer of the section's keys and is a standing
  backlog, not this run's.
- 15, 16, 17 — other sections' or central docs; the run touches only its own section's files, so
  they go through the sweep queue.
- 18 — reviewed and kept.
- 19 — nothing in the inbox.
- Sections passed over as blocked (open doctor PR): Auth, Backdoor, Budget, Calendar, Campaigns,
  Camps, Consent, Debug, EarlyEntry, Email, Feedback, Finance, Gate, GoogleIntegration, Governance,
  Holded, Monitor, Scanner, Search, Shifts, Stripe, Surveys, Teams, TicketTailor, Tickets, Tour,
  Users. Feature-active and skipped: AuditLog, Gdpr, Notifications, Rideshare.

## Retro (Phase 6)

- **Selector.** JUDGMENT REQUIRED, decided by staleness and change volume: Containers over Guide
  and Cantina. Right call — the five-image feature had grown the copy layer (1) and left the
  placement-image branches unpinned (7), which is exactly the drift a re-doctor exists to catch.
- **Wasted motion.** Two build rounds on the request-size test (a missing `using`, then the
  attribute exposing no public limit — read via reflection). Thread outputs arrived as JSONL
  transcripts and needed extraction before reading. The cost report detected two context
  compactions (during assess and during the section-docs strike); the Phase 5 re-read rule did
  its job — the mechanics below were re-anchored from the skill, not the summary.
- **Missed by the assessment, found by striking.** The ranked list called the view models
  "field-for-field copies"; the reviewer corrected that to "subsets" (the view models dropped
  fields the DTO carried), which is the wording this file uses. The collapse also pulled two
  caller files the assessment had not listed as changing — `BarrioContainerGroup` in City
  Planning's view models and the `_ContainerFormFields` model type — and the constant `CanManage`
  (3) surfaced only while reading the views to unwrap the model.
- **Target diff.** The previous `health.md` was the old scorecard, so most of the diff is format.
  The one substantive move is §3: the earlier "ideal shape" said a rewrite would produce nearly
  this code, and the view-model layer says otherwise — partly the earlier target being wrong
  (the copy layer predates it) and partly the section moving (the five-image feature widened it).
  The target is now realised in code, so the next diff should be flat unless the legacy-column
  retirement (§5) lands.

## Needs Peter

- [ ] 5 — localize service exception messages via error keys thrown by `Service`, or accept English on validation failures?
- [ ] 14 — `Containers_` prefix backlog: rename now (every consumer) or leave?
- [ ] 20 — Phase 4/7: when a run cannot render a page, state it and lean on the preview deploy (this run), or hold view strikes for interactive runs?
- [ ] 21 — Phase 5: confirm the `debt-ledger-additions` reading the sweep applied, or revert it.
- [ ] 22 — Phase 4: open the reviewer dispatch prompt with a `thread:` marker so the cost report names its row?

## Sweep queue

- `debt: Humans.CityPlanning — Docs/CityPlanning.md :17, :36, :144, :153, :167 claim lead container CRUD is phase-gated; only ContainerOperation.Place is (ContainerAuthorizationHandler; Containers.md invariants). Finding 15, /section-doctor on Containers 2026-09-08.`
- `debt: central — docs/architecture/dependency-graph.md :170 labels the Containers node ContainerService (the type is Service) and omits the Users and CityPlanning edges Humans.Containers.csproj carries. Finding 16, /section-doctor on Containers 2026-09-08.`
- `debt: central — docs/sections/G5-SECTION-TEMPLATE.md :464 and :532 count Containers resx keys ("9 ContainerMap_*") that have since changed, and :911–917 say the section's consumers "were all in Shell" (they are City Planning's); docs/authorization-inventory.md:364 cites a line number that has drifted. Finding 17, /section-doctor on Containers 2026-09-08.`

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Containers/Authorization/ContainerAuthorizationHandler.cs` | changed |
| `src/Sections/Humans.Containers/ContainersResource.ca.resx` | changed |
| `src/Sections/Humans.Containers/ContainersResource.cs` | changed |
| `src/Sections/Humans.Containers/ContainersResource.de.resx` | changed |
| `src/Sections/Humans.Containers/ContainersResource.es.resx` | changed |
| `src/Sections/Humans.Containers/ContainersResource.fr.resx` | changed |
| `src/Sections/Humans.Containers/ContainersResource.it.resx` | changed |
| `src/Sections/Humans.Containers/ContainersResource.resx` | changed |
| `src/Sections/Humans.Containers/Contracts/ContainerAuthorizationTarget.cs` | changed |
| `src/Sections/Humans.Containers/Contracts/ContainerCardModel.cs` | changed |
| `src/Sections/Humans.Containers/Contracts/ContainerOperationRequirement.cs` | reviewed |
| `src/Sections/Humans.Containers/Contracts/ContainerViewModels.cs` | changed |
| `src/Sections/Humans.Containers/Contracts/IContainerService.cs` | changed |
| `src/Sections/Humans.Containers/Controllers/ContainerController.cs` | changed |
| `src/Sections/Humans.Containers/Data/Configurations/ContainerConfiguration.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Configurations/ContainerImageConfiguration.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Configurations/ContainerPlacementConfiguration.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/ContainersDbContext.cs` | changed |
| `src/Sections/Humans.Containers/Data/ContainersDbContextFactory.cs` | changed |
| `src/Sections/Humans.Containers/Data/IContainerRepository.cs` | changed |
| `src/Sections/Humans.Containers/Data/Migrations/20260715084311_BaselineContainers.Designer.cs` | generated |
| `src/Sections/Humans.Containers/Data/Migrations/20260715084311_BaselineContainers.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Migrations/20260820174009_ContainerImages.Designer.cs` | generated |
| `src/Sections/Humans.Containers/Data/Migrations/20260820174009_ContainerImages.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Migrations/ContainersDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Containers/Data/Repository.cs` | reviewed |
| `src/Sections/Humans.Containers/Docs/Containers.md` | changed |
| `src/Sections/Humans.Containers/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Containers/Docs/data-access.md` | changed |
| `src/Sections/Humans.Containers/Docs/debt.yml` | changed (new) |
| `src/Sections/Humans.Containers/Docs/health.md` | changed |
| `src/Sections/Humans.Containers/Domain/Container.cs` | reviewed |
| `src/Sections/Humans.Containers/Domain/ContainerImage.cs` | reviewed |
| `src/Sections/Humans.Containers/Domain/ContainerPlacement.cs` | reviewed |
| `src/Sections/Humans.Containers/Humans.Containers.csproj` | changed |
| `src/Sections/Humans.Containers/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Containers/Section.cs` | changed |
| `src/Sections/Humans.Containers/Services/AuditEntityTypes.cs` | changed |
| `src/Sections/Humans.Containers/Services/Service.cs` | changed |
| `src/Sections/Humans.Containers/Views/Container/Index.cshtml` | changed |
| `src/Sections/Humans.Containers/Views/Shared/_ContainerCardModals.cshtml` | changed |
| `src/Sections/Humans.Containers/Views/Shared/_ContainerCardRow.cshtml` | changed |
| `src/Sections/Humans.Containers/Views/Shared/_ContainerFormFields.cshtml` | changed |
| `src/Sections/Humans.Containers/Views/_ViewImports.cshtml` | changed |
| `tests/Humans.Containers.Tests/Authorization/ContainerAuthorizationHandlerTests.cs` | changed |
| `tests/Humans.Containers.Tests/ContainersArchitectureTests.cs` | changed |
| `tests/Humans.Containers.Tests/Humans.Containers.Tests.csproj` | changed |
| `tests/Humans.Containers.Tests/Services/ServiceImageTests.cs` | changed |
| `tests/Humans.Containers.Tests/Services/ServicePlacementTests.cs` | changed |

Callers changed outside the inventory, as the collapse (1–3) required:
`src/Sections/Humans.CityPlanning/Controllers/CityPlanningController.cs`,
`src/Sections/Humans.CityPlanning/Controllers/CityPlanningApiController.cs`,
`src/Sections/Humans.CityPlanning/Models/CityPlanningViewModels.cs`,
`src/Sections/Humans.CityPlanning/Views/CityPlanning/Containers.cshtml`.

## Threads

| Thread | How | Model | Findings |
|---|---|---|---|
| Inbox | main | main session | 0 |
| Conformance | main (detector script) | main session | 1 (report only) |
| Prose & surface | subagent `doctor-reader` | haiku | 6 |
| Freshness | subagent `doctor-reader` | opus, low | 12 |
| Tests | subagent `doctor-reader` | opus, low | 15 |
| History | subagent `doctor-reader` | opus, low | 20 |
| Comments | subagent `doctor-reader` | opus, low | 30 |
| Reviewer (gate on 1–3, 9) | subagent `doctor-reviewer` | fable, high | APPROVE |

All threads returned before the deadline; none were self-run.
