# Auth — Section Invariants

Role assignments (temporal), magic-link login/signup, claims transformation.

## Concepts

- **Role Assignment** is a temporal assignment of a role name (e.g. `Admin`, `Board`, `TeamsAdmin`, `HumanAdmin`, `ConsentCoordinator`, …) to a human, with a `ValidFrom` and optional `ValidTo`. `RoleAssignmentClaimsTransformation` reads active assignments on every request (cached 60s) and projects them into ASP.NET claims so `[Authorize(Roles = ...)]` and `User.IsInRole()` work.
- **Magic Link Auth** is the email-based login/signup flow. Users enter an email address; the system sends a data-protected link. Login links verify a user id; signup links carry the email address. Tokens are single-use (replay-protected 15-minute cache) and signup sends are rate-limited to 1 per 60 seconds per address.

## Data Model

### RoleAssignment

**Table:** `role_assignments`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User — **FK only**, `[Obsolete]`-marked nav |
| CreatedByUserId | Guid? | FK → User — **FK only**, `[Obsolete]`-marked nav |
| Role | string | Role name (see constants below) |
| ValidFrom | Instant | When the role became active |
| ValidTo | Instant? | When the role ended (null for currently active) |
| CreatedAt | Instant | When the row was written |

Cross-domain navs `RoleAssignment.User` and `RoleAssignment.CreatedByUser` are `[Obsolete]`-marked. The repository does not `.Include()` them; display data stitches via `IUserService.GetByIdsAsync`. Pending full entity strip — tracked with the User nav-strip in design-rules §15i.

### RoleNames (constants)

| Constant | Value | Purpose |
|----------|-------|---------|
| Admin | `"Admin"` | Full system access |
| Board | `"Board"` | Elevated permissions, votes on tier applications |
| ConsentCoordinator | `"ConsentCoordinator"` | Safety gate for onboarding consent checks |
| VolunteerCoordinator | `"VolunteerCoordinator"` | Facilitation contact for onboarding |
| FinanceAdmin | `"FinanceAdmin"` | Full access to Finance section (budgets, audit log) |

Additional role names referenced across the codebase: `HumanAdmin`, `TeamsAdmin`, `CampAdmin`, `FeedbackAdmin`, `NoInfoAdmin`, `TicketAdmin`.

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
- **On magic-link login success:** `User.MagicLinkSentAt` is stamped so subsequent requests within 60s are rate-limited.

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetByIdsAsync` — display names for assignee/creator stitched in memory (design-rules §6b). `IUserEmailService.FindVerifiedEmailWithUserAsync` — verified email → owning user for magic-link login.
- **Teams:** `ISystemTeamSync.SyncBoardTeamAsync` — Board system team's membership mirrors current `Board` role assignments.
- **Governance:** Tier applications and board voting flows are a separate concern. Governance concerns association-level affairs; Auth concerns who-has-what-role within the running system. `role_assignments` is owned by Auth, not Governance.
- **Notifications:** `INotificationService` — best-effort in-app notifications on role changes.

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
