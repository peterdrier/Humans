<!-- freshness:triggers
  src/Sections/Humans.Issues/**
  src/Sections/Humans.Issues.Contracts/**
-->
<!-- freshness:flag-on-change
  Issue lifecycle, auto-reopen, role-based section routing, screenshot validation, and reporter-vs-handler authorization — review when Issues service/entities/controllers change.
-->

# Issues — Section Invariants

In-app issue tracker (bugs, features, questions) with screenshots, role-routed triage, and a reporter↔handler conversation thread.

## Concepts

- An **Issue** is an in-app submission from a human — a bug report, feature request, or question. It is routed to the section's role-holders based on `Issue.Section` and stays in their queue until terminal.
- An **IssueComment** is one entry in an issue's conversation thread, posted by either the reporter or a handler. There is no admin/reporter flag on the row — sender role is derived by comparing `SenderUserId` to `Issue.ReporterUserId`.
- **Section** is a free-form string drawn from `IssueSectionRouting.AllKnownSections` (Tickets, Camps, Teams, Shifts, Onboarding, Profiles, Budget, Governance, Legal, CityPlanning, Scanner) or `null`. Stored as a string so the routing table can change without migrations. Null-section issues fall to the Admin queue only.
- A **Handler** is a user who can triage, assign, change status of, or comment as a non-reporter on an issue: `Admin`, or any role listed by `IssueSectionRouting.RolesFor(issue.Section)`.
- **Ball-in-court** is **derived**, not stored: compare the latest comment's `SenderUserId` to `Issue.ReporterUserId`. There is no boolean column for "needs reply" — it is computed at read time.
- The Detail view's **activity thread has no event table**: status/assignee/section/GitHub-link events are reconstructed at read time from the audit log (the four `AuditAction.Issue*` values, fetched via `IAuditLogService`) and merged chronologically with `IssueComment` rows in `IssuesService`. The audit log is the source of truth for inline events — do not add an issue-events schema.
- **Issue status** tracks the lifecycle: Triage, Open, InProgress, Resolved, WontFix, Duplicate. Resolved/WontFix/Duplicate are terminal.

## Data Model

### Issue

**Table:** `issues`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ReporterUserId | Guid | FK → User (reporter), Restrict on delete — **FK only, no nav property** (removed nobodies-collective/Humans#1188). Restrict is intentional: `IAccountDeletionService` anonymizes the User row in place rather than removing it, so the FK should never trip; if anyone bypasses the deletion service, Restrict makes the DB reject the operation rather than silently wiping reported issues. |
| Section | string? | One of `IssueSectionRouting.AllKnownSections` or null. Max 64. Indexed. |
| Category | IssueCategory | Bug, Feature, Question. Stored as string (max 32). |
| Title | string | Issue title (max 200) |
| Description | string | Issue body (max 5000) |
| PageUrl | string? | URL captured by the floating widget (max 2000); null for `/Issues/New` and API submissions |
| UserAgent | string? | Browser user agent (max 1000) |
| AdditionalContext | string? | Extra context captured at submission (e.g., reporter's roles) (max 2000) |
| ScreenshotFileName | string? | Original filename (max 256) |
| ScreenshotStoragePath | string? | Relative path under `wwwroot/uploads/issues/{issueId}/` (max 512) |
| ScreenshotContentType | string? | MIME type (`image/jpeg`, `image/png`, `image/webp`) (max 64) |
| Status | IssueStatus | Triage (default), Open, InProgress, Resolved, WontFix, Duplicate. Stored as string (max 32). |
| GitHubIssueNumber | int? | Linked GitHub issue (org-scoped) |
| DueDate | LocalDate? | Optional handler-set deadline |
| AssigneeUserId | Guid? | FK → User, SetNull on delete — **FK only, no nav property** |
| CreatedAt | Instant | Submission timestamp |
| UpdatedAt | Instant | Last modification |
| ResolvedAt | Instant? | When resolved/won't-fix/duplicate |
| ResolvedByUserId | Guid? | FK → User, SetNull on delete — **FK only, no nav property** |

**Indexes:** `Status`, `CreatedAt`, `ReporterUserId`, `AssigneeUserId`, `Section`, `(Section, Status)`.

**Cross-section FKs:** `ReporterUserId`, `AssigneeUserId`, `ResolvedByUserId` → `Users/Identity.User` — **FK only**, no navigation property at all (the `Reporter`, `Assignee`, `ResolvedByUser` nav props were removed, not just deprecated — nobodies-collective/Humans#1188); EF configures the FK/cascade behavior via `HasOne<User>()` with no nav reference, and Application code stitches display names via `IUserServiceRead.GetUserInfosAsync`.

### IssueComment

**Table:** `issue_comments`

Conversation thread between reporter and handlers. Aggregate-local (same section as Issue). `Issue.Comments ↔ IssueComment.Issue` is a legal `.Include` inside the repository.

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| IssueId | Guid | FK → Issue, Cascade on delete |
| SenderUserId | Guid? | FK → User, SetNull on delete — null when posted via API key (no user session) |
| Content | string | Comment body (max 5000) |
| CreatedAt | Instant | When the comment was posted |

There is no per-comment reporter/handler flag — reporter-vs-handler is derived by comparing `SenderUserId` to `Issue.ReporterUserId`. Ball-in-court is derived from the latest comment's sender.

**Indexes:** `IssueId`, `CreatedAt`.

**Cross-section FKs:** `SenderUserId` → `Users/Identity.User` — **FK only**, no navigation property (`SenderUser` was removed, not just deprecated — nobodies-collective/Humans#1188).

### IssueStatus

| Value | Description |
|-------|-------------|
| Triage | Default for new submissions; not yet picked up by a handler |
| Open | Acknowledged by a handler, not yet started |
| InProgress | Being worked on |
| Resolved | Fixed or addressed (terminal) |
| WontFix | Will not be addressed (terminal) |
| Duplicate | Closed as a duplicate of another issue (terminal) |

`IssueStatus.IsTerminal()` returns true for Resolved, WontFix, Duplicate.

### IssueCategory

| Value | Description |
|-------|-------------|
| Bug | Bug report |
| Feature | Feature request |
| Question | General question |

## Routing

Two controllers serve this section:

- `IssuesController` (`/Issues`, `/Issues/New`, `/Issues/{id}`, `/Issues/{id}/Comments`, `/Issues/{id}/Status`, `/Issues/{id}/Assignee`, `/Issues/{id}/Section`, `/Issues/{id}/GitHubIssue`) — cookie-authenticated humans.
- `IssuesApiController` (`/api/issues/*`) — API-key authenticated; no user session. Used by Claude Code agents and external integrations.

`Issue.Section` selects which roles see the issue in their queue (see `IssueSectionRouting.RolesFor`); a null section is Admin-only. Section is editable by handlers as long as the issue is non-terminal — re-routing an issue is just changing its `Section` string.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Submit an issue (with optional screenshot). View, comment on, and reopen by commenting on issues they reported. Cannot triage or change status. |
| Section role-holder (e.g., `TicketAdmin`, `CampAdmin`, `TeamsAdmin`, `Board`, …) | All reporter capabilities. Additionally: list, view, comment on, change status, assign, change section, link GitHub issue **on issues whose `Section` maps to their role** (per `IssueSectionRouting.RolesFor`). |
| Admin | All section-role-holder capabilities, on every section including null-section issues. |
| API (key auth) | List, get, create, post comments, update status, update assignee, set GitHub issue, change section via `/api/issues/*` (no user session required; the section's own `IssuesApiKeyAuthFilter`, over the `IssuesApi` config section, enforces the key). |

## Invariants

- Every issue is linked to the human who submitted it (`ReporterUserId` is required).
- Status flows Triage → Open → InProgress → Resolved/WontFix/Duplicate. Transitioning out of a terminal status clears `ResolvedAt` and `ResolvedByUserId`.
- A reporter posting a comment on a terminal issue **auto-reopens** it to `Open` (audit-logged as `AuditAction.IssueStatusChanged` with actor = the reporter).
- A handler may post a comment and atomically mark the issue resolved in the same request ("Comment & mark resolved"). The status change is audit-logged after the comment is persisted.
- Visibility: a regular human sees only the issues they reported. A section role-holder sees all issues whose `Section` maps to one of their roles. Admin sees every issue.
- Mutation: only handlers (Admin or section role-holders) may change status, assignee, section, or GitHub link, or post a comment as a non-reporter. The reporter may post a comment but cannot change other fields.
- `Section` is editable in any non-terminal state (handlers may re-route at any time before the issue closes).
- Screenshots are validated for allowed file types (JPEG, PNG, WebP) and a max size of 10 MB before storage.
- All issue mutations are audit-logged via `IAuditLogService.LogAsync` (`AuditAction.IssueStatusChanged`, `AuditAction.IssueAssigneeChanged`, `AuditAction.IssueSectionChanged`, `AuditAction.IssueGitHubLinked`). Audit writes happen **after** the business save, never before — see `coding-rules.md` "audit-after-save".
- API-initiated changes are audit-logged with actor `null` (the API-key path has no user identity); the audit row's metadata records that the change came from the API.

## Negative Access Rules

- A regular human **cannot** see issues they did not report — even in sections they do not own.
- A section role-holder **cannot** see, comment on, or mutate issues whose `Section` does not map to one of their roles. (Their elevated access is scoped to their section; null-section issues are Admin-only.)
- A regular human **cannot** change an issue's status, assignee, section, or GitHub link — even on issues they reported. (They may comment, and that comment may auto-reopen a terminal issue, but the status field itself is handler-only.)
- An API client **cannot** call `/Issues/*` (the cookie-authenticated controller) — and a cookie-authenticated user **cannot** call `/api/issues/*` without an API key.

## Triggers

- When an issue is submitted, an in-app `NotificationSource.IssueSubmitted` notification fans out to every handler for whom the issue is in-queue (Admins + role-holders of `IssueSectionRouting.RolesFor(issue.Section)`), excluding the reporter, using `sourceKey: issue.Id.ToString()`. The nav-badge actionable count for those same handlers is invalidated in the same step. When the issue transitions to a terminal status, `IssuesService` calls `ResolveSubmittedNotificationsAsync`, which resolves those `IssueSubmitted` notifications by that sourceKey.
- When a comment is posted, an in-app notification fans out to the **other party** — handlers + assignee when the reporter comments, the reporter + assignee when a handler comments. Email is sent **only when a handler comments** (to the reporter), via `IUserEmailService.GetNotificationTargetEmailsAsync` + a localized `IEmailService.SendAsync(IEmailMessageFactory.IssueComment(...))` queued through the email outbox (`OutboxEmailService` in production). Reporter→handler comments are in-app only — handlers see the new comment in their queue without an email ping.
- When status changes, the reporter and current assignee are notified.
- When an issue is assigned, the new assignee is notified.
- When a reporter comments on a terminal issue, the issue is auto-reopened to `Open` and an audit row records the implicit status change with actor = the reporter.
- When the actionable count for a viewer could have changed (issue created, status changed, comment posted, section changed, assignee changed), the nav-badge cache is invalidated via `INavBadgeCacheInvalidator`.
- **Retention.** Issues that have been in a terminal state (Resolved / WontFix / Duplicate) for at least 6 months are deleted by `CleanupIssuesJob` (daily Hangfire job at 05:00 UTC). Comments cascade via FK; the screenshot directory under `wwwroot/uploads/issues/{id}/` is removed best-effort in the same pass. A reporter comment that auto-reopens a terminal issue clears `ResolvedAt`, which automatically excludes the issue from the retention sweep.
- When a user is purged via `IAccountDeletionService.PurgeAsync`, the User row is **anonymized in place** (display name + email replaced with sentinels) — the row itself stays, so issues they reported persist with their FKs intact and continue to render under the anonymized name. The `Reporter` FK uses `Restrict` (not `Cascade`) so a stray `db.Users.Remove(user)` would be rejected by the DB rather than silently wiping every issue. Assignee / resolved-by FKs on issues where the deleted-user was acting in those roles set to null. Their comments set `SenderUserId` to null but keep the row.

## Cross-Section Dependencies

- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — reporter / assignee / resolver / comment-sender display names (cross-domain navs are stripped per `design-rules.md §6c`).
- **Profiles:** `IUserEmailService.GetNotificationTargetEmailsAsync` — resolves the effective notification email for the reporter when a handler comments, and for the assignee on status/assignment changes.
- **Auth:** `IRoleAssignmentService` — used by the section-routing logic to fan out notifications to the set of users who currently hold a role mapped to the issue's `Section`.
- **Email:** `IEmailService.SendAsync` with `IEmailMessageFactory.IssueComment` — comment-thread emails (queued through the outbox in production).
- **Notifications:** `INotificationService.SendAsync` — `NotificationSource.IssueSubmitted`, `NotificationSource.IssueComment`, `NotificationSource.IssueStatusChanged`, `NotificationSource.IssueAssigned` in-app notifications.
- **Audit Log:** `IAuditLogService.LogAsync` — every mutation (`AuditAction.IssueStatusChanged`, `AuditAction.IssueAssigneeChanged`, `AuditAction.IssueSectionChanged`, `AuditAction.IssueGitHubLinked`).
- **Caching:** `INavBadgeCacheInvalidator` (global nav count) and `IIssuesBadgeCacheInvalidator` (per-viewer actionable count) — both invalidated whenever the actionable count for a viewer could have changed.
- **GDPR:** implements `IUserDataContributor` to export the user's reported issues and their comments under `GdprExportSections.Issues`.

## Architecture

**Owning services:** `IssuesService`
**Owned tables:** `issues`, `issue_comments`
**Status:** (G5) Moved into its own project — `src/Sections/Humans.Issues`, with the cross-section surface on the `Humans.Issues.Contracts` leaf (nobodies-collective/Humans#866).

- `IssuesService` (`Humans.Issues.Services`, `internal sealed`) depends only on Application-layer abstractions and its own repository; the architecture test pins that its constructor takes no `DbContext` and no `IDbContextFactory<>`.
- `IIssuesRepository` (impl `src/Sections/Humans.Issues/Data/IssuesRepository.cs`) is the only code path that touches `issues` and `issue_comments` via `DbContext`. Singleton + `IDbContextFactory<IssuesDbContext>` per `design-rules.md §15b`.
- **Aggregate-local navs kept:** `Issue.Comments ↔ IssueComment.Issue`. Both sides live in Issues-owned tables, so `.Include(i => i.Comments)` is legal inside the repository.
- **Decorator decision — no caching decorator.** Issues are per-section queues triaged by handlers, not a hot bulk-read path. Same rationale as Feedback / User / Governance.
- **Cross-domain navs removed, not just `[Obsolete]`-marked:** `Issue.Reporter`, `.Assignee`, `.ResolvedByUser`, `IssueComment.SenderUser` no longer exist as nav properties (nobodies-collective/Humans#1188). The repository does not `.Include()` them (there's nothing to include); `IssuesService` resolves display data in memory via `IUserServiceRead.GetUserInfosAsync` (design-rules §6b). EF configures the cross-section FK/cascade behavior with `HasOne<User>()` and no nav reference — there are no `#pragma warning disable CS0618` blocks left in the EF configurations.
- **Cross-section calls** — the public interfaces this section consumes: `IUserServiceRead`, `IUserEmailService`, `IRoleAssignmentService`, `IEmailService`, `INotificationService`, `IAuditLogService`, `INavBadgeCacheInvalidator`, `IIssuesBadgeCacheInvalidator`.
- **Nav-badge cache invalidation** uses two invalidators: `INavBadgeCacheInvalidator` (global nav count) and `IIssuesBadgeCacheInvalidator` (per-viewer actionable count). `IssuesService` holds both; the architecture test pins `IIssuesBadgeCacheInvalidator` explicitly. `IssuesBadgeCacheInvalidator` (impl in `Humans.Infrastructure/Caching/MemoryCacheInvalidators.cs`) stays in Base — `MemoryCacheExtensions.InvalidateNavBadgeCounts()` drops every badge count by the same `CacheKeys` constant — and is registered Scoped from `Section.Register`.
- **Cross-section surface** — `Humans.Issues.Contracts` carries `IIssuesServiceRead.GetActionableCountForViewerAsync` (Shell's `NavBadgesViewComponent`), `IIssuesRetention.PurgeExpiredAsync` (driven by `CleanupIssuesJob`, which moved from `Humans.Infrastructure/Jobs` into this section's `Contracts/` at G5 lane 5b-1) and `IssueCategory` (named by `Humans.Agent`'s `route_to_issue` proposal). Everything else is internal to the section.
- **Architecture test** — `tests/Humans.Issues.Tests/Architecture/IssuesArchitectureTests.cs` pins the shape (constructor takes no EF type, the section localizes only through `IssuesResource`, audit discriminators are literals).

### Touch-and-clean guidance

- Do **not** reintroduce `.Include(i => i.Reporter | i.Assignee | i.ResolvedByUser)` or `.Include(c => c.SenderUser)` anywhere — new read paths should go through the repository's existing methods (or extend the repository with a new narrowly-shaped query) and stitch display data in `IssuesService` via `IUserServiceRead`.
- Aggregate-local `.Include(i => i.Comments)` is fine — `issue_comments` is Issues-owned.
- `IssuesService` injects `IMemoryCache` for the per-viewer actionable-count cache (`CacheKeys.IssuesBadge(userId)`, short-TTL). This is appropriate per design-rules §15 (short-TTL request-acceleration cache, not a canonical domain-data cache). Do **not** add new `IMemoryCache` entries for domain data — use `IIssuesBadgeCacheInvalidator.Invalidate/InvalidateMany` to evict the count cache after mutations, and `INavBadgeCacheInvalidator` to invalidate the global nav count.
- New section names referenced in `Issue.Section` must be added to `IssueSectionRouting.AllKnownSections` and have an entry in `IssueSectionRouting.RolesFor`. Do not invent free-form section strings outside this constant set.
- New tables that logically belong to Issues must be added to `design-rules.md §8`; do not silently grow the section's footprint.
