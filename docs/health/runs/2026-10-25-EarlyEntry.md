# section-doctor — EarlyEntry — 2026-10-25

- Invocation: scheduled unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `898cf4cf5` (origin/main at branch point); branch `section-doctor/2026-10-25T011606Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

Third pass, selected on age plus churn since the last run: peterdrier/Humans#2087 dropped the caching decorator and the invalidator in between, and the service now gathers its providers once per request scope. The code held a clean shape — a provider fan-out, a scoped orchestrator, a read-only admin roster — and no defect turned up. What drifted was the prose: the [target](../../../src/Sections/Humans.EarlyEntry/Docs/health.md) still said both reads ran live on every call and cited a test that no longer exists; `data-access.md` said the same; and the invariant doc and target both still called the `Contracts/` folder debt with a queued carve-out, after Peter closed CENTRAL-50 and design-rules §1 made the folder the house shape. The target was regenerated; the section moved, the earlier target was not wrong when written.

Routine prompt followed over the skill where they differ: Phase 8 skipped (unattended), and the PR targets `peterdrier/Humans` `main`. The open-PR list came from the REST API because GraphQL is closed to this session.

Independence check: pass — findings 1, 2 and 5 came from the target (the Contracts-folder spec-vs-reality delta, the once-per-request claim against its only stated caller, and the roster view's duplicated empty state), not from a tool.

## Findings

1. `Docs/EarlyEntry.md` Architecture and the target's seams called the `Contracts/` folder known debt with a queued carve-out in `debt-ledger.yml`; CENTRAL-50 was closed in the ledger review with Peter and design-rules §1 makes the folder the house shape.
2. `Docs/EarlyEntry.md` Invariants and the `GetForUserAsync_GathersOncePerScope_AndFreshInTheNext` comment justified the per-request gather with "the home page asks twice"; the home page reaches early entry once (`MemberTicketStatus` renders `MyTicketStubs`), and `TicketHoldings` renders in the profile and admin-detail sidebars, so no current page is shown to ask twice.
3. `Docs/data-access.md` said `GetRosterAsync` and `GetForUserAsync` both fan out on every call; they share one gather per request scope.
4. `Docs/EarlyEntry.md` Triggers and the decorator decision spoke of a fan-out per read rather than per request.
5. `Views/EarlyEntryRoster/Index.cshtml` set an empty message on the table builder inside the branch that only runs when rows exist; the outer branch already renders the empty state.
6. The roster test grouping one user's grants from two providers (removed this run) was a subset of `Roster_keeps_one_row_per_user` except its `HasMultiple` assertion.
7. The target was stale: a "live on every call, nothing cached" invariant citing a test that does not exist, three carried records where two exist, `file:line` cites moved by later commits, and one line narrating a prior state.
8. nobodies-collective/Humans#735 — edit: the body still names Camps + Shifts as the section, and the profile-badge criterion is not shipped as the June re-scope comment says.
9. nobodies-collective/Humans#799 — edit: its early-entry motivation describes a cache and invalidator that peterdrier/Humans#2087 deleted, and its cited roster spec is not in the tree.
10. peterdrier/Humans#1805 — keep: the early-entry FAQ cluster routes to Tickets/Shifts, while the roster now lives in EarlyEntry.
11. The "serves GET only" invariant has no pinning test; no write verb exists in the section.
12. `EarlyEntryRosterControllerTests.Index_SingleMultiSourceRow_ReturnsCorrectViewModel` mostly checks pass-through from the stub.

## Debt verified

`Docs/debt.yml` carries no rows (`inbox: []`); nothing to verify. The central ledger carries no open EarlyEntry row — its only mention is the closed-invalidator note under HUM0028.

## Worked

- Finding 7: target regenerated in 3c.
- Findings 1, 2, 3, 4: doc and comment strikes by a sonnet executor, every edit checked against the source on main; no additions beyond the listed findings.
- Finding 5: dead builder call deleted; razor-lint clean.
- Finding 6: test folded; `doctor-reviewer` (opus high) approved after checking each removed assertion is still pinned and the removed name has no other reference.
- Findings 8, 9, 10: issue comments posted on nobodies-collective/Humans#735, nobodies-collective/Humans#799 and peterdrier/Humans#1805.

## Skipped

- Finding 11: declined — no write verb exists, and a reflection pin adds a test for a defect nobody can introduce without a reviewer seeing a new `HttpPost`.
- Finding 12: declined — mapping the service row onto the view row is the controller's job, and the sort and legal-name tests do not assert `HasMultiple` or `Sources` mapping.
- Comments thread: the scoped-lifetime rationale in `Section.cs`, the service summary and the architecture test kept — each states a different why.
- Blocked this run: Cantina (open PR peterdrier/Humans#2152), Gdpr (open PR peterdrier/Humans#2148).

## Retro

**Selector.** Right call: the section changed under a debt sweep since its last run, and the sweep left the prose behind. Small sections with one structural change between runs are where doc drift concentrates.

**Wasted motion.** The reforge surface-score was launched before the first restore and refused on an incomplete semantic model; it belongs after the baseline test, not beside it. GitHub search is closed to this session, so the issue dump was built by listing open issues per repo and filtering locally.

**What striking revealed.** Finding 2 surfaced only when checking the test comment's claim by hand: the per-request gather's stated reason no longer matches any caller. Whether the gather itself still earns its place is Peter's call.

**Target diff.** The section moved: the decorator and invalidator went in peterdrier/Humans#2087 and the gather became per request. The Contracts-folder seam turned wrong when the ledger review closed CENTRAL-50, after the previous run.

## Needs Peter

- [ ] 2 — keep the once-per-request gather (`_gathered`) though no page now asks twice, or drop it?

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryProvider.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryService.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/EarlyEntry.md` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/data-access.md` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/health.md` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Humans.EarlyEntry.csproj` | reviewed |
| `src/Sections/Humans.EarlyEntry/Models/EarlyEntryRosterViewModel.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Section.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Views/EarlyEntryRoster/Index.cshtml` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Controllers/EarlyEntryRosterControllerTests.cs` | reviewed |
| `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Humans.EarlyEntry.Tests.csproj` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Services/EarlyEntryServiceTests.cs` | reviewed, changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | — | 1, 2, 5, 7 |
| Behavior & bugs | main | — | none — flows, provider registrations and auth read by hand |
| Controller intent | subagent (`doctor-reader`) | opus | none |
| Freshness | subagent (`doctor-reader`) | opus | 1, 3, 4 |
| Conformance | subagent (`general-purpose`) | haiku | none (detectors clean for this section) |
| Tests | subagent (`doctor-reader`) | opus | 6, 11, 12 |
| Prose & surface | self-run on main | — | none — no resx (admin-exempt), nav entry in `SectionAdminNav`; InspectCode absent from the environment; reforge surface-score refused (started before restore) |
| History | subagent (`doctor-reader`), same dispatch as Comments | opus | 7 |
| Comments | subagent (`doctor-reader`), same dispatch as History | opus | none kept |
| Inbox | subagent (`doctor-reader`); peterdrier/Humans: covered; nobodies-collective/Humans: covered | opus | 8, 9, 10 |

