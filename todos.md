# Release TODOs

Audit date: 2026-02-05 | Reorganized: 2026-02-06 | Updated: 2026-02-08

---

## Completed

### ~~P0-05: Fix admin authorization inconsistency~~ DONE

Replaced custom `AdminRoleRequirement` with `IClaimsTransformation` that syncs `RoleAssignment` entities to Identity role claims. Added Board/Admin role separation. Committed `f10a6b9`.

### ~~F-02: Volunteer acceptance gate before system access~~ DONE

Added `Profile.IsApproved` flag (default false). Board must approve before `SystemTeamSyncJob` enrolls user in Volunteers team. Dashboard shows pending count, member list supports `?filter=pending`, member detail has approve button, profile page shows pending alert. Existing profiles migrated as approved. Committed `4364b5d`.

### ~~P0-06: Register all Hangfire jobs in DI~~ DONE

Added missing `AddScoped` registrations for `ProcessAccountDeletionsJob` and `SuspendNonCompliantMembersJob`. Committed `502ed59`.

### ~~P0-10: Suspend job does not actually suspend anyone~~ DONE

Removed `AsNoTracking()`, set `Profile.IsSuspended = true`, added `SaveChangesAsync()`. Committed `97aec59`.

### ~~P0-07: Consent immutability trigger is not applied by migrations~~ DONE

Created migration that executes the PostgreSQL trigger SQL to prevent UPDATE/DELETE on consent_records. Committed `ff7acc8`.

### ~~P0-08: Prevent consent history deletion via user cascade~~ DONE

Changed ConsentRecord→User FK from Cascade to Restrict. Committed `e449e91`.

### ~~P0-09: GDPR wording mismatch on account deletion~~ DONE

Updated Privacy page wording from "permanently deleted" to "anonymized". Committed `f3200c2`.

### ~~P0-02: Remove external token persistence unless needed~~ DONE

Set `SaveTokens = false` in Google OAuth config. Committed `2cde1dc`.

### ~~P0-14: Fix privacy contact email inconsistency~~ DONE

Replaced hardcoded `admin@nobodies.es` in both Privacy views with `ViewData["AdminEmail"]` sourced from `Email:AdminAddress` config. Committed `cba4767`.

### ~~P0-11: Harden CSP (remove unsafe-inline)~~ DONE

Moved inline styles to `wwwroot/css/site.css`, inline cookie consent script to `wwwroot/js/site.js`. Replaced all `onclick/onsubmit` confirm handlers across 10 views with `data-confirm` attributes handled by delegated event listeners. Removed `'unsafe-inline'` from both `script-src` and `style-src` CSP directives. Committed `9dee750`.

### ~~P1-01: Correct membership status logic on profile page~~ DONE
### ~~P1-02: Correct pending consent calculation on profile page~~ DONE

Replaced ad-hoc `ComputeStatus()` and custom consent query with `IMembershipCalculator.ComputeStatusAsync()` and `GetMissingConsentVersionsAsync()`. Committed `c7b127f`.

### ~~P1-05: Fix anonymous email verification redirect~~ DONE

VerifyEmail now returns a `VerifyEmailResult` view directly instead of redirecting to `[Authorize]` actions. Committed `3319eec`.

### ~~P1-08: Enforce uniqueness for active team membership~~ DONE

Added partial unique index on `team_members(TeamId, UserId) WHERE LeftAt IS NULL`. Wrapped `SaveChangesAsync` in `JoinTeamDirectlyAsync` and `ApproveRequestAsync` with `DbUpdateException` catch. Committed `44330ec`.

### ~~P1-10: Fix slug generation race conditions~~ DONE

Replaced check-then-insert slug generation with retry loop on `DbUpdateException`. Committed `1c5bfc0`.

### ~~P1-03: Add anti-caching headers to data export endpoints~~ DONE

Added `[ResponseCache(NoStore = true)]` to `ExportData` and `DownloadData` actions. Committed `6287976`.

### ~~G-02: N+1 query in SendReConsentReminderJob~~ DONE

Batch-load users with `ToDictionaryAsync` instead of per-user queries. Committed `3966e79`.

### ~~G-04: Google Drive provisioning not idempotent~~ DONE

