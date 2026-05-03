<!-- freshness:triggers
  src/Humans.Application/Services/Issues/**
  src/Humans.Web/Controllers/IssuesController.cs
  src/Humans.Web/Controllers/IssuesApiController.cs
  src/Humans.Web/ViewComponents/IssuesWidgetViewComponent.cs
  src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs
  src/Humans.Domain/Entities/Issue.cs
  src/Humans.Domain/Entities/IssueComment.cs
  src/Humans.Domain/Constants/IssueSectionRouting.cs
  src/Humans.Infrastructure/Data/Configurations/Issues/**
-->
<!-- freshness:flag-on-change
  Issues entities, controller routes, API surface, status transitions, section routing, or handler-vs-reporter auth rules may have changed; verify the auth matrix and routes table.
-->

# 28 — Issues System

## Business Context

The Issues section is the in-app issue tracker — bugs, feature requests, and questions raised by humans against any section of the app. Submissions are routed by `Issue.Section` to the role-holders who own that section (e.g. an issue tagged `Tickets` lands in the queue of every `TicketAdmin`), so triage is decentralised: each section's coordinators see only the issues that concern them, while Admin sees everything. A reporter can have a back-and-forth conversation with handlers right inside the issue detail; "ball-in-court" is derived from the latest comment so the system never lies about whose move it is. Claude Code agents have programmatic read/write access via an API key for automated triage and follow-up.

This is **not** the same surface as Feedback (`27-feedback-system.md`). Feedback is a single global queue triaged by `FeedbackAdmin`; Issues is a section-routed queue triaged by each section's role-holders. Both are kept because they serve different audiences and lifecycles, but new in-app reports of section-specific problems should go to Issues.

## User Stories

### US-28.1: Submit an Issue

**As** an authenticated human, **I want** to file an issue from any page, **so that** I can report a problem against the right area without leaving my current workflow.

**Acceptance Criteria:**
- Floating widget on every page (`IssuesWidgetViewComponent`), authenticated users only
- `/Issues/New` page with full form (title, type, optional section override, description, optional screenshot)
- `Section` defaults to auto-detected from `PageUrl` via `IssueSectionInference.FromPath` when the widget is used; the `/Issues/New` form lets the reporter pick or leave blank for auto-detection
- Page URL, user agent, and the reporter's roles captured automatically into `AdditionalContext`
- Screenshot upload limited to JPEG/PNG/WebP, max 10 MB
- Issues are retained for 6 months after entering a terminal state (Resolved / WontFix / Duplicate); a daily Hangfire job (`CleanupIssuesJob`) then deletes the row, comments, and screenshot directory
- Success/error feedback via TempData toast
- Submission lands at `Status = Triage` with `Section` from the form (or auto-inferred, or null → Admin queue)

### US-28.2: Section-Routed Triage

**As** a section role-holder (e.g., `TicketAdmin`, `CampAdmin`, `Board`) **or** an Admin, **I want** to see and triage the issues filed against my section, **so that** my queue stays focused on what I actually own.

**Acceptance Criteria:**
- Unified page at `/Issues` — Admin sees all issues; section role-holders see issues where `Issue.Section` maps to one of their roles (per `IssueSectionRouting.RolesFor`); a regular human sees only the issues they reported
- Master-detail layout: list on the left with quick-filters (All / Open / Mine / Closed), section/category/reporter filters, and a search box; detail panel on the right (loaded via AJAX)
- Detail view shows full description, screenshot, reporter link with admin popover (handlers only), assignee, GitHub link, timestamps, due date, resolved-by
- Handlers can update status (Triage / Open / InProgress / Resolved / WontFix / Duplicate), assignee, section, and GitHub issue number from the detail panel via auto-submitting selects
- `Section` is editable in any non-terminal state — re-routing an issue is just changing its `Section` string
- Nav badge on "Issues" link shows count of actionable items via `NavBadges` ViewComponent (queue = `issues`); the per-viewer count comes from `IssuesService.GetActionableCountForViewerAsync`

### US-28.3: API Access for Claude Code

**As** Claude Code (or another external tool), **I want** to query and manage issues via a REST API, **so that** I can integrate issue triage into automated workflows.

**Acceptance Criteria:**
- `GET /api/issues` — list with optional status / category / section / reporter / search / limit filters
- `GET /api/issues/{id}` — single issue detail with comments
- `POST /api/issues` — create an issue (no user session; the caller passes a reporter user id in the body)
- `GET /api/issues/{id}/comments` — list conversation comments
- `POST /api/issues/{id}/comments` — post a comment to the conversation thread
- `PATCH /api/issues/{id}/status` — update status (accepts string enum names)
- `PATCH /api/issues/{id}/assignee` — set/clear assignee
- `PATCH /api/issues/{id}/section` — change section (re-route)
- `PATCH /api/issues/{id}/github-issue` — link GitHub issue
- All endpoints require `X-Api-Key` header (configured via `ISSUES_API_KEY` env var)
- 503 if API key not configured, 401 if key invalid
- Enum values serialized as strings consistently (GET and PATCH)
- Handler / reporter context included in responses: name, email, userId, preferred language
- Comment tracking: count on list, full comment history on detail

### US-28.4: Notifications

**As** a reporter or handler, **I want** to know when something happens on an issue I'm involved in, **so that** I don't have to refresh `/Issues` to keep the conversation moving.

**Acceptance Criteria:**
- When a new issue is submitted, an in-app `NotificationSource.IssueSubmitted` notification fans out to every viewer for whom the issue is in-queue (Admin + role-holders of `issue.Section`)
- When a comment is posted, an in-app notification goes to the **other party** — handlers + assignee when the reporter comments, the reporter + assignee when a handler comments. Email is only sent when a **handler** comments (to the reporter); reporter→handler comments are in-app only because handlers already see new comments in their queue.
- When status changes, the reporter and current assignee are notified
- When an issue is assigned, the new assignee is notified
- Emails are queued via the email outbox (`OutboxEmailService`) and localized in the recipient's preferred language (en/es/de/fr/it/ca)
- Effective notification email is resolved through `IUserEmailService.GetNotificationTargetEmailsAsync` so verified-email and forwarding rules apply

### US-28.5: Conversation Thread

**As** a reporter or handler, **I want** to have a conversation thread on an issue, **so that** we can discuss the problem back and forth without switching to email.

**Acceptance Criteria:**
- `IssueComment` entity tracks individual comments (content, sender, timestamp); aggregate-local to `Issue`
- Both reporters and handlers can post comments via the detail view
- Comments displayed chronologically in the detail panel, interleaved with audit events ("Status changed to InProgress", "Assigned to Jane") in a single thread view
- Reporter posts a comment on a terminal issue → the issue auto-reopens to `Open` and an audit row records the implicit status change
- Handler can post a comment and atomically mark the issue resolved in the same request ("Comment & mark resolved")
- "Ball-in-court" is derived from the latest comment's `SenderUserId` vs `Issue.ReporterUserId` — there is no boolean column for "needs reply"

### US-28.6: Section Routing

**As** the org **and** as the system, **I want** issues to land in the right queue automatically based on which section they're about, **so that** triage scales as we add coordinators per section.

**Acceptance Criteria:**
- `Issue.Section` is one of the constants in `IssueSectionRouting.AllKnownSections` (Tickets, Camps, Teams, Shifts, Onboarding, Profiles, Budget, Governance, Legal, CityPlanning) or null
- `IssueSectionRouting.RolesFor(section)` is the routing table mapping section → role(s) whose holders see that section; Admin is always implicit
- Null `Section` falls to the Admin queue only
- Routing is data-driven: a change to `IssueSectionRouting.RolesFor` takes effect immediately (sections are stored as strings; no migration needed)
- The widget infers `Section` from `PageUrl` via `IssueSectionInference.FromPath` when the reporter doesn't pick one explicitly
- Handlers can re-route (change `Section`) on any non-terminal issue

## Data Model

See [`docs/sections/Issues.md`](../sections/Issues.md) for full field-level detail on `Issue`, `IssueComment`, and the `IssueStatus` / `IssueCategory` enums. Owned tables: `issues`, `issue_comments`. Cross-section FKs (`ReporterUserId`, `AssigneeUserId`, `ResolvedByUserId`, `SenderUserId`) are FK-only — the navigation properties are `[Obsolete]`-marked and display data is stitched in `IssuesService` via `IUserService`.

**Screenshot storage:** `wwwroot/uploads/issues/{issueId}/{guid}.{ext}`

## Authorization Matrix

| Endpoint | Auth |
|----------|------|
| `GET /Issues` | `[Authorize]` — Admin sees all; section role-holders see issues whose `Section` maps to one of their roles; regular humans see only their own |
| `GET /Issues/{id}` | `[Authorize]` — handler (Admin or section role-holder) **or** the reporter |
| `GET /Issues/New` | `[Authorize]` (any authenticated user) |
| `POST /Issues` (Submit) | `[Authorize]` (any authenticated user) |
| `POST /Issues/{id}/Comments` | `[Authorize]` — handler **or** reporter (reporter comment on terminal status auto-reopens) |
| `POST /Issues/{id}/Status` | `[Authorize]` + handler check (Admin or `IssueSectionRouting.RolesFor(issue.Section)`) |
| `POST /Issues/{id}/Assignee` | `[Authorize]` + handler check |
| `POST /Issues/{id}/Section` | `[Authorize]` + handler check |
| `POST /Issues/{id}/GitHubIssue` | `[Authorize]` + handler check |
| `* /api/issues/*` | API key (`X-Api-Key` header) |

**Handler check:** done in the controller via `User.IsInRole(...)` (claims-first per coding-rules), comparing the user's roles against `IssueSectionRouting.RolesFor(issue.Section)` plus implicit Admin. The handler check is per-issue (depends on the issue's `Section`), not a static `[Authorize(Roles = ...)]` attribute.

## URL Routes

| Route | Controller | Action |
|-------|-----------|--------|
| `GET /Issues` | IssuesController | Index |
| `GET /Issues/New` | IssuesController | New |
| `POST /Issues` | IssuesController | Submit |
| `GET /Issues/{id}` | IssuesController | Detail |
| `POST /Issues/{id}/Comments` | IssuesController | PostComment |
| `POST /Issues/{id}/Status` | IssuesController | UpdateStatus |
| `POST /Issues/{id}/Assignee` | IssuesController | UpdateAssignee |
| `POST /Issues/{id}/Section` | IssuesController | UpdateSection |
| `POST /Issues/{id}/GitHubIssue` | IssuesController | SetGitHubIssue |
| `GET /api/issues` | IssuesApiController | List |
| `GET /api/issues/{id}` | IssuesApiController | Get |
| `POST /api/issues` | IssuesApiController | Create |
| `GET /api/issues/{id}/comments` | IssuesApiController | GetComments |
| `POST /api/issues/{id}/comments` | IssuesApiController | PostComment |
| `PATCH /api/issues/{id}/status` | IssuesApiController | UpdateStatus |
| `PATCH /api/issues/{id}/assignee` | IssuesApiController | UpdateAssignee |
| `PATCH /api/issues/{id}/section` | IssuesApiController | UpdateSection |
| `PATCH /api/issues/{id}/github-issue` | IssuesApiController | SetGitHubIssue |

## Claude Code Integration

The Issues API is the read/write surface Claude Code agents use to triage and follow up on issues during dev sessions. This replaces the older `/triage` skill that worked exclusively against the Feedback API.

- **API key:** `ISSUES_API_KEY` env var on the server. `ApiKeyAuthFilter` enforces the `X-Api-Key` header on every `/api/issues/*` route. 503 if the key isn't configured at all (so we don't silently accept anonymous traffic on a missing-key server); 401 if the key is wrong.
- **Workflow:** an agent calls `GET /api/issues?status=Triage` to pull the current triage queue, optionally narrowed by `section=` for per-section sweeps. For each issue it can `POST /api/issues/{id}/comments` to ask a clarifying question, `PATCH /api/issues/{id}/status` to advance through the lifecycle, `PATCH /api/issues/{id}/assignee` to route the issue to a human, `PATCH /api/issues/{id}/section` to re-route, or `PATCH /api/issues/{id}/github-issue` to link a freshly-created GitHub issue.
- **Audit:** API-initiated changes are audit-logged (`AuditAction.IssueStatusChanged`, etc.). Because the API path has no user session, the actor is recorded as `null` and the audit metadata records that the change came from the API.
- **Local config:** `ISSUES_API_URL` / `ISSUES_API_KEY` go in `.claude/settings.local.json` (gitignored) so the agent picks them up without leaking the key into the repo.
- **Admin visibility:** `ISSUES_API_KEY` configuration status is shown on `/Admin/Configuration`.

## Navigation

- **Top nav:** "Issues" link visible to all authenticated users; nav badge (`NavBadges` ViewComponent, queue `issues`) shows the actionable count for the current viewer (sum across all sections they own + their own reported issues that need their reply).
- **Floating widget:** `IssuesWidgetViewComponent` renders on every page for authenticated users.
- **`/Admin/Configuration`:** shows whether `ISSUES_API_KEY` is configured.

## Related Features

- `27-feedback-system.md` — separate global feedback queue triaged by `FeedbackAdmin`. Issues is the section-routed sibling.
- Email outbox (`EmailOutboxMessage`) — used for comment notification emails.
- Notifications (`37-notification-inbox.md`) — `NotificationSource.IssueSubmitted`, `NotificationSource.IssueComment`, `NotificationSource.IssueStatusChanged`, `NotificationSource.IssueAssigned`.
- Audit log — every issue mutation is recorded.
- `NavBadges` ViewComponent — extended with the `issues` queue for actionable item count.
- Role management — `TicketAdmin`, `CampAdmin`, `TeamsAdmin`, `Board`, `ConsentCoordinator`, `VolunteerCoordinator`, `HumanAdmin`, `NoInfoAdmin`, `FinanceAdmin` roles all gate section-specific issue queues per `IssueSectionRouting.RolesFor`.
