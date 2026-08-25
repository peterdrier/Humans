# Maintenance Log

Tracks when recurring maintenance processes were last run.

**Notes are one line: current state + links.** Run narrative belongs in the run's PR body (or the
process's own per-run file, e.g. `docs/health/runs/`) — never in this table. Completed one-time
work doesn't get a row; PRs and git history record it. Rows here exist so the next session knows
what's overdue.

| Process | Last Run | Next Due | Cadence | Est. Cost | Notes |
|---------|----------|----------|---------|-----------|-------|
| NuGet vulnerability check | 2026-07-30 | 2026-08-06 | Weekly | — | `dotnet list package --vulnerable`. 2026-07-30: 8 advisories fixed after a 4-week gap that turned CI red repo-wide. |
| Freshness sweep (diff) | 2026-08-25 | 2026-08-26 | Daily | — | `/freshness-sweep` — report at `docs/freshness/last-report.md`; narrative lives in each sweep's PR body. 2026-08-25: 43 of 83 "dirty" editorial docs fired only on sibling `.md` changes under `src/Sections/Humans.X/**` — the previous sweep's own doc edits re-dirtying them; skipped as no-code-drift. Added `src/Sections/*/Docs/authorization.md` to the catalog `ignore:` list (mechanical output, same as `data-access.md`), cutting the "Unmarked editorial" flag list from 45 to 5. |
| Section doctor | 2026-08-17 | — | 1–2×/day | — | `/section-doctor` — frozen row (nobodies-collective/Humans#1069); each run writes `docs/health/runs/<date>-<Section>.md`. |
| Debt sweep | 2026-06-14 | 2026-06-15 | Daily | — | `/debt-sweep` — ledger `docs/architecture/debt-ledger.yml`, report `docs/debt/last-report.md`. Last: PR #1010. |
| Freshness sweep (full) | — | — | Weekly | — | `/freshness-sweep --full` — full regeneration of every catalog entry. First run pending. |
| Code simplification | 2026-06-11 | — | After features | codex: ~5% | Per-section pass: #969–#980. |
| ReSharper InspectCode | 2026-06-10 | 2026-06-17 | Weekly | — | `/resharper` — fix Tier 1+2 warnings. Last: #928. |
| Context cleanup | 2026-03-18 | 2026-04-18 | Monthly | — | CLAUDE.md, .claude/, memory/ |
| Feature spec sync | 2026-04-05 | 2026-05-05 | Monthly | — | section `Docs/features/` specs + docs/features/global/ vs implementation |
| i18n audit | 2026-02-24 | 2026-03-24 | Monthly | gemini: ~2% | Missing translations |
| Navigation audit | 2026-03-22 | 2026-04-22 | Monthly | — | `/nav-audit` — discoverability, backlinks |
| GDPR audit | — | — | Quarterly | — | Exports, consent, PII logging |
| Migration squash check | 2026-02-24 | 2026-03-24 | Monthly | — | Check `/Debug/DbVersion` on prod, QA (humans.n.burn.camp), and local dev. Oldest `lastApplied` across all three is the safe squash boundary. |
| Architecture ratchet sweep (`arch-sweep`) | 2026-06-24 | 2026-07-08 | Bi-weekly | — | `dotnet test --filter "FullyQualifiedName~Architecture.Rules"`; baselines at `tests/Humans.Application.Tests/Architecture/`. 2026-06-24: all green, 0 regressions. |
| NuGet full update | 2026-06-24 | 2026-07-24 | Monthly | — | Non-security package updates. Last: 21-package LOW-risk batch (`chore/maintenance-2026-06-24`). |
| About page package sync | 2026-06-27 | 2026-07-27 | Monthly | — | Sync `Views/About/Index.cshtml` versions to `Directory.Packages.props` after NuGet updates. Last: #1049. |
| Repo stats + README milestones refresh | 2026-06-12 | 2026-07-12 | Monthly | — | `generate-stats.sh` rows + hand-maintained headers in `docs/development-stats.md`, `docs/reforge-history.csv`, README milestones. |
| GitHub issue triage | 2026-06-10 | 2026-06-17 | Weekly | — | Last: 36 unscheduled → q2/q3; plan `local/sprint-2026-06-10.md`. |
| App triage (`/triage`) | 2026-07-13 | 2026-07-20 | Weekly | — | Logs + agent + in-app issues (prod). Last findings → #926–#931; in-app execution deferred by Peter. |
| Access matrix verification | 2026-06-13 | 2026-06-20 | Weekly | — | Compare `AccessMatrixDefinitions.cs` vs controller auth. Open P2: `TeamAdminController` action-level auth unverified (#832). |
| Service ownership migration | 2026-04-15 | As needed | Per-section | — | Governance landed as first full end-to-end spike in PR #503. |
| Agent section Phase 1 | 2026-04-21 | Phase 1.5 | Per-phase | — | Next: flip `AgentSettings.PreloadConfig = Tier2` once the Anthropic org is promoted. |
| §15i FullProfile landmark (issue #635) | 2026-05-04 | Drop-column follow-up | One-time | — | Shipped in PR #403. Pending after prod soak: drop `Profile.IsSuspended`, `UserEmail.IsNotificationTarget`; promote `Profile.State` to NOT NULL. |
| Section refactor history snapshot | 2026-06-11 | Each swarm wave | Per-wave | — | See "Section Refactor History" table below; re-snapshot scores each wave. |
| Screenshot review | 2026-04-20 | 2026-05-20 | Monthly | — | Review `TODO: screenshot` placeholders in `docs/guide/`; process: `docs/architecture/screenshot-maintenance.md`. |

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
