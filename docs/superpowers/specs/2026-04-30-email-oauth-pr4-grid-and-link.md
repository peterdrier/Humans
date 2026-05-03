# PR 4 — Email Grid & Link Surface

**Date:** 2026-04-30
**Status:** Approved for implementation
**Parent spec:** [`2026-04-27-email-and-oauth-decoupling-design.md`](2026-04-27-email-and-oauth-decoupling-design.md) §"PR 4" (lines 320–333)
**Branch:** `email-oauth-pr4-grid-ui`

PR 4 is the user-facing surface for the email-identity decoupling: the Profile email grid gains a Google service-email radio, contextual Unlink/Delete row removal, a "Link Google account" affordance, and cross-User merge visibility. Link/Unlink and Add/Delete are dimensional pairs operating on disjoint row sets — Link/Unlink handles Provider-attached rows, Add/Delete handles plain rows. Both Unlink and Delete remove the row; the distinction is which row state each operates on. There are **seven self actions** (`SetPrimary`, `SetGoogle`, `AddEmail`, `Link`, `Unlink`, `DeleteEmail`, `SetVisibility`) and **six admin actions** (everything except `Link`) reachable via a parallel `/Profile/Admin/{userId}/Emails/...` route family. `Link` is **self-only**: only the target user can complete an interactive Google OAuth challenge, so the admin grid hides that single button. Admins fix existing linkages (including unlinking); they don't establish new linkages on a user's behalf. No schema changes — the rename absorbed in this PR (`IsNotificationTarget` → `IsPrimary`) is C#-only with the EF column mapping pinned to the legacy column name. No follow-ups split off; full PR 4 lands in one shot.

## Goals

1. A verified email row can be flagged as the Google service email; exactly-one-per-User is service-enforced via single-batch flip.
2. A signed-in user can link an additional Google account from the grid (POST → `ChallengeResult` → existing OAuth callback wiring).
3. **Unlink** and **Delete** are dimensional inverses operating on disjoint row sets: Unlink runs on Provider-attached rows (removes `AspNetUserLogins` and the email row); Delete runs on plain rows (just removes the email row). Both end with the row gone — the distinction is which row type each operates on. Link↔Unlink is the provider-attachment pair; Add↔Delete is the row-lifecycle pair for plain rows.
4. Cross-User email collisions surface in the grid as a `MergePending` status pill, driven by the existing `AccountMergeRequest` flow.
5. Per-row badge displays the actual `Provider` value ("Google" today, future providers data-driven).
6. The token "OAuth" no longer appears in any service or repo method name. Provider-specific operations are parameterized.
7. A site admin (`User.IsInRole("Admin")`) can perform six grid actions (`SetPrimary`, `SetGoogle`, `AddEmail`, `Unlink`, `DeleteEmail`, `SetVisibility`) against any user's emails via the `/Profile/Admin/{userId}/Emails/...` route family. `Link` is excluded — Google OAuth requires the target user's own browser session, so admins cannot link on a user's behalf. Authorization is enforced via the existing resource-based handler pattern; service signatures stay auth-free (no `isPrivileged` boolean).

## Non-goals

