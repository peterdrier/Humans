# Release TODOs

Audit date: 2026-02-05
Last synced: 2026-03-18T01:55

---

## Open Work — Prioritized

### Priority 1: Bugs / User-Reported Issues

#### #134: Standardize role authorization to prevent Identity/RoleAssignments mismatch
Two role-checking systems (ASP.NET Identity claims vs RoleAssignments table) can disagree. Need a single source of truth. Label: bug.

### Priority 2: Small-Medium Enhancements

#### #77: Reasons why an Asociado is accepted (or applying)
Board members voting on Asociado applications should be required to select which bylaw criteria the applicant meets. Optionally, the applicant could also state their reasons when applying.

#### #127: Add incomplete signup lifecycle — reminders and auto-deletion
Send reminders to humans who signed up but never completed profile/consent, auto-delete after configurable period. Label: enhancement.

#### #137: Public team pages with editable markdown content, CTAs, and member roster
Public-facing team pages with editable markdown, calls-to-action, and visible member roster. Label: none.

#### #141: Add in-app feedback system for bug reports and feature requests
Feedback widget on every page (post-login) capturing bug reports / feature requests with automatic URL/browser context and optional screenshot. Admin triage page with status workflow, email responses to reporters, and Claude Code queryable data for dev workflow.

#### #138: Add Catalan (ca) translation
New `SharedResource.ca.resx` with all ~805 keys, register `"ca"` culture in `Program.cs`, language selector, and Catalan email templates.

#### #97: Add communication preference management with magic-link unsubscribe
Let humans manage email notification preferences with magic-link unsubscribe support. Label: enhancement.

---

### Priority 3: Medium Features

#### #111: Add organizational email account management (@nobodies.team)
Three parts: (1) Associate @nobodies.team emails with humans and force as primary for communication + Google services, (2) Admin UI to provision @nobodies.team accounts via Google Workspace, (3) Subtle badge on profiles for humans with @nobodies.team accounts.

#### #126: Add Stripe fee tracking to ticket vendor integration
Track Stripe processing fees on ticket purchases for financial reporting. Label: enhancement.

#### #116: Add per-team Google Drive permission level configuration
Configure Drive permission levels (viewer/commenter/editor) per team instead of global default. Label: enhancement.

### Blocked — Waiting on External Input

#### #56: Add site policies page for app-specific legal disclosures
Dedicated page for app-specific operational disclosures (delegated coordinator roles + enhanced access, contact point, visibility model, automated provisioning) with multi-language privacy policy viewer below (same tabbed UX as Governance statutes). Replaces current hardcoded English-only Privacy page.
**Blocked:** Waiting on Pepe for legal advice on disclosure content.

---

### Priority 4: Architecture / Refactoring

#### #102: Codebase audit: tech debt, test gaps, and cleanup opportunities
Comprehensive audit of codebase quality. Label: enhancement.


### Priority 5: UI/Navigation Improvements

#### #110: Add inbound bounce email parsing for delivery failure tracking
Parse bounce/NDR emails from `noreply@nobodies.team` inbox and update outbox message status from Sent to Bounced. Depends on email outbox system being implemented first.


#### #14: Drive Activity Monitor: resolve people/ IDs to email addresses
Drive Activity API returns `people/` IDs instead of email addresses. Need to resolve these via the People API for meaningful audit display.

#### #33 / #82: Discord integration — sync team memberships to server roles
Discord bot integration to automatically assign/remove Discord server roles based on Humans team memberships and role assignments. Configurable team→Discord role mappings, drift detection, audit logging, and manual sync UI at `/Admin/DiscordSync`. #82 has detailed architecture and phased implementation plan. Follows the Google Workspace sync pattern (outbox, periodic full sync, stub for dev).

---

### Priority 5b: Large Features (Future)

#### #86: Voting system — bylaw-compliant member voting with notifications and multilingual support
Formal member voting system: Yes/No votes with configurable voting periods (default 1 week), automated reminder schedule (open, 3 days, 1 day), multilingual support (5 languages), quorum enforcement, eligibility checks (Asociados only for binding votes). Open questions around anonymity requirements, proxy voting, and bylaw specifics. Optional non-binding community polls alongside binding votes.

#### #83: Add other OAuth options, additional to Google
Currently only Google OAuth login. Add email/password option (and potentially other OAuth providers) for users without Google accounts.

#### #99: Add local username/password authentication
Standard email/password auth for users without Google. Overlaps #83.

#### #98: Add magic-link email authentication for non-Google users
Passwordless magic-link login via email for non-Google users.

#### #100: Add SSO/JWT token issuance for nobodies.team services
Issue JWT tokens for authenticated humans to use across nobodies.team services.

---

### Priority 6: Data Integrity & Security

#### P1-09: Enforce uniqueness for active role assignments (DB-level)
App-layer overlap guard added (`RoleAssignmentService.HasOverlappingAssignmentAsync`), but DB-level exclusion constraint on `tsrange(valid_from, valid_to)` is still deferred. Low urgency since admin UI validates before insert.
**Where:** `RoleAssignmentConfiguration.cs`

#### P1-22: Add row-level locking to outbox processor
`ProcessGoogleSyncOutboxJob` reads pending events without `FOR UPDATE SKIP LOCKED`, risking duplicate processing if the job overlaps. Low risk at single-server scale but good defensive design.
**Where:** `ProcessGoogleSyncOutboxJob.cs:41-52`
**Source:** Multi-model production readiness assessment (2026-02-16), Codex unique finding


