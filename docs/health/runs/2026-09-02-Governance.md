# Governance — section doctor, 2026-09-02

- **Invocation:** unattended daily run, no arguments. Phase 8 (inline round) skipped.
- **Anchor commit:** `f2a1a3fb` (`origin/main`)
- **Branch:** `section-doctor/2026-09-02T071811Z` (cloud run, repo root — no worktree)
- **Budget:** 2.5h, single PR.
- **PR:** peterdrier/Humans#1580

## Assessment summary

First doctor pass over Governance (reforge 626, loc=3888, files=57, cognitive p95=6,
max=14, maxClassLoc=612 `ApplicationDecisionService`) — the median never-doctored section
by score. The target shape
([`health.md`](../../../src/Sections/Humans.Governance/Docs/health.md), written this run
before any scan) finds unrelated things under one roof: the `applications` aggregate
(tier applications → Board votes → Admin finalization → a term to 31 December of the first
odd year at least two years out) and a membership-standing calculator that owns no table,
shares no entity with the aggregate, and answers a different question for the rest of the
app. That split is real and load-bearing, not a defect; it is written down so the next reader meets it deliberately.

**The section had live member-facing bugs, and no scan found them.**
The term-expiry date shown on both member-facing pages was recomputed in Razor from
`ResolvedAt` with arithmetic that disagrees with `TermExpiryCalculator` — an application
approved in 2026 renders "31 December 2027" while the stored `TermExpiresAt`, which the
renewal job and the dashboard term card both use, is 2029-12-31. And every approval and
rejection notification pointed at `/Governance/MyApplications`, a route that has never
existed, so the one action link a decided applicant gets is a 404.

Everything else was surface and truth. Dead surface went — a public cross-section contract
method dead end to end, an interface nothing injected, a calculator shortcut with no caller.
Comments said things that were false, and the section's own docs carried a controller, an
architecture test file and service interfaces that do not exist. `authorization.md` and
`data-access.md` had no freshness triggers at all.

Conformance was clean: section-file-layout PASS, `reforge audit-auth` PASS,
`reforge ownership-violations` 0.

## Ranked findings

Value = bug surface removed, then concepts removed, then words removed.

