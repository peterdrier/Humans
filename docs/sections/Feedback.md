<!-- freshness:triggers
  src/Humans.Application/Services/Feedback/**
  src/Humans.Domain/Entities/FeedbackReport.cs
  src/Humans.Domain/Entities/FeedbackMessage.cs
  src/Humans.Infrastructure/Data/Configurations/Feedback/**
  src/Humans.Infrastructure/Repositories/FeedbackRepository.cs
  src/Humans.Web/Controllers/FeedbackController.cs
  src/Humans.Web/Controllers/FeedbackApiController.cs
-->
<!-- freshness:flag-on-change
  Feedback report lifecycle, screenshot validation, message thread invariants, and admin-vs-reporter authorization — review when Feedback service/entities/controllers change.
-->

# Feedback — Section Invariants

In-app feedback reports (bugs, feature requests, questions) with screenshots and a reporter↔admin conversation thread.

## Concepts

- A **Feedback Report** is an in-app submission from a human — a bug report, feature request, or question. It captures the page URL, optional screenshot, and conversation thread between the reporter and admins.
- **Feedback status** tracks the lifecycle: Open, Acknowledged, Resolved, or WontFix.

## Data Model

### FeedbackReport

**Table:** `feedback_reports`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User (reporter), Cascade on delete — **FK only**, `[Obsolete]`-marked nav |
| Category | FeedbackCategory | Bug, FeatureRequest, Question |
| Description | string | Feedback text (max 5000) |
| PageUrl | string | URL where feedback was submitted (max 2000) |
| UserAgent | string? | Browser user agent string (max 1000) |
| AdditionalContext | string? | Extra context captured at submission (e.g., reporter's roles) (max 2000) |
| ScreenshotFileName | string? | Original filename (max 256) |
| ScreenshotStoragePath | string? | Relative path under `wwwroot/uploads/feedback/` (max 512) |
| ScreenshotContentType | string? | MIME type (`image/jpeg`, `image/png`, `image/webp`) (max 64) |
| Status | FeedbackStatus | Open, Acknowledged, Resolved, WontFix |
| GitHubIssueNumber | int? | Linked GitHub issue |
| LastReporterMessageAt | Instant? | Timestamp of the most recent reporter message (drives "needs reply") |
| LastAdminMessageAt | Instant? | Timestamp of the most recent admin message (drives "needs reply") |
| AssignedToUserId | Guid? | FK → User, SetNull on delete — **FK only**, `[Obsolete]`-marked nav |
| AssignedToTeamId | Guid? | FK → Team, SetNull on delete — **FK only**, `[Obsolete]`-marked nav |
| CreatedAt | Instant | Submission timestamp |
| UpdatedAt | Instant | Last modification |
| ResolvedAt | Instant? | When resolved/won't-fix |
| ResolvedByUserId | Guid? | FK → User, SetNull on delete — **FK only**, `[Obsolete]`-marked nav |

**Indexes:** `Status`, `CreatedAt`, `UserId`, `AssignedToUserId`, `AssignedToTeamId`.

### FeedbackMessage

**Table:** `feedback_messages`

Conversation thread between reporter and admins. Aggregate-local (same section as FeedbackReport). `FeedbackReport.Messages ↔ FeedbackMessage.FeedbackReport` is a legal `.Include` inside the repository.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| FeedbackReportId | Guid | FK → FeedbackReport, Cascade on delete |
| SenderUserId | Guid? | FK → User, SetNull on delete — null when posted via API key (no user session) |
| Content | string | Message body (max 5000) |
| CreatedAt | Instant | When the message was posted |

There is no per-message admin/reporter flag — admin-vs-reporter is derived by comparing `SenderUserId` to `FeedbackReport.UserId`, and report-level "needs reply" is derived from `LastReporterMessageAt` vs `LastAdminMessageAt`.

**Indexes:** `FeedbackReportId`, `CreatedAt`.

Cross-domain nav `FeedbackMessage.SenderUser` is `[Obsolete]`-marked — senders resolve via `IUserService.GetByIdsAsync`.

### FeedbackCategory

| Value | Description |
|-------|-------------|
| Bug | Bug report |
| FeatureRequest | Feature request |
| Question | General question |

### FeedbackStatus

| Value | Description |
|-------|-------------|
| Open | New, unreviewed |
| Acknowledged | Admin has seen it |
| Resolved | Fixed or addressed |
| WontFix | Will not be addressed |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Submit feedback (with optional screenshot). View and reply to their own feedback reports. Accessible even during onboarding (before becoming an active member) |
| FeedbackAdmin, Admin | View all feedback reports. Update status (`PolicyNames.FeedbackAdminOrAdmin`). Assign to humans and/or teams (`PolicyNames.FeedbackAdminOrAdmin`). Link GitHub issues (`PolicyNames.FeedbackAdminOrAdmin`). Reply to any report (admin replies queue an email and dispatch an in-app notification to the reporter) |
| API (key auth) | List, get, post messages, update status, update assignment, set GitHub issue via `/api/feedback` (no user session required; `ApiKeyAuthFilter` enforces the key) |

## Invariants

- Every feedback report is linked to the human who submitted it.
- Screenshots are validated for allowed file types (JPEG, PNG, WebP) and a max size of 10 MB before storage.
- Feedback status flows Open → Acknowledged → Resolved or WontFix; transitioning out of a terminal status (Resolved/WontFix) clears `ResolvedAt` and `ResolvedByUserId`.
- Regular humans can only see their own feedback reports. FeedbackAdmin and Admin can see all reports.
- "Needs reply" is derived: true when the reporter has posted a message more recent than any admin reply (`LastReporterMessageAt > LastAdminMessageAt`) or when the report is still Open and no admin has ever replied. The nav-badge count uses the same rule and excludes Resolved/WontFix.
- A report can optionally be assigned to a human and/or a team. Both assignments are independent and nullable.
- Status changes and assignment changes are audit-logged via `AuditAction.FeedbackStatusChanged` and `AuditAction.FeedbackAssignmentChanged`. API-initiated changes are logged with actor `"API"`.
- Admin replies send the response email **before** persisting the new message — if SMTP throws, the message and `LastAdminMessageAt` are never committed, so the request can be retried without duplicating the admin reply. The in-app notification is best-effort post-save.

## Negative Access Rules

- Regular humans **cannot** view other humans' feedback reports.
- Regular humans **cannot** update feedback status, assign reports, link GitHub issues, or post replies on reports they did not submit.
- FeedbackAdmin **cannot** perform system administration tasks — their elevated access is scoped to feedback only.

## Triggers

- When an admin posts a message on a report, the reporter's effective notification email is resolved via `IUserEmailService.GetNotificationTargetEmailsAsync` and a localized response email is queued via `IEmailService.SendFeedbackResponseAsync`. After the message is persisted, an in-app `NotificationSource.FeedbackResponse` notification is also dispatched.
- When a report is created or any message is posted (admin or reporter), the nav-badge cache is invalidated via `INavBadgeCacheInvalidator`.

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetByIdsAsync` — reporter / assignee / resolver / message-sender display names.
- **Profiles:** `IUserEmailService.GetNotificationTargetEmailsAsync` — resolves the effective notification email for a report's reporter when an admin posts a reply (added in PR for issue nobodies-collective/Humans#549).
- **Teams:** `ITeamService.GetTeamNamesByIdsAsync` — assigned-team display names.
- **Email:** `IEmailService.SendFeedbackResponseAsync` — admin-reply emails (the production binding is `OutboxEmailService`, so the email is queued through the email outbox).
- **Notifications:** `INotificationService.SendAsync` — `NotificationSource.FeedbackResponse` in-app notification dispatched after an admin reply is persisted.
- **Audit Log:** `IAuditLogService.LogAsync` — status and assignment changes (`AuditAction.FeedbackStatusChanged`, `AuditAction.FeedbackAssignmentChanged`).
- **Caching:** `INavBadgeCacheInvalidator` — invalidated whenever the actionable count could have changed.
- **GDPR:** implements `IUserDataContributor` to export the reporter's feedback reports and message contents under `GdprExportSections.FeedbackReports`.
- **Admin:** GitHub issue linking connects feedback reports to the external issue tracker.
- **Onboarding:** Feedback submission is available during onboarding, before the human is an active member.

## Architecture

**Owning services:** `FeedbackService`
**Owned tables:** `feedback_reports`, `feedback_messages`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#549, 2026-04-22).

- `FeedbackService` lives in `Humans.Application.Services.Feedback` and depends only on Application-layer abstractions. It never imports `Microsoft.EntityFrameworkCore`.
- `IFeedbackRepository` (impl `Humans.Infrastructure/Repositories/Feedback/FeedbackRepository.cs`) owns the SQL surface. Registered as Singleton and uses `IDbContextFactory<HumansDbContext>` to create per-call scoped contexts, so the repository can be a long-lived singleton while EF state stays per-request.
- **Aggregate-local navs kept:** `FeedbackReport.Messages ↔ FeedbackMessage.FeedbackReport`. Both sides live in Feedback-owned tables, so `.Include(f => f.Messages)` is legal inside the repository.
- **Decorator decision — no caching decorator.** Feedback reports are per-user and admin-triaged, not a hot bulk-read path (same rationale as Governance / User).
- **Cross-domain navs `[Obsolete]`-marked:** `FeedbackReport.User`, `.ResolvedByUser`, `.AssignedToUser`, `.AssignedToTeam`, `FeedbackMessage.SenderUser`. The repository does not `.Include()` them; the service stitches display data in memory from `IUserService`, `IUserEmailService`, and `ITeamService` (design-rules §6b). Controllers and views continue to read `report.User.DisplayName` etc. under `#pragma warning disable CS0618` until the shared User-entity nav strip lands.
- **Nav-badge cache invalidation** routes through `INavBadgeCacheInvalidator` instead of `IMemoryCache` directly.

### Touch-and-clean guidance

- Do **not** reintroduce `.Include(f => f.User | f.ResolvedByUser | f.AssignedToUser | f.AssignedToTeam)` or `.Include(m => m.SenderUser)` anywhere — new read paths should go through the repository's existing methods (or extend the repository with a new narrowly-shaped query) and stitch display data in `FeedbackService` via the cross-section service interfaces above.
- Aggregate-local `.Include(f => f.Messages)` is fine — `feedback_messages` is Feedback-owned.
- Do **not** inject `IMemoryCache` into `FeedbackService`. Use `INavBadgeCacheInvalidator` (or add a new cross-cutting invalidator interface) for cache-staleness signaling.
- New tables that logically belong to Feedback must be added to `design-rules.md §8`; do not silently grow the section's footprint.
