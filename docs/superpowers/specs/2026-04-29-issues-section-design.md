# Issues Section — Design Spec

**Date:** 2026-04-29
**Status:** Draft — pending review
**Owner:** Peter Drier
**Related:** Design system bundle (`humans-design-system/project/ui-kits/issue-tracker.html`, `tokens.css`); existing Feedback section docs (`docs/sections/Feedback.md`, `docs/features/27-feedback-system.md`) — being retired.

## 1. Problem

The Feedback section (87 reports across 6 weeks of production use) outgrew its design:

- **Single inbox, single triager.** Every report funneled to Admin (one person) for hand-off. Reports about Tickets, Camps, or Volunteer onboarding belong with the people who run those areas, not Admin.
- **Assignment never landed.** `assignedToUserId` and `assignedToTeamId` exist on `feedback_reports` and are 0/87 used. The fields exist; the workflow that puts a report in the right person's queue does not.
- **State model is too thin for support traffic.** ~20% of reports are end-user Questions ("how do I link my partner's ticket", "is the food voucher still a thing") — these need a real triage → resolution flow visible to all parties, not the same `Open/Acknowledged/Resolved` state machine used for code bugs.
- **UI is rough.** Reporters can't easily see where their issue stands; coordinators can't easily track a back-and-forth.

The replacement is a new top-level **Issues** section. It is **not** a migration of the Feedback section — it is a parallel system. New submissions go to Issues from day 0; existing Feedback reports drain to terminal status through the old UI; once `feedback_reports` has no non-terminal rows, the old code and tables are deleted.

## 2. Decisions taken in brainstorming

| Decision | Choice |
|---|---|
| Cut-over | **No migration. New section, new URL, new tables, new namespace.** Old Feedback runs in parallel until backlog drains. |
| Section name | **Issues.** URL `/Issues`, namespace `Humans.Application.Issues`, tables `issues` + `issue_comments`, entities `Issue` + `IssueComment`. |
| Routing model | **Section field on each issue + section→role mapping.** Anyone with a matching role sees that section's queue. `Admin` sees all; `Section = null` falls to Admin only. |
| Submission Section | **Inferred from `PageUrl`, reporter can override.** |
| State machine | Six states: `Triage` (entry) / `Open` / `InProgress` / `Resolved` / `WontFix` / `Duplicate`. Reporter comment on terminal auto-reopens to `Open`. |
| Assignee | One optional `AssigneeUserId`. Set/changed by anyone with the section's role or Admin. Surfaces in the inline thread as a status-change event. |
| Inline thread | Comments and status/assignee/section/GitHub-link audit events render in one chronological thread (Jira/GitHub-style). |
| UI shape | Three views — Index (filtered list + chips), Detail (thread + facts panel), New (form). Sits in the existing admin shell topbar/sidebar. |
| Visual reference | `humans-design-system/project/ui-kits/issue-tracker.html` is authoritative for layout, type, and palette. |

## 3. Section → role routing

| `Section` value | Owning role(s) besides `Admin` |
|-----------------|---------------------------------|
| `Tickets` | `TicketAdmin` |
| `Camps` | `CampAdmin` |
| `Teams` | `TeamsAdmin` |
| `Shifts` | `NoInfoAdmin` |
| `Onboarding` | `ConsentCoordinator`, `VolunteerCoordinator`, `HumanAdmin` |
| `Profiles` | `HumanAdmin` |
| `Users` | `HumanAdmin` |
| `Budget` | `FinanceAdmin` |
| `Governance` | `Board` |
| `Legal` | `ConsentCoordinator` |
| `CityPlanning` | `CampAdmin` |
| `(null)` | (Admin only) |

`Admin` is implicit on every row (global superset). Mapping is editable in code (`IssueSectionRouting` static class) and is acknowledged to be a starting point — adjustable as the org learns.

The reporter-side dropdown on `/Issues/New` shows **friendly area labels** ("Shifts & volunteering", "Camp setup & ops", "Members & profiles", etc.) that map onto the technical Section enum values.

## 4. Data model