| # | Finding | Value | Disposition |
|---|---|---|---|
| 1 | **Term expiry mis-displayed on both member-facing pages.** `Views/Governance/Index.cshtml` and `Views/Governance/Applications/Index.cshtml` each computed `resolvedYear % 2 == 0 ? +1 : +2` from `ResolvedAt`; `TermExpiryCalculator` computes `today.Year + 2`, bumped to the next odd year. A 2026 approval displayed 2027 against a stored 2029. `UserApplicationSnapshot` already carried `TermExpiresAt`, so the fix carries the stored value through and deletes both copies of the arithmetic — which also removed a hardcoded English "31 December". Tests added at the service seam. | high | **worked** |
| 2 | **`actionUrl: "/Governance/MyApplications"` 404s for every decided applicant.** No such route; the applicant's list is `/Governance/Applications`. | high | **worked** |
| 3 | **`GetApprovedTiersForUserAsync` dead end to end** — public contract, service, repository interface, repository implementation, zero callers in `src/` or `tests/`. Its xmldoc claimed the consent clear-check flow used it; that flow has been annotation-only since the name-only access switch. Reviewer-approved. | high | **worked** |
| 4 | **`IMembershipCalculator` injected by nothing.** Every consumer, in-section and cross-section, goes through `IMembershipCalculatorRead`, and `Section.cs` registered the concrete class behind both. Target §3: nothing only called from inside the section belongs on an interface. The class now carries `IMembershipCalculatorRead, IOrchestrator` directly. Reviewer-approved, with the marker mechanics it named. | med | **worked** |
| 5 | **`MembershipCalculator.HasAnyExpiredConsentsAsync` had no production caller** — a Volunteers-team shortcut over the live `…ForTeamAsync`. Its tests pinned nothing else. Reviewer-approved; the reviewer also corrected the proposal (the member was on the class, not the interface). | med | **worked** |
| 6 | **Dead `"ConcurrencyConflict"` switch arm** in `GovernanceBoardVotingController.Finalize`, plus its resx entries. The service never returns that key and `Application` carries no concurrency token — [`no-concurrency-tokens`](../../../memory/architecture/no-concurrency-tokens.md) says it never will. | med | **worked** |
| 7 | **Hardcoded English `[Display(Name = …)]` on the member-facing Create form.** `Motivation` and `AdditionalInfo` rendered English labels in every culture through empty `<label asp-for>` tags; new `GovernanceCreate_*Label` keys in every supported culture now carry them, matching the Asociado questions beside them. The one on `ConfirmAccuracy` never rendered at all — the view supplies that label — so it was deleted outright. | med | **worked** |
| 8 | **Comments that say something false.** Unresolvable crefs (`Domain.Enums.ApplicationStatus`, `IMembershipCalculator` from the Contracts leaf, `UsersDbContext`); wrong migrations path; wrong table name (`application_state_histories`); a caching decorator, a Board daily digest, an `IProfileService` and an `Application/Details.cshtml` that do not exist; `GetBoardVotingDashboardAsync` attributed to the wrong interface; `BoardVoteRow` claimed to cross into `OnboardingService`; "nine prefixes" for a list that has since grown; `IRecurringJob` attributed to Hangfire; and two stacked `<summary>` blocks that left `ReassignApplicationsToUserAsync` undocumented while its prose documented the member below it. No project sets `GenerateDocumentationFile`, so every broken cref here was silent. | med | **worked** |
| 9 | **Governance.md drift** — a `BoardController` row and prose (`authorization.md` already said no such class exists); an architecture test file at a path that does not exist; `IProfileService` / `IProfileService.GetTierCountsAsync` / `ITeamService` / `IUserService` / `ILegalDocumentSyncService` / `IConsentService` named where the code injects the `…Read` variants; a grandfathering bullet naming retired jobs and claiming consumers read governance tables directly (`reforge ownership-violations` reports zero); "one pending per tier" where the code enforces one per person, any tier. | med | **worked** |
| 10 | **Feature-doc and guide drift** — a board-voting tier filter documented across the feature docs and never built; "stays as Volunteer" on rejection, which changes no tier; "reverts to Volunteer" on lapse, which downgrades to another held tier first; `/Governance/Applications` given as the submit route (it is `/Create`); a "nightly" system-team sync that runs hourly; the wrong policy on `/Users/Admin/Roles`; and the guide's "a second application for the same tier", where the code blocks a second of any tier. | med | **worked** |
| 11 | **Freshness triggers.** `authorization.md` and `data-access.md` had no trigger block at all, so nothing could ever flag them; no doc in the section was triggered by the Contracts leaf; and docs listed `Services/TermExpiryCalculator.cs`, already inside the section glob. | med | **worked** |
| 12 | **Comments that restate the code or narrate history.** Which lane a file moved in, which PR added a line, what "was already arriving transitively"; and the xmldoc summaries and `<param>` lines that say the member's name back ("Unique identifier for the application", "Approves this application"). Where a provenance clause wrapped a live constraint, the constraint stayed. | low | **worked** |
| 13 | **`humans.asociados` counts approved applications of both tiers** and publishes them as "Approved asociado members" — `GovernanceMetricsService` reads `applicationStats.Approved`, which excludes only Withdrawn. Defensible definitions — people currently holding an active Asociado term; approved Asociado applications ever; the profile tier count the sidebar already shows — and no way to pick from the code. | med | **noted → Needs Peter** |
| 14 | **`Application.ReviewStartedAt` is never set.** Private setter, no writer, yet DTOs and views carry it to the page as a permanent blank. It belongs to the unbuilt request-more-info flow (target §5), so it is reported, not removed. | low | **noted → Needs Peter** |
| 15 | **`RequestMoreInfo` is unreachable.** The state machine permits `Submitted → Submitted` with reviewer notes and `Application` implements it, but no route, service method or UI reaches it. A seam, not dead code — target §5. | low | **noted → Needs Peter** |
| 16 | **Approval and rejection notification titles and bodies are hardcoded English** (`$"Your {tier} application has been approved"`). Not this run's to fix: which culture a notification renders in is a question about the notification path, not about Governance. | low | **sweep queue** |
| 17 | **GDPR erasure is entirely unpinned.** `ScrubFreeTextForUserAsync` is the section's Art. 17 obligation — which free text is cleared, which skeleton is kept, and that Board notes the person wrote about others are cleared too — and no test asserts any of it. The highest-value remaining test gap. | high | **section ledger** |
| 18 | **Audit entries on approve/reject unpinned**, and the vote-overwrite rule (one row per Board member, updated in place) is pinned only at the repository, not through `CastBoardVoteAsync`. | med | **section ledger** |
| 19 | **Assert-the-mock tests.** The `GetRequiredTeamIdsForUserAsync` Colaborador cases and the `HasActiveRolesAsync` pair assert the substitute's own return value. The filtering tests and the user-scoping test duplicate repository coverage at the service. | low | **section ledger** |
| 20 | **Controller authorization attributes unpinned.** The policies themselves are tested; that these controllers and actions carry them is not. | med | **section ledger** |
| 21 | **Unprefixed resx keys** in `GovernanceResource.resx`. Reported, not backfilled — a prefix rename is a rename, not a doctor strike. | low | **no change** |

