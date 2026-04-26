<!-- freshness:triggers
  src/Humans.Application/Services/Governance/**
  src/Humans.Domain/Entities/Application.cs
  src/Humans.Domain/Entities/ApplicationStateHistory.cs
  src/Humans.Domain/Entities/BoardVote.cs
  src/Humans.Infrastructure/Data/Configurations/ApplicationConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/ApplicationStateHistoryConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/BoardVoteConfiguration.cs
  src/Humans.Infrastructure/Repositories/ApplicationRepository.cs
  src/Humans.Web/Controllers/ApplicationController.cs
  src/Humans.Web/Controllers/BoardController.cs
  src/Humans.Web/Controllers/GovernanceController.cs
-->
<!-- freshness:flag-on-change
  Application state machine, Board voting flow, term-expiry calculation, and BoardVote deletion-on-finalize — review when Governance service/entities/controllers change.
-->

# Governance — Section Invariants

Colaborador and Asociado tier applications, Board voting workflow, term lifecycle. **Not** volunteer onboarding — that lives under `docs/sections/Onboarding.md` and is explicitly a separate track.

## Concepts

- **Volunteer** is the standard membership tier. Nearly all humans are Volunteers. Becoming a Volunteer happens through the onboarding process — not through the application/voting workflow described here.
- **Colaborador** is an active contributor with project and event responsibilities. Requires an application and Board vote. 2-year term.
- **Asociado** is a voting member with governance rights (assemblies, elections). Requires an application and Board vote. 2-year term. A human must first be an approved Colaborador before applying for Asociado.
- **Application** is a formal request to become a Colaborador or Asociado. Never used for becoming a Volunteer.
- **Board Vote** is an individual Board member's vote on a tier application. Board votes are transient working data — they are deleted when the application is finalized, and only the collective decision note and meeting date are retained (GDPR data minimization).
- **Term** — Colaborador and Asociado memberships have synchronized 2-year terms expiring on December 31 of odd years (2027, 2029, 2031...).

## Data Model

### Application

Tier application entity with state machine workflow. Used for Colaborador and Asociado applications (never Volunteer). During initial signup, created inline alongside the profile. After onboarding, created via the dedicated Application route.

**Table:** `applications`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User — **FK only**, no nav |
| MembershipTier | MembershipTier | Tier being applied for (Colaborador or Asociado) |
| Status | ApplicationStatus | Current state (Submitted, Approved, Rejected, Withdrawn) |
| Motivation | string (4000) | Required motivation statement |
| AdditionalInfo | string? (4000) | Optional additional information |
| Language | string? (10) | UI language at submission (ISO 639-1 code) |
| SubmittedAt | Instant | When submitted |
| UpdatedAt | Instant | Last update |
| ResolvedAt | Instant? | When resolved (approved/rejected/withdrawn) |
| ReviewedByUserId | Guid? | Reviewer ID — **FK only**, no nav |
| ReviewNotes | string? (4000) | Reviewer notes / rejection reason |
| TermExpiresAt | LocalDate? | Term expiry (Dec 31 of odd year), set on approval |
| BoardMeetingDate | LocalDate? | Date of Board meeting where decision was made |
| DecisionNote | string? (4000) | Board's collective decision note (only record after vote deletion) |
| RenewalReminderSentAt | Instant? | When renewal reminder was last sent |

**Aggregate-local navs:** `Application.StateHistory`, `Application.BoardVotes`.

### ApplicationStateHistory

Append-only per design-rules §12 — `IApplicationRepository` exposes no update or delete surface for this table.

**Table:** `application_state_histories`

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

### Term lifecycle

Colaborador and Asociado memberships have 2-year synchronized terms expiring Dec 31 of **odd years** (2027, 2029, 2031...). `TermExpiryCalculator.ComputeTermExpiry()` computes the expiry as the next Dec 31 of an odd year that is at least 2 years from the approval date.

- On approval: `Application.TermExpiresAt` is set.
- On expiry without renewal: human reverts to Volunteer tier, removed from Colaboradors/Asociados system team.
- Renewal: new Application entity (same tier), goes through normal Board voting.
- Reminder: `TermRenewalReminderJob` sends reminders 90 days before expiry.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View own governance status (tier, active applications). Submit a Colaborador or Asociado application |
| Board | View all pending applications and role assignments. Cast individual votes on applications. View Board voting detail |
| Board, Admin | Approve or reject tier applications with a decision note and meeting date. Manage role assignments (all roles except Admin) |
| Admin | Assign the Admin role. All Board capabilities |

## Invariants

- Application status follows: Submitted then Approved, Rejected, or Withdrawn. No other transitions.
- Each Board member gets exactly one vote per application.
- On approval, the term expiry is set to the next December 31 of an odd year that is at least 2 years from the approval date.
- On approval, the human's membership tier is updated and they are added to the corresponding system team (Colaboradors or Asociados).
- On finalization (approval or rejection), all individual Board vote records for that application are deleted. Only the collective decision note and Board meeting date survive.
- Admin can assign all roles. Board and HumanAdmin can assign all roles except Admin.
- Role assignments track temporal membership with valid-from and optional valid-to dates. See `Auth.md` for the role-assignment entity.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- `application_state_histories` is append-only per §12 — repository exposes `AddAsync` and `GetXxxAsync` but no `UpdateAsync` / `DeleteAsync`.

## Negative Access Rules

- Regular humans **cannot** view other humans' applications, cast Board votes, or manage role assignments.
- Board **cannot** assign the Admin role.
- HumanAdmin **cannot** assign the Admin role.
- Humans who already have a pending (Submitted) application for a tier **cannot** submit another for the same tier until the first is resolved.

## Triggers

- When an application is approved: the human's tier is updated on their profile, and they are added to the Colaboradors or Asociados system team.
- When an application is approved or rejected: all Board vote records for that application are deleted (atomic inside `IApplicationRepository.FinalizeAsync`).
- A renewal reminder is sent 90 days before term expiry (`TermRenewalReminderJob`).
- On term expiry without renewal: the human reverts to Volunteer tier and is removed from the tier system team.
- After every write, `ApplicationDecisionService` invalidates `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator`; on approve/reject it also invalidates each affected voter's `IVotingBadgeCacheInvalidator` entry.

## Cross-Section Dependencies

- **Profiles:** `IProfileService` — membership tier lives on the profile. Approval updates the profile.
- **Teams:** `ISystemTeamSync` — tier approval or expiry adds/removes the human from Colaboradors/Asociados system teams.
- **Onboarding:** Tier applications are a separate, optional path — never block Volunteer onboarding.
- **Legal & Consent:** Consent checks are reviewed alongside (but independently of) tier applications.
- **Users/Identity:** `IUserService.GetByIdsAsync` — display data for applicant/reviewer/voter, stitched into DTOs.

## Architecture

**Owning services:** `ApplicationDecisionService`
**Owned tables:** `applications`, `application_state_histories`, `board_votes`
**Status:** (A) Migrated (peterdrier/Humans PR #503, 2026-04-15). Store/decorator layer subsequently removed under issue nobodies-collective/Humans#533.

- `ApplicationDecisionService` lives in `Humans.Application/Services/Governance/` and depends only on Application-layer abstractions. No `HumansDbContext`, no `IMemoryCache`.
- `IApplicationRepository` (impl `Humans.Infrastructure/Repositories/ApplicationRepository.cs`) is the only non-test file that touches `DbContext.Applications` / `BoardVotes` / `ApplicationStateHistories`. Aggregate loads include `Application` + `ApplicationStateHistory` + `BoardVote`.
- `FinalizeAsync(app, ct)` is the atomic approve/reject commit: application update + board-vote bulk delete in one `SaveChangesAsync`.
- **Decorator decision — no caching decorator.** At this section's traffic level (a handful of Board-driven writes per week and a few admin reads per day) a caching layer isn't worth the complexity. The earlier store/decorator from peterdrier/Humans PR #503 was removed under issue nobodies-collective/Humans#533 once §15 (`CachingProfileService`) established the canonical shape.
- **Cross-domain navs stripped:** `Application.User`, `Application.ReviewedByUser`, `ApplicationStateHistory.ChangedByUser`, `BoardVote.BoardMemberUser`. Display data resolves via `IUserService.GetByIdsAsync` and is stitched into DTOs (`ApplicationAdminDetailDto`, `ApplicationUserDetailDto`, `ApplicationAdminRowDto`, `ApplicationStateHistoryDto`).
- **Write-side invalidation** is inline in the service. `ApproveAsync` / `RejectAsync` capture voter ids via `IApplicationRepository.GetVoterIdsForApplicationAsync` **before** `FinalizeAsync` (which deletes the `BoardVote` rows), then after the write invalidate `INavBadgeCacheInvalidator`, `INotificationMeterCacheInvalidator`, and every per-voter `IVotingBadgeCacheInvalidator`. `SubmitAsync` / `WithdrawAsync` invalidate nav badge + notification meter only.

### Touch-and-clean guidance

- `OnboardingService`, `SendBoardDailyDigestJob`, `SendAdminDailyDigestJob`, `TermRenewalReminderJob`, `SystemTeamSyncJob`, and `NotificationMeterProvider` still read governance-owned tables directly for dashboards and batch jobs. Those uses are grandfathered until the sections owning those services migrate to call `IApplicationDecisionService` / `IApplicationRepository` instead.
