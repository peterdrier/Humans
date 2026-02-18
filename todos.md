# Release TODOs

Audit date: 2026-02-05
Last synced: 2026-02-18T12:15

---

## Open Work — Prioritized

### Priority 1: GDPR & Security (Pre-Launch Blockers)

---

### Priority 2: User-Facing Features & Improvements

#### #14: Drive Activity Monitor: resolve people/ IDs to email addresses
Drive Activity API returns `people/` IDs instead of email addresses. Need to resolve these via the People API for meaningful audit display.

#### #28: Finish asociado application workflow for launch
Localization gaps (English remains in some views), verify `Application.Language` tracking, test all state machine transitions, add feature gate to open/close applications.

#### #44: Fix consent checkbox translation and add Spanish-is-binding notice site-wide
Consent Review checkbox text not translating to user's language despite `.resx` translations existing. Also: add a site-wide "Spanish is the legally binding text" notice (e.g., in `_Layout.cshtml` footer) visible on every page.

#### Rename "Legal First Name" to "Legal First Name(s)" to allow plural
Spanish naming convention uses multiple given names (e.g., "María del Carmen"). Update label in `ProfileViewModel.cs` `[Display]` attribute, `Profile_LegalName` localization key across all 5 `.resx` files, and the `Profile.FirstName` XML doc comment. The DB column and property name stay as-is.

#### #45: Show board-private fields first on initial profile setup
First time editing profile (legal name + emergency contact empty), put the board-private section at the top of the form so new humans don't miss it. On subsequent edits (fields populated), keep the current layout with board info at the bottom.

#### #46: Add reject signup action and fix volunteer/member terminology to "human"
No way to reject a pending signup — only approve or leave pending. Add reject action with optional reason, email notification, and audit log. Also fix "Approve Volunteer" / "Suspend Member" / "Unsuspend Member" button labels to use "Human" across all 5 locales.

#### #47: Require Burner CV or "burn virgin" checkbox on profile
Empty Burner CV is ambiguous (forgot vs. never been). Require at least one entry OR a "no prior burn experience" checkbox. New `NoPriorBurnExperience` bool on `Profile`, client + server validation, existing empty profiles grandfathered.

#### #49: Reorganize profile edit into three named sections
Three distinct sections: 1) General Information (picture, burner name, pronouns, location, birthday, contacts, bio), 2) Contributor Information (Burner CV, contribution interests, board-approval note), 3) Private Information (legal name, emergency contact, board notes). Supersedes #48. Related: #45, #47.

#### #50: Split teams page into "my teams" and "other teams" sections
Reorganize Teams Index into two sections: user's teams at top, other teams below. May consolidate with MyTeams page.

#### #52: Redesign onboarding with three membership tiers
Three tiers: Volunteer (auto-accepted, basic profile), Colaborador (board vote, fuller profile), Asociado (board vote, full profile + TBD questions from Ben). Tier selection at signup, profile requirements vary by tier, users can upgrade later. Supersedes #51. Related: #46, #47, #49.

#### #53: Add board voting system for application reviews
Board members vote Yay/Maybe/No/Abstain on each application. Spreadsheet-style dashboard with per-board-member columns, separate Colaborador/Asociado views. Final approve/deny records board meeting date + decision note. Related: #52, #46, #28.

#### #54: Add Consent and Volunteer Coordinator roles with onboarding gate
Two new board-appointed roles with board-level data visibility. Consent Coordinator vets incoming humans for known issues (safety gate — must clear before admission). Volunteer Coordinator facilitates onboarding and team placement (not a gate). Related: #52, #53.

#### #33: Add Discord integration to sync team/role-based server roles via API
Discord bot integration to automatically assign/remove Discord server roles based on Humans team memberships and role assignments. Configurable team→Discord role mappings, drift detection, audit logging, and manual sync UI at `/Admin/DiscordSync`.

---

### Priority 3: Data Integrity & Security

#### P1-09: Enforce uniqueness for active role assignments (DB-level)
App-layer overlap guard added (`RoleAssignmentService.HasOverlappingAssignmentAsync`), but DB-level exclusion constraint on `tsrange(valid_from, valid_to)` is still deferred. Low urgency since admin UI validates before insert.
**Where:** `RoleAssignmentConfiguration.cs`

