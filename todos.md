# Release TODOs

Audit date: 2026-02-05 | Last updated: 2026-02-10

---

## Open Work — Prioritized

### Priority 1: Bugs

#### #19: Fix profile edit data lost when navigating to Preferred Email
Clicking "Manage Preferred Email" navigates away from the profile edit form, losing all unsaved changes. Fix with auto-save, confirmation prompt, or in-page flow.

#### P1-16: Stubs silently activate in production if Google credentials missing
`StubTeamResourceService` creates real DB records with fake Google IDs when credentials are missing. No startup warning. Fix: fail fast in production, or health check reports degraded.
**Where:** `Program.cs:146-157`

#### P1-12: Google group sync misses paginated results
`Members.List()` and `Permissions.List()` don't handle `NextPageToken`. Groups with >200 members will have members silently dropped or removed.
**Where:** `GoogleWorkspaceSyncService.cs:334,554`

#### G-05: No reminder frequency tracking (spam risk)
`SendReConsentReminderJob` sends reminders on every execution with no cooldown. Needs `LastConsentReminderSentAt` field and 7-day cooldown. Requires migration.

#### P2-06: Schedule orphaned SendReConsentReminderJob
The job class exists but isn't registered in DI or scheduled. Volunteers get suspended without warning because no reminders are sent.

---

### Priority 2: User-Facing Features & Improvements

#### #16: Consolidate phone and contact fields, add validation
Two separate phone entry points (standalone phone + contact info) are confusing. Country codes non-exhaustive. Email contact fields lack validation. Merge or clarify, expand country codes, add validation.

#### #18: Burner CV: separate position/role from event name
Event Name field mixes event + position (e.g. "Burning Man 2024, Gate Lead"). Add separate Position/Role field to enable filtering/searching by role.

#### #17: Add Discord as a contact type
Add Discord username as a contact field type. Future: manage Discord roles/groups automatically based on team membership.

#### #20: Add volunteer location map showing shared city/country
Map page (Google Maps) showing pins for volunteers who've shared their city/country. Cluster markers, respect visibility settings. Similar pattern to the birthday calendar.

#### #14: Drive Activity Monitor: resolve people/ IDs to email addresses
Drive Activity API returns `people/` IDs instead of email addresses. Need to resolve these via the People API for meaningful audit display.

---

### Priority 3: Data Integrity & Security

#### P1-09: Enforce uniqueness for active role assignments
Partial unique index only covers `ValidTo IS NULL`. Future-dated assignments can overlap. Fix with PostgreSQL exclusion constraint on `tsrange(valid_from, valid_to)`.
**Where:** `RoleAssignmentConfiguration.cs:39`, `AdminController.cs:663-674`

#### P1-07: Add transactional consistency for Google sync
DB commits before Google API call. If Google fails, DB is committed but resources are out of sync. Fix: outbox pattern or reconciliation.
**Where:** `TeamService.cs:258,261` and `:574,577`

#### P1-13: Apply configured Google group settings during provisioning
`GoogleWorkspaceSettings.GroupSettings` properties (WhoCanViewMembership, AllowExternalMembers, etc.) are defined but never applied. Groups get Google defaults.
**Where:** `GoogleWorkspaceSettings.cs:49-78`, `GoogleWorkspaceSyncService.cs:208-215`

#### P1-06: Domain restriction decision for Google login
Any Google account can auto-create a user. No domain restriction or approval gate. Linked to decision R-01.
**Where:** `AccountController.cs:91`, `Program.cs:71-81`

---

### Priority 4: Quality & Compliance

#### G-01: AdminNotes GDPR exposure decision
GDPR data export omits `AdminNotes`. May qualify as personal data. Needs a documented decision: include in export, or document legal basis for exclusion.

#### P2-09: PII logging policy and redaction
Structured logs include emails and user IDs in plaintext. No redaction or classification policy. GDPR data minimization gap.

#### P2-03: Re-enable vulnerable package warning visibility
`NU1902`/`NU1903` warnings suppressed globally in `Directory.Packages.props`. Vulnerable dependencies won't surface in builds.

#### P2-07: Add integration tests for critical paths
Integration test project exists with TestContainers but has 0 tests. Critical compliance paths (consent, auth, deletion) untested end-to-end.

#### P2-08: Expand configuration health checks
`ConfigurationHealthCheck` only validates `GoogleMaps:ApiKey`. Missing checks for OAuth, SMTP, GitHub, and Google Workspace config.

#### P2-10: Feature spec lists AddressLine fields not in Profile entity
`docs/features/02-profiles.md` lists `AddressLine1`/`AddressLine2` which don't exist in the entity. DateOfBirth has since been added. Update the spec.

#### P1-04: Enforce export throttling
No rate limiting on GDPR data export. Any user can call `DownloadData` unlimited times.
**Where:** `ProfileController.cs:739`

---

### Priority 5: Technical Debt (Low Priority)

#### G-03: N+1 queries in GoogleWorkspaceSyncService
Helper methods re-query resources already loaded by parent methods. Redundant DB round-trips.

#### G-07: AdminController over-fetches data
`MemberDetail` loads ALL applications and consent records via `Include` when it only needs a few. `Members` list relies on implicit Include behavior.

#### G-08: Centralize admin business logic into services
All business logic lives in `AdminController`. Extract to service interfaces for testability and reuse.

