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
| `FeedbackBadgeCount` | 2 min | yes | yes | yes (in-service) |

Cross-section calls via `IUserService`, `IUserEmailService`,
`ITeamService`, `IEmailService`, the section's own `FeedbackEmails`
builder,
`INotificationEmitter`, `IAuditLogService`, and `IFileStorage`
(screenshot blob deletion during GDPR erasure). Implements `IFeedbackService`, `IFeedbackTriage`
(Backdoor's machine-API triage surface, nobodies-collective/Humans#1128),
`IUserDataContributor`, `IUserMerge`, `IRecurringJob` (minute retry of owned pending reply emails). Reply creation commits its pending flag with the message and report timestamps; email handoff clears only that message flag through the repository. Owns and caches `FeedbackBadgeCount`
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


