# section-doctor — Governance — 2026-09-18

- Invocation: unattended daily run, no arguments (scheduled routine); Phase 8 skipped per prompt
- Anchor commit: `c654a568` (origin/main at branch point); branch `section-doctor/2026-09-18T011545Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1739
- Target: [`src/Sections/Humans.Governance/Docs/health.md`](../../../src/Sections/Humans.Governance/Docs/health.md)

## Assessment summary

Governance is the section that changed most since its last doctor run: the assembly-votes lane
(peterdrier/Humans#1649) landed on 2026-09-14 and roughly doubled the section. `Docs/health.md`
predated it entirely, so the target was regenerated from behaviour before any scan ran, and the
trace gate passed clean on the result.

The lane itself reads well. Its invariants are stated, its orderings carry the reason they exist,
its embargo is enforced in one place, and its tests are thorough — every invariant this run checked
in the vote lane is pinned. What the lane did not bring with it is the paperwork around it: the
member-facing guide never learned the lane exists, `authorization.md` and the routing tables missed
the pages that came with it, and the applications lane's own test coverage is now visibly the thin
half of the section.

Every thread ran dispatched; none was self-run. Some thread findings were rejected on
re-verification and are recorded as such below — reviewer findings are hypotheses, and these did not
survive contact with the code.

**Independence check: pass.** Findings 1, 2, 6, 7, 15, 21 and 23 came from reading the section
against the shape the target describes, before and beside the detectors: the dead members and the
dead-actor query from the shape pass, the guide gap and the two false caller lists from the
behaviour pass, the tie overreach from the target itself, and the GDPR actor slice from following
the export path end to end. No detector reports any of them.

`doctor.py trace` resolves every name in this file and in the target except the six this run
removed — `AssemblyVote.IsClosedAt`, `BoardVoting_DetailTitle`,
`BoardVoting_ApplicationNotVotable_WithStatus`, `NewApplication_ShouldDefaultToVolunteerTier` and
`AnySubmittedForUserAsync_ReturnsTrueOnlyForSubmittedStatus` — which a findings list has to name in
order to say what it cut.

## Findings

1. `AssemblyVote.IsTerminal` (`Domain/AssemblyVote.cs:102`) and `AssemblyVote.IsClosedAt` (`:111`)
   are dead — untruncated repo-wide greps return only their own declarations, the `IsTerminal` hits
   elsewhere being Humans.Issues' unrelated `IssueStatus` extension. `IsClosedAt`'s doc additionally
   claimed "the embargo and the ballot gate both read this"; both read `AcceptsBallotsAt` (`:116`).
   **Struck** — cut, with the true half of the sentence moved onto `AcceptsBallotsAt`.
2. `docs/guide/Governance.md` documented two of the section's three lanes. `git grep -n -i
   "assembly vote\|Votes/Admin\|/Governance/Votes" -- docs/guide/` (untruncated) returned nothing:
   no member-facing documentation existed anywhere of how to cast or change a ballot, when a result
   becomes visible, or who may open, stop, extend, cancel or peek. **Struck.**
3. `docs/guide/Governance.md`'s freshness triggers named the applications and board-voting
   controllers and not the vote controllers; `flag-on-change` named no vote behaviour. **Struck.**
4. `Docs/authorization.md` had no row for `AdminTermExpiry` / `AdminTermExpiryFix`, the section's
   one un-listed action-level `AdminOnly` override. **Struck.**
5. `Docs/Governance.md:217` gave the term-expiry POST as `Admin/TermExpiry`; it is
   `Admin/TermExpiry/Fix` (`GovernanceApplicationsController.cs:254`), and neither route was in the
   routing table. **Struck.**
6. `Docs/Governance.md` named "Shell's `NavBadgesViewComponent` and `AdminNavTree`" as the callers
   of `GetUnvotedApplicationCountAsync`. Neither type exists. The callers are this section's own
   `SectionAdminNav.cs:35` and Notifications' `NotificationMeterProvider.cs:228`. **Struck.**
7. `Docs/data-access.md` named `AdminDashboardService` as an external reader; no such type exists
   (`git grep -ln "class AdminDashboardService" -- '*.cs'` is empty). It also omitted
   `ProfileController`, the one caller that injects the full `IApplicationDecisionService`, ended a
   sentence mid-phrase, and labelled the services' lifetimes while saying nothing about the
   repositories — which are the surprising ones, both registered Singleton (`Section.cs:28`, `:41`).
   **Struck.**
8. `AssemblyVoteService.cs:691` carried two stacked `<summary>` blocks on `ClosedByNameAsync`; the
   first describes the acta and belongs to `BuildActa` (`:708`), which had no doc. The section's
   only doubled summary. **Struck.**
9. `AssemblyVoteService.cs:501` and `:519` narrated which earlier revisions had lost the guarantee
   each helper carries, and both had gone stale by arithmetic — one warns against "a third
   end-of-vote path" on a helper that has since gained more callers than that, the other that "the
   fourth path will forget" on a helper called from many more than four. **Struck** — the review
   history, not the rule.
10. `Section.cs:49` and `Services/IMembershipQuery.cs:16` said `ITeamServiceRead` and
    `IRoleAssignmentService` "both inject `ISystemTeamSync`". `RoleAssignmentService.cs:28` injects
    it; `TeamService.cs:49-50` resolves it lazily through `IServiceProvider`, which is itself a
    consequence of the same cycle. **Struck** — wording corrected, comment kept.
11. Dead resx keys in all six cultures: `Governance_ApprovedMessage` (superseded by the
    tier-specific pair `Views/Governance/Applications/Index.cshtml:84-86` selects between), `BoardVoting_DetailTitle`
    and `BoardVoting_ApplicationNotVotable_WithStatus`. **Struck.** Both halves of the scan ran: an
    identifier-token sweep over every `*.cs`/`*.cshtml` collected the neutral keys with no literal
    reference, and a second pass established that most of those are reached by prefix + enum name
    and are live. Culture parity is clean — `missing=0 extra=0` for es/de/it/fr/ca.
12. `GovernanceCreate_Lead` read "Colaboradors" in en, de, fr and it while its sibling keys in
    the same file say "Colaboradores". Member-facing. **Struck** for those four; `ca` renders the
    tier names in Catalan throughout, which is a file-wide convention and goes to Peter.
13. `Docs/features/membership-status.md` stated the same negative at `:18`, `:55` and `:57`, and its
    consumer list named the Admin dashboard as the only consumer of `PartitionUsersAsync` while
    Humans.Users' `PreferredLanguageCardViewComponent.cs:27` is another. **Struck.**
14. `Docs/features/asociado-applications.md` and `Docs/features/board-voting.md` both triggered on the bare
    `src/Sections/Humans.Governance/**`, so every vote-lane change flagged two docs that assert
    nothing about votes. **Struck** — replaced with the files each doc makes claims about.
15. `Docs/health.md` §4's "Nothing in this section ever resolves a tie" overreached. Outcome ties
    are never resolved; *elimination* ties inside instant runoff are, by fewest votes in the
    previous round then authored order, and both steps are written into the published notes
    (`AssemblyVoteCounting.cs:172`). Tests pin the behaviour. **Struck** — the target line was
    the thing that was wrong.
16. Cases in `ApplicationTests` asserted auto-properties and a collection initialiser and would
    have passed with every rule in `Application` deleted; `NewApplication_ShouldDefaultToVolunteerTier`
    read as if Volunteer were a tier an application may carry, which `AGENTS.md` names as this
    codebase's most common conceptual mistake. **Struck** — the property-only cases deleted, the
    remaining case renamed, with its body extended to assert the `ValidateTier` rejection it
    now claims.
17. `ApplicationRepositoryTests.cs:89` `AnySubmittedForUserAsync_ReturnsTrueOnlyForSubmittedStatus`
    never varies status (it varies the user); its neighbour named Withdrawn while exercising only
    Approved. **Struck** — both renamed to their bodies.
18. `UserInfoFixtures.ToUserInfo`'s `userEmails` parameter is supplied by none of its call sites.
    **Struck.**
19. `MembershipCalculatorTests.cs:676` explained itself with "at that section's G5" and a
    "design §15 step 8, Campaigns' rewrite-the-stub" citation that resolves to nothing in
    `design-rules.md`. **Struck** — waypoints out, the `internal`-to-Teams constraint kept.
20. Migration-era narration in `Docs/Governance.md` — PR #503, #533, "at G5" / #866, #584 at `:293`,
    `:323`, `:330` and `:338`. **Struck**; every live constraint they were wrapped around stays.
21. `AssemblyVoteRepository.ReassignVoteActorsToUserAsync` (`:560`) made a separate query+loop pass
    over `assembly_votes` for each of its actor columns. **Struck** — collapsed to one, and pinned
    by a new per-column test: the pre-existing merge test sets every actor column on one row, so a
    dropped disjunct still loads that row through another column and the test stays green.
22. Service-layer filter tests that passed their arguments straight through duplicated
    `ApplicationRepositoryTests` over the same query. **Struck** — see "Rejected" below for the ones
    that stay.
23. GDPR export's actor slice does not resolve a merged-away id forward.
    `AssemblyVoteService.cs:1909` passes the raw `userId` to `GetActorRecordForUserAsync`, and
    `:1918-1920` compares the three actor columns against that same raw id, while the voter slice at
    `:1872-1875` resolves through `GetUserInfoAsync` first. `design-rules.md:537` requires the
    forward redirect. An export requested under a merged-away id returns the survivor's ballots
    beside an empty `AssemblyVoteActions` slice. **Queued** — the fix is `info?.Id ?? userId`, but
    it changes export output and wants a test, so it is Peter's call, not a run's.
24. peterdrier/Humans#1698 (GDPR export misses ballots on merged-away accounts) is already fixed:
    `AssemblyVoteService.cs:1872-1875` walks `UserInfo.AllUserIds`, the quoted defect line is gone,
    and the test the issue asked for ships at `AssemblyVoteServiceTests.cs:1629`. **Queued** —
    recommend close; a run never mutates an issue.
25. peterdrier/Humans#1623 sits in the Governance inbox carrying `section:auth` and has no
    Governance deliverable in its body. **Queued** — recommend relabel.
26. The `AdminAppDetail_*` keys are dead in all six cultures, but they are the specified-and-unbuilt
    request-more-info seam rather than leftovers. **Queued** — keep the seam or cut and re-add when
    it is built.
27. Vote finalization's atomicity is unobservable in this section's test project: the in-memory
    fixture ignores `TransactionIgnoredWarning`, so a test that asserts "result and status move
    together or not at all" passes whether or not a transaction wraps them. **Queued** — the
    invariant is real and untestable where the section's tests live.

### Rejected on re-verification

- **History thread H5** claimed `Governance.md:338` named the wrong interface for
  `GetUnvotedApplicationCountAsync`'s Notifications call site. Re-grepped on main:
  `NotificationMeterProvider.cs:37` injects `IApplicationServiceRead`; the thread read the local's
  name (`applicationDecisionService`) as its type. The doc's interface claim was right — its
  *caller* names were not, which is finding 6.
- **Comments thread C2** called `AssemblyVoteService.cs:1510`'s doc false for naming a `varchar`
  bound where the code truncates at `MaxTitleLength`. Read against the code, the parenthetical
  describes the Email outbox's `Subject` column, whose configured maximum length really is the one
  the doc names (`EmailOutboxMessageConfiguration.cs:16`), and what follows is accurate: drafts are
  bounded at `MaxTitleLength`, so the truncation only bites on one written before that check
  existed. No change.
- **Tests thread 24**, half. `GetFilteredApplicationsAsync` takes `string?` filters and
  `Enum.TryParse`s them (`ApplicationDecisionService.cs:322-337`), so the filter-parsing tests cover
  service behaviour the enum-taking repository tests cannot reach. Only the null-pass-through cases
  were duplicates.

## Worked

Run branch `section-doctor/2026-09-18T011545Z`, anchored at `c654a568`; PR peterdrier/Humans#1739. Findings 1-22
above; 23-27 are queued for Peter rather than struck. Both reviewer gates ran as
`doctor-reviewer-critical` (fable high) and both returned APPROVE-with-correction; the corrections
are recorded against the commits that carry them.

Surfaces hit: every supported culture (the resx cuts and the plural fix are in all six, parity
re-checked); authorization including the negative cases (the two `AdminOnly` rows added to
`authorization.md`); the section's invariant doc (regenerated, then corrected at §4); navigation
(the guide's key-pages list gained the vote routes); tests (the section project green, the full
solution green). Not applicable: migrations (none; a doctor run makes none), audit trail (no
behaviour changed), GDPR paths (no new personal data — but see finding 23).

## Skipped

- No EF migration, schema change or backfill; none was needed and a run makes none.
- No analyzer suppression added or removed.
- `ca`'s Catalan rendering of the tier names left as it stands — a file-wide convention, not a typo.
- `Glossary.md` has no "assembly vote" entry. It is outside this section, so it goes to the ledger.
- The dead `AdminAppDetail_*` keys left in place — they belong to the specified-but-unbuilt
  request-more-info seam, and which way that goes is Peter's (finding 26).
- The `resource-key-prefix` backlog left alone: the detector is report-only and renaming the keys
  would touch every call site. Ledgered.
- `verify-migrations-apply` was not consulted; the routine's prompt says it is disabled.

## Retro

The ordering rule earned its place this run. `Docs/health.md` predated the entire assembly-votes
lane, and regenerating the target from behaviour *before* running any scan is what surfaced findings
2, 6, 7 and 15 — all of them shape-vs-reality gaps that no detector reports and that a target
reverse-engineered from the scans would have inherited rather than caught.

The thread findings that did not survive re-verification failed the same way: a thread inferred a
type from an identifier's *name* (`H5` read `applicationDecisionService` as
`IApplicationDecisionService`; the tests thread assumed the service filter tests had the same
signature as the repository ones). Threads read a pre-fetched slice and cannot open the constructor,
so a claim about what a call site's *type* is deserves a main-thread grep before it becomes a
strike. The blast rule already says this for symbols being removed; it does not say it for a claim
being repaired, and finding 6 only came out right because the re-grep happened anyway. That is now
[`thread-claims-about-types`](../../../memory/process/thread-claims-about-types.md).

The full-solution gate caught what the section gate could not. A new `## As a member of the
association` heading in the member guide is invisible to Governance's own tests and fails
`GuideSegmenterTests.Segment_EveryRoleHeadingInShippedContent_OpensAScopedSegment`, because the
segmenter admits only Volunteer, Coordinator and Board and serves an unrecognised `As a …` block to
every visitor, anonymous included. Editing `docs/guide/**` is editing Humans.Guide's input, and the
blast radius of a guide edit is that section's tests, not this one's.

The selector was right about the section and silent about the cost. Governance carried by far the
largest churn in the pool, and the work it implied was almost entirely paperwork the vote lane left
behind rather than anything the surface score can see — the score barely moved and should not have.
Wasted motion was concentrated in one place: the dead-resx scan's first pass was built on a
malformed `git grep` and had to be redone from scratch as an identifier-token sweep.

## Needs Peter

Answered 2026-09-20; the dispositions are in this branch.

- [x] 23 — **fix, and say why the ids differ.** The actor slice now resolves forward and the export
      envelope carries the merge lineage (`GdprExport.UserId` + `MergedFromUserIds`), so a slice
      keyed to an archived id reads as this person's row instead of a stranger's. Peter's framing:
      ballots are immutable and keep the id they were cast under, so the export explains the id
      rather than rewriting it. Lineage resolved once in `GdprService`; contributors still resolve
      for themselves, because the two directions are both correct — rows the merge left behind read
      `AllUserIds`, columns `ReassignAsync` moved read the survivor alone.
- [x] 24 — **close peterdrier/Humans#1698.**
- [x] 25 — **withdrawn, the finding was wrong.** `section:auth` marks the *owning* section, and Auth
      owns the role registry; fanning out to every section is not a second label. It surfaced in this
      inbox because the issue body names `Governance.md` in its docs step and the listing matched on
      text, not on a label. Nothing to relabel.
- [x] 26 — **cut**, and the finding overstated it: nine of the thirteen `AdminAppDetail_*` keys were
      dead (the approve/reject/notes/start-review/view-profile/request-more-info action bar, which
      `AdminDetail.cshtml` never had — it links out to Board voting instead). Those nine are gone
      from all six cultures. The four the page does read stay.
- [x] 27 — **accept it unpinned.** Already in `Docs/debt.yml`; the only destination that runs a real
      transaction self-skips in CI and on cloud runs.
- [x] 12 — **"Colaboradores"; "Colaboradors" is the typo**, verified against the association's own
      statutes (`nobodies-collective/legal`, `Estatutos/ESTATUTOS NOBODIES.md`, Cap. IV and Arts.
      24–25): the Spanish text says *Colaboradores* throughout and never *Colaboradors*. The rename
      of `SystemTeamType.Colaboradors`, `SystemTeamIds.Colaboradors`, the seeded team name and the
      `colaboradors` slug is its own issue — the slug is a live URL and needs a redirect decision.
      `ca` keeps its Catalan rendering: the Catalan statutes say *Col·laboradors*, so the resource
      file matches the official text, and that spelling is the likely origin of the bare
      "Colaboradors" elsewhere.

## File coverage

| Path | Disposition |
|---|---|
| `docs/guide/Governance.md` | changed, reviewed |
| `src/Sections/Humans.Governance.Contracts/ApplicationStatus.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/Humans.Governance.Contracts.csproj` | reviewed |
| `src/Sections/Humans.Governance.Contracts/IApplicationDecisionService.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/IApplicationServiceRead.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/IMembershipCalculatorRead.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipPartition.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipSnapshot.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipStatus.cs` | reviewed |
| `src/Sections/Humans.Governance.Contracts/MembershipStatusLabels.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceApplicationsController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceBoardVotingController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceVotesAdminController.cs` | reviewed |
| `src/Sections/Humans.Governance/Controllers/GovernanceVotesController.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/ApplicationRepository.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/AssemblyVoteRepository.cs` | changed, reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/ApplicationStateHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyBallotConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyBallotHistoryConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteOptionConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVotePeekConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/AssemblyVoteRosterConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/BoardVoteConfiguration.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Configurations/GovernanceJson.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/GovernanceDbContext.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/GovernanceDbContextFactory.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/IApplicationRepository.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/IAssemblyVoteRepository.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260809124929_BaselineGovernance.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/20260910220748_AddAssemblyVotes.Designer.cs` | generated |
| `src/Sections/Humans.Governance/Data/Migrations/20260910220748_AddAssemblyVotes.cs` | reviewed |
| `src/Sections/Humans.Governance/Data/Migrations/GovernanceDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Governance/Docs/Governance.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/authorization.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/data-access.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/debt.yml` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/features/asociado-applications.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/features/assembly-votes.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/features/board-voting.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/features/membership-status.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Docs/features/membership-tiers.md` | reviewed |
| `src/Sections/Humans.Governance/Docs/health.md` | changed, reviewed |
| `src/Sections/Humans.Governance/Domain/Application.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/ApplicationStateHistory.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/ApplicationTrigger.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyBallot.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyBallotChoice.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyBallotHistory.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVote.cs` | changed, reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteKind.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteOption.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVotePeek.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteRoster.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/AssemblyVoteStatus.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/BallotDisclosure.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/BoardVote.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/GovernanceLocalizedText.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/IndicativeAudience.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/RequiredMajority.cs` | reviewed |
| `src/Sections/Humans.Governance/Domain/VoteChoice.cs` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.ca.resx` | changed, reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.cs` | reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.de.resx` | changed, reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.es.resx` | changed, reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.fr.resx` | changed, reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.it.resx` | changed, reviewed |
| `src/Sections/Humans.Governance/GovernanceResource.resx` | changed, reviewed |
| `src/Sections/Humans.Governance/Humans.Governance.csproj` | reviewed |
| `src/Sections/Humans.Governance/Jobs/AssemblyVoteLapseJob.cs` | reviewed |
| `src/Sections/Humans.Governance/Jobs/TermRenewalReminderJob.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/AdminApplicationViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/ApplicationViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/AssemblyVoteViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/BoardVotingViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Models/GovernanceViewModels.cs` | reviewed |
| `src/Sections/Humans.Governance/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.Governance/Section.cs` | changed, reviewed |
| `src/Sections/Humans.Governance/SectionAdminNav.cs` | reviewed |
| `src/Sections/Humans.Governance/SectionChrome.cs` | reviewed |
| `src/Sections/Humans.Governance/SectionJobs.cs` | reviewed |
| `src/Sections/Humans.Governance/SectionMemberDashboard.cs` | reviewed |
| `src/Sections/Humans.Governance/SectionNav.cs` | reviewed |
| `src/Sections/Humans.Governance/SectionThingsToDo.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/ApplicationDecisionService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteCounting.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/AssemblyVoteService.cs` | changed, reviewed |
| `src/Sections/Humans.Governance/Services/AuditEntityTypes.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminDetailDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationAdminRowDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationStateHistoryDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/ApplicationUserDetailDto.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/AssemblyVoteResult.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/AssemblyVoteViews.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVoteRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardData.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDashboardRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/BoardVotingDetailData.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/Dtos/TermExpiryDriftRow.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/GovernanceIndexService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/GovernanceMetricsService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/IAssemblyVoteService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/IGovernanceIndexService.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/IMembershipQuery.cs` | changed, reviewed |
| `src/Sections/Humans.Governance/Services/MembershipCalculator.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/MembershipQuery.cs` | reviewed |
| `src/Sections/Humans.Governance/Services/TermExpiryCalculator.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/AssemblyVotesCardViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/GovernanceApplicationsTileViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/MemberTermStatusViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/PendingConsentsAlertViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/ViewComponents/TierApplicationsCardViewComponent.cs` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Admin.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminDetail.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/AdminTermExpiry.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Create.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Details.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Applications/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Detail.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/BoardVoting/_ViewStart.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Ballots.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Create.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Edit.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Admin/Peek.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Details.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Index.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Governance/Votes/Results.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/AssemblyVotesCard/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/GovernanceApplicationsTile/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/MemberTermStatus/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/PendingConsentsAlert/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/Components/TierApplicationsCard/Default.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationHistory.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationResponseSections.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/Shared/_ApplicationsListContent.cshtml` | reviewed |
| `src/Sections/Humans.Governance/Views/_ViewImports.cshtml` | reviewed |
| `tests/Humans.Governance.Tests/Data/ApplicationRepositoryTests.cs` | changed, reviewed |
| `tests/Humans.Governance.Tests/Domain/ApplicationTests.cs` | changed, reviewed |
| `tests/Humans.Governance.Tests/Domain/GovernanceLocalizedTextTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Enums/EnumStringStabilityTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Humans.Governance.Tests.csproj` | reviewed |
| `tests/Humans.Governance.Tests/Infrastructure/AssemblyVoteServiceFixture.cs` | reviewed |
| `tests/Humans.Governance.Tests/Infrastructure/UserInfoFixtures.cs` | changed, reviewed |
| `tests/Humans.Governance.Tests/Models/AssemblyBallotFormViewModelTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/ApplicationDecisionServiceTests.cs` | changed, reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteCountingTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteEmbargoTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteRosterTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/AssemblyVoteServiceTests.cs` | changed, reviewed |
| `tests/Humans.Governance.Tests/Services/GovernanceIndexServiceTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/MembershipCalculatorTests.cs` | changed, reviewed |
| `tests/Humans.Governance.Tests/Services/MembershipPartitionTests.cs` | reviewed |
| `tests/Humans.Governance.Tests/Services/TermExpiryCalculatorTests.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | — | 6 (one pass with Behavior & bugs) |
| Behavior & bugs | main | — | see Shape |
| Freshness | subagent (`doctor-reader`) | opus-low | 11 |
| Conformance | detectors on main + subagent (`haiku`) | haiku | 3 |
| Tests | subagent (`doctor-reader`) | opus-low | 16 |
| Prose & surface | `reforge` on main + subagent (`haiku`) | haiku | 0 (informational; no threshold crossed) |
| History | subagent (`doctor-reader`) | opus-low | 8 |
| Comments | subagent (`doctor-reader`) | opus-low | 5 |
| Inbox | subagent (`doctor-reader`) | opus-low | 8 |

