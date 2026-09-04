<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Governance.Contracts/**
-->

# Governance — target shape

Derived fresh each section-doctor run, before any scan. Not a description of today's layout;
the layout the section's behavior implies. Run history is at the bottom.

## 1. What the section does

Unrelated jobs live here.

**Tier membership.** A volunteer asks to become a Colaborador or an Asociado. They write a
motivation (and, for Asociado, what they have contributed and what they think the role is).
The Board reads the request, each Board member records a position on it, and an Admin closes
it at a Board meeting — approved or not. Approval grants the tier for a term that runs to
31 December of the first odd year at least two years out, puts the person in the matching system team, tells them by email and
in-app, and leaves an audit entry. Ninety days before a term runs out the person is reminded to
re-apply. Individual Board positions are destroyed the moment the decision is made; only the
Board's collective note and the meeting date survive.

**Membership standing.** Separately, this section answers "is this person in good standing?"
for the rest of the app: are they approved, suspended, awaiting approval, deleting, or missing
agreements they were required to sign — and which agreements those are. Nothing here owns that
data; it is computed from profiles, teams, roles and consents each time it is asked.

## 2. The shapes

The external surface, grouped by the question each group answers.

| # | Question shape | Where it is answered | Surface |
|---|---|---|---|
| S1 | *What tier applications does this person have, and what happened to them?* | contracts read surface + own pages | `GetUserApplicationsAsync`, `GetSubmittedApplicationForUserAsync`, `GetUserIdsWithPendingApplicationAsync`; `GET /Governance`, `GET /Governance/Applications`, `GET /Governance/Applications/Details/{id}` |
| S2 | *Does this person hold an active tier today?* | contracts read surface | `HasActiveApprovedTierAsync`, `GetActiveApprovedTierUserIdsAsync`, `GetOtherActiveTierAssignmentsAsync` |
| S3 | *What is still on the Board's plate?* | contracts read surface + admin pages | `GetUnvotedApplicationCountAsync`, `GetPendingApplicationCountAsync`, `GetAdminStatsAsync`; `GET /Governance/Applications/Admin[/{id}]`, `GET /Governance/BoardVoting[/{id}]` |
| S4 | *Move one application along its state machine* | write surface + own pages | `ValidateSubmission`, `SubmitAsync`, `UpdateDraftApplicationAsync`, withdraw, cast vote, approve, reject; `POST /Governance/Applications/Create`, `POST /Governance/Applications/Withdraw/{id}`, `POST /Governance/BoardVoting/Vote`, `POST /Governance/BoardVoting/Finalize` |
| S5 | *Who needs a renewal nudge, and has it gone out?* | write surface, job-only | `GetExpiringApplicationsNeedingReminderAsync`, `GetPendingApplicationUserTiersAsync`, `MarkRenewalReminderSentAsync`; `TermRenewalReminderJob` |
| S6 | *Is this person in good standing, and what are they missing?* | membership-calculator read surface | the whole of `IMembershipCalculatorRead` — consent completeness (per person, per team, batched), the consolidated snapshot, the standing partition, required-team resolution |
| S7 | *GDPR and identity plumbing* | crosscut contracts | `IUserDataContributor` export + erasure declaration, `IUserMerge` re-FK |

Load-bearing consequence of the grouping: **S6 shares no data, no entity and no caller shape
with S1–S5.** It owns no table, reads nothing this section writes, and answers a question about
consent and profile state. Everything else here is the `applications` aggregate. A reader
arriving at this section meets things that have nothing to do with each other.

## 3. Structure

Written fresh from the shapes, not from today's folders.

- **`applications` aggregate** — `Application` (+ `ApplicationStateHistory`, `BoardVote` as
  aggregate-local children), one repository as the sole reader/writer of its tables, one
  service holding the state-machine rules and the fan-out to email / notifications / audit /
  system teams. S1–S5 and S7 are all this.
- **Term arithmetic** — one pure function (`TermExpiryCalculator`); no state, no dependencies.
- **Membership standing** — one calculator (S6) with a query adapter under it whose only reason
  to exist is breaking the DI cycle through `ISystemTeamSync`. No repository, no table.
- **Presentation** — one controller per audience (own applications, Board voting, the
  section index); view components for the member dashboard and the admin dashboard; one nav
  contribution; one job contribution.
- **Contracts leaf** — exactly what lives outside the section: the read surface, the write
  members Shell's profile submit path and the renewal job call, the membership-calculator read
  surface, the DTOs those signatures name, and `ApplicationStatus`.

Nothing that is only called from inside the section belongs on an interface.

## 4. Invariants

- An application exists only for Colaborador or Asociado. Volunteer is rejected at both the
  field rules and the entity (`ValidateTier`).
- `Submitted` is the only state anything can be done from. Approve, reject, withdraw and vote
  all refuse anything else.
- A person may hold at most one `Submitted` application at a time (any tier).
- Approval and rejection require at least one Board vote to exist; with none, finalization is
  refused and nothing is written.
- A Board member has at most one vote per application, and updating it overwrites rather than
  appends.
- Finalization is atomic: the application update and the destruction of every `BoardVote` row
  for it commit together.
- After finalization no individual vote survives — only `DecisionNote` and `BoardMeetingDate`.
- Approval sets a term expiring 31 December of the first odd year at least two years out.
- `application_state_history` is append-only in normal operation: the repository offers no
  update or delete for it. GDPR erasure is the one exception — `ScrubFreeTextForUserAsync`
  nulls `Notes` on the rows of the person's own applications and on the rows they authored
  as a Board member. Nothing else ever mutates a history row.
- Casting a vote is Board-only; finalizing is Admin-only; every admin read is Board-or-Admin;
  a person can only ever fetch or withdraw their own application.
- Erasure keeps the tier/status/date skeleton and clears every free text the person wrote or
  that was written about them, including notes they cast as a Board member.
- S6 never writes anything.

## 5. Seams

Specified but not built. Reserved, not ranked, not to be built by a doctor run.

- **Request-more-info.** The state machine permits a `Submitted → Submitted` re-entry carrying
  reviewer notes, and the entity implements it, but no route, service method or UI reaches it.
  `Application.ReviewStartedAt` belongs to the same unbuilt flow: it has a private setter,
  nothing sets it, and DTOs and views carry it through to the page as a permanent blank.
- **Colaborador-before-Asociado.** The Create page defaults the radio to Asociado for an
  approved Colaborador, but no rule requires the order. Whether one should exist is not settled.

## 6. Deliberately not done

- **No caching decorator.** A handful of Board-driven writes a week does not pay for one. The
  per-Board-member voting badge is the one cached read, inline in the service, two minutes.
- **No concurrency token on `Application`.** Repo-wide rule; two admins finalizing the same
  application in the same second is not a scenario at this scale.
- **No cross-domain navigation properties.** Applicant, reviewer, voter and history-author are
  Guids; display names are stitched from `IUserServiceRead` at the call site.
- **No `nameof` for the audit entity discriminator.** It is a persisted string; a CLR rename
  must not silently change the schema.
- **No absence tests.** "There is no route for X" is not a test.

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
  because the in-memory provider the unit tests use does not support the latter.
- `ApplicationRepository` is registered singleton; it holds no state and takes a
  `IDbContextFactory`, so a context is created per call.
- The badge map deliberately carries no row for `ApplicationStatus.Withdrawn` — unmapped values
  render `bg-secondary`, which is what a withdrawn application should look like.

## Run history

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-02 | Term expiry shown wrong on both member pages; dead `/Governance/MyApplications` notification link; dead surface removed; doc and comment truth | peterdrier/Humans#1580 |