Raised after 3e, so each takes the next unused number. Numbers are assigned once and never
reused; `## Needs Peter` cites them rather than restating them.

| # | Finding | Value | Disposition |
|---|---|---|---|
| 22 | **The threads contract and `no-derived-aggregates-in-docs` disagree.** The contract asks each thread row for a findings count; the row already enumerates its findings, so that count is the shadow copy the atom forbids — and the atom is marked HARD RULE. The mode and model columns went in; the count did not. | — | **Needs Peter** |
| 23 | **Phase 3 amendment: never pipe a `reforge` scan through `head`.** `audit-surface` orders by symbol, not by value, so truncation drops evidence from the middle rather than the tail; this run's first pass lost zero-caller entries that way. | — | **Needs Peter** |
| 24 | **Phase 4 amendment: check for a near-identical live name before proposing a delete.** `HasAnyExpiredConsentsAsync` (dead, on the concrete class) and `HasAnyExpiredConsentsForTeamAsync` (live, on the interface) differ by one word; the reviewer caught the wrong home. | — | **Needs Peter** |
| 25 | **Does scoped-inner-loop testing apply to doctor strikes?** This run spent a large slice of its budget on solution-wide builds it did not need. | — | **Needs Peter** |
| 26 | **Both remotes in this container point at production**, and a run has already pushed a branch there. | — | **Needs Peter** |
| 27 | **Removing the `[Display(Name = …)]` attributes made the DataAnnotations validation text worse.** `[Required]`/`[StringLength]` on `Motivation` and `AdditionalInfo` interpolate the metadata display name, which is now the CLR name — "The Motivation field is required." Not a localization regression: neither the message template nor the old display names resolve against `SharedResource`, so this text was raw English in all six cultures before and after. The real defect underneath is older and larger — see the section ledger. | med | **section ledger** |
| 28 | **One label, two destinations.** The member-dashboard tile is titled `Nav_Governance` but links to `GovernanceApplications/Index`, while the user dropdown and the profile page use the same `Nav_Governance` label for `Governance/Index`. Neither is a dead end; the same words lead two places. | low | **section ledger** |
| 29 | **InspectCode Tier 1/2 cannot run in a cloud doctor run.** Nothing installs it in the container, so the Prose & surface lens is structurally half-runnable here. Either the skill should install it, or Phase 3d should say the tier check is a local-only half and stop counting a cloud run's silence as coverage. | — | **Needs Peter** |

