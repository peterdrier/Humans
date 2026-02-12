# Release TODOs

Audit date: 2026-02-05 | Last updated: 2026-02-12

---

## Open Work — Prioritized

### Priority 1: Bugs

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
`GoogleWorkspaceSettings.GroupSettings` properties (WhoCanViewMembership, AllowExternalMembers, etc.) are defined but never applied. Groups get Google defaults. Per R-04, external members must be allowed.
**Where:** `GoogleWorkspaceSettings.cs:49-78`, `GoogleWorkspaceSyncService.cs:208-215`

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

#### #22: Add EF Core query monitoring to identify caching opportunities
Add a `DbCommandInterceptor` tracking query counts by table + operation (SELECT/INSERT/UPDATE/DELETE) in a singleton `ConcurrentDictionary`. Expose via admin page at `/Admin/DbStats`. Informs future `IMemoryCache` adoption for hot read paths. No persistence needed — resets on restart.

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
| P0-03 | Restrict health and metrics endpoints | Deployment model (public OK for now per R-03, revisit post-launch) |
| P0-04 | Enforce host header restrictions (`AllowedHosts: *`) | Production domain name |
| P0-12 | Docker healthcheck broken (curl missing) | Docker/K8s deployment |
| P0-13 | Replace insecure default credentials in docker-compose | Secrets management |
| P2-01 | Persist Data Protection keys | Production storage decision |
| P2-02 | Add explicit cookie/security policy settings | Production HTTPS setup |
| P2-05 | Improve consent metadata fidelity (IP/UA accuracy) | P0-01 (proxy trust) |

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

### #3: Full Lead rename (domain, DB, code) DONE
Renamed all internal "Lead" references across domain, application, infrastructure, web, tests, migrations, resources, and documentation.

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
