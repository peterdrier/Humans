<!-- freshness:triggers
  src/Sections/Humans.Feedback/Services/**
  src/Sections/Humans.Feedback/Data/**
  src/Sections/Humans.Feedback/Section.cs
-->

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
`ITeamServiceRead`, `IEmailService`, the section's own `FeedbackEmails`
builder,
`INotificationEmitter`, `IAuditLogService`, and `IFileStorage`
(screenshot blob deletion during GDPR erasure). Implements `IFeedbackServiceRead`, `IFeedbackTriage`
(Backdoor's machine-API triage surface, nobodies-collective/Humans#1128),
`IUserDataContributor`, `IUserMerge`. Owns and caches `FeedbackBadgeCount`
inside `GetActionableCountAsync`.

### FeedbackEmails (Scoped, internal)

No repository. Pure builder — reads `FeedbackResource` (via
`IStringLocalizer<FeedbackResource>`), writes nothing. Returns
`EmailMessage` values for `FeedbackService` to pass to
`IEmailService.SendAsync`. No DB access, no cache.

### FeedbackEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
registered in `Section.Register`) — builds one sample per template via
`FeedbackEmails` for `/Email/EmailPreview`. No DB access, no cache.

---