---

### Priority 7: Technical Debt (Low Priority)

#### G-03: N+1 queries in GoogleWorkspaceSyncService
Helper methods re-query resources already loaded by parent methods. Redundant DB round-trips.



#### #60: Replace magic string ViewModel properties with domain enums
~50+ sites across 20+ ViewModels, 10+ controllers, and 3 views use `.ToString()` on domain enums instead of passing typed enums through. Affects `ApplicationStatus`, `MembershipStatus`, `TeamMemberRole`, `SystemTeamType`, `GoogleResourceType`, `TeamJoinRequestStatus`, `AuditAction`, `GoogleSyncSource`, `MembershipTier`. Also fix `StatusBadgeExtensions` to accept enums and add coding rules to prevent recurrence.

#### Replace audit log entity type magic strings with nameof()
~40 instances of hardcoded `"User"`, `"Team"`, `"Profile"`, `"Application"`, `"GoogleResource"` strings passed to `IAuditLogService` across 11 files. Replace with `nameof(User)`, `nameof(Team)`, etc. for type safety. Also 3 `Url.Action` calls in TeamController/ProfileCardViewComponent use hardcoded `"Picture"` instead of `nameof(ProfileController.Picture)`.

#### G-09: Team membership caching
Every page load queries team memberships. At ~500 users, in-memory cache with short TTL would eliminate most DB hits.

#### #22: Add EF Core query monitoring to identify caching opportunities
Add a `DbCommandInterceptor` tracking query counts by table + operation (SELECT/INSERT/UPDATE/DELETE) in a singleton `ConcurrentDictionary`. Expose via admin page at `/Admin/DbStats`. Informs future `IMemoryCache` adoption for hot read paths. No persistence needed — resets on restart.

#### P1-11: Implement real pagination at query layer
`GetAllTeamsAsync()` and `GetPendingRequestsForTeamAsync()` load everything into memory, then paginate in LINQ-to-Objects.

#### P2-04: Review prerelease/beta observability packages
Two OpenTelemetry packages pinned to beta versions. Check for stable releases or document risk acceptance.


---

### Deferred — Revisit Post-Launch

| ID | Issue | Status |
|----|-------|--------|
| P0-03 | Restrict health and metrics endpoints | Public OK per R-03, revisit post-launch |
| P2-05 | Verify consent metadata fidelity (IP/UA accuracy) | Code uses `RemoteIpAddress` + `UseForwardedHeaders` — should be correct. Verify real IPs appear in consent records after first deploy. |

---

### Decisions (Resolved)

| ID | Question | Decision |
|----|----------|----------|
| R-01 | Should registration be restricted to `@nobodies.team`? | **Allow any.** Volunteer approval gate is sufficient. |
| R-02 | Is anonymization sufficient for GDPR deletion? | **Yes.** Anonymization is acceptable. |
| R-03 | Should `/metrics` and health endpoints be public? | **Public for now.** Revisit post-launch once running in production. |
| R-04 | Should Google Groups allow external members? | **Allow external members.** Required for the organization's needs. |

---

## Completed

### #133: Add clickable lead profiles on barrio pages and auto-create Barrio Leads system team DONE
Clickable lead links on camp detail pages, removed "at Nowhere" event name references, added Barrio Leads system team auto-populated from active CampLead assignments. Committed `d3a40c8`.

### #136: Camp self-service — flatten leads, fix withdraw, add rejoin DONE
Fixed UX dead-ends: withdraw/rejoin season, flattened leads display. Committed `929d64e`.

### #139: Fix googlemail.com/gmail.com mismatch in Google sync drift detection DONE
Normalized @googlemail.com to @gmail.com to prevent sync drift. Data migration included. Committed `a191fd7`, `34fb014`.

### #135: Add shift management system (Slices 1-3) DONE
Shift management for camps/events: shift definitions, volunteer signup, coordinator management. Committed `23b0ec1`.

### #130: Add team hierarchy (departments) and rename Leads to Coordinators DONE
Teams can now be sub-teams of departments. Leads renamed to Coordinators. IsManagement flag on roles. Multiple follow-up fixes for slugs, checkbox state, demotion logic. Committed across 15+ commits, closed 2026-03-16.

### #117: Add ticket vendor integration — coupon tracking, purchase matching, code generation DONE
Ticket vendor integration for coupon tracking and purchase matching. Closed 2026-03-15.

### #115: Fix membership status model — dashboard counts don't match admin page DONE
Fixed membership status model discrepancies between dashboard and admin counts. Closed 2026-03-15.

### #113: Show campaign grants on admin human detail page DONE
Campaign grant information visible on admin human detail page. Closed 2026-03-15.

### #112: Add preferred language distribution chart to board dashboard DONE
Language distribution chart on board dashboard. Closed 2026-03-15.

### Earlier completed items (condensed)
Items prior to March 2026 are in git history. Key milestones: onboarding redesign (#52/#53/#54), BoardController dissolution (#90), service extraction (#59/G-08), Cloud Identity migration, security hardening batch, integration tests, UI consolidation, localization, GDPR compliance, Google sync outbox pattern.
