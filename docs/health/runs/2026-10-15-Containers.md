# section-doctor — Containers — 2026-10-15

- Invocation: scheduled unattended daily run, no arguments (routine prompt: skip Phase 8)
- Anchor commit: `89c128e35` (origin/main at branch point); branch `section-doctor/2026-10-15T011642Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

Containers was already close to its target: no open issues in either repo, no open debt rows,
conformance detectors clean, resx parity clean. The drift was in the prose. `Containers.md` credited
GeoJSON validation to City Planning's API controller, claimed placement-image files get cleaned up,
listed only part of the API, and narrated its own history. In the code, the service had three
hand-copied best-effort file-delete blocks, a factory method that only wrapped the constructor, and a
sort that `ToDto` repeats. The behaviour walk turned up one off-section year mismatch in City Planning's
container map gate.

Independence check: pass — findings 3 and 10 came from the target shape (one file-drop helper, no pass-through surface), and 13 from the behaviour walk.

## Findings

1. `Containers.md` said City Planning's API controller enforces GeoJSON properties; `Service.SavePlacementAsync` does.
2. `Containers.md` claimed a scan window removes orphaned placement images; no placement-image file is deleted on container delete for any year.
3. Three hand-copied best-effort post-commit file deletions in `Service`, against the target's one-helper shape.
4. Content-type refusal of an image had no direct test.
5. A Map Admin placing while the phase is closed had no test.
6. `Containers.md` listed only part of the container API routes.
7. `Containers.md`'s ContainerMap 403 condition was muddled.
8. Narrating prose in `Containers.md` (issue provenance, retired plans, arguments past a decision).
9. Comment strikes across the section: misplaced security comment, design history, restating comments, an unbacked number.
10. `ContainerAuthorizationTarget.ForCamp` was a pass-through for the constructor.
11. `LoadImagesAsync` sorted images that `ToDto` sorts again.
12. `data-access.md` cross-section dependency wording.
13. City Planning's `ContainerMap/{year}` gates on the public year's phase while placement writes gate on the route year.
14. `CityPlanning.md` says the container phase gates lead CRUD; City Planning `health.md` cites a stale controller line.
15. `dependency-graph.md` Container node label and edges; `G5-SECTION-TEMPLATE.md` stale `ContainerMap_*` key claims.
16. `Containers.md` freshness triggers missed City Planning's `Containers.cshtml`.

## Debt verified

- Containers `Docs/debt.yml` has no open rows; nothing to verify.

## Worked

- 1, 2, 6, 7, 8, 12, 16 — `Containers.md` and `data-access.md` realigned with the code.
- 9 — comment strikes.
- 11 — redundant sort cut.
- 10 — `ForCamp` deleted; callers use the constructor (reviewer: approve).
- 3 — one `DeleteFilesAsync` helper serves update, delete, placement-image replacement and failed-upload cleanup (reviewer: reject, fixed, approve).
- 4, 5 — tests added.
- 13 — ledgered as CITY-6 in City Planning's `debt.yml`.
- 14 — ledgered as CITY-7 in City Planning's `debt.yml`.

## Skipped

- 15 — the dependency graph scopes itself to service-to-service edges, and Containers' service calls only Audit and Camps, which it shows; the handler's City Planning read sits outside that diagram. The G5 key claims are a hand-maintained count, which is never ledgered.
- Tests thread also noted no controller-level 403 tests and unasserted audit entity types on some writes; not ranked this run — the handler tests pin the 403 decision.
- Prose thread's breadcrumb `aria-label` literal is a repo-wide convention, not a section finding.
- Environment, 2026-10-15: `dotnet build Humans.slnx` fails on NuGet audit advisories NU1902/NU1903 for `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.Xml` in projects outside this section, so the full-solution test gate could not run; the section's own test project passes. No live render and no in-app issue read, as no database is available.

## Retro

The selector picked well: a small section with a fresh target, where the remaining work was prose drift plus a little shape. Nothing was blocked.

Wasted motion: two prose-gate refusals on doc wording, one of them a false positive where a sibling bullet's numeral was read as a count of the list beneath it.

Striking showed one thing assessment missed: the shared delete helper first got a comment that was true for only two of its callers, and the placement path lost its structured year. The reviewer caught both. A dedup has to keep each caller's log fields, not just its control flow.

The target diff added two shape statements, refusals as values and one way to drop a file, and moved the invariants onto `file:line` cites. The code now matches both statements.

## Needs Peter

None.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Containers/Authorization/ContainerAuthorizationHandler.cs` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.ca.resx` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.cs` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.de.resx` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.es.resx` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.fr.resx` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.it.resx` | reviewed |
| `src/Sections/Humans.Containers/ContainersResource.resx` | reviewed |
| `src/Sections/Humans.Containers/Contracts/ContainerAuthorizationTarget.cs` | changed |
| `src/Sections/Humans.Containers/Contracts/ContainerCardModel.cs` | reviewed |
| `src/Sections/Humans.Containers/Contracts/ContainerOperationRequirement.cs` | reviewed |
| `src/Sections/Humans.Containers/Contracts/ContainerViewModels.cs` | reviewed |
| `src/Sections/Humans.Containers/Contracts/IContainerService.cs` | changed |
| `src/Sections/Humans.Containers/Controllers/ContainerController.cs` | changed |
| `src/Sections/Humans.Containers/Data/Configurations/ContainerConfiguration.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Configurations/ContainerImageConfiguration.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Configurations/ContainerPlacementConfiguration.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/ContainersDbContext.cs` | changed |
| `src/Sections/Humans.Containers/Data/ContainersDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/IContainerRepository.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Migrations/20260715084311_BaselineContainers.Designer.cs` | generated |
| `src/Sections/Humans.Containers/Data/Migrations/20260715084311_BaselineContainers.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Migrations/20260820174009_ContainerImages.Designer.cs` | generated |
| `src/Sections/Humans.Containers/Data/Migrations/20260820174009_ContainerImages.cs` | reviewed |
| `src/Sections/Humans.Containers/Data/Migrations/ContainersDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Containers/Data/Repository.cs` | reviewed |
| `src/Sections/Humans.Containers/Docs/Containers.md` | changed |
| `src/Sections/Humans.Containers/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Containers/Docs/data-access.md` | changed |
| `src/Sections/Humans.Containers/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.Containers/Docs/health.md` | changed |
| `src/Sections/Humans.Containers/Domain/Container.cs` | reviewed |
| `src/Sections/Humans.Containers/Domain/ContainerImage.cs` | reviewed |
| `src/Sections/Humans.Containers/Domain/ContainerPlacement.cs` | reviewed |
| `src/Sections/Humans.Containers/Humans.Containers.csproj` | changed |
| `src/Sections/Humans.Containers/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Containers/Section.cs` | reviewed |
| `src/Sections/Humans.Containers/Services/AuditEntityTypes.cs` | changed |
| `src/Sections/Humans.Containers/Services/Service.cs` | changed |
| `src/Sections/Humans.Containers/Views/Container/Index.cshtml` | reviewed |
| `src/Sections/Humans.Containers/Views/Shared/_ContainerCardModals.cshtml` | reviewed |
| `src/Sections/Humans.Containers/Views/Shared/_ContainerCardRow.cshtml` | reviewed |
| `src/Sections/Humans.Containers/Views/Shared/_ContainerFormFields.cshtml` | reviewed |
| `src/Sections/Humans.Containers/Views/_ViewImports.cshtml` | changed |
| `tests/Humans.Containers.Tests/Authorization/ContainerAuthorizationHandlerTests.cs` | changed |
| `tests/Humans.Containers.Tests/ContainerValidationLocalizationTests.cs` | reviewed |
| `tests/Humans.Containers.Tests/ContainersArchitectureTests.cs` | changed |
| `tests/Humans.Containers.Tests/Humans.Containers.Tests.csproj` | reviewed |
| `tests/Humans.Containers.Tests/Services/ServiceImageTests.cs` | changed |
| `tests/Humans.Containers.Tests/Services/ServicePlacementTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus (main) | 3 |
| Behavior & bugs | main | opus (main) | 1 |
| Freshness | subagent (`doctor-reader`) | opus-low | 7 |
| Conformance | subagent (`general-purpose`) | haiku | 0 |
| Tests | subagent (`doctor-reader`) | opus-low | 2 |
| Prose & surface | subagent (`general-purpose`) | haiku | 0 |
| History | subagent (`doctor-reader`) | opus-low | 1 |
| Comments | subagent (`doctor-reader`) | opus-low | 1 |
| Inbox | subagent (`doctor-reader`) | opus-low | 2 |

