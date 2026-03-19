# In-App Feedback System Design

**Issue:** #141
**Date:** 2026-03-18
**Status:** Approved

## Overview

An in-app feedback system allowing authenticated humans to report bugs, request features, and ask questions. Includes an admin triage UI, email responses to reporters, and a JSON API with API key auth for Claude Code integration.

## Data Model

### FeedbackReport Entity

```
FeedbackReport
├── Id: Guid
├── UserId: Guid (FK → User)
├── Category: FeedbackCategory (enum)
├── Description: string (5000)
├── PageUrl: string (2000) — auto-captured current URL
├── UserAgent: string? (1000) — auto-captured browser info
├── ScreenshotFileName: string? (256) — original filename
├── ScreenshotStoragePath: string? (512) — relative path: uploads/feedback/{reportId}/{guid}.ext
├── ScreenshotContentType: string? (64)
├── Status: FeedbackStatus (enum)
├── AdminNotes: string? (5000) — internal notes
├── GitHubIssueNumber: int? — linked GitHub issue
├── AdminResponseSentAt: Instant? — last email response timestamp
├── CreatedAt: Instant
├── UpdatedAt: Instant — last status/notes change
├── ResolvedAt: Instant?
├── ResolvedByUserId: Guid? (FK → User)
└── Navigation: User, ResolvedByUser
```

Note: The report `Id` is generated before `SaveChangesAsync` so it can be used in the screenshot storage path (`uploads/feedback/{reportId}/{guid}.ext`).

### Enums

```
FeedbackCategory:
  Bug
  FeatureRequest
  Question

FeedbackStatus:
  Open
  Acknowledged
  Resolved
  WontFix
```

### Storage

- Screenshot files stored on disk at `wwwroot/uploads/feedback/{reportId}/{guid}.ext`
- Served via standard static file middleware (same pattern as camp images)
- Allowed types: JPEG, PNG, WebP. Max 10MB.
- Metadata (filename, path, content type) stored in the entity
- If a screenshot file is missing on disk, the image tag gracefully degrades (broken image); no special handling needed

## Status Workflow

```
Open → Acknowledged → Resolved
                   → WontFix
```

- New submissions start as `Open`
- `Acknowledged` = admin has seen it
- `Resolved` / `WontFix` = terminal states, sets `ResolvedAt` and `ResolvedByUserId`
- Reopening a resolved report: clears `ResolvedAt` and `ResolvedByUserId`, sets status back to `Open`
- All status changes update `UpdatedAt`

## UI: Feedback Widget

### Floating Button
- Fixed-position button in bottom-right corner of every page
- Only visible to authenticated users (`User.Identity.IsAuthenticated`)
- Implemented as `FeedbackWidgetViewComponent` invoked in `_Layout.cshtml`
- Clicking opens a Bootstrap modal

### Footer Link
- "Feedback" link in the site footer, opens the same modal

### Modal Form
- Category dropdown (Bug / Feature Request / Question)
- Description textarea (required, max 5000 chars)
- File upload input (optional, JPEG/PNG/WebP, max 10MB)
- Hidden fields auto-populated by JS: current page URL (`window.location.href`), user agent (`navigator.userAgent`)
- Submit POSTs to `POST /Feedback/Submit`
- Redirects back to the referring page with TempData success message
- Full i18n across all 5 locales (en/es/de/fr/it)

## UI: Admin Pages

**Authorization:** Admin role only.

