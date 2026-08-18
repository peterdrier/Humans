# Freshness Sweep — 2026-08-18

| | |
|---|---|
| **Mode** | diff |
| **Previous anchor** | `caec503c8` |
| **New anchor** | `d237f3cc0` |
| **Window** | 50 commits / 1,890 files |
| **Worktree base** | `origin/main` @ `485a4714b` (frozen) |
| **Mechanical entries** | 11 dirty — 2 script-driven, 6 regenerated, 3 verified clean |
| **Editorial docs** | 143 dirty across 21 lanes |
| **Pruned** | 7 husks, −3,437 lines |
| **Verifier** | `diff-mode.sh` 7/7 — and now genuinely 7/7, see below |

The window is almost entirely the G5 section migration (nobodies-collective/Humans#866).
`Humans.Domain`, `Humans.Application`, `Humans.Infrastructure` and `Humans.UI` were **all
deleted as projects**; Users and Profiles merged into a new `Humans.Users`; Shifts, Camps,
GoogleIntegration, Stripe, EarlyEntry, Holded and Monitor moved into their own projects;
Email/Notifications/Issues Hangfire jobs moved into their sections' `Jobs/` folders.

---

## The finding: the verifier was reporting green while half-blind

Test 7 of `docs/scripts/freshness-checks/diff-mode.sh` — added last sweep as *"the only check
that can see this failure mode"* — was broken in two independent ways, and the two hid each
other.

**1. It only ever checked wildcard patterns.** The test expanded each trigger with
`matches=( $glob )` and failed when the array came back empty. That detects a dead `**`
pattern (via `nullglob`), but a **literal path with no wildcard word-splits to itself**, so the
array always holds exactly one element whether or not the file exists. Every literal trigger
passed unconditionally.

**2. It aborted partway through and printed nothing.** Under `set -o pipefail`, the trigger
extraction pipeline returns non-zero for any doc with no `freshness:triggers` marker, because
`grep` finds no lines. The first such file — `src/Sections/*/Docs/health.md`, a section-doctor
scorecard, new since 2026-08-16 — killed the whole script mid-loop. Every doc after it went
unchecked, and the test emitted no PASS *or* FAIL line at all. **Silence read as a pass.**

Both fixed: the check now branches on whether the trigger contains a wildcard and tests
`[ -e ]` for literals; the extraction is `|| true`-guarded; and `health.md` is excluded from the
editorial walk, matching the catalog's own `ignore` list. Test 7 now traverses all **143** docs
and fails loudly.

With it fixed, the true count was **316 dead triggers across 70 of 145 marked docs** — 38
globs and 278 literal paths. Nearly half the marked corpus. All repaired by resolving each
dead path's basename against the current file tree (unique match, then trailing-segment
disambiguation), never by hand-guessing a destination.

### Fourteen docs had *every* trigger dead and had stopped firing entirely

These were invisible to the sweep — not "unchecked", but silently reported as clean. Each got
a full content pass against current source rather than a diff-scoped one:

`features/camps/camps.md` · `guide/Camps.md` · `features/profiles/contact-fields.md` ·
`features/shifts/shift-signup-visibility.md` · `features/47-volunteer-tracking.md` ·
`features/shifts/department-coverage-pies.md` · `features/shifts/post-event-stats.md` ·
`features/shifts/shift-preference-wizard.md` · `features/shifts/workload-dashboard.md` ·
`features/email/email-flag-violations-remediation.md` ·
`features/profiles/burner-name-collision-warning.md` ·
`features/profiles/communication-preferences.md` · `features/profiles/profile-search-detail.md` ·
`guide/TwoStepVerification.md`

**The lesson, and it is the third variant of the same one:** *a dead glob makes a doc look
clean; a dead literal makes it look clean too; and a test that dies before printing makes the
whole corpus look clean.* Each round the sweep fixed the instance and missed the class. The
class here is **"the checker's own silence is not evidence."** Test 7 must fail loudly or print
a PASS line — it must never be able to exit having said nothing.

---

## Updated automatically

### Mechanical (11)

