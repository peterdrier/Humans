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

Resolves role holders through `IRoleAssignmentService`, then delegates all emission to the
section-internal `NotificationEmitter`. Its only repository write is merge reassignment.
Implements `IUserMerge`.

### NotificationEmitter (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W |
| NotificationRecipients | R/W |

Owns preference filtering, copy normalization and persistence for both individual delivery
and shared role broadcasts. `NotificationService` resolves role recipients; direct callers
use `INotificationEmitter` with their known recipient lists. Both resolve the same scoped
emitter. Cross-section calls via `ICommunicationPreferenceService`.

### NotificationInboxService (Scoped)

Repository: `INotificationRepository`.

| Table | R/W |
|-------|-----|
| Notifications | R/W (shared resolution/dismissal) |
| NotificationRecipients | R/W (per-user read state) |

Resolve/dismiss share class-scoped single and bulk repository writes; the service selects the eligible class. Unread badge counts query this repository directly on each read.

Cross-section calls via `IUserService`. Implements `IUserDataContributor`.

### NotificationMeterProvider (Scoped)

No repository and no cache. Collects every registered `INotificationMeterContributor`'s meters for the
viewer, highest priority first; each section computes and caches its own counts.

### NotificationInboxRead (Scoped)

No repository. Implements `INotificationInboxRead` (`Humans.Notifications.Contracts`), the
snapshot Backdoor serves at `/api/backdoor/notifications`: the unread tab
of `NotificationInboxService.GetInboxAsync` plus
`NotificationMeterProvider.GetMetersForUserAsync`, both same-section.

---


