<!-- freshness:triggers
  src/Humans.Application/Services/Feedback/**
  src/Humans.Web/Controllers/FeedbackController.cs
  src/Humans.Web/Controllers/FeedbackApiController.cs
  src/Humans.Web/ViewComponents/FeedbackWidgetViewComponent.cs
  src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs
  src/Humans.Domain/Entities/FeedbackReport.cs
  src/Humans.Domain/Entities/FeedbackMessage.cs
  src/Humans.Infrastructure/Data/Configurations/Feedback/**
-->
<!-- freshness:flag-on-change
  Feedback entities, controller routes, API surface, status transitions, or FeedbackAdmin auth rules may have changed; verify the auth matrix and routes table.
-->

# 27 — Feedback System

## Business Context

Humans need a way to report bugs, request features, and ask questions directly from the app. A unified feedback page lets reporters track their own submissions and have conversations with admins, while FeedbackAdmin users see all reports and can triage them. Claude Code has API access to query and manage reports programmatically.

## User Stories

### US-27.1: Submit Feedback

**As** an authenticated human, **I want** to submit feedback from any page, **so that** I can report issues without leaving my current workflow.

**Acceptance Criteria:**
- Floating feedback button visible on all pages (authenticated users only)
- Modal form with category (Bug/Feature Request/Question), description, and optional screenshot
- Page URL and user agent captured automatically
- Success/error feedback via TempData toast
- Screenshot upload limited to JPEG/PNG/WebP, max 10MB

### US-27.2: Feedback Triage

**As** a FeedbackAdmin (or Admin), **I want** to view and manage all feedback reports, **so that** I can triage and respond to user issues.

**Acceptance Criteria:**
- Unified page at `/Feedback` — FeedbackAdmin sees all reports, regular users see only their own
- Master-detail layout: report list on the left, detail panel on the right (loaded via AJAX)
- Status/category filtering
- Detail view with full description, screenshot, reporter link, timestamps
- Update status (Open/Acknowledged/Resolved/Won't Fix)
- Link GitHub issue number
- Conversation thread with bidirectional messaging (see US-27.5)
- Nav badge on "Feedback" link showing count of actionable items (reports needing admin reply)
- Accessible from main nav for FeedbackAdmin/Admin; "My Feedback" link in profile dropdown for all users

### US-27.3: API Access

**As** Claude Code (or another external tool), **I want** to query and manage feedback via a REST API, **so that** I can integrate feedback into automated workflows.

**Acceptance Criteria:**
- `GET /api/feedback` — list with optional status/category/limit filters
- `GET /api/feedback/{id}` — single report detail with messages
- `GET /api/feedback/{id}/messages` — list conversation messages
- `POST /api/feedback/{id}/messages` — post a message to the conversation thread
- `PATCH /api/feedback/{id}/status` — update status (accepts string enum names)
- `PATCH /api/feedback/{id}/github-issue` — link GitHub issue
- All endpoints require `X-Api-Key` header (configured via `FEEDBACK_API_KEY` env var)
- 503 if API key not configured, 401 if key invalid
- Enum values serialized as strings consistently (GET and PATCH)
- Reporter context included: name, email, userId, preferred language
- Message tracking: count on list, full message history on detail

### US-27.4: Email Notifications

**As** a feedback reporter, **I want** to receive email notifications when an admin replies to my feedback, **so that** I know my feedback was heard and can continue the conversation.

**Acceptance Criteria:**
- Email sent via outbox pattern (not inline) when an admin posts a message
- Localized in reporter's preferred language (en/es/de/fr/it)
- Includes the admin's reply content
- Includes a direct link to `/Feedback/{reportId}` so the reporter can reply
- `LastAdminMessageAt` timestamp updated on the report

### US-27.5: Conversation History

**As** a feedback reporter or admin, **I want** to have a conversation thread on a feedback report, **so that** we can discuss the issue back and forth without switching to email.

**Acceptance Criteria:**
- `FeedbackMessage` entity tracks individual messages (content, sender, timestamp)
- Both reporters and admins can post messages via the detail view
- Messages displayed chronologically in the detail panel
- `LastReporterMessageAt` / `LastAdminMessageAt` timestamps maintained on the report
- Reports needing admin reply are flagged (reporter message is newer than last admin message, or no admin message yet)

## Data Model

See `docs/architecture/data-model.md` — `FeedbackReport` and `FeedbackMessage` entities.

**Table:** `feedback_reports`

Key fields: Id, UserId, Category (enum→string), Description, PageUrl, UserAgent, AdditionalContext (auto-populated with user roles at submission), Screenshot* (FileName/StoragePath/ContentType), Status (enum→string), GitHubIssueNumber, LastReporterMessageAt, LastAdminMessageAt, CreatedAt, UpdatedAt, ResolvedAt, ResolvedByUserId.

Removed fields (from previous version): `AdminNotes`, `AdminResponseSentAt`.

**Table:** `feedback_messages`

Key fields: Id, FeedbackReportId (FK), SenderUserId (nullable FK), Content, CreatedAt.

Relationship: `FeedbackReport` has many `FeedbackMessage` (cascade delete). `SenderUserId` is nullable to support system/API messages.

**Screenshot storage:** `wwwroot/uploads/feedback/{reportId}/{guid}.{ext}`

## Authorization Matrix

| Endpoint | Auth |
|----------|------|
| `GET /Feedback` | `[Authorize]` — FeedbackAdmin/Admin see all; regular users see own reports only |
| `GET /Feedback/{id}` | `[Authorize]` — FeedbackAdmin/Admin or report owner |
| `POST /Feedback` | `[Authorize]` (any authenticated user) |
| `POST /Feedback/{id}/Message` | `[Authorize]` — FeedbackAdmin/Admin or report owner |
| `POST /Feedback/{id}/Status` | `[Authorize(Roles = "FeedbackAdmin,Admin")]` |
| `POST /Feedback/{id}/GitHubIssue` | `[Authorize(Roles = "FeedbackAdmin,Admin")]` |
| `GET /api/feedback` | API key (`X-Api-Key` header) |
| `* /api/feedback/*` | API key (`X-Api-Key` header) |

**FeedbackAdmin role:** Follows the CampAdmin/TeamsAdmin pattern — a specialized role granting access to feedback triage without requiring full Admin privileges.

## URL Routes

| Route | Controller | Action |
|-------|-----------|--------|
| `GET /Feedback` | FeedbackController | Index |
| `GET /Feedback/{id}` | FeedbackController | Detail |
| `POST /Feedback` | FeedbackController | Submit |
| `POST /Feedback/{id}/Message` | FeedbackController | PostMessage |
| `POST /Feedback/{id}/Status` | FeedbackController | UpdateStatus |
| `POST /Feedback/{id}/GitHubIssue` | FeedbackController | SetGitHubIssue |
| `GET /api/feedback` | FeedbackApiController | List |
| `GET /api/feedback/{id}` | FeedbackApiController | Get |
| `GET /api/feedback/{id}/messages` | FeedbackApiController | GetMessages |
| `POST /api/feedback/{id}/messages` | FeedbackApiController | PostMessage |
| `PATCH /api/feedback/{id}/status` | FeedbackApiController | UpdateStatus |
| `PATCH /api/feedback/{id}/github-issue` | FeedbackApiController | SetGitHubIssue |

Removed routes (from previous version): `PATCH /api/feedback/{id}/notes`, `POST /api/feedback/{id}/respond`, all `/Admin/Feedback/*` routes.

## Claude Code Triage Integration (#147)

The feedback API enables a Claude Code workflow for processing feedback during dev sessions:

- **`/whats` integration:** When `HUMANS_API_URL` and `HUMANS_API_KEY` env vars are set, `/whats` checks for pending feedback and surfaces the count in its status output. Humans-project-specific; other projects skip this step.
- **`/triage` skill:** Interactive triage of pending reports — for each report, choose to respond, create a GitHub issue (on `nobodies-collective/Humans`), mark won't fix, or skip. Issues are linked back to the feedback report via the API.
- **Environment setup:** `FEEDBACK_API_KEY` env var on the server, `HUMANS_API_KEY`/`HUMANS_API_URL` in `.claude/settings.local.json` (gitignored).
- **Admin visibility:** `FEEDBACK_API_KEY` status shown on `/Admin/Configuration` diagnostics page.

## Navigation

- **FeedbackAdmin/Admin:** "Feedback" link in main nav with badge showing actionable item count (via `NavBadges` ViewComponent, `queue = "feedback"`)
- **All authenticated users:** "My Feedback" link in profile dropdown
- **Floating button:** Feedback submission widget on all pages (unchanged)

## Related Features

- Email outbox (`EmailOutboxMessage`) — used for admin reply notification emails
- Audit log (`AuditLogEntry`) — tracks status changes
- `NavBadges` ViewComponent — extended with `feedback` queue for actionable item count
- Role management — FeedbackAdmin role assignable via `/Admin/Roles`
