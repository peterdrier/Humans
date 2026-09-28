# section-doctor — Development — 2026-09-28

- Invocation: scheduled unattended daily routine, no arguments; Phase 8 skipped per routine prompt
- Anchor commit: `c9115857d` (origin/main at branch point); branch `section-doctor/2026-09-28T011647Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

Development is a dev-only consumer section: it owns no tables, and its controllers and seeders act through other sections' interfaces. The code was sound; the prose around it was not. Comments and docs named a policy home, a guard test, a dependency list and a nav path that no longer exist, and the target in `Docs/health.md` had drifted from the tree. Two persona invariants (guest minted fresh per click, no-name legal names re-blanked) had no test. Items 1, 2, 3, 6 and 14 came from reading the target against the behaviour rather than from a reader thread. Independence check: pass.

## Findings

1. `Section.cs` remarks claimed every controller policy stays in Shell's `AuthorizationPolicyExtensions` (design §8); Finance, Camps and Shifts register the ones they own. Also narrows CENTRAL-47.
2. `_ViewImports.cshtml` claimed `DevelopmentArchitectureTests` guards against `IStringLocalizer`; no such test exists.
3. `Contracts/README.md` duplicated `Development.md`'s dependency table and had drifted from it.
4. The `Docs/health.md` target was stale: tree missing `Views/DevSeed/Index.cshtml` and `Docs/debt.yml`, a "Dev" group, a "merged this run" line, and invariants without enforcement cites.
5. `DevSeedControllerTests.cs` said the nav link reaches `/dev/seed` on Staging (the nav is `IsDevelopment()`-gated) and cited a moved `Shifts.md` line.
6. `DevLoginController.BuildPersonaList` and `DevPersonaSeeder.RoleNameFromSlug` each reflected over the `RoleNames` constants for one list both must agree on.
7. `DevelopmentDashboardSeeder` named its shift seeding dependencies after the full services, and its comments said `ITeamService` where the call is `ITeamSeeding`.
8. The guest persona's fresh-per-click and the no-name persona's re-blanked legal names were unpinned.
9. `Development.md`'s dashboard-fixture ordering bullet sat in Concepts; it is an invariant.
10. History narration and restating comments across the section's code, its doc and its tests.
11. The section csproj and tests csproj reasoning comments were stale (interfaces, leaf list, `Users.md` path, "both test classes").
12. Off-section docs misdescribed Development: `docs/seed-data.md`, `service-data-access-map.md`, and Budget's `IBudgetDemoSeeder` and `Section.cs` calling `/dev/seed/budget` Shell's.
13. CENTRAL-12 listed `DevelopmentDashboardSeeder` as carrying EF `EventSettings`; it uses `EventSettingsInfo`.
14. `SeedBudget` and `SeedCampRoles` redirect to `/Admin`, not back to `/dev/seed` where their buttons live.

## Debt verified

The section ledger `Docs/debt.yml` has no rows, so there was nothing to verify there. Central rows naming Development were checked by the Inbox thread: CENTRAL-47 still true for `Section.cs` (corrected and narrowed, item 1); CENTRAL-17 still true (the marker is the final state); CENTRAL-12 obsolete for Development (narrowed, item 13); CENTRAL-15 partly true, left unverified.

## Worked

- 10 — cut by a sonnet executor, verified by hand. The executor also cut a "now" in `Development.md`'s Gap #6 bullet and a "step 5's collapse case" phrase; both are the same class, kept.
- 1, 2, 3, 5, 11, 13 — corrected in one commit; CENTRAL-47 and CENTRAL-12 narrowed in `debt-ledger.yml`.
- 12 — off-section corrections to seed docs, the data-access map and Budget's comments.
- 7 — constructor parameters renamed to `shiftSeeding` / `shiftSignupSeeding`; comments now name `ITeamSeeding`.
- 9 — bullet moved to Invariants, citing `DevelopmentDashboardSeederTests`.
- 8 — `EnsureFreshGuestAsync_EachCall_MintsANewProfilelessUser` and `ResetLegalNamesAsync_BlanksLegalNamesAndKeepsBurnerName` added.
- 6 — `DevPersonaSeeder.RoleNameValues` read once; both callers use it. Reviewer (doctor-reviewer-light) approved after checking binding flags, filter and order equivalence, static init order, usings, blast radius and the admin double-lock.
- 4 — target regenerated with enforcement cites for every invariant; trace passes, with `consent_records.user_agent` hand-verified against `ConsentRecordConfiguration`.

## Skipped

- Store was blocked (nobodies-collective/Humans#1829); the selector passed over it.
- Splitting `DevelopmentDashboardSeeder.SeedAsync`: a linear dev fixture; a split buys little.
- Tests F5 (exclusion provider covered only by integration tests), F6 (no-localizer and writes-via-services unpinned by design), F7 (calendar invariant pinned as a side assertion): noted, no change.
- Freshness #6 (`ITeamServiceRead` wording): optional.
- `DevelopmentDashboardSeeder`'s `ISettingsService` dependency: Settings has no narrower read interface for `GetEventSettingsByIdAsync`.
- 14: UX behaviour, left for Peter.

## Retro

**What the selector got wrong.** Nothing; Development was due and small, and Store's block was honoured.

**Wasted motion.** The first reforge run came before a restore and failed; running it after the build would have saved a round.

**What striking revealed.** Commit 4 was pushed without the full-suite run the skill requires before each push. The later push ran the full build and test first. No live render of `/dev/login` or `/dev/seed` happened this run (2026-09-28); the view changes were comment-only.

**What the target diff says.** The previous target was stale rather than wrong in intent: a missing view and ledger file, a group name that no longer exists, and invariants with no enforcement cites. Adding cites made the trace check meaningful.

## Needs Peter

- [ ] 14 — redirect seed POSTs back to `/dev/seed` instead of `/Admin`?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Development/Contracts/README.md` | reviewed, changed |
| `src/Sections/Humans.Development/Controllers/DevLoginController.cs` | reviewed, changed |
| `src/Sections/Humans.Development/Controllers/DevSeedController.cs` | reviewed, changed |
| `src/Sections/Humans.Development/Docs/Development.md` | reviewed, changed |
| `src/Sections/Humans.Development/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.Development/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.Development/Docs/health.md` | reviewed, changed |
| `src/Sections/Humans.Development/Humans.Development.csproj` | reviewed, changed |
| `src/Sections/Humans.Development/Section.cs` | reviewed, changed |
| `src/Sections/Humans.Development/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.Development/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Development/Services/DevPersonaSeeder.cs` | reviewed, changed |
| `src/Sections/Humans.Development/Services/DevelopmentCampRoleSeeder.cs` | reviewed, changed |
| `src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs` | reviewed, changed |
| `src/Sections/Humans.Development/Views/DevLogin/Users.cshtml` | reviewed |
| `src/Sections/Humans.Development/Views/DevSeed/Index.cshtml` | reviewed |
| `src/Sections/Humans.Development/Views/Shared/_DevLoginPanel.cshtml` | reviewed |
| `src/Sections/Humans.Development/Views/_ViewImports.cshtml` | reviewed, changed |
| `tests/Humans.Development.Tests/DevLoginControllerTests.cs` | reviewed |
| `tests/Humans.Development.Tests/DevPersonaSeederTests.cs` | reviewed, changed |
| `tests/Humans.Development.Tests/DevSeedControllerTests.cs` | reviewed, changed |
| `tests/Humans.Development.Tests/DevelopmentArchitectureTests.cs` | reviewed |
| `tests/Humans.Development.Tests/DevelopmentDashboardSeederTests.cs` | reviewed |
| `tests/Humans.Development.Tests/Humans.Development.Tests.csproj` | reviewed, changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main |  | 3, 6, 7 |
| Behavior & bugs | main |  | 1, 2, 7, 14 |
| Freshness | subagent (`doctor-reader`) | opus low | 3, 4, 11, 12, 13 |
| Conformance | main, self-run against main; clean |  | none |
| Tests | subagent (`doctor-reader`) | opus low | 5, 8, 11 |
| Prose & surface | main, self-run; no InspectCode in this environment |  | none |
| History | subagent (`doctor-reader`) | opus low | 10 |
| Comments | subagent (`doctor-reader`) | opus low | 1, 10 |
| Inbox | subagent (`doctor-reader`) | opus low | 1, 13 |