## Worked

Findings 1–12, one commit per strike:

- `doctor(Governance): show the stored term expiry, not view arithmetic` — finding 1.
- `doctor(Governance): fix the dead decision-notification link, drop two dead paths` — findings 2, 6, 7.
- `doctor(Governance): delete three dead surfaces` — findings 3, 4, 5.
- `doctor(Governance): make the section's comments true` — finding 8.
- `doctor(Governance): make the section's docs true` — findings 9, 10, 11.
- `doctor(Governance): cut comments that restate the code` — finding 12.
- `doctor(Governance): sweep the queue from merged run files` — Phase 5 sweep, not a Governance finding.

Gates: `dotnet build Humans.slnx -v quiet` clean at every strike;
`dotnet test Humans.slnx -v quiet` green across the solution before the deletion
push (`Humans.Integration.Tests` self-skips, by design);
`dotnet format whitespace Humans.slnx --verify-no-changes` clean.

## Skipped

- **Findings 13–15** are Peter's, not a run's: a metric whose correct definition is a
  product question, and two halves of a seam the target shape reserves.
- **Finding 21** left alone: renaming resource keys is a rename.
- **Phase 8** skipped — unattended run.
- Deleting `IMembershipCalculator` was proposed with the wrong mechanics (this run believed
  `HasAnyExpiredConsentsAsync` sat on it); the reviewer corrected that before the strike.

## Retro

**What did the target shape catch that a scan did not?** Both headline bugs and the
interface deletion. `reforge audit-surface` reported neither view's arithmetic nor the dead
notification URL — nothing in a call-graph scan can see that a string literal is not a
route, or that two Razor expressions disagree with a pure function three files away. The
target's §3 line "nothing that is only called from inside the section belongs on an
interface" is what condemned `IMembershipCalculator`; the scan listed it among many
zero-caller symbols without distinguishing it from the deliberate ones.

**What did the run get wrong?** It proposed deleting
`HasAnyExpiredConsentsAsync` from `IMembershipCalculator` when the member exists only on
the concrete class and the interface declares a different, live method one line away — a
name-prefix collision the run would have walked into without the reviewer. And it read
`reforge audit-surface` through `head -70`, which truncated the dead-surface evidence
mid-list; the rerun without the pipe found the rest.

**What cost the most time?** Solution-wide builds — one per strike, at ~3 minutes each,
against a section test project that runs in 2 seconds. That is a large slice of a 2.5h
budget. The scoped-inner-loop rule exists for this and the run under-used it: only the
deletion strike genuinely needed the full-solution gate.

**What should the next run on this section look at?** The queued test gaps, GDPR
erasure first — it is the section's only regulatory obligation and nothing pins it. And
`ApplicationDecisionService` at 612 LOC is the section's largest class; the target shape
blesses it as one aggregate's state machine plus its fan-out, but a run that wanted to
split anything should start by asking whether the notification/email fan-out is part of the
state machine or a listener on it.

## Needs Peter

- [ ] **`humans.asociados` counts the wrong thing** (finding 13). `GovernanceMetricsService`
  publishes `applicationStats.Approved` — every approved application of either tier, all
  years — under the description "Approved asociado members". The defensible definitions:
  people holding an active Asociado term today, approved Asociado applications ever, or the
  profile-tier count the section's own sidebar already computes. Which one does the gauge
  mean? A run cannot pick this from the code.
- [ ] **`Application.ReviewStartedAt` and `RequestMoreInfo`** (findings 14, 15) are two
  halves of one unbuilt flow: the state machine and entity implement a `Submitted →
  Submitted` re-entry carrying reviewer notes, nothing reaches it, and `ReviewStartedAt`
  rides through DTOs to views as a permanent blank. Build the flow, or delete both?
