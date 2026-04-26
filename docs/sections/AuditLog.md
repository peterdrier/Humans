<!-- freshness:triggers
  src/Humans.Application/Services/AuditLog/**
  src/Humans.Domain/Entities/AuditLogEntry.cs
  src/Humans.Infrastructure/Data/Configurations/AuditLog/**
  src/Humans.Infrastructure/Repositories/AuditLogRepository.cs
  src/Humans.Application/Interfaces/Repositories/IAuditLogRepository.cs
  src/Humans.Application/Interfaces/IAuditLogService.cs
-->
<!-- freshness:flag-on-change
  Audit log append-only invariant, AuditAction enum surface, and self-persisting semantics — review when AuditLog service/repo/entity changes.
-->

# Audit Log — Section Invariants

Append-only system audit trail: who did what, when, to which entity. Used by every section that performs a privileged or irreversible action. Enforced append-only per design-rules §12.

## Concepts

- An **Audit Log Entry** is an append-only record of a single user-initiated or job-initiated action. Captures actor, action, entity type + id, free-text description, and timestamp; Google sync entries also carry resource id, role, sync source, success/error, and the user email at the time of the call.
- **AuditAction** is the cross-section enum (`Humans.Domain.Enums.AuditAction`) of action names, stored as string in the DB via `HasConversion<string>()`. Every action name is a contract — sections use the shared enum so reviewers can grep "who writes TierApplicationApproved" across the whole codebase.
- **Self-persisting audit** (design-rules §7a): `IAuditLogService.LogAsync` saves each entry immediately via `IAuditLogRepository.AddAsync`, which uses `IDbContextFactory<HumansDbContext>` to open a fresh per-call context and `SaveChangesAsync`. Callers do not need to `SaveChanges` to flush audit, and must not expect audit to roll back if a later business step fails.
- **Best-effort** — audit save failures are logged at Error and swallowed inside `AuditLogService.PersistAsync`. An audit hiccup never fails the business operation that called it.

## Data Model

### AuditLogEntry

Append-only per design-rules §12. Enforced at two layers: the architecture test `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods` (no Update/Delete/Remove methods on `IAuditLogRepository`), and the Postgres triggers `prevent_audit_log_update` / `prevent_audit_log_delete` defined in migration `20260212152552_Initial` (which raise an exception on any UPDATE or DELETE against `audit_log`).

**Table:** `audit_log` (DbSet `AuditLogEntries`)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Action | AuditAction | Enum stored as string, max 100 chars |
| EntityType | string (100) | Type of the primary affected entity (e.g. `"User"`, `"Team"`, `"GoogleResource"`) |
| EntityId | Guid | Id of the primary affected entity (non-nullable) |
| Description | string (4000) | Human-readable description of what happened |
| OccurredAt | Instant | When the action occurred — callers stamp via `IClock` |
| ActorUserId | Guid? | FK → User. Nullable because system jobs write audit with no actor. `OnDelete: SetNull` |
| ActorUser | User? | EF nav (set, not init) — required by EF for the FK relationship |
| RelatedEntityId | Guid? | Id of a secondary related entity (e.g. UserId when EntityType=Team) |
| RelatedEntityType | string? (100) | Type of the secondary related entity |
| ResourceId | Guid? | FK → GoogleResource. Only set for Google sync entries. `OnDelete: SetNull` |
| Resource | GoogleResource? | EF nav (set, not init) for the Google resource |
| Success | bool? | Whether the Google API call succeeded. Null for non-Google entries |
| ErrorMessage | string? (4000) | Error details if the Google API call failed. Null for non-Google entries |
| Role | string? (100) | The role granted or revoked (e.g. `"writer"`, `"MEMBER"`). Null for non-Google entries |
| SyncSource | GoogleSyncSource? | Stored as string (max 100). What triggered the Google sync action. Null for non-Google entries |
| UserEmail | string? (500) | Email at time of the Google sync action — denormalized so history survives anonymization |

**Indexes:** `(EntityType, EntityId)`, `(RelatedEntityType, RelatedEntityId)`, `OccurredAt`, `Action`, `ResourceId`, `ActorUserId` (FK index).

### AuditAction (cross-section enum)

`AuditAction` (`Humans.Domain.Enums.AuditAction`) is the shared contract across all writers. Stored as string via `HasConversion<string>()`. Full surface as of this sweep:

- **Onboarding / Profile / Users:** `ConsentCheckCleared`, `ConsentCheckFlagged`, `SignupRejected`, `VolunteerApproved`, `MemberSuspended`, `MemberUnsuspended`, `AccountAnonymized`, `MembershipsRevokedOnDeletionRequest`, `AccountMergeRequested`, `AccountMergeAccepted`, `AccountMergeRejected`, `CommunicationPreferenceChanged`, `ContactCreated`.
- **Governance (tier applications + roles):** `TierApplicationApproved`, `TierApplicationRejected`, `TierDowngraded`, `RoleAssigned`, `RoleEnded`.
- **Teams:** `TeamMemberAdded`, `TeamMemberRemoved`, `TeamMemberRoleChanged`, `TeamJoinedDirectly`, `TeamLeft`, `TeamJoinRequestApproved`, `TeamJoinRequestRejected`, `TeamRoleDefinitionCreated`, `TeamRoleDefinitionUpdated`, `TeamRoleDefinitionDeleted`, `TeamRoleAssigned`, `TeamRoleUnassigned`, `TeamPageContentUpdated`, `RotaMovedToTeam`.
- **Google Integration:** `GoogleResourceProvisioned`, `GoogleResourceAccessGranted`, `GoogleResourceAccessRevoked`, `GoogleResourceDeactivated`, `GoogleResourceSettingsRemediated`, `GoogleResourceInheritanceDriftCorrected`, `GoogleEmailRenamed`, `AnomalousPermissionDetected`.
- **Workspace accounts:** `WorkspaceAccountProvisioned`, `WorkspaceAccountSuspended`, `WorkspaceAccountReactivated`, `WorkspaceAccountPasswordReset`, `WorkspaceAccountLinked`.
- **Camps:** `CampCreated`, `CampUpdated`, `CampDeleted`, `CampNameChanged`, `CampImageUploaded`, `CampImageDeleted`, `CampLeadAdded`, `CampLeadRemoved`, `CampPrimaryLeadTransferred`, `CampSeasonCreated`, `CampSeasonApproved`, `CampSeasonRejected`, `CampSeasonWithdrawn`, `CampSeasonStatusChanged`, `CampMemberRequested`, `CampMemberApproved`, `CampMemberRejected`, `CampMemberWithdrawn`, `CampMemberLeft`, `CampMemberRemoved`.
- **Shifts:** `ShiftSignupConfirmed`, `ShiftSignupRefused`, `ShiftSignupVoluntold`, `ShiftSignupBailed`, `ShiftSignupNoShow`, `ShiftSignupCancelled`.
- **Calendar:** `CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden`.
- **Feedback / Communications:** `FeedbackResponseSent`, `FeedbackStatusChanged`, `FeedbackAssignmentChanged`, `FacilitatedMessageSent`.

Note: `BudgetAuditLog` is a separate per-section append-only log owned by Budget — it is **not** an `AuditAction` value and does not write to `audit_log`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Write audit entries via `IAuditLogService.LogAsync(...)` (human or job overload) and `IAuditLogService.LogGoogleSyncAsync(...)`. No authorization check at the log site — the caller has already authorized the underlying action |
| Board, Admin | View the system audit log via `GET /Board/AuditLog` (`[Authorize(Policy = PolicyNames.BoardOrAdmin)]`), with filter + pagination via `IAuditLogService.GetAuditLogPageAsync` |
| Any authenticated viewer of an entity page | See per-entity audit history rendered through the shared `AuditLogViewComponent` (e.g. on Profile, Team, Calendar, Google resource pages) — entries are scoped by `entityType` / `entityId` / `userId` / `actions` filters and inherit the host page's authorization |

No one reads audit entries anonymously. The `/Board/AuditLog` dashboard is gated to BoardOrAdmin; per-entity audit history is gated by the host page's policy.

## Invariants

- Audit entries are append-only. `IAuditLogRepository` exposes `AddAsync` and `GetXxxAsync` — **no** `UpdateAsync`, **no** `DeleteAsync`, **no** `RemoveAsync`. Enforced by `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods`.
- The `audit_log` table itself rejects UPDATE and DELETE at the database layer via the `prevent_audit_log_update` and `prevent_audit_log_delete` Postgres triggers (defined in migration `20260212152552_Initial`). No application path can mutate or delete an existing row.
- `LogAsync` / `LogGoogleSyncAsync` are self-persisting — each call routes through `AuditLogRepository.AddAsync`, which opens a fresh `DbContext` via `IDbContextFactory<HumansDbContext>`, adds the entry, and calls `SaveChangesAsync`. Callers do not flush audit.
- Audit is called **after** the business save, never before (design-rules §7a). A business rollback never leaves a ghost audit row because audit hasn't written yet.
- Audit commits separately from the business change. The rare failure mode is "business saved, audit did not" — logged loudly, detectable by reconciling row counts, and strictly better than "audit silently vanishes".
- Audit save failures are swallowed after a log at Error inside `AuditLogService.PersistAsync`. The audit `LogAsync` overloads do not throw back to the caller.
- `ActorUserId` is nullable — system jobs (Hangfire recurring jobs) write audit entries with no actor; the job-overload `LogAsync(..., string jobName, ...)` prepends the job name to the description.
- `AuditLogService` implements `IUserDataContributor`, exposing every entry where the user is actor, primary entity, or related entity to the GDPR export orchestrator.

## Negative Access Rules

- Callers **cannot** `UpdateAsync`, `DeleteAsync`, or `RemoveAsync` an audit entry. The repository exposes no such methods, and the database triggers reject the operation even if a caller went around the repository.
- Services **cannot** call `IAuditLogService.LogAsync` inside an outer `DbContext` transaction expecting audit to roll back with it — audit uses its own context via `IDbContextFactory`.
- Services **cannot** bypass `IAuditLogService` and write `audit_log` directly. `AuditLogRepository` is the only non-test file that touches `DbContext.AuditLogEntries`.
- The log **cannot** be pruned by production admins. There is no retention/cleanup job — entries persist indefinitely.
- Controllers **cannot** read `audit_log` (or `users`/`teams` for audit display) directly. The Board/Admin dashboard goes through `IAuditLogService.GetAuditLogPageAsync`, which composes the page (entries + user/team display lookups) inside the Audit Log section.

## Triggers

- **On any privileged business write:** the owning section's service calls `IAuditLogService.LogAsync(action, entityType, entityId, description, actorUserId, ...)` after its business `SaveChangesAsync` returns successfully.
- **On a background job action:** the job calls the job overload `IAuditLogService.LogAsync(action, entityType, entityId, description, jobName, ...)` — `ActorUserId` is recorded as null and the job name is prepended to the description.
- **On Google sync apply:** Google integration code calls `IAuditLogService.LogGoogleSyncAsync(...)`, which records the Google resource id, the user email at the time of the call, the role granted/revoked, the `GoogleSyncSource`, the success flag, and (on failure) the error message.
- **No cleanup trigger:** there is no retention or pruning job; the database triggers reject DELETE in every case.

## Cross-Section Dependencies

Nearly every other section **writes** into this section via `IAuditLogService`. This section depends on almost nothing:

- **Users (display lookup):** `AuditLogRepository.GetUserDisplayNamesAsync` reads `ctx.Users` directly to batch-resolve actor and subject display names. This is a sanctioned read-only cross-table lookup that lives behind the repository so controllers and view components never touch other domains' tables.
- **Teams (display lookup):** `AuditLogRepository.GetTeamNamesAsync` reads `ctx.Teams` directly to batch-resolve team name + slug for entries that reference a team.
- **GoogleResource (FK):** the `ResourceId` column FKs to `google_resources` (`OnDelete: SetNull`) and the entity exposes a `Resource` nav used by `GetGoogleSyncByUserAsync`.
- **GDPR (`IUserDataContributor`):** `AuditLogService` contributes per-user audit slices to the GDPR export orchestrator via `ContributeForUserAsync`.

No other cross-section writes from this section outward. Audit is a sink.

## Architecture

**Owning services:** `AuditLogService`
**Owned tables:** `audit_log`
**Status:** (A) Migrated (issue nobodies-collective/Humans#552).

- `AuditLogService` lives in `Humans.Application.Services.AuditLog/` and depends only on Application-layer abstractions (no `DbContext`, no `IMemoryCache`).
- `IAuditLogRepository` (impl `Humans.Infrastructure/Repositories/AuditLog/AuditLogRepository.cs`, sealed) is the only non-test file that touches `DbContext.AuditLogEntries`. Uses `IDbContextFactory<HumansDbContext>` with short-lived contexts per call.
- **Decorator decision — no caching decorator (§15 Option A).** Writes are scattered across every section (~96 call sites at migration time); reads are admin-only and already filtered server-side by index. No benefit from a section-owned cache.
- **Append-only enforcement:** two-layer — the architecture test `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods` reflects over `IAuditLogRepository` and fails the build if any `Update*` / `Delete*` / `Remove*` method is added; the Postgres triggers `prevent_audit_log_update` and `prevent_audit_log_delete` enforce the same constraint at the database.
- **Cross-domain navs on the entity:** `ActorUserId` has an `ActorUser` nav and `ResourceId` has a `Resource` nav (both with `set` accessors as required by EF Core for FK relationships). The Google sync read path uses `Include(e => e.Resource)`. Batch display-name lookups for actors and subjects go through `AuditLogRepository.GetUserDisplayNamesAsync` / `GetTeamNamesAsync`, which read `users` and `teams` directly inside the Audit Log section.

### Touch-and-clean guidance

- When adding a new `AuditAction` enum value, pair it with a one-line entry in the section list above. Reviewers should be able to grep the enum value to find the single writer.
- Do **not** call `IAuditLogService.LogAsync` before the business save. Audit goes after, always.
- Do **not** add an `Update*` / `Delete*` / `Remove*` method to `IAuditLogRepository`; the architecture test will fail and the database triggers will reject the operation regardless.
- Do **not** attempt to log inside an outer transaction expecting rollback — audit commits independently via its own `DbContext`.
- Do **not** read `audit_log` from outside this section. New admin dashboards extend `IAuditLogService` with narrow filtered-query methods instead of joining the table.
- Do **not** confuse `audit_log` with `budget_audit_logs` — the Budget section owns its own append-only field-level log (`BudgetAuditLog`), rendered at `/Finance/AuditLog`. That is a separate table, separate service, and not part of this section.