### `Issue` (table `issues`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `ReporterUserId` | Guid | FK → User. Cascade on delete. Cross-domain nav `[Obsolete]`-marked per design-rules §6c. |
| `Section` | string? | Nullable. One of the routing-table values; null routes to Admin. |
| `Category` | enum | `Bug` / `Feature` / `Question` |
| `Title` | string (max 200) | Required. One-line summary. |
| `Description` | string (max 5000) | Required. Body of the report. |
| `PageUrl` | string? (max 2000) | Captured from the floating widget; null for `/Issues/New` and API submissions. |
| `UserAgent` | string? (max 1000) | |
| `AdditionalContext` | string? (max 2000) | Reporter's roles at submission time, etc. |
| `ScreenshotFileName` | string? (max 256) | |
| `ScreenshotStoragePath` | string? (max 512) | Stored under `wwwroot/uploads/issues/{issueId}/{guid}.{ext}` |
| `ScreenshotContentType` | string? (max 64) | `image/jpeg` / `image/png` / `image/webp` only |
| `Status` | enum | `Triage` / `Open` / `InProgress` / `Resolved` / `WontFix` / `Duplicate`. Submissions land in `Triage`. |
| `AssigneeUserId` | Guid? | FK → User. SetNull on delete. Cross-domain nav `[Obsolete]`-marked. |
| `GitHubIssueNumber` | int? | Linked GH issue/PR number. |
| `DueDate` | LocalDate? | When we've committed an external date. |
| `CreatedAt` | Instant | |
| `UpdatedAt` | Instant | |
| `ResolvedAt` | Instant? | Set on transition to any terminal state (Resolved/WontFix/Duplicate). Cleared on reopen. |
| `ResolvedByUserId` | Guid? | FK → User. SetNull on delete. Cross-domain nav `[Obsolete]`-marked. |

**Indexes:** `Section`, `Status`, `CreatedAt`, `ReporterUserId`, `AssigneeUserId`, `(Section, Status)` composite for queue queries.

**Aggregate-local nav kept:** `Issue.Comments ↔ IssueComment.Issue`. `.Include(i => i.Comments)` is legal in the repo.

### `IssueComment` (table `issue_comments`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `IssueId` | Guid | FK → Issue, cascade on delete. |
| `SenderUserId` | Guid? | FK → User, SetNull on delete. Null when posted via API key (LLM agent path). |
| `Content` | string (max 5000) | |
| `CreatedAt` | Instant | |

**Indexes:** `IssueId`, `CreatedAt`.

There is no per-comment "is admin" flag — the design's "Member" vs "Coordinator" badge is derived from comparing `SenderUserId` to `Issue.ReporterUserId`.

### State transitions

```
                        ┌──────────────────────┐
                        │       Triage         │   ← submission entry point
                        └──────────────────────┘
                          │ ↓ ↓ ↓ ↓ ↓
                          │ │ │ │ │ └──→ Duplicate (terminal)
                          │ │ │ │ └────→ WontFix   (terminal)
                          │ │ │ └──────→ Resolved  (terminal)
                          │ │ └────────→ InProgress
                          │ └──────────→ Open
                          ↓
                       (any state, including Triage)
                          ↓
                Open ⇄ InProgress
                          ↓
                Open / InProgress → Resolved / WontFix / Duplicate

       Reporter comment on terminal issue → auto-reopen to Open
       (clears ResolvedAt, ResolvedByUserId; status becomes Open)
```

Re-routing (changing `Section`) is allowed in any non-terminal state. `AssigneeUserId` may be set or cleared in any non-terminal state. Both produce inline events.

## 5. Authorization

| Action | Allowed by |
|--------|-----------|
| Create issue | Any authenticated user (widget, `/Issues/New`, API key) |
| View own issues | The reporter |
| View issue in section | Anyone with a role mapped to that section, or `Admin` |
| Comment on issue | The reporter; section-role-holders; `Admin` |
| Change `Status` / `AssigneeUserId` / `Section` / `GitHubIssueNumber` / `DueDate` | Section-role-holders; `Admin` |
| API access (key) | `ISSUES_API_KEY` in `X-Api-Key` header. 503 if not configured, 401 if invalid. |

No reporter-vs-handler privacy partition — the design is "everyone on the issue sees everything." This matches Peter's stated decision: "not worried about privacy."

`FeedbackAdmin` role is **not** carried forward. It is retired with the old Feedback section.

## 6. UI — three views