Added existence check in `ProvisionTeamFolderAsync` and partial unique index on `google_resources(TeamId, ResourceType) WHERE IsActive = true`. Committed `4243ca7`.

### ~~F-03: Admin UI for managing team Google resources~~ DONE

Added team resource management GUI with link/unlink/sync for Drive folders and Google Groups. Service account authenticates as itself (no impersonation — removed all `CreateWithUser` from codebase). Supports Shared Drives (`SupportsAllDrives`), hierarchical folder path display, inactive record reactivation on re-link. Dropped global unique constraint on `GoogleId` (replaced with non-unique index). Includes stub service for development, health check for Google Workspace connectivity, and service account setup docs. Committed `d9bf5c1`.

### ~~F-04: Audit log for automatic user and resource changes~~ DONE (Phase 1)

Added `AuditLogEntry` entity (append-only, immutability trigger), `IAuditLogService` with job and admin overloads, wired into `SystemTeamSyncJob` (TeamMemberAdded/Removed), `SuspendNonCompliantMembersJob` (MemberSuspended), `ProcessAccountDeletionsJob` (AccountAnonymized), and `AdminController` (MemberSuspended/Unsuspended, VolunteerApproved, RoleAssigned/Ended). Per-user audit view on MemberDetail page shows 50 most recent entries. Phase 2 will wire TeamService and GoogleWorkspaceSyncService. Feature spec: `docs/features/12-audit-log.md`.

### ~~F-05: Localization / PreferredLanguage support~~ DONE

Added full i18n infrastructure using `IStringLocalizer` with `.resx` resource files. 557 resource keys extracted from all views, controllers, and email subjects. Five languages: EN (default), ES (primary — Castilian, formal usted), DE (formal Sie), IT (formal Lei), FR (formal vous). Language chooser dropdown in navbar. `User.PreferredLanguage` wired to custom `RequestCultureProvider` for authenticated users, cookie fallback for anonymous. `LanguageController` persists preference to DB + cookie. Localization startup diagnostic in development. Fixed pre-existing CSP bug blocking Bootstrap JS from cdn.jsdelivr.net. Privacy policy legal text left as English (needs legal review per language). Committed `4189f8d`. Closes GitHub #7.

### ~~F-06: Email template system~~ PARTIALLY DONE

Email subjects localized via `IStringLocalizerFactory` in `SmtpEmailService` (13 subjects). Email body content remains inline HTML with string interpolation — a full template engine (Razor views, Scriban, etc.) is still a future improvement for body localization and admin customization.

### ~~P0-11 (addendum): CSP blocking Bootstrap JS~~ FIXED

Added `https://cdn.jsdelivr.net` to `script-src` CSP directive. Bootstrap JS was silently blocked, breaking all interactive components (dropdowns, modals, collapse). Committed with `4189f8d`.

### ~~F-01: Profile pictures with team photo gallery~~ DONE

Added profile picture upload with resize to 256x256 (max 2MB, stored in DB). Team detail view shows member photo gallery with meta leads at top. Also added DateOfBirth field and team birthdays view. Feature spec: `docs/features/14-profile-pictures-birthdays.md`. Committed `f04c8cf`.

### ~~F-07: Admin dashboard RecentActivity widget~~ DONE

Wired `RecentActivity` to query the 15 most recent `AuditLogEntry` records. Dashboard now shows recent activity list. Committed `f04c8cf`.

### ~~F-08: Admin dashboard PendingConsents metric~~ DONE

Calculated `PendingConsents` using `IMembershipCalculator.GetUsersWithAllRequiredConsentsAsync()`. Dashboard now displays the metric with warning styling when non-zero. Committed `f04c8cf`.

### ~~P1-14: Hangfire dashboard link returns 404~~ FIXED

Replaced `asp-controller="Hangfire" asp-action="Index"` with direct `href="/hangfire"` link. Committed `f04c8cf`.

### ~~P1-15: Team Join view passes GUID where slug is expected~~ FIXED

Added `TeamSlug` property to `JoinTeamViewModel` and updated Join view to use `asp-route-slug="@Model.TeamSlug"` instead of `@Model.TeamId`. Committed `f04c8cf`.

### ~~Drive Activity API monitoring~~ DONE