- [ ] Finding 22 — which rule wins, the threads contract's findings count or
  [`no-derived-aggregates-in-docs`](../../../memory/process/no-derived-aggregates-in-docs.md)?
- [ ] Finding 23 — take the Phase 3 amendment (redirect a `reforge` scan to a file, never
  `head` it)?
- [ ] Finding 24 — take the Phase 4 amendment (check for a near-identical live name before
  proposing a delete)?
- [ ] Finding 25 — does the scoped-inner-loop guidance apply to doctor strikes? Saying so in
  Phase 4 buys back most of this run's build time.
- [ ] Finding 26 — two things: delete the stray branches
  `section-doctor/2026-09-02T071811Z` and `section-doctor/2026-08-29T071612Z` from
  `nobodies-collective/Humans` (this run cannot; the environment's git relay drops every
  delete refspec), and decide whether Phase 0 should assert that `origin` resolves to
  `peterdrier/Humans` before any push. In this container `origin` *and* `upstream` were both
  the production URL, and the 2026-08-29 branch shows this run was not the first to hit it.
- [ ] Finding 29 — should the skill install InspectCode for cloud runs, or should Phase 3d
  record Tier 1/2 as a local-only half of the Prose & surface lens?

## Sweep queue

Findings 17–20 are in-section and went straight to
[`src/Sections/Humans.Governance/Docs/debt.yml`](../../../src/Sections/Humans.Governance/Docs/debt.yml)
this run, not here — the queue carries off-section debt only.

Phase 5's sweep also dropped, rather than applied, one item from the merged 2026-08-28 Agent
run: a note that `AgentService.RunTurnAsync` and `AnthropicClient.StreamAsync` score high but
are the load-bearing streaming loops that section's target shape blesses. It names no defect
and no remedy, so a debt inbox would serve it to `/debt-sweep` forever to be re-declined each
time. The observation survives where it belongs — in the 2026-08-28 Agent run file and that
section's target shape.

- `debt: Notifications — every Governance decision notification's title and body is a hardcoded English interpolated string (ApplicationDecisionService.ApproveAsync/RejectAsync). The recipient's PreferredLanguage is already used for the matching email, so the notification path is the odd one out; which culture an in-app notification renders in is that section's call, not Governance's (finding 16, /section-doctor on Governance 2026-09-02).`
- `memory: code/no-recomputing-a-pure-function-in-a-view — a view that recomputes something a pure function already computes is a bug waiting, not a duplication smell. Both sides compile, so nothing in the build, the tests or any scan can see the disagreement. Carry the computed value through the DTO instead (finding 1, /section-doctor on Governance 2026-09-02).`

## File coverage

The 3a inventory at anchor `f2a1a3fb`, plus `docs/guide/Governance.md`. `.Designer.cs` and the
model snapshot are `generated` — 3a's only exemptions; the shipped baseline migration is not
exempt and was read.