### List Page (`GET /Admin/Feedback`)
- Table of all feedback reports, newest first
- Columns: status badge, category badge, description snippet (truncated), reporter name, page URL, date
- Filter bar: status (Open / Acknowledged / Resolved / Won't Fix / All), category
- Click row to open detail

### Detail Page (`GET /Admin/Feedback/{id}`)
- Full description text
- Screenshot display (if uploaded)
- Page URL (clickable), user agent, reporter profile link
- Status dropdown to change status
- Admin notes textarea (saved on the report)
- GitHub issue number field (optional, renders as clickable link to `https://github.com/nobodies-collective/Humans/issues/{number}`)
- "Send Response" section: textarea to compose email to reporter, sends via email outbox
- Timestamps: submitted, last updated, last response sent

**No localization** on admin pages per coding rules.

## API: Claude Code Integration

### Authentication

New `ApiKeyAuthFilter` — an `IAuthorizationFilter` that checks the `X-Api-Key` header against a configured key. Returns 401 on missing/invalid key. This is new infrastructure (no existing inbound API key pattern in the codebase). The key value is stored in environment variable `FEEDBACK_API_KEY` and loaded via a settings object at DI registration time (same config pattern as TicketTailor, though that's outbound auth).

### Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/feedback` | List reports. Query params: `status`, `category`, `limit` (default 50) |
| GET | `/api/feedback/{id}` | Single report detail |
| PATCH | `/api/feedback/{id}/status` | Update status. Body: `{ "status": "Resolved" }` |
| PATCH | `/api/feedback/{id}/notes` | Update admin notes. Body: `{ "notes": "..." }` |
| PATCH | `/api/feedback/{id}/github-issue` | Set GitHub issue number. Body: `{ "issueNumber": 141 }` |
| POST | `/api/feedback/{id}/respond` | Send email to reporter. Body: `{ "message": "..." }` |

### Response Format
JSON. Report objects include: id, reporter display name, description, category, status, page URL, user agent, admin notes, GitHub issue number, screenshot URL (relative path), timestamps.

No create/delete via API. Submissions come through the web UI only. Reports are never deleted.

## Email Response

- Requires new methods on `IEmailService` (`SendFeedbackResponseAsync`) and `IEmailRenderer` (`RenderFeedbackResponse`) with implementations in `OutboxEmailService` and `EmailRenderer`
- Localized to reporter's preferred language
- Subject: "Update on your feedback" (localized)
- Body: greeting, original description quoted, admin's response message
- `AdminResponseSentAt` updated on each send
- Multiple responses allowed over time
- One-way communication — no reply-to

## Service Layer

### IFeedbackService
- `SubmitFeedbackAsync(userId, category, description, pageUrl, userAgent, screenshot?)` — creates report (generates Id first), stores screenshot to disk
- `GetFeedbackByIdAsync(id)` — single report with navigation properties
- `GetFeedbackListAsync(status?, category?, limit)` — filtered list
- `UpdateStatusAsync(id, status, actorUserId)` — change status, set/clear resolved fields, update `UpdatedAt`
- `UpdateAdminNotesAsync(id, notes)` — save admin notes, update `UpdatedAt`
- `SetGitHubIssueNumberAsync(id, issueNumber)` — link to GitHub issue
- `SendResponseAsync(id, message, actorUserId)` — compose and queue email response via `IEmailService`

### Audit Logging
- `AuditAction.FeedbackResponseSent` — logged when email response is sent

## Authorization

| Action | Who |
|--------|-----|
| Submit feedback | Any active member (authenticated + ActiveMember claim) |
| View/manage feedback (admin UI) | Admin only |
| API access | Valid API key |

Note: `FeedbackController` requires the `ActiveMember` claim (default behavior via `MembershipRequiredFilter`). Users still onboarding cannot submit feedback. The `FeedbackApiController` must be added to `MembershipRequiredFilter.ExemptControllers` since it uses API key auth instead of cookie auth.

## Files Created/Modified

### New Files
- `src/Humans.Domain/Entities/FeedbackReport.cs`
- `src/Humans.Domain/Enums/FeedbackCategory.cs`
- `src/Humans.Domain/Enums/FeedbackStatus.cs`
- `src/Humans.Infrastructure/Data/Configurations/FeedbackReportConfiguration.cs`
- `src/Humans.Infrastructure/Services/FeedbackService.cs`
- `src/Humans.Application/Interfaces/IFeedbackService.cs`
- `src/Humans.Web/Controllers/FeedbackController.cs` (submission)
- `src/Humans.Web/Controllers/AdminFeedbackController.cs` (admin UI)
- `src/Humans.Web/Controllers/FeedbackApiController.cs` (JSON API)
- `src/Humans.Web/Filters/ApiKeyAuthFilter.cs`
- `src/Humans.Web/ViewComponents/FeedbackWidgetViewComponent.cs`
- `src/Humans.Web/Views/Shared/Components/FeedbackWidget/Default.cshtml`
- `src/Humans.Web/Views/AdminFeedback/Index.cshtml`
- `src/Humans.Web/Views/AdminFeedback/Detail.cshtml`
- `src/Humans.Web/Models/FeedbackViewModels.cs`
- EF migration

### Modified Files
- `src/Humans.Domain/Enums/AuditAction.cs` — add `FeedbackResponseSent`
- `src/Humans.Application/Interfaces/IEmailService.cs` — add `SendFeedbackResponseAsync`
- `src/Humans.Application/Interfaces/IEmailRenderer.cs` — add `RenderFeedbackResponse`
- `src/Humans.Infrastructure/Services/OutboxEmailService.cs` — implement `SendFeedbackResponseAsync`
- `src/Humans.Infrastructure/Services/EmailRenderer.cs` — implement `RenderFeedbackResponse`
- `src/Humans.Web/Views/Shared/_Layout.cshtml` — invoke FeedbackWidgetViewComponent, add footer link
- `src/Humans.Web/Resources/SharedResource.resx` (+ es/de/fr/it) — widget labels, category names, email template
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — register services, API key config
- `src/Humans.Web/Authorization/MembershipRequiredFilter.cs` — add `FeedbackApiController` to exempt list
- `.claude/DATA_MODEL.md` — add FeedbackReport entity
- `docs/features/` — new feature spec

## What This Doesn't Do (YAGNI)

- No automated GitHub webhook integration (manual response flow)
- No user-facing "my feedback history" page
- No admin notification on new submission
- No rich text or markdown in feedback descriptions
- No feedback voting or prioritization