Added `IDriveActivityMonitorService` with `DriveActivityMonitorJob` to detect anomalous permission changes on Shared Drives. Audit log entries created for detected anomalies. Admin can trigger manual check from Audit Log page. Feature spec: `docs/features/13-drive-activity-monitoring.md`. Committed `f04c8cf`. Closes GitHub #11.

---

## 1. Missing Features (toward overall goal)

These are prioritized by how critical they are to the core purpose: managing the membership lifecycle, organizing teams, and giving Board visibility into automatic actions.

No open feature items — all planned features are complete.

---

## 2. Bugs & Issues — P0 (Critical)

All P0 items completed. See Completed section above.

---

## 3. Bugs & Issues — P1 (High)

### P1-14: Hangfire dashboard link returns 404

**Where:** `src/Humans.Web/Views/Admin/Index.cshtml:76`

**What happens today:** The admin dashboard has a "Background Jobs" link using `asp-controller="Hangfire" asp-action="Index"`, but Hangfire is mounted as middleware at `/hangfire`, not as an MVC controller. Clicking the link returns 404.

**Fix:** Replace `asp-controller`/`asp-action` with a direct `href="/hangfire"` link.

---

### P1-15: Team Join view passes GUID where slug is expected

**Where:** `src/Humans.Web/Views/Team/Join.cshtml:9,41,57`

**What happens today:** Breadcrumb and Cancel links use `asp-route-slug="@Model.TeamId"` but `TeamController.Details(string slug)` expects a slug string, not a GUID. These navigation links will break.

**Fix:** Add a `TeamSlug` property to `JoinTeamViewModel` and pass it from the controller, then use `asp-route-slug="@Model.TeamSlug"` in the view.

---

### P1-16: Stubs silently activate in production if Google credentials are missing

**Where:** `src/Humans.Web/Program.cs:146-157`

**What happens today:** `StubGoogleSyncService` and `StubTeamResourceService` activate when `ServiceAccountKeyPath`/`ServiceAccountKeyJson` are not configured. `StubTeamResourceService` creates **real DB records** with fake Google IDs — orphaned data that will confuse reconciliation. No startup warning or health check failure for this case.

**Fix:** Either fail fast on startup when Google credentials are missing in production, or add a health check that reports degraded/unhealthy when stubs are active. At minimum, log a warning at startup.

---

### P1-06: Domain restriction decision for Google login

**Where:** `src/Humans.Web/Controllers/AccountController.cs:91` and `Program.cs:71-81`

**What happens today:** Any Google account (gmail.com, yahoo.com, any domain) can auto-create a user. No `HostedDomain` restriction, no approval gate.

**Why it matters:** The system is for a Spanish nonprofit (Nobodies Collective) but anyone with a Google account can create a profile. This may conflict with membership governance.

**Fix:** This is partially a policy decision (see R-01). Options:
- Set Google's `HostedDomain` option to restrict to `nobodies.team`
- Check the `hd` claim in `ExternalLoginCallback` and reject non-matching domains
- Require admin approval before first login (invitation model)

---

### P1-07: Add transactional consistency for Google sync

**Where:** `src/Humans.Infrastructure/Services/TeamService.cs:258,261` and `:574,577`

**What happens today:** In `JoinTeamDirectlyAsync` and `RemoveMemberAsync`, the database transaction commits first, then the Google API call happens. If Google fails (network error, quota, permissions), the DB is committed but Google resources are out of sync.

**Why it matters:** User is recorded as team member in DB but not added to Google Group/Drive. No automatic retry. Drift accumulates until the hourly sync job runs.

**Fix:** Outbox pattern — insert a pending `TeamSyncEvent` in the same DB transaction, then process it asynchronously with retries. Add a reconciliation job to detect and fix drift.

---

### P1-09: Enforce uniqueness for active role assignments

**Where:** `src/Humans.Infrastructure/Data/Configurations/RoleAssignmentConfiguration.cs:39` and `AdminController.cs:663-674`

**What happens today:** Partial unique index filters on `ValidTo IS NULL` only. Controller checks for overlap with current time but not future-dated assignments. Two concurrent requests can both pass the check.

**Why it matters:** Future-dated role assignments can overlap (e.g., Board role from Feb 1-Mar 1 and Feb 15-Apr 1). The index doesn't catch this. Temporal overlaps create ambiguity in role auditing.

