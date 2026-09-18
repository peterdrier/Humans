<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Governance.Contracts/**
-->

# Governance — target shape

Derived fresh each section-doctor run, before any scan. Not a description of today's layout;
the layout the section's behavior implies. Run history is at the bottom.

## 1. What the section does

Three unrelated jobs live here. Two are about the association deciding things; the third is a
calculator the rest of the app asks questions of.

**Tier membership.** A volunteer asks to become a Colaborador or an Asociado. They write a
motivation (and, for Asociado, what they have contributed and what they think the role is).
The Board reads the request, each Board member records a position on it, and an Admin closes
it at a Board meeting — approved or not. Approval grants the tier for a term that runs to
31 December of the current cycle's odd year, puts the person in the matching system team, tells
them by email and in-app, and leaves an audit entry. Ninety days before a term runs out the
person is reminded to re-apply. Individual Board positions are destroyed the moment the
decision is made; only the Board's collective note and the meeting date survive.

**Binding votes of the association.** The Board writes a motion in the six languages, with
either a yes/no/abstain answer or a ranked list of options, and a time it closes. An Admin
opens it; at that instant the list of who may vote is frozen — the current Asociados and Board
members vote for the record, and, if the motion says so, Colaboradores or every volunteer may
add an opinion that is counted separately and never mixed in. Everyone on that list is emailed
in their own language and nudged once more a day before the close. Any member may read the
motion and watch how many people have voted; nobody may see which way it is going. Members
vote from their phone and may change their answer as often as they like until it closes, and
every version is kept. An Admin who has to know the numbers early can look, and that look is
recorded and published beside the result. At the announced time — or when an Admin stops it —
the count is taken once and stored, the outcome is shown with every elimination round, and a
plain-text block is produced for the Secretary to paste into the minutes. An Admin may also
extend the deadline or call the whole thing off with a reason. Nothing reopens.

**Membership standing.** Separately, this section answers "is this person in good standing?"
for the rest of the app: are they approved, suspended, awaiting approval, deleting, or missing
agreements they were required to sign — and which agreements those are. Nothing here owns that
data; it is computed from profiles, teams, roles and consents each time it is asked.

## 2. The shapes

The external surface, grouped by the question each group answers.

| # | Question shape | Where it is answered | Surface |
|---|---|---|---|
| S1 | *What tier applications does this person have, and what happened to them?* | contracts read surface + own pages | `GetUserApplicationsAsync`, `GetUserApplicationDetailAsync`, `GetSubmittedApplicationForUserAsync`, `GetUserIdsWithPendingApplicationAsync`; `GET /Governance`, `GET /Governance/Applications`, `GET /Governance/Applications/Details/{id}` |
| S2 | *Does this person hold an active tier today?* | contracts read surface | `HasActiveApprovedTierAsync`, `GetActiveApprovedTierUserIdsAsync`, `GetOtherActiveTierAssignmentsAsync` |
| S3 | *What is still on the Board's plate?* | contracts read surface + admin pages | `GetUnvotedApplicationCountAsync`, `GetPendingApplicationCountAsync`, `GetAdminStatsAsync`; `GET /Governance/Applications/Admin`, `GET /Governance/BoardVoting` |
| S4 | *Move one application along its state machine* | write surface + own pages | `ValidateSubmission`, `SubmitAsync`, `UpdateDraftApplicationAsync`, `WithdrawAsync`, `CastBoardVoteAsync`, `ApproveAsync`, `RejectAsync`; `POST /Governance/Applications/Create`, `POST /Governance/BoardVoting/Vote`, `POST /Governance/BoardVoting/Finalize` |
| S5 | *Who needs a renewal nudge, and has it gone out?* | write surface, job-only | `GetExpiringApplicationsNeedingReminderAsync`, `GetPendingApplicationUserTiersAsync`, `MarkRenewalReminderSentAsync`; `TermRenewalReminderJob` |
| S6 | *Is this person in good standing, and what are they missing?* | membership-calculator read surface | the whole of `IMembershipCalculatorRead` — consent completeness (per person, per team, batched), the consolidated snapshot, the standing partition, required-team resolution |
| S7 | *What is the association voting on, and may I vote?* | own pages only | `GetVotesForMemberAsync`, `GetVoteForMemberAsync`; `GET /Governance/Votes`, `GET /Governance/Votes/{id}` |
| S8 | *Record what I choose, and let me change it* | own pages only | `CastBallotAsync`; `POST /Governance/Votes/{id}/Ballot` |
| S9 | *Author a motion and run its lifecycle* | own admin pages only | `GetAllForAdminAsync`, `GetDraftAsync`, `CreateDraftAsync`, `UpdateDraftAsync`, `PreFillTranslationsAsync`, `DeleteDraftAsync`, `OpenAsync`, `StopAsync`, `ExtendAsync`, `CancelAsync`; `GET /Governance/Votes/Admin`, `GET/POST /Governance/Votes/Admin/Create`, `GET/POST /Governance/Votes/Admin/{id}/Edit`, `POST /Governance/Votes/Admin/{id}/Delete`, `POST /Governance/Votes/Admin/{id}/Open`, `POST /Governance/Votes/Admin/{id}/Stop`, `POST /Governance/Votes/Admin/{id}/Extend`, `POST /Governance/Votes/Admin/{id}/Cancel` |
| S10 | *What did the vote decide, and who looked early?* | own pages only | `GetResultsAsync`, `PeekAsync`, `GetBallotsForBoardAsync`; `GET /Governance/Votes/{id}/Results`, `GET /Governance/Votes/{id}/Results.csv`, `GET /Governance/Votes/Admin/{id}/Peek`, `GET /Governance/Votes/Admin/{id}/Ballots` |
| S11 | *Keep the automation honest while nobody is looking* | job-only | `RunLapseAndReminderSweepAsync`; `AssemblyVoteLapseJob` |
| S12 | *GDPR and identity plumbing* | crosscut contracts | two `IUserDataContributor` export + erasure declarations, two `IUserMerge` re-FK paths |

Load-bearing consequences of the grouping:

- **S7–S11 share no data, no entity and no caller shape with S1–S5.** They are a second
  aggregate that happens to live in the same project because the electorate is derived from the
  first one's rows. The only call between them is the roster build reading active approved
  tiers.
- **S6 shares nothing with either.** It owns no table, reads nothing this section writes, and
  answers a question about consent and profile state.
- **Nothing in S7–S11 is on the contracts leaf.** No other section reads votes.

## 3. Structure

Written fresh from the shapes, not from today's folders.

- **`applications` aggregate** — `Application` (+ `ApplicationStateHistory`, `BoardVote` as
  aggregate-local children), one repository as the sole reader/writer of its tables, one
  service holding the state-machine rules and the fan-out to email / notifications / audit /
  system teams. S1–S5 are this.
- **`assembly_*` aggregate** — `AssemblyVote` (+ options, roster, ballots, ballot history and
  peeks as aggregate-local children), one repository as the sole reader/writer of its tables,
  one service holding the lifecycle, the embargo and the fan-out to email / notifications /
  audit / translation. S7–S11 are this. The counting is a separate pure function with no
  clock and no repository, so a member can check a published result by hand against it.
- **Term arithmetic** — one pure function (`TermExpiryCalculator`); no state, no dependencies.
- **Membership standing** — one calculator (S6) with a query adapter under it whose only reason
  to exist is breaking the DI cycle through `ISystemTeamSync`. No repository, no table.
- **Presentation** — one controller per audience (own applications, Board voting, the section
  index, member-facing votes, vote administration); view components for the member dashboard
  and the admin dashboard; one nav contribution each for members and admins; one job
  contribution carrying two jobs.
- **Contracts leaf** — exactly what lives outside the section: the read surface, the write
  members Users' profile submit path and the renewal job call, the membership-calculator read
  surface, the DTOs those signatures name, and `ApplicationStatus`.

Nothing that is only called from inside the section belongs on an interface.

## 4. Invariants

Each cites the line that enforces it.

**Tier applications**

- An application exists only for Colaborador or Asociado; Volunteer is rejected at the entity
  (`Domain/Application.cs:99`).
- `Submitted` is the only state anything can be done from — approve, reject, withdraw and vote
  all refuse anything else (`Services/ApplicationDecisionService.cs:58`, `:138`, `:305`, `:546`;
  the state machine permits nothing else, `Domain/Application.cs:121`).
- A person may hold at most one `Submitted` application at a time, any tier
  (`Services/ApplicationDecisionService.cs:266`).
- Approval and rejection require at least one Board vote to exist; with none, finalization is
  refused and nothing is written (`Services/ApplicationDecisionService.cs:68`, `:145`).
- Finalization is atomic: the application update and the destruction of every `BoardVote` row
  for it commit together (`Data/ApplicationRepository.cs:84`).
- Approval sets a term expiring 31 December of the current cycle's odd year — the approval year
  if odd, else the next; from 1 October of an odd year, the next cycle's
  (`Services/TermExpiryCalculator.cs:19`).
- `application_state_history` is append-only in normal operation: the repository offers no
  update or delete for it. GDPR erasure is the one exception, nulling `Notes` on the rows of the
  person's own applications and on the rows they authored as a Board member
  (`Data/ApplicationRepository.cs:292`).
- Casting a Board vote is Board-only and finalizing is Admin-only
  (`Controllers/GovernanceBoardVotingController.cs:114`, `:149`).

**Assembly votes**

- A draft is invisible to members: the list omits it and the detail read answers null for it,
  so knowing a draft's id is not a way to read its motion early
  (`Services/AssemblyVoteService.cs:116`, `:150`).
- Content is immutable once Open. The draft write refuses a non-draft, and the option replace
  re-reads the status under the row lock
  (`Services/AssemblyVoteService.cs:872`, `Data/AssemblyVoteRepository.cs:141`).
- The electorate is written exactly once, at open, in the same unit of work as the transition
  (`Data/AssemblyVoteRepository.cs:198`), and is never recomputed. A person reachable through
  more than one source gets one row, official if any source is official
  (`Services/AssemblyVoteService.cs:1122`).
- Only official roster rows contribute to the official result; an indicative audience is
  tallied separately and never merged (`Services/AssemblyVoteService.cs:558`).
- A ballot is accepted only while the vote is Open and its deadline has not passed, and only
  from the roster row that entitles the submitter. Checked once before the write
  (`Services/AssemblyVoteService.cs:233`, `:238`) and again under the vote row's lock
  (`Data/AssemblyVoteRepository.cs:357`), so a ballot accepted after the count began is
  impossible rather than unlikely.
- A ballot's audit entry names the actor and the vote, never the choice, and never the ballot
  row (`Services/AssemblyVoteService.cs:263`).
- The embargo: the only reads that return anything derived from ballot content are the stored
  result after close, the audited peek, and the audited post-close ballot list
  (`Services/AssemblyVoteService.cs:609`, `:1315`, `:1348`; the contract is stated at
  `Services/IAssemblyVoteService.cs:14`).
- A peek is recorded before its tally is handed back, and only while the vote still accepts
  ballots (`Services/AssemblyVoteService.cs:1315`, `Data/AssemblyVoteRepository.cs:470`).
- Individual ballots are disclosed for a Closed vote only; a Cancelled vote's are not
  (`Services/AssemblyVoteService.cs:1348`).
- The result is computed once and stored, and the close persists the closed status *before* it
  reads any ballot (`Services/AssemblyVoteService.cs:438`, `:484`).
- A vote whose deadline has passed is closed for every purpose, whether or not the hourly job
  has run: every read and write settles the state first
  (`Services/AssemblyVoteService.cs:339`).
- Closed and Cancelled are terminal, enforced at the write: every vote-row write names the
  state it read and applies under that row's lock only while the row is still in it
  (`Data/AssemblyVoteRepository.cs:68`, lock at `:115`).
- The announced deadline of an open vote only ever moves later
  (`Data/AssemblyVoteRepository.cs:75`).
- A ranked draft carrying a two-thirds threshold is rejected: instant runoff has no defined
  two-thirds rule (`Services/AssemblyVoteService.cs:1007`).
- YesNo: Simple passes when for beats against and ties when level; TwoThirds passes at
  `ceil(2/3 × (for + against))`. Abstentions are in neither base
  (`Services/AssemblyVoteCounting.cs:45`).
- Instant runoff: a round's majority base is the ballots still ranking a continuing option, and
  a level deciding round is reported as a tie rather than broken
  (`Services/AssemblyVoteCounting.cs:128`, `:152`).
- Nothing in this section ever resolves an *outcome* tie — a level deciding round and an
  exhausted field both return `Tie` rather than pick a winner
  (`Services/AssemblyVoteCounting.cs:144`, `:156`). *Elimination* ties inside instant runoff
  are resolved, by fewest votes in the previous round and then authored order, and every
  step used is written into the published notes (`Services/AssemblyVoteCounting.cs:172`).
- Erasure keeps the vote arithmetically valid: the roster row is unlinked from the account and
  the ballot is retained (`Data/AssemblyVoteRepository.cs:545`).
- An account merge leaves roster rows and ballots exactly where they were cast and audits every
  vote it found one on (`Services/AssemblyVoteService.cs:1991`,
  `Data/AssemblyVoteRepository.cs:575`).

**Both**

- S6 never writes anything.

## 5. Seams

Specified but not built. Reserved, not ranked, not to be built by a doctor run.

- **Request-more-info.** The state machine permits a `Submitted → Submitted` re-entry carrying
  reviewer notes, and the entity implements it (`Domain/Application.cs:123`, `:174`), but no
  route, service method or UI reaches it. `Application.ReviewStartedAt` belongs to the same
  unbuilt flow: nothing sets it, and DTOs and views carry it through to the page as a
  permanent blank.
- **Colaborador-before-Asociado.** The Create page defaults the radio to Asociado for an
  approved Colaborador, but no rule requires the order. Whether one should exist is not settled.
- **Opening moves to the Board.** Opening a vote is Admin-only for the first live votes by
  decision, and is meant to become Board-or-Admin once the process has run once or twice
  (`Controllers/GovernanceVotesAdminController.cs:119`). A policy-constant change, not a
  redesign.
- **The temporary term-expiry repair page.** `GET /Governance/Applications/Admin/TermExpiry`
  and its POST exist to rewrite rows approved under the pre-September-2026 rule, and are marked
  for deletion once QA and production are clean (`SectionAdminNav.cs:22`).

## 6. Deliberately not done

- **No caching decorator on either aggregate.** A handful of Board-driven writes a week does
  not pay for one, and on votes a cached tally is a leaked tally. The per-Board-member voting
  badge is the one cached read, inline in the service, two minutes.
- **No concurrency token.** Repo-wide rule. The vote lane does not need one either: it takes
  the row's lock and compares the state it read, which is a narrower guarantee than a version
  column and does not put one on every table.
- **No recount.** The close path is the only writer of the stored result and refuses a vote
  that already has one. A result that could be recomputed is a result that could change after
  it was published.
- **No cross-domain navigation properties.** Applicant, reviewer, voter, history author, vote
  actor and roster member are Guids; display names are stitched from `IUserServiceRead` at the
  call site.
- **No `nameof` for the audit entity discriminators.** They are persisted strings; a CLR rename
  must not silently change the schema.
- **No shared localized-text type with Surveys**, and no promotion of one to Base: a Base type
  would be public surface that nothing outside Governance and Surveys needs.
- **No absence tests.** "There is no route for X" is not a test.
- **No PDF, no Backdoor read, no proxy votes, no quorum determination.** All explicitly out of
  scope for the vote lane; the acta block and the CSV are the export.

## Load-bearing weirdness

Settled decisions. Do not re-litigate.

- `IMembershipQuery` looks like a pointless pass-through over `ITeamServiceRead` and
  `IRoleAssignmentService`. It is not: injecting them directly closes a DI cycle through
  `ISystemTeamSync` and trips `ValidateOnBuild`. Same for the lazy `IConsentServiceRead`
  resolve through `IServiceProvider` inside `MembershipCalculator`.
- `TermRenewalReminderJob` is `public` in a section assembly (HUM0034 makes that an error for
  everything else) because Shell names the concrete type when it schedules it.
- `Humans.Governance` references whole sections — `Humans.Consent` and `Humans.Onboarding` —
  for their resource markers alone. Both are acyclic and disclosed in the csproj.
- The Contracts leaf exists as a separate project, not a folder, because Base-resident consumers
  read through it; a folder inside the section would make Base reference a section.
- `FinalizeAsync` loads the votes and calls `RemoveRange` rather than `ExecuteDeleteAsync`
  because the in-memory provider the unit tests use does not support the latter. The same
  provider is why `BeginLockedWriteAsync` returns null and the vote writes fall back to the
  state check alone outside PostgreSQL.
- Both repositories are registered singleton; they hold no state and take a
  `IDbContextFactory`, so a context is created per call.
- The application badge map deliberately carries no row for `ApplicationStatus.Withdrawn` —
  unmapped values render `bg-secondary`, which is what a withdrawn application should look like.
- `AssemblyVoteService` is long, and most of its length is comment. Every non-obvious ordering
  in it — notification before emails, status before tally, stamp per row rather than per batch,
  the five-minute grace before a result-less close is taken over — was a defect found in
  review, and the comment is the only thing standing between the next reader and reintroducing
  it. Length here is not a target.
- The acta block is deliberately unlocalized and unstyled: it goes into a Spanish legal
  document, and the numbers are what statutes Art. 8.6 requires. Rendering it per viewer is
  tracked as peterdrier/Humans#1695.
- `EffectiveRosterAsync` walks the merge chain on every member-facing read instead of the merge
  re-pointing rows. The roster is evidence of who was entitled when the vote opened; rewriting
  it would destroy the record of a human enrolled twice.

## Run history

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-02 | Term expiry shown wrong on both member pages; dead `/Governance/MyApplications` notification link; dead surface removed; doc and comment truth | peterdrier/Humans#1580 |
| 2 | 2026-09-18 | Assembly votes reached the member guide, the routing table and `authorization.md`; tier plural fixed in four cultures; dead members and resource keys cut | peterdrier/Humans#1739 |
