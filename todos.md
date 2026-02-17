# Release TODOs

Audit date: 2026-02-05
Last synced: 2026-02-17T15:20

---

## Open Work — Prioritized

### Priority 1: GDPR & Security (Pre-Launch Blockers)

---

### Priority 2: User-Facing Features & Improvements

#### #26: Add custom Prometheus metrics to /metrics endpoint
`Humans.Metrics` meter is registered but emits nothing. Add `ObservableGauge` callbacks for membership status, compliance risk, role distribution, team/resource health. Add counters for emails sent, admin actions, job runs. Use `IMemoryCache` for gauge queries.

#### #14: Drive Activity Monitor: resolve people/ IDs to email addresses
Drive Activity API returns `people/` IDs instead of email addresses. Need to resolve these via the People API for meaningful audit display.

#### #28: Finish asociado application workflow for launch
Localization gaps (English remains in some views), verify `Application.Language` tracking, test all state machine transitions, add feature gate to open/close applications.

#### #29: Link birthday calendar entries to member profiles
Birthday calendar entries are plain text — add links to `/Human/View/{id}` for display names and profile pictures, matching the team detail page pattern.

#### #30: Reorganize profile page card layout and information flow
Consolidate 4 profile cards into 2 (public profile + board-only), reorder fields for natural flow (photo+name → teams → location/birthday → contact → bio → CV), fix birthday visibility (should be public), fix nav card vertical alignment. Open questions: "how I'd like to contribute" and "notes for the board" fields.

#### #31: Send email notification when user is added to a team
Notify users when they're added to a team (volunteer approval, join request, manual add). Email lists the team, its Google resources, and links to the team page. Fallback to account email if no notification target set.

#### #33: Add Discord integration to sync team/role-based server roles via API
Discord bot integration to automatically assign/remove Discord server roles based on Humans team memberships and role assignments. Configurable team→Discord role mappings, drift detection, audit logging, and manual sync UI at `/Admin/DiscordSync`.

#### #32: Fix Lead role — remove standalone RoleAssignment, derive from TeamMemberRole.Lead only
`RoleNames.Lead` exists as an assignable governance role in Admin → Roles, but Lead should be derived solely from `TeamMemberRole.Lead` on user-created teams. Remove Lead from assignable roles, clean up orphaned RoleAssignment records, keep existing `SystemTeamSyncJob` derivation logic.

#### #34: Add environment banner for non-production deployments
Red banner at top of every page in non-production environments (QA/Staging, Development) so users don't mistake QA for production. No banner in production. Uses `Environment` tag helper in `_Layout.cshtml`.

#### #27: Revoke team memberships immediately on deletion request
Currently users keep full access during the 30-day deletion grace period. Should immediately remove from all teams and end role assignments on request. Returning users must re-consent and rejoin. Google deprovisioning via normal sync job.

---

### Priority 3: Data Integrity & Security

#### P1-09: Enforce uniqueness for active role assignments (DB-level)
App-layer overlap guard added (`RoleAssignmentService.HasOverlappingAssignmentAsync`), but DB-level exclusion constraint on `tsrange(valid_from, valid_to)` is still deferred. Low urgency since admin UI validates before insert.
**Where:** `RoleAssignmentConfiguration.cs`

#### P1-22: Add row-level locking to outbox processor
`ProcessGoogleSyncOutboxJob` reads pending events without `FOR UPDATE SKIP LOCKED`, risking duplicate processing if the job overlaps. Low risk at single-server scale but good defensive design.
**Where:** `ProcessGoogleSyncOutboxJob.cs:41-52`
**Source:** Multi-model production readiness assessment (2026-02-16), Codex unique finding

#### P1-23: Tighten CSP — remove `unsafe-inline`
`Content-Security-Policy` includes `script-src 'self' 'unsafe-inline'` which weakens XSS protection. Move to nonce-based CSP for inline scripts.
**Where:** `Program.cs:328-330`
**Source:** Multi-model production readiness assessment (2026-02-16), consensus Claude + Codex

#### P1-13: Apply configured Google group settings during provisioning
`GoogleWorkspaceSettings.GroupSettings` properties (WhoCanViewMembership, AllowExternalMembers, etc.) are defined but never applied. Groups get Google defaults. Per R-04, external members must be allowed.
**Where:** `GoogleWorkspaceSettings.cs:49-78`, `GoogleWorkspaceSyncService.cs:208-215`

---

### Priority 4: Quality & Compliance

#### P2-09: PII logging policy and redaction
Structured logs include emails and user IDs in plaintext. No redaction or classification policy. GDPR data minimization gap.

#### P2-03: Re-enable vulnerable package warning visibility
`NU1902`/`NU1903` warnings suppressed globally in `Directory.Packages.props`. Vulnerable dependencies won't surface in builds.

#### P2-07: Add integration tests for critical paths
Integration test project exists with TestContainers but has 0 tests. Critical compliance paths (consent, auth, deletion) untested end-to-end.

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
