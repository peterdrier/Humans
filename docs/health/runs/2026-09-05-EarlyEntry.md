# EarlyEntry — section doctor, 2026-09-05

- **Invocation:** unattended daily run, no arguments. Phase 8 (inline round) skipped.
- **Anchor commit:** `10199a23` (`origin/main`)
- **Branch:** `section-doctor/2026-09-05T061605Z` (cloud run, repo root — no worktree)
- **Budget:** 2.5h, single PR.
- **PR:** peterdrier/Humans#1593

## Assessment summary

First doctor pass over EarlyEntry, a tableless read-side aggregator (reforge 54, loc=245,
cogP95=1, cogMax=1 — the smallest surface in the never-doctored tier): three contracts in a
`Contracts/` folder, one orchestrator, one Singleton caching decorator, one admin roster page.
The target shape ([`health.md`](../../../src/Sections/Humans.EarlyEntry/Docs/health.md),
written this run before any scan) finds the structure right as built. Two small deltas from the
fresh form: the per-person collapse was written twice in the orchestrator (finding 10, struck),
and `HasMultiple` travels as a field when it is `Sources.Count > 1` (finding 11, Peter's call).

One behavior delta, stated-but-unbuilt: the section's contract says Shifts' gate-date and
build-offset edits evict every cached answer, and no Shifts write path has ever called
`InvalidateAll` (finding 1). Doc and code disagree and the code looks wrong, so neither was
changed. Everything else was sediment of one recognisable kind: comments and docs written at
the section's move out of the Shell in August — design-rules cites to sections that do not
say what they are cited for, project names that no longer exist, a pin on a test that was
never written, and "shared DbContext, not thread-safe" as the reason for a sequential fan-out
that is a simplicity choice (findings 2, 3, 7, 8). Six invariant tests were missing, including
a CI-reachable pin on the roster's policy (finding 5).

## Ranked findings

Value = bug surface removed, then concepts removed, then words removed.

| # | Finding | Value | Disposition |
|---|---|---|---|
| 1 | **Shifts' EventSettings gate / build-offset edits never evict.** `EarlyEntry.md` Triggers and the `IEarlyEntryInvalidator.InvalidateAll` xmldoc say they call `InvalidateAll`; `ShiftManagementService.CreateAsync`/`UpdateAsync` call only `viewInvalidator.InvalidateAll()`, and `git log -S` shows no commit ever wired the early-entry one. A shift-derived date can stay stale until the person's next signup change or a restart. Doc/code disagreement with the code looking wrong → both left as they are. | high | **Needs Peter** + sweep queue (Shifts) |
| 2 | **Phantom pin.** `EarlyEntry.md` and `_ViewImports.cshtml` cited `EarlyEntryArchitectureTests.SectionTypesTakeNoStringLocalizer`; the file has never held it. Claim dropped, test not written ([`no-tests-for-absences`](../../../memory/architecture/no-tests-for-absences.md)). | med | **worked** |
| 3 | **Obsolete sequential-fan-out rationale** in `EarlyEntryService.cs` and `EarlyEntry.md` ("providers share the scoped section DbContexts, not thread-safe (same reason GdprService is sequential)") — each provider reads through its own section, and `GdprService.cs` itself says simplicity. Corrected in one home; the section `debt.yml` entry tracking it (2026-08-27, from the Gdpr run) retired. | med | **worked** |
| 4 | **Roster intro copy omitted Teams** ("from camps and build shifts"); doc Routing said the page is reached from the shift-dashboard nav (the entry is in the Tickets admin group). Both fixed. | low | **worked** |
| 5 | **Test gaps from the invariant matrix:** same label from two providers collapses to one reason and is not "multiple"; reasons keep provider order (`Contain` → `Equal`); one row per person across people; `InvalidateAll` had zero coverage; controller sort date → user id; the roster policy was pinned only by the local-only integration test — a reflection pin now runs in CI. | med | **worked** |
| 6 | **Cross-doc drift about this section:** `data-access.md` named `CampService` as the provider (it is `CachingCampService`); `EarlyEntry.md` filed the two ticket-stub view components under the Shell (they are Tickets'); `Teams.md` named `TeamsSectionExtensions`; `Shifts.md` called the roster "deliberately not its own section; interim location"; `service-data-access-map.md` listed `ShiftManagementService` as an invalidator caller; `design-rules.md` named a `Humans.EarlyEntry.Contracts` project; the integration test's comment placed `ShiftDashboardAccess` in the Shell (it is Shifts' `SectionPolicies.cs`). All fixed. `Camps.md:258` (same `CampService` claim) and `dependency-graph.md:481` (Scanner and Tickets edges missing) are written by open PRs and were left alone — enumerated for the next run. `authorization-inventory.md` having no roster row is by design: per-section maps live in each section's `Docs/authorization.md`, which this one has. | med | **worked** (two hits enumerated) |
| 7 | **History narration:** `Section.cs`'s `<remarks>` about the move from the Shell, "byte-identical to their originals" in the csproj, "G5 lane 4b-2b" and "(Peter, 2026-08-14)" in the doc, the lane label in the architecture-test header. Cut. | low | **worked** |
| 8 | **Comment truth cluster** (32 comment verdicts): design-rules cites resolving to unrelated sections (§5, §7a, §1/§2, §15.3b) or to a numbered checklist §15 does not have (steps 1/4/8/12); `Humans.UI` and `Humans.Base.Interfaces.Caching` as homes of types in `Humans.Base`; `GetForUserAsync` described as "the viewer's own" when Gate and Scanner ask for the scanned attendee; the invalidator's caller list missing Teams; a controller comment above the line it does not describe; summaries restating the next line. Rewritten or cut; only §15e and §8b survived as cites. The `InvalidateAll` xmldoc is untouched (finding 1). | med | **worked** |
| 9 | **Dead items:** the test csproj referenced `Humans.Shifts.Contracts` (nothing uses it); `Properties/AssemblyInfo.cs` granted DynamicProxy internals access "because internal types are substituted in tests" — every substituted type is public, tests pass without it; `_ViewImports` imported `.Contracts` and `.Controllers` that no view names. Deleted. | low | **worked** |
| 10 | **Collapse written twice** in `EarlyEntryService` (`Min` + `Distinct(Ordinal)` in both read methods). One private `Collapse`; reviewer-gated, accept. | med | **worked** |
| 11 | **`HasMultiple` is derivable** (`Sources.Count > 1`) and `UserEarlyEntry` is `EarlyEntryRosterRow` minus `UserId`. Dropping the field or folding the records is a public-contract change (six referencing sections). | low | **Needs Peter** |
| 12 | **Delete paths do not evict:** `CampService.DeleteCampAsync` and `TeamService.PermanentlyDeleteTeamAsync` remove grants without calling `IEarlyEntryInvalidator`, so a deleted camp's or team's members keep a cached date until their next own change. Other sections' code → sweep queue. | med | **queued** |
| 13 | **`EarlyEntry.md` rewritten end to end** (898 → 848 words): Cross-Section Dependencies rebuilt from the csproj; caching and fan-out explanations that lived three times (doc, `data-access.md`, xmldoc) now live once each; Status footer trimmed to the issue ref. | low | **worked** |
| 14 | **Freshness triggers watched only the section's own tree** while the doc asserts about Camps, Shifts, Teams, Gate, Scanner and Tickets files. Widened in `EarlyEntry.md`; `health.md` also watches the three contributors' `Section.cs`, Shifts' `SectionPolicies.cs` and the integration render test. | low | **worked** |
| 15 | **`docs/sections/SECTION-TEMPLATE.md`'s (A) block names `Humans.Application` / `Humans.Infrastructure`**, projects that no longer exist, so every new section doc starts stale. Shared file → sweep queue. | low | **queued** |
| 16 | **`design-rules.md:620` wheat comment cites `docs/superpowers/plans/2026-05-25-early-entry-roster.md`**, which does not exist in the tree. Provenance marker, shared file → sweep queue. | low | **queued** |
| 17 | **Inbox:** zero open EarlyEntry issues on peterdrier/Humans; ledger reviewed — the section `debt.yml` entry is finding 3 (retired), the central HUM0028 ruling (2026-06-13, leave `IEarlyEntryInvalidator` grandfathered) stands and is recorded in `health.md` §5. No verdicts to enact. | — | **no change** |
| 18 | **Skill (Phase 5):** the sweep's "skip if already present" cannot tell *never applied* from *applied, then fixed*. This run fixed and retired the Gdpr run's `debt: EarlyEntry` item; every later sweep will re-add it. Filed as peterdrier/Humans#1592. | — | **Needs Peter** |
| 19 | **Skill (Phase 4 step 6):** "UI-affecting strikes get runtime verification" is unachievable in the cloud container — no database, so `dotnet run` cannot serve the page. Finding 4's copy change is a text node, verified by build and `razor-lint`, not rendered. Say what counts as verification when the app cannot run. | — | **Needs Peter** |
| 20 | **Skill (Phase 9) vs routine prompt:** the prompt says stop monitoring 3h after PR creation; Phase 9 says keep the check-in armed until the PR is terminal. This run follows the prompt. | — | **Needs Peter** |

## Worked

Findings 2–10, 13, 14, one commit per strike:

- `doctor(EarlyEntry): target shape` — `health.md`, committed first.
- `doctor(EarlyEntry): drop dead test reference, AssemblyInfo, unused view usings` — finding 9.
- `doctor(EarlyEntry): comments say what the code cannot, and only that` — findings 2, 7, 8.
- `doctor(EarlyEntry): pin the invariants the tests left open` — finding 5.
- `doctor(EarlyEntry): one collapse, not two` — finding 10 (doctor-reviewer: accept).
- `doctor(EarlyEntry): section docs true to the code, end to end` — findings 2, 3, 4, 13, 14.
- `doctor(EarlyEntry): sweep stale claims about the section in other docs` — finding 6.

Surfaces hit: **localization** — none; the roster is an admin page
([`localization-admin-exempt`](../../../memory/code/localization-admin-exempt.md)) and the one
copy change is English admin text. **Authorization** — no behavior change; finding 5 pins the
existing `ShiftDashboardAccess` policy where CI runs it. **Audit** — unchanged; the section
performs no actions. **GDPR** — untouched; the section holds no data. **Invariant doc** —
rewritten and consistent with the struck code. **Migrations** — none. **Navigation** — unchanged.
**Tests** — 15 passing in `tests/Humans.EarlyEntry.Tests`, up from 10. The view change is one
text node in an admin `.cshtml`, verified by build and `razor-lint`, not rendered live
(finding 19).

## Skipped

Findings 1, 11, 12, 15, 16, 18–20 (dispositions above; 1, 12, 15, 16 to the sweep queue; 1, 11,
18, 19, 20 to Needs Peter).

Sections passed over as blocked (open section-doctor PRs on peterdrier/Humans): Auth (#1575),
Backdoor (#1586), Budget (#1565), Calendar (#1578), Campaigns (#1564), Camps (#1561), Consent
(#1572), Email (#1587), Feedback (#1566), Gate (#1574), Governance (#1580), Holded (#1583),
Monitor (#1582), Stripe (#1588), Tickets (#1589). Down-ranked as feature-active, not excluded:
AuditLog, Gdpr, Notifications, Rideshare (peterdrier/Humans#1579).

## Threads

Raw per-thread finding counts before consolidation into the ranked list above.

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session default (see cost comment) | 2 → findings 10, 11 |
| Behavior & bugs | main | session default (see cost comment) | 2 → findings 1, 12; auth paths read by hand (controller attribute, `SectionPolicies.cs`, the three holder surfaces) |
| Freshness | subagent (`doctor-reader`) | opus (low effort) | 14 → findings 2, 6, 14, 16 |
| Tests | subagent (`doctor-reader`) | opus (low effort) | 11 → findings 5, 9 |
| History | subagent (`doctor-reader`) | opus (low effort) | 17 → findings 3, 7, 15 |
| Comments | subagent (`doctor-reader`) | opus (low effort) | 32 → findings 8, 9 |
| Prose & surface + Conformance | subagent (one combined run) | haiku | 4 + conformance table (section-file-layout no hit; resource-key-prefix N/A, no resx; section-table-prefix declined per nobodies-collective/Humans#1012) → findings 4, 13 |
| Inbox | main | session default (see cost comment) | 1 → finding 17. Fork-only scope: zero open EarlyEntry issues on peterdrier/Humans; ledgers reviewed |
| Second opinion (finding 10) | subagent (`doctor-reviewer`) | session default (see cost comment) | accept; one stale sentence in `health.md` §3 flagged and fixed in the same commit |

Independence check: pass — findings 1, 4, 10 and 11 came from the target (a spec-vs-reality
delta, the page against §1, the collapse written twice, the derivable field), not from a scan.

## Retro

**What the selector/rubric got wrong:** nothing. EarlyEntry was the median of the never-doctored
tier after the feature-active down-rank; small, and the right size for a first pass. A low
reforge score again said nothing about correctness: the section's one real defect (finding 1)
is an eviction that never fires, invisible to every structural measure.

**Wasted motion:** the Comments thread's proposed rewrite of the `InvalidateAll` xmldoc had to be
declined after the fact — it would have reworded the very claim finding 1 holds open. A thread
that is told about the Needs-Peter pair up front skips that. The Freshness thread suggested
dropping the `ShiftManagementService.cs` trigger from `health.md`; it stays, because that file
is where the §5 seam lives and a trigger is meant to fire when it moves.

**What the assessment missed that striking revealed:** the staged `AssemblyInfo.cs` deletion rode
into the target-shape commit unnoticed and had to be re-split — a strike's `git add` sees
whatever is staged, not what it means to stage. And the reviewer, not the assessment, caught
that `health.md` §3 described the collapse as written twice one commit after it was not.

**Target diff:** none possible — first doctor pass; `health.md` was written this run. Next run
gets the first real diff.

Two auto-compactions occurred: at the end of Phase 3, and mid-Phase 7 before the push. Phase 5's
re-read of Phases 5–7 was applied after the first; the second landed on the pre-push gate, which
was re-run from the phase log rather than from memory.

**Environment noise:** the full-solution build twice reported Razor compile errors in sections this
run never touched (Camps `RoleDrillDown.cshtml`, Debug `CacheStats.cshtml`), on files unchanged
since #1382 / #1362 that had compiled cleanly at run start. `dotnet build-server shutdown` and a
serial build (`-m:1`) cleared both; nothing in the tree was edited. A parallel Razor source-generator
race in the compiler server, not a defect in either section.

## Needs Peter

- [ ] 1 — wire `InvalidateAll` into `ShiftManagementService`'s gate/build-offset writes, or drop the claim from `EarlyEntry.md` Triggers and the `InvalidateAll` xmldoc?
- [ ] 11 — drop `HasMultiple` from `EarlyEntryRosterRow` (derivable), fold `UserEarlyEntry` into the row shape, or leave the contract?
- [ ] 18 — skill Phase 5: how should the sweep skip an item that was applied and later fixed? (peterdrier/Humans#1592)
- [ ] 19 — skill Phase 4 step 6: with no database in the cloud container, does build + `razor-lint` on a text-only view edit count, or must the strike be queued?
- [ ] 20 — skill Phase 9: when a routine prompt caps monitoring (3h here) and Phase 9 says keep the check-in armed, which wins?

## Sweep queue

- debt: Shifts — `ShiftManagementService.CreateAsync`/`UpdateAsync` never call `IEarlyEntryInvalidator.InvalidateAll`, while `EarlyEntry.md` Triggers and the `InvalidateAll` xmldoc say gate-date and build-offset edits do; the fix adds a constructor parameter and touches every test that constructs the service. Pending Peter's ruling on finding 1 of 2026-09-05-EarlyEntry (wire it or drop the claim).
- debt: Camps — `CampService.DeleteCampAsync` removes a camp's members and grants without calling `IEarlyEntryInvalidator`, so their cached early-entry answer survives the delete (finding 12, 2026-09-05-EarlyEntry).
- debt: Teams — `TeamService.PermanentlyDeleteTeamAsync` removes a team's early-entry grants without calling `IEarlyEntryInvalidator` (finding 12, 2026-09-05-EarlyEntry).
- debt: `docs/sections/SECTION-TEMPLATE.md` — the (A) Migrated block and the "Adding a new section" steps name `Humans.Application`, `Humans.Infrastructure` and `tests/Humans.Application.Tests/Architecture/`, none of which exist; every new section doc starts from stale text (finding 15, 2026-09-05-EarlyEntry).
- debt: `docs/architecture/design-rules.md:620` — the wheat marker cites `docs/superpowers/plans/2026-05-25-early-entry-roster.md`, which is not in the tree; point it at a live source or drop it (finding 16, 2026-09-05-EarlyEntry).

## File coverage

`generated` = excluded from review per the skill; none in this section.

**Changed:**

- `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryInvalidator.cs` — changed
- `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryProvider.cs` — changed
- `src/Sections/Humans.EarlyEntry/Contracts/IEarlyEntryService.cs` — changed
- `src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs` — changed
- `src/Sections/Humans.EarlyEntry/Docs/EarlyEntry.md` — changed
- `src/Sections/Humans.EarlyEntry/Docs/data-access.md` — changed
- `src/Sections/Humans.EarlyEntry/Docs/debt.yml` — changed
- `src/Sections/Humans.EarlyEntry/Docs/health.md` — changed (new)
- `src/Sections/Humans.EarlyEntry/Humans.EarlyEntry.csproj` — changed
- `src/Sections/Humans.EarlyEntry/Properties/AssemblyInfo.cs` — changed (deleted)
- `src/Sections/Humans.EarlyEntry/Section.cs` — changed
- `src/Sections/Humans.EarlyEntry/SectionAdminNav.cs` — changed
- `src/Sections/Humans.EarlyEntry/Services/CachingEarlyEntryService.cs` — changed
- `src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs` — changed
- `src/Sections/Humans.EarlyEntry/Views/EarlyEntryRoster/Index.cshtml` — changed
- `src/Sections/Humans.EarlyEntry/Views/_ViewImports.cshtml` — changed
- `tests/Humans.EarlyEntry.Tests/Controllers/EarlyEntryRosterControllerTests.cs` — changed
- `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs` — changed
- `tests/Humans.EarlyEntry.Tests/Humans.EarlyEntry.Tests.csproj` — changed
- `tests/Humans.EarlyEntry.Tests/Services/CachingEarlyEntryServiceTests.cs` — changed
- `tests/Humans.EarlyEntry.Tests/Services/EarlyEntryServiceTests.cs` — changed

**Reviewed (every name resolves):**

- `src/Sections/Humans.EarlyEntry/Docs/authorization.md` — reviewed
- `src/Sections/Humans.EarlyEntry/Models/EarlyEntryRosterViewModel.cs` — reviewed
- `src/Sections/Humans.EarlyEntry/Views/EarlyEntryRoster/_ViewStart.cshtml` — reviewed

Off-inventory files changed by finding 6's sweep: `src/Sections/Humans.Shifts/Docs/Shifts.md`,
`src/Sections/Humans.Teams/Docs/Teams.md`, `docs/architecture/design-rules.md`,
`docs/architecture/service-data-access-map.md`,
`tests/Humans.Integration.Tests/Controllers/EarlyEntryPageRenderTests.cs`; by the Phase 5 sweep:
`docs/architecture/debt-ledger.yml`, `memory/process/debt-ledger-additions.md` (the Agent
`debt.yml` entry the sweep also carried over was dropped in review round 2: it blessed a design
rather than naming a defect, and Agent's `health.md` already holds the rationale).
