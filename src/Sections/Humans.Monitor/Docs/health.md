<!-- freshness:triggers
  src/Sections/Humans.Monitor/**
  tests/Humans.Monitor.Tests/**
-->

# Monitor — target shape

The shape this section is converging on, re-derived each `/section-doctor` run before any
scan is read. Not a description of today's layout where the two differ.

## What the section does

Watches the Google Workspace estate for permission changes nobody asked for, and shows an
operator what the Google sync did to one folder or one human.

Once an hour — and on demand from a button on the audit log page — it asks Google which
permission changes happened on the managed Drive folders since it last looked. Anything the
system's own service account did is expected and ignored; anything else is written to the
audit log as an anomaly, naming the folder, the person and the change. It remembers when it
last looked, and deliberately forgets that only when the look was incomplete, so a bad run
is re-covered rather than skipped.

Separately it renders two read-only pages that show the sync history of one Drive resource
or one human. It does not read that history itself.

## The shapes

Two question-shapes; the entry points that ask them are the table's left-hand column.

| Question shape | Entry points | Answer |
|---|---|---|
| *Did anyone outside the service account change permissions on our folders?* | `DriveActivityMonitorJob` (hourly), `POST /Monitor/CheckDriveActivity` | audit-log anomaly rows + a count |
| *What did the Google sync do to this thing?* | `GET /Monitor/Resource/{id}`, `GET /Monitor/Human/{id}` | a page hosting `<vc:google-sync-log>` |

The second shape is two routes because the predicate, the policy and the back-link differ;
only the page chrome is shared, and it already is — one view, one view model.

The contract surface is one method (`IDriveActivityMonitorService.CheckForAnomalousActivityAsync`),
consumed only from inside this project.

## Structure

- `Contracts/IDriveActivityMonitorService.cs` — the one-method scan contract.
- `Services/DriveActivityMonitorService.cs` — the scan. Three separable jobs live here: run
  the scan over the resource set, decide whether the marker advances, and turn a Drive
  activity event into a sentence. The third is pure string work over the connector's DTOs
  and depends on nothing but the people-id resolver.
- `Jobs/DriveActivityMonitorJob.cs` + `SectionJobs.cs` — the hourly trigger and its schedule.
- `Controllers/MonitorController.cs` — three actions, no logic: one dispatches the scan and
  redirects, two resolve an id and render `SyncAudit`.
- `Models/MonitorViewModels.cs`, `Views/Monitor/SyncAudit.cshtml` — the page chrome around
  `<vc:google-sync-log>`.
- `Section.cs` — two DI lines.
- `Docs/` — this file, `Monitor.md`, `authorization.md`, `data-access.md`.

No `Data/`, no repository, no resource set.

## Invariants

- A permission change initiated by the service account — matched by email **or** by
  `people/{client_id}` — is never an anomaly. Everything else on a managed Drive folder is.
- The last-run marker advances only when every resource was queried without error **and**
  the connector is really configured. Stub mode never advances it.
- Anomalies are audited even on a run that does not advance the marker.
- When every resource fails to query, the scan throws, so Hangfire records a failed run
  rather than a hollow success. A `DriveActivityResourceNotFoundException` is not a failure —
  the connector answered.
- The marker is persisted before the audit entries are written.
- An unparseable or absent stored marker falls back to a 24-hour lookback, never to zero.
- A `people/{id}` actor resolves cache → Directory connector → Users read-model → raw id.
  A Google provider key claimed by two humans resolves to the raw id, never to either human.
- `POST /Monitor/CheckDriveActivity` never surfaces an exception to the operator; it shows
  an error banner and redirects.
- `GET /Monitor/Resource/{unknown}` and `GET /Monitor/Human/{unknown}` are 404, not 500.
- A human without the route's policy gets a 302 to `AccessDeniedPath`, not a 403.
- Monitor owns no tables and calls no repository. Its only state is one Settings key.
- Monitor ships no resource set: one admin-only English page.

## Seams

None. Nothing here is specified-but-unbuilt.

## Deliberately not done

- **No repository or `DbContext`.** The section's one piece of state is a single Settings
  key; a table for it would buy nothing.
- **No `I<Section>ServiceRead` split.** Nothing outside this project consumes the contract,
  so there is no cross-section read to narrow.
- **No resource set / localization — open, not settled.** The one page is operator-only
  English (`BoardOrAdmin` / `HumanAdminBoardOrAdmin`). It is *not* covered by
  [`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md),
  which enumerates `/Admin/*`, `/TeamAdmin/*` and `/Shifts/Dashboard` — and these routes are
  `/Monitor/*`. Whether the exemption should reach operator pages off those paths (as
  `/Shifts/Dashboard` already does) or the section should gain a resx set is Peter's, not this
  document's: recorded in `Docs/debt.yml` rather than blessed here.
- **No retry or backoff around the connector.** A failed resource simply holds the marker
  back and the next hourly run re-covers the window — that *is* the retry.
- **No collapsing `Resource` and `Human` into one route.** They differ in policy and in
  predicate; a single action taking an optional discriminator would hide an authorization
  difference behind a query string.

## Load-bearing weirdness

- **`<vc:google-sync-log>` needs two things, and neither fails loudly.** The
  `ProjectReference` to `Humans.GoogleIntegration` *and* the `@addTagHelper *,
  Humans.GoogleIntegration` line in `Views/_ViewImports.cshtml`. Drop either and the element
  ships as inert literal markup with a green build.
- **Monitor may reference GoogleIntegration *and* AuditLog** because it is a leaf consumer,
  not a horizontal. That is the whole reason the section was carved out of AuditLog.
- **The stub-mode marker guard.** Not advancing the marker when the connector is
  unconfigured looks like a missing else-branch; it is what keeps historical changes
  visible once real credentials land in the same database.
- **`DriveActivityMonitorJob` is `public`.** HUM0034 allows a section's public types under
  `Contracts/` and `Jobs/`; the type is named by `Section.cs` and `SectionJobs.cs`.
- **The scan reads Users through `IUserServiceRead.GetAllUserInfosAsync`** — the whole user
  set, lazily, once per run, and only when the Directory connector could not resolve a
  people-id. In-memory over the whole set is the house style at this scale.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-02 | First doctoring: docs and comments still described the pre-carve-out world and a renamed Settings interface; the scan's people-id resolution lived in five fields and three methods and is now one nested resolver; three untested invariants pinned | peterdrier/Humans#1582 |
