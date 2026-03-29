# Release TODOs

Audit date: 2026-02-05
Last synced: 2026-03-22T18:30

---

## Open Work — Prioritized

### Priority 1: Bugs / User-Reported Issues

#### #172: Google sign-in returns 500 when correlation cookie is missing
Sign-in fails with 500 error when correlation cookie is absent. Needs investigation.

#### #173: Duplicate key error when syncing Google Group on team edit
Google Group sync on barrios team throws duplicate key error. Affects group membership propagation.

#### #174: Creating a team with a duplicate slug returns a database error
Raw database error instead of user-friendly validation message.

#### #175: Role edit fails when description exceeds varchar(2000) limit
No client-side validation; raw DB error on long descriptions.

#### #177: Feedback API: accept string enum values for status
API only accepts integer enum values, not string names. Quick fix — add `JsonStringEnumConverter`.

#### #182: Fix: profile edit Add buttons may silently break (missing null guards)
Contact and volunteer history JS IIFEs lack null checks on DOM elements. Reporter (phaune) can't add entries on Firefox/macOS.

#### #188: Fix shift editing bugs: empty start date, histogram min/cap split, plot inconsistency
Three shift admin bugs from Frank: (1) full-day shift edit shows empty start date, (2) histograms don't distinguish min from cap, (3) confirmed plot doesn't match data.

### Priority 2: Small-Medium Enhancements

#### #153: Feedback API: enum serialization, response tracking, missing context, markdown emails
Batch of feedback system improvements. Partially overlaps #177.

#### #178: Add "My Feedback" page so users can track their own reports
User-facing page (linked from profile menu) showing their submitted feedback: status, linked GitHub issue, admin responses, resolution timeline. No new entities needed.

#### #176: Batch voluntell: add range assignment from shift admin view
Coordinators can assign a user to a range of consecutive all-day shifts from the admin view.

#### #180: Clarify rota separation in shift signup UI (build/event/strike)
Make build/event/strike rota boundaries visually clearer so users understand separate signup flows.

#### #181: Add visibility toggle for team role definitions
`IsPublic` boolean on `TeamRoleDefinition` to hide internal tracking roles from volunteer views.

#### #183: Improve discoverability of Asociado application for existing Colaboradores
Add contextual guidance for Colaboradores on the Application page — the upgrade path exists but users don't find it.

#### #184: Shift UI text/label cleanup: rename buttons and headings
Rename "Browse Shifts" → "Browse Volunteer Options", coordinator button → "Manage Shifts", remove duplicate button, "Sign up for range" → "Sign up for these dates".

#### #185: Add rota visibility control and shift period filtering for volunteers
Coordinator rota enable/disable toggle + volunteer-facing set-up/event/strike filter with set-up week sub-filter.

#### #186: Add custom labels/tags to rotas with volunteer-facing filter
Tag rotas with descriptive labels (e.g. "Heavy lifting", "Working in the sun"). Multi-select filter on browse page.

#### #77: Reasons why an Asociado is accepted (or applying)
Board members voting on Asociado applications should be required to select which bylaw criteria the applicant meets.

#### #127: Add incomplete signup lifecycle — reminders and auto-deletion
Send reminders to humans who signed up but never completed profile/consent, auto-delete after configurable period.

#### #144: Fix preview environment file share for uploaded images
Preview environments clone QA DB but lack access to uploaded images.

#### #148: Add automated Codex code review on pull requests
GitHub Action workflow running OpenAI Codex CLI on PRs.

#### #138: Add Catalan (ca) translation
New `SharedResource.ca.resx` with all ~805 keys.

#### #97: Add communication preference management with magic-link unsubscribe
Let humans manage email notification preferences with magic-link unsubscribe support.

#### #156: Add public API endpoint for app version and commit hash
Simple health/version endpoint.

#### #161: Add shift exports, iCal feed, and post-event stats
Slice 4 of shift management. Independent of #162.

#### #162: Add shift notification emails and signup cleanup jobs
Slice 5 of shift management. Independent of #161.

#### #163: Gather feedback on shift management UX before slices 4-5
Get coordinator and volunteer feedback on slices 1-3. Frank's feedback (today) is a major input — see #184, #185, #186, #187, #188.

---

### Priority 3: Medium Features

#### #149: Integrate new Figma designs into Humans UI
Adopt new visual designs from Figma.

#### #150: Event guide: submission, moderation, and in-app browser
Event guide feature for content submission and browsing.

#### #187: Add image attachments to team page editor for visual job descriptions
Upload and display images on team EditPage for visual job descriptions.

#### #111: Add organizational email account management (@nobodies.team)
Associate @nobodies.team emails with humans, admin provisioning UI, profile badges.

#### #126: Add Stripe fee tracking to ticket vendor integration
Track Stripe processing fees on ticket purchases.

#### #157: Add bus ticket sales for event transport
First Stripe write integration.

#### #158: Add barrio services store for at-cost supplies
Second store on the shared Stripe payment layer.

#### #159: Add invoice generation for Stripe payments
Official invoices for Stripe payments. Depends on #157, #158.

#### #116: Add per-team Google Drive permission level configuration
Configure Drive permission levels per team instead of global default.

#### #179: Drive sync: resolve max permission level when same resource linked to multiple teams
Handle permission level conflicts when a Drive resource belongs to multiple teams.

### Blocked — Waiting on External Input

#### #56: Add site policies page for app-specific legal disclosures
**Blocked:** Waiting on Pepe for legal advice on disclosure content.

