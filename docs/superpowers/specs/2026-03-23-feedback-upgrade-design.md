# Feedback System Upgrade — Design Spec

**Issue:** [#192](https://github.com/nobodies-collective/Humans/issues/192)
**Date:** 2026-03-23

## Summary

Upgrade the feedback system from a one-directional admin triage tool to a bidirectional conversation system with unified UI, role-based access, and admin visibility via nav badges.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Admin role | New `FeedbackAdmin` role | Follows existing pattern (CampAdmin, TeamsAdmin, TicketAdmin) |
| Conversation model | New `FeedbackMessage` table | Proper relational model, queryable message counts |
| Unread tracking | Timestamp-based (`LastReporterMessageAt` vs `LastAdminMessageAt`) | Captures "ball in admin's court" without per-user read tracking |
| Email notifications | Admin→reporter only | Reporters need notification; admins have the nav badge |
| Page layout | Master-detail (side-by-side) | List left, detail right, no page navigation |
| Controller | Single unified `FeedbackController` | Replaces both `FeedbackController` and `AdminFeedbackController` |
| API | Extend with message endpoints | Triage skill can chat with users for clarification |
| Role snapshot | `AdditionalContext` string field | Plain text, extensible, display-only context alongside UserAgent |
| Nav for regular users | Profile dropdown menu | Unobtrusive; submission widget already on every page |
| AdminNotes | Migrate to messages, drop column | Preserves existing notes as conversation history |

## Data Model

### New Entity: `FeedbackMessage`

| Field | Type | Notes |
|-------|------|-------|
| `Id` | `Guid` | PK |
| `FeedbackReportId` | `Guid` | FK → `feedback_reports`, CASCADE |
| `SenderUserId` | `Guid` | FK → `users`, SET NULL |
| `Content` | `string` | Max 5000 |
| `CreatedAt` | `Instant` | |

### Changes to `FeedbackReport`

| Change | Details |
|--------|---------|
| Add `AdditionalContext` | `string?`, max 2000. Populated at submission with current roles. |
| Add `LastReporterMessageAt` | `Instant?` — updated when reporter posts a message |
| Add `LastAdminMessageAt` | `Instant?` — updated when any FeedbackAdmin posts a message |
| Drop `AdminNotes` | Migrated to `FeedbackMessage` rows, then column dropped |
| Drop `AdminResponseSentAt` | Superseded by `LastAdminMessageAt` + email-on-post. No migration needed. |

### Data Migration

Existing `FeedbackReport` rows with non-null `AdminNotes`: insert a `FeedbackMessage` with notes content, `ResolvedByUserId` as sender (or null), `CreatedAt` = report's `UpdatedAt`. Then drop column.

## Controller & Routing

### Unified `FeedbackController` (`/Feedback`)

| Action | Method | Route | Auth | Description |
|--------|--------|-------|------|-------------|
| `Index` | GET | `/Feedback` | `[Authorize]` | Master-detail page. FeedbackAdmin sees all, regular users see own. |
| `Detail` | GET | `/Feedback/{id}` | `[Authorize]` | Detail partial for AJAX. Also works as direct link (full page, report pre-selected). |
| `Submit` | POST | `/Feedback` | `[Authorize]` | Submit new report. Populates `AdditionalContext` with roles. |
| `PostMessage` | POST | `/Feedback/{id}/Message` | `[Authorize]` | Add message. Reporter: own reports only. FeedbackAdmin: any. Email if admin→reporter. |
| `UpdateStatus` | POST | `/Feedback/{id}/Status` | `[FeedbackAdmin]` | Change status. |
| `SetGitHubIssue` | POST | `/Feedback/{id}/GitHubIssue` | `[FeedbackAdmin]` | Link GitHub issue. |

### Deleted

- `AdminFeedbackController` — removed entirely
- `UpdateNotes` action — replaced by `PostMessage`
- `SendResponse` action — replaced by `PostMessage`

### Direct Link Behavior

`/Feedback/{id}` (e.g., from email) loads the full page with that report pre-selected. If user lacks access (not their report and not FeedbackAdmin), return 404.

## API Changes (`FeedbackApiController`)

### New Endpoints

| Action | Method | Route | Description |
|--------|--------|-------|-------------|
| `GetMessages` | GET | `/api/feedback/{id}/messages` | List all messages for a report |
| `PostMessage` | POST | `/api/feedback/{id}/messages` | Post message as admin. Triggers email to reporter. |

### Updated Responses

- `Get` response: includes `messages` array and `additionalContext`
- `List` response items: include `messageCount`, `lastReporterMessageAt`, `lastAdminMessageAt`

### Removed

- `SendResponse` endpoint → replaced by `PostMessage`
- `UpdateNotes` endpoint → admin notes replaced by messages

## Navigation

- **FeedbackAdmin:** Top nav item "Feedback" with badge count via `NavBadges` ViewComponent (`feedback` queue)
- **Regular users:** "My Feedback" link in profile dropdown menu
- **Submission widget:** Unchanged (floating button on every page)

### Badge Query

Reports needing attention: `(Status == Open && LastAdminMessageAt == null)` OR `(LastReporterMessageAt > LastAdminMessageAt)`. Cached in `NavBadges` ViewComponent alongside existing review/voting counts.

## UI Layout

Master-detail (side-by-side):

- **Left panel (40%):** Report list with status badges, message counts, "needs reply" indicator. Filter bar at top for status/category.
- **Right panel (60%):** Report header with inline status dropdown and GitHub issue field. Context line showing UserAgent and `AdditionalContext` (roles). Original description with screenshot link. Conversation thread with colored left borders (blue = reporter, green = admin). Reply textarea at bottom.
- **Responsive:** On narrow screens, collapse to list-only with click-through to detail (fallback to separate-page pattern on mobile).

## Email Notification

- **Trigger:** Admin or API posts a message on a report
- **Content:** Admin's message, original description quoted, direct link to `/Feedback/{reportId}`
- **Localization:** Sent in reporter's `PreferredLanguage`
- **Infrastructure:** Update existing `IEmailService.SendFeedbackResponseAsync` to include direct link
- **Not triggered by:** Reporter messages, status changes

## Migration

### New Table: `feedback_messages`

| Column | Type |
|--------|------|
| `id` | `uuid` PK |
| `feedback_report_id` | `uuid` FK CASCADE |
| `sender_user_id` | `uuid` FK SET NULL |
| `content` | `varchar(5000)` |
| `created_at` | `timestamptz` |

Indexes: `feedback_report_id`, `created_at`.

### Alter `feedback_reports`

- Add `additional_context` (`varchar(2000)`, nullable)
- Add `last_reporter_message_at` (`timestamptz`, nullable)
- Add `last_admin_message_at` (`timestamptz`, nullable)
- Data-migrate `admin_notes` → `feedback_messages` rows
- Drop `admin_notes`
- Drop `admin_response_sent_at` (superseded by `last_admin_message_at`)

## Files Affected

### New
- `src/Humans.Domain/Entities/FeedbackMessage.cs`
- `src/Humans.Infrastructure/Data/Configurations/FeedbackMessageConfiguration.cs`
- `src/Humans.Web/Views/Feedback/Index.cshtml` (master-detail page)
- `src/Humans.Web/Views/Feedback/_Detail.cshtml` (detail partial)
- Migration file

### Modified
- `src/Humans.Domain/Entities/FeedbackReport.cs` — new fields, drop AdminNotes
- `src/Humans.Domain/Constants/RoleNames.cs` — add FeedbackAdmin
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add `FeedbackMessages` DbSet
- `src/Humans.Infrastructure/Data/Configurations/FeedbackReportConfiguration.cs` — new columns
- `src/Humans.Application/Interfaces/IFeedbackService.cs` — message methods, remove notes/response
- `src/Humans.Infrastructure/Services/FeedbackService.cs` — implement message methods
- `src/Humans.Web/Controllers/FeedbackController.cs` — rewrite as unified controller
- `src/Humans.Web/Controllers/FeedbackApiController.cs` — add message endpoints, remove notes/response
- `src/Humans.Web/Models/FeedbackViewModels.cs` — new view models for master-detail
- `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs` — add feedback queue
- `src/Humans.Web/Views/Shared/_Layout.cshtml` — add FeedbackAdmin nav item + badge, profile dropdown link
- `src/Humans.Web/Views/Shared/Components/FeedbackWidget/Default.cshtml` — unchanged (submission still works)
- `src/Humans.Application/Interfaces/IEmailService.cs` — update signature for direct link
- Email template(s) — add direct link
- Localization resources — new strings for conversation UI

### Deleted
- `src/Humans.Web/Controllers/Admin/AdminFeedbackController.cs`
- `src/Humans.Web/Views/AdminFeedback/Index.cshtml`
- `src/Humans.Web/Views/AdminFeedback/Detail.cshtml`

## YAGNI — Explicitly Out of Scope

- Per-user read receipts / unread tracking
- File attachments on messages (screenshots only on initial report)
- Message editing or deletion
- Real-time updates (WebSocket/SignalR)
- Reporter email notifications on status changes
- Markdown rendering in messages
