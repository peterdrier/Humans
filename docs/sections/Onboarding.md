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
  Onboarding orchestration across Profile/Consent/Governance/Teams and the membership gate — review when OnboardingService or any of its cross-section dependencies change.
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
- The **access gate** is the stored `User.State` (`UserState`): the full application is reachable only when state is `Active` — i.e. the user has entered their legal name. `MembershipRequiredFilter` routes everyone else by state (`Bare` → name entry, `DeletePending` → cancel-deletion screen, `Suspended`/`Rejected`/`Deleted`/`Merged` → the account-status wall). Access is **not** derived from Volunteers-team membership, and roles do not bypass the gate.
- **Profileless accounts** are authenticated users with no Profile record (e.g., ticket holders, newsletter subscribers created by imports). With no legal name they are `UserState.Bare` and are routed to name entry. The Guest dashboard (comms preferences, GDPR export, account deletion) remains for these accounts.
- The **Consent Coordinator review** (`Profile.ConsentCheckStatus`) is a pure audit/annotation track. When all required consents are signed, `ConsentCheckStatus` flips to `Pending` and the human appears in the CC review queue. CC Clear / Flag still flip `Profile.IsApproved` (Clear → true, Flag → false) for the annotation, but they have **no membership or access side-effects** — neither admission nor access depends on `ConsentCheckStatus` or `IsApproved`. Reject (which sets `RejectedAt`) is the only CC action that changes access.

## Data Model

This section owns no tables. Entity detail for the objects Onboarding reads / mutates lives in the owning sections: `docs/sections/Profiles.md` (Profile, User, ConsentCheckStatus), `docs/sections/LegalAndConsent.md` (ConsentRecord), `docs/sections/Governance.md` (Application, BoardVote), `docs/sections/Teams.md` (TeamMember, Volunteers system team).

Onboarding-specific value types: `OnboardingResult`, `BulkOnboardingResult`. The consent-check threshold (`Profile.ConsentCheckStatus → Pending` + Consent Coordinator notification) lives on `IOnboardingService.SetConsentCheckPendingIfEligibleAsync` — a director method invoked by controllers as a **peer call** after `ProfileService.SaveProfileAsync` or `ConsentService.SubmitConsentAsync`. The leaf services never call back into Onboarding (that was the inverted arrow this PR removed).

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
| `AccountController` | Login/logout/OAuth | Exempt from membership gate; no onboarding-specific routes |
| `MembershipRequiredFilter` | (global filter) | Routes by `UserState`: `Bare` → `/OnboardingWidget`, `DeletePending` → `/User/Deletion`, walled states → `/User/Status`, `Active` → app |
| `NameRequiredFilter` | (global filter) | Name-gate: redirects any authenticated user without a real BurnerName to `/OnboardingWidget/Names` |

