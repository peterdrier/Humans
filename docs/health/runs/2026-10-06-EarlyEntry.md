# section-doctor — EarlyEntry — 2026-10-06

- Invocation: unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `9fb4df7aa` (origin/main at branch point); branch `section-doctor/2026-10-06T011715Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1915

## Assessment summary

Second pass over EarlyEntry, selected on age plus churn since run 1: the event-settings move to Settings and the admin-nav rework both landed in between. The code held its shape — a provider fan-out, a Singleton decorator caching the per-person answer, a read-only admin roster. What drifted was the description of the change that landed: the [target](../../../src/Sections/Humans.EarlyEntry/Docs/health.md) named Settings as a caller of `IEarlyEntryInvalidator` and still listed settings-save eviction as an unbuilt seam, when Settings fans out over `IEventSettingsChangeListener` and the decorator evicts; the invariant doc left Settings out of the section's dependencies and still put the roster in the "Tickets" nav group. The last run's sweep queue never reached a ledger; its eviction gaps in Camps and Teams still hold and are now filed with their owners, beside new ones met this run.

Independence check: pass — findings 1, 2 and 14 come from the target (the settings-eviction spec-vs-reality delta, the nav-group shape, and `UserEarlyEntry` as a partial duplicate of the roster row).

## Findings

1. The target named Settings as an `IEarlyEntryInvalidator` caller and kept event-settings eviction as an unbuilt seam; it is built — Settings fans out over `IEventSettingsChangeListener` and `CachingEarlyEntryService.EventSettingsChanged` evicts everyone. Target regenerated in 3c.
2. `Docs/EarlyEntry.md` Routing and the target's weirdness list put the roster's nav entry in the "Tickets" admin group; `SectionAdminNav.cs` gives it its own "Early Entry" group.
3. `Docs/EarlyEntry.md` Cross-Section Dependencies and its Architecture block omitted the Settings reference the decorator's listener needs.
4. `Docs/data-access.md` omitted `IEventSettingsChangeListener`, `ICacheStats` and settings-save eviction from the decorator's description.
5. The `InvalidateAll` xmldoc omitted Teams' `EarlyEntryEnabled` flip and narrated a move; the Triggers section said a contributor calls `InvalidateAll` for settings edits, when the listener does.
6. The single-source "HasMultiple is false" test in `EarlyEntryServiceTests` is subsumed by `Roster_keeps_one_row_per_user` and `Same_source_label_twice_is_one_source_and_not_multiple`.
7. The Tests thread called `Index_SingleMultiSourceRow_ReturnsCorrectViewModel` a mock passthrough. Rejected on main: it is the only test pinning the controller's mapping of `HasMultiple` and `Sources` into the view model.
8. The ordinal (case-sensitive) half of the distinct-sources invariant had no test.
9. `EarlyEntryRosterControllerTests` substituted `IUserService` where the controller takes `IUserServiceRead`.
10. Dated provenance in `Docs/EarlyEntry.md` and the target (move date, ledger "added" dates, a "Last assessed" line).
11. A trailing comment in `CachingEarlyEntryService.GetForUserAsync` restated the class summary.
12. Contributor write paths that skip eviction: `CampService.DeleteCampAsync` and `TeamService.PermanentlyDeleteTeamAsync` (carried from run 1, never ledgered); `ShiftManagementService.DeleteEventAsync`; and team deactivation, which also leaves the team's grants live. Filed as CAMPS-7, TEAMS-9, TEAMS-10, SHIFTS-8.
13. Carried from run 1, never ledgered and still true: `docs/sections/SECTION-TEMPLATE.md` names projects that no longer exist, and the early-entry wheat marker in `docs/architecture/design-rules.md` cites a plan file not in the tree. New: `AdminLayoutRenderTests` labels name per-section `_ViewStart` files the admin-nav rework deleted. Filed as CENTRAL-78, CENTRAL-79 and CENTRAL-77, each rooted at CENTRAL-57.
14. `UserEarlyEntry` duplicates the roster row less `UserId` and `HasMultiple`; Peter ruled the fold on finding 11 of run 1 and it has not landed. Filed as EARLYENTRY-1.
15. Inbox: nobodies-collective/Humans#735 — edit. Its 2026-06-10 comment lists early-entry status on the volunteer profile badges as shipped; `_VolunteerProfileBadges.cshtml` shows none today. The remaining scope (roster filters, daily arrivals, an explicit shift flag) still holds. The Inbox thread's claim that `ShiftEarlyEntryProjection` no longer exists was wrong: it is in `src/Sections/Humans.Shifts/Services/ShiftEarlyEntryProjection.cs`.
16. Freshness triggers named `ShiftManagementService.cs`, which writes nothing early entry derives from any more, and missed the Settings listener and service.
17. Run 1's ruling on its finding 1 (wire `InvalidateAll` into the Shifts gate and build-offset writes) is moot: those writes moved to Settings, and the listener covers them.

## Debt verified

- The section ledger had no open rows at the branch point; nothing to verify. The cap did not stop the pass.

## Worked

- `doctor(earlyentry): docs describe the settings-listener eviction and the section's own nav group` — findings 1–5, 10, 11, 16. Doc and comment strikes ran through a sonnet executor, which added nothing beyond the listed findings. Main then corrected an xmldoc closing tag it had malformed and rewrote the Triggers bullet the executor had left alone.
- `doctor(earlyentry): ledger the eviction gaps and stale references found on the way` — findings 12, 13, 14.
- `doctor(earlyentry): controller test substitutes the read interface the controller takes` — finding 9.
- `doctor(earlyentry): pin ordinal source comparison in place of a subsumed test` — findings 6, 8. Reviewer: doctor-reviewer (opus high), APPROVE. It checked that every wrong `HasMultiple` rule it tried is still caught by the remaining tests, and that the new assertions match `EarlyEntryService.cs:34`. It also caught that the target cited line 33 for the ordinal rule; that cite is fixed. The new test fails under `StringComparer.OrdinalIgnoreCase` (mutation-checked on main).