- Dropping `IsOAuth`, `DisplayOrder`, `User.GoogleEmail`, `User.GetGoogleServiceEmail()` — gated on PR 7 and prod verification of upstream PRs.
- Renaming the `IsNotificationTarget` DB column. The C# rename is permanent; the column keeps its old name forever per the no-DB-renames-for-decoupling rule.
- Admin UI for the `AccountMergeRequest` review queue. The request is created and visible to existing admin tooling; the queue UI itself is out of scope.
- A separate `IUserEmailService.SetProviderAsync` for non-OAuth provider attachment — folded into `LinkAsync`.
- A "revert to default Google service email" affordance — explicitly dropped from the brainstorm. Switching means picking a different row; there is no "let the system pick" state worth restoring to.
- Adding `IUserEmailService` to `InterfaceMethodBudgetTests`. Net method count: 28 → 29 (`LinkAsync` replaces `AddOAuthEmailAsync` + `SetProviderAsync` → −1; `SetGoogleAsync`, `UnlinkAsync` add → +2; renames neutral). The interface is not currently in the budget list and we're not adding it as part of PR 4. No budget collision.
- **Admin override of cross-User merge.** When an admin adds an email on a target user's grid that collides with a verified row on a different User, the existing merge-request flow runs unchanged — an `AccountMergeRequest` is created in `Submitted` state. The admin does not "force-add" past the collision; admins resolve it via the existing merge admin queue. No admin-only bypass semantics in `AddEmailAsync`.
- **Admin-driven OAuth link.** Only the target user can complete a Google OAuth challenge — Google's IdP authenticates whoever is sitting at the keyboard, and admins should never hold user credentials. The admin grid hides the "Link Google account" button entirely. Admins fix existing linked emails (set as Google service email, change visibility, delete, etc.); establishing new linkages requires the user themselves.

## Architecture

Four concrete additions:

