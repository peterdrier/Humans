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

No repository. Pure read-aggregation over owning services.

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `NotificationMeters` (`CountsCacheKey`) | 2 min | yes | yes | none (TTL only; team join-request count is read live from `ITeamService`) |

Cross-section calls via `IUserService`, `IGoogleSyncService`,
`ITeamService`, `ITicketSync`, `IGovernanceService`,
`IConsentService`, `ICampService`. **No direct DB access** — every counter fans out
through an owning-service interface call. The per-lead camp join-request
count is not cached here: it reads Camps' in-RAM `CampInfo` projection,
which Camps evicts on its own writes.

### NotificationInboxRead (Scoped)

No repository. Implements `INotificationInboxRead` (`Humans.Notifications.Contracts`), the
snapshot Backdoor serves at `/api/backdoor/notifications`: the unread tab
of `NotificationInboxService.GetInboxAsync` plus
`NotificationMeterProvider.GetMetersForUserAsync`, both same-section.

---