Visual reference: `humans-design-system/project/ui-kits/issue-tracker.html`. Renaissance palette (parchment + aged ink + gold), 62px topbar with `Humans` wordmark + topbar nav (Issues is one of the items), dark sidebar under the bar (existing admin shell). Member-facing pages also access the same `/Issues` URL with the role-filtered list.

### 6.1 Index — `/Issues`

- **Filter pills** above the list: `Open` (default) · `Mine` · `Closed` · `All`, each with count. `Mine` = issues where `ReporterUserId == me` (regardless of role); `Open` = all non-terminal statuses (`Triage` + `Open` + `InProgress`); `Closed` = `Resolved` + `WontFix` + `Duplicate`.
- **Dropdowns:** All types, All areas (Section), Sort (recently updated / newest / oldest / most commented).
- **Search** input on the right (full-text across `Title` + `Description`).
- **Table columns:** ID (`№ 142` styled in italic display font) · Title (with type chip + comment count below) · Status pill · Area (Section tag) · Reporter (avatar + name) · Updated.
- **`+ New issue`** primary button top-right.
- **Pager** + total count at the foot.
- **Visibility:**
    - Reporter (no role): shows only `ReporterUserId == me` (the "Mine" pill = the same set; `Open` and `Closed` are subsets of "Mine" for them).
    - Role-holder: own reports + all issues whose `Section` maps to a role they hold.
    - `Admin`: everything.

### 6.2 Detail — `/Issues/{id}`

- **Header:** `№ 142 · opened 3 days ago` + Title + status pill + type chip + area tag + reporter line.
- **Activity thread** (single column, left): comments and status-change events sorted by `CreatedAt`. Comments render as cards with sender avatar, role badge (Member / Coordinator), timestamp, body. Status events render as a single line with a hollow circle marker ("○ Catarina O. moved from Triage to Open · 2 days ago"). Inline-event types:
    - Status change
    - Assignee set / changed / cleared
    - Section changed
    - GitHub issue linked
- **Composer** at the bottom of the thread: textarea with Write/Preview tabs, "Comment" button, "Comment & mark resolved" combined action. Markdown supported.
- **Facts panel** (right, sticks while scrolling): Status / Type / Area / Reporter / Assignee / Linked GH issue / DueDate / Opened / Last update. Below the facts is a "Change status" select with the six statuses (Resolved/WontFix/Duplicate separated by a divider). Hidden for non-handlers.
- Reporter posting a comment when `Status` is terminal triggers auto-reopen on submit; UI shows the new status in the rendered thread immediately after.

### 6.3 New issue — `/Issues/New`

- **Title** (required, one line).
- **Type** + **Area** selects, side-by-side.
- **Body / Description** textarea (required, markdown supported).
- **Attachments** dropzone (screenshots only, 10 MB cap, JPEG/PNG/WebP).
- **Tip card** on the right: "What makes a good issue?" with steps-to-reproduce/screenshots/search-first guidance, plus a "heads up" pointing real emergencies to email.
- Submit lands in `Triage`.

### 6.4 Floating widget (existing pattern)

Submission widget on every page (members can hit it from anywhere). Pre-fills `PageUrl`; infers `Section` from URL; reporter can override the inferred Section in the modal. Posts to `POST /api/issues` server-side.

## 7. Submit flows

| Path | Inputs available | Status on land | Notes |
|------|------------------|----------------|-------|
| Floating widget | `Title`, `Description`, `Category`, `Section` (inferred), `PageUrl`, `UserAgent`, screenshot | `Triage` | Most common path. |
| `/Issues/New` form | All fields except `PageUrl` (null) | `Triage` | The from-scratch path. |
| `POST /api/issues` (key-authed) | All fields including `ReporterUserId` (the human the agent is helping) | `Triage` | LLM-agent path. `SenderUserId` on subsequent agent comments is null. |

## 8. Notifications

| Event | Recipients | Channel |
|-------|-----------|---------|
| Comment by reporter | Section role-holders + Assignee (if set, and not the commenter) | In-app + email |
| Comment by handler | Reporter + Assignee (if different from commenter) | In-app + email |
| Status change | Reporter + Assignee (if different from changer) | In-app + email |
| Assignee changed | New assignee | In-app + email |
| Section changed | New section's role-holders + reporter | In-app |
| GitHub issue linked | Reporter | In-app |