**Fix:** Use a PostgreSQL exclusion constraint with `tsrange`:
```sql
EXCLUDE USING gist (user_id WITH =, role_name WITH =, tsrange(valid_from, valid_to) WITH &&)
```
Or validate overlapping intervals in code before insert.

---

### P1-12: Google group sync misses paginated results

**Where:** `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs:334,554`

**What happens today:** `Members.List()` and `Permissions.List()` are called once without handling `NextPageToken`. Only the first page of results is processed.

**Why it matters:** Groups with >200 members (default page size) will have members silently dropped from sync. The sync job may *remove* legitimate members it doesn't see because they're on page 2+.

**Fix:** Loop on `NextPageToken` until all pages are retrieved before comparing membership.

---

### P1-13: Apply configured Google group settings during provisioning

**Where:** `src/Humans.Infrastructure/Configuration/GoogleWorkspaceSettings.cs:49-78` and `GoogleWorkspaceSyncService.cs:208-215`

**What happens today:** `GoogleWorkspaceSettings.GroupSettings` defines configurable properties (WhoCanViewMembership, WhoCanPostMessage, AllowExternalMembers), but group provisioning ignores them entirely. Groups are created with Google's defaults.

**Why it matters:** Config says `AllowExternalMembers = true` but this is never applied. If org policy changes, the config change has no effect. Groups may have overly permissive defaults.

**Fix:** After creating a group, call the Groups Settings API to apply the configured settings.

---

## 4. Bugs & Issues — Gemini Audit Items

### G-01: AdminNotes GDPR exposure decision

**Where:** `src/Humans.Domain/Entities/Profile.cs:110`

**What happens today:** `AdminNotes` is a hidden field on profiles. Admins can write notes about members. The GDPR data export (`ProfileController.ExportData`) does **not** include this field — it exports FirstName, LastName, Phone, City, etc. but silently omits AdminNotes.

**Why it matters:** Under GDPR, users have the right to access all personal data stored about them. Admin-written assessments about an individual may qualify as personal data. Withholding it from export could be a compliance gap.

**Decision needed:**
- If AdminNotes is personal data → include in export with clear labeling
- If not → document the legal basis for exclusion (e.g., legitimate interest in internal administration)
- Either way, document the decision

---

### G-03: N+1 queries in GoogleWorkspaceSyncService

**Where:** `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` (multiple locations)

**What happens today:** Several methods load resources, then call helper methods that re-query the same resources from the database. For example, `AddUserToTeamResourcesAsync` loads all resources for a team, then calls `AddUserToGroupAsync` which queries the resource again by ID. Similarly, `SyncResourcePermissionsAsync` loads resources with members, then makes individual Google API calls per member.

**Why it matters:** Unnecessary DB round-trips per member per resource. With 10 resources and 50 members, that's potentially hundreds of redundant queries.

**Fix:** Pre-load all needed entities, pass them directly to helper methods instead of re-querying by ID. Use batch Google API calls where the API supports it.

---

### G-05: No reminder frequency tracking (spam risk)

**Where:** `src/Humans.Infrastructure/Jobs/SendReConsentReminderJob.cs`

**What happens today:** The job sends re-consent reminders to all eligible users on every execution with no cooldown. No `LastConsentReminderSentAt` field or equivalent exists. If the job runs daily, users get emailed every day until they consent.

**Why it matters:** Daily emails are spam. Users will ignore or report them. ISP reputation damage. Bad UX.

**Fix:** Add `LastConsentReminderSentAt` (Instant?) to User entity. Check cooldown (e.g., 7 days) before sending. Update timestamp after each send. Requires a migration.

---

### G-06: SystemTeamSyncJob runs sequentially

**Where:** `src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs:40-57`

**What happens today:** `ExecuteAsync` awaits three independent sync operations sequentially: `SyncVolunteersTeamAsync`, `SyncMetaleadsTeamAsync`, `SyncBoardTeamAsync`. Each waits for the previous to complete.

**Why it matters:** If each sync takes 30 seconds, total is 90 seconds. These syncs operate on different teams with no shared state — they're safe to parallelize.

**Fix:** Replace sequential awaits with `await Task.WhenAll(...)`. Total time becomes ~30 seconds (longest single sync).

