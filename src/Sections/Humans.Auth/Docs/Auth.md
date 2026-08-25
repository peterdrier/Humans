<!-- freshness:triggers
  src/Sections/Humans.Auth/**
  src/Sections/Humans.Auth.Contracts/**
  src/Humans.Base/Constants/RoleNames.cs
  src/Humans.Base/Constants/RoleGroups.cs
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Base/Authorization/PolicyNames.cs
  src/Humans.Base/Authorization/RoleChecks.cs
  src/Sections/**/Authorization/**/*.cs
  src/Humans.Base/Models/AccessMatrixDefinitions.cs
  src/Humans.Base/ViewComponents/AccessMatrixViewComponent.cs
-->
<!-- freshness:flag-on-change
  Role-assignment temporal invariants, magic-link rate-limit/replay rules, role-name constants, and the access-matrix mechanism (§"Access Matrix UI" — AccessMatrixViewComponent over static AccessMatrixDefinitions data, no DB table) — review when Auth services, role constants, claims transformation, or the access-matrix component/source change.
-->

# Auth — Section Invariants

Role assignments (temporal), magic-link login/signup, claims transformation.

## Concepts

- **Role Assignment** is a temporal assignment of a role name (e.g. `Admin`, `Board`, `TeamsAdmin`, `HumanAdmin`, `ConsentCoordinator`, …) to a human, with a `ValidFrom` and optional `ValidTo`. `RoleAssignmentClaimsTransformation` reads active assignments on every request (cached 60 s per user) and projects them into ASP.NET role claims so `[Authorize(Roles = ...)]`, `[Authorize(Policy = …)]`, and `User.IsInRole()` work. The same transformation also stamps the stored `UserState` claim — the single source of truth for access (`UserState == Active`, i.e. the user entered their legal name, grants full app access). The former `ActiveMember` (Volunteers-membership-derived) and `HasProfile` claims were removed.
- **Magic Link Auth** is the email-based login/signup flow handled by `MagicLinkService` + `AccountController`. Users enter an email address; the system sends a Data-Protection-token-protected link. Login links verify a user id; signup links carry the email address. Tokens are single-use (replay-protected 15-minute cache) and signup sends are rate-limited to 1 per 60 seconds per address; login-link sends are rate-limited to 1 per 60 seconds per user via `User.MagicLinkSentAt`. Token generation/validation/URL building sits behind `IMagicLinkUrlBuilder` and rate-limit/replay state behind `IMagicLinkRateLimiter` so `MagicLinkService` itself depends on no `HumansDbContext`, `IDataProtectionProvider`, or `IMemoryCache`.
- **Access gating** — `MembershipRequiredFilter` (a global `IAsyncActionFilter`) routes authenticated users by their stored `UserState`: only `Active` reaches the app; `Bare` → `/OnboardingWidget`, `DeletePending` → `/User/Deletion`, and `Suspended`/`AdminSuspended`/`Rejected`/`Deleted`/`Merged` → `/User/Status`. It passes through endpoints carrying `[AllowAnonymous]` and the exempt controllers a non-Active user must still reach (`Account`, `OnboardingWidget`, `Profile`, `Consent`, `User`, `Language`, `Guest`, `GovernanceApplications`, `Issues`, `Notifications`, `Survey`). Role-gated app controllers require `UserState == Active` before their role policies matter. It also passes a Backdoor-API-key-authenticated request straight through — a machine principal never carries the `UserState` claim this gate reads, and there is no onboarding page to redirect a JSON client to (see `src/Sections/Humans.Backdoor/Docs/Backdoor.md`).

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
| GET | `GateLogin` | anonymous | Renders the gate-terminal username/password form (shared laptop at gate; see `src/Sections/Humans.Scanner/Docs/features/gate-terminal-login.md`) |
| POST | `GateLogin` | anonymous | Checks the credential against the well-known gate account (`SystemUserIds.GateTerminal`). Failures throttle per source IP (`GateLoginThrottle`, 10/min) — never per account, so nobody can lock the gate out; the account has Identity lockout disabled. Success signs in `isPersistent: true`, stamps `LastLoginAt`, redirects to `/Gate` (the gate terminal, which forwards to the claim screen to pick who's scanning) |
| POST | `Logout` | (any) | Sign-out and redirect to `Home/Index` |
| GET | `AccessDenied` | (any) | Renders the access-denied page |

## Data Model

### RoleAssignment

**Table:** `role_assignments`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK → User — no nav property (removed, not just deprecated), never `.Include()`d |
| CreatedByUserId | Guid | FK → User (required) — no nav property (removed, not just deprecated); `OnDelete(DeleteBehavior.Restrict)` |
| RoleName | string (max 256) | Role name (see constants below) |
| ValidFrom | Instant | When the role became active |
| ValidTo | Instant? | When the role ended (null for currently active) |
| Notes | string? (max 2000) | Free-text reason recorded at assign / end time (end notes are appended as `Ended: …`) |
| CreatedAt | Instant | When the row was written |

**Constraints / indexes:** `CK_role_assignments_valid_window` (`ValidTo IS NULL OR ValidTo > ValidFrom`); indexes on `UserId`, `RoleName`, `(UserId, RoleName, ValidFrom)`, plus a partial index on `(UserId, RoleName)` filtered to `ValidTo IS NULL` for active-row lookups.

Cross-domain navs `RoleAssignment.User` and `RoleAssignment.CreatedByUser` were removed entirely, not just deprecated. The repository does not `.Include()` them (there's nothing to include); display data stitches via `IUserServiceRead.GetUserInfosAsync`.

### RoleNames (constants)

Defined in `src/Humans.Base/Constants/RoleNames.cs`.

| Constant | Value | Purpose |
|----------|-------|---------|
| Admin | `"Admin"` | Full system access — global superset |
| Board | `"Board"` | Elevated permissions, votes on tier applications |
| HumanAdmin | `"HumanAdmin"` | Approve/suspend/reject humans, provision @nobodies.team email accounts, manage role assignments |
| TeamsAdmin | `"TeamsAdmin"` | Manage all teams, approve membership, assign leads, configure Google Group prefixes system-wide |
| CampAdmin | `"CampAdmin"` | Manage camps, approve/reject season registrations, configure camp settings system-wide |
| TicketAdmin | `"TicketAdmin"` | Manage ticket vendor integration, trigger syncs, generate discount codes, export ticket data |
| FeedbackAdmin | `"FeedbackAdmin"` | No privileges since #977 — Feedback is retired and `AdminOnly`. Kept assignable; still admits the holder to the `/Admin` shell via `AnyAdminRole` |
| FinanceAdmin | `"FinanceAdmin"` | Full access to Finance section (budgets, audit log) |
| NoInfoAdmin | `"NoInfoAdmin"` | Approve/voluntell shift signups (cannot create/edit shifts); access to volunteer-event-profile medical data |
| StoreAdmin | `"StoreAdmin"` | Store-domain superset: catalog, orders, payments, invoices, treasury sync; FinanceAdmin retains parallel access for accounting workflows |
| EventsAdmin | `"EventsAdmin"` | Approve, reject, and request edits on event guide submissions |
| CantinaAdmin | `"CantinaAdmin"` | Read-only cantina weekly/daily roster (on-site cohort, dietary breakdown, allergy/intolerance rollups); sees dietary preferences but never medical conditions — excluded at the DTO boundary |
| ConsentCoordinator | `"ConsentCoordinator"` | Safety gate for onboarding consent checks |
| VolunteerCoordinator | `"VolunteerCoordinator"` | Facilitation contact for onboarding; read-only access to onboarding review queue |
| EETeamAdmin | `"EETeamAdmin"` | Cross-team Early-Entry administrator — can grant/edit/revoke early-entry grants on ANY team that has `EarlyEntryEnabled`. Confers nothing else. Coordinators manage EE on their own team without this role |

`RoleNames.BoardManageableRoles` (a `HashSet<string>` on the same constants class) enumerates the roles that Board and HumanAdmin are permitted to assign / end; `Admin` is **not** in that set, so only an existing Admin can assign the Admin role (enforced by both `RoleAssignmentAuthorizationHandler` and `RoleChecks.GetAssignableRoles`).

### Authorization-policy layout (RoleGroups → PolicyNames)

The auth surface is mid-transition per `docs/plans/2026-04-03-first-class-authorization-transition.md`:

- **Phase 1 — coarse policies** (`Humans.Base/Authorization/PolicyNames.cs` + `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`): **complete.** Controllers use `[Authorize(Policy = PolicyNames.X)]` and views use the `authorize-policy` TagHelper. The legacy `Humans.Base.Constants.RoleGroups` constants still exist in source but have **zero in-source call sites** — kept around as constants only and slated for deletion.
- **Phase 2 — resource-based authorization (first vertical slices):** shipped. Production handlers registered by `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies` (`src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs`): `CampAuthorizationHandler` (resource: `Camp`) and `UserEmailAuthorizationHandler` (resource: `UserEmail`). Composite custom handlers (`HumanAdminOnlyHandler`, `IsAnyTeamManagerOrCoordinatorHandler`, and `CampComplianceAccessHandler` (policy `CampComplianceAccess`: succeeds for CampAdmin/Admin, or any team/sub-team coordinator via the cached `IShiftManagementService.GetCoordinatorTeamIdsAsync` lookup — gates the read-only Barrios role-staffing compliance matrix, broader than the CampAdmin-only `CampAdminOrAdmin` management surface)) are also registered here. The nav-visibility gate is the single `AppAccess` policy — a plain `RequireAssertion` (`UserState == Active`), no custom handler.

  Section-owned resource handlers now register themselves from inside their own section (each section's `Section.cs`), not from Auth's extension method — they reuse `PolicyNames`/`RoleChecks` from `Humans.Base.Authorization` but are not part of Auth's registration surface: `TeamAuthorizationHandler` (resource: `Team`; `src/Sections/Humans.Teams/Authorization/` — gates the `ManageCoordinators` and `ManageEarlyEntry` team operations — Admin/TeamsAdmin/Board pass any operation on any team, `EETeamAdmin` passes `ManageEarlyEntry` on any team only, and a team's own coordinator passes both for their team), `RoleAssignmentAuthorizationHandler` (resource: target role-name string; `src/Sections/Humans.Auth/Authorization/` — moved with the rest of Auth at its own G5), `AgentRateLimitHandler` (+ its now-section-internal `AgentRateLimitRequirement`; `src/Sections/Humans.Agent/Authorization/` — the requirement and the `PolicyNames.AgentRateLimit` constant left Base with the G5 Agent move), `BudgetAuthorizationHandler` (resource: `BudgetCategory`/department-scoped budget edits; `src/Sections/Humans.Budget/Authorization/`), `IssuesAuthorizationHandler` (resource: `Issue`; `src/Sections/Humans.Issues/Authorization/`), `ContainerAuthorizationHandler` (resource: `ContainerAuthorizationTarget`; `src/Sections/Humans.Containers/Authorization/`), `ExpenseReportAuthorizationHandler` (resource: `ExpenseReportDto`; gates View/Edit/Submit/Withdraw/Endorse/CoordinatorReject/Approve/FinanceReject/CategoryOverride) and `IbanAccessHandler` (gates raw IBAN access for self, FinanceAdmin with non-Draft/non-Withdrawn report context, or Admin on admin page) (both `src/Sections/Humans.Expenses/Authorization/`), and `OrderAuthorizationHandler` (resource: `OrderDto` / `OrderCreateContext` / `OrderLineContext`; also gates line edits past a product's `OrderableUntil` deadline for non-admin paths; `src/Sections/Humans.Store/Authorization/`, renamed from `StoreOrderAuthorizationHandler` when Store moved into its own project, nobodies-collective/Humans#866).
- **Phase 3 — broad service-layer authorization enforcement:** **cancelled / tombstoned.** Superseded by `docs/architecture/design-rules.md §11`: services are auth-free by default; controllers call `IAuthorizationService.AuthorizeAsync` and do not pass `isPrivileged` booleans. The sole exception is the documented full-Admin destructive-delete/reset guard via `IAdminAuthorizationService`.

### Magic-link state

`MagicLinkService` owns no tables. Its only persistent state is `User.MagicLinkSentAt` (mutated via `UserManager<User>`). Replay protection and signup rate-limit state sit behind `IMagicLinkRateLimiter` (section-internal, `IMemoryCache`-backed); Data Protection token generation/validation and URL construction sit behind `IMagicLinkUrlBuilder`.

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
- Every sign-in path signs in with `isPersistent: true`, and the Identity application cookie carries a **14-day sliding** lifetime (`ExpireTimeSpan` + `SlidingExpiration` in `Program.cs`): a visit inside the window extends it, 14 days away requires re-login. Session-scoped cookies (`isPersistent: false`) have no `Expires` and die on browser close — that was the re-login-every-visit bug (nobodies-collective#925). Cookie lifetime is **not** a revocation mechanism and doesn't need to be short: `MembershipRequiredFilter` re-checks `UserState` on every request, `RoleAssignmentClaimsTransformation` re-derives roles (60 s cache), and Identity's security-stamp validation invalidates server-side. DataProtection keys persist to the DB, so cookies also survive container restarts.
- Google OAuth remote failures (missing/expired correlation cookie, user-cancelled consent, browser cookie restrictions) are handled by an `OnRemoteFailure` handler on the Google authentication options in `Program.cs` that logs at Warning and redirects to `/Account/Login?error=sign-in-failed`; the unhandled `CorrelationException` would otherwise propagate as a 500. The login view reads `Request.Query["error"]` and renders a localized dismissible alert.

## Negative Access Rules

- Magic links are sent silently when the email is unknown or rate-limited — no account enumeration. The caller always sees the same "check your email" UI.
- `RoleAssignmentService.AssignRoleAsync` **cannot** create a new assignment when an overlapping active one already exists for the same (user, role).
- `EndRoleAsync` **cannot** end an assignment that is already ended or not yet active.

## Triggers

- **On role assigned or ended:** `IRoleAssignmentClaimsCacheInvalidator.Invalidate(userId)` clears the per-user claims cache so the next request re-derives claims; `INavBadgeCacheInvalidator.Invalidate()` refreshes the top-nav counters; an in-app `RoleAssignmentChanged` notification is sent to the affected human (best-effort — failures are logged, not propagated); if the role is `Board`, `ISystemTeamSync.SyncBoardTeamAsync` runs.
- **On `RevokeAllActiveAsync(userId)`:** every currently-active row for the user has `ValidTo` stamped to now in one batch update, then `IRoleAssignmentClaimsCacheInvalidator.Invalidate(userId)` is called. (Nav-badge counters and per-row notifications are intentionally **not** dispatched on this bulk path — it's a privacy/account-deletion code path, not an admin role-management action.)
- **On magic-link login email sent:** `User.MagicLinkSentAt` is stamped (in `SendLoginLinkAsync`, before the cache-backed cooldown applies) so subsequent send requests within 60 seconds for the same user are silently no-op'd. Signup-link sends use a separate `IMagicLinkRateLimiter.TryReserveSignupSendAsync` reservation per email address (released on send failure so the caller can retry).
- **On magic-link login token verified:** the token is reserved in the replay cache for the remainder of its 15-minute lifetime via `IMagicLinkRateLimiter.TryConsumeLoginTokenAsync` so it cannot be redeemed twice. `AccountController.MagicLink` stamps `User.LastLoginAt` after the verified sign-in.
- **On account merge accept:** `IAccountMergeService.AcceptAsync` (Profiles section) calls `IRoleAssignmentService.ReassignToUserAsync(sourceId, targetId, …)` to re-FK active `role_assignments` rows from source to target. AspNetUserLogins re-FK is handled separately by `IUserService.ReassignLoginsToUserAsync`.

## Cross-Section Dependencies

- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — display names for assignee/creator stitched in memory (design-rules §6b). `IUserEmailService.FindVerifiedEmailWithUserAsync` — verified email → owning user for magic-link login.
- **Teams:** `ISystemTeamSync.SyncBoardTeamAsync` — Board system team's membership mirrors current `Board` role assignments.
- **Governance:** Tier applications and board voting flows are a separate concern. Governance concerns association-level affairs; Auth concerns who-has-what-role within the running system. `role_assignments` is owned by Auth, not Governance.
- **Notifications:** `Humans.Notifications.Contracts.INotificationEmitter` (the narrow per-user dispatch surface — `INotificationService` extends it but Auth only needs the emitter) — best-effort in-app notifications on role changes.
- **Profiles:** Called by `IAccountMergeService` (Profiles section) — `IRoleAssignmentService.ReassignToUserAsync` re-FKs `role_assignments` from source to target during account merge fold.

## Access Matrix UI (per-section)


Each section's landing page exposes an info-icon button (`AccessMatrixViewComponent`, invoked as `<vc:access-matrix section="…" />`) that opens a modal showing which roles can do what in that section. Definitions live in `src/Humans.Base/Models/AccessMatrixDefinitions.cs` as **static data, not DB-driven** — there is intentionally no `access_matrix` table.

- **Admin is excluded from every matrix.** Admin can do everything everywhere; including the column would be visual noise.
- **"Volunteer" is the baseline, not a formal role.** It means "any active member" — the absence of an elevated role, not an entry in `role_assignments`.
- **Admin-only sections have no matrix.** Sections gated entirely to Admin would have no non-Admin columns to show, so they don't render the component.
- **Maintenance hazard — the matrix can drift from the code.** The dictionary is hand-maintained and is not derived from `[Authorize]` attributes or `PolicyNames`. When you change a policy, update the matrix in the same commit.

## Architecture

**Owning services:** `RoleAssignmentService`, `MagicLinkService` (both in the section)
**Owned tables:** `role_assignments`
**Status:** (G5) Own project — `src/Sections/Humans.Auth` + `src/Sections/Humans.Auth.Contracts` (nobodies-collective/Humans#866).

### What is in the section and what stayed in Shell

Auth is a **horizontal** section. `peters-hard-rules.md` forbids a horizontal from referencing a vertical *section project*; a vertical's `.Contracts` leaf is legal from anywhere (Peter's Base-floor decision of 2026-08-14). That is what decided the split:

- **In `Humans.Auth`:** the `RoleAssignment` entity, `AuthDbContext` + factory + configuration + `Data/Migrations/`, `RoleAssignmentRepository`, `RoleAssignmentService`, `CachingRoleAssignmentService`, `AdminAuthorizationService`, `RoleAssignmentAuthorizationHandler`, and — since nobodies-collective/Humans#866 G5 lane 4b-2i — the whole magic-link sign-in path: `MagicLinkService`, `IMagicLinkUrlBuilder` + `MagicLinkUrlBuilder`, `IMagicLinkRateLimiter` + `MagicLinkRateLimiter`. Everything except the `Contracts/` folder is `internal`.
- **On `Humans.Auth.Contracts`** (framework-free leaf; ~30 Base and Shell consumers): `IRoleAssignmentService`, `IAdminAuthorizationService`, `ICurrentUserContext`, `RoleAssignmentRow`, the three snapshots, `RoleAssignmentResult`, and `IMagicLinkService`. The last is on the leaf rather than in the section's `Contracts/` folder because a *Base* consumer names it — `Humans.Application`'s `ExternalLoginService` calls `FindUserByVerifiedEmailAsync` on the OAuth callback path, and Base cannot reference a section project (design §15 step 5b). It is also why this leaf, alone among the horizontals', references `Humans.Users.Contracts`: two of that interface's members return `User`.
- **In `Humans.Auth/Contracts/`** (folder, needs ASP.NET): `RoleAssignmentOperationRequirement`, which Shell's `UsersAdminController` passes to `IAuthorizationService.AuthorizeAsync`.
- **Stayed in Shell:** `AccountController` and `Views/Account/*` with their 35 `SharedResource` keys, `HttpCurrentUserContext`, `RoleAssignmentClaimsTransformation` and `RoleAssignmentClaimsCacheInvalidator`. `AccountController` did not follow `MagicLinkService` in: every action it exposes writes Users'/Profiles' tables through their services and none writes `role_assignments`. The section still ships no controller, no view and no `Resources/` folder.

The section's outbound reference set is `Humans.AuditLog.Contracts`, `Humans.Gdpr.Contracts`, `Humans.Notifications.Contracts` (horizontal leaves), `Humans.Users.Contracts` (display stitching, `UserManager<User>`, `IUserEmailService`) and `Humans.Email.Contracts` (the magic-link send), each with a reason recorded beside it in `Humans.Auth.csproj`. `AuthArchitectureTests.SectionReferencesNoVerticalSection` used to pin that list; it was **retired** at G5 lane 4b-2i because Peter's Base-floor decision deleted its premise, the same call lane 4b-2h made for AuditLog's copy. A "horizontals may name leaves but never another section's project" rule, if wanted, belongs once as a generic rule over all horizontals — not restated per section.

- `RoleAssignmentService` lives in `Humans.Auth/Services/` and depends only on Application-layer abstractions and other horizontals' leaves. It never imports `Microsoft.EntityFrameworkCore`.
- `IRoleAssignmentRepository` (impl `Humans.Auth/Data/RoleAssignmentRepository.cs`) owns the SQL surface for `role_assignments`, reading/writing through `IDbContextFactory<AuthDbContext>` so the repository is registered Singleton while `AuthDbContext` itself stays Scoped. Since nobodies-collective/Humans#858, `role_assignments` lives in its own `AuthDbContext` (own `__EFMigrationsHistory_Auth` table) — not `HumansDbContext`. Same database and connection; the split is code-side only.
- **Decorator decision — no caching decorator.** Role assignments are low-traffic (handful of admin-driven writes per month, few reads per day) and magic links are throwaway; a dict-backed decorator isn't warranted (same rationale as Governance / User / Feedback).
- **Section boundary shape** — no `IStringLocalizer`, no `DbContext`/store in `Services/`, framework-free `Contracts` leaf. `MagicLinkService` takes no `EmailSettings`/`IDataProtectionProvider` parameter and no repository parameter (URL construction sits behind `IMagicLinkUrlBuilder`; it is an orchestrator per peters-hard-rules.md). These are documentation, not pinned tests — a test asserting a type lacks a constructor parameter is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md). `MagicLinkServiceTests` stayed in `Humans.Application.Tests` because it derives from that project's `ServiceTestHarness`; it reaches the now-internal service through `Humans.Auth`'s existing `InternalsVisibleTo`.
- **Cross-domain navs removed, not just `[Obsolete]`-marked:** `RoleAssignment.User`, `RoleAssignment.CreatedByUser` no longer exist as nav properties. The repository does not `.Include()` them; the service stitches display data in memory from `IUserServiceRead.GetUserInfosAsync` (§6b). `AboutController` and `ProfileController` still carry a stale `#pragma warning disable CS0618` (leftover from when they read `ra.User.DisplayName` / `ra.CreatedByUser.DisplayName` directly) but no longer reference either nav — the pragma is now a no-op tracked as cleanup debt, not an active suppression.
- `MagicLinkService` owns no tables. Its persistent state is `User.MagicLinkSentAt`, mutated via `UserManager<User>`. Verified-email lookup goes through `IUserEmailService.FindVerifiedEmailWithUserAsync`. Data-Protection token generation/validation and URL construction sit behind `IMagicLinkUrlBuilder`; replay-protection and signup rate-limit state sit behind `IMagicLinkRateLimiter`. That arrangement keeps `MagicLinkService` free of `HumansDbContext`, `EmailSettings`, `IDataProtectionProvider`, and `IMemoryCache`.
- **Cross-cutting invalidation** routes through `INavBadgeCacheInvalidator` (top-nav counters) and `IRoleAssignmentClaimsCacheInvalidator` (per-user claims transform cache) — never raw `IMemoryCache` calls.

### Touch-and-clean guidance

- Do **not** reintroduce `.Include(ra => ra.User | ra.CreatedByUser)` anywhere. New read paths go through `IRoleAssignmentRepository` (or extend the repository with a new narrowly-shaped query) and stitch display data in `RoleAssignmentService` via `IUserService`.
- Do **not** inject `HumansDbContext` into `RoleAssignmentService` or `MagicLinkService`.
- Do **not** inject `IMemoryCache` into `MagicLinkService`. Use `IMagicLinkRateLimiter` (or add a new cross-cutting abstraction) for cache-backed state.
- Do **not** add a sign-in call with `isPersistent: false`. There are zero left in `src/`; `SecurityHeaderTests` pins the resulting cookie policy.
- Nothing outside the section reads `role_assignments` directly any more: `AuthDbContext` is `internal` to `Humans.Auth`, so the compiler enforces what the old §15h touch-and-clean list asked for. Cross-section reads go through `IRoleAssignmentService` on the leaf.
