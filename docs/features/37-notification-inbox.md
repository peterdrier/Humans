# Notification Inbox

## Business Context

Admins and coordinators receive many email notifications that lack shared state. When a group notification goes to all coordinators of a team, each sees it in their email independently with no way to know if someone else is already handling it. The notification inbox provides a central "what needs my attention" view with shared resolution for group-targeted items.

## Data Model

### Notification
- `Id` (Guid, PK)
- `Title` (string, max 200) -- short title displayed in the notification row
- `Body` (string, nullable, max 2000) -- optional body text
- `ActionUrl` (string, nullable, max 500) -- link for the action button
- `ActionLabel` (string, nullable, max 50) -- button text, falls back to "View ->"
- `Priority` (NotificationPriority: Normal, High, Critical) -- stored as string
- `Source` (NotificationSource) -- which system generated it, stored as string
- `Class` (NotificationClass: Informational, Actionable) -- stored as string
- `TargetGroupName` (string, nullable, max 100) -- display name for group targets
- `CreatedAt` (Instant)
- `ResolvedAt` (Instant, nullable) -- when resolved, shared across all recipients
- `ResolvedByUserId` (Guid, nullable, FK -> User) -- who resolved it

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
- **Group target** (team/role): one shared Notification, any recipient can resolve for all

## Dispatch Service

`INotificationService` provides three dispatch methods:
- `SendAsync` -- individual users, one notification per user
- `SendToTeamAsync` -- shared notification for all team members
- `SendToRoleAsync` -- shared notification for all users with a role

Dispatch checks `CommunicationPreference.InboxEnabled` and suppresses informational notifications when disabled. Actionable notifications are always delivered.

## UI Components

### Bell Icon (Nav Bar)
Three-state badge: no badge / success dot (informational only) / danger count (actionable). Counts cached 2 minutes per user.

### Popup
340px dropdown from bell, right-aligned. Sections: "Needs your attention" (actionable) then "Recent" (informational). Footer: "Mark all as read" + "See all notifications". Empty state: "You're all caught up."

### Full Inbox Page
Route: `/Notifications`. Search + filter pills (All, Needs action, Shifts, Approvals, Resolved). Card with dark green header, Unread/All tab toggle. Sections: "Needs attention", "Informational", "Resolved".

### Notification Row
Shared between popup and inbox. 3px left border accent by priority/class. Type tags: "Urgent" (danger), "Action" (warning), "Info" (success). Group avatar cluster for group-targeted notifications.

## Cleanup

`CleanupNotificationsJob` runs daily, deletes resolved notifications older than 7 days.

## Authorization

All authenticated users can access their own notifications. No role-based restrictions on the inbox itself -- the dispatch service controls who receives notifications.

## Notification Sources

| Source | Class | Recipients | Category | Trigger |
|--------|-------|------------|----------|---------|
| TeamMemberAdded | Informational | The user | TeamUpdates | User added to a team |
| TeamMemberRemoved | Informational | The user | TeamUpdates | User removed from a team |
| TeamJoinRequestSubmitted | Actionable | Team coordinators | TeamUpdates | User requests to join |
| TeamJoinRequestDecided | Informational | The requester | TeamUpdates | Request approved/rejected |
| ShiftCoverageGap | Actionable | Dept coordinators | VolunteerUpdates | Confirmed count drops below min |
| ShiftSignupChange | Informational | Dept coordinators | VolunteerUpdates | Signup confirmed/bailed |
| ShiftAssigned | Informational | The volunteer | VolunteerUpdates | Coordinator voluntells a shift |
| ConsentReviewNeeded | Actionable | ConsentCoordinator role | System | New human completes consents |
| ApplicationSubmitted | Actionable | Board role | Governance | Tier application submitted |
| ApplicationApproved | Informational | The applicant | Governance | Board approves application |
| ApplicationRejected | Informational | The applicant | Governance | Board rejects application |
| VolunteerApproved | Informational | The user | Governance | Profile cleared for membership |
| ProfileRejected | Informational | The user | System | Admin rejects signup |
| AccessSuspended | Actionable | The user | System | Non-compliance suspension |
| ReConsentRequired | Actionable | All active members | System | Required doc updated |
| LegalDocumentPublished | Actionable | All active members | System | New required doc published |
| FeedbackResponse | Informational | The reporter | System | Admin responds to feedback |
| WorkspaceCredentialsReady | Informational | The user | System | @nobodies.team provisioned |
| SyncError | Actionable | Admin role | System | Google sync failure |
| TermRenewalReminder | Actionable | The user | System | 90 days before term expiry |
| RoleAssignmentChanged | Informational | The user | Governance | Role assigned or ended |
| CampaignReceived | Informational | Campaign recipients | Marketing | Campaign code granted |
| GoogleDriftDetected | Informational | Admin role | System | Reconciliation fixes drift |
| FacilitatedMessageReceived | Informational | Camp leads | FacilitatedMessages | Facilitated message sent |

## Related Features

- Communication Preferences (28) -- InboxEnabled extends existing preference model
- Teams (06) -- AddedToTeam is the first notification migration
- Shift Management -- ShiftCoverageGap and ShiftAssigned notifications