---

### G-07: AdminController over-fetches data

**Where:** `src/Humans.Web/Controllers/AdminController.cs` (MemberDetail, Members list)

**What happens today:**
- `MemberDetail` (line 111) loads ALL applications and ALL consent records via `Include`, even though it only displays the 5 most recent applications and a count. A user with 1000 consent records loads all 1000 rows.
- `Members` list projects `u.Profile != null` in a `Select()` but doesn't explicitly `Include(u => u.Profile)` — relies on implicit behavior.

**Why it matters:** Wastes memory and bandwidth. Gets worse as data grows.

**Fix:** For MemberDetail: use separate count queries for consents/applications, and only `Include` the 5 most recent applications. For Members: add explicit `.Include(u => u.Profile)` before the projection.

---

### G-08: Centralize admin business logic into services

**Where:** `src/Humans.Web/Controllers/AdminController.cs` (throughout)

**What happens today:** The controller directly uses `HumansDbContext` for dashboard metrics, member suspension/unsuspension, role assignment/removal, and member detail loading. All business logic lives in the controller.

**Why it matters:**
- Violates Clean Architecture (controller should only handle HTTP concerns)
- Business logic is untestable without a database
- Logic can't be reused (e.g., suspending a member from a background job would require duplicating the controller code)

**Fix:** Extract into service interfaces: `IAdminDashboardService` (metrics), `IUserAdminService` (suspend/unsuspend), `IRoleAdminService` (assign/end roles). Controller becomes thin — calls services, maps to view models, returns views.

**Priority:** Low — refactoring quality improvement, not a bug or security issue.

---

### G-09: Team membership caching

**Where:** `src/Humans.Infrastructure/Services/TeamService.cs`

**What happens today:** Every call to `GetAllTeamsAsync`, `GetUserTeamsAsync`, `GetTeamBySlugAsync` hits the database directly. No caching. Profile page loads trigger `GetUserTeamsAsync` on every view.

**Why it matters:** Team memberships change infrequently but are read on nearly every page load. For a small user base (<500), the data set is small enough to cache entirely in memory.

**Fix:** Wrap `TeamService` in a `CachedTeamService` decorator using `IMemoryCache` or `IDistributedCache`. Invalidate on mutations (join, leave, create, delete). Short TTL (5-30 minutes) is fine since membership changes are rare.

**Priority:** Low — premature optimization for current scale.

---

## 5. Bugs & Issues — P2 (Quality / Compliance)

### P1-04: Enforce export throttling

**Where:** `src/Humans.Web/Controllers/ProfileController.cs:739`

**What happens today:** Comment says "implement rate limiting if needed" but no throttling exists. Any user can call DownloadData unlimited times.

**Why it matters:** Data export queries multiple tables and serializes large JSON. Unthrottled access enables abuse (DoS via repeated exports). No audit trail of exports.

**Fix:** Per-user throttle (e.g., 1 export/hour) using `IDistributedCache` or a database timestamp. Log each export for audit.

---

### P1-11: Implement real pagination at query layer

**Where:** `src/Humans.Web/Controllers/TeamController.cs:39` and `TeamAdminController.cs:51,184`

**What happens today:** `GetAllTeamsAsync()` and `GetPendingRequestsForTeamAsync()` load all records into memory, then `Skip/Take` is applied in LINQ-to-Objects.

**Why it matters:** Memory usage scales with total data, not page size. For 1000 teams, all 1000 are loaded to show page 1 of 20.

**Fix:** Push `Skip/Take` into EF queries via new paged service methods (e.g., `GetAllTeamsPagedAsync(page, pageSize)`).

---

### P2-03: Re-enable vulnerable package warning visibility

**Where:** `Directory.Packages.props:6`

**What happens today:** `NU1902`/`NU1903` warnings (vulnerable package alerts) are suppressed globally.

**Why it matters:** Known security vulnerabilities in NuGet dependencies won't surface in CI builds.

**Fix:** Remove global suppression. If specific packages have known false positives, scope suppressions with documented justification and an expiry date.

---

### P2-04: Review prerelease/beta observability packages

**Where:** `Directory.Packages.props:40,43`

