# Feedback — Section Invariants

## Concepts

- A **Feedback Report** is an in-app submission from a human — a bug report, feature request, or question. It captures the page URL, optional screenshot, and conversation thread between the reporter and admins.
- **Feedback status** tracks the lifecycle: Open, Acknowledged, Resolved, or WontFix.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | Submit feedback (with optional screenshot). View and reply to their own feedback reports. Accessible even during onboarding (before becoming an active member) |
| FeedbackAdmin, Admin | View all feedback reports. Update status. Assign to humans or teams. Add admin notes. Send email responses to reporters. Link GitHub issues. Reply to any report |
| API (key auth) | Full CRUD on feedback reports via the REST API (no user session required) |

## Invariants

- Every feedback report is linked to the human who submitted it.
- Screenshots are validated for allowed file types (JPEG, PNG, WebP) before storage.
- Feedback status follows: Open then Acknowledged then Resolved or WontFix.
- Regular humans can only see their own feedback reports. FeedbackAdmin and Admin can see all reports.
- A report tracks whether it needs a reply (the reporter sent a message that the admin has not yet responded to).
- A report can optionally be assigned to a human and/or a team. Both assignments are independent and nullable.
- Assignment changes are audit-logged.

## Negative Access Rules

- Regular humans **cannot** view other humans' feedback reports.
- Regular humans **cannot** update feedback status, assign reports, add admin notes, link GitHub issues, or send admin responses.
- FeedbackAdmin **cannot** perform system administration tasks — their elevated access is scoped to feedback only.

## Triggers

- When an admin sends a response, an email is queued to the reporter via the email outbox.

## Cross-Section Dependencies

- **Admin**: GitHub issue linking connects feedback reports to the external issue tracker.
- **Email**: Response emails are queued through the email outbox system.
- **Onboarding**: Feedback submission is available during onboarding, before the human is an active member.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `FeedbackService`
**Owned tables:** `feedback_reports`, `feedback_messages`

## Architecture

Feedback was migrated to the §15 repository pattern in issue #549 (2026-04-22):

- **`FeedbackService`** lives in `Humans.Application.Services.Feedback` and depends only on Application-layer abstractions. It never imports `Microsoft.EntityFrameworkCore`.
- **`IFeedbackRepository`** owns the SQL surface for `feedback_reports` and `feedback_messages`. Its implementation (`Humans.Infrastructure/Repositories/FeedbackRepository.cs`) is Scoped + `HumansDbContext` (mirrors `ApplicationRepository`) because Feedback is admin-only and low-traffic.
- **Aggregate-local navs kept:** `FeedbackReport.Messages` ↔ `FeedbackMessage.FeedbackReport`. Both sides live in Feedback-owned tables, so `.Include(f => f.Messages)` is legal inside the repository.
- **Cross-domain navs `[Obsolete]`-marked:** `FeedbackReport.User`, `.ResolvedByUser`, `.AssignedToUser`, `.AssignedToTeam`, `FeedbackMessage.SenderUser`. The repository does not `.Include()` them; the service stitches display data in memory from `IUserService`, `IUserEmailService`, and `ITeamService` (design-rules §6b "in-memory join"). Controllers and views continue to read `report.User.DisplayName` etc. under `#pragma warning disable CS0618` until the shared User-entity nav strip lands.
- **Nav-badge cache invalidation** routes through `INavBadgeCacheInvalidator` instead of `IMemoryCache` directly.
- **No caching decorator.** Feedback reports are per-user and admin-triaged, not a hot bulk-read path, so a dict-backed decorator isn't warranted (same rationale as Governance / User).

### Cross-section calls

- `IUserService.GetByIdsAsync` — batched reporter / assignee / resolver / message-sender display names.
- `IUserEmailService.GetNotificationTargetEmailsAsync` — resolves the effective notification email for a report's reporter when an admin posts a reply. Added in issue #549.
- `ITeamService.GetTeamNamesByIdsAsync` — assigned-team display names.

### Touch-and-clean guidance

- Do **not** reintroduce `.Include(f => f.User | f.ResolvedByUser | f.AssignedToUser | f.AssignedToTeam)` or `.Include(m => m.SenderUser)` anywhere — new read paths should go through the repository's existing methods (or extend the repository with a new narrowly-shaped query) and stitch display data in `FeedbackService` via the cross-section service interfaces above.
- Aggregate-local `.Include(f => f.Messages)` is fine — `feedback_messages` is Feedback-owned.
- Do **not** inject `IMemoryCache` into `FeedbackService`. Use `INavBadgeCacheInvalidator` (or add a new cross-cutting invalidator interface) for cache-staleness signaling.
- New tables that logically belong to Feedback must be added to `design-rules.md` §8; do not silently grow the section's footprint.
