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

- An **Audit Log Entry** is an append-only record of a single user-initiated action. Captures actor, action, entity kind + id, before/after summary (as free-text), and timestamp.
- **AuditAction** is the cross-section enum of action strings. Every action name is a contract — sections use the shared enum so reviewers can grep "who writes TierApplicationApproved" across the whole codebase.
- **Self-persisting audit** (design-rules §7a): `IAuditLogService.LogAsync` saves each entry immediately via `IAuditLogRepository.AddAsync` → `IDbContextFactory<HumansDbContext>` → `SaveChangesAsync`. Callers do not need to `SaveChanges` to flush audit, and must not expect audit to roll back if a later business step fails.
- **Best-effort** — audit save failures are logged at Error and swallowed. An audit hiccup never fails the business operation that called it.

## Data Model

### AuditLogEntry

Append-only per design-rules §12. Enforced by `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods`.

**Table:** `audit_log_entries`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Action | AuditAction | Enum stored as string |
| EntityKind | string? | Free-form kind (e.g. `"User"`, `"Team"`, `"Application"`) |
| EntityId | Guid? | Optional id of the entity acted on |
| ActorUserId | Guid? | FK → User — **FK only**, no nav. Nullable because system jobs write audit too. |
| RelatedGoogleResourceId | Guid? | FK → GoogleResource — **FK only**, no nav. Present for Google-sync audits. |
| Summary | string? (4000) | Free-text old/new value summary or reason |
| CreatedAt | Instant | When the action occurred (not when audit was flushed — callers stamp via `IClock`) |

**Indexes:** `(ActorUserId, CreatedAt)`, `(EntityKind, EntityId, CreatedAt)`, `(Action, CreatedAt)`.

### AuditAction (cross-section enum)

`AuditAction` is the shared contract across all writers. Representative entries (non-exhaustive):

- **Onboarding / Profile:** `ConsentCheckCleared`, `ConsentCheckFlagged`, `SignupRejected`, `TierDowngraded`, `UserSuspended`, `UserUnsuspended`, `MembershipsRevokedOnDeletionRequest`, `AccountMerged`, `AccountPurged`.
- **Governance:** `TierApplicationSubmitted`, `TierApplicationApproved`, `TierApplicationRejected`, `TierApplicationWithdrawn`, `BoardVoteCast`.
- **Teams:** `TeamMemberAdded`, `TeamMemberRemoved`, `TeamJoinRequestApproved`, `TeamJoinRequestRejected`, `TeamRoleAssigned`, `TeamRoleUnassigned`.
- **Google Integration:** `GoogleGroupMembershipAdded`, `GoogleGroupMembershipRemoved`, `DrivePermissionGranted`, `DrivePermissionRevoked`, `InheritedPermissionsReEnabled`.
- **Calendar:** `CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden`.
- **Email / Feedback / Campaigns / Legal / Auth:** see each section's docs for its audit-writing triggers.

Stored as string via `HasConversion<string>()`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Write audit entries via `IAuditLogService.LogAsync(...)`. No authorization check at the log site — the caller has already authorized the underlying action |
| HumanAdmin, Board, Admin | View audit log (filtered by actor, entity, or action) via the admin audit dashboards |

No one reads audit entries anonymously or as a regular user — all audit dashboards are admin-gated.

## Invariants

- Audit entries are append-only. `IAuditLogRepository` exposes `AddAsync` and `GetXxxAsync` — **no** `UpdateAsync`, **no** `DeleteAsync`. Enforced by `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods`.
- `LogAsync` is self-persisting — each call opens a fresh `DbContext` via `IDbContextFactory`, adds the entry, and calls `SaveChangesAsync`. Callers do not flush audit.
- Audit is called **after** the business save, never before (design-rules §7a). A business rollback never leaves a ghost audit row because audit hasn't written yet.
- Audit commits separately from the business change. The rare failure mode is "business saved, audit did not" — logged loudly, detectable by reconciling row counts, and strictly better than "audit silently vanishes".
- Audit save failures are swallowed after a log at Error. `LogAsync` does not throw back to the caller.
- `ActorUserId` is nullable — system jobs (Hangfire recurring jobs) write audit entries with no actor.

## Negative Access Rules

- Callers **cannot** `UpdateAsync` or `DeleteAsync` an audit entry. The repository exposes no such methods.
- Services **cannot** call `IAuditLogService.LogAsync` inside an outer `DbContext` transaction expecting audit to roll back with it — audit uses its own context via `IDbContextFactory`.
- Services **cannot** bypass `IAuditLogService` and write `audit_log_entries` directly.
- The log **cannot** be pruned by production admins as a routine operation — retention is policy-governed and applied by a single dedicated cleanup job (if configured), not by ad-hoc admin action.

## Triggers

- **On any privileged business write:** the owning section's service calls `IAuditLogService.LogAsync(action, entityKind, entityId, summary, actorId)` after its business `SaveChangesAsync` returns successfully.
- **On Google sync apply:** `GoogleSyncService` writes an audit entry tagged with `RelatedGoogleResourceId` for permission changes.
- **On cleanup (if retention policy configured):** a dedicated job deletes entries older than the retention window. This is the only DELETE path, and it's policy-driven, not caller-driven.

## Cross-Section Dependencies

Nearly every other section **writes** into this section via `IAuditLogService`. This section depends on almost nothing:

- **Users/Identity:** `IUserService.GetByIdsAsync` — display names for actor rendering on admin audit views.
- **Teams → Google Integration:** `RelatedGoogleResourceId` is populated by Google Integration audits.

No other cross-section writes from this section outward. Audit is a sink.

## Architecture

**Owning services:** `AuditLogService`
**Owned tables:** `audit_log_entries`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#552, 2026-04-22).

- `AuditLogService` lives in `Humans.Application.Services.AuditLog/` and depends only on Application-layer abstractions.
- `IAuditLogRepository` (impl `Humans.Infrastructure/Repositories/AuditLogRepository.cs`) is the only file that touches `audit_log_entries` via `DbContext`. Uses `IDbContextFactory<HumansDbContext>` with short-lived contexts per call.
- **Decorator decision — no caching decorator.** Writes are scattered across every section (~96 call sites at migration time); reads are admin-only and already filtered server-side by index. No benefit from a section-owned cache.
- **Append-only enforcement:** `AuditLogArchitectureTests.IAuditLogRepository_HasNoUpdateOrDeleteMethods` pins the shape — the interface declares `AddAsync` + `GetXxxAsync` only. The test reflects over `IAuditLogRepository` and fails the build if an Update or Delete method is ever added.
- **Cross-domain navs:** `ActorUserId` and `RelatedGoogleResourceId` are FK-only — no navigation properties on the entity. Display data resolves via `IUserService` (actors) and `ITeamResourceService` (Google resources).

### Touch-and-clean guidance

- When adding a new `AuditAction` enum value, pair it with a one-line entry in the representative list above. Reviewers should be able to grep the enum value to find the single writer.
- Do **not** call `IAuditLogService.LogAsync` before the business save. Audit goes after, always.
- Do **not** re-introduce `ActorUser` / `RelatedGoogleResource` navigation properties on `AuditLogEntry`. Resolve display via cross-section services.
- Do **not** attempt to log inside an outer transaction expecting rollback — audit commits independently via its own `DbContext`.
- Do **not** read `audit_log_entries` from outside this section. New admin dashboards extend `IAuditLogService` with narrow filtered-query methods instead of joining the table.
