# Notifications — Data Access

## Notifications

Project: `src/Sections/Humans.Notifications`; services under `Services/`,
repository under `Data/`.
**DbContext:**
`NotificationsDbContext`. `NotificationRepository` injects
`IDbContextFactory<NotificationsDbContext>` directly. Owns
`Notifications`, `NotificationRecipients`.

### NotificationService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes (on dispatch) |

Cross-section calls via `INotificationEmitter`, `IRoleAssignmentService`,
`ICommunicationPreferenceService`, `IClock`. Implements `IUserMerge`.

### NotificationEmitter (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes |

Low-level emitter used by `NotificationService` and direct callers
(`TeamService`, `CampService`, `CampRoleService`, `CampContactService`)
that have a single-recipient dispatch already targeted. Cross-section
calls via `ICommunicationPreferenceService`.

### NotificationInboxService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R |
| NotificationRecipients | R/W (read state, dismissal) |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationBadge:{userId}` | 2 min | | | yes (on read/dismiss) |

Cross-section calls via `IUserServiceRead`. Implements `IUserDataContributor`.

### NotificationMeterProvider (Scoped)

No repository. Pure read-aggregation over owning services.

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationMeters` | 2 min | yes | yes | (per `INotificationMeterCacheInvalidator` callers) |
| `NavBadge:CampLeadJoinRequests:{userId}` | 2 min | yes | yes | (per `ICampLeadJoinRequestsBadgeCacheInvalidator`) |

Cross-section calls via `IUserServiceRead`, `IGoogleSyncServiceRead`,
`ITeamServiceRead`, `ITicketSync`, `IApplicationServiceRead`,
`ICampServiceRead`. **No direct DB access** — every counter fans out
through an owning-service interface call.

---


