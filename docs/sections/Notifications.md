# Notifications — Section Invariants

In-app notification fan-out (stored events + per-user inbox) and live meter counts (computed, not stored). Cross-cut fan-in section: every other section dispatches through `INotificationService` on state changes.

## Concepts

- A **Notification** is a stored event record (`notifications` table) with a templated title + body, a category, and optional deep-link target. Created by business services when something happens the human should know about.
- A **Notification Recipient** is a per-user delivery record (`notification_recipients` table) linking a notification to a user with read/unread state.
- The **Notification Inbox** is the authenticated user's view of their unread + recent notifications. Lives at `/Notifications`.
- A **Notification Meter** is a *live count* of pending work items (e.g. pending tier applications, unvoted board items, unreviewed signups). **Meters are not stored** — they are computed on demand from each owning section's service, cached in `IMemoryCache` with short TTLs, and exposed via `INotificationMeterProvider`. See [project_notification_meters memory note] for the two-lifecycle pattern.

## Data Model

### Notification

**Table:** `notifications`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Category | NotificationCategory | Enum stored as string |
| Title | string (200) | Templated title (post-render) |
| Body | string (4000) | Templated body (post-render) |
| DeepLinkUrl | string (500)? | Optional in-app navigation target |
| CreatedAt | Instant | When emitted |
| RelatedEntityKind | string? | Free-form kind tag for filtering |
| RelatedEntityId | Guid? | Optional FK-like pointer to the domain entity |

### NotificationRecipient

**Table:** `notification_recipients`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| NotificationId | Guid | FK → Notification (Cascade) |
| UserId | Guid | FK → User — **FK only**, no nav |
| ReadAt | Instant? | Set when the user marks as read |
| DismissedAt | Instant? | Set when the user dismisses |

**Indexes:** `(UserId, ReadAt)` for inbox queries; `NotificationId`.

### NotificationCategory

Category taxonomy for routing + filtering. The exact enum values should be kept in sync with `Humans.Domain.Notifications.NotificationCategory` — representative entries: `RoleAssignmentChanged`, `TierApplicationSubmitted`, `TierApplicationApproved`, `FeedbackReceived`, `ShiftSignupApproved`, `AccountSuspended`, etc.

### Meter interface (no table)

`INotificationMeterProvider` exposes per-meter live counts. Each meter is a delegate that reads its owning section's service (`IApplicationDecisionService.GetPendingCountAsync`, `IProfileService.GetReviewQueueCountAsync`, `ITeamService.GetPendingJoinRequestCountAsync`, etc.) and returns an int. Meters are cached via `IMemoryCache` with short TTLs (~30–60 s) inside view components; invalidations from owning sections route through `INotificationMeterCacheInvalidator`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View own notification inbox (`/Notifications`). Mark individual notifications as read / dismissed. Mark all as read |
| Admin | View system-wide notification diagnostics (if implemented). No other special surface |
| Any service / job | Emit notifications via `INotificationService.SendToUserAsync(userId, …)` or `SendToRoleAsync(roleName, …)` |

## Invariants

- Notifications are stored events — once written, they are persisted until cleaned up by `CleanupNotificationsJob` (retention policy: ~N days per category, TBD by config).
- Meters are **never** stored. `INotificationMeterProvider` computes them from each owning section's public service; do not add a `meter_counts` table.
- Every notification has at least one recipient row. Emit calls without recipients are rejected.
- `INotificationService.SendToRoleAsync(role, …)` resolves recipients via `IRoleAssignmentService.GetActiveUserIdsForRoleAsync` — never via `DbContext.RoleAssignments` directly.
- Notification fan-out is fire-and-forget from the caller's perspective: the emit is best-effort, logged at Error on failure, and never throws back to the business-save path.
- In-app notifications and email notifications are separate surfaces — emitting a notification does not automatically queue an email. Sections that need both send both.

## Negative Access Rules

