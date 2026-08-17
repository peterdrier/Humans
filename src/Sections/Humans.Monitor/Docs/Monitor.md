<!-- freshness:triggers
  src/Sections/Humans.Monitor/**
-->
<!-- freshness:flag-on-change
  Monitor's reference set is its whole reason to exist — review MonitorArchitectureTests.SectionReferencesOnlyBaseAndTheLeavesItConsumes when any ProjectReference is added.
-->

# Monitor — Section Invariants

Operator-facing monitoring of the Google Workspace estate: detect permission changes nobody
asked for, and show the Google-sync audit trail for one resource or one human.

## Why the section exists

Monitor was carved out of **AuditLog**, not out of GoogleIntegration.

`AuditLogController` had three actions that were monitoring rather than audit browsing, and two
of them injected GoogleIntegration services directly. AuditLog is a **horizontal**;
[`peters-hard-rules.md`](../../../../docs/architecture/peters-hard-rules.md) forbids a
horizontal from referencing a vertical section. That reach was invisible while GoogleIntegration
still lived in `Humans.Application` — both ends were Base — and became an assembly-level
violation the moment GoogleIntegration went to G5 (nobodies-collective/Humans#866).

`DriveActivityMonitorService` turned out to be the same shape one level down: it injects five
sections' services (`IGoogleDriveActivityClient`, `ITeamResourceService`, `ISystemSettingsService`,
`IUserServiceRead`, `IAuditLogService`) and calls **no repository** — a cross-section
orchestrator by the hard rules' own definition, sitting in `Services/GoogleIntegration/` on code
locality alone.

**Monitor may reference both GoogleIntegration and AuditLog because Monitor is not a
horizontal.** It is a leaf consumer: it sits above both and nothing sits above it.

## Concepts

- **Anomalous permission change** — a permission grant or revocation on a managed Drive folder
  made by someone other than the service account. Detected by polling the Google Drive Activity
  API and comparing actors; each one is written to the audit log as
  `AuditAction.AnomalousPermissionDetected`.
- **Time-window dedup** — each scan processes only events since the last successful run,
  persisted through `ISystemSettingsService` under the `DriveActivityMonitorJob` key. First run,
  or a missing marker, falls back to 24 hours.
- **Google sync audit trail** — the audit entries carrying `ResourceId` / `SyncSource` /
  `Success`, projected for one resource or one human. The read path is Base's
  `IAuditViewerService`, which resolves actor and subject display names.

## Data Model

**Monitor owns no tables.** No `DbContext`, no repository, no migrations, no G4 gate. It reads
Google through GoogleIntegration's connector abstraction, reads and writes audit through
`IAuditLogService` / `IAuditViewerService`, and stores its one piece of state (the last-run
timestamp) in SystemSettings. `MonitorArchitectureTests.SectionOwnsNoDbContext` pins this.

## Actors / Roles

| Route | Policy |
|---|---|
| `POST /Monitor/CheckDriveActivity` | `BoardOrAdmin` |
| `GET /Monitor/Resource/{id}` | `BoardOrAdmin` |
| `GET /Monitor/Human/{id}` | `HumanAdminBoardOrAdmin` |

Policies stay in Shell's `AuthorizationPolicyExtensions` (G5 template step 6's asymmetry: DI
registration moves into the section, policy registration does not).

## Invariants

- **Monitor's reference set is the section's justification and is asserted, not documented.**
  `MonitorArchitectureTests.SectionReferencesOnlyBaseAndTheLeavesItConsumes` fixes it at
  `Humans.AuditLog.Contracts` + `Humans.SystemSettings.Contracts` (GoogleIntegration is still
  Base-resident and arrives via `Humans.Application`). Every name added there is a section
  Monitor now couples to.
- **Nothing depends on Monitor except Shell naming the job.** Its whole outward surface is
  `IDriveActivityMonitorService` in `Contracts/` — one method, returning `int` — consumed by
  `DriveActivityMonitorJob` beside it, home since the G5 jobs move
  (nobodies-collective/Humans#866); the `Humans.Monitor.Contracts` leaf folded back in once
  that job left Base. It is `public` there because Shell names
  the concrete type in `AddScoped` and in the recurring roll-call — there is still no
  `ISection`-style discovery seam for recurring jobs (template step 6b) — and HUM0034 allows a
  section's public types only under `Contracts/`.
- **The scan is best-effort and never throws to its caller.** `CheckDriveActivity` catches,
  logs at Error, and shows the operator an error banner; the recurring job records a failed run.
- **No resource set.** One admin-only English page.
  `MonitorArchitectureTests.SectionTypesTakeNoStringLocalizer` pins it, so the day someone adds
  copy the build says "carve a resource set first".

## Negative access rules

- A volunteer reaching any `/Monitor/*` route gets a **302 to `AccessDeniedPath`**, not a 403 —
  cookie authentication redirects an authenticated-but-unauthorized request app-wide.
  `MonitorPageRenderTests.Monitor_is_closed_to_a_non_privileged_human` asserts the redirect.
- `GET /Monitor/Resource/{unknown}` is a **404**, not a 500. The distinction is the test: a 500
  means a dependency failed to resolve out of the section's DI graph.

## Triggers

- `DriveActivityMonitorJob` (recurring, Hangfire) → `CheckForAnomalousActivityAsync`.
- `POST /Monitor/CheckDriveActivity` → the same method, on demand, from the audit log page's
  toolbar button. The button still lives on `/AuditLog`; only the form's target moved.

## Cross-section dependencies

| Direction | Section | Through |
|---|---|---|
| out | GoogleIntegration | `IGoogleDriveActivityClient`, `ITeamResourceService` |
| out | AuditLog | `IAuditLogService` (write), `IAuditViewerService` (read) |
| out | SystemSettings | `ISystemSettingsService` (last-run marker) |
| out | Users | `IUserServiceRead` (resolve Google actors to humans) |
| in | — | none; Shell names `DriveActivityMonitorJob`, which is in this project |

## Architecture status

At G5: own project (`src/Sections/Humans.Monitor`); its former `.Contracts` leaf folded into
the project's `Contracts/` folder. Table-less, so no G4 gate applies. `Section.Register` has one line.
