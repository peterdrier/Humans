<!-- freshness:triggers
  src/Humans.Application/Services/Notifications/**
  src/Humans.Domain/Entities/Notification.cs
  src/Humans.Domain/Entities/NotificationRecipient.cs
  src/Humans.Infrastructure/Data/Configurations/Notifications/**
  src/Humans.Infrastructure/Repositories/NotificationRepository.cs
  src/Humans.Web/Controllers/NotificationController.cs
-->
<!-- freshness:flag-on-change
  Notification fan-out semantics, meter-vs-stored distinction, role-scoped dispatch, and inbox state machine — review when Notifications services/entities/controller change.
-->

# Notifications — Section Invariants

In-app notification fan-out (stored events + per-user inbox) and live meter counts (computed, not stored). Cross-cut fan-in section: every other section dispatches through `INotificationService` on state changes.

## Concepts

- A **Notification** is a stored event record (`notifications` table) with a title, optional body, optional action URL + label, a `Source` (the originating system), a `Class` (Informational vs Actionable), and a `Priority`. Created by business services when something happens the human should know about. Resolution is **shared** across recipients: when any recipient resolves the notification, it resolves for all.
- A **Notification Recipient** is a per-user delivery row (`notification_recipients` table) linking a notification to a user with personal `ReadAt` state. Resolution lives on the parent `Notification`, not here.
- The **Notification Inbox** is the authenticated user's view of their unresolved + recently-resolved notifications. Lives at `/Notifications`. A popup partial at `/Notifications/Popup` powers the bell dropdown.
- A **Notification Meter** is a *live count* of pending work items (e.g. consent reviews pending, applications pending board vote, failed Google sync events). **Meters are not stored** — they are computed on demand from each owning section's service, cached in `IMemoryCache` with a short TTL (~2 min), and exposed via `INotificationMeterProvider`. Meters are role-gated: each meter only renders for users in the matching role.

## Data Model

### Notification

**Table:** `notifications`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Title | string (200) | Display title |
| Body | string (2000)? | Optional body text |
| ActionUrl | string (500)? | Optional URL for the action button |
| ActionLabel | string (50)? | Optional button label (falls back to "View →" in UI) |
| Priority | NotificationPriority | Enum stored as string (50): `Normal`/`High`/`Critical` |
| Source | NotificationSource | Enum stored as string (50) — see below |
| Class | NotificationClass | Enum stored as string (50): `Informational`/`Actionable` |
| TargetGroupName | string (100)? | Display label for group-targeted notifications (e.g. "Coordinators", team name); null for individual targets |
| CreatedAt | Instant | When emitted |
| ResolvedAt | Instant? | Set when any recipient resolves (shared across all recipients) |
| ResolvedByUserId | Guid? | FK → User (`SetNull` on user delete) — who resolved |

**Indexes:** `(CreatedAt)`.

### NotificationRecipient

**Table:** `notification_recipients`

| Property | Type | Purpose |
|----------|------|---------|
| NotificationId | Guid | FK → Notification (Cascade); part of composite PK |
| UserId | Guid | FK → User (Cascade); part of composite PK |
| ReadAt | Instant? | Personal read state — set when this user has seen the notification |

**PK:** composite `(NotificationId, UserId)`. **Indexes:** `IX_NotificationRecipient_UserId` for badge-count queries. `Notification` and `User` navs are declared (recipient display data is still stitched in memory via `IUserService.GetByIdsAsync` rather than `.Include`d cross-domain — see Architecture).

### NotificationSource

The originating system for a notification, mapped to a `MessageCategory` for preference checks. Defined in `Humans.Domain.Enums.NotificationSource`. Current values: `TeamMemberAdded`, `ShiftCoverageGap`, `ShiftSignupChange`, `ConsentReviewNeeded`, `ApplicationSubmitted`, `SyncError`, `TermRenewalReminder`, `ApplicationApproved`, `ApplicationRejected`, `VolunteerApproved`, `ProfileRejected`, `AccessSuspended`, `ReConsentRequired`, `TeamJoinRequestSubmitted`, `TeamJoinRequestDecided`, `FeedbackResponse`, `WorkspaceCredentialsReady`, `RoleAssignmentChanged`, `CampaignReceived`, `TeamMemberRemoved`, `ShiftAssigned`, `GoogleDriftDetected`, `FacilitatedMessageReceived`, `LegalDocumentPublished`, `CampMembershipApproved`, `CampMembershipRejected`, `CampMembershipSeasonClosed`.

### NotificationClass

`Informational` (dismissable, suppressible via per-category InboxEnabled preference) or `Actionable` (requires user action; cannot be dismissed, only resolved).

### NotificationPriority

`Normal`, `High`, or `Critical` — affects visual presentation only.

### Meter interface (no table)

`INotificationMeterProvider.GetMetersForUserAsync(ClaimsPrincipal, …)` returns the meters visible to the current user, filtered by role. Each meter calls into the owning section's service (`IProfileService.GetConsentReviewPendingCountAsync` / `GetNotApprovedAndNotSuspendedCountAsync`, `IUserService.GetPendingDeletionCountAsync`, `IGoogleSyncService.GetFailedSyncEventCountAsync`, `ITeamService.GetTotalPendingJoinRequestCountAsync`, `ITicketSyncService.IsInErrorStateAsync`, `IApplicationDecisionService.GetUnvotedApplicationCountAsync`, `ICampService.GetPendingMembershipCountForLeadAsync`). Aggregate counts are cached in `IMemoryCache` for ~2 minutes (`CacheKeys.NotificationMeters`); per-user counts (board voting, camp lead requests) are cached per-user with the same TTL. Writes elsewhere invalidate via `INotificationMeterCacheInvalidator`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View own notification inbox (`/Notifications`) and popup (`/Notifications/Popup`). Mark individual notifications as read; resolve actionable; dismiss informational; click-through (auto-marks read + redirects); bulk-resolve / bulk-dismiss; mark-all-read |
| Admin / Board / Coordinators | See additional **meters** in inbox + popup, gated by role: Consent Coordinator → consent reviews pending; Board → applications pending your vote; Admin → pending deletions, failed Google sync events, team join requests, ticket sync error; Board + Volunteer Coordinator → onboarding profiles pending; Camp Leads → "N humans want to join your camp" |
| Any service / job | Emit notifications via `INotificationEmitter.SendAsync(source, class, priority, title, recipientUserIds, …)` for known recipient lists, or `INotificationService.SendToTeamAsync(…, teamId, …)` / `SendToRoleAsync(…, roleName, …)` for group dispatch |

## Invariants

- Notifications are stored events — once written, they are persisted until cleaned up by `CleanupNotificationsJob` (resolved older than 7 days; unresolved informational older than 30 days; **actionable notifications are never auto-cleaned** — they represent real work).
- Meters are **never** stored. `INotificationMeterProvider` computes them from each owning section's public service; do not add a `meter_counts` table.
- An emit call with an empty recipient list is logged at Warning and silently skipped (no notification row written). An emit call where every recipient suppresses the notification via `InboxEnabled=false` for the source's `MessageCategory` (Informational only) is logged at Information and silently skipped.
- `INotificationService.SendToRoleAsync(role, …)` resolves recipients via `INotificationRecipientResolver.GetActiveUserIdsForRoleAsync`, which delegates to `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` — never via `DbContext.RoleAssignments` directly.
- `INotificationService.SendToTeamAsync(…, teamId, …)` resolves recipients via `INotificationRecipientResolver.GetTeamNotificationInfoAsync`, which delegates to `ITeamService` — never via `DbContext.Teams`/`TeamMembers` directly.
- `Informational` notifications respect each recipient's `InboxEnabled` preference for the source's `MessageCategory`; `Actionable` notifications always go through (they cannot be suppressed by user preference).
- Resolution is **shared**: when any recipient resolves a notification, `Notification.ResolvedAt` and `ResolvedByUserId` are set once and the resolution is visible to all recipients. Read state (`NotificationRecipient.ReadAt`) remains per-user.
- `Actionable` notifications cannot be dismissed (dismiss requires `Class == Informational` in the repository); they can only be resolved via `Resolve` or click-through into the underlying work item.
- Notification fan-out should be fire-and-forget from the caller's perspective. (Note: the dispatch methods themselves are not `try/catch`-wrapped today — callers wrap the call when needed; see `design-rules` §7a for the current pattern.)
- In-app notifications and email notifications are separate surfaces — emitting a notification does not automatically queue an email. Sections that need both send both.

## Negative Access Rules

- Regular humans **cannot** read another user's inbox or see another user's unread count. All inbox/popup/action endpoints scope to the authenticated user's `userId`.
- A user who is **not** a recipient of a notification cannot resolve, dismiss, mark-read, or click-through it — the repository returns `Forbidden` and the controller maps to a 403.
- Actionable notifications **cannot** be dismissed (only resolved). The `BulkDismiss` and `Dismiss` paths reject actionable rows.
- Sections that own meters **cannot** query the `notifications` table to back their meter — they return counts from their own service. (A meter that reads `notifications` would be a stored count, not a live count, and would drift.)
- Services **cannot** bypass `INotificationService` / `INotificationEmitter` to write `notifications` / `notification_recipients` directly. `INotificationRepository` is the only non-test type allowed to touch these tables.

## Triggers

- When a section's state changes in a way that should surface to users, that section calls `INotificationEmitter.SendAsync` (known recipients), `INotificationService.SendToTeamAsync`, or `INotificationService.SendToRoleAsync` inline in the write path (after the business save — same ordering as audit, design-rules §7a).
- When a user resolves or click-throughs a notification, the per-recipient `ReadAt` is set (if not already) and the shared `Notification.ResolvedAt` + `ResolvedByUserId` are set. Subsequent mutations are no-ops (idempotent).
- After every successful send, resolve, dismiss, mark-read, mark-all-read, or click-through, the per-user `CacheKeys.NotificationBadgeCounts(userId)` entry is removed for each affected user (the `NotificationBellViewComponent` re-computes on next render). Dispatch and inbox services hold `IMemoryCache` directly for this — they do not route through `INavBadgeCacheInvalidator`.
- Per-section meter caches invalidate via `INotificationMeterCacheInvalidator` after any write that changes an owning-section count (called from owning sections such as `ApplicationDecisionService`, `CachingProfileService`, etc.).
- `INotificationInboxService.ResolveBySourceAsync(userId, source)` exists for sections that need to auto-resolve all open notifications of a given source for a user when the underlying condition is fixed (e.g. resolving `AccessSuspended` when consents are completed).

## Cross-Section Dependencies

- **Auth:** `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` (via `INotificationRecipientResolver`) — role-scoped fan-out.
- **Teams:** `ITeamService.GetTeamByIdAsync` + `GetTeamMembersAsync` (via `INotificationRecipientResolver`) — team-scoped fan-out. `ITeamService.GetTotalPendingJoinRequestCountAsync` — admin team-join-requests meter.
- **Profiles / Users:** `IUserService.GetByIdsAsync` — display data for resolver + recipient rendering (stitched in memory). `IUserService.GetPendingDeletionCountAsync` — admin pending-deletions meter. `IProfileService.GetConsentReviewPendingCountAsync` + `GetNotApprovedAndNotSuspendedCountAsync` — consent-review and onboarding-pending meters.
- **Governance:** `IApplicationDecisionService.GetUnvotedApplicationCountAsync(boardMemberUserId)` — per-board-member voting meter.
- **Tickets:** `ITicketSyncService.IsInErrorStateAsync` — admin ticket-sync-error meter.
- **Google Integration:** `IGoogleSyncService.GetFailedSyncEventCountAsync` — admin failed-sync-events meter.
- **Camps:** `ICampService.GetPendingMembershipCountForLeadAsync(userId)` — per-camp-lead pending-requests meter.
- **Communication preferences:** `ICommunicationPreferenceService.GetUsersWithInboxDisabledAsync` — filters out informational notifications for users who suppressed the source's `MessageCategory`.
- **GDPR:** `NotificationInboxService` implements `IUserDataContributor` (via `INotificationRepository.GetAllForUserContributorAsync`) for the GDPR export.

This section is **fan-in**: almost every other section calls in, but this section only reads back a small, narrow slice of count + recipient methods from each. It does not aggregate-join.

## Architecture

**Owning services:** `NotificationService`, `NotificationEmitter`, `NotificationRecipientResolver`, `NotificationInboxService`, `NotificationMeterProvider`
**Owned tables:** `notifications`, `notification_recipients`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#550, 2026-04-22).

- All services live in `Humans.Application.Services.Notifications/` and depend only on Application-layer abstractions.
- `INotificationRepository` (impl `Humans.Infrastructure/Repositories/Notifications/NotificationRepository.cs`) is the only non-test file that touches `notifications` / `notification_recipients` via `DbContext`. The repository uses `IDbContextFactory<HumansDbContext>` so it can be registered as **Singleton** while `HumansDbContext` remains Scoped.
- **DI cycle break — `INotificationEmitter` vs `INotificationService`.** `INotificationEmitter` is the narrow outbound interface (`SendAsync` to a known recipient list). `INotificationService` extends it with `SendToTeamAsync` + `SendToRoleAsync`, which require `INotificationRecipientResolver` (which itself depends on `ITeamService` + `IRoleAssignmentService`). Since `ITeamService` and `IRoleAssignmentService` already depend on the notification surface, they inject the narrower `INotificationEmitter` (implemented by a separate `NotificationEmitter` type, **not** `NotificationService`) to avoid closing the DI cycle.
- **Decorator decision — no caching decorator.** Dispatch is fire-and-forget and inbox reads are per-user and per-request-rate. Per-user unread badge counts are cached in the `NotificationBellViewComponent` via short-TTL `IMemoryCache` (~2 min) keyed by user; meter aggregates are cached inside `NotificationMeterProvider` itself with the same TTL. Both are invalidated in-band by the dispatch / inbox services after every write.
- **Cross-section reads** for meter counts route through `IProfileService.GetConsentReviewPendingCountAsync` + `GetNotApprovedAndNotSuspendedCountAsync`, `IUserService.GetPendingDeletionCountAsync`, `IGoogleSyncService.GetFailedSyncEventCountAsync`, `ITeamService.GetTotalPendingJoinRequestCountAsync`, `ITicketSyncService.IsInErrorStateAsync`, `IApplicationDecisionService.GetUnvotedApplicationCountAsync`, and `ICampService.GetPendingMembershipCountForLeadAsync`. `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` powers `SendToRoleAsync` so it doesn't query `role_assignments` directly.
- **Cleanup:** `CleanupNotificationsJob` is registered with Hangfire as `cleanup-notifications` on cron `30 4 * * *` (daily at 04:30 UTC). It goes through `INotificationRepository.DeleteResolvedOlderThanAsync` (7-day cutoff) and `DeleteUnresolvedInformationalOlderThanAsync` (30-day cutoff). Actionable unresolved notifications are never auto-deleted.
- **Cross-domain navs:** `NotificationRecipient.User` and `Notification.ResolvedByUser` navs are declared in EF, but the read-path repository methods deliberately **do not** `.Include` them — recipient + resolver display names resolve via `IUserService.GetByIdsAsync` in `NotificationInboxService` (design-rules §6).

### Routes

`NotificationController` is `[Authorize]` and rooted at `/Notifications`:

| Verb | Route | Purpose |
|------|-------|---------|
| GET  | `/Notifications` | Inbox view (`search`, `filter`, `tab` query params) |
| GET  | `/Notifications/Popup` | Bell-popup partial |
| POST | `/Notifications/Resolve/{id}` | Resolve actionable (recipient-only) |
| POST | `/Notifications/Dismiss/{id}` | Dismiss informational (recipient-only; rejects actionable) |
| POST | `/Notifications/MarkRead/{id}` | Mark single as read |
| POST | `/Notifications/MarkAllRead` | Mark all unread as read |
| POST | `/Notifications/BulkResolve` | Bulk-resolve actionable rows from `selectedIds` |
| POST | `/Notifications/BulkDismiss` | Bulk-dismiss informational rows from `selectedIds` |
| GET  | `/Notifications/ClickThrough/{id}` | Mark read + redirect to `ActionUrl` (LocalUrl-checked) |

All POST routes are `[ValidateAntiForgeryToken]`. Authorization is "must be a recipient" — enforced by `INotificationRepository` returning `Forbidden` when the actor's `UserId` is not in the notification's recipient set; the controller maps to 403 / `Forbid()`.

### Touch-and-clean guidance

- Do **not** add new `DbContext.Notifications` / `DbContext.NotificationRecipients` reads outside this section. New notification shapes go behind new methods on `INotificationService` / `INotificationEmitter` / `INotificationInboxService`.
- Do **not** introduce a stored meter. Every meter is a delegate that calls the owning section's service. If a count is too expensive to compute on demand, fix the owning section (add a narrow, indexable count method to its repository) — do not persist a denormalised counter.
- When adding a new `NotificationSource`, pair the emission with a decision about whether a meter should track it. If yes, add a count method to the owning section's service and wire it into `NotificationMeterProvider`; if no, ensure `/Notifications` filtering handles the new source. Update `NotificationSource → MessageCategory` mapping so preference suppression works.
- Callers of dispatch should treat it as fire-and-forget; if you need to swallow exceptions, do it at the call site after a log (design-rules §7a analogue).
