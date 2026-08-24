# Freshness sweep — 2026-08-24

| | |
|---|---|
| Mode | diff |
| Previous anchor | `d237f3cc0` (2026-08-16) |
| New anchor | `81080d53e` (upstream/main, 2026-08-24) |
| Worktree base | `183a09fd2` (origin/main) |
| Changed files in window | 3196 |
| Mechanical entries dirty | 9 of 9 |
| Editorial docs dirty | 144 of 144 marked |
| Unmarked editorial candidates | 43 |

`upstream/main` and `origin/main` had crossed at start (each one commit ahead of the
other — the usual post-promotion merge-vs-squash shape). Noted once, not reconciled;
both anchors were frozen at the start of the run per the skill.

An eight-day anchor gap meant 1960 changed files under `src/Sections/` alone, so every
marked editorial doc and every mechanical entry fired. Work ran in 16 editorial lanes
plus 7 mechanical workers.

## Updated automatically

**Mechanical**

- `dev-stats` — 6 new day rows through 2026-08-24, all 19 columns populated.
- `reforge-history` — 6 new snapshot rows through `183a09fd2`.
- `authorization-inventory` — 8 missing controllers and 2 missing handlers added to their
  owning per-section maps (Debug, Gdpr, Settings, Users, Shifts; Gdpr and Settings got new
  `authorization.md` files). Removed five stale rows from the global inventory claiming
  `WelcomeController`, `GuestController`, `ColorPaletteController`, `LogApiController` and
  `TicketsGateAdminController` as `Humans.Web` types — all had moved into sections.
- `service-data-access-map` — rollup plus 11 per-section maps (Agent, Containers, Email,
  Governance, MailerLite, Search, Settings, Shifts, Surveys, Tickets, Users). Fixed a Gate
  class mislabelled as Web infra (it lives in Tickets) and added Containers' new
  `ContainerImages` table. `Humans.SystemSettings` no longer exists — merged into
  `Humans.Settings` inside this window.
- `about-page-packages` — corrected the `Google.Apis.CloudIdentity.v1` version; removed the
  dead `Polly` card (unreferenced anywhere in the repo).
- `dependency-graph` — regenerated; the seven services the verifier could not account for
  were classified and placed (see "Verifier repairs").
- `docs-readme-index` — regenerated against the current doc set.
- `guid-reservations` — table already matched source; no doc change needed.
- `code-analysis-suppressions` — suppression list already current; no doc change needed.

**Editorial** — 144 docs reviewed claim-by-claim, roughly 45 edited. Highlights:

