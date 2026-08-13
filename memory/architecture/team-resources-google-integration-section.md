---
name: TeamResourceService lives in the GoogleIntegration section
description: ITeamResourceService and TeamResourceService live in the Humans.GoogleIntegration section project even though google_resources is a Team Resources sub-aggregate, so HUM0017 sees the IGoogleResourceRepository injection as intra-section.
---

`ITeamResourceService` lives on the `Humans.GoogleIntegration.Contracts` leaf and its implementation `TeamResourceService` in `Humans.GoogleIntegration.Services` (both moved there by the section's G5 project split, nobodies-collective/Humans#866). The `IGoogleResourceRepository` interface stays `[Section("GoogleIntegration")]` (matches its EF impl namespace), and the arch-test `RepositoryOwners` map records `IGoogleResourceRepository → "GoogleIntegration"` to match.

`google_resources` is still a Team Resources sub-aggregate (see `src/Sections/Humans.Teams/Docs/Teams.md`), but the table is heavily Google-Workspace-coupled — its repository, EF entity configuration, and the `ITeamResourceGoogleClient` / `IGoogleDrivePermissionsClient` connectors all live in GoogleIntegration. Section labels follow code locality so HUM0017 (and `ServiceBoundaryArchitectureTests`) treat the service ↔ repo edge as intra-section.

**Why:** Splitting "ownership section" (Teams) from "code-locality section" (GoogleIntegration) was creating false HUM0017 reports that the previous PR papered over with `#pragma warning disable HUM0017`. Suppressing an architecture analyzer to dodge a structural mismatch is forbidden (`memory/process/no-analyzer-suppressions.md`). The clean fix is one section label per service surface, applied consistently to both the analyzer view and the arch-test ownership map.

**How to apply:** When adding code that reads or writes `google_resources`, put it in the `Humans.GoogleIntegration` section project — `Humans.GoogleIntegration.Services` for a service, `Humans.GoogleIntegration.Data` for repository code, `Humans.GoogleIntegration.Contracts` only for surface another section must see (alongside `GoogleWorkspaceSyncService`; `DriveActivityMonitorService` is no longer an example — it moved to `Humans.Monitor.Services`). Do not relocate `TeamResourceService` into `Humans.Teams.Services` — the section is now GoogleIntegration and the docs in `src/Sections/Humans.Teams/Docs/Teams.md` and `src/Sections/Humans.GoogleIntegration/Docs/GoogleIntegration.md` reflect that.

**Related:** `memory/architecture/users-profiles-one-section.md` (sibling section-fold rule).