Emails go through the existing `IEmailService` outbox. Localization in the recipient's preferred language (en/es/de/fr/it). New email method: `IEmailService.SendIssueCommentAsync(...)` (mirrors the existing `SendFeedbackResponseAsync`). New `NotificationSource.IssueComment`, `NotificationSource.IssueStatusChanged`, `NotificationSource.IssueAssigned`.

Nav-badge `queue = "issues"` shows the count of `Open` + `Triage` issues whose section maps to a role I hold (plus my own non-terminal issues with newer handler activity since I last viewed). Cache invalidation via `INavBadgeCacheInvalidator` on every comment, status change, or new issue, mirroring today's Feedback wiring.

## 9. API surface

All routes require `X-Api-Key: $ISSUES_API_KEY` header. 503 if the env var is unset, 401 if the key is invalid.

| Route | Purpose |
|-------|---------|
| `GET /api/issues` | List, optional `?status=&category=&section=&assignee=&limit=` |
| `GET /api/issues/{id}` | Single issue + comments + audit events (the inline thread) |
| `GET /api/issues/{id}/comments` | Comments only |
| `POST /api/issues` | Create on behalf of a user (LLM-agent path). Requires `reporterUserId` in payload. |
| `POST /api/issues/{id}/comments` | Post comment (key-authed; `SenderUserId` is null in the row) |
| `PATCH /api/issues/{id}/status` | Change status, including terminal closures |
| `PATCH /api/issues/{id}/assignee` | Set or clear assignee |
| `PATCH /api/issues/{id}/section` | Re-route |
| `PATCH /api/issues/{id}/github-issue` | Link |

Enum values are serialized as strings. The list response shape mirrors today's `/api/feedback` output (reporter name/email/language; Title; Section; messageCount; lastReporterCommentAt/lastHandlerCommentAt) so the `/triage` skill rewrite is mechanical.

## 10. Audit + inline thread

New `AuditAction` values:

- `IssueStatusChanged`
- `IssueAssigneeChanged`
- `IssueSectionChanged`
- `IssueGitHubLinked`

Logged via `IAuditLogService.LogAsync` with the issue ID as the resource ID. `ActorUserId = "API"` for key-authed mutations.

The Detail view's thread is built by:
1. Loading `IssueComment`s for the issue (aggregate-local).
2. Loading the four `AuditAction` values above for the issue's resource ID via `IAuditLogService` (cross-section call — read-only).
3. Sorting both sets by `CreatedAt` and rendering as a single chronological list.

No new schema for inline events. The audit log is the source.

## 11. Cross-section dependencies

| Dependency | Used for |
|------------|----------|
| `IUserService.GetByIdsAsync` | Reporter / assignee / commenter / resolver display names |
| `IUserEmailService.GetNotificationTargetEmailsAsync` | Recipient email resolution for handler-reply emails |
| `IRoleAssignmentService` (or equivalent) | Resolving "who has role X" for fan-out and queue filtering |
| `IEmailService.SendIssueCommentAsync` (new) | Reply emails through the outbox |
| `INotificationService.SendAsync` | In-app notifications |
| `IAuditLogService.LogAsync` + `GetByResourceIdAsync` | Status / assignee / section / GH-link events for the inline thread |
| `INavBadgeCacheInvalidator` | Nav-badge cache invalidation |
| `IUserDataContributor` (implemented by `IssuesService`) | GDPR export under `GdprExportSections.Issues` (new) |

`Issues` section sits **above** Users / Profiles / Auth — it calls into them, never the reverse. This satisfies the "User/Profile are foundational" rule.

## 12. Architecture (per design-rules)

- **Owning service:** `IssuesService` (`Humans.Application.Services.Issues.IssuesService`). Application-layer; never imports `Microsoft.EntityFrameworkCore`.
- **Repository:** `IIssuesRepository` (`Humans.Infrastructure/Repositories/Issues/IssuesRepository.cs`). Owns the SQL surface. Singleton with `IDbContextFactory<HumansDbContext>` per call.
- **Owned tables:** `issues`, `issue_comments`.
- **Status:** (A) Migrated from day 1 — built to current architecture conventions, no migration debt.
- **Cross-domain navs `[Obsolete]`-marked** on the entity: `Issue.Reporter`, `Issue.Assignee`, `Issue.ResolvedByUser`, `IssueComment.Sender`. The repo never `.Include()`s them; the service stitches display data via the cross-section interfaces above.
- **No caching decorator.** Per-user / role-triaged read pattern, not a hot bulk-read.

