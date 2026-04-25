# Freshness Sweep Report — 2026-04-25 22:10:57 UTC

**Anchor:** upstream/main @ 8f6a2556 (previous: none)
**Mode:** full
**Entries dirty:** 11
**Entries updated:** 9
**Entries flagged:** 4
**Entries skipped:** 2
**Questions accumulated:** 0

## Updated automatically

- `about-page-packages` — Bumped 5 Microsoft framework packages from 10.0.6 to 10.0.7 and added 5 missing production packages (DataProtection.Extensions, EntityFrameworkCore.Design, Google.Apis.Admin.Directory, Google.Apis.Groupssettings, Serilog).
- `docs-readme-index` — Regenerated indexed tables for `features/`, `sections/`, and `guide/`; added Section Invariants and User Guide tables; refreshed feature spec descriptions; added entries for new docs (33–39 series, communication-preferences, event-participation, gdpr-export).
- `authorization-inventory` — Refreshed controller authorization map and added resource-based handler section to reflect migration from RoleGroups to PolicyNames; AdminController/GoogleController class-level `[Authorize]` attributes have been removed and actions are now individually policy-decorated.
- `controller-architecture-audit` — Regenerated action-name audit for current 46 controllers (was 35); reflects HumanController → ProfileController merger, new Google/Email/Contacts/Calendar/CityPlanning/Guest/Notification/AdminDuplicateAccounts controllers, and updated rename suggestions and misplaced-action analysis.
- `dependency-graph` — Regenerated Mermaid dependency graph from current service constructors: added MembershipQuery, NotificationEmitter, NotificationInboxService, NotificationRecipientResolver, MagicLinkService, EmailOutboxService, GdprExportService nodes; corrected stale edges (Onboard no longer depends on Audit; Consent uses NotifInbox; CampService and TeamService inject NotificationEmitter; ShiftMgmt has eager Audit; CommPref/AcctProv depend on Audit; Dashboard adds Team; MembershipCalc routes through MemQuery; Team lazy edges to Role and Email; GSyncSvc lazy edge to TRes).
- `service-data-access-map` — Regenerated service-data-access-map by walking each service's repository deps to its EF DbSet usage and cache-key surface; reflects the §15 repository pattern and adds new sections (Calendar, Dashboard, Gdpr, Users; separated Camps/CityPlanning/Legal/Consent), new services (CalendarService, DashboardService, GdprExportService, UserService, UnsubscribeService, NotificationEmitter, NotificationRecipientResolver, MembershipQuery), and the CachingProfileService Singleton decorator owning the FullProfile cache.
- `data-model-index` — Added ProfileLanguage row (Profiles section) and removed TeamPage row (entity and configuration no longer exist).
- `guid-reservations` — Fixed stale source paths for blocks 0003 (Shifts/) and 0010 (Camps/) and added TeamConfiguration.cs as a co-source for block 0001.
- `code-analysis-suppressions` — Added missing CS1591 and VSTHRD200 entries from `Directory.Build.props` `<NoWarn>` and xUnit1051 from `tests/Directory.Build.props`; preserved existing entry wording.

## Flagged for human review

### docs/authorization-inventory.md
**Triggers fired:** full sweep — all controller/authorization sources
**Why:** Section 2 (View Authorization Map) was preserved with line numbers from the 2026-04-03 generation; views were not re-scanned in this sweep so view line numbers may be stale.
**Suggested follow-up:** Re-scan `src/Humans.Web/Views/` for current `RoleChecks`/`ShiftRoleChecks` usage and update view-table line numbers.

### docs/authorization-inventory.md
**Triggers fired:** full sweep — all controller/authorization sources
**Why:** Phase 1 of the transition plan (policy registration) is now complete in code; the inventory still references `plans/2026-04-03-first-class-authorization-transition.md` as the in-flight plan.
**Suggested follow-up:** Confirm whether the transition plan should be archived or whether a Phase 2 (resource-based) plan should replace the link.

