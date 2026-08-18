# Feedback — Data Access

## Feedback

Project: `src/Sections/Humans.Feedback` — services under `Services/`,
repository under `Data/`. **DbContext:** `FeedbackDbContext`.
`FeedbackRepository` injects `IDbContextFactory<FeedbackDbContext>`
directly. Owns `FeedbackReports`, `FeedbackMessages`.

### FeedbackService (Scoped)

Repository: `IFeedbackRepository`.

| Table | R/W |
|-------|-----|
| FeedbackReports | R/W |
| FeedbackMessages | R/W |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `FeedbackBadgeCount` | 2 min | yes | yes | yes (via `INavBadgeCacheInvalidator`) |

Cross-section calls via `IUserServiceRead`, `IUserEmailService`,
`ITeamServiceRead`, `IEmailService`, `IEmailMessageFactory`,
`INotificationEmitter`,
`IAuditLogService`. Implements `IFeedbackServiceRead`,
`IUserDataContributor`, `IUserMerge`. Owns and caches `FeedbackBadgeCount`
inside `GetActionableCountAsync`.

---