## 13. Retiring `/Feedback`

**Day 0 (this PR):**
- `/Issues` ships.
- The site-wide floating widget is updated to post to `/api/issues` (i.e., `/Feedback` no longer receives new submissions from the widget).
- `/Feedback` page remains accessible to admins and reporters who have open reports there. No new code is written for it.
- `FEEDBACK_API_KEY` and `ISSUES_API_KEY` coexist.

**Day N (a separate PR, when `feedback_reports` has zero non-terminal rows):**
- Drop `Humans.Application.Services.Feedback`, `Humans.Infrastructure.Repositories.Feedback`, `FeedbackController`, `FeedbackApiController`, `FeedbackWidgetViewComponent`, `FeedbackSectionExtensions`, `FeedbackViewModels`, `FeedbackReport` + `FeedbackMessage` entities, all configurations, all migrations.
- Drop the `feedback_reports` and `feedback_messages` tables in a single migration.
- Drop `FeedbackAdmin` role (`RoleNames.FeedbackAdmin`, `RoleGroups.FeedbackAdminOrAdmin`, `PolicyNames.FeedbackAdminOrAdmin`, any `[Authorize(Roles=...)]` references).
- Drop `FEEDBACK_API_KEY` env var, the `/Admin/Configuration` diagnostic for it, the localization keys (`Feedback_*`), and the nav-badge `queue = "feedback"` wiring.
- Remove `docs/sections/Feedback.md` and `docs/features/27-feedback-system.md`. Update `MEMORY.md` references and the `/triage` skill.

No data migration. No URL aliases. No backwards-compatibility shims.

## 14. Out of scope

- Priority field, tags/labels, watchers, related-issue links, time tracking — none of these were requested and the data does not motivate them.
- Internal-only / private comments — Peter has confirmed there is no privacy requirement.
- A built-in resolution-note field separate from comments — comments are the only conversation surface; the closing comment is the resolution note.
- Per-section sub-roles (e.g., a "Tickets coordinator" who is not `TicketAdmin`). The existing role taxonomy is the routing taxonomy.
- Auto-close on inactivity. Not at 500-user scale.
- Email submission ("send a problem to issues@…"). Possible follow-up; not in this PR.

## 15. Open questions / follow-ups

- Section→role mapping is committed as the day-0 starting point. Adjustment is a code change in `IssueSectionRouting`; document in `docs/sections/Issues.md` once written.
- The `/triage` Claude Code skill (`.claude/skills/triage/SKILL.md`) needs to be rewritten to point at the new API surface. Plan once cut-over is in place.
- Consider whether non-Admin role-holders should be able to **delete** their own comments (currently no delete path is specified — comments are append-only).
- LLM-agent submission path needs a small `/Admin/Configuration` indicator showing `ISSUES_API_KEY` set/unset, like the existing `FEEDBACK_API_KEY` row.

## 16. Acceptance criteria

- A reporter can submit an issue from any page (widget) or `/Issues/New`. Submission lands in `Triage`.
- A `TicketAdmin` (without `Admin`) sees only Tickets-section issues + their own reports on `/Issues`. Other section-role-holders behave analogously.
- An issue's Section can be changed by a handler; the new section's role-holders see it; a status-change-style event appears in the thread.
- Auto-reopen: a reporter posting a comment on a `Resolved` / `WontFix` / `Duplicate` issue moves status to `Open` atomically with the comment insert.
- The Detail view's thread merges comments and audit events in one chronological list.
- The floating widget on every page submits to `/api/issues`.
- `/api/issues` accepts `ISSUES_API_KEY` and is documented for the LLM-agent submission path.
- Old `/Feedback` continues to function read-and-close-only for the existing 22 non-terminal reports.
- After the cut-over PR, the codebase contains no references to `Feedback*` types, `FeedbackAdmin` role, or `feedback_*` tables.
