<!-- freshness:triggers
  src/Sections/Humans.Feedback/**
  src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs
-->
<!-- freshness:flag-on-change
  Feedback entities, controller routes, API surface, or status transitions may have changed; verify the auth matrix and routes table. The section is retired — if any change reintroduces a creation path or a reporter-facing view, this spec is wrong.
-->

# 27 — Feedback System

> **Retired — nobodies-collective/Humans#977.** Feedback no longer accepts new reports; Issues (`docs/features/issues/issues-system.md`) superseded it. This spec is kept as the record of what was built. The sections below are marked where the as-shipped behaviour now differs: there is no submission path, no reporter-facing view, and every remaining screen is full-`Admin` only. For the current invariants see `src/Sections/Humans.Feedback/Docs/Feedback.md`.

## Business Context

Humans need a way to report bugs, request features, and ask questions directly from the app. A unified feedback page lets reporters track their own submissions and have conversations with admins, while FeedbackAdmin users see all reports and can triage them. Claude Code has API access to query and manage reports programmatically.

## User Stories

### US-27.1: Submit Feedback — **REMOVED (#977)**

Every creation path below was deleted: the floating widget item and its modal, `POST /Feedback`, `IFeedbackService.SubmitUserFeedbackAsync`/`SubmitFeedbackAsync`, `IFeedbackRepository.AddReportAsync`, and `FeedbackWidgetViewComponent`. Reporting now goes to `/Issues`. Criteria retained for the historical record only.

**As** an authenticated human, **I want** to submit feedback from any page, **so that** I can report issues without leaving my current workflow.

**Acceptance Criteria:**
- Floating feedback button visible on all pages (authenticated users only)
- Modal form with category (Bug/Feature Request/Question), description, and optional screenshot
- Page URL and user agent captured automatically
- Success/error feedback via TempData toast
- Screenshot upload limited to JPEG/PNG/WebP, max 10MB

### US-27.2: Feedback Triage

**As** an Admin, **I want** to view and manage all feedback reports, **so that** I can triage and respond to user issues.

**Since #977:** full `Admin` only. `FeedbackAdmin` alone no longer reaches any Feedback screen, and neither does a report's own reporter — there is no "regular users see only their own" view left, and no "My Feedback" dropdown link.

**Acceptance Criteria:**
- Unified page at `/Feedback` — every report, Admin only
- Master-detail layout: report list on the left, detail panel on the right (loaded via AJAX)
- Status/category filtering
- Detail view with full description, screenshot, reporter link, timestamps
- Update status (Open/Acknowledged/Resolved/Won't Fix)
- Assign a report to a user and/or a team
- Link GitHub issue number
- Conversation thread with bidirectional messaging (see US-27.5)
- Nav badge on "Feedback" link showing count of actionable items (reports needing admin reply)
- Accessible from the admin nav for Admin only

### US-27.3: API Access

**As** Claude Code (or another external tool), **I want** to query and manage feedback via a REST API, **so that** I can integrate feedback into automated workflows.

**Acceptance Criteria:**
- `GET /api/feedback` — list with optional status/category/limit filters
- `GET /api/feedback/{id}` — single report detail with messages
- `GET /api/feedback/{id}/messages` — list conversation messages
- `POST /api/feedback/{id}/messages` — post a message to the conversation thread
- `PATCH /api/feedback/{id}/status` — update status (accepts string enum names)
- `PATCH /api/feedback/{id}/assignment` — set assignee user and/or team (either may be null to clear)
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
- ~~Includes a direct link to `/Feedback/{reportId}` so the reporter can reply~~ — **since #977** the email template renders no link (the `reportLink` argument is unused) and the in-app notification carries no action URL, because `/Feedback/{id}` is Admin-only. The reply text in the email is the reporter's only copy
- `LastAdminMessageAt` timestamp updated on the report

### US-27.5: Conversation History

**As** a feedback reporter or admin, **I want** to have a conversation thread on a feedback report, **so that** we can discuss the issue back and forth without switching to email.

**Since #977:** one-directional. `PostMessageAsync` lost its `isAdmin` flag — every message posted now is an admin reply. Reporters cannot post follow-ups; historical reporter messages are still displayed.

**Acceptance Criteria:**
- `FeedbackMessage` entity tracks individual messages (content, sender, timestamp)
- ~~Both reporters and admins can post messages via the detail view~~ — admins only
- Messages displayed chronologically in the detail panel
- `LastReporterMessageAt` / `LastAdminMessageAt` timestamps maintained on the report
- Reports needing admin reply are flagged (reporter message is newer than last admin message, or no admin message yet)

## Data Model

See `docs/architecture/data-model.md` — `FeedbackReport` and `FeedbackMessage` entities.

**Table:** `feedback_reports`

Key fields: Id, UserId, Category (enum→string), Description, PageUrl, UserAgent, AdditionalContext (auto-populated with user roles at submission), Screenshot* (FileName/StoragePath/ContentType), Status (enum→string), Source (`FeedbackSource`: UserReport / AgentUnresolved, enum→string with an out-of-range EF sentinel), GitHubIssueNumber, AgentConversationId (bare Guid, no EF FK), AssignedToUserId, AssignedToTeamId (both bare Guids, no nav), LastReporterMessageAt, LastAdminMessageAt, CreatedAt, UpdatedAt, ResolvedAt, ResolvedByUserId.

`Source` and `AgentConversationId` are vestigial **for new rows**: the agent's `route_to_feedback` auto-create flow is gone and no creation path remains, so nothing writes `AgentUnresolved` any more. Existing databases still hold historical rows with `Source = AgentUnresolved` and a populated `AgentConversationId`, and those stay queryable through the Feedback admin filter — do not assume `Source == UserReport` when reading.

Removed fields (from previous version): `AdminNotes`, `AdminResponseSentAt`.

**Table:** `feedback_messages`

Key fields: Id, FeedbackReportId (FK), SenderUserId (nullable, bare cross-section Guid column — no FK constraint, no nav), Content, CreatedAt.

Relationship: `FeedbackReport` has many `FeedbackMessage` (cascade delete). `SenderUserId` is nullable to support system/API messages.

**Screenshot storage:** `wwwroot/uploads/feedback/{reportId}/{guid}.{ext}`

## Authorization Matrix

As shipped since #977 — the policy sits on `FeedbackController` itself, so every action inherits it:

| Endpoint | Auth |
|----------|------|
| `GET /Feedback` | `[Authorize(Policy = AdminOnly)]` — every report |
| `GET /Feedback/{id}` | `[Authorize(Policy = AdminOnly)]` |
| `POST /Feedback/{id}/Message` | `[Authorize(Policy = AdminOnly)]` |
| `POST /Feedback/{id}/Status` | `[Authorize(Policy = AdminOnly)]` |
| `POST /Feedback/{id}/Assignment` | `[Authorize(Policy = AdminOnly)]` |
| `POST /Feedback/{id}/GitHubIssue` | `[Authorize(Policy = AdminOnly)]` |
| `GET /api/feedback` | API key (`X-Api-Key` header) |
| `* /api/feedback/*` | API key (`X-Api-Key` header) |

`POST /Feedback` is gone — the controller has no root-`POST` route at all.

**FeedbackAdmin role:** originally followed the CampAdmin/TeamsAdmin pattern — a specialized role granting feedback triage without full Admin. Since #977 it grants **no** Feedback access. The role name is kept because the Staff page, `GuideRoleResolver`/`GuideRolePrivilegeMap`, the authorization pill-filter label map, and `AnyAdminRole` still reference it; `PolicyNames.FeedbackAdminOrAdmin`, `RoleGroups.FeedbackAdminOrAdmin`, and `RoleChecks.IsFeedbackAdmin` were deleted.

## URL Routes

| Route | Controller | Action |
|-------|-----------|--------|
| `GET /Feedback` | FeedbackController | Index |
| `GET /Feedback/{id}` | FeedbackController | Detail |
| `POST /Feedback/{id}/Message` | FeedbackController | PostMessage |
| `POST /Feedback/{id}/Status` | FeedbackController | UpdateStatus |
| `POST /Feedback/{id}/Assignment` | FeedbackController | UpdateAssignment |
| `POST /Feedback/{id}/GitHubIssue` | FeedbackController | SetGitHubIssue |
| `GET /api/feedback` | FeedbackApiController | List |
| `GET /api/feedback/{id}` | FeedbackApiController | Get |
| `GET /api/feedback/{id}/messages` | FeedbackApiController | GetMessages |
| `POST /api/feedback/{id}/messages` | FeedbackApiController | PostMessage |
| `PATCH /api/feedback/{id}/status` | FeedbackApiController | UpdateStatus |
| `PATCH /api/feedback/{id}/assignment` | FeedbackApiController | UpdateAssignment |
| `PATCH /api/feedback/{id}/github-issue` | FeedbackApiController | SetGitHubIssue |

Removed routes: `POST /Feedback` (Submit, removed by #977); and from an earlier version, `PATCH /api/feedback/{id}/notes`, `POST /api/feedback/{id}/respond`, and all `/Admin/Feedback/*` routes.

## Claude Code Triage Integration (#147)

The feedback API enables a Claude Code workflow for processing feedback during dev sessions:

- **`/whats` integration:** When `HUMANS_API_URL` and `HUMANS_API_KEY` env vars are set, `/whats` checks for pending feedback and surfaces the count in its status output. Humans-project-specific; other projects skip this step.
- **`/triage` skill:** Interactive triage of pending reports — for each report, choose to respond, create a GitHub issue (on `nobodies-collective/Humans`), mark won't fix, or skip. Issues are linked back to the feedback report via the API.
- **Environment setup:** `FEEDBACK_API_KEY` env var on the server, `HUMANS_API_KEY`/`HUMANS_API_URL` in `.claude/settings.local.json` (gitignored).
- **Admin visibility:** `FEEDBACK_API_KEY` status shown on `/Debug/Configuration` diagnostics page.

## Navigation

Since #977:

- **Admin:** "Feedback queue" item in the admin sidebar with a pill showing the actionable count, supplied by `AdminNavTree` via `PillCounts.FeedbackQueue`, plus the `/Admin` dashboard tile — both `AdminOnly`. `NavBadgesViewComponent` does **not** serve this: it has no `feedback` queue and returns zero for that value
- **All authenticated users:** nothing. The "My Feedback" profile-dropdown link was removed
- **Floating button:** removed from the Help widget; the widget's remaining report action is "Create issue"

## Related Features

- Email outbox (`EmailOutboxMessage`) — used for admin reply notification emails
- Audit log (`AuditLogEntry`) — tracks status changes
- `AdminNavTree` / `PillCounts.FeedbackQueue` — renders the actionable count on the admin sidebar item. The count itself is still cached inline in `FeedbackService.GetActionableCountAsync` (`CacheKeys.FeedbackBadgeCount`, 2-min TTL) and invalidated through `INavBadgeCacheInvalidator`
- Role management — FeedbackAdmin role assignable via `/Admin/Roles`
