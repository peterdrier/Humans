---
name: TeamResourceService lives in the GoogleIntegration section
description: ITeamResourceService and TeamResourceService live in the Humans.GoogleIntegration section project even though google_resources is a Team Resources sub-aggregate.
---

`ITeamResourceService` lives on the `Humans.GoogleIntegration.Contracts` leaf and its implementation `TeamResourceService` in `Humans.GoogleIntegration.Services`. The arch-test `RepositoryOwners` map records `IGoogleResourceRepository → "GoogleIntegration"` to match.

`google_resources` is still a Team Resources sub-aggregate (see `src/Sections/Humans.Teams/Docs/Teams.md`), but the table is heavily Google-Workspace-coupled — its repository, EF entity configuration, and the `ITeamResourceGoogleClient` / `IGoogleDrivePermissionsClient` connectors all live in GoogleIntegration.

**Why:** section labels follow code locality, not table aggregate ownership. Splitting "ownership section" (Teams) from "code-locality section" (GoogleIntegration) put the service and its repository in different sections on paper while they sat in one assembly, and every guardrail keyed on the mismatch reported a violation that was not there.

**How to apply:** When adding code that reads or writes `google_resources`, put it in the `Humans.GoogleIntegration` section project — `Humans.GoogleIntegration.Services` for a service, `Humans.GoogleIntegration.Data` for repository code, `Humans.GoogleIntegration.Contracts` only for surface another section must see (alongside `GoogleWorkspaceSyncService`; `DriveActivityMonitorService` is no longer an example — it moved to `Humans.Monitor.Services`). Do not relocate `TeamResourceService` into `Humans.Teams.Services` — the section is now GoogleIntegration and the docs in `src/Sections/Humans.Teams/Docs/Teams.md` and `src/Sections/Humans.GoogleIntegration/Docs/GoogleIntegration.md` reflect that.

**Related:** `memory/architecture/users-profiles-one-section.md` (sibling section-fold rule).