#### P1-22: Add row-level locking to outbox processor
`ProcessGoogleSyncOutboxJob` reads pending events without `FOR UPDATE SKIP LOCKED`, risking duplicate processing if the job overlaps. Low risk at single-server scale but good defensive design.
**Where:** `ProcessGoogleSyncOutboxJob.cs:41-52`
**Source:** Multi-model production readiness assessment (2026-02-16), Codex unique finding


#### P1-13: Apply configured Google group settings during provisioning
`GoogleWorkspaceSettings.GroupSettings` properties (WhoCanViewMembership, AllowExternalMembers, etc.) are defined but never applied. Groups get Google defaults. Per R-04, external members must be allowed.
**Where:** `GoogleWorkspaceSettings.cs:49-78`, `GoogleWorkspaceSyncService.cs:208-215`

---

### Priority 4: Quality & Compliance


---

### Priority 5: Technical Debt (Low Priority)

#### G-03: N+1 queries in GoogleWorkspaceSyncService
Helper methods re-query resources already loaded by parent methods. Redundant DB round-trips.


#### G-07: AdminController over-fetches data
`HumanDetail` loads ALL applications and consent records via `Include` when it only needs a few. `Humans` list relies on implicit Include behavior.

#### G-08: Centralize admin business logic into services
Legal docs slice extracted to `AdminLegalDocumentsController` + `IAdminLegalDocumentService`. Remaining: role management, member management, application review slices still in `AdminController`.

#### G-09: Team membership caching
Every page load queries team memberships. At ~500 users, in-memory cache with short TTL would eliminate most DB hits.

#### #22: Add EF Core query monitoring to identify caching opportunities
Add a `DbCommandInterceptor` tracking query counts by table + operation (SELECT/INSERT/UPDATE/DELETE) in a singleton `ConcurrentDictionary`. Expose via admin page at `/Admin/DbStats`. Informs future `IMemoryCache` adoption for hot read paths. No persistence needed — resets on restart.

#### P1-11: Implement real pagination at query layer
`GetAllTeamsAsync()` and `GetPendingRequestsForTeamAsync()` load everything into memory, then paginate in LINQ-to-Objects.

#### P2-04: Review prerelease/beta observability packages
Two OpenTelemetry packages pinned to beta versions. Check for stable releases or document risk acceptance.

#### #25 / F-06: Localize email content and fix background job culture context
Email subjects are localized but body content is still inline HTML with string interpolation. Additionally, background jobs (`SendReConsentReminderJob`, `SuspendNonCompliantMembersJob`, `ProcessAccountDeletionsJob`) don't set `CurrentUICulture` to each user's `PreferredLanguage` before calling `IEmailService`, so even subjects come out English-only for job-triggered emails.

#### #35: Refactor email previews to use live templates instead of static HTML
Admin email previews (`/Admin/EmailPreview`) use duplicated static HTML in `AdminController.GenerateEmailPreviews()` — separate from the real `SmtpEmailService` templates. Replace with calls through the actual email rendering path using stub data (Volunteers team for team-related emails). This picks up the real CSS, environment banner, and localization. **Batch with #25.**

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

### #26: Wire up custom Prometheus metrics DONE
Eagerly resolve HumansMetricsService at startup, add RecordJobRun to 3 uninstrumented jobs, add google_sync_outbox_pending gauge. Committed `5a99d19`.

### #27: Revoke team memberships immediately on deletion request DONE
Immediately revokes all team memberships and ends role assignments on deletion request. Returning users must re-consent and rejoin. Google deprovisioning via normal sync job. Committed `966e2a6`.

### #32: Fix Lead role — remove standalone RoleAssignment DONE
Removed `RoleNames.Lead` from assignable roles, data migration to soft-end orphaned assignments, fixed Leads team sync on role change, consolidated consent eligibility into `GetRequiredTeamIdsForUserAsync`, fixed HumanController missing ViewModel properties, added 8 unit tests. Committed `5acfa4f`.

### #34: Add environment banner for non-production deployments DONE
Colored banner below navbar in non-production environments. Committed `66c19f1`.

### #29: Link birthday calendar entries to member profiles DONE
Birthday entries now link to member profiles via `/Human/View/{id}`. Committed `66c19f1`.

### #31: Send email notification when user is added to a team DONE
Members receive email with team resources when added via approval, join request, or system sync. Committed `247385c`.

### #30: Reorganize profile page card layout and information flow DONE
Consolidated 4 cards to 2 (public + board-private), teams as pills under name, edit page reordered to match, added ContributionInterests and BoardNotes fields, fixed email visibility enum comparison bug, OAuth email backfill migration. Committed `d96376d`.