#### G-09: Team membership caching
Every page load queries team memberships. At ~500 users, in-memory cache with short TTL would eliminate most DB hits.

#### P1-11: Implement real pagination at query layer
`GetAllTeamsAsync()` and `GetPendingRequestsForTeamAsync()` load everything into memory, then paginate in LINQ-to-Objects.

#### P2-04: Review prerelease/beta observability packages
Two OpenTelemetry packages pinned to beta versions. Check for stable releases or document risk acceptance.

#### F-06: Email body localization
Email subjects are localized but body content is still inline HTML with string interpolation. Full template engine is a future improvement.

---

### Blocked — Requires Production Environment

These need production domain, IP ranges, deployment model, or infrastructure decisions.

| ID | Issue | Blocked On |
|----|-------|------------|
| P0-01 | Lock down trusted proxy headers (IP spoofing risk) | Production reverse proxy IP ranges |
| P0-03 | Restrict health and metrics endpoints | Deployment model (R-03) |
| P0-04 | Enforce host header restrictions (`AllowedHosts: *`) | Production domain name |
| P0-12 | Docker healthcheck broken (curl missing) | Docker/K8s deployment |
| P0-13 | Replace insecure default credentials in docker-compose | Secrets management |
| P2-01 | Persist Data Protection keys | Production storage decision |
| P2-02 | Add explicit cookie/security policy settings | Production HTTPS setup |
| P2-05 | Improve consent metadata fidelity (IP/UA accuracy) | P0-01 (proxy trust) |

---

### Decision Items

| ID | Question | Options |
|----|----------|---------|
| R-01 | Should registration be restricted to `@nobodies.team`? | Restrict domain / Allow any + approval / Keep open |
| R-02 | Is anonymization sufficient for GDPR deletion? | Accept anonymization / Hard delete / Legal opinion |
| R-03 | Should `/metrics` and health endpoints be public? | Auth-restricted / Private network / Strip details |
| R-04 | Should Google Groups allow external members? | Members-only / Allow external / Per-group policy |

---

## Completed

### Issue #15: Redesign legal document management DONE
Team-scoped, multi-language legal documents with admin CRUD, folder-based GitHub sync, consent UI grouped by team with dynamic language tabs, per-document grace periods. Committed `b73982c`, `dbc6676`.

### Membership gating, volunteer sync, application language tracking DONE
Added MembershipRequiredFilter (gates app access on Volunteers team), ActiveMember claim, consolidated volunteer team sync into SyncVolunteersMembershipForUserAsync (triggered on both approval and consent), application language tracking, dashboard status fix, audit actor name fix, admin Sync System Teams button, consent tab ordering (Castellano first). Committed `28a2e8b`.

### G-06: SystemTeamSyncJob runs sequentially RESOLVED BY DESIGN
Sequential execution is intentional — the three sync methods share a single DbContext instance which is not thread-safe. Documented in code comment. Parallelizing would require IServiceScopeFactory to create separate scopes.

### P0-05: Fix admin authorization inconsistency DONE
Committed `f10a6b9`.

### F-02: Volunteer acceptance gate before system access DONE
Committed `4364b5d`.

### P0-06: Register all Hangfire jobs in DI DONE
Committed `502ed59`.

### P0-10: Suspend job does not actually suspend anyone DONE
Committed `97aec59`.

### P0-07: Consent immutability trigger is not applied by migrations DONE
Committed `ff7acc8`.

### P0-08: Prevent consent history deletion via user cascade DONE
Committed `e449e91`.

### P0-09: GDPR wording mismatch on account deletion DONE
Committed `f3200c2`.

### P0-02: Remove external token persistence unless needed DONE
Committed `2cde1dc`.

### P0-14: Fix privacy contact email inconsistency DONE
Committed `cba4767`.

### P0-11: Harden CSP (remove unsafe-inline) DONE
Committed `9dee750`. Addendum: added `cdn.jsdelivr.net` to script-src (`4189f8d`).

### P1-01: Correct membership status logic on profile page DONE
### P1-02: Correct pending consent calculation on profile page DONE
Committed `c7b127f`.

### P1-05: Fix anonymous email verification redirect DONE
Committed `3319eec`.

### P1-08: Enforce uniqueness for active team membership DONE
Committed `44330ec`.

### P1-10: Fix slug generation race conditions DONE
Committed `1c5bfc0`.

### P1-03: Add anti-caching headers to data export endpoints DONE
Committed `6287976`.

### G-02: N+1 query in SendReConsentReminderJob DONE
Committed `3966e79`.

### G-04: Google Drive provisioning not idempotent DONE
Committed `4243ca7`.

### F-03: Admin UI for managing team Google resources DONE
Committed `d9bf5c1`.

### F-04: Audit log for automatic user and resource changes DONE
Feature spec: `docs/features/12-audit-log.md`.

### F-05: Localization / PreferredLanguage support DONE
Committed `4189f8d`. Closes GitHub #7.

### F-01: Profile pictures with team photo gallery DONE
Committed `f04c8cf`.

### F-07: Admin dashboard RecentActivity widget DONE
### F-08: Admin dashboard PendingConsents metric DONE
### P1-14: Hangfire dashboard link returns 404 FIXED
### P1-15: Team Join view passes GUID where slug expected FIXED
### Drive Activity API monitoring DONE
Committed `f04c8cf`. Closes GitHub #11.