1. **Service-layer surface.** `IUserEmailService` gains `SetGoogleAsync(userId, emailId, ct)` (single-batch exclusive flip) and `LinkAsync(userId, provider, providerKey, email, ct)` (find-or-create email row with provider attached). `LinkAsync` replaces both `AddOAuthEmailAsync` and `SetProviderAsync` — 2 methods consolidate to 1. Service is auth-free; the `userId` is the **target** user (not the actor) and the controller authorizes the actor against the target before calling.
2. **Profile email grid rewrite** (`Views/Profile/Emails.cshtml`). Per-row Primary radio, Google service-email radio, Visibility dropdown. Per-row actions: a single contextual button — "Unlink" on Provider-attached rows (POSTs to `/Unlink/{id}`), "Delete" on plain rows (POSTs to `/{id}`). Bottom-of-grid: existing magic-link "Add email" + new "Link Google account" button. The view is parameterized by target user and form-action prefix so the same cshtml renders both the self-edit grid and the admin-edit grid.
3. **Controller surface — self.** `ProfileController.SetGoogleServiceEmail` (legacy) replaced by `SetGoogle(emailId)` on the existing `/Profile/Me/Emails/...` route family. New `Link(provider, returnUrl)` kicks an OAuth challenge for the named provider. New `Unlink(emailId)` removes the `AspNetUserLogins` row and the email row in one call (operates only on Provider-attached rows). Existing `DeleteEmail` operates only on plain rows (precondition: `Provider == null`).
4. **Controller surface — admin.** New `/Profile/Admin/{userId}/Emails/...` route family on `ProfileController` mirrors **six of the seven** self actions against a target user (`Link` is excluded — admins cannot complete the target's OAuth challenge; `Unlink` is included because it operates on already-stored data and needs no OAuth flow). Authorization on each admin action is `IAuthorizationService.AuthorizeAsync(User, targetUserId, UserEmailOperations.Edit)` — the same handler also gates the self routes (returning success when `User.Id == targetUserId`). Per the project's `architecture_no_admin_url_section` convention, admin pages live under `<Section>/Admin/*`, so `/Profile/Admin/{userId}/Emails/...` is the correct shape.

Cross-cutting cleanups absorbed into PR 4 (no follow-ups):

- `UserEmail.IsNotificationTarget` → `IsPrimary` C#-only rename. EF mapping pins `IsPrimary` at column `"IsNotificationTarget"`.
- `IUserEmailService.SetNotificationTargetAsync` → `SetPrimaryAsync`.
- `UserEmailRepository.SetNotificationTargetExclusiveAsync` → `SetPrimaryExclusiveAsync`.
- `UserEmailRepository.RewriteOAuthEmailAsync` → `RewriteLinkedEmailAsync` (predicate stays `Provider != null`).
- `AccountMergeService.AcceptAsync` — verify the five conflict-resolution rules at parent spec §184–190 are implemented; add any missing.
- `ProfileService` GDPR export — value source corrected to `e.IsGoogle` (not `Provider != null`). **JSON key stays `IsOAuth`** per `coding-rules.md` "Never Rename Fields in Serialized Objects" (the export is a JSON file users download). Earlier draft of this task called for renaming the key to `IsGoogle`; that was wrong and is reverted.

**Hard naming rule that drove the design.** No "OAuth" token in any method or route name. `Provider` (column name) is fine. Provider-specific operations are parameterized — `Link(provider, providerKey)` not `LinkGoogle`. An architecture test extension enforces this on `IUserEmailService` and `UserEmailRepository`.

## Authorization

New resource-based handler matching the existing `*OperationRequirement` + `*AuthorizationHandler` pattern (see `TeamOperationRequirement` / `TeamAuthorizationHandler`, `CampOperationRequirement` / `CampAuthorizationHandler`):

- **`UserEmailOperationRequirement` (Application layer)** — single `Edit` operation. (One requirement is sufficient; all seven grid actions are gated identically.)
- **`UserEmailAuthorizationHandler` (Web layer)** — resource is `Guid targetUserId`. Returns success when:
  - `context.User.GetUserId() == targetUserId` (self), or
  - `context.User.IsInRole("Admin")` (site admin).
- **Operations symbol** — `UserEmailOperations.Edit` (static class holding the requirement instance), matching the codebase's existing per-section `*Operations` static-class pattern.

Controller actions call `await _authz.AuthorizeAsync(User, targetUserId, UserEmailOperations.Edit)`; on failure return `Forbid()`. No `[Authorize(Roles = "Admin")]` attribute on the admin route family — authorization runs through the handler so the self/admin distinction stays in one place.

If a search of the codebase at impl time finds an existing handler that already covers per-user-edit authorization with the right semantics (e.g., a `ProfileAuthorizationHandler` or `UserAuthorizationHandler`), reuse it instead of creating a new one. The intent is one handler covering "may actor edit target's email settings"; do not multiply requirements.

Service signatures stay auth-free per the project's design rules — no `isPrivileged` boolean, no `IPrincipal` parameter, no role check inside the service. The `userId` parameter is the **target**, the actor identity never enters the service.

## Service & repo changes

### `IUserEmailService.SetGoogleAsync(Guid userId, Guid userEmailId, CancellationToken ct)`

- Owner-gated read via `_repo.GetByIdAndUserIdAsync` — returns `false` if not found.
- Verified-only guard — returns `false` if `!email.IsVerified`.
- Calls new repo method `SetGoogleExclusiveAsync(userId, emailId, ct)` — single-transaction flip: target row `IsGoogle = true`, all sibling rows for the user `IsGoogle = false`. Mirrors the renamed `SetPrimaryExclusiveAsync`.
- Invalidates `_fullProfileInvalidator.InvalidateAsync(userId)`.
- Audit log: `AuditAction.UserEmailGoogleSet`. Subject = target user; actor = current `HttpContext.User` resolved at the controller and threaded through (matches the pattern from commit 7bde7a96 — `audit-log: attribute shift signup entries to actor and subject`). Payload = email address + previous holder's email if any. Actor and subject differ on admin-driven edits and match on self-edit.

### `IUserEmailService.LinkAsync(Guid userId, string provider, string providerKey, string email, CancellationToken ct)`

- Find-or-create. Looks for an existing `UserEmail` row matching `email` (normalized) for `userId`; if found, sets `Provider`/`ProviderKey` on it; if not, creates a new row with `IsVerified = true`, `IsPrimary = false`, `Provider`/`ProviderKey` set.
- Replaces both `AddOAuthEmailAsync` and `SetProviderAsync` — net −1 method on the interface (2 removed, 1 added). Existing call site at `AccountController.cs:355` (`SetProviderAsync`) rewrites to `LinkAsync` (drops `userEmailId` arg, adds `email` arg).
- Invalidates `FullProfile` cache.
- Audit log: `AuditAction.UserEmailLinked`. Subject = target user; actor = current `HttpContext.User`. Payload = provider, email.

### `IUserEmailService.UnlinkAsync(Guid userId, Guid userEmailId, CancellationToken ct)`

- Owner-gated read via `_repo.GetByIdAndUserIdAsync` — returns `false` if not found.
- Pre-condition: `Provider != null && ProviderKey != null` on the row. If not, returns `false` (Unlink only operates on Provider-attached rows; the per-row UI never routes a plain row here).
- Calls `UserManager.RemoveLoginAsync(user, email.Provider, email.ProviderKey)` to remove the `AspNetUserLogins` row.
- Removes the email row entirely (same row removal as `DeleteEmailAsync`).
- Invalidates `_fullProfileInvalidator.InvalidateAsync(userId)`.
- Audit log: `AuditAction.UserEmailUnlinked`. Subject = target user; actor = current `HttpContext.User`. Payload = provider, providerKey hash (do not log the full key), email.
- No "you'd be locking yourself out" guard — magic link is always available as a fallback.

### `DeleteEmailAsync` — preserved behavior + precondition

The method on `UserEmailService` is unchanged in body. PR 4 adds one precondition:

- Pre-delete read: if `Provider != null` on the row, return `false`. Provider-attached rows go through `UnlinkAsync`, not `DeleteEmailAsync`. The per-row UI ensures this never happens at runtime; the service guards anyway.
- Existing cache-invalidation pattern stays.
- No `RemoveLoginAsync` cascade is needed — by the time `DeleteEmailAsync` executes, the row is already plain.

Unlink and Delete are mutually exclusive paths over disjoint row sets. Both end with the row gone; only Unlink also tears down `AspNetUserLogins`. The dimensional split (Link/Unlink = provider attachment, Add/Delete = plain-row lifecycle) keeps each method's contract narrow.

### `UserEmail.IsPrimary` rename

- C# property + EF mapping (`HasColumnName("IsNotificationTarget")`).
- Call sites updated in lockstep: service, repo, controller, view, tests, GDPR export.
- Coding-rules check during impl: confirm `IsNotificationTarget` is not a serialized JSON key. Audit at impl time per the no-rename-of-serialized-fields rule.

### `UserEmailRepository.RewriteLinkedEmailAsync` rename

- Predicate stays `e.Provider != null`. Single call site in the OAuth-callback rename-detection branch.

### `AccountMergeService.AcceptAsync` audit

Walk the method against parent spec §184–190:

- `UserEmails` fold with OR-combine flags; target's `IsPrimary` and `IsGoogle` preserved.
- `AspNetUserLogins` re-FK; constraint conflict → `Failed` state + admin-required.
- `EventParticipation` highest-status wins.
- `CommunicationPreference` most-recent `UpdatedAt` wins.
- `Tickets` / `RoleAssignment` / `AuditLog` re-FK with no resolution needed.

Any gap → add. The audit-surface explore reported the merge service looks complete; expect a small or zero diff.

### Interface budget

`IUserEmailService` is not currently in `InterfaceMethodBudgetTests`. Net change in PR 4: `LinkAsync` replaces `AddOAuthEmailAsync` + `SetProviderAsync` (−1); `SetGoogleAsync` adds (+1); `UnlinkAsync` adds (+1); renames neutral. **28 → 29.** No budget collision. Not adding the service to the budget list as part of PR 4.

## Controller actions & data flows

Every action authorizes via `_authz.AuthorizeAsync(User, targetUserId, UserEmailOperations.Edit)` before calling the service. On the self routes `targetUserId = User.GetUserId()`; on the admin routes `targetUserId` comes from the route. Handler returns success for self or for `User.IsInRole("Admin")`.

### Self routes (target = current user)

| Action | Route | Calls |
|---|---|---|
| `Link(string provider, string returnUrl)` | `POST /Profile/Me/Emails/Link/{provider}` | `SignInManager.ConfigureExternalAuthenticationProperties(provider, returnUrl)` → `ChallengeResult` |
| `Unlink(Guid emailId)` | `POST /Profile/Me/Emails/Unlink/{id}` | `_userEmailService.UnlinkAsync` (Provider-attached rows only) |
| `DeleteEmail(Guid emailId)` | `POST /Profile/Me/Emails/{id}` | `_userEmailService.DeleteEmailAsync` (plain rows only — Provider attached returns false) |
| `SetGoogle(Guid emailId)` | `POST /Profile/Me/Emails/SetGoogle` | `_userEmailService.SetGoogleAsync` |
| `SetPrimary(Guid emailId)` | `POST /Profile/Me/Emails/SetPrimary` | `_userEmailService.SetPrimaryAsync` |
| `AddEmail(string email)` | existing magic-link add | unchanged |
| `SetVisibility(...)` | existing | unchanged |

### Admin routes (target = `{userId}` from path)

| Action | Route | Calls |
|---|---|---|
| `AdminUnlink(Guid userId, Guid emailId)` | `POST /Profile/Admin/{userId}/Emails/Unlink/{emailId}` | `_userEmailService.UnlinkAsync(userId, emailId)` |
| `AdminDeleteEmail(Guid userId, Guid emailId)` | `POST /Profile/Admin/{userId}/Emails/{emailId}` | `_userEmailService.DeleteEmailAsync(userId, emailId)` |
| `AdminSetGoogle(Guid userId, Guid emailId)` | `POST /Profile/Admin/{userId}/Emails/SetGoogle` | `_userEmailService.SetGoogleAsync(userId, emailId)` |
| `AdminSetPrimary(Guid userId, Guid emailId)` | `POST /Profile/Admin/{userId}/Emails/SetPrimary` | `_userEmailService.SetPrimaryAsync(userId, emailId)` |
| `AdminAddEmail(Guid userId, string email)` | `POST /Profile/Admin/{userId}/Emails/Add` | `_userEmailService.AddEmailAsync(userId, email)` (existing magic-link path; merge collision still creates `AccountMergeRequest`) |
| `AdminSetVisibility(Guid userId, Guid emailId, ContactFieldVisibility v)` | `POST /Profile/Admin/{userId}/Emails/SetVisibility` | existing visibility setter, target = `userId` |

There is **no `AdminLink` action**. The `/Profile/Admin/{userId}/Emails/Link/...` route does not exist. Only the target user can establish an OAuth link to a new Google account, and they do so via the self route family (`/Profile/Me/Emails/Link/{provider}`).

Admin actions are factored as either separate methods or the existing actions parameterized by an optional `userId` route param — the implementer may pick whichever yields fewer LOC. The route shapes above are the contract; the C# action method names are not.

`SetGoogleServiceEmail` (legacy controller action) and its `IUserService.SetGoogleEmailAsync` call site are deleted. `IUserService.SetGoogleEmailAsync` itself stays — out of PR 4 scope (touches `User.GoogleEmail` shadow column, deletion gated on PR 7).

### Flow 1 — Set Google service email

User clicks Google radio on a verified row → form submits `emailId` → controller authorizes (User.IsAuthenticated + ownership check delegated to service) → `SetGoogleAsync` flips exclusively → cache invalidated → audit row → redirect back to grid.

### Flow 2 — Add email triggers cross-User merge

Existing wiring: `AddEmailAsync` creates an `AccountMergeRequest` when the new email matches a verified row on another User. PR 4 adds visibility — the grid view-model populates `MergePendingEmailIds` via `IAccountMergeService.GetPendingEmailIdsAsync`; the row's Status column shows a `MergePending` pill when matched.

### Flow 3 — Link Google account

Already-authenticated user clicks "Link Google account" → `POST /Profile/Me/Emails/Link/Google` → `ChallengeResult` redirects to Google → callback hits existing `ExternalLoginCallback` → user is signed in → `UserManager.AddLoginAsync` → existing PR 3 wiring at `AccountController.cs:355` calls `LinkAsync` (renamed from `SetProviderAsync`) → done.

`ExternalLoginCallback` may need a small branch for "user is already authenticated" (the link-while-signed-in case). Verify the current code path during impl. Outcome is either a no-op confirmation or the addition of that branch.

### Flow 4 — Delete email (plain row)

User clicks "Delete" on a plain row (no Provider) → `POST /Profile/Me/Emails/{id}` → `DeleteEmailAsync` precondition passes (Provider is null) → removes the email row → cache invalidated → audit row → redirect.

### Flow 5 — Unlink Google account (Provider-attached row)

User clicks "Unlink" on a Provider-attached row → `POST /Profile/Me/Emails/Unlink/{id}` → `UnlinkAsync` precondition passes (Provider non-null) → calls `RemoveLoginAsync` to drop `AspNetUserLogins` → removes the email row → cache invalidated → audit row (`UserEmailUnlinked`) → redirect.

Flows 4 and 5 are mutually exclusive over disjoint row sets — the per-row UI exposes exactly one of the two buttons based on whether the row has a Provider attached. A given row never qualifies for both paths simultaneously.

### Flow 6 — Admin edits another user's grid

Admin navigates to the target user's profile admin detail → clicks "Manage emails" link → arrives at `/Profile/Admin/{userId}/Emails` (admin grid view) → performs any of the **six** admin actions (`SetPrimary`, `SetGoogle`, `AddEmail`, `Unlink`, `DeleteEmail`, `SetVisibility`) → form posts to the matching `/Profile/Admin/{userId}/Emails/...` route → controller authorizes via `UserEmailOperations.Edit` (handler grants because actor is in `Admin` role) → service called with `userId` as the target → cache invalidated → audit row written with actor=admin, subject=target → redirect back to admin grid.

The admin grid renders without the "Link Google account" button. Establishing an OAuth link requires the target user's own browser session — Google authenticates whoever is at the keyboard, and admins should never hold user credentials. If a user needs help linking, the admin walks them to the user's own Profile page; admins fix existing linkages (Unlink included), they don't create them.

## View structure

### Self vs. admin grid — single cshtml

`Views/Profile/Emails.cshtml` is parameterized by a view-model that carries `TargetUserId`, a `RoutePrefix` ("/Profile/Me/Emails" for self, "/Profile/Admin/{userId}/Emails" for admin), and an `IsAdminContext` flag. Form actions are built from the prefix; column rendering is identical between self and admin. The admin route returns the same view with the admin prefix, no separate cshtml. A small admin-only banner at the top of the grid ("Editing emails for {target.DisplayName}") makes the actor/target distinction explicit when `IsAdminContext` is true.

When `IsAdminContext` is true, the "Link Google account" button (and the help line beneath it) are hidden — admins cannot drive a target user's OAuth flow. The six remaining admin actions (including `Unlink`) render normally.

The admin grid is reached from `Views/Profile/AdminDetail.cshtml` via a "Manage emails" contextual link — satisfies the "no orphan pages" rule.

### `Views/Profile/Emails.cshtml` — table columns

1. **Email** — address. Provider badge if `Provider != null`; badge label = the `Provider` value itself ("Google" today; future "Facebook" / "Microsoft" data-driven).
2. **Status** — pill: `Verified` / `MergePending` (new) / `Pending` / `Unverified`. The new `MergePending` mode is driven by the `MergePendingEmailIds` set on the view-model.
3. **Primary** — radio per row, exactly-one. Submits `SetPrimary` form on change. Copy: "Primary" (renamed from "Notification target").
4. **Google** — radio per row, exactly-one. Enabled on **any verified row** (not gated on `Provider`). Workspace APIs accept any address — `IsGoogle` just picks which address we hand to Workspace. Disabled on unverified rows. Tooltip on column header: "Email used for Google Workspace sync."
5. **Visibility** — existing dropdown.
6. **Actions** — single form-POST button per row, contextual on row state:
   - Provider-attached row → button labeled "Unlink Google account", form action `/Unlink/{id}`.
   - Plain row → button labeled "Delete", form action `/{id}`.
   Confirm dialog on click in both cases. Both end with the row gone; only Unlink also removes `AspNetUserLogins`.

### Below the grid

- Existing magic-link "Add email" form — unchanged.
- New "Link Google account" button — POSTs `/Profile/Me/Emails/Link/Google` with the current page as `returnUrl`. Always present (a User can have multiple linked Google accounts).
- Help line: "Adding via Google links your Google sign-in to that email."

### `Views/Profile/AdminDetail.cshtml`

- `IsGoogle` badge already present (lines 310–312, PR 3 cleanup) — keep.
- `IsNotificationTarget` bell badge (lines 326–331) → relabel to "Primary"; icon stays.

### Localization (resx, all locales en/es/de/fr/it)

- Add: `EmailGrid_StatusMergePending`, `EmailGrid_LinkGoogleAccount`, `EmailGrid_UnlinkGoogleAccount`, `EmailGrid_GoogleColumnTooltip`.
- Rename: `EmailGrid_NotificationTarget` → `EmailGrid_Primary`. Old key deleted (no shadow keys).

## Tests

### Service-level unit tests (`tests/Humans.Application.Tests/Services/Profile/UserEmailServiceTests.cs`)

- `SetGoogleAsync_FlipsExclusively`
- `SetGoogleAsync_RejectsOtherUser`
- `SetGoogleAsync_RejectsUnverified`
- `SetGoogleAsync_InvalidatesFullProfileCache`
- `LinkAsync_AttachesToExistingEmail`
- `LinkAsync_CreatesRowWhenMissing`
- `UnlinkAsync_RemovesAspNetUserLoginsAndEmailRow`
- `UnlinkAsync_RejectsRowWithoutProvider`
- `UnlinkAsync_AdminCanUnlinkAnyUser`
- `DeleteEmailAsync_RejectsProviderAttachedRow` — precondition guard; the row must be plain.
- `SetGoogleAsync_AdminCanEditAnyUser` — actor is in `Admin` role, target is a different user; assert success and that audit-log actor != subject.

### Cross-User merge integration test (new `tests/Humans.Web.Tests/Profile/EmailGridFlowTests.cs`)

- User A magic-link adds an email already verified on User B → assert `AccountMergeRequest` created in `Submitted` state, email row visible to A with status `MergePending`.
- **Authorization gate test** — unprivileged User C posts to `/Profile/Admin/{User-A-id}/Emails/SetGoogle` and `/Profile/Admin/{User-A-id}/Emails/Unlink/{emailId}` → both assert HTTP 403 (handler-driven), no service call, no DB mutation. Confirms the gate fails closed when actor is neither the target nor an admin, on both the SetGoogle and Unlink admin routes.
- **Admin-driven cross-User merge test** — admin posts `AddEmail` on target user A's admin grid with an email already verified on user B → assert `AccountMergeRequest` is created (not bypassed); admin does not get force-add semantics.

### `AccountMergeService` rule tests (one per parent-spec §184–190)

- `UserEmails` collision folds (OR-combine flags; target's `IsPrimary`/`IsGoogle` preserved).
- `AspNetUserLogins` re-FK; constraint conflict → `Failed` state + admin-required.
- `EventParticipation` highest-status.
- `CommunicationPreference` most-recent `UpdatedAt`.
- `Tickets` / `RoleAssignment` / `AuditLog` re-FK.

### `ProfileService` GDPR export test

- Assert exported JSON `"IsOAuth"` value sources from the row's `IsGoogle` column (not `Provider != null`). Seed `Provider = null` + `IsGoogle = true` and assert `"IsOAuth":true`.

### Architecture test extension

- Extend `UserArchitectureTests.cs` "no new reads/writes of stop-using fields" assertions to also forbid the token `"OAuth"` in method/property names of `IUserEmailService` and `UserEmailRepository`. Catches future regressions on the no-OAuth-in-method-names rule.

### Manual smoke (post-deploy to PR preview env)

- Add email via magic link → verify → flag Primary/Google → delete (plain row → "Delete" button).
- Link Google account → callback returns to grid → "Google" badge on row.
- Unlink a Provider-attached row → row gone, `AspNetUserLogins` gone, can re-Link the same email afterward.
- Confirm the per-row button is "Unlink" on Provider-attached rows and "Delete" on plain rows — never both, never the wrong one.
- Cross-User collision → `MergePending` pill shows.
- Admin `AcceptAsync` of merge request → emails fold correctly.
- Set `IsGoogle` on a `proton.me` magic-link row → Workspace sync uses that address (verify on next sync run, or unit-test the sync's email picker).
- Sign in as admin, navigate to a target user's profile admin detail → "Manage emails" → run all six admin actions (add, set primary, set Google, set visibility, unlink a Provider-attached row, delete a plain row) → confirm changes apply to the target user, audit-log entries record actor=admin / subject=target. Confirm the "Link Google account" button is **not** rendered on the admin grid.
- Sign in as non-admin user, attempt direct GET/POST to `/Profile/Admin/{other-user-id}/Emails/...` → expect 403.
- Sign in as admin, attempt direct POST to `/Profile/Admin/{other-user-id}/Emails/Link/Google` → expect 404 (route does not exist) or 405; confirm there's no admin-link backdoor.

## Risks

Low-medium. UI rework + repo/service surface changes; no schema changes. Mitigations:

- `AccountMergeService` rule tests catch regressions in the merge engine.
- Architecture test bans the "OAuth" token from method names → catches drift.
- Service-method renames are mechanical; ReSharper-led at impl time.
- The `ExternalLoginCallback` "user is already authenticated" branch is the only logic addition — covered by manual smoke.
- Admin-edit path mirrors self-edit; authorization gates each action via the existing handler pattern. Audit-log actor/subject split keeps admin actions traceable. Risk that an admin-driven cross-User collision is "force-resolved" is mitigated by routing through the same `AddEmailAsync` flow — no admin-only bypass exists in the service layer.
- Unlink and Delete are mutually exclusive paths — one for Provider-attached rows, one for plain rows. Per-row UI picks the correct route based on row state; service-level preconditions on both methods guard against misuse if a request arrives via a non-UI path. Both end with the row gone; only Unlink also tears down `AspNetUserLogins`.

## Why this over alternatives

- **vs. collapsing Unlink into Delete (the previous draft of this spec).** Link/Unlink and Add/Delete are dimensional pairs — each pair handles its own row state. Unlink and Delete both remove the row; the distinction is which row state each operates on (Provider-attached vs plain). Symmetric inverses, disjoint row sets. Collapsing them folds the OAuth-tear-down branch into `DeleteEmailAsync`'s body and obscures the symmetry on the service surface; keeping them separate aligns the method names with the user's mental model ("the link came off" vs "the email came off") and keeps each method's contract narrow.
- **vs. Google radio gated on `Provider == "Google"`.** Workspace APIs accept any email; the gate was overzealous. A magic-link-added `proton.me` address is a valid `IsGoogle` target — the user authenticates with Google's identity provider out-of-band.
- **vs. a separate "revert to default" affordance.** Dropped from PR 4 entirely. Once a user explicitly picks a Google service email, switching means picking a different row. There is no scenario where reverting to "let the system pick" is the right action — that surface was inertia from the parent spec's PR 3 known-limitations note.
- **vs. `LinkGoogle` / `UnlinkOAuth` provider-baked methods.** Parameterized `Link(provider, providerKey)` extends to Facebook/Microsoft/Apple without new methods, and keeps the no-"OAuth"-in-method-names rule clean.
