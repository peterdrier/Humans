<!-- freshness:triggers
  src/Sections/Humans.AuditLog/**
  src/Sections/Humans.AuditLog.Contracts/**
  src/Sections/Humans.AuditLog.Contracts/AuditAction.cs
-->
<!-- freshness:flag-on-change
  Audit log append-only invariant, AuditAction enum surface, and self-persisting semantics — review when AuditLog service/repo/entity changes.
-->

# Audit Log — Section Invariants

Append-only system audit trail: who did what, when, to which entity. Used by every section that performs a privileged or irreversible action. Enforced append-only per design-rules §12.

## Concepts

- An **Audit Log Entry** is an append-only record of a single user-initiated or job-initiated action. Captures actor, action, entity type + id, free-text description, and timestamp; Google sync entries also carry resource id, role, sync source, success/error, and the user email at the time of the call.
- **AuditAction** is the cross-section enum (`Humans.AuditLog.Contracts.AuditAction`) of action names, stored as string in the DB via `HasConversion<string>()`. Every action name is a contract — sections use the shared enum so reviewers can grep "who writes TierApplicationApproved" across the whole codebase.
- **Self-persisting audit** (design-rules §7a): `IAuditLogService.LogAsync` saves each entry immediately via `IAuditLogRepository.AddAsync`, which uses `IDbContextFactory<AuditLogDbContext>` to open a fresh per-call context and `SaveChangesAsync`. Callers do not need to `SaveChanges` to flush audit, and must not expect audit to roll back if a later business step fails.
- **Best-effort** — audit save failures are logged at Error and swallowed inside `AuditLogService.PersistAsync`. An audit hiccup never fails the business operation that called it.

## Data Model

### AuditLogEntry

Append-only per design-rules §12. Enforced by the Postgres triggers `prevent_audit_log_update` / `prevent_audit_log_delete` (both calling `prevent_audit_log_modification()`, which raises an exception on any UPDATE or DELETE against `audit_log`). They were introduced in `20260212152552_Initial` and are re-created by the section's own baseline `Migrations/AuditLog/20260810193154_BaselineAuditLog` when `AuditLogDbContext` was peeled out (nobodies-collective/Humans#858).

**Table:** `audit_log` (DbSet `AuditLogEntries`, `AuditLogDbContext`, history `__EFMigrationsHistory_AuditLog`)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Action | AuditAction | Enum stored as string, max 100 chars |
| EntityType | string (100) | Type of the primary affected entity (e.g. `"User"`, `"Team"`, `"GoogleResource"`) |
| EntityId | Guid | Id of the primary affected entity (non-nullable) |
| Description | string (4000) | Human-readable description of what happened |
| OccurredAt | Instant | When the action occurred — callers stamp via `IClock` |
| ActorUserId | Guid? | Bare cross-section Guid column — no FK constraint, no nav (nobodies-collective/Humans#992). Nullable because system jobs write audit with no actor. |
| RelatedEntityId | Guid? | Id of a secondary related entity (e.g. UserId when EntityType=Team) |
| RelatedEntityType | string? (100) | Type of the secondary related entity |

**Retired columns.** `ResourceId`, `Success`, `ErrorMessage`, `Role`, `SyncSource` and `UserEmail` were the Google-sync wing. GoogleIntegration's own `google_sync_log` replaced them in nobodies-collective/Humans#1083, and nothing reads or writes them any more: they are off `AuditLogEntrySnapshot` and `AuditEvent`, and the entity keeps them only so the historical rows stay readable until the data move retires them. `ILegacyGoogleSyncAuditReader` (contracts leaf, implemented by `AuditLogService`) is the one read left over them — it feeds GoogleIntegration's `/Google/Admin/SyncHistoryMigration` screen, which copies the history into `google_sync_log` and changes nothing here. The column drop, and this reader, are their own PR under `no-drops-until-prod-verified`.

**Indexes:** `(EntityType, EntityId)`, `(RelatedEntityType, RelatedEntityId)`, `OccurredAt`, `Action`, `ResourceId`.

### AuditAction (cross-section enum)

`AuditAction` (`Humans.AuditLog.Contracts.AuditAction`) is the shared contract across all writers. Stored as string via `HasConversion<string>()`. Full surface as of this sweep:

<!-- freshness:auto id="auditaction-catalog" prompt="Regenerate this catalog from src/Sections/Humans.AuditLog.Contracts/AuditAction.cs: every enum value must appear exactly once, grouped by section, one line each; preserve prose outside this block." -->
- **Onboarding / Profile / Users:** `ConsentCheckCleared`, `ConsentCheckFlagged`, `SignupRejected`, `VolunteerApproved`, `MemberSuspended`, `MemberUnsuspended`, `AccountAnonymized`, `MembershipsRevokedOnDeletionRequest`, `AccountMergeRequested`, `AccountMergeAccepted`, `AccountMergeRejected`, `AccountPurged`, `CommunicationPreferenceChanged`, `ContactCreated`.
- **User emails:** `UserEmailProviderBackfilled`, `UserEmailGoogleSet`, `UserEmailGoogleCleared`, `UserEmailLinked`, `UserEmailUnlinked`, `UserEmailPrimarySet`, `UserEmailPrimaryCleared`, `UserEmailDeleted`, `UserEmailVisibilityChanged`, `UserEmailAdded`, `UserEmailManuallyVerified`, `OrphanUserEmailDeleted`, `GhostExternalLoginsDeleted`, `LegacyIdentityEmailBackfilled`, `OAuthRenameCollision`, `OAuthRenameCollisionBlocked`, `UserEmailDisplacedByOAuthRename` (the last three are the OAuth-callback reconcile audits from nobodies-collective/Humans#697, written by `UserEmailService`).
- **Governance (tier applications + roles):** `TierApplicationApproved`, `TierApplicationRejected`, `TierDowngraded`, `RoleAssigned`, `RoleEnded`.
- **Teams:** `TeamMemberAdded`, `TeamMemberRemoved`, `TeamMemberRoleChanged`, `TeamJoinedDirectly`, `TeamLeft`, `TeamJoinRequestApproved`, `TeamJoinRequestRejected`, `TeamRoleDefinitionCreated`, `TeamRoleDefinitionUpdated`, `TeamRoleDefinitionDeleted`, `TeamRoleAssigned`, `TeamRoleUnassigned`, `TeamPageContentUpdated`, `RotaMovedToTeam`, `EarlyEntryGranted`, `EarlyEntryUpdated`, `EarlyEntryRevoked` (the last three written by `TeamService` when team early-entry grants are added/edited/revoked, entity type `TeamEarlyEntryGrant`).
- **Google Integration:** `GoogleResourceProvisioned`, `GoogleResourceAccessGranted`, `GoogleResourceAccessRevoked`, `GoogleResourceDeactivated`, `GoogleResourceSettingsRemediated`, `GoogleResourceInheritanceDriftCorrected`, `GoogleEmailRenamed`, `AnomalousPermissionDetected`, `GoogleSyncRetryScheduled` (a scoped group-sync retry was scheduled — written by `GoogleGroupSyncService` on Execute failure and by `GoogleController` admin actions).
- **Workspace accounts:** `WorkspaceAccountProvisioned`, `WorkspaceAccountSuspended`, `WorkspaceAccountReactivated`, `WorkspaceAccountPasswordReset`, `WorkspaceAccountLinked`, `WorkspaceAccountBackupCodesGenerated`, `WorkspaceAccountResetBlockedFor2Sv`. `WorkspaceAccountBackupCodesInvalidated` is reserved (wired briefly during PR #254, no active writer — do not remove; audit enum is positional).
- **Camps:** `CampCreated`, `CampUpdated`, `CampDeleted`, `CampNameChanged`, `CampImageUploaded`, `CampImageDeleted`, `CampLeadAdded`, `CampLeadRemoved`, `CampPrimaryLeadTransferred`, `CampSeasonCreated`, `CampSeasonApproved`, `CampSeasonRejected`, `CampSeasonWithdrawn`, `CampSeasonStatusChanged`, `CampMemberRequested`, `CampMemberApproved`, `CampMemberRejected`, `CampMemberWithdrawn`, `CampMemberLeft`, `CampMemberRemoved`, `CampMemberAddedByLead`, `CampRoleDefinitionCreated`, `CampRoleDefinitionUpdated`, `CampRoleDefinitionDeactivated`, `CampRoleDefinitionReactivated`, `CampRoleAssigned`, `CampRoleUnassigned`, `CampEarlyEntryGranted`, `CampEarlyEntryRevoked`, `CampSeasonEeSlotCountChanged`, `CampSettingsEeStartDateChanged` (camp early-entry grant lifecycle plus per-season EE slot-count and EE start-date settings changes, written by `CampService`).
- **Shifts:** `ShiftSignupCreated`, `ShiftSignupConfirmed`, `ShiftSignupRefused`, `ShiftSignupVoluntold`, `ShiftSignupBailed`, `ShiftSignupNoShow`, `ShiftSignupCancelled`, `ShiftSignupReassigned`. `ShiftSignupCreated` fires on every self-signup (Pending or Confirmed) so the creation moment is always traceable; `ShiftSignupConfirmed` fires only on the later Pending → Confirmed transition by an approver. `ShiftSignupReassigned` fires once per account-merge fold, summarising how many ShiftSignups were re-FK'd from source to target.
- **Volunteer tracking (Shifts):** `VolunteerCampSetupSet`, `VolunteerCampSetupCleared`, `VolunteerDayOffMarked`, `VolunteerDayOffCleared` (volunteer-tracking camp-setup flag and day-off marks, written via `VolunteerTrackingController`); `VolunteerAvailabilitySet`, `VolunteerAvailabilityCleared` (coordinator edits a volunteer's declared build availability on their behalf — Profile build strip); `CoordinatorRotaMessageSent`, `CoordinatorTeamRotasMessageSent` (coordinator messages to rota / team-rota signups, written by `RotaCoordinatorMessageService`). `VolunteerDayBlocked`, `VolunteerDayUnblocked`, `VolunteerOwnBlockedDaysSaved` are reserved — wired during the first volunteer-tracking day-off iteration, renamed in the redesign; no active writer — do not remove (audit enum is positional).
- **Calendar:** `CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden`.
- **Feedback / Communications:** `FeedbackResponseSent`, `FeedbackStatusChanged`, `FeedbackAssignmentChanged`, `FacilitatedMessageSent`, `FeedbackGitHubLinked` (linking a feedback report to a GitHub issue — the last Feedback mutation that recorded no actor, nobodies-collective/Humans#1128).
- **Issues:** `IssueStatusChanged`, `IssueAssigneeChanged`, `IssueSectionChanged`, `IssueGitHubLinked`, `IssueCreated` (filing an issue through the machine API, where the filer and the reporter can differ, nobodies-collective/Humans#1128 — the in-app reporter is their own filer, so that path still records creation on the issue row alone).
- **Store:** `StoreOrderCreated`, `StoreOrderDeleted`, `StoreLineAdded`, `StoreLineRemoved`, `StoreCounterpartyEdited`, `StoreProductCreated`, `StoreProductUpdated`, `StoreProductPriceChanged`, `StoreProductDeactivated`, `StoreInvoiceIssued`, `StorePaymentRecorded`, `StorePaymentSettled`, `StorePaymentFailed`, `StorePaymentExpired`, `StorePaymentsReconciled`.
- **Containers:** `ContainerCreated`, `ContainerUpdated`, `ContainerDeleted`, `ContainerPlacementSaved`, `ContainerPlacementCleared`, `ContainerPlacementNotesUpdated` — container CRUD and city-placement lifecycle, written by `ContainerService`.
- **Expenses / IBAN:** `ExpenseSubmit`, `ExpenseEndorse`, `ExpenseCoordinatorReject`, `ExpenseApprove`, `ExpenseReject`, `ExpenseWithdraw`, `ExpenseCategoryOverride`, `ExpenseSepaSent`, `ExpenseSepaReopened`, `ExpensePaid`, `ExpenseAttachmentUploaded`, `ExpenseAttachmentRemoved`, `IbanSet`, `IbanRemove`, `IbanReveal`, `ExpenseHoldedPushed`, `ExpenseHoldedFailed`, `ExpenseHoldedRequeued`. `ExpenseSepaReopened` fires when a FinanceAdmin/Admin reopens a SepaSent report back to Approved after a failed SEPA file download; `IbanReveal` logs plaintext IBAN reveals (expense detail + users admin); `ExpenseHoldedPushed`/`ExpenseHoldedFailed`/`ExpenseHoldedRequeued` track the Holded push outbox (job pushes/write-offs, admin re-queues), written by `ExpenseReportService`.
- **Finance / SEPA payout:** `SepaPayoutTransfer` — one entry per credit transfer in a generated pain.001.001.09 payout file, written by Finance's `Service.GenerateSepaPayoutAsync`. `EntityId` is the persisted `SepaPayoutTransfer` row (the same id the file's `EndToEndId` derives from), the related entity is the member paid, and the description carries the amount and a **masked** IBAN only.
- **MailerLite / Imports:** `MailerLiteReconciliationCompleted` — job-level summary written at the end of each MailerLite import. Description carries counts as a structured string; no per-row PII.
- **MailerLite / Audience sync:** `MailerLiteAudienceSyncCompleted` — written once per audience by `MailerLiteAudienceSyncService.SyncAsync` (daily Hangfire job + on-demand admin button). Description is a JSON object with `audience_key`, `group_id`, `group_name`, `candidates`, `excluded_unsubscribed`, `created`, `assigned`, `already_assigned`, `unassigned`, `errors`. No per-row PII.
- **Scanner:** `GateTerminalPasswordSet` — written when the gate-terminal shared kiosk password is set.
- **Gate:** `GateStaffPinSet`, `GateStaffPinReset` — gate personal-PIN lifecycle, written by `GateService` with the acting user (the staffer on kiosk self-enrol, the admin on admin set/reset); PIN values are never logged.
- **Tickets (transfers):** `TicketTransferRequested`, `TicketTransferApproved`, `TicketTransferRejected`, `TicketTransferCancelled`, `TicketTransferAutoFailed` (the flag-gated automated TicketTailor void+reissue failed and fell back to manual handling); plus `TicketContactsImported`.
- **Surveys:** `SurveyCreated`, `SurveyUpdated`, `SurveyOpened`, `SurveyClosed`, `SurveyInvitesSent`, `SurveyReminderSent` — survey lifecycle events written by the Survey section.
- **Backdoor:** `BackdoorApiKeyIssued`, `BackdoorApiKeyRevoked` — personal machine-API key lifecycle (nobodies-collective/Humans#1128); rotation is recorded as a revoke of the old key followed by an issue of the new one.
<!-- /freshness:auto -->

Note: `BudgetAuditLog` is a separate per-section append-only log owned by Budget — it is **not** an `AuditAction` value and does not write to `audit_log`.

## Routing

All Audit Log routes are owned by `AuditLogController` (`[Route("AuditLog")]`).

| Route | Action | Auth policy |
|-------|--------|------------|
| `GET /AuditLog` | `AuditLogController.Index` | `BoardOrAdmin` |

These three moved to the **Monitor** section at nobodies-collective/Humans#866 — two of them
injected GoogleIntegration services, and a horizontal may not reference a vertical
(`peters-hard-rules.md`). See [Monitor.md](../../Humans.Monitor/Docs/Monitor.md):

| Route | Now |
|---|---|
| `POST /Monitor/CheckDriveActivity` | `MonitorController.CheckDriveActivity`, `BoardOrAdmin` |
| `GET /Monitor/Resource/{id}` | `MonitorController.Resource`, `BoardOrAdmin` |
| `GET /Monitor/Human/{id}` | `MonitorController.Human`, `HumanAdminBoardOrAdmin` |

`AuditLogController` injects `IAuditViewerService` — no controller touches `IAuditLogService` or any repository directly.

Note: `AdminController.Index` consumes `IAuditViewerService.GetRecentAsync(8)` for the `/Admin` dashboard activity widget. That is widget consumption from another section's service, not route ownership. AuditLog owns no Admin routes.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Write audit entries via `IAuditLogService.LogAsync(...)` (human or job overload). No authorization check at the log site — the caller has already authorized the underlying action |
| Board, Admin | View the system audit log via `GET /AuditLog` (`[Authorize(Policy = PolicyNames.BoardOrAdmin)]`), with filter + pagination via `IAuditViewerService.GetPageAsync` |
| Any authenticated viewer of an entity page | See per-entity audit history rendered through the shared `AuditLogViewComponent` (e.g. on Profile, Team, Calendar, Store order, Google resource pages) — entries are scoped by the `<vc:audit-log>` predicate attributes and inherit the host page's authorization |

No one reads audit entries anonymously. The `/AuditLog` dashboard is gated to BoardOrAdmin; per-entity audit history is gated by the host page's policy.

## Invariants

- Audit entries are append-only. `IAuditLogRepository` exposes `AddAsync` and `GetXxxAsync` — **no** `UpdateAsync`, **no** `DeleteAsync`, **no** `RemoveAsync`.
- The `audit_log` table itself rejects UPDATE and DELETE at the database layer via the `prevent_audit_log_update` and `prevent_audit_log_delete` Postgres triggers (created by `Migrations/AuditLog/20260810193154_BaselineAuditLog`). No application path can mutate or delete an existing row.
- `LogAsync` is self-persisting — each call routes through `AuditLogRepository.AddAsync`, which opens a fresh `DbContext` via `IDbContextFactory<AuditLogDbContext>`, adds the entry, and calls `SaveChangesAsync`. Callers do not flush audit.
- Audit is called **after** the business save, never before (design-rules §7a). A business rollback never leaves a ghost audit row because audit hasn't written yet.
- Audit commits separately from the business change. The rare failure mode is "business saved, audit did not" — logged loudly, detectable by reconciling row counts, and strictly better than "audit silently vanishes".
- Audit save failures are swallowed after a log at Error inside `AuditLogService.PersistAsync`. The audit `LogAsync` overloads do not throw back to the caller.
- `ActorUserId` is nullable — system jobs (Hangfire recurring jobs) write audit entries with no actor; the job-overload `LogAsync(..., string jobName, ...)` prepends the job name to the description.
- `AuditLogService` implements `IUserDataContributor`, exposing every entry where the user is actor, primary entity, or related entity to the GDPR export orchestrator.
- Per-user reads chain-follow merge tombstones via `IUserService.GetMergedSourceIdsAsync(userId)` so audit entries written under a now-merged source id surface for the fold target. Applies to `GetByUserAsync`, `GetUserAuditLogPageAsync`, the per-entity history filter when entity is a User, and `ContributeForUserAsync` (GDPR). Audit entries are append-only (§12) and stay attributed to the source User row by design — `AnonymizeForMergeAsync` does NOT rewrite `ActorUserId` / `EntityId` / `RelatedEntityId` columns.

### `<vc:audit-log>` — the render path for other sections

A section that wants to *show* audit history emits `<vc:audit-log>` with a predicate; it never reads audit itself. The component owns the read (`IAuditViewerService`) and the render.

- **Predicates** (first match wins): `entity-ids` (several ids of one `entity-type`, one query each, merged newest-first, capped at `limit`); otherwise `entity-type` / `entity-id` / `user-id` / `actions`. `since` drops anything older, applied after the read.
- **Layouts** (`layout`): `line` (default) is the narrative one-line-per-entry list; `table` is When · Actor · Action · Subject · Description · Target, subsettable via `columns`. The Google-specific `sync` layout and its `resource-id` / `google-sync-only` predicates left with the sync wing in nobodies-collective/Humans#1083 — GoogleIntegration's own `<vc:google-sync-log>` renders that grid now.
- **Chrome:** `title`, `empty-text`, `column-labels` and `show-card`. The section ships no resource set, so a localized host page passes every display string in. `column-labels` is **keyed, not positional** (`when:Fecha,actor:Remitente`): an unsupplied or unknown key falls back to the English column name, and reordering `columns` can never silently mis-header the table.
- Every consuming assembly needs `@addTagHelper *, Humans.AuditLog` in its `_ViewImports.cshtml`. Missing it is silent: the element ships as inert literal markup with a green build and no runtime error. Pinned by `AuditLogPageRenderTests`.

## Negative Access Rules

- Sections **cannot** read audit to render it. Reading `IAuditViewerService` from another section's service, controller or view model to build a table is the anti-pattern `<vc:audit-log>` exists to remove — Monitor, Profile and Store were migrated off it.
- Callers **cannot** `UpdateAsync`, `DeleteAsync`, or `RemoveAsync` an audit entry. The repository exposes no such methods, and the database triggers reject the operation even if a caller went around the repository.
- Services **cannot** call `IAuditLogService.LogAsync` inside an outer `DbContext` transaction expecting audit to roll back with it — audit uses its own context via `IDbContextFactory`.
- Services **cannot** bypass `IAuditLogService` and write `audit_log` directly. `AuditLogRepository` is the only file that touches `DbContext.AuditLogEntries`. The former GoogleIntegration violation (`DriveActivityMonitorRepository.cs` writing `ctx.AuditLogEntries.AddRange(anomalies)` directly) was fixed as part of that section's §15 migration (#554) — `DriveActivityMonitorService` now routes anomaly audit entries through `IAuditLogService` like every other writer, and `DriveActivityMonitorRepository.cs` no longer exists.
- The log **cannot** be pruned by production admins. There is no retention/cleanup job — entries persist indefinitely.
- Controllers **cannot** read `audit_log` directly. The Board/Admin dashboard goes through `IAuditViewerService.GetPageAsync`, and per-entity / per-user views go through the other `IAuditViewerService` overloads — the viewer service composes the page (entries + actor/subject/team display batching, with display names resolved through Base's `IEntityNameContributor` fan-out) inside the Audit Log section. `IAuditLogService` is the write side; the read+render path is `IAuditViewerService`.

## Triggers

- **On any privileged business write:** the owning section's service calls `IAuditLogService.LogAsync(action, entityType, entityId, description, actorUserId, ...)` after its business `SaveChangesAsync` returns successfully.
- **On a background job action:** the job calls the job overload `IAuditLogService.LogAsync(action, entityType, entityId, description, jobName, ...)` — `ActorUserId` is recorded as null and the job name is prepended to the description.
- **No cleanup trigger:** there is no retention or pruning job; the database triggers reject DELETE in every case.

## Cross-Section Dependencies

Nearly every other section **writes** into this section via `IAuditLogService`. This section depends on almost nothing:

- **No section (display names):** `AuditViewerService` injects `IEnumerable<IEntityNameContributor>` from `Humans.Base` and asks every contributor to name the Guids on a page — actor, subject and target-team ids in one pass. Teams and Users implement it on their existing caching services; ids nobody owns come back unnamed. This is the inversion nobodies-collective/Humans#1059 asked for: the sections point at the Base contract, and AuditLog names none of them.
- **GDPR (`IUserDataContributor`):** `AuditLogService` contributes per-user audit slices to the GDPR export orchestrator via `ContributeForUserAsync`.
- **Users/Identity:** `IUserService.GetMergedSourceIdsAsync` — chain-follow merge tombstones on every per-user audit read so source-attributed entries surface for the fold target.

No other cross-section writes from this section outward. Audit is a sink.

## Architecture

**Owning services:** `AuditLogService` (write + raw queries) and `AuditViewerService` (read+render), both `Humans.AuditLog.Services`. `AuditEventTextualizer` is the stateless verb-table helper backing both `RenderPlainText` (agent tool output, with viewer-GUID → "You" substitution) and `RenderStructured` (view-component HTML composition).
**Owned tables:** `audit_log`
**Status:** G5 — own project `src/Sections/Humans.AuditLog` + contracts leaf `src/Sections/Humans.AuditLog.Contracts` (nobodies-collective/Humans#866). Original migration: nobodies-collective/Humans#552.

### Read+render path and the two Contracts homes

`AuditViewerService` wraps the section's own `IAuditLogReader` raw queries with actor, subject and team name resolution, taken from `IEnumerable<IEntityNameContributor>` (`Humans.Base`). It lived in `Humans.Application` for one batch on the reading that a horizontal section may not reference a vertical. **Peter reversed that in the Base-floor decision of 2026-08-14**: a former Base resident that names another section's read interface moves to its section. G5 lane 4b-2h moved `IAuditViewerService`, `AuditEvent`, `AuditEventPage`, `AuditEventTextualizer` and `AuditLogViewComponent` into this project, and retired the assembly-level `SectionReferencesNoVerticalSection` test whose premise the decision inverted. nobodies-collective/Humans#1059 then removed the reason the section needed the names at all: the `Humans.Teams.Contracts` and `Humans.GoogleIntegration.Contracts` references are gone, and only `Humans.Users.Contracts` (merge-tombstone chain-following, `[DontFix]`) and `Humans.Gdpr.Contracts` remain.

**Two Contracts homes, on purpose.** The leaf *project* `Humans.AuditLog.Contracts` carries `IAuditLogService` — the append path, called from ~130 files including Base ones, which cannot reference a section. This project's `Contracts/` *folder* carries `IAuditViewerService` / `AuditEvent` / `AuditEventPage`, whose consumers are all Shell or other sections and can `ProjectReference` `Humans.AuditLog` directly. Both use the namespace `Humans.AuditLog.Contracts`, as Shifts and Tickets already do.

**Section-internal reads: `IAuditLogReader`.** `internal interface IAuditLogReader` (`Services/IAuditLogReader.cs`) holds the raw-snapshot reads whose only caller is `AuditViewerService` — `GetRecentAsync`, `GetFilteredAsync`, `GetByUserAsync`, plus `GetFilteredEntriesAsync` so the viewer takes one injection rather than two. `AuditLogService` implements it alongside `IAuditLogService`; `Section.Register` maps both to the same scoped instance. Nothing outside this assembly can name it, which is the point: those reads used to sit on the public leaf.

**`<vc:audit-log>` binding.** The component is public in `Humans.AuditLog.ViewComponents`; its views are at `Views/Shared/Components/AuditLog/`. Every consuming assembly — `Humans.Web`, `Humans.Users`, `Humans.Teams`, `Humans.Store`, `Humans.Tickets` — needs both a `ProjectReference` and `@addTagHelper *, Humans.AuditLog` in its `_ViewImports.cshtml`; a missing directive ships inert literal markup with a green build. Note the reference can be IL-invisible: Razor resolves `@addTagHelper` at compile time and the generated tag helper names no type from this assembly, so a consumer that only *renders* audit shows no `Humans.AuditLog` entry in `GetReferencedAssemblies()`. Seeded-marker render assertions guard the binding: `AuditLogPageRenderTests` on `/Users/Admin/{id}`, `/WidgetGallery` and `/Profile/{id}`, `StoreControllerTests` on `/Store/Order/{id}`, plus a source scan asserting every `<vc:audit-log>` call site sits under a `_ViewImports` chain that binds it.

- `AuditLogService` (`internal sealed`, `Humans.AuditLog.Services`) depends only on abstractions — no `DbContext`, no `IMemoryCache`. Its cross-section surface is the whole of `Humans.AuditLog.Contracts.IAuditLogService` — four members: the two `LogAsync` write overloads plus `GetFilteredEntriesAsync` (Issues interleaves audit events with issue comments) and `GetEntityIdsForEntityTypeActionsAsync` (`ShiftsController.OrphanSignups`). Consumed by ~130 files: that leaf is a project rather than a folder because most of those consumers are in Base.
- `IAuditLogRepository` (impl `src/Sections/Humans.AuditLog/Data/AuditLogRepository.cs`, `internal sealed`) is the only file that touches `DbContext.AuditLogEntries` — confirmed by source: no other repository references the `AuditLogEntries` DbSet. Uses `IDbContextFactory<AuditLogDbContext>` with short-lived contexts per call.
- **Decorator decision — no caching decorator (§15 Option A).** Writes are scattered across every section (~96 call sites at migration time); reads are admin-only and already filtered server-side by index. No benefit from a section-owned cache.
- **Predicate-pushed reads (sanctioned exception to `no-linq-at-db-layer`).** Unlike most sections where in-memory filtering is preferred, `IAuditLogRepository` keeps predicate-pushed query methods (`GetByUserAsync`, `GetFilteredAsync`, `GetFilteredEntriesAsync`, etc.) rather than exposing a `GetAll().Where(...)` surface. Reason: `audit_log` is a large append-only table with ~96 writers, indefinite retention, and no ceiling on row count — loading all rows into RAM for in-memory filtering does not scale here. The section doc explicitly justifies this exception.
- **Append-only enforcement:** the Postgres triggers `prevent_audit_log_update` and `prevent_audit_log_delete` reject UPDATE/DELETE at the database; `IAuditLogRepository` exposes no `Update*` / `Delete*` / `Remove*` surface to begin with.
- **Own DbContext (#858):** `AuditLogDbContext` (`src/Sections/Humans.AuditLog/Data/AuditLogDbContext.cs`) maps only `audit_log`, migrates under `Data/Migrations/` against `__EFMigrationsHistory_AuditLog`, and carries the immutability triggers in its baseline.
- **Cross-domain navs on the entity:** `ActorUserId`, `EntityId` and `RelatedEntityId` are bare cross-section Guid columns — no FK constraint, no nav property (all 54 cross-section FK constraints were cut in nobodies-collective/Humans#992; the last cross-section EF navs were stripped in #996). Display names for those Guids come from the `IEntityNameContributor` fan-out, so the section names no other section to render them (nobodies-collective/Humans#1059).

- **No resource set.** Neither `/AuditLog` page carries a `Localizer[…]` call — the copy is admin-only English — so the section ships no `Resources/` folder and no `AuditLogResource` (template step 3b's first question).

### Touch-and-clean guidance

- When adding a new `AuditAction` enum value, pair it with a one-line entry in the section list above. Reviewers should be able to grep the enum value to find the single writer.
- Do **not** call `IAuditLogService.LogAsync` before the business save. Audit goes after, always.
- Do **not** add an `Update*` / `Delete*` / `Remove*` method to `IAuditLogRepository`; the database triggers will reject the operation regardless.
- Do **not** attempt to log inside an outer transaction expecting rollback — audit commits independently via its own `DbContext`.
- Do **not** read `audit_log` from outside this section. New admin dashboards go through `IAuditViewerService`; a new raw-snapshot read belongs on the internal `IAuditLogReader`, and only a read another section genuinely cannot get from the view component earns a place on `IAuditLogService`.
- Do **not** confuse `audit_log` with `budget_audit_logs` — the Budget section owns its own append-only field-level log (`BudgetAuditLog`), rendered at `/Finance/AuditLog`. That is a separate table, separate service, and not part of this section.
