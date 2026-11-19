---
name: TeamResourceService lives in the Google section
description: ITeamResourceService and TeamResourceService live in the Humans.Google section project even though google_resources is a Team Resources sub-aggregate.
---

`ITeamResourceService` lives in the `Humans.Google/Contracts/` folder and its implementation `TeamResourceService` in `Humans.Google.Services`. The arch-test `RepositoryOwners` map records `IGoogleResourceRepository → "Google"` to match.

`google_resources` is still a Team Resources sub-aggregate (see `src/Sections/Humans.Teams/Docs/Teams.md`), but the table is heavily Google-Workspace-coupled — its repository, EF entity configuration, and the `ITeamResourceGoogleClient` / `IGoogleDrivePermissionsClient` connectors all live in Google.

**Why:** section labels follow code locality, not table aggregate ownership. Splitting "ownership section" (Teams) from "code-locality section" (Google) put the service and its repository in different sections on paper while they sat in one assembly, and every guardrail keyed on the mismatch reported a violation that was not there.

**How to apply:** When adding code that reads or writes `google_resources`, put it in the `Humans.Google` section project — `Humans.Google.Services` for a service, `Humans.Google.Data` for repository code, `Humans.Google.Contracts` only for surface another section must see (alongside `GoogleWorkspaceSyncService`; `DriveActivityMonitorService` is no longer an example — it moved to `Humans.Monitor.Services`). Do not relocate `TeamResourceService` into `Humans.Teams.Services` — the section is now Google and the docs in `src/Sections/Humans.Teams/Docs/Teams.md` and `src/Sections/Humans.Google/Docs/Google.md` reflect that.

**Related:** `memory/architecture/users-profiles-one-section.md` (sibling section-fold rule).
