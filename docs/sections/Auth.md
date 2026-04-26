<!-- freshness:triggers
  src/Humans.Application/Services/Auth/**
  src/Humans.Domain/Entities/RoleAssignment.cs
  src/Humans.Domain/Constants/RoleNames.cs
  src/Humans.Domain/Constants/RoleGroups.cs
  src/Humans.Infrastructure/Data/Configurations/Auth/**
  src/Humans.Infrastructure/Repositories/RoleAssignmentRepository.cs
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Web/Authorization/PolicyNames.cs
  src/Humans.Web/Authorization/RoleChecks.cs
  src/Humans.Web/Authorization/Requirements/**
  src/Humans.Application/Authorization/**
-->
<!-- freshness:flag-on-change
  Role-assignment temporal invariants, magic-link rate-limit/replay rules, and role-name constants — review when Auth services, role constants, or claims transformation change.
-->

# Auth — Section Invariants

Role assignments (temporal), magic-link login/signup, claims transformation.

## Concepts

- **Role Assignment** is a temporal assignment of a role name (e.g. `Admin`, `Board`, `TeamsAdmin`, `HumanAdmin`, `ConsentCoordinator`, …) to a human, with a `ValidFrom` and optional `ValidTo`. `RoleAssignmentClaimsTransformation` reads active assignments on every request (cached 60 s per user) and projects them into ASP.NET role claims so `[Authorize(Roles = ...)]`, `[Authorize(Policy = …)]`, and `User.IsInRole()` work. The same transformation also emits the `ActiveMember` claim (set when the user is a current `Volunteers` system-team member and is not suspended) and the `HasProfile` claim.
- **Magic Link Auth** is the email-based login/signup flow handled by `MagicLinkService` + `AccountController`. Users enter an email address; the system sends a Data-Protection-token-protected link. Login links verify a user id; signup links carry the email address. Tokens are single-use (replay-protected 15-minute cache) and signup sends are rate-limited to 1 per 60 seconds per address; login-link sends are rate-limited to 1 per 60 seconds per user via `User.MagicLinkSentAt`. Token generation/validation/URL building sits behind `IMagicLinkUrlBuilder` and rate-limit/replay state behind `IMagicLinkRateLimiter` so `MagicLinkService` itself depends on no `HumansDbContext`, `IDataProtectionProvider`, or `IMemoryCache`.
- **Membership gating** — `MembershipRequiredFilter` (a global `IAsyncActionFilter`) blocks most controllers for authenticated users who lack the `ActiveMember` claim, redirecting non-active humans to `Home/Index` (if they have a profile) or `Guest/Index` (profileless). It exempts onboarding controllers (`Home`, `Account`, `Application`, `Consent`, `Profile`, `OnboardingReview`, `Guest`, `Legal`, `Notification`, `Feedback`, `FeedbackApi`, `Camp`, `CampApi`, `CampAdmin`, `Admin`, `Board`, `Contacts`, `Language`), endpoints carrying `[AllowAnonymous]`, and any caller for whom `RoleChecks.BypassesMembershipRequirement` returns true (Admin / Board / TeamsAdmin / CampAdmin / HumanAdmin / TicketAdmin / NoInfoAdmin / FinanceAdmin / ConsentCoordinator / VolunteerCoordinator).

### AccountController routes

All under `/Account`. Anti-forgery on every POST.

| Verb | Action | Auth | Purpose |
|------|--------|------|---------|
| GET | `Login` | anonymous | Renders login page (Google OAuth + magic-link request) |
| POST | `ExternalLogin` | anonymous | Initiates external (Google) OAuth challenge |
| GET | `ExternalLoginCallback` | anonymous | Handles OAuth return: signs in, links to existing account by verified email, recovers a locked-out source account by re-linking to an active one, or creates a new account |
| POST | `MagicLinkRequest` | anonymous | Sends a magic link (login or signup) — always shows "check your email" (no enumeration) |
| GET | `MagicLinkConfirm` | anonymous | Landing page that defers token consumption to a user-clicked POST (defeats email security scanners) |
| POST | `MagicLink` | anonymous | Verifies login token, signs the user in, stamps `LastLoginAt` |
| GET | `MagicLinkSignup` | anonymous | Verifies signup token and renders the complete-signup form |
| POST | `CompleteSignup` | anonymous | Re-verifies signup token, creates the `User` + `UserEmail`, signs the new user in (double-click safe) |
| POST | `Logout` | (any) | Sign-out and redirect to `Home/Index` |
| GET | `AccessDenied` | (any) | Renders the access-denied page |

## Data Model

### RoleAssignment

**Table:** `role_assignments`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User — paired with `[Obsolete]`-marked `User` nav (stitched in memory, never `.Include()`d) |
| CreatedByUserId | Guid | FK → User (required) — paired with `[Obsolete]`-marked `CreatedByUser` nav; `OnDelete(DeleteBehavior.Restrict)` |
| RoleName | string (max 256) | Role name (see constants below) |
| ValidFrom | Instant | When the role became active |
| ValidTo | Instant? | When the role ended (null for currently active) |
| Notes | string? (max 2000) | Free-text reason recorded at assign / end time (end notes are appended as `Ended: …`) |
| CreatedAt | Instant | When the row was written |

**Constraints / indexes:** `CK_role_assignments_valid_window` (`ValidTo IS NULL OR ValidTo > ValidFrom`); indexes on `UserId`, `RoleName`, `(UserId, RoleName, ValidFrom)`, plus a partial index on `(UserId, RoleName)` filtered to `ValidTo IS NULL` for active-row lookups.

Cross-domain navs `RoleAssignment.User` and `RoleAssignment.CreatedByUser` are `[Obsolete]`-marked. The repository does not `.Include()` them; display data stitches via `IUserService.GetByIdsAsync`. Pending full entity strip — tracked with the User nav-strip in design-rules §15i.

### RoleNames (constants)

Defined in `src/Humans.Domain/Constants/RoleNames.cs`.

| Constant | Value | Purpose |
|----------|-------|---------|
| Admin | `"Admin"` | Full system access — global superset |
| Board | `"Board"` | Elevated permissions, votes on tier applications |
| HumanAdmin | `"HumanAdmin"` | Approve/suspend/reject humans, provision @nobodies.team email accounts, manage role assignments |
| TeamsAdmin | `"TeamsAdmin"` | Manage all teams, approve membership, assign leads, configure Google Group prefixes system-wide |
| CampAdmin | `"CampAdmin"` | Manage camps, approve/reject season registrations, configure camp settings system-wide |
| TicketAdmin | `"TicketAdmin"` | Manage ticket vendor integration, trigger syncs, generate discount codes, export ticket data |
| FeedbackAdmin | `"FeedbackAdmin"` | View all feedback reports, respond to reporters, manage feedback status, link GitHub issues |
| FinanceAdmin | `"FinanceAdmin"` | Full access to Finance section (budgets, audit log) |
| NoInfoAdmin | `"NoInfoAdmin"` | Approve/voluntell shift signups (cannot create/edit shifts); access to volunteer-event-profile medical data |
| ConsentCoordinator | `"ConsentCoordinator"` | Safety gate for onboarding consent checks; bypasses MembershipRequiredFilter |
| VolunteerCoordinator | `"VolunteerCoordinator"` | Facilitation contact for onboarding; read-only access to onboarding review queue; bypasses MembershipRequiredFilter |

`RoleNames.BoardManageableRoles` (a `HashSet<string>` on the same constants class) enumerates the roles that Board and HumanAdmin are permitted to assign / end; `Admin` is **not** in that set, so only an existing Admin can assign the Admin role (enforced by both `RoleAssignmentAuthorizationHandler` and `RoleChecks.GetAssignableRoles`).

### Authorization-policy layout (RoleGroups → PolicyNames)

The auth surface is mid-transition per `docs/plans/2026-04-03-first-class-authorization-transition.md`:

- **Phase 1 — coarse policies** (`Humans.Web/Authorization/PolicyNames.cs` + `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`): **complete.** Controllers use `[Authorize(Policy = PolicyNames.X)]` and views use the `authorize-policy` TagHelper. The legacy `Humans.Domain.Constants.RoleGroups` constants still exist in source but have **zero in-source call sites** — kept around as constants only and slated for deletion.
- **Phase 2 — resource-based authorization (first vertical slices):** shipped. Production handlers in place: `TeamAuthorizationHandler` (resource: `Team`), `CampAuthorizationHandler` (resource: `Camp`), `BudgetAuthorizationHandler` (resource: `BudgetCategory`/department-scoped budget edits), and `RoleAssignmentAuthorizationHandler` (resource: target role-name string; lives in `Humans.Application.Authorization` because it has no scoped dependencies). Composite custom handlers (`IsActiveMemberHandler`, `ActiveMemberOrShiftAccessHandler`, `HumanAdminOnlyHandler`) are also registered.
- **Phase 3 — service-layer authorization enforcement:** **cancelled / tombstoned.** Superseded by `docs/architecture/design-rules.md §11`: services are auth-free; controllers call `IAuthorizationService.AuthorizeAsync` and pass an `isPrivileged` boolean (or equivalent) into the service.

### Magic-link state

`MagicLinkService` owns no tables. Its only persistent state is `User.MagicLinkSentAt` (mutated via `UserManager<User>`). Replay protection and signup rate-limit state sit behind `IMagicLinkRateLimiter` (Infrastructure-side `IMemoryCache`); Data Protection token generation/validation and URL construction sit behind `IMagicLinkUrlBuilder`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Request a magic-link login or signup |
| Admin, Board, HumanAdmin | Assign / end role assignments (scope depends on role — see Governance invariants). Admin is the only role that can assign the Admin role. |

## Invariants

- Role assignments are temporal: a new assignment is always added (never "resurrected" by clearing `ValidTo`). `RevokeAllActiveAsync(userId)` stamps `ValidTo = now` on every currently-active row in one write.
- `RoleAssignmentClaimsTransformation` reads only `role_assignments` (not legacy `AspNetUserRoles`). The transformation's cache is keyed per user and invalidated by `IRoleAssignmentClaimsCacheInvalidator.Invalidate(userId)` on every Auth write.
- Assigning the `Board` role triggers a `SyncBoardTeamAsync` side-effect so the Board system team stays aligned with the current Board composition.
- Magic-link login tokens are single-use: once a token reaches `VerifyLoginTokenAsync` successfully, it is reserved in the replay cache for the remainder of its 15-minute lifetime and cannot be used again.
- Magic-link signup sends are rate-limited to 1 email per 60 seconds per target address. The reservation is released on downstream send failure so the caller can retry.
- Verified-email resolution goes through `IUserEmailService.FindVerifiedEmailWithUserAsync` — never through raw `DbContext.UserEmails` queries. Unverified rows are ignored (their auto-link would bypass the merge-request review gate).

## Negative Access Rules

- Magic links are sent silently when the email is unknown or rate-limited — no account enumeration. The caller always sees the same "check your email" UI.
- `RoleAssignmentService.AssignRoleAsync` **cannot** create a new assignment when an overlapping active one already exists for the same (user, role).
- `EndRoleAsync` **cannot** end an assignment that is already ended or not yet active.

## Triggers

- **On role assigned or ended:** `IRoleAssignmentClaimsCacheInvalidator.Invalidate(userId)` clears the per-user claims cache so the next request re-derives claims; `INavBadgeCacheInvalidator.Invalidate()` refreshes the top-nav counters; an in-app `RoleAssignmentChanged` notification is sent to the affected human (best-effort — failures are logged, not propagated); if the role is `Board`, `ISystemTeamSync.SyncBoardTeamAsync` runs.
- **On `RevokeAllActiveAsync(userId)`:** every currently-active row for the user has `ValidTo` stamped to now in one batch update, then `IRoleAssignmentClaimsCacheInvalidator.Invalidate(userId)` is called. (Nav-badge counters and per-row notifications are intentionally **not** dispatched on this bulk path — it's a privacy/account-deletion code path, not an admin role-management action.)
- **On magic-link login email sent:** `User.MagicLinkSentAt` is stamped (in `SendLoginLinkAsync`, before the cache-backed cooldown applies) so subsequent send requests within 60 seconds for the same user are silently no-op'd. Signup-link sends use a separate `IMagicLinkRateLimiter.TryReserveSignupSendAsync` reservation per email address (released on send failure so the caller can retry).
- **On magic-link login token verified:** the token is reserved in the replay cache for the remainder of its 15-minute lifetime via `IMagicLinkRateLimiter.TryConsumeLoginTokenAsync` so it cannot be redeemed twice. `AccountController.MagicLink` stamps `User.LastLoginAt` after the verified sign-in.

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetByIdsAsync` — display names for assignee/creator stitched in memory (design-rules §6b). `IUserEmailService.FindVerifiedEmailWithUserAsync` — verified email → owning user for magic-link login.
- **Teams:** `ISystemTeamSync.SyncBoardTeamAsync` — Board system team's membership mirrors current `Board` role assignments.
- **Governance:** Tier applications and board voting flows are a separate concern. Governance concerns association-level affairs; Auth concerns who-has-what-role within the running system. `role_assignments` is owned by Auth, not Governance.
- **Notifications:** `INotificationEmitter` (the narrow per-user dispatch surface — `INotificationService` extends it but Auth only needs the emitter) — best-effort in-app notifications on role changes.

## Architecture

**Owning services:** `RoleAssignmentService`, `MagicLinkService`
**Owned tables:** `role_assignments`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#551, 2026-04-22).

- `RoleAssignmentService` lives in `Humans.Application/Services/Auth/` and depends only on Application-layer abstractions. It never imports `Microsoft.EntityFrameworkCore`.
- `IRoleAssignmentRepository` (impl `Humans.Infrastructure/Repositories/RoleAssignmentRepository.cs`) owns the SQL surface for `role_assignments`. Uses the Scoped + `HumansDbContext` pattern (mirrors `ApplicationRepository`) because Auth writes are rare.
- **Decorator decision — no caching decorator.** Role assignments are low-traffic (handful of admin-driven writes per month, few reads per day) and magic links are throwaway; a dict-backed decorator isn't warranted (same rationale as Governance / User / Feedback).
- **Cross-domain navs `[Obsolete]`-marked:** `RoleAssignment.User`, `RoleAssignment.CreatedByUser`. The repository does not `.Include()` them; the service stitches display data in memory from `IUserService.GetByIdsAsync` (§6b). Controllers (`AboutController`, `GovernanceController`, `ProfileController`) and two daily-digest jobs (`SendAdminDailyDigestJob`, `SendBoardDailyDigestJob`) continue to read `ra.User.DisplayName` / `ra.CreatedByUser.DisplayName` under `#pragma warning disable CS0618` until the broader User-entity nav strip lands.
- `MagicLinkService` owns no tables. Its persistent state is `User.MagicLinkSentAt`, mutated via `UserManager<User>`. Verified-email lookup goes through `IUserEmailService.FindVerifiedEmailWithUserAsync`. Data-Protection token generation/validation and URL construction sit behind `IMagicLinkUrlBuilder`; replay-protection and signup rate-limit state sit behind `IMagicLinkRateLimiter`. That arrangement keeps `MagicLinkService` free of `HumansDbContext`, `EmailSettings`, `IDataProtectionProvider`, and `IMemoryCache`.
- **Cross-cutting invalidation** routes through `INavBadgeCacheInvalidator` (top-nav counters) and `IRoleAssignmentClaimsCacheInvalidator` (per-user claims transform cache) — never raw `IMemoryCache` calls.

### Touch-and-clean guidance

- Do **not** reintroduce `.Include(ra => ra.User | ra.CreatedByUser)` anywhere. New read paths go through `IRoleAssignmentRepository` (or extend the repository with a new narrowly-shaped query) and stitch display data in `RoleAssignmentService` via `IUserService`.
- Do **not** inject `HumansDbContext` into `RoleAssignmentService` or `MagicLinkService`.
- Do **not** inject `IMemoryCache` into `MagicLinkService`. Use `IMagicLinkRateLimiter` (or add a new cross-cutting abstraction) for cache-backed state.
- Jobs and services outside Auth that still read `DbContext.RoleAssignments` directly (`SendAdminDailyDigestJob`, `SendBoardDailyDigestJob`, `OnboardingService`, `SystemTeamSyncJob`, `NotificationService`, `MembershipCalculator`, `DuplicateAccountService`) are tracked §15h touch-and-clean follow-ups — migrate them to call `IRoleAssignmentService` when you next touch them.