### G-01: AdminNotes GDPR exposure RESOLVED
Decision: AdminNotes field is fine as-is. Users can request to see what's stored about them (GDPR subject access request), so board members should be mindful of what they write. No code change needed.

### P1-19: Sanitize markdown-to-HTML in consent and governance views DONE
Added `HtmlSanitizer` (Ganss.Xss) to both `Consent/Review.cshtml` and `Governance/Index.cshtml` markdown render paths. Markdig output is now sanitized before `@Html.Raw()`. Committed `34a8f79`.

### P1-18: Account deletion Google deprovisioning DISMISSED
Not needed — users lose team memberships (and thus Google permissions) at the start of the deletion flow, not at the 30-day anonymization step. The overnight sync job provides a second check by removing any stale permissions it finds.

### P1-17: GDPR anonymization — clear all PII fields DONE
Added missing field clearings to `ProcessAccountDeletionsJob.AnonymizeUserAsync`: `Pronouns`, `DateOfBirth`, `ProfilePictureData`, `ProfilePictureContentType`, `EmergencyContactName`, `EmergencyContactPhone`, `EmergencyContactRelationship`. Also added removal of `VolunteerHistoryEntries` and `ContactFields` related entities. Committed `84f0538`.

### P1-21: Add missing database constraints DONE (already existed)
Both CHECK constraints (`CK_google_resources_exactly_one_owner`, `CK_role_assignments_valid_window`) were already applied in the `AddPreProdIntegrityAndGoogleSyncOutbox` migration. The P1-09 temporal exclusion constraint remains tracked separately.

### P0-12: Docker healthcheck DONE
Added `curl` to runtime image and `HEALTHCHECK` directive hitting `/health/live`. Coolify/Docker will detect unhealthy containers.

### P0-13: Remove insecure default credentials from docker-compose DONE
Replaced `:-humans` fallback with `:?POSTGRES_PASSWORD must be set` — compose fails loudly if env var missing. Updated `.env.example`.

### P2-02: Add explicit cookie/security policy settings DONE
`ConfigureApplicationCookie` with `SecurePolicy.Always`, `SameSite.Lax`, `HttpOnly = true`. TLS terminated by Coolify reverse proxy.

### P2-01: Persist Data Protection keys to database DONE
Keys persisted to PostgreSQL via `PersistKeysToDbContext<HumansDbContext>()`. Auth cookies survive container restarts and Coolify redeploys. Migration `AddDataProtectionKeys` creates the table. Zero deploy-time config needed.

### P1-16: Fail fast in production if Google credentials missing DONE
`AddHumansInfrastructure` throws `InvalidOperationException` at startup in Production if Google Workspace credentials are not configured. Stubs still available in Development/Staging.

### P2-06 + G-05: Register, schedule, and configure SendReConsentReminderJob DONE
Job registered in DI, scheduled daily at 04:00 (before suspension job at 04:30). Cooldown and days-before-suspension now configurable via `Email:ConsentReminderDaysBeforeSuspension` (prod: 30, QA: 3) and `Email:ConsentReminderCooldownDays` (prod: 7, QA: 1). G-05 cooldown was already implemented in code.

### P0-01: Lock down trusted proxy headers DONE
`KnownProxies` set to `46.225.30.76` in `Program.cs`. Consent records and audit logs will now capture real client IPs.

### P0-04: Enforce host header restrictions DONE
`AllowedHosts` set to `humans.nobodies.team;humans.n.burn.camp;localhost`. QA override in `appsettings.Staging.json`.

### P1-07: Add transactional consistency for Google sync DONE
Outbox pattern implemented: `TeamService` enqueues `GoogleSyncOutboxEvent` rows instead of calling Google API in-request. `ProcessGoogleSyncOutboxJob` drains the outbox with retry logic (max 10 attempts).

### #3: Full Lead rename (domain, DB, code) DONE
Renamed all internal "Lead" references across domain, application, infrastructure, web, tests, migrations, resources, and documentation.

### #23: Rename "Members" to "Humans" across internal code and UI DONE
Renamed view models (AdminMember* → AdminHuman*), controller methods (Members → Humans, MemberDetail → HumanDetail, SuspendMember → SuspendHuman, UnsuspendMember → UnsuspendHuman, MemberGoogleSyncAudit → HumanGoogleSyncAudit), view files, ~30 AdminMember_ localization keys across all 5 .resx files, asp-action references, and feature docs. TeamMember domain entities untouched.

