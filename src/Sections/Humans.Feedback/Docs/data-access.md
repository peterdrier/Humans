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

Cross-section calls via `IUserService`, `ITeamService`, `IAuditLogService`, and
`IFileStorage` (screenshot blob deletion during GDPR erasure). Implements
`IFeedbackService`, `IFeedbackTriage` (Backdoor's machine-API triage surface,
nobodies-collective/Humans#1128), `IUserDataContributor`, and `IUserMerge`.
Reply creation commits its pending flag with the message and report timestamps,
then calls `FeedbackReplyDelivery`. Owns and caches `FeedbackBadgeCount` inside
`GetActionableCountAsync`.

### FeedbackReplyDelivery (Scoped, internal)

Repository: `IFeedbackRepository`. Reads `FeedbackReports` and `FeedbackMessages`;
clears the message's pending-email flag after handoff. No cache.

Implements `IRecurringJob` for the minute retry of owned pending reply emails.
The request handoff and retry pass use the same pipeline, including its static
lock and stable Email delivery id. Cross-section calls use `IUserService`,
`IUserEmailService`, `IEmailService`, `INotificationEmitter`, and `IAuditLogService`;
`FeedbackEmails` builds the section's copy.

### FeedbackEmails (Scoped, internal)

No repository. Pure builder — reads `FeedbackResource` (via
`IStringLocalizer<FeedbackResource>`), writes nothing. Returns
`EmailMessage` values for `FeedbackReplyDelivery` to pass to
`IEmailService.SendAsync`. No DB access, no cache.

### FeedbackEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
registered in `Section.Register`) — builds one sample per template via
`FeedbackEmails` for `/Email/EmailPreview`. No DB access, no cache.

---


