<!-- freshness:triggers
  src/Sections/Humans.Auth/**
  src/Sections/Humans.Auth/Contracts/**
  src/Humans.Base/Constants/RoleNames.cs
  src/Humans.Base/Interfaces/ISectionRoles.cs
  src/Sections/Humans.Auth/Contracts/RoleCatalog.cs
  src/Sections/Humans.Auth/Section.cs
  src/Sections/Humans.Auth/Authorization/RoleAssignmentRules.cs
  src/Sections/Humans.Auth/Section.cs
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Authorization/**
  src/Humans.Web/Program.cs
  src/Humans.Web/Services/LoginMethodCounter.cs
  src/Humans.Web/Services/GateLoginThrottle.cs
  src/Humans.Base/Extensions/MemoryCacheExtensions.cs
  src/Humans.Base/Authorization/PolicyNames.cs
  src/Humans.Base/Authorization/RoleChecks.cs
  src/Sections/**/Authorization/**/*.cs
  src/Humans.Base/Interfaces/ISectionAccessMatrix.cs
  src/Humans.Base/Models/AccessMatrixData.cs
  src/Sections/**/Section.cs
  src/Humans.Base/ViewComponents/AccessMatrixViewComponent.cs
  src/Sections/Humans.Auth/AuthResource*.resx
-->
<!-- freshness:flag-on-change
  Role-assignment temporal invariants, magic-link rate-limit/replay rules, role-name constants, and the access-matrix mechanism (§"Access Matrix UI" — AccessMatrixViewComponent over the static rows sections contribute through ISectionAccessMatrix, no DB table) — review when Auth services, role constants, claims transformation, or the access-matrix component/source change. The Auth resx is here for one reason: Auth_Email_MagicLinkSignup_Body promises the recipient the link works only once, and this doc asserts the code keeps that promise — reword the copy and this doc goes stale.
-->

# Auth — Section Invariants

Role assignments (temporal), magic-link login/signup, claims transformation.

## Concepts