| id | outcome |
|---|---|
| `dev-stats` | script — 5 new day-rows (149 total); the reforge CSV was regenerated **first**, so all 5 days came from real snapshots instead of the regex fallback |
| `reforge-history` | script — 5 rows appended (138 days) |
| `authorization-inventory` | regenerated (+207/−195) |
| `controller-architecture-audit` | regenerated — 4 real drifts: undocumented `ShiftProfileController` (the `ShiftInfo` GET/POST pair moved off `ProfileController` at the G5 Shifts move), missing `ExpensesController.HoldedRetry`, `FeedbackController.Submit` documented but gone since #1185/#977, and 4 stale inline path comments |
| `dependency-graph` | regenerated — caught a gap no prior sweep did: the Guide section (`GuideRoleResolver → ITeamServiceRead`) landed in #1269 on 2026-08-12 and was never added; eager edges 290→291. It also re-asserted a `TeamService` fan-in of 29 over 28 real edges — an inherited off-by-one, caught by the self-verification pass below and corrected to 28 |
| `service-data-access-map` | regenerated (+279/−146) — 8 sections repointed off `Humans.Application`, Monitor split out as its own section, 3 undocumented services added (`GoogleSyncOutboxProcessor`, `NonCompliantMemberSuspension`, `AgentPreloadAugmentor`), 10 caching-decorator headers still labelled "(Singleton, Infrastructure)" corrected, TOC 43→44 |
| `guid-reservations` | 5 stale Source links repointed to `Humans.Interfaces/Constants` and `src/Sections/*/Data/Configurations` |
| `docs-readme-index` | regenerated **last**, after the husk deletions, so no row links at a deleted file |
| `data-model-index` | **verified clean** — every entity, owning section and DbContext mapping already correct, including the Finance/Holded split a prior sweep got wrong |
| `code-analysis-suppressions` | **verified clean** — auto-block matches `Directory.Build.props` + `tests/Directory.Build.props` exactly |
| `about-page-packages` | **verified clean** — no version drift; the window's one added package (`Newtonsoft.Json` 13.0.4) is a transitive security pin over Octokit's 11.0.1, not an ingredient card |

### Editorial — content catches worth naming

- **`Consent.md` said Flag deprovisions teams.** It claimed Flag "deprovisions then re-admits
  next sync". `OnboardingService.FlagConsentCheckAsync` only calls `RecordConsentCheckAsync` —
  it never calls `DeprovisionApprovalGatedSystemTeamsAsync`. Only `RejectSignupAsync` does.
  Flag is annotation-only. This is the *second consecutive sweep* to find Flag/Reject semantics
  wrong, in a different doc each time.
- **`magic-link-auth.md` documented a mechanism that never shipped.** Its Technical Design
  described Case-1 login as an ASP.NET Identity token with single-use enforced by
  `UserManager.UpdateSecurityStampAsync`. The real code (`MagicLinkUrlBuilder`,
  `MagicLinkService`) uses DataProtection tokens for both login and signup, with single-use
  enforced by an `IMagicLinkRateLimiter` replay-cache reservation. That security-stamp call
  exists nowhere in the codebase.
- **`gdpr-export.md` said right-to-erasure was out of scope.** It described Article 17 as
  "designed for (future `IUserDataEraser`)". Erasure is fully implemented via
  `IAccountDeletionService` / `ProcessAccountDeletionsJob`.
- **`administration.md`'s role-assignment section was flatly wrong.** It claimed Board manages
  only Board/ConsentCoordinator/VolunteerCoordinator. `RoleAssignmentAuthorizationHandler`
  grants Board **or** HumanAdmin the full 14-role `RoleNames.BoardManageableRoles` set. It also
  listed three `/Google/*` routes that no longer exist and filed three `/Monitor/*` rows under
  `GoogleController`.
- **`camps.md`'s season state machine had two wrong transitions.** Withdrawn seasons reactivate
  to **Pending**, not Active. And `Active → Full (lead marks full)` is dead —
  `SetSeasonFullAsync` was deleted as dead code in `b4710bf01` (2026-05-03); nothing sets a
  season Full today. 17 undocumented routes were also added.
- **`test-system-reliability.md` documented a CI filter that silently skipped tests.** The bare
  `FullyQualifiedName!~Integration` substring also excluded `GoogleIntegration` and `Monitor`
  tests; corrected to the now-qualified `!~Humans.Integration.Tests`.