- The admin-shell refactor (#1385/#1390/#1393/#1406) replaced `AdminDashboardViewModel`,
  `AdminNavTree.cs` and the hard-coded Hangfire roll-call with per-section
  `ISectionAdminNav` / `ISectionAdminTiles` / `ISectionChrome` / `ISectionJobs`
  contributions. That drift ran through `docs/sections/admin-shell.md`,
  `docs/guide/Admin.md`, `docs/features/global/{active-user-metrics,administration,background-jobs}.md`,
  `Feedback`'s and `Issues`' feature docs, and `Debug.md`.
- `Users.md` and four Users feature docs: retired `FullProfile` cache → `UserInfo`;
  `Profile.ProfilePictureData` and `Profile.State` columns dropped; `MembershipStatus`
  moved from a computed `Profile` property to Governance's `IMembershipCalculator`.
- `Budget.md`: three separate false claims about `ITeamService` methods that do not exist
  or belong to Expenses; Budget computes coordinator scope itself.
- `AuditLog.md`: missing `StoreInvoiceIssued` action; `IAuditLogService` member count
  4, not 5; `audit-log.md`'s `IAuditViewerService` fan-out claim replaced with the real
  `IEntityNameContributor` mechanism.
- `Notifications`: `ConsentReviewNeeded` / `ApplicationSubmitted` marked retired;
  `IssueAssigned` reclassified Actionable; `MailerLite.md` gained the missing
  `DeleteSubscriberAsync` GDPR write and `MailerLiteGdprContributor`, and its "daily
  Hangfire job" corrected to opt-in/unscheduled-by-default.
- `Gdpr.md` (21→24 contributors), `Monitor.md` (SystemSettings→Settings), `Search.md`,
  `Stripe.md`, `Surveys.md`, `Finance.md`, `Expenses.md`, `Calendar.md`, `Tickets.md`,
  `Store-feature.md`, `Events.md`, `Consent.md`, `CityPlanning.md`, `Camps-feature.md`.
- End-user guides: `Admin.md`, `Camps.md`, `Events.md`, `Expenses.md`, `GoogleIntegration.md`,
  `Shifts.md`, `Tickets.md` — user-visible drift only.
- Architecture rules: `design-rules.md`, `conventions.md`, `roslyn-analysis.md` — 12
  contradicted facts (context count 28→29, `container_images`, three handlers moved out of
  the Shell, `AdminNavTree`/`NavBadgesViewComponent` gone, two seams now wired,
  `HumansMetricsService` resolution, `Profile` not in Contracts, two moved view paths,
  `SurfaceBudgetAttribute` path, section arch-test count 21→39). `code-review-rules.md`
  and `coding-rules.md` verified clean.

## Dead trigger globs

Phase 3.5 found 22 dead triggers across the marked corpus.

**Repaired (13)** — all the same cause: Users' domain types left `Humans.Users.Contracts`
for `src/Sections/Humans.Users/Domain/`.

| Doc | Old → new |
|---|---|
| `docs/guide/Profiles.md` | `…Users.Contracts/Profile.cs` → `…Users/Domain/Profile.cs` |
| `docs/guide/Profiles.md` | `…Users.Contracts/ProfileLanguage.cs` → `…Users/Domain/ProfileLanguage.cs` |
| `docs/guide/Profiles.md` | `…Users.Contracts/ContactField.cs` → `…Users/Domain/ContactField.cs` |
| `docs/guide/Profiles.md` | `…Users.Contracts/CommunicationPreference.cs` → `…Users/Domain/CommunicationPreference.cs` |
| `docs/guide/YourData.md` | `…Users.Contracts/ContactField.cs` → `…Users/Domain/ContactField.cs` |
| `docs/guide/YourData.md` | `…Users.Contracts/CommunicationPreference.cs` → `…Users/Domain/CommunicationPreference.cs` |
| `Governance/Docs/features/membership-tiers.md` | `…Users.Contracts/Profile.cs` → `…Users/Domain/Profile.cs` |
| `Notifications/Docs/features/notification-inbox.md` | `…Users.Contracts/CommunicationPreference.cs` → `…Users/Domain/CommunicationPreference.cs` |
| `Onboarding/Docs/features/onboarding-pipeline.md` | `…Users.Contracts/Profile.cs` → `…Users/Domain/Profile.cs` |
| `Users/Docs/features/communication-preferences.md` | `…Users.Contracts/CommunicationPreference.cs` → `…Users/Domain/CommunicationPreference.cs` |
| `Users/Docs/features/contact-fields.md` | `…Users.Contracts/ContactField.cs` → `…Users/Domain/ContactField.cs` |
| `Users/Docs/features/profile-pictures-birthdays.md` | `…Users.Contracts/Profile.cs` → `…Users/Domain/Profile.cs` |
| `Users/Docs/features/profiles.md` | `…Users.Contracts/Profile.cs` → `…Users/Domain/Profile.cs` |

**Unresolved at scan time (9)** — each force-dirtied its doc into this run's dirty list.
All nine were then resolved by the lane that owned the doc, which had the context the
scanner lacked:

| Doc | Dead trigger | Resolution |
|---|---|---|
| `docs/sections/admin-shell.md` | `src/Humans.Web/ViewComponents/AdminNavTree.cs` | retargeted — nav is now `AdminSidebarViewComponent` / `AdminNavComposition` |
| `docs/guide/Admin.md` | same | same |
| `Debug/Docs/Debug.md` | same | same |
| `docs/features/global/active-user-metrics.md` | `src/Humans.Web/Views/Shared/_DashboardStats.cshtml` | retargeted to the per-section stat-tile surface |
| `Feedback/Docs/features/feedback-system.md` | `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs` | retargeted to `AdminSidebarViewComponent.cs` |
| `Issues/Docs/features/issues-system.md` | same | retargeted to `IssuesUserMenuViewComponent` |
| `Auth/Docs/Auth.md` | `src/Humans.Web/Authorization/Requirements/**` | retargeted — Requirement classes now live per-section |
| `Email/Docs/features/email-outbox.md` | `…Humans.SystemSettings/Domain/SystemSetting.cs` | retargeted — SystemSettings merged into Settings |
| `Email/Docs/features/email-outbox.md` | `…Humans.SystemSettings.Contracts/SystemSettingKeys.cs` | same |

## Verifier repairs

The sweep owns its verifier (`memory/process/freshness-sweep-owns-its-verifier.md`), and
two of them were broken badly enough to have stopped checking anything:

- **`authorization-inventory.sh` was silently exiting 2.** It grepped
  `src/Humans.Web/Authorization/Requirements/`, a directory the G5 move emptied. `grep -r`
  over a missing directory exits 2, and under `set -o pipefail` that aborted the script
  before a single check printed — no PASS, no FAIL, just a silent non-zero. It now scans
  only directories that exist and PASSes (100 controllers + 13 handlers, 210 rows ≥ 129).
  This is the same failure shape the 2026-08-18 sweep recorded as "the checker's own
  silence is not evidence."
- **`guid-reservations.sh` scanned two deleted projects** (`src/Humans.Domain/Constants/`,
  `src/Humans.Infrastructure/Data/Configurations/`). Repointed at
  `src/Humans.Base/Constants/` + `src/Sections/*/Data/`.
- **`about-page-packages.sh` counted `Microsoft.CodeAnalysis.CSharp` as a production
  package.** It is referenced only by `src/Humans.Analyzers` (and its test project) — the
  analyzer toolchain, not something the running app is made of. Added to the ignore list
  with the reason recorded; now PASSes on all 32 production packages.
- **`docs-readme-index.sh` demanded a README row for a file the catalog ignores.** It
  excluded `SECTION-TEMPLATE.md` but not `G5-SECTION-TEMPLATE.md`, so `sections:` was
  permanently `src=3 doc=2 — MISSING 1` and the check could never pass however correct the
  README was.
- **`verify-triggers.sh` crashed on success.** `${#dirty_docs[@]}` on an associative array
  that never had a key assigned is an unbound-variable error under `set -u` in the Bash
  that Git Bash ships, so the *clean* case — zero unresolved triggers, the state every
  sweep works toward — died with `dirty_docs: unbound variable` and printed no SUMMARY.
  It only ever worked while something was broken. Confirmed: the corpus now scans clean
  (`repaired=0 unresolved=0 docs_forced_dirty=0`) and the script says so.

All nine mechanical verifiers PASS at the end of this sweep.

The catalog also gained an ordering note: `dev-stats` must run **after** `reforge-history`,
because `generate-stats.sh` reads `docs/reforge-history.csv` for its semantic
class/interface counts and silently regex-falls-back for days the CSV does not cover. This
run hit it (`regex-fallback=6 days`) and had to revert and re-run.

## Pruned

2264 lines removed against a 5% target of 1505 and a 7% reviewability cap of 2107. The
157-line overage is the "never strand a mined husk" exception: three husks were mined this
sweep and are deleted this sweep rather than left half-emptied.

**Wheat migrated**

| Source | Destination | What survived |
|---|---|---|
| `2026-05-14-userinfo-debug-and-venn-design.md` | `Humans.Users/Docs/Users.md` | the `/Users/Admin/Debug` route row — verified against `UsersAdminDebugController` (route `Users/Admin/Debug`, `AdminOnly`, `GetAllUserInfosAsync` snapshot) |
| `2026-05-27-coordinator-availability-on-profile-design.md` | `Humans.Shifts/Docs/features/47-volunteer-tracking.md` §Profile embed | the profile-hosting mechanism — verified against `VolunteerBuildStripViewComponent` and `Humans.Users/Views/Profile/Index.cshtml` |
| `2026-06-10-table-component-design.md` | `docs/architecture/conventions.md` §List Tables | the table-component pattern, now 77 views — verified against `TableModel` / `_Table.cshtml` in `Humans.Base` |

**Husks deleted**

| File | Lines | Why |
|---|---|---|
| `docs/plans/2026-06-13-q3-transition-plan.md` | 539 | all chaff — G0/G5 gate tracking for a split that is complete; its one wheat item (FK-cut carve-out) was already in `conventions.md` from an earlier sweep |
| `docs/superpowers/plans/2026-06-14-community-knowledge-base-agent.md` | 1088 | all chaff — fully built; `CommunityFaqReader`, `fetch_community_faq`, `AgentPreloadWarmupHostedService` all verified present |
| `docs/superpowers/specs/2026-05-14-userinfo-debug-and-venn-design.md` | 278 | mined above; the rest diverged from what shipped (`UserSetMembershipCalculator` is a 4-dimensional bitmask, not the spec's 3-set design) |
| `docs/superpowers/specs/2026-05-27-coordinator-availability-on-profile-design.md` | 205 | mined above; routes/data model/heatmap already documented |
| `docs/superpowers/specs/2026-06-10-table-component-design.md` | 154 | mined above; Phase 1/2 rollout plan and the currency-symbol debate are moot |

**Refs retargeted** — 9 inbound refs to `2026-06-13-q3-transition-plan.md` (from
`2026-08-07-fk-cut-inventory.md` ×2, `2026-08-03-demolition-inventory.md` ×2,
`2026-08-03-section-dependency-dag.md` ×2, `2026-08-03-proposed-frozen-section-inventory.md`,
`2026-08-03-g0-first-audit/Finance.md`, `2026-08-07-g5-section-project-split-design.md`).
Two pointed at live FK-cut rationale and now cite `conventions.md §Cross-Section FK Columns`;
the rest are archive-to-archive and were rewritten as historical. One dangling-ref sweep
confirmed nothing else points at any deleted file. `2026-06-09-team-early-entry`'s inbound
ref in `profile-search-detail.md` was left intact — that husk was not deleted this sweep.

**Not pruned, deliberately**

- `2026-06-14-rideshare-section-design.md` (271) — analysed as chaff by the never-built
  rule, but it is explicitly a *future* spec ("Design only — not scheduled for build.
  Targeted for Q4"). Deleting it would remove the only design doc for an intended build.
  Excluded; not a punt.
- `docs/architecture/tech-debt-2026-04-23.md` (227) — the allowlist admits it only when
  every item is `[DONE]`, and several are still open. `debt-ledger.yml:356` already tracks
  retiring it.
- `2026-06-06-account-merge-consolidation-design.md` (121),
  `2026-06-08-scanner-ticket-lookup-design.md` (94),
  `2026-06-09-team-early-entry-ticket-lookup-design.md` (174) — analysed, fully superseded
  by living docs, no wheat left to extract. Deferred to the next sweep on budget alone.

## Flagged for human review

- `src/Sections/Humans.Email/Docs/Email.md` — the Architecture section still counts
  `IEmailService`'s callers as "nine `Humans.Application` services, six
  `Humans.Infrastructure` jobs". Both projects were deleted in G5, so the sentence names
  homes that no longer exist and the counts cannot be trusted either. Pre-existing drift
  outside this window's changed files; needs a recount against `src/Sections/*`, not a
  find-and-replace of the project names.
- `src/Sections/Humans.Gate/Docs/features/Events-feature.md` — still written against the
  pre-G5 entity names (`GuideEvent`, `GuideCamp`, `GuideSharedVenue`,
  `UserGuidePreference`, `UserEventFavourite`, `ModerationAction`, `GuideSettings`) and a
  stale `/Admin/Guide*` route. The lane added a naming-note banner rather than rewriting
  ~200 lines of prose in a sweep. A rename pass over this one doc is its own piece of work.
- `docs/guide/Calendar.md` — an anonymous iCal feed exists and is undocumented.
  Pre-existing, outside this window's changed files.
- `docs/guide/CityPlanning.md` — container multi-image support landed in this window, but
  the guide does not cover container CRUD at all, so nothing is contradicted. Coverage gap,
  not drift.

## Fixed outside the editorial trees

- `src/Humans.Analyzers/AnalyzerReleases.Unshipped.md` — the HUM0030 row named the
  sanctioned home as `Humans.Application.Extensions.DateFormattingExtensions`; the
  analyzer's own `HomeTypeFullName` constant says `Humans.Base.Extensions.DateFormattingExtensions`.
  Fixed the notes to match the code.
- `src/Humans.Base/Resources/SharedResource*.resx` (6 files) — this sweep's own
  `about-page-packages` regen deleted the dead Polly card from `Views/About/Index.cshtml`,
  which was the only consumer of `About_Cat_Resilience`. Verified zero remaining references
  repo-wide and removed the now-orphaned key from all six locales. An orphan this sweep
  created, so this sweep cleans it up.

**Stale project names in `src/Humans.Web` comments** (Peter's call, 2026-08-24: fix them
here). Source comments are outside the sweep's declared scope, so these rode in only on an
explicit go-ahead. Each was repointed at where the type actually lives now, verified before
editing:

- `Authorization/AuthorizationPolicyExtensions.cs:98` — `Humans.Interfaces` → `Humans.Base`.
- `Extensions/Infrastructure/TicketVendorInfrastructureExtensions.cs:11` — the ticket-vendor
  port was said to live in `Humans.Application`; `ITicketVendorService` and
  `TicketVendorSettings` are both in `src/Sections/Humans.Tickets/Contracts/`.
- `Extensions/InfrastructureServiceCollectionExtensions.cs:53-54,59` — the badge-cache
  invalidators were called `Humans.Infrastructure` implementations of `Humans.Application`
  interfaces; all four impls are in `Humans.Base/Caching/MemoryCacheInvalidators.cs` and all
  four interfaces in `Humans.Base/Interfaces/Caching/`.
- `Extensions/PersistenceServiceCollectionExtensions.cs:13-15` — **left alone deliberately.**
  Both mentions are historical narrative that names the deleted projects correctly as past
  events ("came here at lane 5b-6, which deleted `Humans.Infrastructure`"), and line 13
  already glosses the rename as "Humans.Interfaces (Base)". Rewriting accurate history to
  use today's names would make it wrong.

## Resolved by Peter after the PR opened

- **`docs/sections/_Index.md` is deleted (145 lines).** Peter's call, and the evidence
  supported it. The three-column table (Section / Project / Invariants doc) was pure
  derivation — every section's doc is at `src/Sections/Humans.<Section>/Docs/<Section>.md`,
  so `ls src/Sections` is the same information. The two six-column tables (Controllers /
  Orchestrators / Services / Repositories / Tables) held several hundred hand-typed class
  names whose Services / Repositories / Tables columns duplicate the *generated* per-section
  `src/Sections/*/Docs/data-access.md` — the exact shape `no-derived-aggregates-in-docs`
  warns about.

  The copies had already drifted into contradicting their sources: the AuditLog row said the
  read path injects Users', Teams' and GoogleIntegration's read interfaces, while
  `AuditLog.md` records that nobodies-collective/Humans#1059 removed the Teams and
  GoogleIntegration references, leaving Users and Gdpr. `G5-SECTION-TEMPLATE.md` also
  carried a standing correction telling readers not to misread the Orchestrators column —
  a doc that needs a "do not misread me" note is a liability.

  Nothing was migrated out. All four "Notes & known drift" items already had better homes:
  `/Admin/*` is not a vertical section and `SystemDbContext` is not a section are both in
  `CLAUDE.md`; `event_participations` ownership is settled at `design-rules.md:264`; and the
  "§8 still lists `google_resources`/`TeamResourceService` under Teams" note was *itself*
  stale — `design-rules.md:285` already files both under Google Integration, explicitly
  noting "not Teams". Per Peter, a doc is not the proper home for a drift list anyway:
  drift gets fixed at its source or filed as an issue, and that stale §8 note is exactly
  what a "known drift" section decays into.

  Inbound references retargeted: `CLAUDE.md` (now states the derivable path and points at
  `data-access.md` for owner lookups), `docs/README.md` (row removed),
  `memory/architecture/governance-scope.md` + `memory/INDEX.md` (owner lookup is now a grep
  of `src/Sections/*/Docs/data-access.md`), `docs/sections/admin-shell.md`,
  `docs/architecture/freshness-catalog.yml`, and three places in
  `docs/sections/G5-SECTION-TEMPLATE.md` — whose Orchestrators-column warning was rewritten
  to keep its durable point ("is an orchestrator" is not "can't move"; an orchestrator may
  live in the section it orchestrates for) without the dead column. References inside
  `docs/superpowers/**` were left as the historical records they are. All nine mechanical
  verifiers still pass.

- **`docs/features/global/section-activation.md` — the "26 of the 42" count is gone.** Peter:
  counts in docs only burn update cycles, and `no-derived-aggregates-in-docs` (HARD RULE)
  already covers it. The paragraph now carries the judgment — Shell still pins a large
  minority, the seam lanes shrink it — and points readers at
  `SectionActivation.ShellDependencies`, which computes the set, instead of a number typed
  here. The doc's `flag-on-change` marker was updated to say the pinned set is described
  qualitatively and its count must never be written down, so a future sweep does not
  helpfully put a number back.
- **`docs/architecture/conventions.md` — the `UserSearchResult` row is removed.** Peter:
  search became component-based so each section renders its own result appropriately, which
  makes the row stale. Verified before deleting — the view has zero `fetch()` and zero
  `<script>`; it is pure Razor delegating to `<vc:human>`, so it never belonged in a table
  of "the following use `fetch()`".

## Unmarked editorial

43 docs sit inside the catalog's `editorial_trees` with no `freshness:triggers` marker, so
they never enter a dirty list. Reviewing them against all of `src/**` is noise; they need
triggers before a sweep can scope them. Unchanged from prior sweeps — a marking pass is its
own piece of work.

## Proposed for review

None — all prune candidates resolved this sweep.

## Questions

None asked of Peter mid-sweep. The judgment calls above were delivered inline at the end of
the run.

## Skipped (errors)

None. All 16 editorial lanes and 7 mechanical workers completed.