| Path | Disposition |
|---|---|
| `docs/guide/Governance.md` | changed |
| `src/Sections/Humans.Governance.Contracts/ApplicationStatus.cs` | changed |
| `src/Sections/Humans.Governance.Contracts/Humans.Governance.Contracts.csproj` | changed |
| `src/Sections/Humans.Governance.Contracts/IApplicationDecisionService.cs` | changed |
| `src/Sections/Humans.Governance.Contracts/IApplicationServiceRead.cs` | changed |
| `src/Sections/Humans.Governance.Contracts/IMembershipCalculatorRead.cs` | changed |
| `src/Sections/Humans.Governance.Contracts/MembershipPartition.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipSnapshot.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipStatus.cs` | changed |
| `src/Sections/Humans.Governance.Contracts/MembershipStatusLabels.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceApplicationsController.cs` | changed |
| `src/Sections/Humans.Governance/Controllers/GovernanceBoardVotingController.cs` | changed |
| `src/Sections/Humans.Governance/Controllers/GovernanceController.cs` | changed |
| `src/Sections/Humans.Governance/Data/ApplicationRepository.cs` | changed |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationStateHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/BoardVoteConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/GovernanceDbContext.cs` | changed |
| `src/Sections/Humans.Governance/Data/GovernanceDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/IApplicationRepository.cs` | changed |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/GovernanceDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Governance/Docs/Governance.md` | changed |
| `src/Sections/Humans.Governance/Docs/authorization.md` | changed |
| `src/Sections/Humans.Governance/Docs/data-access.md` | changed |
| `src/Sections/Humans.Governance/Docs/debt.yml` | changed |
| `src/Sections/Humans.Governance/Docs/features/asociado-applications.md` | changed |
| `src/Sections/Humans.Governance/Docs/features/board-voting.md` | changed |
| `src/Sections/Humans.Governance/Docs/features/membership-status.md` | changed |
| `src/Sections/Humans.Governance/Docs/features/membership-tiers.md` | changed |
| `src/Sections/Humans.Governance/Docs/health.md` | changed |
| `src/Sections/Humans.Governance/Domain/Application.cs` | changed |
| `src/Sections/Humans.Governance/Domain/ApplicationStateHistory.cs` | changed |
| `src/Sections/Humans.Governance/Domain/ApplicationTrigger.cs` | changed |
| `src/Sections/Humans.Governance/Domain/BoardVote.cs` | changed |
| `src/Sections/Humans.Governance/Domain/VoteChoice.cs` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.ca.resx` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.cs` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.de.resx` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.es.resx` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.fr.resx` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.it.resx` | changed |
| `src/Sections/Humans.Governance/GovernanceResource.resx` | changed |
| `src/Sections/Humans.Governance/Humans.Governance.csproj` | changed |
| `src/Sections/Humans.Governance/Jobs/TermRenewalReminderJob.cs` | changed |
| `src/Sections/Humans.Governance/Models/AdminApplicationViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/ApplicationViewModels.cs` | changed |
| `src/Sections/Humans.Governance/Models/BoardVotingViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/GovernanceViewModels.cs` | changed |
| `src/Sections/Humans.Governance/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Governance/Section.cs` | changed |
| `src/Sections/Humans.Governance/SectionAdminNav.cs` | changed |
| `src/Sections/Humans.Governance/SectionChrome.cs` | changed |
| `src/Sections/Humans.Governance/SectionJobs.cs` | changed |
| `src/Sections/Humans.Governance/SectionMemberDashboard.cs` | changed |
| `src/Sections/Humans.Governance/SectionThingsToDo.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/ApplicationDecisionService.cs` | changed |
| `src/Sections/Humans.Governance/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminDetailDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminRowDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationStateHistoryDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationUserDetailDto.cs` | changed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVoteRow.cs` | changed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardData.cs` | changed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDetailData.cs` | changed |
| `src/Sections/Humans.Governance/Services/GovernanceIndexService.cs` | changed |
| `src/Sections/Humans.Governance/Services/GovernanceMetricsService.cs` | changed |
| `src/Sections/Humans.Governance/Services/IGovernanceIndexService.cs` | changed |
| `src/Sections/Humans.Governance/Services/IMembershipCalculator.cs` | changed |
| `src/Sections/Humans.Governance/Services/IMembershipQuery.cs` | changed |
| `src/Sections/Humans.Governance/Services/MembershipCalculator.cs` | changed |
| `src/Sections/Humans.Governance/Services/MembershipQuery.cs` | changed |
| `src/Sections/Humans.Governance/Services/TermExpiryCalculator.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/GovernanceApplicationsTileViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/MemberTermStatusViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/PendingConsentsAlertViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/TierApplicationsCardViewComponent.cs` | changed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Admin.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminDetail.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Create.cshtml` | changed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Details.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Index.cshtml` | changed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Detail.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/_ViewStart.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Index.cshtml` | changed |
| `src/Sections/Humans.Governance/Views/Shared/Components/GovernanceApplicationsTile/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/MemberTermStatus/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/PendingConsentsAlert/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/TierApplicationsCard/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationHistory.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationResponseSections.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationsListContent.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Governance.Tests/Data/ApplicationRepositoryTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Domain/ApplicationTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Enums/EnumStringStabilityTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Humans.Governance.Tests.csproj` | reviewed |
| `tests/Humans.Governance.Tests/Infrastructure/UserInfoFixtures.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/ApplicationDecisionServiceTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/GovernanceIndexServiceTests.cs` | changed |
| `tests/Humans.Governance.Tests/Services/MembershipCalculatorTests.cs` | changed |
| `tests/Humans.Governance.Tests/Services/MembershipPartitionTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/TermExpiryCalculatorTests.cs` | reviewed |

