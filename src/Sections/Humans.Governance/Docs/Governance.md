<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Governance.Contracts/**
-->
<!-- freshness:flag-on-change
  Application state machine, Board voting flow, term-expiry calculation, BoardVote deletion-on-finalize, and the assembly-vote lifecycle (roster snapshot at open, embargo + audited peek, recorded-ballot history, IRV counting) — review when Governance service/entities/controllers change.
-->

# Governance — Section Invariants

Colaborador and Asociado tier applications, Board voting workflow, term lifecycle, and the association's binding assembly votes. **Not** volunteer onboarding — that lives under `src/Sections/Humans.Onboarding/Docs/Onboarding.md` and is explicitly a separate track.

## Concepts

- **Volunteer** is the standard membership tier. Nearly all humans are Volunteers. Becoming a Volunteer happens through the onboarding process — not through the application/voting workflow described here.
- **Colaborador** is an active contributor with project and event responsibilities. Requires an application and Board vote. 2-year term.
- **Asociado** is a voting member with governance rights (assemblies, elections). Requires an application and Board vote. 2-year term. There is no code-enforced prerequisite of being a Colaborador first — the Application/Create UI defaults the radio to Asociado when the applicant is already an approved Colaborador, but the Submit endpoint accepts either tier from any volunteer.
- **Application** is a formal request to become a Colaborador or Asociado. Never used for becoming a Volunteer.
- **Board Vote** is an individual Board member's vote on a tier application. Board votes are transient working data — they are deleted when the application is finalized, and only the collective decision note and meeting date are retained (GDPR data minimization).
- **Term** — Colaborador and Asociado memberships have synchronized 2-year terms expiring on December 31 of odd years (2027, 2029, 2031...).
- **Assembly Vote** is a binding vote of the association (statutes Art. 8.2 / Art. 10). Distinct from a **Board Vote**, which is the Board's internal vote on one tier application, and from a Surveys **Asociado vote**, which is a secret ballot. An assembly vote is a *recorded vote under embargo*: every ballot is attributable and changeable until close, the tally is invisible until close, and the result is part of the permanent record. Full design: [`features/assembly-votes.md`](features/assembly-votes.md).
- **Roster** is the frozen list, snapshotted the instant a vote opens, of who was entitled to vote in it. It is the legal record of the electorate and the denominator for turnout, and is never recomputed.
- **Official / indicative** — official ballots (Asociados and Board role holders) are the only ones counted in the result; indicative ballots (Colaboradores and/or all Volunteers, per the vote's setting) are tallied separately and never merged in.
- **Embargo** is the rule that no read path returns per-option counts, rankings, or ballot content while a vote is Open. The one exception is the Admin **Peek**, which is itself recorded and listed on the results page.
- **Acta** is the minutes of a General Assembly. The results page renders a plain-text acta block (the numbers statutes Art. 8.6 requires) for the Secretary to paste in.

## Data Model

### Application

Tier application entity with state machine workflow. Used for Colaborador and Asociado applications (never Volunteer). During initial signup, created inline alongside the profile. After onboarding, created via the Governance Applications route.

**Table:** `applications`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User — **FK only**, no nav |
| MembershipTier | MembershipTier | Tier being applied for (Colaborador or Asociado), stored as string |
| Status | ApplicationStatus | Current state (Submitted, Approved, Rejected, Withdrawn), stored as string |
| Motivation | string (4000) | Required motivation statement |
| AdditionalInfo | string? (4000) | Optional additional information |
| SignificantContribution | string? | Asociado-only: applicant's most significant contribution to Nowhere or another Burn. No configured max length. |
| RoleUnderstanding | string? | Asociado-only: applicant's understanding of the asociado role and why they want it. No configured max length. |
| Language | string? (10) | UI language at submission (ISO 639-1 code) |
| SubmittedAt | Instant | When submitted |
| UpdatedAt | Instant | Last update |
| ReviewStartedAt | Instant? | When review began (currently unused — no controller path triggers it) |
| ResolvedAt | Instant? | When resolved (approved/rejected/withdrawn) |
| ReviewedByUserId | Guid? | Reviewer ID — **FK only**, no nav |
| ReviewNotes | string? (4000) | Reviewer notes / rejection reason |
| TermExpiresAt | LocalDate? | Term expiry (Dec 31 of odd year), set on approval |
| BoardMeetingDate | LocalDate? | Date of Board meeting where decision was made |
| DecisionNote | string? (4000) | Board's collective decision note (only record after vote deletion) |
| RenewalReminderSentAt | Instant? | When renewal reminder was last sent |

**Aggregate-local navs:** `Application.StateHistory`, `Application.BoardVotes`.

### ApplicationStateHistory

Append-only per design-rules §12 — `IApplicationRepository` exposes no update or delete surface for this table. (Append-only is enforced at the repository layer, not via DB triggers — only `consent_records` has DB-level immutability triggers.)

**Table:** `application_state_history`

### BoardVote

Individual Board member's vote on a tier application. **Transient working data** — records are deleted when the application is finalized (GDPR data minimization). Only the collective decision (`Application.DecisionNote`, `BoardMeetingDate`) is retained.

**Table:** `board_votes`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| ApplicationId | Guid | FK → Application |
| BoardMemberUserId | Guid | FK → User — **FK only**, no nav |
| Vote | VoteChoice | The vote choice |
| Note | string? (4000) | Optional note explaining the vote |
| VotedAt | Instant | When the vote was first cast |
| UpdatedAt | Instant? | When the vote was last updated |

**Constraint:** Unique `(ApplicationId, BoardMemberUserId)` — one vote per Board member per application.

### ApplicationStatus

| Value | Int | Description |
|-------|-----|-------------|
| Submitted | 0 | Initial state, awaiting Board vote |
| Approved | 2 | Accepted — tier granted |
| Rejected | 3 | Denied — stays at current tier |
| Withdrawn | 4 | Applicant cancelled |

### VoteChoice

| Value | Int | Description |
|-------|-----|-------------|
| Yay | 0 | In favor |
| Maybe | 1 | Leaning yes but has concerns |
| No | 2 | Against |
| Abstain | 3 | No position |

Stored as string via `HasConversion<string>()`.

### AssemblyVote

A binding vote of the association. Full design in [`features/assembly-votes.md`](features/assembly-votes.md).

**Table:** `assembly_votes`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Title | jsonb culture → text | `GovernanceLocalizedText` |
| OfficialText | jsonb culture → text | Markdown, rendered through the shared sanitizer |
| OfficialCulture | string (10) | The culture whose text is binding; the rest are translations |
| InfoUrl | string? (2000) | Optional information link |
| Kind | AssemblyVoteKind | YesNo / RankedChoice, stored as string |
| RequiredMajority | RequiredMajority | Simple / TwoThirds, stored as string |
| IndicativeAudience | IndicativeAudience | None / Colaboradores / AllMembers, stored as string |
| BallotDisclosure | BallotDisclosure | BoardOnly / RosterSeesNames, stored as string |
| Status | AssemblyVoteStatus | Draft / Open / Closed / Cancelled, stored as string |
| AssemblyDate | LocalDate? | The assembly this vote belongs to, for the acta |
| ClosesAt | Instant | Announced closing time; extendable while Open |
| OpenedAt / OpenedByUserId | Instant? / Guid? | Who opened it and when — **FK only**, no nav |
| ClosedAt / ClosedByUserId | Instant? / Guid? | Null `ClosedByUserId` on a lapse means the job closed it |
| CancelReason | string? (2000) | Required when Cancelled |
| ResultJson | string? | The result computed once at close; the results page renders this, never a recount |
| CreatedByUserId / CreatedAt / UpdatedAt | Guid / Instant / Instant | |

**Aggregate-local nav:** `AssemblyVote.Options`.

### AssemblyVoteOption

One authored RankedChoice option: a stable `Key`, the authored `Order` (the documented last tie-break), and a per-culture `Label`. YesNo votes author no options — Yes / No / Abstain are fixed.

**Table:** `assembly_vote_options` — unique `(VoteId, Key)`.

### AssemblyVoteRoster

The frozen electorate, written exactly once at open.

**Table:** `assembly_vote_roster`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| VoteId | Guid | FK → AssemblyVote |
| UserId | Guid? | **FK only**, no nav. **Nullable**: Art. 17 erasure tombstones it to null, which keeps the counts and the stored result valid |
| Tier | MembershipTier | The tier as of the snapshot |
| IsBoardMember | bool | Board role holder at the snapshot |
| IsOfficial | bool | Only official rows contribute to the official result |
| NotifiedAt / ReminderSentAt | Instant? / Instant? | Stamped so the open email and the T-24h reminder never repeat |

**Constraint:** Unique `(VoteId, UserId)` filtered on `"UserId" IS NOT NULL` — one row per person per vote, while any number of erasure tombstones can coexist.

### AssemblyBallot

One person's standing ballot. Hangs off `RosterId`, **not** `UserId`, so erasure can unlink the voter and leave the ballot as the association's legal record with the counts intact.

**Table:** `assembly_ballots`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| VoteId | Guid | FK → AssemblyVote |
| RosterId | Guid | FK → AssemblyVoteRoster — one ballot per roster row (unique) |
| Choice | AssemblyBallotChoice | Yes / No / Abstain / Ranked, stored as string |
| Ranking | jsonb string[]? | Ordered option keys; set only when `Choice = Ranked` |
| Revision | int | Bumped on every change |
| CastAt | Instant | First cast — preserved across changes |
| UpdatedAt | Instant | Last change |

**Aggregate-local nav:** `AssemblyBallot.History`.

### AssemblyBallotHistory

Append-only: one row per revision, written in the same unit of work as the ballot it records. Nothing is ever updated or deleted.

**Table:** `assembly_ballot_history` — unique `(BallotId, Revision)`.

### AssemblyVotePeek

One row per Admin peek at the embargoed tally: `VoteId`, `AdminUserId` (**FK only**), `PeekedAt`. Written in the same unit of work as the read it records, and listed on the results page after close.

**Table:** `assembly_vote_peeks`

### Assembly vote enums

| Enum | Values |
|---|---|
| AssemblyVoteKind | YesNo, RankedChoice |
| AssemblyVoteStatus | Draft, Open, Closed, Cancelled |
| RequiredMajority | Simple, TwoThirds |
| IndicativeAudience | None, Colaboradores, AllMembers |
| BallotDisclosure | BoardOnly, RosterSeesNames |
| AssemblyBallotChoice | Yes, No, Abstain, Ranked |

All stored as strings via `HasConversion<string>()`.

### Assembly vote lifecycle

```
Draft ──open (Admin)──▶ Open ──ClosesAt reached / Stop (Admin)──▶ Closed
  │                      │
  └─delete (Board)       └─cancel (Admin, reason)──▶ Cancelled
```

Closed and Cancelled are terminal — a vote never reopens; to redo one, create a new vote.

### Term lifecycle

Colaborador and Asociado memberships have 2-year synchronized terms expiring Dec 31 of **odd years** (2027, 2029, 2031...). `TermExpiryCalculator.ComputeTermExpiry()` computes the expiry as the next Dec 31 of an odd year that is at least 2 years from the approval date.

- On approval: `Application.TermExpiresAt` is set.
- On expiry without renewal: the next `SystemTeamSyncJob` run removes the human from the Colaboradors / Asociados system team (computed via `HasActiveApprovedTierAsync`) **and downgrades the profile's `MembershipTier`** via `IUserService.DowngradeMembershipTierForExpiredAsync` — to another tier the human still holds an active approval for, otherwise to `Volunteer`. Each downgrade writes an `AuditAction.TierDowngraded` entry.
- Renewal: new Application entity (same tier), goes through normal Board voting.
- Reminder: `TermRenewalReminderJob` sends reminders 90 days before expiry.

## Routing

These controllers serve this section.

| Controller | Routes | Notes |
|------------|--------|-------|
| `GovernanceController` | `GET /Governance` — overview + tier counts + statutes |
| `GovernanceApplicationsController` | `GET /Governance/Applications` — user's own applications | `GET /Governance/Applications/Create`, `POST /Governance/Applications/Create` — submit | `GET /Governance/Applications/Details/{id}`, `POST /Governance/Applications/Withdraw/{id}` | `GET /Governance/Applications/Admin` — admin list (BoardOrAdmin) | `GET /Governance/Applications/Admin/{id}` — admin detail (BoardOrAdmin) |
| `GovernanceBoardVotingController` | `GET /Governance/BoardVoting` — voting dashboard (BoardOrAdmin) | `GET /Governance/BoardVoting/{id}` — voting detail (BoardOrAdmin) | `POST /Governance/BoardVoting/Vote` — cast vote (BoardOnly) | `POST /Governance/BoardVoting/Finalize` — approve/reject (AdminOnly) |
| `GovernanceVotesController` | `GET /Governance/Votes` — every vote, Open first (authenticated) | `GET /Governance/Votes/{id}` — text, stats, and the ballot form for roster members | `POST /Governance/Votes/{id}/Ballot` — cast or change | `GET /Governance/Votes/{id}/Results` and `.../Results.csv` — the stored result (Closed only) |
| `GovernanceVotesAdminController` | `GET /Governance/Votes/Admin` — all votes, all states (BoardOrAdmin) | `GET/POST /Governance/Votes/Admin/Create`, `.../Admin/{id}/Edit`, `POST .../Admin/{id}/Delete` — drafting (BoardOrAdmin) | `POST .../Admin/{id}/{Open,Stop,Extend,Cancel}` and `GET .../Admin/{id}/Peek` — lifecycle (AdminOnly) | `GET .../Admin/{id}/Ballots` — per-member ballots, Closed only, audited (BoardOrAdmin) |

`OnboardingReviewController` also owns the Consent Coordinator review queue (`GET /OnboardingReview`, `POST /OnboardingReview/{id}/Clear`, etc.) — those routes belong to the Onboarding section, not Governance.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View own governance status (tier, active applications). Submit a Colaborador or Asociado application. See **every** assembly vote, its official text and its live participation stats, and the results of any closed vote — roster or not |
| Roster member (official) | Cast and change a ballot until the vote closes; see their own ballot history |
| Roster member (indicative) | The same, with the ballot labelled indicative and excluded from the official result |
| Board | Draft, edit and delete assembly-vote drafts; see every vote and its stats; list every individual ballot after close (audited). **Cannot** open, stop, extend, cancel or peek. View all pending applications and role assignments. Cast individual votes on applications. View Board voting detail. Manage role assignments (all `BoardManageableRoles` — i.e. every role except Admin) |
| HumanAdmin | Manage role assignments (all `BoardManageableRoles` — i.e. every role except Admin). View admin profile pages. (Cannot vote, cannot finalize.) |
| Automation (`governance-assembly-vote-lapse`) | Close a lapsed vote and audit it under the job actor; send the T-24h reminder. Nothing else |
| Admin | Open, stop, extend and cancel an assembly vote, and peek at the embargoed tally (every peek recorded and published on the results page). Reach the Finalize endpoint to approve/reject (AdminOnly policy). The Finalize UI form is rendered only for Admin (`CanFinalize = isAdmin`). Assign and revoke the Admin role. All Board capabilities. Sole finalizer for tier applications |

## Invariants

- Application status follows: Submitted then Approved, Rejected, or Withdrawn. The state machine also defines a `RequestMoreInfo` self-transition on Submitted, but no controller path currently invokes it.
- Each Board member gets exactly one vote per application (DB-enforced via unique index on `(ApplicationId, BoardMemberUserId)`).
- On approval, the term expiry is set to the next December 31 of an odd year that is at least 2 years from the approval date.
- On approval, the human's membership tier is updated and they are added to the corresponding system team (Colaboradors or Asociados).
- On finalization (approval or rejection), all individual Board vote records for that application are deleted. Only the collective decision note and Board meeting date survive.
- Admin can assign all roles. Board and HumanAdmin can assign all roles except Admin (per `RoleAssignmentAuthorizationHandler` + `RoleNames.BoardManageableRoles`).
- Role assignments track temporal membership with valid-from and optional valid-to dates. See `Auth.md` for the role-assignment entity.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- **Assembly votes — the roster** is written exactly once, at open, from Governance's own `applications` (active Approved Asociado / Colaborador terms), the active Board role holders via `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` (official — Board members are de facto Asociados whatever their profile tier says), and the Volunteers team for `AllMembers`. It is **never recomputed**. A person appearing in more than one source gets one row, official if any source is official.
- **A Draft is invisible to members.** `GetVotesForMemberAsync` omits it and `GetVoteForMemberAsync` answers null for it, so knowing a draft's id is not a way to read its official text before the Board opens it.
- **A ranked draft is publishable only when every option carries a label in the vote's official culture** — a keyed-but-unlabelled option would open as a blank line on a binding ballot.
- **Only roster rows with `IsOfficial = true`** contribute to the official result. Indicative ballots are tallied separately and never merged.
- **A ballot** can be cast or changed only while `Status = Open` and `now < ClosesAt`, by the roster member it belongs to. One ballot per roster row; changes bump `Revision`, preserve `CastAt`, and append a history row. Nothing is ever deleted.
- **Content** (`Title`, `OfficialText`, options, `Kind`, `RequiredMajority`, audiences, disclosure) is immutable once Open. The one permitted change is extending `ClosesAt`.
- **The embargo:** no read path returns per-option counts, rankings, or any ballot content for an Open vote except `Peek`, which writes its audit entry and peek row in the same unit of work as the read. Structurally, only `GetResultsAsync`, `PeekAsync` and `GetBallotsForBoardAsync` on `IAssemblyVoteService` can return ballot-derived content; a fourth such path is a change to the association's voting guarantees, not a refactor.
- **A ballot audit entry** names the actor and the vote, never the choice. The choice lives only in `assembly_ballots` / `assembly_ballot_history`.
- **The result** is computed once, at close, and stored in `ResultJson`; the results page renders the stored result rather than recounting.
- **A vote whose `ClosesAt` has passed is Closed for every purpose**, whether or not the hourly lapse job has run: every service read and write settles the state first, so the job is a backstop rather than the thing that makes closing correct. Such a vote's `ClosedAt` is its announced `ClosesAt`, not the instant the lapse was noticed — the acta prints `ClosedAt`, so a late sweep must not make the record claim the vote ran long. An Admin `Stop` stamps the moment it was stopped.
- **Individual ballots are disclosed for a Closed vote only.** A Cancelled vote's ballots are retained as an abandoned record with no result; `GetBallotsForBoardAsync` answers null for it.
- **Closed and Cancelled are terminal.**
- **YesNo verdict:** Simple passes when `Yes > No`, fails when `Yes < No`, and is `Tie` when equal; TwoThirds passes when `Yes >= ceil(2/3 × (Yes + No))`. Abstain is excluded from both bases. With zero ballots cast, Simple is a Tie and TwoThirds Failed.
- **IRV:** the majority base in each round is the ballots that still rank a continuing option; ballots that rank none (or Abstain) are exhausted. Elimination takes the fewest votes, breaking ties by fewest votes in the previous round and then by authored option order.
- **Ties are reported, never resolved.** Statutes Art. 10.2 gives the chair the casting vote and the Secretary records it in the acta; the system prints that rule and stops.
- `application_state_history` is append-only per §12 — repository exposes `AddAsync` and `GetXxxAsync` but no `UpdateAsync` / `DeleteAsync`.

## Negative Access Rules

- Regular humans **cannot** view other humans' applications, cast Board votes, or manage role assignments.
- Board **cannot** assign the Admin role.
- HumanAdmin **cannot** assign the Admin role.
- A human with a pending (Submitted) application **cannot** submit another, of any tier, until the first is resolved.
- Non-roster members **cannot** cast or change a ballot, and **cannot** see any individual ballot.
- Board **cannot** open, stop, extend, cancel or peek an assembly vote — all AdminOnly.
- Nothing **can** bypass the embargo: no export, no Backdoor endpoint, and no Debug page returns ballot content for an Open vote.
- Indicative ballots **cannot** appear in the official tally, the acta block, or the verdict.
- Nothing **can** reopen a Closed or Cancelled vote.
- The lapse job **cannot** touch a repository directly — it calls `IAssemblyVoteService` like every other job in this section.

## Triggers

- When an application is submitted: nav badge and notification meter caches are invalidated so the Board's pending-application count updates. `NotificationSource.ApplicationSubmitted` is retired — no new rows emit it (historical rows only); submission no longer dispatches an in-app notification.
- When an application is approved: the human's tier is updated on their profile (`IUserService.SetMembershipTierAsync`), they are added to the Colaboradors or Asociados system team via `ISystemTeamSync`, an audit-log entry is written (`AuditAction.TierApplicationApproved`), an approval email is sent (`IEmailMessageFactory.ApplicationApproved` via `IEmailService.SendAsync`), and an in-app notification is dispatched (`NotificationSource.ApplicationApproved`). Email + notification are best-effort.
- When an application is rejected: an audit-log entry is written (`AuditAction.TierApplicationRejected`), a rejection email is sent (`IEmailMessageFactory.ApplicationRejected` via `IEmailService.SendAsync`), and an in-app notification is dispatched (`NotificationSource.ApplicationRejected`). Email + notification are best-effort.
- When an application is approved or rejected: all Board vote records for that application are deleted (atomic inside `IApplicationRepository.FinalizeAsync`).
- A renewal reminder email + in-app notification is dispatched 90 days before term expiry (`TermRenewalReminderJob`, `NotificationSource.TermRenewalReminder`). The job is `Humans.Governance/Jobs/TermRenewalReminderJob.cs` since G5 lane 5b-4 (nobodies-collective/Humans#866) — `public` because Shell names the concrete type when it registers and schedules it; it reads and stamps Applications only through `IApplicationDecisionService`.
- On term expiry without renewal: the next `SystemTeamSyncJob` removes the human from the Colaboradors / Asociados system team (driven by `HasActiveApprovedTierAsync`) and calls `IUserService.DowngradeMembershipTierForExpiredAsync`, which **resets the profile's `MembershipTier`** — to another still-active tier the human holds, otherwise to `Volunteer` — and writes an `AuditAction.TierDowngraded` entry per downgrade.
- After every write, `ApplicationDecisionService` invalidates `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator`; on approve/reject it also invalidates each affected voter's `IVotingBadgeCacheInvalidator` entry; on Board-vote upsert it invalidates the voter's `IVotingBadgeCacheInvalidator` entry.
- When an account merge accepts, `AccountMergeService.AcceptAsync` fans out to all `IUserMerge` implementations; `ApplicationDecisionService.ReassignAsync` re-FKs `Application.UserId` (the applicant) from source to target. `BoardVote.BoardMemberUserId` is not re-FK'd — votes are transient, deleted on finalization.
- **Assembly vote opened:** the roster is snapshotted; each roster member is emailed in their preferred language (`IEmailMessageFactory.AssemblyVoteOpened`, `MessageCategory.System`, per-recipient try/catch) and stamped `NotifiedAt`; one in-app notification goes out via `INotificationEmitter.SendAsync` with `sourceKey` = the vote id; audit `AssemblyVoteOpened` carries the official and indicative roster counts.
- **Ballot cast or changed:** a history row is appended and audit `AssemblyBallotCast` / `AssemblyBallotChanged` is written naming the voter and the vote but **not** the choice. The audit entity is the **vote**, never the ballot row: an audit entry keeps its actor for good, so naming the ballot id would leave a permanent join from an erased person to their recorded choice.
- **Stop / Extend / Cancel:** audited with before/after values or the required reason. Cancel emails the roster (`IEmailMessageFactory.AssemblyVoteCancelled`) and computes no result.
- **Close (any path):** stamps `ClosedAt`, computes and stores the result once, resolves the open-vote notification via `INotificationAutoResolve.ResolveBySourceKeyAsync`, and audits `AssemblyVoteClosed` / `AssemblyVoteStopped` — under the job actor (the `jobName` overload of `IAuditLogService.LogAsync`) when the hourly `governance-assembly-vote-lapse` job did it. Any earlier request that observes the deadline passed closes inline before serving.
- **Peek:** an `assembly_vote_peeks` row plus audit `AssemblyVotePeeked`, in the same unit of work as the read.
- **Ballots list after close:** audit `AssemblyBallotsViewed` on every call.
- **T-24h reminder:** one email to roster members with no ballot (`IEmailMessageFactory.AssemblyVoteReminder`), stamped on the roster row (`ReminderSentAt`) so it never repeats. Sent by the same hourly job as the lapse sweep (`SectionJobs`, cron `0 * * * *`).
- **GDPR export:** `IUserDataContributor` contributes the member's roster rows, standing ballots and history under `GdprExportSections.AssemblyVotes`. A second slice, `GdprExportSections.AssemblyVoteActions`, carries the other side of the feature: the votes the person drafted, opened or closed, and the live tallies they peeked at — an officer who runs a vote without being on its roster has no rows in the first slice at all.
- **Art. 17 erasure:** the roster row's `UserId` is set to null (a tombstone that keeps counts and the stored result valid); ballot and history rows are retained unlinked, because the vote is a legal record of the association (GDPR Art. 17(3)(b) / (e)). Declared as partial retention in the section's `ErasureDeclaration`.
- **Account merge (`IUserMerge`):** roster rows and ballots re-FK from source to target; when both accounts are on the same roster the target's row wins and the source's ballot is dropped with an audit entry. The vote actor columns (`OpenedByUserId`, `ClosedByUserId`, `AssemblyVotePeek.AdminUserId`) are re-FK'd as well.
- `UpdateDraftApplicationAsync` silently updates a Submitted application's tier, motivation, and Asociado fields. Allowed only while Status = Submitted; no cache invalidation, no state history append, no notifications.

## Cross-Section Dependencies

- **Users:** `IUserService` — membership tier lives on the profile; approval calls `SetMembershipTierAsync`. `GovernanceIndexService` counts the sidebar tiers itself from `IUserServiceRead.GetAllUserInfosAsync`. Account merge: `ApplicationDecisionService` implements `IUserMerge`; `AccountMergeService` (Users section) fans out to all `IUserMerge` implementations, which triggers `ApplicationDecisionService.ReassignAsync` → `IApplicationRepository.ReassignApplicationsToUserAsync` to re-FK `Application.UserId` from source to target. `BoardVote.BoardMemberUserId` is not re-FK'd (votes are transient, deleted on finalization).
- **Teams:** `ISystemTeamSync` — tier approval or expiry adds/removes the human from Colaboradors/Asociados system teams.
- **Onboarding:** Tier applications are a separate, optional path — never block Volunteer onboarding.
- **Consent:** Consent checks are reviewed alongside (but independently of) tier applications.
- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — display data for applicant/reviewer/voter, stitched into DTOs.
- **Auth:** `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` — used by `ApplicationDecisionService.GetBoardVotingDashboardAsync` to enumerate Board member IDs for vote grid headers, and by `AssemblyVoteService` to put the active Board role holders on the official roster.
- **Assembly votes** additionally use: `IUserServiceRead` (names, preferred language, active state) and `IUserEmailService` (the notification address); `ITeamServiceRead` for Volunteers-team membership when `IndicativeAudience = AllMembers`; `IEmailService.SendAsync` with three `IEmailMessageFactory` messages (`AssemblyVoteOpened`, `AssemblyVoteReminder`, `AssemblyVoteCancelled`); `INotificationEmitter` + `INotificationAutoResolve` with `NotificationSource.AssemblyVoteOpened`; `IAuditLogService`; `IGoogleTranslationService` for the draft-only translate-blanks assist; and `IUserDataContributor` / `ErasureDeclaration` for GDPR. **Nothing new on `Humans.Governance.Contracts`** — no other section reads votes, and the roster is derived from Governance's own data.

## Architecture

**Owning services:** `ApplicationDecisionService`, `MembershipCalculator`, `MembershipQuery`, `AssemblyVoteService`
**Owned tables:** `applications`, `application_state_history`, `board_votes`, `assembly_votes`, `assembly_vote_options`, `assembly_vote_roster`, `assembly_ballots`, `assembly_ballot_history`, `assembly_vote_peeks`
**Status:** (A) Migrated (peterdrier/Humans PR #503, 2026-04-15). Store/decorator layer subsequently removed under issue nobodies-collective/Humans#533. Moved into its own project `src/Sections/Humans.Governance` at G5 (nobodies-collective/Humans#866); the cross-section surface — `IApplicationServiceRead` (including `GetUnvotedApplicationCountAsync` for the nav badge and admin nav tree), `IMembershipCalculatorRead`, `IApplicationDecisionService` (validate-submission / submit / update-draft / the two renewal-reminder reads / mark-reminder-sent), `ApplicationStatus` and the membership snapshot records — sits on the `Humans.Governance.Contracts` leaf, everything else is `internal`.

- **Architecture tests:** this section has no architecture test project of its own; the solution-wide rules in `tests/Humans.Web.Tests/Architecture/` cover it, including `ApplicationServicesTakeNoMemoryCacheRule`, which allowlists `ApplicationDecisionService` — see below.
- `ApplicationDecisionService`, `MembershipCalculator`, and `MembershipQuery` all live in `Humans.Governance/Services/` and depend only on Application-layer abstractions. No `HumansDbContext`. `ApplicationDecisionService` caches the per-board-member unvoted-application count inline via `IMemoryCache` (`CacheKeys.VotingBadge`, 2-min TTL, PerUser — allowlisted in `ApplicationServicesTakeNoMemoryCacheRule`); `MembershipCalculator` and `MembershipQuery` hold no cache.
- `MembershipCalculator` owns no tables — it computes status by orchestrating reads through `IMembershipQuery` (a thin pass-through over `ITeamServiceRead` + `IRoleAssignmentService`, used to break the DI cycle with `ISystemTeamSync`), `IUserServiceRead`, `ILegalDocumentSyncServiceRead`, and `IConsentServiceRead` (resolved lazily via `IServiceProvider` to break a second cycle).
- `IApplicationRepository` (impl `src/Sections/Humans.Governance/Data/ApplicationRepository.cs`) is the only non-test file that touches `DbContext.Applications` / `BoardVotes` / `ApplicationStateHistories`. Aggregate loads include `Application` + `ApplicationStateHistory` + `BoardVote`.
- `FinalizeAsync(app, ct)` is the atomic approve/reject commit: application update + board-vote bulk delete in one `SaveChangesAsync`.
- **Decorator decision — no caching decorator.** At this section's traffic level (a handful of Board-driven writes per week and a few admin reads per day) a caching layer isn't worth the complexity. The earlier store/decorator from peterdrier/Humans PR #503 was removed under issue nobodies-collective/Humans#533 once §15 (`CachingProfileService`) established the canonical shape.
- **Cross-domain navs stripped:** `Application.User`, `Application.ReviewedByUser`, `ApplicationStateHistory.ChangedByUser`, `BoardVote.BoardMemberUser`. Display data resolves via `IUserServiceRead.GetUserInfosAsync` and is stitched into DTOs (`ApplicationAdminDetailDto`, `ApplicationUserDetailDto`, `ApplicationAdminRowDto`, `ApplicationStateHistoryDto`).
- `AssemblyVoteService` is the only caller of `IAssemblyVoteRepository`, which is the only non-test code that touches the six `assembly_*` DbSets. The counting itself is `AssemblyVoteCounting`, a pure static with no clock and no repository, so the verdict logic is testable in isolation and the stored `ResultJson` is reproducible from ballots alone. `AssemblyVoteLapseJob` calls the service, never the repository. No caching decorator: one vote at a time, ~120 voters.
- **Write-side invalidation** is inline in the service. `ApproveAsync` / `RejectAsync` capture voter ids via `IApplicationRepository.GetVoterIdsForApplicationAsync` **before** `FinalizeAsync` (which deletes the `BoardVote` rows), then after the write invalidate `INavBadgeCacheInvalidator`, `INotificationMeterCacheInvalidator`, and every per-voter `IVotingBadgeCacheInvalidator`. `SubmitAsync` / `WithdrawAsync` invalidate nav badge + notification meter only.

### Touch-and-clean guidance

- Nothing outside this section reads the governance-owned tables. `OnboardingService`, `SystemTeamSyncJob` and `NotificationMeterProvider` all go through `IApplicationServiceRead`.
- After nobodies-collective#584 the four board-voting methods lost their `OnboardingService` delegating wrappers and are consumed directly. Three of them — `GetBoardVotingDashboardAsync`, `GetBoardVotingDetailAsync`, `CastBoardVoteAsync` — came in with the section at G5 and are now internal, consumed only by the section's own `GovernanceBoardVotingController` at `/Governance/BoardVoting`. `GetUnvotedApplicationCountAsync` is the one that stayed cross-section, and it sits on `IApplicationServiceRead` (Shell's `NavBadgesViewComponent` and `AdminNavTree`, plus Notifications' `NotificationMeterProvider`). `CastBoardVoteAsync` returns `ApplicationDecisionResult` (not `OnboardingResult`); error keys `NotFound` and `NotSubmitted` are still returned by the service. The controller switch handles `NotFound` explicitly and maps all other error keys (including `NotSubmitted`) to the same "not votable" message via the default arm.
