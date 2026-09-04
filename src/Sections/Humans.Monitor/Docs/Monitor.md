<!-- freshness:triggers
  src/Sections/Humans.Monitor/**
  tests/Humans.Integration.Tests/Controllers/MonitorPageRenderTests.cs
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
-->
<!-- freshness:flag-on-change
  Monitor's reference set is its whole reason to exist — review the reference list in Invariants below when any ProjectReference is added.
-->

# Monitor — Section Invariants

Operator-facing monitoring of the Google Workspace estate: detect permission changes nobody
asked for, and show the Google-sync audit trail for one resource or one human.

## Why the section exists

**Monitor may reference both GoogleIntegration and AuditLog because Monitor is not a
horizontal.** It is a leaf consumer: it sits above both and nothing sits above it. AuditLog is
a horizontal, and [`peters-hard-rules.md`](../../../../docs/architecture/peters-hard-rules.md)
forbids a horizontal from referencing a vertical section — which is why these three actions and
`DriveActivityMonitorService` live here and not there (nobodies-collective/Humans#866).

`DriveActivityMonitorService` injects four sections' services — GoogleIntegration's
`IGoogleDriveActivityClient` and `ITeamResourceService`, plus `ISettingsService`,
`IUserServiceRead` and `IAuditLogService` — and calls **no repository**.

## Concepts

- **Anomalous permission change** — a permission grant or revocation on a managed Drive folder
  made by someone other than the service account. Detected by polling the Google Drive Activity
  API and comparing actors; each one is written to the audit log as
  `AuditAction.AnomalousPermissionDetected`.
- **Time-window dedup** — each scan processes only events since the last successful run,
  persisted through `ISettingsService` under the `DriveActivityMonitor:LastRunAt` key. First run,
  or a missing marker, falls back to 24 hours.
- **Google sync log** — GoogleIntegration's `google_sync_log` rows, shown for one resource or one
  human. Monitor does not read them: `SyncAudit.cshtml` emits `<vc:google-sync-log>` with the
  predicate and the GoogleIntegration section owns the read and the render
  (nobodies-collective/Humans#1083).

## Data Model

**Monitor owns no tables.** No `DbContext`, no repository, no migrations, no G4 gate. It reads
Google through GoogleIntegration's connector abstraction, writes audit through
`IAuditLogService`, renders the sync log through `<vc:google-sync-log>`, and stores its one
piece of state — the last-run timestamp — through the Settings section's `ISettingsService`.
Documentation, not a pinned assertion
([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

## Actors / Roles

| Route | Policy |
|---|---|
| `POST /Monitor/CheckDriveActivity` | `BoardOrAdmin` |
| `GET /Monitor/Resource/{id}` | `BoardOrAdmin` |
| `GET /Monitor/Human/{id}` | `HumanAdminBoardOrAdmin` |

Policies stay in Shell's `AuthorizationPolicyExtensions` (G5 template step 6's asymmetry: DI
registration moves into the section, policy registration does not).

## Invariants

- **Monitor's reference set is the section's justification.** It is
  `Humans.AuditLog.Contracts` + `Humans.Settings.Contracts` + `Humans.GoogleIntegration.Contracts` +
  `Humans.Users.Contracts` (plus `Humans.GoogleIntegration` itself, so `SyncAudit.cshtml`'s
  `<vc:google-sync-log>` tag helper binds). Every name added there is a section
  Monitor now couples to — documentation, not a pinned assertion
  ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).
- **No section depends on Monitor.** Its whole outward surface is `IDriveActivityMonitorService`
  in `Contracts/` — one method, returning `int` — and its only consumer is
  `DriveActivityMonitorJob` in `Jobs/`, inside this project. The Shell's
  `ProjectReference` is the exception and is required: `Humans.Web` references every section
  so the dependency context can discover this one's `ISection`, controllers and recurring job. The job is `public` because
  `Section.cs` and `SectionJobs.cs` name the concrete type; HUM0034 allows a section's public
  types under `Contracts/` and, for Hangfire jobs, under `Jobs/`.
- **The operator never sees an exception; the job does.** `CheckDriveActivity` catches, logs at
  Error and shows an error banner. The scan itself throws when *every* resource failed to
  query, so the recurring job records a failed run instead of a hollow success.
- **No resource set.** One admin-only English page — documentation, not a pinned
  assertion ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

## Negative access rules

- A volunteer reaching any `/Monitor/*` route gets a **302 to `AccessDeniedPath`**, not a 403 —
  cookie authentication redirects an authenticated-but-unauthorized request app-wide.
  `MonitorPageRenderTests.Monitor_is_closed_to_a_non_privileged_human` asserts the redirect.
- `GET /Monitor/Resource/{unknown}` is a **404**, not a 500. The distinction is the test: a 500
  means a dependency failed to resolve out of the section's DI graph.

Both live in `tests/Humans.Integration.Tests`, which CI filters out
([`integration-tests-are-not-ci-tests`](../../../../memory/process/integration-tests-are-not-ci-tests.md)) —
they are local-only assertions, not branch gates.

## Triggers

- `DriveActivityMonitorJob` (recurring, Hangfire) → `CheckForAnomalousActivityAsync`.
- `POST /Monitor/CheckDriveActivity` → the same method, on demand, from the toolbar button on
  `/AuditLog`.

## Cross-section dependencies

| Direction | Section | Through |
|---|---|---|
| out | GoogleIntegration | `IGoogleDriveActivityClient`, `ITeamResourceService`, `<vc:google-sync-log>` (render) |
| out | AuditLog | `IAuditLogService` (write) |
| out | Settings | `ISettingsService` (last-run marker) |
| out | Users | `IUserServiceRead` (resolve Google actors to humans) |
| in | — | none |

## Architecture status

Table-less, so no G4 gate applies.
