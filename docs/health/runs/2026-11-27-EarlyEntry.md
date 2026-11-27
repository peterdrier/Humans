# section-doctor — EarlyEntry — 2026-11-27

- Invocation: unattended (scheduled daily run via project coordinator)
- Anchor commit: `b01f4012f` (origin/main at branch point); branch `section-doctor/2026-11-27T060926Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#2661

## Assessment summary

A re-doctor from `BASE` `48d8aeaef`. Since the last run the roster moved off the Shifts policy onto the section's own `EarlyEntryPolicies.Roster` (Admin, Board, Early Entry Admin) with its own role, the admin nav moved into `Section.cs`, and the test names were shortened. The code is small and sound: no behavior defect was found. The drift was all in prose: the target still described the Shifts audience and policy, the section doc missed a caller, freshness triggers pointed at a deleted file, and a handful of comments and test names said something the code no longer does.

The target moved, and the earlier one was also wrong: its roster invariant cited `ShiftsPolicies.Manage` and a test that no longer exists, and its scoped-registration cite had drifted to a different line. Regenerated: section 1 now names the admins who reach the roster, section 2 adds the my-tickets page as a caller, section 3 adds the registration, section 4 cites the section's own policy, and the load-bearing list records that `HasMultiple` stays on the shared row by Peter's 2026-09-05 ruling.

Independence check: pass. Findings 2 and 3 came from the target: the shapes table names the my-tickets page as a `GetForUserAsync` caller the section doc omits, and the structure line "a roster row that answers both reads" is what exposed `HasMultiple` as a roster-only fact on the shared row.

- 2026-11-27: unattended cloud run; no live render (no database), the preview deploy is where the page is checked. The changed view-side file is a Razor comment only.
- 2026-11-27: the run worked on branch `section-doctor/2026-11-27T060926Z` as this skill names it, not the session's default `claude/` branch, because the selector reads blocked sections from `section-doctor/` branch names.

## Findings

1. `Docs/health.md` sections 1 and 4 named the old roster audience (a volunteer coordinator; `ShiftsPolicies.Manage` and a pin test that no longer exists) and cited the wrong `Section.cs` line for the scoped registration.
2. `Docs/EarlyEntry.md` omitted `TicketBuyController` (the my-tickets page) as a `GetForUserAsync` caller, in Inbound and in Actors.
3. `EarlyEntryRosterRow.HasMultiple` is `Sources.Count > 1` and only the roster view reads it. Settled: Peter kept the field on 2026-09-05 (that run's finding 11). Recorded in the target's load-bearing weirdness; not struck.
4. Test names said the wrong thing: `GetForUserAsync_LabelsDifferByCase_AreTwoSources` exercises `GetRosterAsync` only; `Roster_KeepsOneRowPerUser` named no method; `Index_PopulatesLegalNameFromProfile` said Profile where the legal name is on the users row; `Index_SingleMultiSourceRow_ReturnsCorrectViewModel` and `Index_SortsByEntryDateThenUserId` named no rule or no condition.
5. `Views/_ViewImports.cshtml` called `EarlyEntryPageRenderTests` local-only; the integration suite is a PR check.
6. `EarlyEntryPageRenderTests.A_volunteer_cannot_reach_the_roster` said the roster policy is registered by Shifts; EarlyEntry's `Section` registers it.
7. `Docs/EarlyEntry.md` freshness triggers listed the deleted `TeamEarlyEntryProjection.cs`, listed `src/Sections/Humans.Shifts/Section.cs` twice, and missed `TicketBuyController.cs`, `ICampManagement.cs`, the section's tests and the render test.
8. History narration: `EarlyEntryRoles.cs` dated the `EETeamAdmin` rename (the `FormerNames` entry in `Section.cs` carries it); `Docs/EarlyEntry.md` said Shifts Admin "no longer" reaches the roster.
9. `Humans.EarlyEntry.csproj` carried comments restating the next line (`AddRazorSupportForMvc`, the `None Include` for docs).
10. Off-section: `AdminLayoutRenderTests.AdminPages` site labels name per-section `_ViewStart` files, EarlyEntry's among them, that no longer exist.
11. Conformance (section-file-layout, detector 1b): `EarlyEntryPolicies.cs` sits loose at the project root. Advisory under the rule's notes.
12. `EarlyEntryService.cs` and `EarlyEntryPolicies.cs` summaries repeat what the interface and the policy registration say.
13. The GET-only invariant has no pin test.
14. `GetForUserAsync_SameLabelTwice_IsOneSource` asserts both reads.
15. `Docs/EarlyEntry.md` is long for a section this small (Prose & surface thread).
16. Carry-forward from the 2026-10-25 run (its finding 2): whether to keep the once-per-request gather. Re-grepped: `_gathered` is still at `EarlyEntryService.cs:31`. Still open there; not re-asked.

## Worked

- Finding 1: target regenerated (`doctor(earlyentry): regenerate the target; roster audience and policy match the code`).
- Findings 2, 5, 6, 7, 8, 9: one sonnet strike executor; main verified the diff line by line against the source; no additions beyond the listed findings (`doctor(earlyentry): section doc names every caller; comments match the code`).
- Finding 4: renamed on main, mechanical, `doctor.py blast` showed no reference outside the test files and old run files: `GetRosterAsync_LabelsDifferByCase_AreTwoSources`, `GetRosterAsync_TwoProviders_OneRowPerUser`, `Index_KnownUser_ShowsLegalName`, `Index_MultiSourceRow_KeepsSourceDetail`, `Index_MixedDates_SortsByDateThenUser`. The build's naming check refused a first name containing "And"; renamed again.
- Finding 3: load-bearing weirdness line in the target.
- Finding 10: ledgered as CENTRAL-115 in `docs/architecture/debt-ledger.yml`.
- No reviewer gate ran: every strike is a doc, comment or test-name change.
- No issue comments: no open section issue in a covered repo.

## Skipped

- Finding 3: Peter's standing ruling; the field stays.
- Finding 11: advisory; the layout rule's notes say a loose root file is reported, never moved.
- Finding 12: declined; each summary is accurate and states its file's own contract.
- Finding 13: declined, as last run: no write verb exists, and a new `HttpPost` is visible in review.
- Finding 14: declined; the test pins one rule (a repeated label is one source) across both reads.
- Finding 15: declined; the doc follows `docs/sections/SECTION-TEMPLATE.md` and every template line stays.
- Finding 16: carried, not re-asked (unattended runs never re-ask).
- Blocked this run: Search (pushed branch `section-doctor/2026-11-26T181006Z`).

## Debt verified

- No rows: `Docs/debt.yml` has an empty inbox. Nothing to verify; the cap was not reached.

## Retro

**Selector.** Right call. A small section whose authorization moved between runs is where target prose rots fastest: the code change landed with tests, the derived prose did not follow.

**Wasted motion.** The reforge surface-score was launched before a full restore and refused on an incomplete semantic model, exactly as the previous run's retro recorded; it belongs after `dotnet restore Humans.slnx`. The Prose & surface thread re-reported trigger drift the Freshness thread owned and flagged the regenerated target's triggers as stale when they were not.

**What striking revealed.** The `HasMultiple` collapse looked like the run's one structural strike until the blast grep surfaced Peter's 2026-09-05 ruling in an old run file. Settled decisions belong in the target's load-bearing list, where the next run reads them first; it is there now.

**Target diff.** Both: the section moved (own policy and role, nav folded into `Section.cs`) and the earlier target was already wrong about the policy line it cited.

## Needs Peter

- None this run. Finding 16 stays open in the 2026-10-25 run file. Section review posted on the PR.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.EarlyEntry/Contracts/EarlyEntryRoles.cs` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryProvider.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryService.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/EarlyEntry.md` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/debt.yml` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/health.md` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/EarlyEntryPolicies.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Humans.EarlyEntry.csproj` | reviewed, changed |
| `src/Sections/Humans.EarlyEntry/Models/EarlyEntryRosterViewModel.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Section.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Views/EarlyEntryRoster/Index.cshtml` | reviewed |
| `src/Sections/Humans.EarlyEntry/Views/_ViewImports.cshtml` | reviewed, changed |
| `tests/Humans.EarlyEntry.Tests/Controllers/EarlyEntryRosterControllerTests.cs` | reviewed, changed |
| `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Humans.EarlyEntry.Tests.csproj` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Services/EarlyEntryServiceTests.cs` | reviewed, changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | main session | 1, 3, 10 |
| Behavior & bugs | main | main session | none: each flow walked against the invariants, auth read by hand |
| Controller intent | subagent (`doctor-reader`) | opus low | none |
| Freshness | subagent (`doctor-reader`) | opus low | 2, 7, 8 |
| Conformance | self-run on main: the detector output was one line | main session | 11 |
| Tests | subagent (`doctor-reader`) | opus low | 4, 6, 13, 14 |
| Prose & surface | subagent (`general-purpose`) | haiku | 7, 15 |
| History | subagent (`doctor-reader`) | opus low | 8 |
| Comments | subagent (`doctor-reader`) | opus low | 5, 8, 9, 12 |
| Inbox | self-run on main: no issue or ledger row to review | main session | 16 |

- Inbox: peterdrier/Humans: covered (no open section issues). nobodies-collective/Humans: not covered: the probe read of a known issue returned HTTP 403.
- Prose & surface: InspectCode not run (not installed); resx checks not run (the section has no resx).