### #24: Add emergency contact field to member profiles DONE
Emergency contact fields (name, phone, relationship) on Profile. Board-only visibility, GDPR export included, all 5 locales. Also added public `/Admin/DbVersion` endpoint for migration squash checks.

### #20: Add volunteer location map showing shared city/country DONE
Google Maps page with volunteer pins. Committed `2664c46`.

### #19: Fix profile edit data lost when navigating to Preferred Email DONE
Added beforeunload guard to profile edit form. Committed `3cf905a`.

### #18: Burner CV: separate position/role from event name DONE
Updated placeholder text to separate event from role. Committed `b424f9b`, `352c79a`.

### #17: Add Discord as a contact type DONE
Added Discord as contact field type. Committed `352c79a`.

### #16: Consolidate phone and contact fields, add validation DONE
Consolidated emails, removed standalone phone, birthday as month-day. Committed `352c79a`.

### Codebase simplification: remove dead code and unnecessary abstractions DONE
Committed `251da28`.

### P2-08: Expand configuration health checks DONE
Added OAuth, Email, and GitHub config keys to `ConfigurationHealthCheck`. Now checks 9 required keys (was 1). Dedicated connectivity checks (SMTP, GitHub, GoogleWorkspace) remain separate.

### P1-12: Google group sync pagination DONE (stale)
All three call sites (`SyncTeamGroupMembersAsync`, `PreviewGroupSyncAsync`, `ListDrivePermissionsAsync`) already handle `NextPageToken` with `do/while` loops. Bug was fixed as part of earlier work; todo was stale.

### Earlier completed items (condensed)
- F-01: Profile pictures with team photo gallery (`f04c8cf`)
- F-02: Volunteer acceptance gate before system access (`4364b5d`)
- F-03: Admin UI for managing team Google resources (`d9bf5c1`)
- F-04: Audit log for automatic user and resource changes
- F-05: Localization / PreferredLanguage support (`4189f8d`, closes #7)
- F-07/F-08: Admin dashboard RecentActivity + PendingConsents (`f04c8cf`)
- Drive Activity API monitoring (`f04c8cf`, closes #11)
- Issue #15: Redesign legal document management (`b73982c`, `dbc6676`)
- Membership gating, volunteer sync, application language tracking (`28a2e8b`)
- P0-02 through P0-14: Security hardening (CSP, token persistence, consent cascade, GDPR wording, email consistency)
- P1-01/P1-02: Profile page membership status + pending consent fix (`c7b127f`)
- P1-03: Anti-caching headers on data export (`6287976`)
- P1-05: Anonymous email verification redirect (`3319eec`)
- P1-06: Domain restriction decision (R-01)
- P1-08: Active team membership uniqueness (`44330ec`)
- P1-10: Slug generation race conditions (`1c5bfc0`)
- P1-14/P1-15: Hangfire dashboard 404 + Team Join slug fix
- G-02: N+1 query in SendReConsentReminderJob (`3966e79`)
- G-04: Google Drive provisioning idempotency (`4243ca7`)
- G-06: SystemTeamSyncJob sequential execution (resolved by design)

### Batch: UI consolidation, security hardening, integration tests DONE
Committed 2026-02-18 in 3 commits:

**UI consolidation** (`3a2e444`): TempDataAlertsViewComponent replaces 19 duplicated alert blocks (#38). UserAvatarViewComponent replaces 9 avatar patterns (#39). ProfileCardViewComponent with dedicated ViewModel replaces 3 duplicated profile renderings (#37). _RoleBadge partial fixes Lead color inconsistency across 5 views (#40). _ApplicationHistory partial deduplicates timeline in 2 views (#41). StatusBadgeExtensions wired in Profile/Index + Admin/Humans, added "Pending Approval" case (G-06b). Net -150 lines across 37 files.

**Security hardening** (`dbdcf58`): CSP nonce middleware + NonceTagHelper replace `unsafe-inline` in script-src (P1-23). Inline onclick/onchange handlers converted to addEventListener in LegalDocuments, Resources, Emails. PII redaction Serilog enricher masks emails and PII in structured logs (P2-09). P2-03 was already resolved (NU1902/NU1903 not suppressed).

**Integration tests** (`b6c43c1`): WebApplicationFactory with TestContainers PostgreSQL, 16 tests across health endpoints, anonymous access controls, and security headers (P2-07).
