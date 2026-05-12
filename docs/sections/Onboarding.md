<!-- freshness:triggers
  src/Humans.Application/Services/Onboarding/**
  src/Humans.Application/Services/HumanLifecycle/**
  src/Humans.Application/Services/Profile/ProfileService.cs
  src/Humans.Application/Services/Users/UserService.cs
  src/Humans.Application/Services/Users/AccountProvisioningService.cs
  src/Humans.Application/Services/Consent/**
  src/Humans.Application/Services/Governance/ApplicationDecisionService.cs
  src/Humans.Application/Services/Teams/TeamService.cs
  src/Humans.Web/Controllers/OnboardingReviewController.cs
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
-->
<!-- freshness:flag-on-change
  Onboarding orchestration across Profile/Consent/Governance/Teams, membership gate, and IOnboardingEligibilityQuery seam — review when OnboardingService or any of its cross-section dependencies change.
-->

# Onboarding — Section Invariants

Pure orchestrator over Profiles, Legal & Consent, Teams, and Governance. Owns no tables.

> **Three-concerns split (umbrella nobodies-collective#563).** `OnboardingService` is the *intake funnel only* (signup → profile → consents → first-team admission, plus CC review queue). Sibling services own the other workflow stages so onboarding stays narrow:
>
> - **Lifecycle state-machine** (suspend/unsuspend, future re-consent suspensions and term-renewals) → `IHumanLifecycleService` (nobodies-collective#583).
> - **Board voting** (`GetBoardVotingDashboardAsync`, `GetBoardVotingDetailAsync`, `HasBoardVotesAsync`, `CastBoardVoteAsync`, `GetUnvotedApplicationCountAsync`) → `IApplicationDecisionService` (Governance owns the `applications` + `board_votes` tables; this PR removed the OnboardingService delegating wrappers).
> - **Admin dashboard aggregation** (`GetAdminDashboardAsync`, `GetPendingReviewCountAsync`) → `IAdminDashboardService` (`Humans.Application.Services.Dashboard`). Distinct from `IDashboardService` (single-user member dashboard) — different shapes, different consumers.
> - **Account deletion cascade** → future `IAccountDeletionService` (nobodies-collective#582).

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, and consent to all required legal documents. Once both are done, admission to the Volunteers team is automatic — no Consent Coordinator approval is required for entry.
- The **Membership Gate** restricts most of the application to active Volunteers. Humans still onboarding are limited to their profile, consent, feedback, legal documents, camps (public), and the home dashboard. All admin and coordinator roles bypass this gate entirely.
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). They are redirected to the Guest dashboard instead of the Home dashboard and see a reduced nav: Guest Dashboard, Camps, Teams (public), Calendar, Legal. They can create a profile to enter the standard onboarding flow.
- The **Consent Coordinator review** (`Profile.ConsentCheckStatus`) is an audit/annotation track that runs in parallel with admission. When all required consents are signed, `ConsentCheckStatus` flips to `Pending` and the human appears in the CC review queue. CC actions (Clear / Flag / Reject) maintain the audit annotation and still flip `Profile.IsApproved`, but admission to the Volunteers team is decoupled from CC review — admission happens on profile-complete + consents-complete regardless of `IsApproved`.

## Data Model

This section owns no tables. Entity detail for the objects Onboarding reads / mutates lives in the owning sections: `docs/sections/Profiles.md` (Profile, User, ConsentCheckStatus), `docs/sections/LegalAndConsent.md` (ConsentRecord), `docs/sections/Governance.md` (Application, BoardVote), `docs/sections/Teams.md` (TeamMember, Volunteers system team).

The only onboarding-specific value type is the narrow `IOnboardingEligibilityQuery` seam used to break the DI cycle between `OnboardingService` and `ProfileService` (see Architecture).

## Routing

Multiple controllers serve this section:

| Controller | Route | Notes |
|------------|-------|-------|
| `OnboardingReviewController` | `GET /OnboardingReview` | Review queue (`PolicyNames.ReviewQueueAccess`) |
| `OnboardingReviewController` | `GET /OnboardingReview/{userId}` | Detail view |
| `OnboardingReviewController` | `POST /OnboardingReview/{userId}/Clear` | CC clear (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| `OnboardingReviewController` | `POST /OnboardingReview/BulkClear` | Bulk clear (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| `OnboardingReviewController` | `POST /OnboardingReview/{userId}/Flag` | CC flag (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| `OnboardingReviewController` | `POST /OnboardingReview/{userId}/Reject` | CC reject (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| `ProfileController` | `POST /Profile/{id}/Admin/Approve` | Manual volunteer override (`PolicyNames.HumanAdminBoardOrAdmin`) |
| `AccountController` | Login/logout/OAuth | Exempt from membership gate; no onboarding-specific routes |
| `MembershipRequiredFilter` | (global filter) | Redirects non-members; profileless → `/Guest`, onboarding → `/Home` |

Board voting moved to Governance: `/Governance/BoardVoting`. Onboarding only consumes `IApplicationDecisionService` for pending-application badges and detail context.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Unauthenticated visitor | Sign up via Google OAuth (or magic link) |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), submit feedback |
| ConsentCoordinator | Clear, flag, or reject signups in the onboarding review queue (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| VolunteerCoordinator | Read-only access to the onboarding review queue (`PolicyNames.ReviewQueueAccess`) — cannot clear, flag, or reject |
| HumanAdmin, Board, Admin | All ConsentCoordinator capabilities, plus manual `ApproveVolunteerAsync` to override a flagged profile (`PolicyNames.HumanAdminBoardOrAdmin`). Board voting on Colaborador/Asociado tier applications is Board+Admin only (`PolicyNames.BoardOrAdmin` / `PolicyNames.BoardOnly`). |

## Invariants

- Onboarding steps: (1) complete profile, (2) consent to all required global legal documents, (3) automatic admission to the Volunteers system team. CC review of the consent check is an independent audit track that runs in parallel — it does not gate admission.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- The ActiveMember status is derived from membership in the Volunteers system team.
- Volunteers admission is `!IsSuspended && ConsentCheckStatus != Flagged && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)`. `Profile.IsApproved` is the CC's audit annotation and is NOT consulted for admission. The `Flagged` and `RejectedAt` exclusions preserve the CC's kick-out levers — `FlagConsentCheckAsync` and `RejectSignupAsync` set those fields before calling `DeprovisionApprovalGatedSystemTeamsAsync`, so the deprovision actually removes the user from Volunteers.
- All admin and coordinator roles bypass the membership gate entirely — they can access the full application regardless of membership status.
- OAuth login checks verified UserEmails, unverified UserEmails, and User.Email before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.
- `OnboardingService` depends only on interfaces (plus `IClock`) — no `DbContext`, `IDbContextFactory`, `DbSet<T>`, `IMemoryCache`, `IFullProfileInvalidator`, or repository. Enforced by `OnboardingArchitectureTests`.
- The DI cycle between `OnboardingService` and `ProfileService` is broken by the narrow `IOnboardingEligibilityQuery` interface (`SetConsentCheckPendingIfEligibleAsync(userId, ct)`). `OnboardingService` implements it; `ProfileService` depends on it. `ConsentService` currently depends on the full `IOnboardingService` (cycle broken via `IServiceProvider` for `IMembershipCalculator` rather than the narrow interface — see ConsentService ctor) — narrowing Consent to `IOnboardingEligibilityQuery` is a follow-up.
- Onboarding can be completed via the legacy linear flow (Profile → Consents) or the `/OnboardingWidget` guided flow (Names → Shifts → Consents). The data and admission rules are identical; the widget reorders the user-facing screens. Active-member admission still fires from `ConsentService.SubmitConsentAsync`'s `SyncVolunteersMembershipForUserAsync` call.

## Negative Access Rules

- VolunteerCoordinator **cannot** clear, flag, or reject in the review queue. They have read-only access only (`PolicyNames.ReviewQueueAccess` lets them view; the Clear/Flag/Reject POST endpoints all require `PolicyNames.ConsentCoordinatorBoardOrAdmin`).
- ConsentCoordinator **cannot** cast Board votes on tier applications, **cannot** finalize tier-application Approve/Reject decisions (Board+Admin only via `GovernanceBoardVotingController.Vote` / `Finalize`), and **cannot** manually `ApproveVolunteerAsync` a flagged profile (HumanAdmin+Board+Admin only via `ProfileController.ApproveVolunteer`, `PolicyNames.HumanAdminBoardOrAdmin`).
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they become active Volunteers.
- Profileless accounts **cannot** access the Home dashboard, City Planning, Budget, Shifts, Governance, or any member-only features. They are redirected to the Guest dashboard. **Exception:** profileless mid-widget users see the priority-shift list rendered inside `/OnboardingWidget` Step 2; direct navigation to `/Shifts` still routes them through the membership filter as today.

## Triggers

- When a human completes their profile and signs all required documents: `OnboardingService.SetConsentCheckPendingIfEligibleAsync` flips `Profile.ConsentCheckStatus` to `Pending` (only if not already approved and `IMembershipCalculator.HasAllRequiredConsentsForTeamAsync(Volunteers)` returns true), and notifies the ConsentCoordinator role. This is the audit/annotation track — admission to Volunteers happens automatically and does not depend on this flow.
- When a legal document is signed: `ConsentService.SubmitConsentAsync` calls `SetConsentCheckPendingIfEligibleAsync` (flipping the consent check to Pending if all consents are now in) and `SyncVolunteersMembershipForUserAsync`, which admits the user to the Volunteers system team if `!IsSuspended && ConsentCheckStatus != Flagged && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)`. `Profile.IsApproved` is not consulted.
- When a profile review is cleared by a CC: `Profile.IsApproved` is set to true and `ConsentCheckStatus = Cleared`. `ISystemTeamSync.SyncVolunteersMembershipForUserAsync` runs but is a no-op for admission if the user is already a Volunteer team member. No email is sent on this audit-track path.
- When a consent check is flagged: `Profile.IsApproved` is set to false, `ConsentCheckStatus = Flagged`, and Volunteers / Colaborador / Asociado memberships are de-provisioned via `DeprovisionApprovalGatedSystemTeamsAsync`. HumanAdmin+Board+Admin must resolve via `ProfileController.ApproveVolunteer` (manual override, `PolicyNames.HumanAdminBoardOrAdmin`).
- When a signup is rejected: `Profile.RejectedAt`, `RejectionReason`, and `RejectedByUserId` are recorded; `IsApproved` is set to false; system team memberships are de-provisioned; a `SignupRejected` email and `ProfileRejected` notification are dispatched. (`Profile` has no `IsRejected` boolean — rejection is detected by `RejectedAt is not null`.)
- When a HumanAdmin+Board+Admin manually `ApproveVolunteerAsync` (override path used after a flag is resolved): `IsApproved` is set to true, Volunteers team sync runs, and a `VolunteerApproved` notification ("Welcome! You have been approved") is dispatched.

## Cross-Section Dependencies

After the nobodies-collective#584 narrowing, `OnboardingService` injects only what the 5 onboarding-proper methods (clear / flag / reject / approve / set-pending) need:

- **Profiles:** `IProfileService` — profile reads, review-queue reads, profile mutations (clear/flag consent check, reject signup, approve volunteer). The Profile caching decorator handles `FullProfile` + nav/notification cache invalidation.
- **Users/Identity:** `IUserService` — user reads (rejection email recipient hydration). Admin-initiated account purge is NOT here — it lives on `IAccountDeletionService`.
- **Governance:** `IApplicationDecisionService` — pending-application lookup (review queue) and approved-tier lookup (consent-check clear path). Board-voting methods are now consumed directly by callers, not via OnboardingService.
- **Teams:** `ISystemTeamSync` — Volunteers / Colaboradors / Asociados system team membership sync.
- **Notifications / Email:** `IEmailService` (`SendSignupRejectedAsync`), `INotificationService` (`VolunteerApproved`, `ProfileRejected`, `ConsentReviewNeeded` dispatch). `INotificationInboxService` moved out with `UnsuspendAsync` (now on `IHumanLifecycleService`).
- **Cross-cutting:** `IMembershipCalculator` (consent-check eligibility + review-queue snapshots), `IHumansMetrics`, `ILogger`.

## Architecture

**Owning services:** `OnboardingService` (intake funnel only after the nobodies-collective#584 narrowing).
**Sibling services in the three-concerns split:** `HumanLifecycleService` (state-machine), `ApplicationDecisionService` (board voting), `AdminDashboardService` (dashboard aggregation), `AccountDeletionService` (cascade — single entry point for `RequestDeletionAsync` and `CancelDeletionAsync`; both `ProfileController` and `GuestController` deletion actions call through it; ticket-hold + 30-day-grace fields written atomically, see `Profiles.md` cascade section).
**Owned tables:** None — orchestrator over Profiles, Legal & Consent, Teams, Governance.
**Status:** (A) Migrated (peterdrier/Humans PR #285 for issue nobodies-collective/Humans#553, 2026-04-22). Three-concerns narrowing complete with nobodies-collective#583 (lifecycle) and nobodies-collective#584 (board voting + admin dashboard).

- `OnboardingService` lives in `src/Humans.Application/Services/Onboarding/OnboardingService.cs` and depends only on interfaces (plus `IClock`).
- `HumanLifecycleService` lives in `src/Humans.Application/Services/HumanLifecycle/HumanLifecycleService.cs` and exposes `IHumanLifecycleService` — the **single entry point** for admin-initiated `SuspendAsync` / `UnsuspendAsync`. Same orchestrator shape as `OnboardingService`: owns no tables, depends only on cross-section service interfaces (`IProfileService`, `INotificationService`, `INotificationInboxService`, `IHumansMetrics`). Suspend writes flow through `IProfileService.SetSuspendedAsync`; unsuspend resolves the user's `AccessSuspended` notifications via `INotificationInboxService.ResolveBySourceAsync`. The bulk grace-period suspension path used by `SuspendNonCompliantMembersJob` continues to use `IProfileService.SuspendForMissingConsentAsync` directly — that path has job-specific notification, audit, and per-team Google-removal side effects that don't fit the per-user lifecycle entry point.
- **Decorator decision — no caching decorator.** Onboarding owns no cached data. Every cache invalidation for an Onboarding-driven write happens inside the owning section's service or decorator (Profile decorator refreshes `_byUserId` after clear/flag/reject/approve/suspend/unsuspend; `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator` fire from the same write path; `IVotingBadgeCacheInvalidator` fires from `IApplicationDecisionService.CastBoardVoteAsync`).
- **Cross-domain navs stripped:** N/A — Onboarding owns no entities.
- **DI-cycle break:** `IOnboardingEligibilityQuery` is the narrow interface `ProfileService` depends on (`IOnboardingService` extends it; `OnboardingService` implements it). `ConsentService` still depends on the full `IOnboardingService` — narrowing it to `IOnboardingEligibilityQuery` is a follow-up. Reviewers should reject any change that widens the interface `ProfileService` depends on.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/OnboardingArchitectureTests.cs` enforces: lives in `Humans.Application.Services.Onboarding`; no `DbContext` / `IDbContextFactory` / `DbSet<T>` ctor parameters; no `IMemoryCache` parameter; no `IFullProfileInvalidator` parameter; no repository parameters; implements `IOnboardingEligibilityQuery`; every ctor parameter is an interface (plus `IClock`).

The owning-section repositories (`IProfileRepository`, `IUserRepository`, `IApplicationRepository`) each grew a handful of methods to serve Onboarding's cross-section read + write needs — see each repository's XML docs for the onboarding-support block.
