# Freshness sweep — 2026-08-24

| | |
|---|---|
| Mode | diff |
| Previous anchor | `d237f3cc0` (2026-08-16) |
| New anchor | `81080d53e` (upstream/main, 2026-08-24) |
| Worktree base | `183a09fd2` (origin/main) |
| Changed files in window | 3196 |
| Mechanical entries dirty | 10 of 10 |
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

- `docs/features/global/section-activation.md` — "Shell pins **26 of the 42 shipped
  sections**". The seam lanes have been shrinking that set, so 26 is likely stale, but the
  number is not statically measurable with confidence: a `using`-directive scan of
  `src/Humans.Web` gives 5, a loose text scan gives 18 (it picks up prose in comments —
  it counts `Search` and `Tour`, which this very doc says Shell no longer names). Getting
  the real number needs the app's own reference scan. Left untouched.
- `docs/sections/_Index.md` — the per-section summary table was verified row-by-row against
  `src/Sections/*` (all 42 present and correctly located), but a full cell-by-cell
  regeneration of its content columns was out of scope against ~1989 matched files. Next
  sweep, or a dedicated pass.
- `docs/architecture/conventions.md` — the `fetch()`-exceptions table lists
  `Humans.Users/Views/Shared/Components/UserSearchResult/Default.cshtml` as a "Search
  results" exception, but that view contains no `fetch()`; it is a pure Razor result row.
  It may be deliberate as the documented page-pattern counterpart to `<vc:human-search>`
  per `memory/architecture/person-search.md`. Left in place.
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

**Not fixed, reported:** four `src/Humans.Web` C# files still name deleted projects in
comments — `AuthorizationPolicyExtensions.cs:98` (`Humans.Interfaces`),
`TicketVendorInfrastructureExtensions.cs:11` (`Humans.Application`),
`InfrastructureServiceCollectionExtensions.cs:53-59` (`Humans.Infrastructure`,
`Humans.Application`), `PersistenceServiceCollectionExtensions.cs:13-15`
(`Humans.Interfaces`, `Humans.Infrastructure`). Source comments are outside this sweep's
declared scope.

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
