# Onboarding — Section Invariants

Pure orchestrator over Profiles, Legal & Consent, Teams, and Governance. Owns no tables.

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, consent to all required legal documents, and pass a profile review by a Consent Coordinator. The last two steps can happen in any order.
- The **Membership Gate** restricts most of the application to active Volunteers. Humans still onboarding are limited to their profile, consent, feedback, legal documents, camps (public), and the home dashboard. All admin and coordinator roles bypass this gate entirely.
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). They are redirected to the Guest dashboard instead of the Home dashboard and see a reduced nav: Guest Dashboard, Camps, Teams (public), Legal. They can create a profile to enter the standard onboarding flow.
- The **Profile Review** (consent check) and **Legal Document Signing** are independent, parallel tracks. A Consent Coordinator can clear the profile review before or after legal documents are signed. Admission to the Volunteers team only happens when both are complete.

## Data Model

This section owns no tables. Entity detail for the objects Onboarding reads / mutates lives in the owning sections: `docs/sections/Profiles.md` (Profile, User, ConsentCheckStatus), `docs/sections/LegalAndConsent.md` (ConsentRecord), `docs/sections/Governance.md` (Application, BoardVote), `docs/sections/Teams.md` (TeamMember, Volunteers system team).

The only onboarding-specific value type is the narrow `IOnboardingEligibilityQuery` seam used to break the DI cycle between `OnboardingService` and `ProfileService`/`ConsentService` (see Architecture).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Unauthenticated visitor | Sign up via Google OAuth (or magic link) |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), submit feedback |
| ConsentCoordinator | Clear or flag consent checks in the onboarding review queue |
| VolunteerCoordinator | Read-only access to the onboarding review queue (cannot clear/flag or reject) |
| Board, Admin | All ConsentCoordinator capabilities. Reject signups. Manage Board voting on tier applications |

## Invariants

- Onboarding steps: (1) complete profile, (2a) consent to all required global legal documents, (2b) profile review by a Consent Coordinator — these two can happen in any order, (3) auto-approval as Volunteer when both 2a and 2b are complete.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- The ActiveMember status is derived from membership in the Volunteers system team.
- All admin and coordinator roles bypass the membership gate entirely — they can access the full application regardless of membership status.
- OAuth login checks verified UserEmails, unverified UserEmails, and User.Email before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.
- `OnboardingService` depends only on interfaces (plus `IClock`) — no `DbContext`, `IDbContextFactory`, `DbSet<T>`, `IMemoryCache`, `IFullProfileInvalidator`, or repository. Enforced by `OnboardingArchitectureTests`.
- The DI cycle between `OnboardingService` and `ProfileService` / `ConsentService` is broken by the narrow `IOnboardingEligibilityQuery` interface (`SetConsentCheckPendingIfEligibleAsync(userId, ct)`). `OnboardingService` implements it; Profile and Consent depend on it instead of the full `IOnboardingService`.

## Negative Access Rules

- VolunteerCoordinator **cannot** clear or flag consent checks, and **cannot** reject signups. They have read-only access to the review queue.
- ConsentCoordinator **cannot** reject signups. Rejection requires Board or Admin.
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they become active Volunteers.
- Profileless accounts **cannot** access the Home dashboard, City Planning, Budget, Shifts, Governance, or any member-only features. They are redirected to the Guest dashboard.

## Triggers

- When a human completes their profile and signs all required documents: their consent check status becomes Pending.
- When a profile review is cleared: the profile is approved. If all legal documents are also signed, the human is added to the Volunteers system team and a welcome email is sent. If documents are still pending, admission happens automatically when the last document is signed.
- When a legal document is signed: the system checks if the profile is also approved. If both conditions are met, the human is added to the Volunteers team.
- When a consent check is flagged: onboarding is blocked. Board or Admin must review.
- When a signup is rejected: the rejection reason and timestamp are recorded on the profile. The human is notified.

## Cross-Section Dependencies

- **Profiles:** `IProfileService` — profile reads, review-queue reads, profile mutations (clear/flag consent check, reject signup, approve/suspend/unsuspend). The Profile caching decorator handles `FullProfile` + nav/notification cache invalidation.
- **Users/Identity:** `IUserService` — user reads, all-user-ids enumeration, language distribution, and the atomic purge flow (`PurgeAsync`).
- **Governance:** `IApplicationDecisionService` — pending-application lookup, board voting dashboard/detail, board vote recording, unvoted-count, admin stats, pending-application count.
- **Auth:** `IRoleAssignmentService` — cross-section Board-member resolution (`GetActiveUserIdsInRoleAsync`). Used from within `IApplicationDecisionService.GetBoardVotingDashboardAsync`.
- **Teams:** `ISystemTeamSync` — Volunteers / Colaboradors / Asociados system team membership sync.
- **Notifications / Email:** `IEmailService`, `INotificationService`, `INotificationInboxService` — welcome emails, onboarding notifications.
- **Cross-cutting:** `IMembershipCalculator` (consent-check eligibility + admin-dashboard partition), `IHumansMetrics`, `ILogger`.

## Architecture

**Owning services:** `OnboardingService`
**Owned tables:** None — orchestrator over Profiles, Legal & Consent, Teams, Governance.
**Status:** (A) Migrated (peterdrier/Humans PR #285 for issue nobodies-collective/Humans#553, 2026-04-22).

- `OnboardingService` lives in `src/Humans.Application/Services/Onboarding/OnboardingService.cs` and depends only on interfaces (plus `IClock`).
- **Decorator decision — no caching decorator.** Onboarding owns no cached data. Every cache invalidation for an Onboarding-driven write happens inside the owning section's service or decorator (Profile decorator refreshes `_byUserId` after clear/flag/reject/approve/suspend/unsuspend; `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator` fire from the same write path; `IVotingBadgeCacheInvalidator` fires from `IApplicationDecisionService.CastBoardVoteAsync`).
- **Cross-domain navs stripped:** N/A — Onboarding owns no entities.
- **DI-cycle break:** `IOnboardingEligibilityQuery` is the narrow interface Profile and Consent depend on. `IOnboardingService` extends it; `OnboardingService` implements it. Reviewers should reject any change that widens the interface Profile / Consent depend on.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/OnboardingArchitectureTests.cs` enforces: lives in `Humans.Application.Services.Onboarding`; no `DbContext` / `IDbContextFactory` / `DbSet<T>` ctor parameters; no `IMemoryCache` parameter; no `IFullProfileInvalidator` parameter; no repository parameters; implements `IOnboardingEligibilityQuery`; every ctor parameter is an interface (plus `IClock`).
- **Metrics**: `humans.volunteers_approved_total`, `humans.members_suspended_total`. Registered by `OnboardingService` (counters). Note: `humans.members_suspended_total` is also incremented by `SuspendNonCompliantMembersJob` for the auto-suspend code path.

The owning-section repositories (`IProfileRepository`, `IUserRepository`, `IApplicationRepository`) each grew a handful of methods to serve Onboarding's cross-section read + write needs — see each repository's XML docs for the onboarding-support block.
