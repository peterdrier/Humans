# Onboarding — Section Invariants

## Concepts

- **Onboarding** is the process a new human goes through to become an active Volunteer: sign up via Google OAuth, complete their profile, consent to all required legal documents, and pass a profile review by a Consent Coordinator. The last two steps can happen in any order.
- The **Membership Gate** restricts most of the application to active Volunteers. Humans still onboarding are limited to their profile, consent, feedback, legal documents, camps (public), and the home dashboard. All admin and coordinator roles bypass this gate entirely.
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). They are redirected to the Guest dashboard instead of the Home dashboard and see a reduced nav: Guest Dashboard, Camps, Teams (public), Legal. They can create a profile to enter the standard onboarding flow.
- The **Profile Review** (consent check) and **Legal Document Signing** are independent, parallel tracks. A Consent Coordinator can clear the profile review before or after legal documents are signed. Admission to the Volunteers team only happens when both are complete.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
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

- **Profiles**: Profile completion is step 1. Consent check status and membership tier live on the profile.
- **Legal & Consent**: Consent to all required global documents is step 2.
- **Teams**: Volunteer activation adds the human to the Volunteers system team.
- **Governance**: Tier applications are optional and independent of Volunteer onboarding.
- **Feedback**: Feedback submission is available during onboarding, before the human is active.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `OnboardingService`
**Owned tables:** None — OnboardingService is an orchestrator that coordinates Profiles, Legal, and Teams services.

## Architecture — Migrated 2026-04-22 (issue #553)

Onboarding is a pure orchestrator and owns no tables. It lives in
`src/Humans.Application/Services/Onboarding/OnboardingService.cs` and composes:

- `IProfileService` — profile reads, review-queue reads, profile mutations (clear/flag consent check, reject signup, approve/suspend/unsuspend). The Profile caching decorator handles FullProfile + nav/notification cache invalidation.
- `IUserService` — user reads, all-user-ids enumeration, language distribution, and the atomic purge flow (`PurgeAsync`).
- `IApplicationDecisionService` — Governance-section reads and writes (pending-application lookup, board voting dashboard/detail, board vote recording, unvoted-count, admin stats, pending-application count).
- `IRoleAssignmentService` — cross-section Board-member resolution (`GetActiveUserIdsInRoleAsync`). Used from within `IApplicationDecisionService.GetBoardVotingDashboardAsync`.
- `ISystemTeamSync` — Volunteers/Colaboradors/Asociados system team membership sync.
- `IMembershipCalculator` — consent-check eligibility + admin-dashboard partition.
- `IEmailService` / `INotificationService` / `INotificationInboxService` / `IOnboardingMetrics` / `ILogger` — cross-cutting concerns. `IOnboardingMetrics` is the Onboarding section's push-model metrics surface (issue nobodies-collective/Humans#580); `IHumansMetrics` no longer carries Record methods.

The **DI cycle between `OnboardingService` and `ProfileService`/`ConsentService`** is broken by extracting the narrow `IOnboardingEligibilityQuery` interface (`SetConsentCheckPendingIfEligibleAsync(userId, ct)`). `OnboardingService` implements it; `ProfileService` and `ConsentService` depend on it instead of the full `IOnboardingService`.

**Target repositories: none.** Onboarding has no owned tables. The owning-section repositories (`IProfileRepository`, `IUserRepository`, `IApplicationRepository`) each grew a handful of methods to serve Onboarding's cross-section read + write needs — see each repository's XML docs for the onboarding-support block.

**Target caching: none.** Onboarding owns no cached data. Every cache invalidation for an Onboarding-driven write happens inside the owning section's service or decorator (Profile decorator refreshes `_byUserId` after clear/flag/reject/approve/suspend/unsuspend, `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator` fire from the same write path, `IVotingBadgeCacheInvalidator` fires from `IApplicationDecisionService.CastBoardVoteAsync`).

### Architecture test

`tests/Humans.Application.Tests/Architecture/OnboardingArchitectureTests.cs` enforces:

- Lives in `Humans.Application.Services.Onboarding` (design-rules §2b).
- No `DbContext` / `IDbContextFactory` / `DbSet<T>` constructor parameters.
- No `IMemoryCache` constructor parameter.
- No `IFullProfileInvalidator` constructor parameter — FullProfile invalidation is owned by `ProfileService` (its decorator).
- No repository constructor parameters — Onboarding owns no tables.
- `OnboardingService` implements `IOnboardingEligibilityQuery`.
- `IOnboardingService` extends `IOnboardingEligibilityQuery`.
- Every constructor parameter is an interface (plus `IClock`).
