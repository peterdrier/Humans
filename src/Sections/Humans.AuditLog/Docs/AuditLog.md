<!-- freshness:triggers
  src/Sections/Humans.AuditLog/**
  src/Sections/Humans.AuditLog/Contracts/**
-->
<!-- freshness:flag-on-change
  Audit log append-only invariant, audit action definitions and registry, and self-persisting semantics — review when AuditLog service/repo/entity changes.
-->

# Audit Log — Section Invariants

Append-only system audit trail: who did what, when, to which entity. Used by every section that performs a privileged or irreversible action. Enforced append-only per design-rules §12.

- **For:** the Board can see what people, admins and automation did to members' records and the association's data, after the fact, without trusting anyone's memory.
- **Good looks like:** every privileged or automated change leaves one row, written after the change saves; nothing can edit or delete a row; any page can show the history of the thing it shows without reading audit itself; every shown entry names people, never ids.
- **Deliberate choices:** best-effort writes (an audit hiccup never fails the business step); no cache; database-side filtering because the table never stops growing; no retention or pruning, under GDPR Art. 30 and 17(3)(b).
- **Out of scope:** field-level change history (Budget keeps its own), Google sync history (GoogleIntegration's own log), deciding anything from audit rows.

## Concepts

- An **Audit Log Entry** is an append-only record of a single user-initiated or job-initiated action. Captures actor, action, entity type + id, free-text description, and timestamp. The retired Google-sync columns (see below) are kept for historical rows only.
- An **audit action** is an `AuditActionDefinition` (`Humans.AuditLog.Contracts`): the stored `Name`, the English `Verb` and `SelfVerb` for the narrative line, `DescriptionIsTail`, `Alert` (highlighted rows, counted on the admin page) and `QuickFilter` (a button label on the admin page). The section that writes an action declares it as a `public static readonly` field on its `<Section>AuditActions : AuditActionContributor` class and registers that class as an `IAuditActionContributor`; `AuditActionRegistry` collects every contributor at startup, fails the boot on a duplicate (ignoring case, as filters match) or over-long name, and resolves a stored name back to its definition for rendering (a name nobody declares any more renders as a bare definition). The name is a contract — grep it to find the single writer.
- `SurveyResponsesExported` records the requesting human, survey id and response row count for available Surveys response exports.
- **Self-persisting audit** (design-rules §7a): `IAuditLogService.LogAsync` saves each entry immediately via `IAuditLogRepository.AddAsync`, which uses `IDbContextFactory<AuditLogDbContext>` to open a fresh per-call context and `SaveChangesAsync`. Callers do not need to `SaveChanges` to flush audit, and must not expect audit to roll back if a later business step fails.
- **Best-effort** — audit save failures are logged at Error and swallowed inside `AuditLogService.PersistAsync`. An audit hiccup never fails the business operation that called it.

## Data Model

### AuditLogEntry

Append-only per design-rules §12. Enforced by the Postgres triggers `prevent_audit_log_update` / `prevent_audit_log_delete` (both calling `prevent_audit_log_modification()`, which raises an exception on any UPDATE or DELETE against `audit_log`). The section's baseline migration, `Data/Migrations/20260810193154_BaselineAuditLog.cs`, creates them.

**Table:** `audit_log` (DbSet `AuditLogEntries`, `AuditLogDbContext`, history `__EFMigrationsHistory_AuditLog`)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Action | string (100) | An `AuditActionDefinition.Name` |
| EntityType | string (100) | Type of the primary affected entity (e.g. `"User"`, `"Team"`, `"GoogleResource"`) |
| EntityId | Guid | Id of the primary affected entity (non-nullable) |
| Description | text | Complete human-readable description of what happened |
| OccurredAt | Instant | When the action occurred — callers stamp via `IClock` |
| ActorUserId | Guid? | Bare cross-section Guid column — no FK constraint, no nav. Nullable because system jobs write audit with no actor. |
| RelatedEntityId | Guid? | Id of a secondary related entity (e.g. UserId when EntityType=Team) |
| RelatedEntityType | string? (100) | Type of the secondary related entity |

**Retired columns.** `ResourceId`, `Success`, `ErrorMessage`, `Role`, `SyncSource` and `UserEmail` were the Google-sync wing. GoogleIntegration's own `google_sync_log` replaced them in nobodies-collective/Humans#1083, and nothing reads or writes them any more: they are off `AuditLogEntrySnapshot` and `AuditEvent`, and the entity keeps them only so the historical rows stay readable until the data move retires them. `ILegacyGoogleSyncAuditReader` (`Contracts/` folder, implemented by `AuditLogService`) is the one read left over them — it feeds GoogleIntegration's `/Google/Admin/SyncHistoryMigration` screen, which copies the history into `google_sync_log` and changes nothing here. The column drop, and this reader, are their own PR under `no-drops-until-prod-verified`.

**Indexes:** `(EntityType, EntityId)`, `(RelatedEntityType, RelatedEntityId)`, `OccurredAt`, `Action`, `ResourceId`.

Note: `BudgetAuditLog` is a separate per-section append-only log owned by Budget — it is **not** an audit action and does not write to `audit_log`.

## Routing

All Audit Log routes are owned by `AuditLogController` (`[Route("AuditLog")]`).

| Route | Action | Auth policy |
|-------|--------|------------|
| `GET /AuditLog` | `AuditLogController.Index` | `BoardOrAdmin` |

Drive-activity checks belong to [Monitor](../../Humans.Monitor/Docs/Monitor.md).
Per-resource and per-human sync audits belong to
[GoogleIntegration](../../Humans.GoogleIntegration/Docs/GoogleIntegration.md):

| Route | Now |
|---|---|
| `POST /Monitor/CheckDriveActivity` | `MonitorController.CheckDriveActivity`, `BoardOrAdmin` |
| `GET /Google/Resource/{id}` | `GoogleController.Resource`, `BoardOrAdmin` |
| `GET /Google/Human/{id}` | `GoogleController.Human`, `HumanAdminBoardOrAdmin` |

`AuditLogController` injects the internal `AuditViewerService` — no controller touches `IAuditLogService` or any repository directly. AuditLog owns no Admin routes and contributes no admin dashboard card; its only `/Admin` presence is the "Audit events" tile.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Write audit entries via `IAuditLogService.LogAsync(...)` (human or job overload). No authorization check at the log site — the caller has already authorized the underlying action |
| Board, Admin | View the system audit log via `GET /AuditLog` (`[Authorize(Policy = PolicyNames.BoardOrAdmin)]`), with filter + pagination via the internal `AuditViewerService.GetPageAsync` |
| Any authenticated viewer of an entity page | See per-entity audit history rendered through the shared `AuditLogViewComponent` (e.g. on Profile, Team, Calendar, Store order) — entries are scoped by the `<vc:audit-log>` predicate attributes and inherit the host page's authorization |

No one reads audit entries anonymously. The `/AuditLog` dashboard is gated to BoardOrAdmin; per-entity audit history is gated by the host page's policy.

## Invariants

- Audit-list paging computes offsets without integer overflow; pages beyond the available entries stay empty and counts remain unchanged.

- The Audit Log GET forwards request cancellation through the viewer to repository and name reads; an abandoned page does not complete its data load.

- Audit entries are append-only. `IAuditLogRepository` exposes `AddAsync` and `GetXxxAsync` — **no** `UpdateAsync`, **no** `DeleteAsync`, **no** `RemoveAsync`.
- The `audit_log` table itself rejects UPDATE and DELETE at the database layer via the `prevent_audit_log_update` and `prevent_audit_log_delete` Postgres triggers (created by `Data/Migrations/20260810193154_BaselineAuditLog.cs`). No application path can mutate or delete an existing row.
- `LogAsync` is self-persisting — each call routes through `AuditLogRepository.AddAsync`, which opens a fresh `DbContext` via `IDbContextFactory<AuditLogDbContext>`, adds the entry, and calls `SaveChangesAsync`. Callers do not flush audit.
- Descriptions preserve complete action details and job prefixes without a separate length cap. Valid 4000-character Workgroup notes still fit when their audit descriptions include action and document names.
- Audit is called **after** the business save, never before (design-rules §7a). A business rollback never leaves a ghost audit row because audit hasn't written yet.
- Audit commits separately from the business change. The rare failure mode is "business saved, audit did not" — logged loudly, detectable by reconciling row counts, and strictly better than "audit silently vanishes".
- Audit save failures are swallowed after a log at Error inside `AuditLogService.PersistAsync`. The audit `LogAsync` overloads do not throw back to the caller.
- `ActorUserId` is nullable — system jobs (Hangfire recurring jobs) write audit entries with no actor; the job-overload `LogAsync(..., string jobName, ...)` prepends the job name to the description.
- `AuditLogService` implements `IUserDataContributor`, exposing every entry where the user is actor, primary entity, or related entity to the GDPR export orchestrator.
- Per-user reads chain-follow merge tombstones via `UserInfo.AllUserIds` of the record `IUserService.GetUserInfoAsync(userId)` returned (the survivor's own id plus `MergedUserIds`; never the requested id) so audit entries written under a now-merged source id surface for the fold target, and a read through a merged-away id shows the survivor's rows too. Applies to `GetByUserAsync`, the per-entity history filter when entity is a User, and `ContributeForUserAsync` (GDPR). Audit entries are append-only (§12) and stay attributed to the source User row by design, `AnonymizeForMergeAsync` does NOT rewrite `ActorUserId` / `EntityId` / `RelatedEntityId` columns.

### `<vc:audit-log>` — the render path for other sections

A section that wants to *show* audit history emits `<vc:audit-log>` with a predicate; it never reads audit itself. The component owns the read (`IAuditViewerService`) and the render; it propagates a cancelled host request instead of logging it as a failed empty history.

- **Predicates** (first match wins): `entity-ids` (several ids of one `entity-type`, one query each, merged newest-first, capped at `limit`); otherwise `entity-type` / `entity-id` / `user-id` / `actions`. `since` drops anything older, applied after the read.
- **Layouts** (`layout`): `line` (default) is the narrative one-line-per-entry list; `table` is When · Actor · Action · Subject · Description · Target, subsettable via `columns`. The Google sync grid is GoogleIntegration's own `<vc:google-sync-log>`, not a layout here.
- **Chrome:** `title`, `empty-text`, `column-labels` and `show-card`. A localized host page supplies the chrome; AuditLogResource supplies entry labels, action badges and narrative verbs in all six cultures, keyed by action name, and a definition without entries renders its English verb and its name split into words. `column-labels` is **keyed, not positional** (`when:Fecha,actor:Remitente`): an unsupplied or unknown key falls back to the English column name, and reordering `columns` can never silently mis-header the table.
- Every consuming assembly needs `@addTagHelper *, Humans.AuditLog` in its `_ViewImports.cshtml`. Missing it is silent: the element ships as inert literal markup with a green build and no runtime error. Pinned by `AuditLogPageRenderTests` (`Humans.Integration.Tests`, nightly, not CI).

## Negative Access Rules

- Sections **cannot** read audit to render it. Reading `IAuditViewerService` from another section's service, controller or view model to build a table is the anti-pattern `<vc:audit-log>` exists to remove.
- Callers **cannot** `UpdateAsync`, `DeleteAsync`, or `RemoveAsync` an audit entry. The repository exposes no such methods, and the database triggers reject the operation even if a caller went around the repository.
- Services **cannot** call `IAuditLogService.LogAsync` inside an outer `DbContext` transaction expecting audit to roll back with it — audit uses its own context via `IDbContextFactory`.
- Services **cannot** bypass `IAuditLogService` and write `audit_log` directly. `AuditLogRepository` is the only file that touches `DbContext.AuditLogEntries`.
- The log **cannot** be pruned by production admins. There is no retention/cleanup job — entries persist indefinitely.
- Controllers **cannot** read `audit_log` directly. The `/AuditLog` page goes through the internal `AuditViewerService.GetPageAsync`, and per-entity / per-user views go through `IAuditViewerService` — the viewer service composes the page (entries + actor/subject/team display batching, with display names resolved through Base's `IEntityNameContributor` fan-out) inside the Audit Log section. `IAuditLogService` is the write side; the read+render path is `IAuditViewerService`.

## Triggers

- **On any privileged business write:** the owning section's service calls `IAuditLogService.LogAsync(action, entityType, entityId, description, actorUserId, ...)` after its business `SaveChangesAsync` returns successfully.
- **On a background job action:** the job calls the job overload `IAuditLogService.LogAsync(action, entityType, entityId, description, jobName, ...)` — `ActorUserId` is recorded as null and the job name is prepended to the description.
- **No cleanup trigger:** there is no retention or pruning job; the database triggers reject DELETE in every case.

## Cross-Section Dependencies

Nearly every other section **writes** into this section via `IAuditLogService`. This section depends on almost nothing:

- **No section (display names):** `AuditViewerService` injects `IEnumerable<IEntityNameContributor>` from `Humans.Base` and asks every contributor to name the Guids on a page — actor, subject and target-team ids in one pass. Teams and Users implement it on their existing caching services; ids nobody owns come back unnamed. The sections point at the Base contract; AuditLog names none of them.
- **GDPR (`IUserDataContributor`):** `AuditLogService` contributes per-user audit slices to the GDPR export orchestrator via `ContributeForUserAsync`. `EraseForUserAsync` is a no-op — the whole `AuditLog` category is retained under its `ErasureDeclaration`: an append-only record of processing activity (Art. 30) and the evidence trail for the erasure itself (Art. 17(3)(b), Art. 5(1)(f)), including any identifier a description quotes from when the action happened.
- **Users/Identity:** `IUserService.GetUserInfoAsync`, the returned `UserInfo.AllUserIds` chain-follows merge tombstones on every per-user audit read so source-attributed entries surface for the fold target. The read also resolves a tombstone id forward to its survivor, so a per-user audit read keyed on a merged-away id returns the survivor's history.

No other cross-section writes from this section outward. Audit is a sink.

## Architecture

**Owning services:** `AuditLogService` (write + raw queries) and `AuditViewerService` (read+render), both `Humans.AuditLog.Services`, plus the singleton `AuditActionRegistry` (also registered as an `IHostedService` so it is built at startup). Verbs and flags come from the `AuditActionDefinition` on each `AuditEvent`, which backs both `RenderPlainText` (agent tool output, with viewer-GUID → "You" substitution) and `RenderStructured` (view-component HTML composition).
**Owned tables:** `audit_log`
**Status:** G5 — own project `src/Sections/Humans.AuditLog`, cross-section surface in its `Contracts/` folder (nobodies-collective/Humans#866).

### Read+render path and the Contracts home

`AuditViewerService` wraps the section's own `IAuditLogReader` raw queries with actor, subject and team name resolution, taken from `IEnumerable<IEntityNameContributor>` (`Humans.Base`). Project references are `Humans.Base`, `Humans.Gdpr` (the `IUserDataContributor` contract) and `Humans.Users.Contracts` (merge-tombstone chain-following).

**One Contracts home.** The section's `Contracts/` folder (namespace `Humans.AuditLog.Contracts`) carries `IAuditLogService` — the append path, called from most sections — plus `AuditActionDefinition` / `IAuditActionContributor` / `AuditActionContributor`, `IAuditViewerService` / `AuditEvent` and `ILegacyGoogleSyncAuditReader`.

**Section-internal reads: `IAuditLogReader`.** `internal interface IAuditLogReader` (`Services/IAuditLogReader.cs`) holds the raw-snapshot reads whose only caller is `AuditViewerService` — `GetFilteredAsync`, `GetByUserAsync`, plus `GetFilteredEntriesAsync` so the viewer takes one injection rather than two. `AuditLogService` implements it alongside `IAuditLogService`; `Section.Register` maps both to the same scoped instance. Nothing outside this assembly can name it, which is the point: those reads stay off the public surface.

**`<vc:audit-log>` binding.** The component is public in `Humans.AuditLog.ViewComponents`; its views are at `Views/Shared/Components/AuditLog/`. Every consuming assembly — `Humans.Calendar`, `Humans.Debug`, `Humans.Expenses`, `Humans.Store`, `Humans.Teams`, `Humans.Tickets`, `Humans.Users` — needs both a `ProjectReference` and `@addTagHelper *, Humans.AuditLog` in its `_ViewImports.cshtml`; a missing directive ships inert literal markup with a green build. Note the reference can be IL-invisible: Razor resolves `@addTagHelper` at compile time and the generated tag helper names no type from this assembly, so a consumer that only *renders* audit shows no `Humans.AuditLog` entry in `GetReferencedAssemblies()`. Seeded-marker render assertions guard the binding (`Humans.Integration.Tests`, nightly, not CI): `AuditLogPageRenderTests` on `/Users/Admin/{id}`, `/Debug/WidgetGallery` and `/Profile/{id}`, `StoreControllerTests` on `/Store/Order/{id}`, plus a source scan asserting every `<vc:audit-log>` call site sits under a `_ViewImports` chain that binds it.

- `AuditLogService` (`internal sealed`, `Humans.AuditLog.Services`) depends only on abstractions — no `DbContext`, no `IMemoryCache`. Its cross-section surface is the whole of `Humans.AuditLog.Contracts.IAuditLogService` — four members, each with a definition-typed form and a transitional enum form: the two `LogAsync` write overloads plus `GetFilteredEntriesAsync` (Issues, `ExpenseReportService`, Users' `DuplicateAccountService`) and `GetEntityIdsForEntityTypeActionsAsync` (`ShiftsController.OrphanSignups`).
- `IAuditLogRepository` (impl `src/Sections/Humans.AuditLog/Data/AuditLogRepository.cs`, `internal sealed`) is the only file that touches `DbContext.AuditLogEntries` — confirmed by source: no other repository references the `AuditLogEntries` DbSet. Uses `IDbContextFactory<AuditLogDbContext>` with short-lived contexts per call.
- **Decorator decision — no caching decorator (§15 Option A).** Writes are scattered across most sections; reads are admin-only and already filtered server-side by index. No benefit from a section-owned cache.
- **Predicate-pushed reads (sanctioned exception to `no-linq-at-db-layer`).** Unlike most sections where in-memory filtering is preferred, `IAuditLogRepository` keeps predicate-pushed query methods (`GetByUserIdsAsync`, `GetFilteredAsync`, `GetFilteredEntriesAsync`, etc.) rather than exposing a `GetAll().Where(...)` surface. Reason: `audit_log` is a large append-only table with many writers, indefinite retention, and no ceiling on row count — loading all rows into RAM for in-memory filtering does not scale here. The section doc explicitly justifies this exception.
- **Append-only enforcement:** the Postgres triggers `prevent_audit_log_update` and `prevent_audit_log_delete` reject UPDATE/DELETE at the database; `IAuditLogRepository` exposes no `Update*` / `Delete*` / `Remove*` surface to begin with.
- **Own DbContext (#858):** `AuditLogDbContext` (`src/Sections/Humans.AuditLog/Data/AuditLogDbContext.cs`) maps only `audit_log`, migrates under `Data/Migrations/` against `__EFMigrationsHistory_AuditLog`, and carries the immutability triggers in its baseline.
- **Cross-domain navs on the entity:** `ActorUserId`, `EntityId` and `RelatedEntityId` are bare cross-section Guid columns — no FK constraint, no nav property. Display names for those Guids come from the `IEntityNameContributor` fan-out.

- **Member-facing resources.** `AuditLogResource` localizes line/table entry labels, action badges and subject/self/no-subject verb forms in en, es, de, it, fr and ca, keyed by action name; a definition with no entry falls back to its English verb and its name split into words. Stored descriptions and `RenderPlainText` output stay unchanged. Admin-only `/AuditLog` pages retain English copy under the admin localization exemption; the admin page's quick-filter buttons and alert banner come from the registry's `QuickFilter` and `Alert` flags, not from the view.

### Touch-and-clean guidance

- A new audit action is a field on your section's `<Section>AuditActions`, documented in your section's `Docs/<Section>.md`; nothing in this section changes. While your section still uses the enum, add the value and its `LegacyAuditActions` line together, plus a one-line entry in the catalog above. Reviewers should be able to grep the name to find the single writer.
- Do **not** call `IAuditLogService.LogAsync` before the business save. Audit goes after, always.
- Do **not** add an `Update*` / `Delete*` / `Remove*` method to `IAuditLogRepository`; the database triggers will reject the operation regardless.
- Do **not** attempt to log inside an outer transaction expecting rollback — audit commits independently via its own `DbContext`.
- Do **not** read `audit_log` from outside this section. New admin dashboards go through `IAuditViewerService`; a new raw-snapshot read belongs on the internal `IAuditLogReader`, and only a read another section genuinely cannot get from the view component earns a place on `IAuditLogService`.
- Do **not** confuse `audit_log` with `budget_audit_logs` — the Budget section owns its own append-only field-level log (`BudgetAuditLog`), rendered at `/Finance/AuditLog`. That is a separate table, separate service, and not part of this section.