- **`expires-on-deadline.md` listed a deadline that no longer exists.** `User.NormalizedEmail`'s
  `[ExpiresOn(2026-09-01)]` was dropped in G5 lane 3b (#1316) — the declaring leaf had to reach
  zero `ProjectReference`s, so only a plain `[Obsolete]` survived. Zero `[ExpiresOn]` attributes
  exist anywhere in the codebase now.
- **`_Index.md` carried a Profiles/Users split that no longer exists**, plus Identity table
  names (`AspNetUsers` et al.) that were **never** real — the actual `ToTable` names are
  `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `role_claims`, `user_tokens`.
  `event_participations` was also filed under Shifts, whose context has no such `DbSet`.
- **A recurring shape: `Jobs/` mislabelled as `Contracts/`.** Six section docs (Gate, Expenses,
  Finance, Holded, Issues, Surveys) said their Hangfire jobs live in `Contracts/`. They moved to
  `Jobs/` at G5 lane 5b-1 and #1353's HUM0034 carve-out. Several docs were copying a **stale
  source-code XML comment** — see Questions.
- **Read-split interface names drifted broadly**, as expected: `ITeamService`→`ITeamServiceRead`,
  `IUserService`→`IUserServiceRead`, `IShiftManagementService`→`IBurnSettingsService` or
  `IShiftManagementServiceRead`, `IMembershipCalculator`→`IMembershipCalculatorRead`,
  `INotificationService`→`INotificationEmitter`,
  `IApplicationDecisionService`→`IApplicationServiceRead`.
- **Three route-shape corrections**, the exact failure mode that bit an earlier sweep:
  `/Profile/{id}/Picture` → `/Profile/Picture?id={id}` (query param, not route segment),
  `/Human/{userId}` → `/Profile/{userId}`, and
  `/Profile/Me/CommunicationPreferences` → `.../Update` for the POST.
- **`EventsAdmin` and `CantinaAdmin` were absent from both auth role catalogs** despite being
  defined in `RoleNames.cs` and present in `BoardManageableRoles`. Rows added to `Auth.md` and
  `authentication.md`.
- **`google-integration.md`'s interface blocks were stale by 9 members.** `IGoogleSyncService`
  and `IGoogleGroupSync` were missing nine methods, `EnsureTeamGroupAsync`'s return type had
  changed to `GroupLinkResult`, and three documented members no longer exist. Rewritten from
  the real declarations.

---

## Pruned — 7 husks, −3,437 lines (7.36%, 169 over the 7% soft cap)

Both prune lanes verified every wheat candidate against source. **Zero migrations survived** —
not because nothing was mined, but because every durable claim was already stated, more
accurately and more currently, in a living doc or a code comment.

| husk | lines | why all chaff |
|---|---:|---|
| `plans/2026-06-06-expense-travel-and-iou-view.md` | 1,361 | `Expenses.md` has moved **past** it — line 17 records that the mileage/per-diem UI this plan built was later retracted ("travel lines can no longer be created"), so treating the plan as current would reintroduce stale claims. Its "Notes for the implementer" (`SurfaceBudget(7)` must stay 7) is itself false: there is no `[SurfaceBudget]` on `IExpenseReportServiceRead` at all today. |
| `plans/2026-06-09-ical-feed.md` | 1,410 | `Calendar.md` carries the fanout contract and the "never swallow a contributor failure" rationale verbatim, plus the anonymous-token/no-oracle-404 design and post-G5 ownership — none of which existed when the plan was written. The `BaseUrl` TODO(2027) rationale lives as a code comment on `CalendarFeedItem.cs:32-33`. |
| `specs/2026-05-25-analyzer-consolidation.md` | 131 | HUM0025–HUM0028 all shipped; the "universal enforcement over per-section" doctrine lives in `memory/architecture/universal-enforcement-over-per-section.md`. |
| `specs/2026-05-25-test-suite-reshaping-design.md` | 190 | **Proposal was rejected.** EF-InMemory via `ServiceTestHarness` remains current practice; the only Testcontainers ask since is one scoped debt-ledger item. No wheat survives a rejected premise. |
| `specs/2026-05-26-service-role-markers-design.md` | 105 | Shipped verbatim; taxonomy and the known post-ship gap both written up in `memory/architecture/orchestrator-marker.md`. |
| `specs/2026-06-04-datetime-format-analyzer-design.md` | 133 | Shipped as HUM0030; `conventions.md` and `roslyn-analysis.md` already carry the rule *and* its v1 gaps. |
| `specs/2026-06-05-e2e-auth-state-reuse-design.md` | 107 | Shipped verbatim into `tests/e2e/`; the rationale is self-documented in `auth.setup.ts`'s header. |

The two large plans additionally have in-project design records already
(`Humans.Expenses/Docs/2026-06-06-expense-travel-and-iou-view-design.md`,
`Humans.Calendar/Docs/2026-06-09-ical-feed-design.md`), moved there by #1295 — so the surviving
inbound references resolve to live files and needed no rewrite.

**Inbound refs:** one retargeted —
`memory/architecture/universal-enforcement-over-per-section.md` pointed at the deleted
analyzer-consolidation spec as its "Execution plan"; rewritten to cite the shipped
HUM0025–HUM0028 in `roslyn-analysis.md`. Zero dangling references remain repo-wide.

**Overage note:** 169 lines past the 7% reviewability budget. All seven husks were fully
analysed this sweep; deferring any would bin finished verification for no reviewability gain,
and stranding a mined husk is the deferral anti-pattern.

---

## Self-verification pass — 11 errors the sweep introduced, all fixed

An independent opus pass re-checked **163 added claims** against source (trigger-path lines
excluded — those are bulk-verified by the repo's own verifier). It confirmed **11 errors the
sweep itself wrote**, every one fixed in a follow-up commit on this branch. This pass has now
earned its keep two sweeps running and should stay standing.

Two of them were **the sweep contradicting itself** — the most valuable category, because no
single lane can see them:

1. `design-rules.md` listed `DriveActivityMonitorService` under Google Integration (twice: the
   §8 table row and the §15 migration list) while `service-data-access-map.md`, rewritten in
   the same sweep, correctly recorded it moving to the new Monitor section.
2. `service-data-access-map.md` put `SuspendNonCompliantMembersJob` under `Humans.Users/Contracts/`
   while `_Index.md`, also edited this sweep, had the correct `Humans.Users/Jobs/` path. Same
   `Jobs/`-vs-`Contracts/` confusion the editorial lanes were fixing elsewhere — it reappeared
   in a doc *written by* the sweep.

The rest:

3. `design-rules.md` called `HumanAdminOnlyHandler` "the only one left in the Shell";
   `CampComplianceAccessHandler` and `IsAnyTeamManagerOrCoordinatorHandler` sit beside it.
4. `service-data-access-map.md` said caching decorators are wired from
   `src/Humans.Web/Extensions/Sections/*.cs`; that folder holds only `AdminSectionExtensions`
   and `AuthSectionExtensions`, and neither registers one — registration is each section's
   `Section.cs`.
5. `dependency-graph.md` asserted a `TeamService` fan-in of 29 over 28 actual `--> Team` edges.
   The off-by-one was *inherited* (the prior 28 sat over 27 edges) but the sweep re-asserted it
   instead of counting. Corrected to 28. The 291-eager / 20-lazy / `linkStyle 291..310` figures
   in the same file all check out.
6. `controller-architecture-audit.md` placed three controller base classes in `Humans.UI` — a
   project this very sweep documents as deleted. They are in `Humans.Interfaces/Controllers/`
   and `Humans.Camps/Contracts/`. (The count of 94 controllers was right.)
7. `shift-signup-visibility.md` wrote `/Profile/Picture?id={userId}`; the `id` is a **profile**
   id — `HumanViewComponent` passes `profile.Id` and the endpoint takes `Guid profileId`. The
   sweep fixed this route's *shape* correctly and then got its *parameter* wrong.
8. `Users.md` cited `UserConfiguration.cs:27` twice for the `GoogleEmail` shadow property; it is
   at line 23.
9. `Expenses.md` called `IExpenseReportServiceRead` "the internal read surface
   (`[SurfaceBudget(8)]`)" — it is `public` in `Contracts/` and carries no `[SurfaceBudget]`,
   which the sweep's own `roslyn-analysis.md` lines state.
10. `test-system-reliability.md` claimed the bare `Integration` filter skipped
    `Humans.Monitor.Tests`; that assembly has no such substring. `build.yml`'s own comment says
    the match was on a test *method* name from an unrelated assembly.
11. `authorization-inventory.md` listed the "only authorization files left directly in
    `src/Humans.Web/`" and omitted `HttpCurrentUserContext.cs`,
    `HumansUserClaimsPrincipalFactory.cs` and `RoleAssignmentClaimsTransformation.cs`.

**The pattern worth carrying forward:** every one of these is in a doc a *generator* lane
rewrote wholesale, not in a doc a drift-fix lane edited surgically. Wholesale regeneration
re-asserts inherited numbers and path claims without re-deriving them. Counts and paths in
generated docs need to be *computed*, not carried.

One claim the pass flagged that turned out **correct** on inspection: `Gdpr.md`'s "20 section
projects reference `Humans.Gdpr.Contracts`" — 20 other sections do, and the sentence already
says "plus `Humans.Web`". Left as-is.

## Flagged for human review

None. Every concrete contradiction was verified and fixed inline, including the ones agents
raised as out-of-scope: the `/Profile/Picture` route wording, the two missing role rows, and
the stale `IGoogleSyncService` / `IGoogleGroupSync` interface blocks.

## Proposed for review

None — all prune candidates resolved this sweep.

## Questions

Delivered inline to Peter at the end of the run; see the PR conversation for answers and any
follow-up commit that applied them.

## Skipped (errors)

None.