#### #166: Merge duplicate accounts for Deepak (gn.org + gmail)
**Blocked:** Needs confirmation from Deepak on which account to keep.

---

### Priority 4: Architecture / Refactoring

#### #102: Codebase audit: tech debt, test gaps, and cleanup opportunities
Comprehensive audit of codebase quality.

---

### Priority 5: UI/Navigation Improvements

#### #110: Add inbound bounce email parsing for delivery failure tracking
Parse bounce/NDR emails. Depends on email outbox system.

#### #14: Drive Activity Monitor: resolve people/ IDs to email addresses
Resolve People API IDs for meaningful audit display.

#### #33 / #82: Discord integration — sync team memberships to server roles
Discord bot integration following Google Workspace sync pattern.

---

### Priority 5b: Large Features (Future)

#### #86: Voting system — bylaw-compliant member voting with notifications and multilingual support
Formal member voting system.

#### #83: Add other OAuth options, additional to Google
Add email/password or other OAuth providers.

#### #99: Add local username/password authentication
Standard email/password auth. Overlaps #83.

#### #98: Add magic-link email authentication for non-Google users
Passwordless magic-link login via email.

#### #100: Add SSO/JWT token issuance for nobodies.team services
Issue JWT tokens for cross-service auth.

---

### Priority 6: Data Integrity & Security

#### P1-09: Enforce uniqueness for active role assignments (DB-level)
DB-level exclusion constraint on `tsrange(valid_from, valid_to)`. Low urgency — app layer validates.

#### P1-22: Add row-level locking to outbox processor
`FOR UPDATE SKIP LOCKED` on outbox processing. Low risk at single-server scale.

---

### Priority 7: Technical Debt (Low Priority)

#### G-03: N+1 queries in GoogleWorkspaceSyncService
Helper methods re-query resources already loaded by parent methods.

#### Replace audit log entity type magic strings with nameof()
~40 instances of hardcoded strings passed to `IAuditLogService`.

#### G-09: Team membership caching
In-memory cache with short TTL for team memberships.

#### #22: Add EF Core query monitoring to identify caching opportunities
`DbCommandInterceptor` tracking query counts, admin page at `/Admin/DbStats`.

#### P1-11: Implement real pagination at query layer
`GetAllTeamsAsync()` and `GetPendingRequestsForTeamAsync()` paginate in-memory.

#### P2-04: Review prerelease/beta observability packages
Two OpenTelemetry packages on beta versions.

---

### Deferred — Revisit Post-Launch

| ID | Issue | Status |
|----|-------|--------|
| P0-03 | Restrict health and metrics endpoints | Public OK per R-03, revisit post-launch |
| P2-05 | Verify consent metadata fidelity (IP/UA accuracy) | Verify real IPs appear in consent records after first deploy |

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

### #134: Standardize role authorization to prevent Identity/RoleAssignments mismatch DONE
Closed 2026-03-19.

### #146: Contact info publicly available without log in DONE
Fixed — contact info hidden from public pages. Closed 2026-03-19.

### #147: Add feedback-to-issue triage workflow via API DONE
Feedback API with query, respond, link issues, update status. Closed 2026-03-19.

### #142: Add "report a bug" link in menu or footer DONE
Superseded by #147 (feedback widget). Closed 2026-03-20.

### #154: Team public pages improvements DONE
Custom slug + coordinator visibility. Closed 2026-03-22.

### #170: Support markdown in shift descriptions DONE
Closed 2026-03-22.

### #165: Preferred language indicator on profile and admin pages DONE
Closed 2026-03-22.

### #169: Investigate Google Group email delivery DONE
Google Groups settings drift detection. Closed 2026-03-22.

### #168: Shift edit: max humans count doesn't save DONE
Closed 2026-03-21.

### #60: Replace magic string ViewModel properties with domain enums DONE
~50+ sites updated. Committed `38d3a72`.

### #137: Public team pages with editable markdown content, CTAs, and member roster DONE
Merged to production via PR #145 (commit `31df3c4`).

### #141: In-app feedback system for bug reports and feature requests DONE
Feedback widget on every page (post-login), admin triage dashboard, API endpoint.

### #133: Add clickable lead profiles on barrio pages and auto-create Barrio Leads system team DONE
Committed `d3a40c8`.

### #136: Camp self-service — flatten leads, fix withdraw, add rejoin DONE
Committed `929d64e`.

### #139: Fix googlemail.com/gmail.com mismatch in Google sync drift detection DONE
Committed `a191fd7`, `34fb014`.

### #135: Add shift management system (Slices 1-3) DONE
Committed `23b0ec1`.

### #130: Add team hierarchy (departments) and rename Leads to Coordinators DONE
Committed across 15+ commits, closed 2026-03-16.

### #117: Add ticket vendor integration — coupon tracking, purchase matching, code generation DONE
Closed 2026-03-15.

### #115: Fix membership status model — dashboard counts don't match admin page DONE
Closed 2026-03-15.

### #113: Show campaign grants on admin human detail page DONE
Closed 2026-03-15.

### #112: Add preferred language distribution chart to board dashboard DONE
Closed 2026-03-15.

### Earlier completed items (condensed)
Items prior to March 2026 are in git history. Key milestones: onboarding redesign (#52/#53/#54), BoardController dissolution (#90), service extraction (#59/G-08), Cloud Identity migration, security hardening batch, integration tests, UI consolidation, localization, GDPR compliance, Google sync outbox pattern.