### docs/architecture/dependency-graph.md
**Triggers fired:** full sweep — all service constructors and DI registration
**Why:** Prose sections (Cycles broken by lazy-resolution, Fan-in hotspots table, Notes on architectural follow-ups) cite specific edge counts and a fixed list of cycles that may not match the regenerated diagram exactly. The catalog prompt instructed regenerating only the diagram body, so prose was left as-is.
**Suggested follow-up:** Update the fan-in hotspot counts and cycle list in a separate editorial pass to reflect the current diagram (e.g., AuditLogService dependent count, addition of GSyncSvc-TRes lazy edge, Team-Email/Team-Role lazy edges).

### docs/architecture/service-data-access-map.md
**Triggers fired:** full sweep — all services + HumansDbContext
**Why:** `TicketDashboardStats` cache key is invalidated by `TicketSyncService` but no service-layer writer was found; populator is presumably a controller or view component.
**Suggested follow-up:** Grep the Web project for `TicketDashboardStats` and confirm/document the populator (likely `TicketController` or a dashboard view component).

### docs/architecture/service-data-access-map.md
**Triggers fired:** full sweep
**Why:** `SystemSettings` has no owning service; both `DriveActivityMonitorRepository` and `EmailOutboxRepository` touch it. The map flags this but does not propose an owner.
**Suggested follow-up:** Consider creating `ISystemSettingsService` (or assigning ownership to AuditLog/Email per usage) and route both call sites through it.

### docs/architecture/service-data-access-map.md
**Triggers fired:** full sweep
**Why:** Appendix A controllers (Profile/Board/Budget/CampAdmin/Guest/Unsubscribe) were inherited from the prior version without re-auditing each one against current code; some entries may be stale post-§15 migration.
**Suggested follow-up:** Run a separate pass over `src/Humans.Web/Controllers/` to refresh Appendix A with verified current direct-DB access.

### docs/architecture/conventions.md
**Triggers fired:** controller-architecture-audit subagent flagged dependency
**Why:** The freshness-catalog prompt for `controller-architecture-audit` instructs the audit to flag rename suggestions only when they violate action-name conventions in `conventions.md`, but `conventions.md` does not codify any action-name convention. The audit therefore continues to apply the same heuristic standard used in the original (peterdrier#261) audit.
**Suggested follow-up:** Add an "Action Naming" subsection to `docs/architecture/conventions.md` codifying the heuristics used here, or update `freshness-catalog.yml` to point at a different conventions doc.

### Editorial trees (`docs/sections/`, `docs/features/`, `docs/guide/`, plus the 4 single files in `editorial_trees`)
**Triggers fired:** full sweep — every editorial doc is dirty in `--full` mode
**Why:** None of these docs currently carry inline `freshness:triggers`, `freshness:auto`, or `freshness:flag-on-change` markers. In full sweep mode every editorial doc is therefore "unmarked"; in diff mode they would silently miss any drift.
**Suggested follow-up:** For each section/feature doc whose content depends on src/ — especially authorization rules, workflow logic, and data-model field tables — add a `freshness:flag-on-change` block at the top with the relevant trigger globs so future sweeps can flag them when source changes.

## Skipped (errors)

- (`dev-stats`) `docs/scripts/generate-stats.sh` writes pipe-delimited rows to STDOUT but the catalog target `docs/development-stats.md` is structured markdown (summary tables, language breakdown, prose) that the script does not produce. Running the script verbatim would either no-op (if redirection isn't applied) or destroy the file structure. **Catalog wiring needs review** — either add a formatting wrapper that converts the pipe data into the existing markdown layout, or split this into a CSV-target raw-data file plus a separate prompt-driven entry that consumes that CSV to refresh the markdown.
- (`reforge-history`) `docs/scripts/generate-reforge-history.sh` is incrementally correct but has 132 commits to backfill since the last entry (2026-04-15, commit `6221e43`). Each commit requires `reforge snapshot --solution Humans.slnx` which performs a full Roslyn analysis (~10–30s/commit), making this far too slow for an inline freshness sweep. **Recommendation:** run this once manually to catch up (`bash docs/scripts/generate-reforge-history.sh`), then add a per-commit hook or daily cron so future sweeps only need to append a small handful of rows.