Board voting moved to Governance: `/Governance/BoardVoting`. Onboarding only consumes `IApplicationDecisionService` for pending-application badges and detail context.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Unauthenticated visitor | Sign up via Google OAuth (or magic link) |
| Authenticated human (pre-approval) | Complete profile, sign legal documents, submit a tier application (optional), submit feedback |
| ConsentCoordinator | Clear, flag, or reject signups in the onboarding review queue (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| VolunteerCoordinator | Read-only access to the onboarding review queue (`PolicyNames.ReviewQueueAccess`) — cannot clear, flag, or reject |
| HumanAdmin, Board, Admin | All ConsentCoordinator capabilities. Board voting on Colaborador/Asociado tier applications is Board+Admin only (`PolicyNames.BoardOrAdmin` / `PolicyNames.BoardOnly`). |

## Invariants

- Onboarding steps: (1) complete profile, (2) consent to all required global legal documents, (3) automatic admission to the Volunteers system team. CC review of the consent check is an independent audit track that runs in parallel — it does not gate admission.
- Volunteer onboarding is never blocked by tier applications — they are separate, parallel paths.
- Access is the stored `User.State`: the full app requires `UserState.Active` (legal name entered). It is NOT derived from Volunteers-team membership — nothing in-app should read Volunteers membership to decide access.
- Volunteers admission is `HasRequiredNameFields && !IsSuspended && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)` — **name + consents**. `Profile.ConsentCheckStatus` (incl. `Flagged`) and `Profile.IsApproved` are NOT consulted. `Suspended` and `RejectedAt` remain the CC's kick-out levers: `RejectSignupAsync` sets `RejectedAt` then calls `DeprovisionApprovalGatedSystemTeamsAsync`, so reject removes the user from Volunteers. `FlagConsentCheckAsync` is annotation-only and does not deprovision.
- Roles do not bypass the `UserState` access gate. Admin and coordinator tools require both `UserState.Active` and the relevant role/policy.
- OAuth login checks verified UserEmails, unverified UserEmails, and User.Email before creating a new account — preventing duplicate accounts when the same email exists on another user in any form.
- `OnboardingService` depends only on interfaces (plus `IClock`) — no `DbContext`, `IDbContextFactory`, `DbSet<T>`, `IMemoryCache`, `IFullProfileInvalidator`, or repository. Enforced by `OnboardingArchitectureTests`.
- **No leaf-to-director callbacks.** Neither `ProfileService` nor `ConsentService` depends on `IOnboardingService`. The consent-check threshold (formerly `IOnboardingEligibilityQuery.SetConsentCheckPendingIfEligibleAsync`, called from inside the leaves) is now `IOnboardingService.SetConsentCheckPendingIfEligibleAsync` invoked as a **peer call from the controllers** (`ConsentController.Submit`, `ProfileController.Edit POST`, `OnboardingWidgetController.Names/Consents`) after the leaf write completes. Director-to-leaf is one-way: `OnboardingService` writes through `IProfileService` during clear/flag/reject/approve and during the threshold check, never the reverse.
- Onboarding can be completed via the legacy linear flow (Profile → Consents) or the `/OnboardingWidget` guided flow (Names → Shifts → Consents). The data and admission rules are identical; the widget reorders the user-facing screens. Volunteers admission is reconciled by `SystemTeamSyncJob` on name + consents — `ConsentService.SubmitConsentAsync` no longer fires a per-user team sync (the side-effect was removed; admission is eventually-consistent via the batch job, and access never depended on it).
- **Name-gate (entry invariant).** `NameRequiredFilter` (global action filter, `src/Humans.Web/Authorization/NameRequiredFilter.cs`) is the single gate that forces any *authenticated* user whose profile has no real `BurnerName` — a Stub profile, or an Active profile with blank required names — to the burner + legal-name form at `OnboardingWidget/Names` before they can reach the rest of the app. It covers OAuth/Google first sign-in, imported contacts hitting the magic-link `ExistingUser` branch, and legacy blank-`BurnerName` accounts (nobodies-collective/Humans#812). It runs strictly *after* authentication and only ever redirects — it **never blocks sign-in**. It keys on the cache-backed `UserInfo.HasRequiredNameFields` (refreshed on profile save), so the gate opens on the next request once the form is submitted. Exempt from the gate: the `Account` and `Language` controllers wholesale, plus the actions `OnboardingWidget/Names`, `Home/Error`, and `Home/Privacy`.

## Negative Access Rules

- VolunteerCoordinator **cannot** clear, flag, or reject in the review queue. They have read-only access only (`PolicyNames.ReviewQueueAccess` lets them view; the Clear/Flag/Reject POST endpoints all require `PolicyNames.ConsentCoordinatorBoardOrAdmin`).
- ConsentCoordinator **cannot** cast Board votes on tier applications and **cannot** finalize tier-application Approve/Reject decisions (Board+Admin only via `GovernanceBoardVotingController.Vote` / `Finalize`).
- Regular humans still onboarding **cannot** access most of the application (teams, shifts, budget, tickets, governance, etc.) until they enter their legal name (`UserState.Active`).
- Profileless accounts **cannot** access the Home dashboard, City Planning, Budget, Shifts, Governance, or any member-only features. They are redirected to the Guest dashboard. **Exception:** profileless mid-widget users see the priority-shift list rendered inside `/OnboardingWidget` Step 2; direct navigation to `/Shifts` still routes them through the membership filter as today.

## Triggers

- When a human completes their profile and signs all required documents: the profile-save controller (`ProfileController.Edit` or `OnboardingWidgetController.Names`) calls `ProfileService.SaveProfileAsync` then peer-calls `OnboardingService.SetConsentCheckPendingIfEligibleAsync`. If the predicate holds (profile eligible for review, no existing `ConsentCheckStatus`, `IMembershipCalculator.HasAllRequiredConsentsForTeamAsync(Volunteers)` returns true), it flips `Profile.ConsentCheckStatus` to `Pending` and notifies the ConsentCoordinator role. This is the audit/annotation track — admission to Volunteers happens automatically and does not depend on this flow.
- When a legal document is signed: the consent-submit controller (`ConsentController.Submit` or `OnboardingWidgetController.Consents`) calls `ConsentService.SubmitConsentAsync` then peer-calls `OnboardingService.SetConsentCheckPendingIfEligibleAsync` (same threshold check from the Consent side). `SubmitConsentAsync` writes the `ConsentRecord` only — it no longer fires a per-user team sync. Volunteers admission (`HasRequiredNameFields && !IsSuspended && RejectedAt is null && HasAllRequiredConsentsForTeam(Volunteers)`) is reconciled by `SystemTeamSyncJob`.
- When a profile review is cleared by a CC: `Profile.IsApproved` is set to true and `ConsentCheckStatus = Cleared`. This is annotation-only — no team sync, no email.
- When a consent check is flagged: `Profile.IsApproved` is set to false and `ConsentCheckStatus = Flagged`. Annotation-only — no de-provisioning. The flag no longer gates admission; it is a record nothing acts on.
- When a signup is rejected: `Profile.RejectedAt`, `RejectionReason`, and `RejectedByUserId` are recorded; `IsApproved` is set to false; system team memberships are de-provisioned (`RejectedAt` is the kick-out lever); a `SignupRejected` email and `ProfileRejected` notification are dispatched. (`Profile` has no `IsRejected` boolean — rejection is detected by `RejectedAt is not null`.)

## Cross-Section Dependencies

After the nobodies-collective#584 narrowing, `OnboardingService` injects only what its onboarding-proper methods (clear / flag / reject / set-pending) need:

- **Profiles:** `IProfileService` — profile reads, review-queue reads, profile mutations (clear/flag consent check, reject signup). The Profile caching decorator handles `FullProfile` + nav/notification cache invalidation.
- **Users/Identity:** `IUserService` — user reads (rejection email recipient hydration). Admin-initiated account purge is NOT here — it lives on `IAccountDeletionService`.
- **Governance:** `IApplicationDecisionService` — pending-application lookup (review queue). Board-voting methods are now consumed directly by callers, not via OnboardingService.
- **Teams:** `ISystemTeamSync` — Volunteers / Colaboradors / Asociados de-provisioning on reject (`DeprovisionApprovalGatedSystemTeamsAsync`). Clear/flag no longer sync.
- **Notifications / Email:** `IEmailService.SendAsync` (with `IEmailMessageFactory.SignupRejected`), `INotificationService` (`ProfileRejected`, `ConsentReviewNeeded` dispatch). `INotificationInboxService` moved out with `UnsuspendAsync` (now on `IHumanLifecycleService`).
- **Cross-cutting:** `IMembershipCalculator` (consent-check eligibility + review-queue snapshots), `ILogger`.

## Architecture

**Owning services:** `OnboardingService` (intake funnel only after the nobodies-collective#584 narrowing).
**Sibling services in the three-concerns split:** `HumanLifecycleService` (state-machine), `ApplicationDecisionService` (board voting), `AdminDashboardService` (dashboard aggregation), `AccountDeletionService` (cascade — single entry point for `RequestDeletionAsync` and `CancelDeletionAsync`; both `ProfileController` and `GuestController` deletion actions call through it; ticket-hold + 30-day-grace fields written atomically, see `Profiles.md` cascade section).
**Owned tables:** None — orchestrator over Profiles, Legal & Consent, Teams, Governance.
**Status:** (A) Migrated (peterdrier/Humans PR #285 for issue nobodies-collective/Humans#553, 2026-04-22). Three-concerns narrowing complete with nobodies-collective#583 (lifecycle) and nobodies-collective#584 (board voting + admin dashboard).

- `OnboardingService` lives in `src/Humans.Application/Services/Onboarding/OnboardingService.cs` and depends only on interfaces (plus `IClock`).
- `HumanLifecycleService` lives in `src/Humans.Application/Services/HumanLifecycle/HumanLifecycleService.cs` and exposes `IHumanLifecycleService` — the **single entry point** for admin-initiated `SuspendAsync` / `UnsuspendAsync`. Same orchestrator shape as `OnboardingService`: owns no tables, depends only on cross-section service interfaces (`IProfileService`, `INotificationService`, `INotificationInboxService`, `IHumansMetrics`). Suspend writes flow through `IProfileService.SetSuspendedAsync`; unsuspend resolves the user's `AccessSuspended` notifications via `INotificationInboxService.ResolveBySourceAsync`. The bulk grace-period suspension path used by `SuspendNonCompliantMembersJob` continues to use `IProfileService.SuspendForMissingConsentAsync` directly — that path has job-specific notification, audit, and per-team Google-removal side effects that don't fit the per-user lifecycle entry point.
- **Decorator decision — no caching decorator.** Onboarding owns no cached data. Every cache invalidation for an Onboarding-driven write happens inside the owning section's service or decorator (Profile decorator refreshes `_byUserId` after clear/flag/reject/approve/suspend/unsuspend; `INavBadgeCacheInvalidator` and `INotificationMeterCacheInvalidator` fire from the same write path; `IVotingBadgeCacheInvalidator` fires from `IApplicationDecisionService.CastBoardVoteAsync`).
- **Cross-domain navs stripped:** N/A — Onboarding owns no entities.
- **DI direction is one-way.** `OnboardingService → IProfileService` (and other leaves) only. No leaf depends on `IOnboardingService`. The historical `IOnboardingEligibilityQuery` narrow-interface band-aid is removed; the threshold check stayed on `IOnboardingService.SetConsentCheckPendingIfEligibleAsync` (it's director-level work — predicates over Profile + Consent + Legal) and is now invoked by the controllers as a peer call to the leaf-service write. Reviewers should reject any new ctor dependency from `ProfileService` / `ConsentService` onto `IOnboardingService` (or any other director) — that's the inversion this PR removed. The cycle guard `tests/Humans.Application.Tests/Services/DependencyCycleResolutionTests.NoCircularConstructorDependencies_AcrossApplicationServices` enforces this.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/OnboardingArchitectureTests.cs` enforces: lives in `Humans.Application.Services.Onboarding`; no `DbContext` / `IDbContextFactory` / `DbSet<T>` ctor parameters; no `IMemoryCache` parameter; no `IFullProfileInvalidator` parameter; no repository parameters; every ctor parameter is an interface (plus `IClock`).

The owning-section repositories (`IUserRepository`, `IApplicationRepository`) each grew a handful of methods to serve Onboarding's cross-section read + write needs — see each repository's XML docs for the onboarding-support block.
