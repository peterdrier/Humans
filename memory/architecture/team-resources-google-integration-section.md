---
name: TeamResourceService lives in the GoogleIntegration section
description: ITeamResourceService and TeamResourceService live under Humans.Application.{Interfaces,Services}.GoogleIntegration even though google_resources is a Team Resources sub-aggregate, so HUM0017 sees the IGoogleResourceRepository injection as intra-section.
---

`ITeamResourceService` and its implementation `TeamResourceService` live under `Humans.Application.Interfaces.GoogleIntegration` and `Humans.Application.Services.GoogleIntegration`. The `IGoogleResourceRepository` interface stays `[Section("GoogleIntegration")]` (matches its EF impl namespace), and the arch-test `RepositoryOwners` map records `IGoogleResourceRepository → "GoogleIntegration"` to match.

`google_resources` is still a Team Resources sub-aggregate (see `docs/sections/Teams.md`), but the table is heavily Google-Workspace-coupled — its repository, EF entity configuration, and the `ITeamResourceGoogleClient` / `IGoogleDrivePermissionsClient` connectors all live in GoogleIntegration. Section labels follow code locality so HUM0017 (and `ServiceBoundaryArchitectureTests`) treat the service ↔ repo edge as intra-section.

**Why:** Splitting "ownership section" (Teams) from "code-locality section" (GoogleIntegration) was creating false HUM0017 reports that the previous PR papered over with `#pragma warning disable HUM0017`. Suppressing an architecture analyzer to dodge a structural mismatch is forbidden (`memory/process/no-analyzer-suppressions.md`). The clean fix is one section label per service surface, applied consistently to both the analyzer view and the arch-test ownership map.

**How to apply:** When adding code that reads or writes `google_resources`, put it in `Humans.Application.{Interfaces,Services}.GoogleIntegration` (alongside `GoogleWorkspaceSyncService`, `DriveActivityMonitorService`, etc.). Do not relocate `TeamResourceService` back under `Services.Teams` — the section is now GoogleIntegration and the docs in `docs/sections/Teams.md` and `docs/sections/GoogleIntegration.md` reflect that.

**Related:** `memory/architecture/users-profiles-one-section.md` (sibling section-fold rule).
