<!-- freshness:triggers
  src/Sections/Humans.Monitor/**
  tests/Humans.Monitor.Tests/**
-->

# Monitor — target shape

The shape this section is converging on, re-derived each `/section-doctor` run before any
scan is read.

## What the section does

Watches the managed Google Drive folders for permission changes nobody in Humans made.

Once an hour, and on demand from a button on the audit log page, it asks Google which
permission changes happened on those folders since it last looked. Whatever the system's own
service account did is expected and ignored; anything else is written to the audit log as an
anomaly naming the folder, the person and the change. It remembers when it last looked, and
only moves that memory forward after a complete look with real credentials, so a bad run is
re-covered rather than skipped. When it cannot reach Google at all, the hourly run is recorded
as failed and the person who pressed the button sees an error.

## The shapes

| Question shape | Entry points | Answer |
|---|---|---|
| *Did anyone outside the service account change permissions on our folders?* | `DriveActivityMonitorJob` (hourly), `POST /Monitor/CheckDriveActivity` | audit-log anomaly rows, plus a count for the caller |

One shape, two triggers. The contract surface is one method
(`IDriveActivityMonitorService.CheckForAnomalousActivityAsync`), consumed only from inside
this project.

## Structure

- `Contracts/IDriveActivityMonitorService.cs` — the one-method scan contract.
- `Services/DriveActivityMonitorService.cs` — the scan: walk the resource set, decide whether
  the marker advances, turn an activity event into a sentence. People-id resolution is its
  own nested class, `PersonNameResolver`, because its caches are per-run state.
- `Jobs/DriveActivityMonitorJob.cs` and `SectionJobs.cs` — the hourly trigger, its metric
  and its schedule.
- `Controllers/MonitorController.cs` — the manual trigger: dispatch, banner, redirect to the
  filtered audit log. No views.
- `Section.cs` — DI registration.

No `Data/`, no repository, no views, no resource set.

## Invariants

- A permission change initiated by the service account, matched by email or by
  `people/{client_id}`, is never an anomaly
  (`src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs:155`).
- The last-run marker advances only when no resource query failed and the connector is
  configured (`src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs:176`).
- Anomalies are audited on every run, whether or not the marker moved
  (`src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs:113`).
- When no resource could be queried, the scan throws after auditing what it found; a
  `DriveActivityResourceNotFoundException` counts as a successful query
  (`src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs:124`).
- The job records `failure` and rethrows on that throw, `success` otherwise
  (`src/Sections/Humans.Monitor/Jobs/DriveActivityMonitorJob.cs:45`).
- The manual action never surfaces an exception: it logs, sets an error banner and redirects
  (`src/Sections/Humans.Monitor/Controllers/MonitorController.cs:38`).
- An absent or unparseable marker falls back to a 24-hour lookback
  (`src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs:49`).
- A Google provider key claimed by two humans resolves to the raw `people/{id}`, never to
  either human (`src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs:450`).
- `POST /Monitor/CheckDriveActivity` requires `BoardOrAdmin`
  (`src/Sections/Humans.Monitor/Controllers/MonitorController.cs:21`).

## Seams

None.

## Deliberately not done

- **No repository or `DbContext`.** The section's one piece of state is a single Settings
  key.
- **No `I<Section>ServiceRead` split.** Nothing outside this project consumes the contract.
- **No resource set.** The banners are shown only to `BoardOrAdmin`, which
  [`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md) covers.
- **No retry around the connector.** A failed resource holds the marker back and the next
  hourly run re-covers the window; that is the retry.

## Load-bearing weirdness

- **Marker first, then audit.** `IAuditLogService` asks for audit after the business save it
  describes and swallows its own write failures, so writing the marker first loses nothing.
- **The stub-mode marker guard.** Not advancing the marker when the connector is
  unconfigured looks like a missing else-branch; it keeps historical changes visible once
  real credentials land in the same database.
- **`Humans.Monitor.csproj` references `Humans.GoogleIntegration` whole.** GoogleIntegration's
  contracts live in a `Contracts/` folder of that project, not a leaf, so the compiler cannot
  stop `IGoogleDriveActivityClient` moving out of it;
  `DriveActivityMonitorArchitectureTests.IGoogleDriveActivityClient_LivesOnGoogleIntegrationsLeaf`
  is what holds it there.
- **Monitor references GoogleIntegration and AuditLog, and no section references Monitor.**
  The scan is its own section because it is a distinct read model with its own job, not part
  of the audit log it writes to.
- **`DriveActivityMonitorJob` is `public`.** HUM0034 allows a section's public types under
  `Jobs/`; `Section.cs` and `SectionJobs.cs` name the type.
- **The scan reads Users through `IUserServiceRead.GetAllUserInfosAsync`**, the whole user
  set, lazily, once per run, and only when the Directory connector could not resolve a
  people-id.
- **The test project takes the ASP.NET Core framework reference** so CI can construct
  `MonitorController` and pin its catch path.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-02 | First doctoring: docs and comments still described the pre-carve-out world and a renamed Settings interface; people-id resolution moved into one nested resolver; three untested invariants pinned | peterdrier/Humans#1582 |
| section-doctor | 2026-10-16 | Monitor builds as a plain controller section now its views are gone; its docs stop contradicting its csproj and the code; a mid-stream outage is pinned | peterdrier/Humans#1988 |
