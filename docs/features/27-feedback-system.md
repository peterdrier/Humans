# 27 — Feedback System

## Business Context

Humans need a way to report bugs, request features, and ask questions directly from the app. Admins need a triage dashboard to manage feedback, and Claude Code needs API access to query and manage reports programmatically.

## User Stories

### US-27.1: Submit Feedback

**As** an authenticated human, **I want** to submit feedback from any page, **so that** I can report issues without leaving my current workflow.

**Acceptance Criteria:**
- Floating feedback button visible on all pages (authenticated users only)
- Modal form with category (Bug/Feature Request/Question), description, and optional screenshot
- Page URL and user agent captured automatically
- Success/error feedback via TempData toast
- Screenshot upload limited to JPEG/PNG/WebP, max 10MB

### US-27.2: Admin Triage

**As** an Admin, **I want** to view and manage all feedback reports, **so that** I can triage and respond to user issues.

**Acceptance Criteria:**
- List view at `/Admin/Feedback` with status/category filtering
- Detail view with full description, screenshot, reporter link, timestamps
- Update status (Open/Acknowledged/Resolved/Won't Fix)
- Admin notes (internal, not visible to reporter)
- Link GitHub issue number
- Send email response to reporter (via outbox)
- Accessible from Admin tools page

### US-27.3: API Access

**As** Claude Code (or another external tool), **I want** to query and manage feedback via a REST API, **so that** I can integrate feedback into automated workflows.

**Acceptance Criteria:**
- `GET /api/feedback` — list with optional status/category/limit filters
- `GET /api/feedback/{id}` — single report detail with response history
- `PATCH /api/feedback/{id}/status` — update status (accepts string enum names)
- `PATCH /api/feedback/{id}/notes` — update admin notes
- `PATCH /api/feedback/{id}/github-issue` — link GitHub issue
- `POST /api/feedback/{id}/respond` — send email response (supports markdown)
- All endpoints require `X-Api-Key` header (configured via `FEEDBACK_API_KEY` env var)
- 503 if API key not configured, 401 if key invalid
- Enum values serialized as strings consistently (GET and PATCH)
- Reporter context included: name, email, userId, preferred language
- Response tracking: count on list, full history (timestamps + actor) on detail

### US-27.4: Email Response

**As** an Admin, **I want** to send a response to the feedback reporter via email, **so that** they know their feedback was heard and addressed.

**Acceptance Criteria:**
- Email sent via outbox pattern (not inline)
- Localized in reporter's preferred language (en/es/de/fr/it)
- Includes original description as blockquote
- Response message rendered as markdown (supports bold, links, lists, etc.)
- `AdminResponseSentAt` timestamp updated
- Audit log entry created (`FeedbackResponseSent`)

## Data Model

See `.claude/DATA_MODEL.md` — `FeedbackReport` entity.

**Table:** `feedback_reports`

Key fields: Id, UserId, Category (enum→string), Description, PageUrl, UserAgent, Screenshot* (FileName/StoragePath/ContentType), Status (enum→string), AdminNotes, GitHubIssueNumber, AdminResponseSentAt, CreatedAt, UpdatedAt, ResolvedAt, ResolvedByUserId.

**Screenshot storage:** `wwwroot/uploads/feedback/{reportId}/{guid}.{ext}`

## Authorization Matrix

| Endpoint | Auth |
|----------|------|
| `POST /Feedback/Submit` | `[Authorize]` (any authenticated user) |
| `GET /Admin/Feedback` | `[Authorize(Roles = "Admin")]` |
| `GET /Admin/Feedback/{id}` | `[Authorize(Roles = "Admin")]` |
| `POST /Admin/Feedback/{id}/*` | `[Authorize(Roles = "Admin")]` |
| `GET /api/feedback` | API key (`X-Api-Key` header) |
| `* /api/feedback/*` | API key (`X-Api-Key` header) |

## URL Routes

| Route | Controller | Action |
|-------|-----------|--------|
| `POST /Feedback/Submit` | FeedbackController | Submit |
| `GET /Admin/Feedback` | AdminFeedbackController | Index |
| `GET /Admin/Feedback/{id}` | AdminFeedbackController | Detail |
| `POST /Admin/Feedback/{id}/Status` | AdminFeedbackController | UpdateStatus |
| `POST /Admin/Feedback/{id}/Notes` | AdminFeedbackController | UpdateNotes |
| `POST /Admin/Feedback/{id}/GitHubIssue` | AdminFeedbackController | SetGitHubIssue |
| `POST /Admin/Feedback/{id}/Respond` | AdminFeedbackController | SendResponse |
| `GET /api/feedback` | FeedbackApiController | List |
| `GET /api/feedback/{id}` | FeedbackApiController | Get |
| `PATCH /api/feedback/{id}/status` | FeedbackApiController | UpdateStatus |
| `PATCH /api/feedback/{id}/notes` | FeedbackApiController | UpdateNotes |
| `PATCH /api/feedback/{id}/github-issue` | FeedbackApiController | SetGitHubIssue |
| `POST /api/feedback/{id}/respond` | FeedbackApiController | SendResponse |

## Claude Code Triage Integration (#147)

The feedback API enables a Claude Code workflow for processing feedback during dev sessions:

- **`/whats` integration:** When `HUMANS_API_URL` and `HUMANS_API_KEY` env vars are set, `/whats` checks for pending feedback and surfaces the count in its status output. Humans-project-specific; other projects skip this step.
- **`/triage` skill:** Interactive triage of pending reports — for each report, choose to respond, create a GitHub issue (on `nobodies-collective/Humans`), mark won't fix, or skip. Issues are linked back to the feedback report via the API.
- **Environment setup:** `FEEDBACK_API_KEY` env var on the server, `HUMANS_API_KEY`/`HUMANS_API_URL` in `.claude/settings.local.json` (gitignored).
- **Admin visibility:** `FEEDBACK_API_KEY` status shown on `/Admin/Configuration` diagnostics page.

## Related Features

- Email outbox (`EmailOutboxMessage`) — used for response emails
- Audit log (`AuditLogEntry`) — tracks response sends
- Admin tools dashboard — nav link to feedback list
