# Maintenance Log

Tracks when recurring maintenance processes were last run.

| Process | Last Run | Next Due | Cadence | Est. Cost | Notes |
|---------|----------|----------|---------|-----------|-------|
| NuGet vulnerability check | 2026-04-05 | 2026-04-12 | Weekly | — | `dotnet list package --vulnerable` |
| Freshness sweep (diff) | — | — | Daily | — | `/freshness-sweep` — auto-refresh drift-prone docs against upstream/main diffs. First run pending. |
| Freshness sweep (full) | — | — | Weekly | — | `/freshness-sweep --full` — full regeneration of every catalog entry. First run pending. |
| Todo audit | 2026-03-08 | 2026-03-15 | Weekly | — | Stale items, completed moves |
| Code simplification | 2026-06-11 | — | After features | codex: ~5% | per-section pass: #969, #972, #973, #974, #975, #976, #977, #978, #979, #980 |
| ReSharper InspectCode | 2026-06-10 | 2026-06-17 | Weekly | — | `/resharper` — fix Tier 1+2 warnings. Codex can't run `jb` in sandbox. 2026-06-10: #928 sweep (~800 redundant-code findings + dead Back-to-Admin link). |
| Context cleanup | 2026-03-18 | 2026-04-18 | Monthly | — | CLAUDE.md, .claude/, todos.md |
| Feature spec sync | 2026-04-05 | 2026-05-05 | Monthly | — | docs/features/ vs implementation |
| i18n audit | 2026-02-24 | 2026-03-24 | Monthly | gemini: ~2% | Missing translations |
| Data model doc sync | 2026-02-12 | As needed | As needed | — | docs/architecture/data-model.md vs entities |
| Navigation audit | 2026-03-22 | 2026-04-22 | Monthly | — | `/nav-audit` — discoverability, backlinks |
| GDPR audit | — | — | Quarterly | — | Exports, consent, PII logging |
| Migration squash check | 2026-02-24 | 2026-03-24 | Monthly | — | Check `/Debug/DbVersion` on prod, QA (humans.n.burn.camp), and local dev. Oldest `lastApplied` across all three is the safe squash boundary. |
| Architecture ratchet sweep (`arch-sweep`) | 2026-05-16 | 2026-05-30 | Bi-weekly | — | Run the ratchet test suite (`dotnet test --filter "FullyQualifiedName~Architecture.Rules"`), report counts per rule, and trend baseline sizes vs. the previous sweep. Goal: every baseline shrinks. New violations get fixed at the source (or, if intentional, surface in PR review when the rule's test goes red). Scanners + baselines: `tests/Humans.Application.Tests/Architecture/{Ratchet,Rules,Baselines}/`. Source rules: `memory/architecture/*.md` (no-cross-section-ef-joins, no-drops-until-prod-verified, repository-required-for-db-access, no-concurrency-tokens, no-linq-at-db-layer, no-startup-guards, display-sort-in-controllers, no-business-logic-in-controllers) and `docs/architecture/code-review-rules.md` (no controller-injects-DbContext). 2026-05-16 sweep (peterdrier/Humans#595 — Stream E): all baselines stable or shrunk vs 2026-05-04 except `NoDestructiveMigrationOps` (+2; new migrations) and `OnlyAuditLogRepositoryWritesAuditLogEntries` (+1 — real regression, follow-up). Two new rules added the same sweep — `ApplicationServiceEntityReadReturns` (52 initial) and `CrossSectionRepositoryInjection` (1) — those numbers are seeds, not regressions. Field-level invariants (DisplayName / Profile.IsSuspended reads) enforced via `[Obsolete]` + CS0618, not separate scanners. /Admin/* route invariant enforced via memory atom `no-admin-url-section` + code review. Established 2026-05-04 in nobodies-collective/Humans#636. |
| NuGet full update | 2026-05-17 | 2026-06-17 | Monthly | — | Non-security package updates. 2026-05-17: LOW-risk batch — ASP.NET Core / EF Core / DataProtection / Localization / Mvc.Testing / Identity.EFCore 10.0.7 → 10.0.8 (servicing roll incl. ManagedAuthenticatedEncryptor calc fix); `System.Security.Cryptography.Xml` transitive pin 10.0.7 → 10.0.8; Google.Apis.* (Auth/Admin.Directory/CloudIdentity/Drive/DriveActivity/Groupssettings) → 1.74; `Markdig` 1.1.3 → 1.2.0; `Microsoft.NET.Test.Sdk` 18.4.0 → 18.5.1; `Meziantou.Analyzer` 3.0.54 → 3.0.85; `Microsoft.CodeAnalysis.CSharp` 5.0.0 → 5.3.0 (+ companion pins Common/CSharp.Workspaces/Workspaces.Common/Workspaces.MSBuild). Deferred MEDIUM: `Anthropic` 12.11 → 12.21 (10-minor gap on streaming surface) and `Ical.Net` 5.2.1 → 5.2.2 (deprecates RecurrenceRules/RecurrencePattern — needs migration). |
| About page package sync | 2026-06-12 | 2026-07-12 | Monthly | — | Update `About.cshtml` package versions after NuGet updates. 2026-06-12: verified — all versions match `Directory.Packages.props`, no drift; contributors list updated (added Frank, Cupcake, Imanol). |
| Repo stats + README milestones refresh | 2026-06-12 | 2026-07-12 | Monthly | — | `generate-stats.sh` daily rows + hand-maintained header sections in `docs/development-stats.md`, `docs/reforge-history.csv`, and the README "Selected Milestones" table. 2026-06-12: header was a month stale (05-15); milestones extended Apr 1 → Jun 12. |
| GitHub issue triage | 2026-06-10 | 2026-06-17 | Weekly | — | Full schedule triage: 36 unscheduled → q2/q3, 5 stale closed, 4 partials re-scoped; sprint plan in `local/sprint-2026-06-10.md` |
| Access matrix verification | 2026-03-18 | 2026-03-25 | Weekly | — | Compare `AccessMatrixDefinitions.cs` against actual controller auth checks |
| Service ownership migration | 2026-04-15 | As needed | Per-section | — | Governance landed as first full end-to-end spike in PR #503. Profile is §15a Step 0 next. |
| User guide created | 2026-04-20 | — | One-time | — | `docs/guide/` with 14 section guides + README, GettingStarted, Glossary. Plan: `docs/superpowers/plans/2026-04-20-user-guide.md`. |
| Section refactor history snapshot | 2026-06-11 | Each swarm wave | Per-wave | — | See "Section Refactor History" table below. Re-snapshot scores (`reforge surface-score --format compact`) and update Last Lane rows whenever a refactor wave lands. |
| Screenshot review | 2026-04-20 | 2026-05-20 | Monthly | — | Review outstanding `TODO: screenshot` placeholders in `docs/guide/` and spot-check existing screenshots against the live UI at `nuc.home`. Process: `docs/architecture/screenshot-maintenance.md`. |
| Community calendar slice 1 | 2026-04-21 | — | One-time | — | New entities `CalendarEvent`, `CalendarEventException`. Added `Ical.Net` 5.2.1 (MIT) for RFC 5545 RRULE expansion. Plan: `docs/superpowers/plans/2026-04-21-community-calendar-slice1.md`. |
| xUnit v2 → v3 upgrade | 2026-04-24 | — | One-time | — | Bumped `xunit` 2.9.3 → `xunit.v3` 3.2.2 (keeps `xunit.runner.visualstudio` 3.1.5). Added `tests/xunit.runner.json` with `longRunningTestSeconds: 1`, `--blame-hang-timeout 2m` on CI, and a skipped `GlobalTimeoutDemoTest` to prove per-test `[Fact(Timeout = N)]`. Suppressed `xUnit1051` (CancellationToken-threading advisory; 1700+ pre-existing call sites — follow-up cleanup). See nobodies-collective/Humans#586. |
| HumansFact / HumansTheory introduced | 2026-04-25 | — | One-time | — | Project-wide ban on bare `[Fact]` / `[Theory]` via `BannedApiAnalyzers` (RS0030) + replacement attributes in `tests/Humans.Testing/` (5s default Timeout, infinite forbidden via setter validation). ReSharper sweep + per-test Timeout overrides on ~30 slow tests. CI grep step `Forbid RS0030 suppressions in test code` blocks pragma escape hatches. Default Timeout will be lowered over time as slow bits are cleaned up. |
| Agent section Phase 1 | 2026-04-21 | Phase 1.5 | Per-phase | — | Shipped: `AgentConversation`/`AgentMessage`/`AgentRateLimit`/`AgentSettings` entities; Sonnet 4.6 wrapper over official Anthropic SDK; widget + `/Agent/Ask` SSE endpoint + `/Admin/Agent/Settings`; Tier-1 preload active (8 sections + glossaries + access matrix + route map); widget visible to Admin role only in Phase 1 (broader rollout after Phase 2). Next: flip `AgentSettings.PreloadConfig = Tier2` once Anthropic org is promoted. |
| /section-align shifts | 2026-05-12 | Per-section | Per-section | sonnet+opus: ~6% | First section-align run on Shifts. Plan: `docs/plans/2026-05-12-section-align-shifts.md`. Findings: Shifts section is unusually clean (zero inbound/outbound cross-section DB access). Phase 1: VolunteerTrackingController route prefix realigned to `/Shifts/Dashboard/VolunteerTracking`; 7 service test files moved into canonical `tests/.../Services/Shifts/`. Phase 2: new `VolunteerTrackingArchitectureTests`; `[SurfaceBudget(N)]` declarations on `IShiftSignupService`/`IGeneralAvailabilityService`/`IVolunteerTrackingService`; 8 new invariant/trigger tests (singleton, medical gating, rota delete, rota move audit, coverage gap, dashboard mutex, sub-period narrowing, dev seeder gating); doc-drift fix on `Shifts.md` + `design-rules.md §8` (both omitted `VolunteerTrackingService` + `volunteer_build_statuses`; §8 also had `general_availabilities` plural typo + missing `rota_shift_tags`). Phase 3: Stryker mutation pass at `local/stryker-runs/shifts/`; `ShiftManagementService` has 482 surviving mutants worth a dedicated coverage follow-up; `ShiftSignupService` + `VolunteerTrackingService` 0-killed flagged as MTP-runner test-discovery bug. Zero follow-up /section-align targets. |
| `/section-align` — GoogleIntegration | 2026-05-12 | — | Per-section | — | Branch `align/google-integration`, PR #500. Consolidated DI into `GoogleIntegrationSectionExtensions`; moved controller-base helpers into `GoogleController`; regrouped ViewModels under `Models/Google/`; relocated service/repo tests into `GoogleIntegration/`; added `ITeamResourceService.GetResourceNamesByIdsAsync`; pinned invariant + architecture tests; trimmed `IGoogleSyncService` (20 → 16 methods). Three consumer-side gaps flagged for follow-up: AuditLog (nav strip), Teams (typed-FK), Users/Profiles (cache invalidation API). |
| §15i FullProfile landmark (issue #635) | 2026-05-04 | Drop-column follow-up | One-time | — | Full §15i landmark shipped in PR #403. Phase 2/3/4/5/7/11 (foundations) + Phase 8 (caller-log via `[CallerMemberName]`/`[CallerFilePath]` attributes — not stack-walk) + Phase 9 (reader migration: 12 `GetEffectiveEmail()` callsites replaced with `user.Email`; `user.UserEmails` reads in Google services + ProfileController + GoogleController routed through `IUserEmailRepository` / `IUserService.GetByIdsWithEmailsAsync` / `FullProfile.GoogleEmail`; `TryGetGoogleEmail` no longer traverses `tm.User.UserEmails`) + Phase 10 (six User-side cross-domain navs deleted: `Profile`, `RoleAssignments`, `ConsentRecords`, `Applications`, `TeamMemberships`, `CommunicationPreferences`; plus the `GetEffectiveEmail()` method; UserEmails kept for the User.Email override). Inverse-side EF configurations on each owning entity now own the schema-level FK constraints — verified non-destructive: a fresh `dotnet ef migrations add` produces an empty `Up()`/`Down()`. Arch test `User_HasNoCrossDomainNavigationProperties` enforces. Phase 6 alt: `LoggingUserStoreDecorator` shipped as an observability shim (WRN log on every `Identity.FindByEmailAsync` / `FindByNameAsync`); retired via issue #701 once soak data confirmed Identity does not internally call these. Migration is additive only (`AddColumn`). Drifts: (a) `User.UserEmails` nav stays despite spec's strip list — User.Email override depends on it per AC; (b) the spec's `HumansUserStore` is replaced by the Phase 6 alt logging shim per Peter's option-#1 choice; (c+d) `UserEmail.IsNotificationTarget` and `User.GoogleEmail` C# properties were already consolidated in prior PRs so the strip ACs are mooted. **Drop-column follow-ups still pending:** `Profile.IsSuspended`, `UserEmail.IsNotificationTarget`, promote `Profile.State` to NOT NULL — file after prod soak. |

## Section Refactor History

Tracks per-section surface-refactor lanes (refactor-swarm, Reforge-guided reductions, read-splits,
section-aligns) so targeting doesn't default to "biggest score wins" — the biggest sections have
also absorbed the most refactoring attention, and score-only ranking starves the small sections
indefinitely.

**Selection rule for new waves:** never-served sections first (current score descending); among
already-served sections, prioritize by score growth since the last lane (Score − Post-Lane Score —
a large delta means the section is re-accumulating debt fastest), tie-break by Last Lane ascending.
Skip sections with in-flight or imminently-planned feature work (check the active sprint plan).

**Maintaining the table:** each lane records its Post-Lane Score (the section's built
`reforge surface-score` after the final accepted commit) and Last Lane (date + PR) — the
refactor-swarm coordinator does this in one wave-end docs commit; /section-align and
/section-read-split update their own section's row in the PR. Each wave also re-snapshots the
Score column for all sections from `reforge surface-score --format compact` so deltas stay honest.

Scores below are the 2026-06-11 wave-end snapshot (solution combined: 59182, built at c698496bc
after the Events #967 merge; CityPlanning #970 still open, so its Score is pre-merge). Post-Lane
Score is seeded "—" for lanes that predate this table — no built post-lane scores were recorded
for the 2026-05-24→06-01 wave, so the 2026-06-11 snapshot is everyone's baseline.

| Section | Score | Post-Lane Score | Last Lane | What |
|---------|-------|-----------------|-----------|------|
| Users | 8480 | — | 2026-05-30 | #838 Reforge surface reduction; dead cross-section nav strip #920 (06-09); account-merge consolidation #899 (06-07) |
| Shifts | 5335 | — | 2026-05-30 | #820 service+repo surface refactor; ShiftRepository convergence #882 (06-04); /section-align 05-12 |
| Teams | 4385 | — | 2026-06-01 | #850 route consumers onto ITeamServiceRead + TeamInfo; read-split reference #678 |
| Camps | 3688 | — | 2026-05-29 | #822 cached read model + read surface |
| GoogleIntegration | 3586 | — | 2026-05-30 | #835 Reforge surface reduction; /section-align 05-12 (#500) |
| Tickets | 3293 | — | 2026-05-30 | #833 Reforge surface reduction; ticket read service #744 (05-25); buyer-fallback retirement #953 (06-11) |
| Events | 3218 | 3218 | 2026-06-11 | #967 refactor-swarm deep lane: dead-surface cleanup (GetPreferenceAsync + EventPreferenceInfo DTO, IsSubmissionOpenAsync, duplicate IUserServiceRead DI); boundary already clean — read-split done previously |
| Platform | 2227 | 2207 | 2026-06-11 | refactor-swarm deep lane closed at stasis, 0 commits — group is orchestrator-protected cross-cutting infra (jobs/seeders/cache plumbing); genuine debt (jobs injecting foreign repositories) is owned by other sections, needs cross-section lanes |
| Email | 2123 | — | 2026-05-30 | #837 Reforge surface reduction; IEmailService collapse to SendAsync(EmailMessage) #844 |
| (ungrouped) | 1870 | — | 2026-05-29 | #829 assigned ungrouped surface-score ownership |
| Budget | 1805 | — | 2026-05-30 | #836 Reforge surface reduction; ticketing-budget repo surface removal #815 (05-28) |
| Governance | 1634 | — | 2026-06-01 | #851 read/write split (IApplicationServiceRead + IMembershipCalculatorRead) + dead-surface trim |
| Expenses | 1680 | — | 2026-05-30 | #830 service surface refactor |
| Store | 1628 | — | — | never served |
| Agent | 1318 | — | 2026-05-31 | #849 dead-parameter drop (minor) |
| Consent | 1292 | — | 2026-06-01 | #854 duplicate-read collapse + dead consent-workflow surface deletion |
| Campaigns | 1233 | — | 2026-05-31 | #847 ICampaignServiceRead carve |
| Admin | 1226 | — | 2026-05-30 | #842 admin-nav realign (nav holder, not a section — lanes belong to the owning sections) |
| Auth | 1091 | — | — | never served (horizontal — lanes need extra care) |
| Notifications | 1012 | — | 2026-06-01 | #852 dead-surface deletion + emit-only consumer narrowing; badge-count caching move #954 (06-11) |
| Issues | 1011 | — | 2026-05-31 | #848 forwarding-overload collapse |
| CityPlanning | 975 | 906 | 2026-06-11 | #970 refactor-swarm deep lane: ICityPlanningServiceRead carve (4 consumers routed), CampPolygonSaveResult entity-leak fix, duplicated GeoJSON upload pipeline collapse, dead surface deletions (7 commits, −69) |
| Finance | 899 | — | — | never served |
| AuditLog | 876 | — | — | never served (horizontal — lanes need extra care) |
| Feedback | 873 | — | — | never served |
| Calendar | 769 | — | — | never served |
| Containers | 687 | — | — | never served |
| Dashboard | 451 | — | — | never served |
| Cantina | 304 | — | — | never served |
| Search | 132 | — | 2026-06-07 | #906 relevance-ranked, cache-only search rewrite |
| Gdpr | 81 | — | — | never served |
