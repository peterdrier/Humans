<!-- freshness:triggers
  src/Humans.Application/Services/Onboarding/**
  src/Humans.Application/Services/Profile/ProfileService.cs
  src/Humans.Application/Services/Users/UserService.cs
  src/Humans.Application/Services/Users/AccountProvisioningService.cs
  src/Humans.Application/Services/Consent/**
  src/Humans.Application/Services/Governance/ApplicationDecisionService.cs
  src/Humans.Application/Services/Teams/TeamService.cs
  src/Humans.Web/Controllers/OnboardingReviewController.cs
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
-->
<!-- freshness:flag-on-change
  Onboarding orchestration across Profile/Consent/Governance/Teams, membership gate, and IOnboardingEligibilityQuery seam — review when OnboardingService or any of its cross-section dependencies change.
-->

# Onboarding — Section Invariants

Pure orchestrator over Profiles, Legal & Consent, Teams, and Governance. Owns no tables.

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, and consent to all required legal documents. Once both are done, admission to the Volunteers team is automatic — no Consent Coordinator approval is required for entry.
- The **Membership Gate** restricts most of the application to active Volunteers. Humans still onboarding are limited to their profile, consent, feedback, legal documents, camps (public), and the home dashboard. All admin and coordinator roles bypass this gate entirely.
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). They are redirected to the Guest dashboard instead of the Home dashboard and see a reduced nav: Guest Dashboard, Camps, Teams (public), Calendar, Legal. They can create a profile to enter the standard onboarding flow.
- The **Consent Coordinator review** (`Profile.ConsentCheckStatus`) is an audit/annotation track that runs in parallel with admission. When all required consents are signed, `ConsentCheckStatus` flips to `Pending` and the human appears in the CC review queue. CC actions (Clear / Flag / Reject) maintain the audit annotation and still flip `Profile.IsApproved`, but admission to the Volunteers team is decoupled from CC review — admission happens on profile-complete + consents-complete regardless of `IsApproved`.

## Data Model

This section owns no tables. Entity detail for the objects Onboarding reads / mutates lives in the owning sections: `docs/sections/Profiles.md` (Profile, User, ConsentCheckStatus), `docs/sections/LegalAndConsent.md` (ConsentRecord), `docs/sections/Governance.md` (Application, BoardVote), `docs/sections/Teams.md` (TeamMember, Volunteers system team).

The only onboarding-specific value type is the narrow `IOnboardingEligibilityQuery` seam used to break the DI cycle between `OnboardingService` and `ProfileService` (see Architecture).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Unauthenticated visitor | Sign up via Google OAuth (or magic link) |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), submit feedback |
| ConsentCoordinator | Clear, flag, or reject signups in the onboarding review queue (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| VolunteerCoordinator | Read-only access to the onboarding review queue (`PolicyNames.ReviewQueueAccess`) — cannot clear, flag, or reject |
| Board, Admin | All ConsentCoordinator capabilities, plus manual `ApproveVolunteerAsync` to override a flagged profile, and Board voting on Colaborador/Asociado tier applications |

## Invariants

- Onboarding steps: (1) complete profile, (2) consent to all required global legal documents, (3) automatic admission to the Volunteers system team. CC review of the consent check is an independent audit track that runs in parallel — it does not gate admission.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- The ActiveMember status is derived from membership in the Volunteers system team.
- Volunteers admission is `!IsSuspended && ConsentCheckStatus != Flagged && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)`. `Profile.IsApproved` is the CC's audit annotation and is NOT consulted for admission. The `Flagged` and `RejectedAt` exclusions preserve the CC's kick-out levers — `FlagConsentCheckAsync` and `RejectSignupAsync` set those fields before calling `DeprovisionApprovalGatedSystemTeamsAsync`, so the deprovision actually removes the user from Volunteers.
- All admin and coordinator roles bypass the membership gate entirely — they can access the full application regardless of membership status.
- OAuth login checks verified UserEmails, unverified UserEmails, and User.Email before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.
- `OnboardingService` depends only on interfaces (plus `IClock`) — no `DbContext`, `IDbContextFactory`, `DbSet<T>`, `IMemoryCache`, `IFullProfileInvalidator`, or repository. Enforced by `OnboardingArchitectureTests`.
- The DI cycle between `OnboardingService` and `ProfileService` is broken by the narrow `IOnboardingEligibilityQuery` interface (`SetConsentCheckPendingIfEligibleAsync(userId, ct)`). `OnboardingService` implements it; `ProfileService` depends on it. `ConsentService` currently depends on the full `IOnboardingService` (cycle broken via `IServiceProvider` for `IMembershipCalculator` rather than the narrow interface — see ConsentService ctor) — narrowing Consent to `IOnboardingEligibilityQuery` is a follow-up.

## Negative Access Rules

- VolunteerCoordinator **cannot** clear, flag, or reject in the review queue. They have read-only access only (`PolicyNames.ReviewQueueAccess` lets them view; the Clear/Flag/Reject POST endpoints all require `PolicyNames.ConsentCoordinatorBoardOrAdmin`).
- ConsentCoordinator **cannot** cast Board votes on tier applications, **cannot** finalize tier-application Approve/Reject decisions (Board+Admin only via `OnboardingReviewController.Vote` / `Finalize`), and **cannot** manually `ApproveVolunteerAsync` a flagged profile (Board+Admin only via `ProfileController.ApproveVolunteer`).
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they become active Volunteers.
- Profileless accounts **cannot** access the Home dashboard, City Planning, Budget, Shifts, Governance, or any member-only features. They are redirected to the Guest dashboard.

## Triggers

- When a human completes their profile and signs all required documents: `OnboardingService.SetConsentCheckPendingIfEligibleAsync` flips `Profile.ConsentCheckStatus` to `Pending` (only if not already approved and `IMembershipCalculator.HasAllRequiredConsentsForTeamAsync(Volunteers)` returns true), and notifies the ConsentCoordinator role. This is the audit/annotation track — admission to Volunteers happens automatically and does not depend on this flow.
- When a legal document is signed: `ConsentService.SubmitConsentAsync` calls `SetConsentCheckPendingIfEligibleAsync` (flipping the consent check to Pending if all consents are now in) and `SyncVolunteersMembershipForUserAsync`, which admits the user to the Volunteers system team if `!IsSuspended && ConsentCheckStatus != Flagged && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)`. `Profile.IsApproved` is not consulted.
- When a profile review is cleared by a CC: `Profile.IsApproved` is set to true and `ConsentCheckStatus = Cleared`. `ISystemTeamSync.SyncVolunteersMembershipForUserAsync` runs but is a no-op for admission if the user is already a Volunteer team member. No email is sent on this audit-track path.
- When a consent check is flagged: `Profile.IsApproved` is set to false, `ConsentCheckStatus = Flagged`, and Volunteers / Colaborador / Asociado memberships are de-provisioned via `DeprovisionApprovalGatedSystemTeamsAsync`. Board or Admin must resolve via `ProfileController.ApproveVolunteer` (manual override).
- When a signup is rejected: `Profile.RejectedAt`, `RejectionReason`, and `RejectedByUserId` are recorded; `IsApproved` is set to false; system team memberships are de-provisioned; a `SignupRejected` email and `ProfileRejected` notification are dispatched. (`Profile` has no `IsRejected` boolean — rejection is detected by `RejectedAt is not null`.)
- When a Board+Admin manually `ApproveVolunteerAsync` (override path used after a flag is resolved): `IsApproved` is set to true, Volunteers team sync runs, and a `VolunteerApproved` notification ("Welcome! You have been approved") is dispatched.

## Cross-Section Dependencies

- **Profiles:** `IProfileService` — profile reads, review-queue reads, profile mutations (clear/flag consent check, reject signup, approve/suspend/unsuspend). The Profile caching decorator handles `FullProfile` + nav/notification cache invalidation.
- **Users/Identity:** `IUserService` — user reads, all-user-ids enumeration, language distribution. Admin-initiated account purge is NOT here — it lives on `IAccountDeletionService` (see Users/Profiles for the deletion lifecycle).
- **Governance:** `IApplicationDecisionService` — pending-application lookup, board voting dashboard/detail, board vote recording, unvoted-count, admin stats, pending-application count.
- **Auth:** `IRoleAssignmentService` — cross-section Board-member resolution (`GetActiveUserIdsInRoleAsync`). Used from within `IApplicationDecisionService.GetBoardVotingDashboardAsync`.
- **Teams:** `ISystemTeamSync` — Volunteers / Colaboradors / Asociados system team membership sync.
- **Notifications / Email:** `IEmailService` (`SendSignupRejectedAsync`), `INotificationService` (`VolunteerApproved`, `ProfileRejected`, `AccessSuspended`, `ConsentReviewNeeded` dispatch), `INotificationInboxService` (resolve `AccessSuspended` on unsuspend / consents complete).
- **Cross-cutting:** `IMembershipCalculator` (consent-check eligibility + admin-dashboard partition), `IHumansMetrics`, `ILogger`.

## Architecture

**Owning services:** `OnboardingService`
**Owned tables:** None — orchestrator over Profiles, Legal & Consent, Teams, Governance.
**Status:** (A) Migrated (peterdrier/Humans PR #285 for issue nobodies-collective/Humans#553, 2026-04-22).

- `OnboardingService` lives in `src/Humans.Application/Services/Onboarding/OnboardingService.cs` and depends only on interfaces (plus `IClock`).
- **Decorator decision — no caching decorator.** Onboarding owns no cached data. Every cache invalidation for an Onboarding-driven write happens inside the owning section's service or decorator (Profile decorator refreshes `_byUserId` after clear/flag/reject/approve/suspend/unsuspend; `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator` fire from the same write path; `IVotingBadgeCacheInvalidator` fires from `IApplicationDecisionService.CastBoardVoteAsync`).
- **Cross-domain navs stripped:** N/A — Onboarding owns no entities.
- **DI-cycle break:** `IOnboardingEligibilityQuery` is the narrow interface `ProfileService` depends on (`IOnboardingService` extends it; `OnboardingService` implements it). `ConsentService` still depends on the full `IOnboardingService` — narrowing it to `IOnboardingEligibilityQuery` is a follow-up. Reviewers should reject any change that widens the interface `ProfileService` depends on.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/OnboardingArchitectureTests.cs` enforces: lives in `Humans.Application.Services.Onboarding`; no `DbContext` / `IDbContextFactory` / `DbSet<T>` ctor parameters; no `IMemoryCache` parameter; no `IFullProfileInvalidator` parameter; no repository parameters; implements `IOnboardingEligibilityQuery`; every ctor parameter is an interface (plus `IClock`).

The owning-section repositories (`IProfileRepository`, `IUserRepository`, `IApplicationRepository`) each grew a handful of methods to serve Onboarding's cross-section read + write needs — see each repository's XML docs for the onboarding-support block.
