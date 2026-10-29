<!-- freshness:triggers
  src/Sections/Humans.Notifications/**
  src/Sections/Humans.Notifications.Contracts/**
  src/Sections/Humans.Email/Domain/CommunicationPreference.cs
  src/Sections/Humans.Notifications/Jobs/CleanupNotificationsJob.cs
-->
<!-- freshness:flag-on-change
  Notification entities, dispatch methods, source list, resolution semantics, or InboxEnabled suppression may have shifted.
-->

# Notification Inbox

## Business Context

Admins and coordinators receive many email notifications that lack shared state. When a group notification goes to all coordinators of a team, each sees it in their email independently with no way to know if someone else is already handling it. The notification inbox provides a central "what needs my attention" view with shared resolution for group-targeted items.

## Data Model

### Notification
- `Id` (Guid, PK)
- `Title` (string, max 200) -- short title displayed in the notification row
- `Body` (string, nullable, max 2000) -- optional body text
- `ActionUrl` (string, nullable, max 500) -- link for the action button
- `ActionLabel` (string, nullable, max 50) -- button text; stored only -- not exported, no page renders it
- `Priority` (NotificationPriority: Normal, High, Critical) -- stored as string
- `Source` (string, max 50) -- the emitting section's `NotificationSourceDefinition.Name`
- `SourceKey` (string, nullable, max 128) -- optional correlation key for the specific source entity; enables `ResolveBySourceKeyAsync` auto-resolution
- `Class` (NotificationClass: Informational, Actionable) -- stored as string
- `TargetGroupName` (string, nullable, max 100) -- display name for group targets
- `CreatedAt` (Instant)
- `ResolvedAt` (Instant, nullable) -- when resolved, shared across all recipients
- `ResolvedByUserId` (Guid, nullable) -- who resolved it; bare cross-section id, no FK constraint, no nav

### NotificationRecipient
- Composite PK: (NotificationId, UserId)
- `ReadAt` (Instant, nullable) -- personal read state

### CommunicationPreference (extended)
- `InboxEnabled` (bool, default true) -- controls informational notification suppression

## Notification Classes

| Behavior | Informational | Actionable |
|----------|--------------|------------|
| Dismiss without action | Yes | No |
| Suppress via InboxEnabled pref | Yes | No |
| Email preference | Configurable | Configurable |
| Action URL | Optional | Required |

## Resolution

Resolution lives on `Notification`, not `NotificationRecipient`. When any recipient resolves a notification, it resolves for all recipients. The resolver's name is displayed to all ("Handled by Bob, 2h ago").

Callers decide resolution scope:
- **Individual target**: one Notification per user, each resolves independently
- **Group target** (role): one shared Notification, any recipient can resolve for all

## Dispatch Service

`INotificationService` provides two dispatch methods:
- `SendAsync` -- individual users, one notification per user
- `SendToRoleAsync` -- shared notification for all users with a role

Dispatch checks `CommunicationPreference.InboxEnabled` and suppresses informational notifications when disabled. Actionable notifications skip preference reads entirely on both known-recipient and role-broadcast paths, so a preference lookup failure cannot block delivery. Informational delivery still requires a successful preference read.

## UI Components

### Bell Icon (Nav Bar)
Three-state badge: no badge / success dot (informational only) / danger count (actionable). `NotificationBellViewComponent` calls `NotificationInboxService.GetUnreadBadgeCountsAsync` with the request-abort token. Each read queries current unread, unresolved recipient counts, so a read begun before a mutation cannot cache stale counts afterward. Shared-resolution, merge and retention changes are visible on the next read.

### Popup
340px dropdown from bell, right-aligned. Sections: "Needs your attention" (actionable) then "Recent" (informational). Footer: "Mark all as read" + "See all notifications". Empty state: "You're all caught up."

### Full Inbox Page
Route: `/Notifications`. Search + filter pills (All, Needs action, Shifts, Approvals, Resolved). Card with dark green header, Unread/All tab toggle. Sections: "Needs attention", "Informational", "Resolved".

### Notification Row
Shared between popup and inbox. 3px left border accent by priority/class. Type tags: "Urgent" (danger), "Action" (warning), "Info" (success). Group avatar cluster for group-targeted notifications.

## Cleanup

`CleanupNotificationsJob` runs daily. It deletes resolved notifications older than 7 days, unresolved informational notifications older than 30 days, and unresolved rows of retired sources (currently `ApplicationSubmitted` and `ConsentReviewNeeded`) that have no remaining resolution path. Other actionable unresolved notifications are never auto-deleted. Successful unresolved deletions evict all affected recipients' unread badge counts immediately, even if a later cleanup step fails.

## Authorization

All authenticated users can access their own notifications. No role-based restrictions on the inbox itself -- the dispatch service controls who receives notifications.

## Notification Sources

Each emitting section declares its own sources as `public static readonly NotificationSourceDefinition` fields on its `<Section>NotificationSources` class (a `NotificationSourceContributor`, registered as an `INotificationSourceContributor` singleton). A definition carries the stored name, the `MessageCategory` the InboxEnabled check uses, the inbox filter group it belongs to (Shifts or Approvals) and whether it is retired. The section's own `Docs/<Section>.md` lists its sources with class, recipients and trigger; this document lists none, so a new section adds nothing here.

## Related Features

- Communication Preferences (28) -- InboxEnabled extends existing preference model
- Teams (06) -- AddedToTeam is the first notification migration
- Shift Management -- ShiftCoverageGap and ShiftAssigned notifications