- **Role Assignment** is a temporal assignment of a role name (e.g. `Admin`, `Board`, `TeamsAdmin`, `UsersAdmin`, `ConsentCoordinator`, …) to a human, with a `ValidFrom` and optional `ValidTo`. `RoleAssignmentClaimsTransformation` reads active assignments on every request (from Auth's in-memory role-assignment cache, no per-user claims cache) and projects them into ASP.NET role claims so `[Authorize(Roles = ...)]`, `[Authorize(Policy = …)]`, and `User.IsInRole()` work. The same transformation also stamps the stored `UserState` claim — the single source of truth for access (`UserState == Active`, i.e. the user entered their legal name, grants full app access).
- **Magic Link Auth** is the email-based login/signup flow handled by `MagicLinkService` + `AccountController`. Users enter an email address; the system sends a Data-Protection-token-protected link. Login links verify a user id; signup links carry the email address. Both token types require canonical Base64url spelling before validation: whitespace, padding and alternate trailing bits are rejected so equivalent protected bytes cannot bypass the token-text SHA-256 replay key. Both token types are single-use, consumed into the same replay-protected 15-minute cache: **login** tokens on `VerifyLoginTokenAsync` (returns `bool`; the controller loads the Identity user by id to sign in), **signup** tokens on `VerifyAndConsumeSignupTokenAsync` at the POST that redeems them. The signup GET reads the token without consuming it (form prefill, so a scanner following the link cannot burn it). This is the guarantee the shipped signup email makes in all six cultures. Signup sends are rate-limited to 1 per 60 seconds per address; login-link sends are rate-limited to 1 per 60 seconds per user via an exclusive per-user cache reservation taken before profile lookup or email enqueue (in-memory only; the cooldown does not survive a restart, by decision 2026-10-20).
- **Access gating** — `MembershipRequiredFilter` (a global `IAsyncActionFilter`) routes authenticated users by their stored `UserState`: only `Active` reaches the app; `Bare` → `/OnboardingWidget`, `DeletePending` → `/Gdpr/Deletion`, and `Suspended`/`AdminSuspended`/`Rejected` → `/Gdpr/Status`. `Deleted`/`Merged` accounts are walled off before the exemptions below apply — only `Account`, `Language`, and `Gdpr/Status` reach them. It passes through endpoints carrying `[AllowAnonymous]` and the exempt controllers a non-Active recoverable user must still reach wholesale (`Account`, `OnboardingWidget`, `Consent`, `User`, `Language`, `Guest`, `GovernanceApplications`, `Issues`, `Notifications`, `Survey`), plus specific `Profile`/`ProfileEmails` actions (own-profile editing, privacy/download-data, email management) — not those controllers wholesale. Role-gated app controllers require `UserState == Active` before their role policies matter. It also passes a Backdoor-API-key-authenticated request straight through — a machine principal never carries the `UserState` claim this gate reads, and there is no onboarding page to redirect a JSON client to (see `src/Sections/Humans.Backdoor/Docs/Backdoor.md`).

### AccountController routes

All under `/Account`. Anti-forgery on every POST.

| Verb | Action | Auth | Purpose |
|------|--------|------|---------|
| GET | `Login` | anonymous | Renders login page (Google OAuth + magic-link request); Open Graph link-preview tags describe the area `returnUrl` points at (`LoginLinkPreview`) |
| POST | `ExternalLogin` | anonymous | Initiates external (Google) OAuth challenge |
| GET | `ExternalLoginCallback` | anonymous | Handles OAuth return: signs in, links to existing account by verified email, recovers a locked-out source account by re-linking to an active one, or creates a new account |
| POST | `MagicLinkRequest` | anonymous | Sends a magic link (login or signup) — always shows "check your email" (no enumeration) |
| GET | `MagicLinkConfirm` | anonymous | Landing page that defers token consumption to a user-clicked POST (defeats email security scanners) |
| POST | `MagicLink` | anonymous | Verifies login token, signs the user in, stamps `LastLoginAt` |
| GET | `MagicLinkSignup` | anonymous | Reads the signup token (without consuming it) and renders the complete-signup form |
| POST | `CompleteSignup` | anonymous | Redeems the signup token — consumed once, after required-name and 256-character name-length validation — then creates the `User` + `UserEmail` and signs the new user in. A duplicate submit from the now-signed-in user redirects onward; any other replay gets `MagicLinkError` |
| GET | `GateLogin` | anonymous | Renders the gate-terminal username/password form (shared laptop at gate; see `src/Sections/Humans.Scanner/Docs/features/gate-terminal-login.md`) |
| POST | `GateLogin` | anonymous | Checks the credential against the well-known gate account (`SystemUserIds.GateTerminal`). Failures throttle per source IP (`GateLoginThrottle`, 10/min) — never per account, so nobody can lock the gate out; the account has Identity lockout disabled. Success signs in `isPersistent: true`, stamps `LastLoginAt`, redirects to `/Gate` (the gate terminal, which forwards to the claim screen to pick who's scanning) |
| POST | `Logout` | (any) | Sign-out and redirect to `Home/Index` |
| GET | `AccessDenied` | (any) | Renders the access-denied page |

Successful Google and magic-link sign-ins (including magic-link signup) bump Shell's in-memory `LoginMethodCounter`, shown as the "Sign-in method" dial on `/Admin`. Nothing is stored — a restart zeroes it. Linking a Google account while signed in, the gate terminal, and dev logins are not counted.

### Role-assignment admin routes

`RoleAssignmentsController`, all `HumanAdminBoardOrAdmin`:

| Route | Purpose |
|---|---|
| `GET /Auth/Admin/Roles` | System-wide role roster, filterable by role, active-only by default |
| `GET/POST /Auth/Admin/{id}/Roles/Add` | Assign a role to a member (`HumanAdminOrAdmin`; per-role `RoleAssignmentManage` check on POST) |
| `POST /Auth/Admin/{id}/Roles/{roleId}/End` | End an assignment (`HumanAdminOrAdmin`; per-role `RoleAssignmentManage` check) |

Reached from the Roles item of the Users admin sidebar group and the Role Assignments card on `/Users/Admin/{id}`; both POSTs redirect back to the member's admin detail.

## Data Model

### RoleAssignment

**Table:** `role_assignments`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | The assignee's user id. No FK constraint and no nav property — `users` is Users' table in another context; never `.Include()`d |
| CreatedByUserId | Guid | Who created the row (required). Same shape: bare id, no FK, no nav property |
| RoleName | string (max 256) | Role name (see constants below) |
| ValidFrom | Instant | When the role became active |
| ValidTo | Instant? | When the role ended (null for currently active) |
| Notes | string? (max 2000) | Free-text reason recorded at assign / end time (end notes are appended as `Ended: …`) |
| CreatedAt | Instant | When the row was written |

**Constraints / indexes:** `CK_role_assignments_valid_window` (`ValidTo IS NULL OR ValidTo > ValidFrom`); indexes on `UserId`, `RoleName`, `(UserId, RoleName, ValidFrom)`, plus a partial index on `(UserId, RoleName)` filtered to `ValidTo IS NULL` for active-row lookups.

`RoleAssignment` has no `User` or `CreatedByUser` navigation property; display data stitches via `IUserService.GetUserInfosAsync`.

### Roles (the catalog)

`RoleNames` (Base) holds only the two system-wide roles, `Admin` and `Board`. Every other role is declared by its owning section through `ISectionRoles` — a `RoleDefinition` with name (`<Section><Role>`), title, blurb, icon, `OpensAdminShell` and `FormerNames` — and lives as a `<Section>Roles` constant in that section's Contracts. Auth composes them into the singleton `RoleCatalog` (`Contracts/RoleCatalog.cs`) at startup, which throws on a duplicate name, a duplicate title, or a former name that still names a role; Auth declares `Admin` and `Board` itself (`Section.cs`). The catalog feeds the role-assignment UI, the Staff page, the authorization pill, the dev personas, the Guide's heading parentheticals (by title) and the Shell's `AnyAdminRole` policy (every role with `OpensAdminShell`). The pill reads each page's policy as registered: a role check shows its role titles, and a requirement that is not a role check implements `IDescribedRequirement` (Base) to say who it admits; `AuthorizationPillFilterTests` fails on a policy it can't read.

| Role | Declared by | Purpose |
|------|-------------|---------|
| Admin | Auth | Full system access — global superset |
| Board | Auth | Elevated permissions, votes on tier applications |
| UsersAdmin | Users (was `HumanAdmin`) | Approve/suspend/reject humans, provision @nobodies.team email accounts, manage role assignments |
| TeamsAdmin | Teams | Manage all teams, approve membership, assign leads, configure Google Group prefixes system-wide |
| CampsAdmin | Camps (was `CampAdmin`) | Manage camps, approve/reject season registrations, configure camp settings system-wide |
| TicketsAdmin | Tickets (was `TicketAdmin`) | Manage ticket vendor integration, trigger syncs, generate discount codes, export ticket data |
| FeedbackAdmin | Feedback | No privileges — Feedback is `AdminOnly`. Kept assignable; still admits the holder to the `/Admin` shell via `AnyAdminRole` |
| FinanceAdmin | Finance | Full access to Finance section (budgets, audit log) |
| ShiftsNoInfo | Shifts (was `NoInfoAdmin`) | Approve/voluntell shift signups (cannot create/edit shifts); access to volunteer-event-profile medical data |
| StoreAdmin | Store | Store-domain superset: catalog, orders, payments, invoices, treasury sync; FinanceAdmin retains parallel access for accounting workflows |
| EventsAdmin | Events | Approve, reject, and request edits on event guide submissions |
| CantinaAdmin | Cantina | Read-only cantina weekly/daily roster (on-site cohort, dietary breakdown, allergy/intolerance rollups); sees dietary preferences but never medical conditions — excluded at the DTO boundary |
| ConsentCoordinator | Consent | Safety gate for onboarding consent checks |
| ShiftsAdmin | Shifts (was `VolunteerCoordinator`) | Facilitation contact for onboarding; read-only access to onboarding review queue |
| RideshareAdmin | Rideshare | Rideshare settings (year's destination + travel windows), season statistics, and the operational day roster; confers nothing outside Rideshare |
| EarlyEntryAdmin | EarlyEntry (was `EETeamAdmin`) | Cross-team Early-Entry administrator — can grant/edit/revoke early-entry grants on ANY team that has `EarlyEntryEnabled`. Confers nothing else. Coordinators manage EE on their own team without this role |

**Former names.** The six renamed roles carry their old name in `FormerNames`. Rows stored under a former name keep working: every read goes through `RoleCatalog.StoredNames`/`Canonical`, so claims, filters, counts and the GDPR export report the current name, and a new assignment under the current name is refused while an active row exists under the former one. The operator-run data migration `auth.role-renames` (`/Debug/DataMigrations`) rewrites those rows once and is idempotent.

`RoleAssignmentRules` (`Authorization/RoleAssignmentRules.cs`) decides who may assign or end which role: Admin any role; UsersAdmin any role but `Admin`; nobody else, Board included. `RoleAssignmentAuthorizationHandler` and the add-role form's role list both read it, and `AssignRoleAsync` refuses a name the catalog does not list (`UnknownRole`).

### Authorization-policy layout

- **Phase 1 — coarse policies** (`Humans.Base/Authorization/PolicyNames.cs` + `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies`): **complete.** Controllers use `[Authorize(Policy = PolicyNames.X)]` and views use the `authorize-policy` TagHelper. `RoleGroups` is gone; role lists derive from the `RoleCatalog`.
- **Phase 2 — resource-based authorization (first vertical slices):** shipped. `AuthorizationPolicyExtensions.AddHumansAuthorizationPolicies` (`src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs`) registers the shared platform policies only (`AdminOnly`, `AnyAdminRole`, `BoardOnly`, `BoardOrAdmin`, `ScannerAccess`, `GateAdmit`, `ReviewQueueAccess`, `AppAccess`, `RoleAssignmentManage`) and then picks up each section's `ISectionPolicies`; every handler registers from inside its own section. Camps: `CampAuthorizationHandler` (resource: `Camp`) and `CampComplianceAccessHandler` (policy `CampComplianceAccess`: succeeds for CampsAdmin/Admin, or any team/sub-team coordinator via the cached `IShiftManagement.GetCoordinatorTeamIdsAsync` lookup — gates the read-only Barrios role-staffing compliance matrix, broader than the CampsAdmin-only `CampAdminOrAdmin` management surface). Users: `UserEmailAuthorizationHandler` (resource: `UserEmail`) and `HumanAdminOnlyHandler`. Shifts: `IsAnyTeamManagerOrCoordinatorHandler`. The nav-visibility gate is the single `AppAccess` policy — a plain `RequireAssertion` (`UserState == Active`), no custom handler.

  The other section-owned resource handlers likewise register from their own `Section.cs` — they reuse `PolicyNames`/`RoleChecks` from `Humans.Base.Authorization` but are not part of Auth's registration surface: `TeamAuthorizationHandler` (resource: `Team`; `src/Sections/Humans.Teams/Authorization/` — gates the `ManageCoordinators` and `ManageEarlyEntry` team operations — Admin/TeamsAdmin/Board pass any operation on any team, `EarlyEntryAdmin` passes `ManageEarlyEntry` on any team only, and a team's own coordinator passes both for their team), `RoleAssignmentAuthorizationHandler` (resource: target role-name string; `src/Sections/Humans.Auth/Authorization/`), `AgentRateLimitHandler` (+ its section-internal `AgentRateLimitRequirement`; `src/Sections/Humans.Agent/Authorization/`), `BudgetAuthorizationHandler` (resource: `BudgetCategory`/department-scoped budget edits; `src/Sections/Humans.Budget/Authorization/`), `IssuesAuthorizationHandler` (resource: `Issue`; `src/Sections/Humans.Issues/Authorization/`), `ContainerAuthorizationHandler` (resource: `ContainerAuthorizationTarget`; `src/Sections/Humans.Containers/Authorization/`), `PreapprovalsPolicyHandler` / `ProgramAuthorizationHandler` (`src/Sections/Humans.Preapprovals/Authorization/`), `CityPlanningMapAdminHandler` (`src/Sections/Humans.CityPlanning/Authorization/`), `ExpenseReportAuthorizationHandler` (resource: `ExpenseReportDto`; gates View/Edit/Submit/Withdraw/Endorse/CoordinatorReject/Approve/FinanceReject/RequeueHoldedPush; `src/Sections/Humans.Expenses/Authorization/`), and `OrderAuthorizationHandler` (resource: `OrderDto` / `OrderCreateContext` / `OrderLineContext`; also gates line edits past a product's `OrderableUntil` deadline for non-admin paths; `src/Sections/Humans.Store/Authorization/`).
- **Phase 3 — broad service-layer authorization enforcement:** **not pursued.** Per `docs/architecture/design-rules.md §11`, services are auth-free by default; controllers call `IAuthorizationService.AuthorizeAsync` and do not pass `isPrivileged` booleans. The sole exception is the documented full-Admin destructive-delete/reset guard via `IAdminAuthorizationService`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone with a valid email | Request a magic-link login or signup |
| Admin, UsersAdmin | Assign / end role assignments: Admin any role, UsersAdmin any role but Admin. Board sees the roster and assigns nothing. |

## Invariants

- Role-assignment paging computes offsets without integer overflow; extreme pages cannot wrap into earlier assignments.

- Role assignments are temporal: a new assignment is always added (never "resurrected" by clearing `ValidTo`). `RevokeAllActiveAsync(userId)` stamps `ValidTo = now` on every currently-active row in one write.
- `RoleAssignmentClaimsTransformation` reads only `role_assignments` (not legacy `AspNetUserRoles`). It holds no claims cache: it derives the claims on every request from Auth's in-memory row cache and Users' `UserInfo` cache, so an Auth write shows on the very next request without anyone evicting anything.
- The row cache is cleared right after each repository write and **before** anything later in the same write reads roles back (the Board team sync), so those reads never see the pre-write row set.
- Assigning the `Board` role triggers a `SyncBoardTeamAsync` side-effect so the Board system team stays aligned with the current Board composition.
- Magic-link tokens are single-use: once a token is redeemed — a login token on `VerifyLoginTokenAsync`, a signup token on `VerifyAndConsumeSignupTokenAsync` — it is reserved in the replay cache for the remainder of its 15-minute lifetime and cannot be used again. Replay keys use a SHA-256 digest of the complete token rather than a truncated prefix. Distinct tokens sharing a prefix have independent reservations, and releasing a failed signup’s token leaves the other reservation held. Survey and unsubscribe links use their own token providers and do not redeem through here — they stay reusable by design.
- Missing magic-link tokens are invalid input: the token reader returns no payload rather than letting Data Protection's null-argument failure escape the login POST.
- Existing-account login emails use the resolved recipient’s preferred language, independently of the requesting browser. Signup emails retain the requesting culture; template scopes restore the caller’s culture.
- Login-link sends reserve the user’s 60-second cooldown before preparing the message, so overlapping requests to different verified aliases cannot both enqueue. Preparation/send failure releases the reservation; after a successful send it remains held for the cooldown.
- Magic-link signup sends are rate-limited to 1 email per 60 seconds per target inbox: Gmail dot, plus-tag and googlemail aliases share a reservation; non-Gmail plus-addresses remain distinct. Message destinations and signup-token addresses retain the entered form. The reservation is released on downstream send failure so the caller can retry.
- Verified-email resolution goes through `IUserEmailService.FindByAddressAsync` (aliased, verified only) — never through raw `DbContext.UserEmails` queries. Unverified rows are ignored (their auto-link would bypass the merge-request review gate).
- Every sign-in path signs in with `isPersistent: true`, and the Identity application cookie carries a **14-day sliding** lifetime (`ExpireTimeSpan` + `SlidingExpiration` in `Program.cs`): a visit inside the window extends it, 14 days away requires re-login. Session-scoped cookies (`isPersistent: false`) have no `Expires` and die on browser close. Cookie lifetime is **not** a revocation mechanism and doesn't need to be short: `MembershipRequiredFilter` re-checks `UserState` on every request, `RoleAssignmentClaimsTransformation` re-derives roles on every request from the in-memory role-assignment cache, and Identity's security-stamp validation invalidates server-side. DataProtection keys persist to the DB, so cookies also survive container restarts.
- Google OAuth remote failures (missing/expired correlation cookie, user-cancelled consent, browser cookie restrictions) are handled by an `OnRemoteFailure` handler on the Google authentication options in `Program.cs` that logs at Warning and redirects to `/Account/Login?error=sign-in-failed`; the unhandled `CorrelationException` would otherwise propagate as a 500. The login view reads `Request.Query["error"]` and renders a localized dismissible alert.
- Google account-setup failures return to Login with the six-culture `Login_SetupFailed` validation message.

## Negative Access Rules

- Magic links are sent silently when the email is unknown or rate-limited — no account enumeration. The caller always sees the same "check your email" UI.
- `RoleAssignmentService.AssignRoleAsync` **cannot** create a new assignment when an overlapping active one already exists for the same (user, role) — under the current or a former name — nor one for a role the catalog does not list.
- `EndRoleAsync` **cannot** end an assignment that is already ended or not yet active.
- The roster is `HumanAdminBoardOrAdmin`-gated; `AddRole` and `EndRole` are `HumanAdminOrAdmin`-gated (Board **cannot** reach them: 403, and the Add/End buttons are hidden from it), and each assign/end additionally passes the per-role `RoleAssignmentManage` check: a UsersAdmin actor **cannot** assign or end `Admin` (assign answers 403, end answers 404 and writes nothing). The roster and add-role GETs carry request cancellation through their role-assignment and raw-user reads.

## Triggers

- **On the `auth.role-renames` data migration:** every `role_assignments` row under a former role name is rewritten to the current name, then the row cache is cleared.
- **On role assigned or ended:** the row cache is cleared; an in-app `RoleAssignmentChanged` notification is sent to the affected human (best-effort — failures are logged, not propagated); if the role is `Board`, `ISystemTeamSync.SyncBoardTeamAsync` runs.
- **On `RevokeAllActiveAsync(userId)`:** every currently-active row for the user has `ValidTo` stamped to now in one batch update, then the row cache is cleared. (Per-row notifications are intentionally **not** dispatched on this bulk path — it's a privacy/account-deletion code path, not an admin role-management action.)
- **On magic-link login email sent:** nothing is written. The per-user `IMagicLinkRateLimiter.TryReserveUserSendAsync` reservation was taken before preparation and is the whole cooldown; only preparation/send failures release it. Signup-link sends use their separate per-address reservation (also released on send failure).
- **On a magic-link token redeemed:** the token is reserved in the replay cache for the remainder of its 15-minute lifetime via `IMagicLinkRateLimiter.TryConsumeTokenAsync` so it cannot be redeemed twice. `AccountController.MagicLink` stamps `User.LastLoginAt` after the verified sign-in. On the signup path the reservation is taken in `CompleteSignup` *after* field validation, so a re-rendered form does not burn the link; a replay that follows finds the reservation held and gets `MagicLinkError`, unless the caller is the user the first POST created and signed in, who is redirected onward. If provisioning fails, throws, or the request is cancelled, the reservation is released (`ReleaseSignupToken`) so the link survives to be retried; cancellation propagates without being logged as a provisioning error. The reservation itself is exclusive under concurrent requests: `IMemoryCache` has no atomic add, so `MemoryCacheExtensions.TryReserveAsync` takes the read and the write under one lock.
- **On account merge:** `AccountMergeService.MergeAsync` (Users section) calls every registered `IUserMerge`, including `RoleAssignmentService.ReassignAsync(archivedId, survivorId, …)`, which re-FKs `role_assignments` rows from the archived user to the survivor and then clears the row cache itself (also when the re-FK throws); the orchestrator no longer evicts Auth's caches. AspNetUserLogins re-FK is Users' own merge step (`UserRepository.ReassignLoginsToUserAsync`).

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetUserInfosAsync` — display names for assignee/creator stitched in memory (design-rules §6b). `IUserEmailService.FindByAddressAsync` — verified email → owning user for magic-link login.
- **Email:** Auth owns its two sign-in templates — `AuthEmails` (internal) builds each `EmailMessage` from Auth's own `Auth_Email_*` keys in `AuthResource`, rendered in the recipient's culture via `CultureScope`; `AuthEmailPreviews` (`IEmailPreviewContributor`, registered in `Section.Register`) lists both at `/Email/EmailPreview`. Both set `EmailMessage.Urgent` (template names `magic_link_login`, `magic_link_signup` unchanged) so the outbox drains a sign-in link immediately. Email supplies transport only — `IEmailService.SendAsync` (`memory/architecture/email-templates-live-in-sender.md`, peterdrier/Humans#1651).
- **Teams:** `ISystemTeamSync.SyncBoardTeamAsync` — Board system team's membership mirrors current `Board` role assignments.
- **Governance:** Tier applications and board voting flows are a separate concern. Governance concerns association-level affairs; Auth concerns who-has-what-role within the running system. `role_assignments` is owned by Auth, not Governance.
- **Notifications:** `Humans.Notifications.Contracts.INotificationEmitter` (the narrow per-user dispatch surface — `INotificationService` extends it but Auth only needs the emitter) — best-effort in-app notifications on role changes. Its sources are declared in `AuthNotificationSources` (`src/Sections/Humans.Auth/AuthNotificationSources.cs`), one `NotificationSourceDefinition` each: `RoleAssignmentChanged` (informational, to the user: role assigned or ended).
- **Users (account merge):** `RoleAssignmentService` implements `IUserMerge`; Users' `AccountMergeService` calls `ReassignAsync` to re-FK `role_assignments` from the archived user to the survivor during the merge fold.

## Access Matrix UI (per-section)


Each section's landing page exposes an info-icon button (`AccessMatrixViewComponent`, invoked as `<vc:access-matrix section="…" />`) that opens a modal showing which roles can do what in that section. Each matrix is contributed by the section that owns the page, through the `ISectionAccessMatrix` seam (`src/Humans.Base/Interfaces/`) implemented by that section's `ISectionAccessMatrix` class and discovered from DI; Base keeps the seam and the row shape (`AccessMatrixData`) and none of the rows. Still **static data, not DB-driven** — there is intentionally no `access_matrix` table.

- **Admin is excluded from every matrix.** Admin can do everything everywhere; including the column would be visual noise.
- **"Volunteer" is the baseline, not a formal role.** It means "any active member" — the absence of an elevated role, not an entry in `role_assignments`.
- **Admin-only sections have no matrix.** Sections gated entirely to Admin would have no non-Admin columns to show, so they don't render the component.
- **Maintenance hazard — the matrix can drift from the code.** The rows are hand-maintained and are not derived from `[Authorize]` attributes or `PolicyNames`. When you change a policy, update that section's `ISectionAccessMatrix` in the same commit.

## Architecture

**Owning services:** `RoleAssignmentService`, `CachingRoleAssignmentService`, `AdminAuthorizationService`, `AuthMetricsService`, `MagicLinkService` (all in the section)
**Owned tables:** `role_assignments`
**Status:** Own project — `src/Sections/Humans.Auth` + `src/Sections/Humans.Auth/Contracts`.

### What is in the section and what stayed in Shell

Auth is a **horizontal** section. `peters-hard-rules.md` forbids a horizontal from referencing a vertical *section project*; a vertical's `.Contracts` leaf is legal from anywhere. That is what decides the split:

- **In `Humans.Auth`:** the `RoleAssignment` entity, `AuthDbContext` + factory + configuration + `Data/Migrations/`, `RoleAssignmentRepository`, `RoleAssignmentService`, `CachingRoleAssignmentService`, `AdminAuthorizationService`, `RoleAssignmentAuthorizationHandler`, and the whole magic-link sign-in path: `MagicLinkService`, `IMagicLinkUrlBuilder` + `MagicLinkUrlBuilder`, `IMagicLinkRateLimiter` + `MagicLinkRateLimiter`. Everything except the `Contracts/` folder is `internal`.
- **In `Humans.Auth/Contracts/`** (namespace `Humans.Auth.Contracts`, consumed throughout the Shell and other sections): `IRoleAssignmentService` (reads), `IRoleAssignmentWrite` (the writes other sections call), `IAdminAuthorizationService`, `ICurrentUserContext`, `RoleAssignmentRow`, `RoleAssignmentSnapshot`, `RoleAssignmentDetailSnapshot`, `RoleAssignmentSummarySnapshot`, `RoleAssignmentResult`, `IMagicLinkService` (Shell `AccountController` only; `VerifyLoginTokenAsync` returns `bool`), and `RoleAssignmentOperationRequirement`, which Shell registers under `PolicyNames.RoleAssignmentManage`; Auth's own `RoleAssignmentsController` passes that policy name to `IAuthorizationService.AuthorizeAsync`.
- **Stayed in Shell:** `AccountController` and `Views/Account/*` with their `SharedResource` keys, `HttpCurrentUserContext`, and `RoleAssignmentClaimsTransformation`. `AccountController` stays in Shell: every action it exposes writes Users'/Profiles' tables through their services and none writes `role_assignments`. The role-assignment admin pages are the section's other half: `RoleAssignmentsController` (`/Auth/Admin/Roles`, `/Auth/Admin/{id}/Roles/Add`, `/Auth/Admin/{id}/Roles/{roleId}/End`), its `Views/RoleAssignments/*`, and the Role Assignments card on the admin human-detail page, contributed to the `AdminDetail` user-part slot by `RoleAssignmentsCardViewComponent` (`IUserPart`; Weight 50). `ISectionAdminNav` adds the "Roles" item to the Users admin group. The project is a Razor SDK project for those views; the controller, models and view component are `internal`/section-local. The assign/end audit entries are written by `RoleAssignmentService`, unchanged.

The section's outbound reference set is `Humans.AuditLog`, `Humans.Gdpr` (horizontal sections), `Humans.Notifications.Contracts` (horizontal leaf), `Humans.Teams.Contracts` (`ISystemTeamSync`), `Humans.Users.Contracts` (display stitching, `UserManager<User>`, `IUserEmailService`) and `Humans.Email.Contracts` (the magic-link send), each with a reason recorded beside it in `Humans.Auth.csproj`.

- `RoleAssignmentService` lives in `Humans.Auth/Services/` and depends on Base abstractions, horizontal sections' contracts and the Users/Teams `.Contracts` leaves (`IUserService`, `ISystemTeamSync`). It never imports `Microsoft.EntityFrameworkCore`.
- `IRoleAssignmentRepository` (impl `Humans.Auth/Data/RoleAssignmentRepository.cs`) owns the SQL surface for `role_assignments`, reading/writing through `IDbContextFactory<AuthDbContext>` so the repository is registered Singleton while `AuthDbContext` itself stays Scoped. `role_assignments` lives in its own `AuthDbContext` (own `__EFMigrationsHistory_Auth` table). Same database and connection; the split is code-side only.
- **Caching decorator.** `CachingRoleAssignmentService` (Singleton, `TrackedCache<Guid, RoleAssignmentRow>`) wraps the keyed Scoped inner `IRoleAssignmentManagement` (internal, `: IRoleAssignmentService, IRoleAssignmentWrite`; the singleton backs all three faces) and resolves it per call through `IServiceScopeFactory` — the decorator wraps the service interface, never the repository. It holds every `role_assignments` row, so `GetActiveCountsByRoleAsync` and `GetActiveForUserAsync` derive "active at now" in memory at any clock instant; invalidation is wholesale, driven from the inner service's writes, which take the decorator as its concrete type and call `Clear()` (the merge fold and erasure reach the inner directly, so the eviction lives there, not in the decorator's pass-through). Warm-up runs from a hosted service at startup. See `Docs/health.md` §3 for the warm path's known cost.
- **Section boundary shape** — only `AuthEmails` localizes (`IStringLocalizer<AuthResource>`); no `DbContext`/store in `Services/`, public surface confined to the `Contracts/` folder. `MagicLinkService` takes no `EmailSettings`/`IDataProtectionProvider` parameter and no repository parameter (URL construction sits behind `IMagicLinkUrlBuilder`; it is nearly an orchestrator per peters-hard-rules.md — what disqualifies it today is in `Docs/health.md` §3). These are documentation, not pinned tests — a test asserting a type lacks a constructor parameter is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).
- **No cross-domain navs:** `RoleAssignment` has no `User` or `CreatedByUser` nav property. The repository does not `.Include()` them; the service stitches display data in memory from `IUserService.GetUserInfosAsync` (§6b).
- `MagicLinkService` owns no tables and writes no other section's: `UserManager<User>` is used for lookup only. Verified-email lookup goes through `IUserEmailService.FindByAddressAsync`. Data-Protection token generation/validation and URL construction sit behind `IMagicLinkUrlBuilder`; replay-protection and both rate-limit cooldowns sit behind `IMagicLinkRateLimiter`. That arrangement keeps `MagicLinkService` free of any `DbContext`, `EmailSettings`, `IDataProtectionProvider`, and `IMemoryCache`.

### Touch-and-clean guidance

- Do **not** reintroduce `.Include(ra => ra.User | ra.CreatedByUser)` anywhere. New read paths go through `IRoleAssignmentRepository` (or extend the repository with a new narrowly-shaped query) and stitch display data in `RoleAssignmentService` via `IUserService`.
- Do **not** inject a `DbContext` into `RoleAssignmentService` or `MagicLinkService`; `AuthDbContext` is internal and only the repository takes it.
- Do **not** inject `IMemoryCache` into `MagicLinkService`. Use `IMagicLinkRateLimiter` (or add a new cross-cutting abstraction) for cache-backed state.
- Do **not** add a sign-in call with `isPersistent: false`. There are zero left in `src/`; `SecurityHeaderTests` pins the resulting cookie policy.
- Nothing outside the section reads `role_assignments` directly: `AuthDbContext` is `internal` to `Humans.Auth`, so the compiler enforces it. Cross-section reads go through `IRoleAssignmentService` in `Contracts/`. `IRoleAssignmentWrite` carries the role mutations and cache invalidation other sections call; the internal `IRoleAssignmentManagement : IRoleAssignmentService, IRoleAssignmentWrite` adds the owner-only reads and `EndRoleAsync` (only the section's own admin page ends a single role). All three registrations resolve the same singleton caching decorator.