## Skipped

- Finding 7 — rejected on main (see the finding).
- Finding 12 and 13 — other sections' and shared code; filed with their owners, not struck.
- Finding 14 — a public contract change across Gate, Scanner and Tickets; Peter's ruling has it land on its own.
- Finding 15 — existing issues are read-only to a run; recommendation only.
- Blocked at selection: Agent (peterdrier/Humans#1893), Tickets (peterdrier/Humans#1905).
- Phase 8 skipped (unattended routine). The run used its own `section-doctor/*` branch per the skill rather than the session's default branch.
- 2026-10-06: no live render; the roster view was not edited.

## Retro

**Selector.** EarlyEntry was a fair pick: little code churned, but the churn was the Settings move and the nav rework, and both had left the docs behind.

**Wasted motion.** The first reforge run raced the restore and refused a partial model; it only ran once the solution build had finished. The doc-strike commits were pushed after the section's test project passed but before the full solution suite ran; the full suite ran before the next push.

**What striking revealed.** The previous run's sweep queue had stayed in its run file and never reached a ledger, so its eviction gaps were still open a month later with nobody tracking them. Reading the delete paths for those turned up more (Shifts' event delete, Teams' deactivation). The Inbox thread said `ShiftEarlyEntryProjection` was gone; it was not, which is why absence claims get re-grepped on main.

**Target diff.** The earlier target was wrong rather than the section having moved: it described settings eviction as Settings calling the invalidator, and it kept the nav group the rework replaced. The new target lists the listener as a shape, gives every invariant an enforcing line, and drops the "never writes audit or notifications" bullet, which had no enforcement site.

## Needs Peter

None.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryInvalidator.cs` | changed |
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryProvider.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryService.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/EarlyEntry.md` | changed |
| `src/Sections/Humans.EarlyEntry/Docs/authorization.md` | reviewed |
| `src/Sections/Humans.EarlyEntry/Docs/data-access.md` | changed |
| `src/Sections/Humans.EarlyEntry/Docs/debt.yml` | changed |
| `src/Sections/Humans.EarlyEntry/Docs/health.md` | changed |
| `src/Sections/Humans.EarlyEntry/Humans.EarlyEntry.csproj` | reviewed |
| `src/Sections/Humans.EarlyEntry/Models/EarlyEntryRosterViewModel.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Section.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Services/CachingEarlyEntryService.cs` | changed |
| `src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs` | reviewed |
| `src/Sections/Humans.EarlyEntry/Views/EarlyEntryRoster/Index.cshtml` | reviewed |
| `src/Sections/Humans.EarlyEntry/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Controllers/EarlyEntryRosterControllerTests.cs` | changed |
| `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Humans.EarlyEntry.Tests.csproj` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Services/CachingEarlyEntryServiceTests.cs` | reviewed |
| `tests/Humans.EarlyEntry.Tests/Services/EarlyEntryServiceTests.cs` | changed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | — | 2, 14 |
| Behavior & bugs | main | — | 12, 13, 17 |
| Freshness | subagent (`doctor-reader`) | opus-low | 1, 2, 3, 4, 16 |
| Conformance | subagent (`general-purpose`), detectors on main | haiku | none: every detector clean for EarlyEntry |
| Tests | subagent (`doctor-reader`) | opus-low | 6, 7, 8, 9 |
| Prose & surface | same haiku subagent as Conformance | haiku | none: no resx, nav reachable through the admin layout; InspectCode not installed |
| History | subagent (`doctor-reader`) | opus-low | 10 |
| Comments | subagent (`doctor-reader`) | opus-low | 5, 11 |
| Inbox | subagent (`doctor-reader`) | opus-low | 13, 14, 15 — peterdrier/Humans: covered; nobodies-collective/Humans: covered |