Also read, outside the inventory, because a strike made them false:
`src/Sections/Humans.Onboarding/Docs/Onboarding.md`,
`src/Sections/Humans.Users/Docs/features/profiles.md`,
`docs/architecture/dependency-graph.md`.

## Threads

| Thread | How it ran | Model | Findings folded in |
|---|---|---|---|
| Shape | main | opus | 4, 14, 15 — and the target shape itself, written before any scan |
| Behavior & bugs | main | opus | 1, 2, 6, 7 |
| Freshness | subagent (`doctor-reader`) | opus, effort low | 11, and the doc-drift inputs to 9 and 10 |
| Conformance | main (mechanical detectors, no subagent) | opus | none — layout PASS, `audit-auth` PASS, `ownership-violations` 0 |
| Tests | subagent (`doctor-reader`) | opus, effort low | 5, 17–20 |
| Prose & surface | main (doctor pass + review round 5) | opus | 21, 28, 29 |
| History | subagent (`doctor-reader`) | opus, effort low | 12 — false narrations, and cuts with trim-to text |
| Comments | subagent (`doctor-reader`) | opus, effort low | 8 and 12 — the falsehoods, the cuts, and a keeps list the cuts respected |
| Inbox | subagent (`doctor-reader`) | opus, effort low | none — `peterdrier/Humans` has open issues #1576, #1562 and #1554, none Governance; no Governance rows in `debt-ledger.yml`; the section had no `Docs/debt.yml` |

**Prose & surface ran in two passes**, the second in review round 5 after the gap below was
caught. Dead resources and the resource-key prefix check ran in the doctor pass (finding 21).
Nav quality ran in round 5 and is now complete. InspectCode Tier 1/2 did **not** run, for a
reason that is not a judgement call: no InspectCode is installed in this container — no
`inspectcode` or `jb` on `PATH`, and `dotnet tool list --global` is empty. The run environment
ships the .NET SDK, `dotnet-ef` and `reforge` only. A cloud run can therefore never satisfy
that half of the lens, which is a fact about the skill's environment rather than about
Governance; recorded as finding 29.

Nav result — no dead ends and no missing backlinks. Every page has an inbound link from
outside itself and a way back out: `/Governance` from the user dropdown
(`_LoginPartial.cshtml`) and twice from the profile page; `/Governance/Applications` from the
member-dashboard tile, breadcrumbing up; `Create` from seven call sites, cancelling to
`Index`; `Details` from the list rows, with a back button; `Admin` and `BoardVoting` from the
`AdminNavTree` sidebar contribution, with `_AdminLayout`'s sidebar as the exit; both detail
pages breadcrumb to their lists. One inconsistency fell out of the pass and is finding 28.

No other thread was skipped. Findings counts are deliberately absent — see finding 22.

Independence check: **pass**. Findings 1, 4, 14 and 15 came from the target shape rather
than from a scan; findings 1 and 2, the highest-value items, came from reading views
and service code by hand, and `reforge audit-surface` reported neither.
