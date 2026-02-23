# Release TODOs

Audit date: 2026-02-05
Last synced: 2026-02-24

---

## Open Work — Prioritized

### Blocked — Waiting on External Input

#### #56: Add site policies page for app-specific legal disclosures
Dedicated page for app-specific operational disclosures (delegated coordinator roles + enhanced access, contact point, visibility model, automated provisioning) with multi-language privacy policy viewer below (same tabbed UX as Governance statutes). Replaces current hardcoded English-only Privacy page.
**Blocked:** Waiting on Pepe for legal advice on disclosure content.

---

### Priority 5: UI/Navigation Improvements

#### #14: Drive Activity Monitor: resolve people/ IDs to email addresses
Drive Activity API returns `people/` IDs instead of email addresses. Need to resolve these via the People API for meaningful audit display.

#### #33: Add Discord integration to sync team/role-based server roles via API
Discord bot integration to automatically assign/remove Discord server roles based on Humans team memberships and role assignments. Configurable team→Discord role mappings, drift detection, audit logging, and manual sync UI at `/Admin/DiscordSync`.

---

### Priority 6: Data Integrity & Security

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

### Priority 7: Technical Debt (Low Priority)

#### G-03: N+1 queries in GoogleWorkspaceSyncService
Helper methods re-query resources already loaded by parent methods. Redundant DB round-trips.


#### G-07: AdminController over-fetches data
`HumanDetail` loads ALL applications and consent records via `Include` when it only needs a few. `Humans` list relies on implicit Include behavior.

#### #59 / G-08: Extract duplicated controller business logic into shared services
Legal docs slice extracted to `AdminLegalDocumentsController` + `IAdminLegalDocumentService`. Application approve/reject extracted to `IApplicationDecisionService`. Remaining: signup rejection (duplicated in Admin + OnboardingReview), volunteer approval (duplicated in Admin + OnboardingReview), and extending `IRoleAssignmentService` with assign/end/reassign orchestration. Consolidate into fewest services needed — extend existing interfaces where possible.

#### #60: Replace magic string ViewModel properties with domain enums
~50+ sites across 20+ ViewModels, 10+ controllers, and 3 views use `.ToString()` on domain enums instead of passing typed enums through. Affects `ApplicationStatus`, `MembershipStatus`, `TeamMemberRole`, `SystemTeamType`, `GoogleResourceType`, `TeamJoinRequestStatus`, `AuditAction`, `GoogleSyncSource`, `MembershipTier`. Also fix `StatusBadgeExtensions` to accept enums and add coding rules to prevent recurrence.

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

### #44 + #46 Part 1: Consent tab language + admin terminology DONE
Committed `a53696d`. #44: Consent review checkbox text now follows the active document tab via JS tab-switch handler with per-language translations from ResourceManager. Non-Spanish tabs show bilingual legal note (tab language + Spanish italic). #46 Part 1: Replaced "Volunteer"/"Member" with "Human" in 7 admin button/message keys across all 5 locales (EN/ES/DE/FR/IT) + 3 log messages in AdminController.

### #49: Reorganize profile edit into four named sections DONE
Committed `2e98cbd`. Restructured Profile Edit from a single flat card into 4 distinct cards within one form: General Information, Contributor Information, Application (initial setup only), Private Information. Folds in #45 (Private section shown first when private fields are empty) and #47 (require Burner CV or "no prior burn experience" checkbox). Renamed "Legal First Name" → "Legal First Name(s)" in all 5 locales. Added `NoPriorBurnExperience` domain property with EF migration, client-side + server-side CV validation, `ShowPrivateFirst` conditional ordering, and `_EditSectionPrivate.cshtml` partial.

### #58: Add dev/QA user purge function DONE
Committed `32468ea`. Admin-only PurgeHuman endpoint (non-production gated). Severs OAuth login, clears UserEmails, changes email to `purged-{guid}@deleted.local`, locks out account — so the same Gmail can log back in fresh. Danger-styled button on HumanDetail inside `<environment exclude="Production">`.

### Onboarding QA stabilization (part of #52/#53/#54) DONE
Committed `32468ea`. Removed unnecessary StartReview/UnderReview application states — Submitted now transitions directly to Approved/Rejected/Withdrawn. Fixed board vote concurrency error (vote no longer touches Application entity). Fixed invisible membership tier badge (bg-purple → bg-primary). Simplified OnboardingReview Detail and BoardVotingDetail first cards (removed duplicated profile info, inlined motivation). Added vote date column. Clickable board voting rows. Application/Create tier selector switched to radio buttons with always-visible descriptions.

### #55: Show tier application status on profile banner DONE
Committed `d503e2b`. Profile page now shows a banner with the user's latest tier application status (Submitted/Approved/Rejected) and a link to the Governance page. Skips Withdrawn applications. Localized in all 5 languages.

### #57: Add application transparency statistics to Governance page DONE
Committed `ebc54b0`. Governance page shows aggregate application statistics: total, approved, rejected, pending counts, Colaborador/Asociado breakdown, and approval rate percentage. Visible to all members per Section 8 transparency requirements.

### #50: Split teams page into My Teams / Other Teams DONE
Committed `793bceb`. Teams Index page now shows "My Teams" section at top with user's teams, "Other Teams" section below with remaining teams. Extracted `_TeamCard.cshtml` partial to eliminate card markup duplication. Pagination applies to Other Teams only. Separate `/Teams/My` page retained for Leave/Manage actions.

### #52/#53/#54/#46: Onboarding redesign epic DONE
Three tiers (Volunteer/Colaborador/Asociado), board voting, Consent Coordinator gate, reject signup. Landed `8a9ee4e` + 5 follow-up fixes. QA validated, issues closed.

### #62: Fix missing id attributes on dynamically-generated form fields in profile edit DONE
Committed `e3f1ad5`. Added `id` attributes to dynamically-created contact field and volunteer history rows in `/Profile/Edit`.

### #63: Add daily board digest email for new member approvals DONE
Committed `1e2b29f`. Nightly Hangfire job (02:00 UTC) emails each Board member a summary of the previous UTC day's volunteer and tier application approvals, grouped by tier. Skips on zero-approval days. Localized templates in all 5 locales. Admin email preview added.

### QA-01 through QA-04: Localize status badges, fix page title, diacritics, error pages DONE
Committed `a884b22`. Fixed "Approved" badge localization, `/Application` page title, language name diacritics, and custom error pages in dev mode.

### #25 / F-06: Localize email bodies DONE
### #35: Deduplicate email previews DONE
Committed `08c4d84`. Extracted `IEmailRenderer` to separate rendering from SMTP transport, eliminating ~250 lines of duplicated body HTML. All 14 email body texts localized via SharedResource resx keys across en/es/de/fr/it. Email preview page uses per-locale personas and adds TermRenewalReminder preview.