- Regular humans **cannot** read another user's inbox or see another user's unread count.
- Sections that own meters **cannot** query the `notifications` table to back their meter — they return counts from their own service. (A meter that reads `notifications` would be a stored count, not a live count, and would drift.)
- Services **cannot** bypass `INotificationService` to write `notifications` / `notification_recipients` directly.

## Triggers

- When a section's state changes in a way that should surface to users, that section calls `INotificationService.SendToUserAsync` or `SendToRoleAsync` inline in the write path (after the business save — same ordering as audit, design-rules §7a).
- When a user marks a notification as read, only `NotificationRecipient.ReadAt` is updated. The `Notification` row is never mutated after creation (append-only semantic for the shared event, per-recipient state on the join row).
- The nav-badge count and per-section meter badges invalidate via `INavBadgeCacheInvalidator` / `INotificationMeterCacheInvalidator` after any write that changes an owning-section count.

## Cross-Section Dependencies

- **Auth:** `IRoleAssignmentService.GetActiveUserIdsForRoleAsync` — role-scoped fan-out.
- **Profiles / Users:** `IUserService` — display data for sender/recipient rendering (when needed). `IProfileService` — profile-review-queue count for one meter.
- **Governance:** `IApplicationDecisionService` — pending-application + unvoted counts feed meters.
- **Teams:** `ITeamService` — pending-join-request count feeds a meter.
- **Tickets:** `ITicketSyncService` — sync-state count feeds an admin meter.
- **Google Integration:** `IGoogleSyncService` — outbox-depth count feeds an admin meter.

This section is **fan-in**: almost every other section calls in, but this section only reads back a small, narrow slice of count methods from each. It does not aggregate-join.

## Architecture

**Owning services:** `NotificationService`, `NotificationInboxService`, `NotificationMeterProvider`, `NotificationEmitter`, `NotificationRecipientResolver`
**Owned tables:** `notifications`, `notification_recipients`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#550, 2026-04-22).

- All services live in `Humans.Application.Services.Notifications/` and depend only on Application-layer abstractions.
- `INotificationRepository` (impl `Humans.Infrastructure/Repositories/NotificationRepository.cs`) is the only non-test file that touches `notifications` / `notification_recipients` via `DbContext`.
- **Decorator decision — no caching decorator.** Dispatch is fire-and-forget and inbox reads are per-user and per-request-rate. Meter reads are cached at the view-component layer via short-TTL `IMemoryCache`, not via a section-owned decorator.
- **Cross-section reads** for meter counts routed through `IProfileService.GetReviewQueueCountAsync`, `IUserService.GetDuplicateCandidateCountAsync`, `IGoogleSyncService.GetOutboxDepthAsync`, `ITeamService.GetPendingJoinRequestCountAsync`, `ITicketSyncService.GetSyncStateCountAsync`, `IApplicationDecisionService.GetPendingCountAsync` (and unvoted/admin-stats equivalents). `IRoleAssignmentService.GetActiveUserIdsForRoleAsync` was added so `NotificationService.SendToRoleAsync` doesn't query `role_assignments`.
- **Cleanup:** `CleanupNotificationsJob` also goes through `INotificationRepository`.
- **Cross-domain navs:** `NotificationRecipient.User` is **FK only** (no nav declared) — recipient display data resolves via `IUserService.GetByIdsAsync` in the inbox composer.

### Touch-and-clean guidance

- Do **not** add new `DbContext.Notifications` / `DbContext.NotificationRecipients` reads outside this section. New notification shapes go behind new methods on `INotificationService` or `INotificationInboxService`.
- Do **not** introduce a stored meter. Every meter is a delegate that calls the owning section's service. If a count is too expensive to compute on demand, fix the owning section (add a narrow, indexable count method to its repository) — do not persist a denormalised counter.
- When adding a new notification category, pair the emission with a decision about whether a meter should track it. If yes, add a count method to the owning section's service and wire it into `NotificationMeterProvider`; if no, ensure `/Notifications` filtering handles the new category.
- Fan-out failures must be swallowed after a log — do not let a notification failure abort the business write (design-rules §7a analogue).