**What happens today:**
- `OpenTelemetry.Exporter.Prometheus.AspNetCore` pinned to `1.10.0-beta.1`
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` pinned to `1.0.0-beta.12`

**Why it matters:** Beta packages lack stability guarantees. Breaking changes can appear without notice. Support/maintenance is not guaranteed.

**Fix:** Check for stable releases and upgrade. If no stable version exists, document the risk acceptance and pin with a review date.

---

### P2-06: Schedule orphaned SendReConsentReminderJob

**Where:** `src/Humans.Infrastructure/Jobs/SendReConsentReminderJob.cs` (exists but not scheduled)

**What happens today:** The job class exists and is implemented, but it's not registered in DI and not scheduled in `Program.cs`.

**Why it matters:** Members don't receive re-consent reminders before the grace period expires and suspension kicks in. The compliance workflow is incomplete — users get suspended without warning.

**Fix:** Register in DI and schedule (e.g., daily, 7 days before grace period expiry).

---

### P2-07: Add integration tests for critical paths

**Where:** `tests/Humans.Integration.Tests/` — project exists with TestContainers setup but contains 0 test files.

**What happens today:** Unit tests cover domain and application logic (42 tests). No integration tests exercise auth, consent submission, admin role enforcement, or account deletion flows end-to-end.

**Why it matters:** Critical compliance and security paths are untested in an integrated environment.

**Fix:** Add integration tests for: Google OAuth login flow, consent submission/status transitions, admin policy enforcement, account deletion/anonymization.

---

### P2-08: Expand configuration health checks

**Where:** `src/Humans.Web/Health/ConfigurationHealthCheck.cs:15`

**What happens today:** Only validates `GoogleMaps:ApiKey`. Does not check Google OAuth credentials, SMTP settings, GitHub token, or Google Workspace service account config.

**Why it matters:** `/health/ready` can report healthy even when critical config is missing. App starts but features fail at runtime.

**Fix:** Expand `RequiredKeys` to include auth, email, and sync config. Optionally degrade gracefully by feature flag rather than hard-failing.

---

### P2-10: Feature spec lists fields not in Profile entity (DateOfBirth, AddressLine)

**Where:** `docs/features/02-member-profiles.md:62-66` vs `src/Humans.Domain/Entities/Profile.cs`

**What happens today:** The member profiles feature spec data model lists `DateOfBirth: LocalDate?`, `AddressLine1: string? (512)`, and `AddressLine2: string? (512)`, but these fields do not exist in the `Profile` entity or anywhere in the codebase.

**Fix:** Either add the fields to the entity (if they're wanted) or update the feature spec to remove them. This is documentation drift — the spec describes a data model that doesn't match implementation.

---

### P2-09: PII logging policy and redaction

**Where:** Multiple files (e.g., `SuspendNonCompliantMembersJob.cs:85` logs emails in plaintext)

**What happens today:** Structured logs include user IDs, email addresses, and other PII in plaintext. No redaction filters or classification policy.

**Why it matters:** Log aggregation services (CloudWatch, Datadog, etc.) store PII without explicit retention or access controls. GDPR requires data minimization.

**Fix:** Define a logging classification policy. Apply Serilog destructuring/redaction for PII fields. Document log retention periods.

---

## 6. Paused — Blocked on Production Environment

These items require knowing the production domain, IP ranges, deployment model, or infrastructure before they can be addressed. Revisit once hosting is established.

### P0-01: Lock down trusted proxy headers (spoofing risk)

**Blocked on:** Production reverse proxy IP ranges / cloud load balancer config.

**Where:** `src/Humans.Web/Program.cs:192-193`

**What happens today:** `ForwardedHeadersOptions` clears both `KnownIPNetworks` and `KnownProxies`, trusting `X-Forwarded-For` from any source. Enables IP spoofing, rate limiter bypass, and audit log poisoning.

**Fix:** Configure explicit trusted proxy IPs/subnets from environment config once deployment target is known.

---

### P0-03: Restrict health and metrics endpoints

**Blocked on:** Deployment model decision (R-03) — network-level restriction vs auth-level.

**Where:** `src/Humans.Web/Program.cs:254-270`

**What happens today:** `/health`, `/health/ready`, and `/metrics` are publicly accessible without authentication. Exposes infrastructure details (DB status, latencies, dependency health).

**Fix:** Keep `/health/live` public. Protect the rest via `RequireAuthorization()` or private network, depending on deployment model.

---

### P0-04: Enforce host header restrictions

**Blocked on:** Production domain name.

**Where:** `src/Humans.Web/appsettings.json:90` — `"AllowedHosts": "*"`

**What happens today:** Accepts any Host header. Enables cache poisoning and email link hijacking.

**Fix:** Set `AllowedHosts` to actual production domain(s) once known.

---

### P0-12: Docker healthcheck broken (curl missing)

**Blocked on:** Production Docker/K8s deployment.

**Where:** `Dockerfile:42-43`

**What happens today:** HEALTHCHECK uses `curl` which isn't in the runtime image. Container is always "unhealthy."

**Fix:** Install curl or use alternative health check binary. Only matters when deploying via Docker.

---

### P0-13: Replace insecure default credentials in docker-compose

**Blocked on:** Production infrastructure / secrets management.

**Where:** `docker-compose.yml:28-30, 12, 88-89`

**What happens today:** Hardcoded credentials (profiles/profiles for PostgreSQL, admin/admin for Grafana) in version control. Fine for local dev, problematic beyond that.

**Fix:** Move to `.env` file with generated secrets once deployment environments are established.

---

### P2-01: Persist Data Protection keys

**Blocked on:** Production deployment model (where to persist — Redis, DB, filesystem, Azure Key Vault).

**Where:** `src/Humans.Web/Program.cs` — no `AddDataProtection()` call.

**What happens today:** Keys stored in-memory, lost on restart. All sessions invalidated on deploy.

**Fix:** Configure key persistence once production storage is decided.

---

### P2-02: Add explicit cookie/security policy settings

**Blocked on:** Production HTTPS setup (SecurePolicy.Always requires HTTPS).

**Where:** `src/Humans.Web/Program.cs` — no `ConfigureApplicationCookie()` call.

**What happens today:** Relies on ASP.NET Identity defaults.

**Fix:** Configure SecurePolicy, HttpOnly, SameSite, expiration once HTTPS is in place.

---

### P2-05: Improve consent metadata fidelity

**Blocked on:** P0-01 (proxy trust) — IP accuracy is meaningless until proxy headers are trusted correctly.

**Where:** `src/Humans.Web/Controllers/ConsentController.cs:194` and `ConsentRecordConfiguration.cs:28`

**What happens today:** User-Agent truncated at 500 vs DB column of 1024. IP accuracy depends on proxy trust.

**Fix:** Align truncation limits. Document retention policy. Pair with P0-01.

---

## Decision Items

These require a policy decision before implementation.

### R-01: Self-registration policy

**Question:** Should any Google account be able to create a user, or should registration be restricted?

**Current behavior:** Any Google account auto-creates a user on first login. No domain restriction or approval gate.

**Options:**
- Restrict to `@nobodies.team` Google Workspace domain
- Allow any Google account but require admin approval before first access
- Keep open registration (current behavior, document as intentional)

**Related:** P1-06, F-02

---

### R-02: Anonymization vs. hard deletion for GDPR

**Question:** Is anonymization legally sufficient for account deletion requests under your GDPR interpretation?

**Current behavior:** `ProcessAccountDeletionsJob` anonymizes (replaces PII with placeholders) but retains records for audit.

**Options:**
- Accept anonymization as sufficient (document legal basis)
- Implement hard deletion with separate minimal audit log
- Get formal legal opinion

**Related:** P0-09

---

### R-03: Public vs. private observability endpoints

**Question:** Should `/metrics` and detailed health endpoints be internet-accessible?

**Current behavior:** All health and metrics endpoints are public (no auth, no network restriction).

**Options:**
- Restrict to authenticated admin users
- Restrict to private network (VPC, internal service mesh)
- Keep public but strip detailed information

**Related:** P0-03

---

### R-04: Google Group external member policy

**Question:** Should Google Groups allow external members by default?

**Current behavior:** Config says `AllowExternalMembers = true` but is never applied (P1-13). Google's default may vary.

**Options:**
- Members-only (restrict to organizational domain)
- Allow external members (current config intent)
- Per-group policy based on team type

**Related:** P1-13
