# Email Identity & OAuth Decoupling — Design Spec

**Date:** 2026-04-27
**Status:** Approved for PR breakdown
**Supersedes:** nobodies-collective/Humans#505, #506, #507, #560

## Goals

Achieve the following with no manual cleanup at 1k-user scale:

1. A human can sign in with multiple Google accounts (e.g., `peter@home`, `peter@work`) and reach the same Profile.
2. Tickets and mailing-list signups auto-create Users without requiring full Profile creation.
3. Marketing/transactional emails carry an unsubscribe link that lets recipients edit their `CommunicationPreference` matrix without signing in.
4. Ticket buyers can sign in (magic link or OAuth) and create a Profile attached to their existing User.
5. Users with a Profile see a grid of validated email addresses with two routing columns (Primary, Google), exclusive radio per column.
6. Users can merge accounts inadvertently created.
7. Users can add an email address that may belong to a separate placeholder User (from a ticket purchase under a different address); the system detects the cross-User collision and offers merge.
8. When a Google Workspace email is renamed (`foo@nobodies.team` → `bar@nobodies.team`), the system detects it on the next OAuth sign-in and switches the stored address automatically.

## Non-goals

- **Splitting User into Identity vs. Person vs. Profile entities.** Deferred indefinitely; `User` remains the conflated entity (Identity row + domain anchor for the human). Revisit only if specific pain emerges (e.g., importing donors as non-User contacts).
- **Per-email marketing flag.** `CommunicationPreference` is User × `MessageCategory` and stays that way.
- **Daily Google sync rename detection.** OAuth-callback comparison is sufficient for the 8 goals; defense-in-depth via the daily sync job can be added if gaps appear in practice.
- **Merging User and Profile section docs into one.** Tracked separately.
- **`ConsentRecord`.** Continues to handle legal-document signoffs only; not touched by this work.
- **Account UI restructuring beyond the email grid + linked-accounts panel.** Other Profile-edit pages stay as-is.

## Schema deltas

### `users` table (`User : IdentityUser<Guid>`)

| Column | Action | PR |
|--------|--------|-----|
| `Email`, `NormalizedEmail`, `EmailConfirmed`, `NormalizedUserName` | **Drop** (after virtual-property override) | 2 |
| `GoogleEmail` (and `GetGoogleServiceEmail()` helper) | **Drop** | 3 |
| `LastLoginAt`, `DisplayName`, `ProfilePictureUrl`, `CreatedAt`, etc. | Keep | — |

### `user_emails` table (`UserEmail`)

| Column | Action | Notes | PR |
|--------|--------|-------|-----|
| `Provider` (string, nullable) | **Add** | `"Google"` or null. Future: `"Apple"`, `"Microsoft"`. | 3 |
| `ProviderKey` (string, nullable) | **Add** | OIDC `sub` for the linked OAuth identity. | 3 |
| Unique constraint `(Provider, ProviderKey)` where both non-null | **Add** | One UserEmail row per OAuth identity | 3 |
| `IsGoogle` (bool) | **Add** | At most one true per `UserId`. User-controlled in the grid; not auto-derived. | 3 |
| `IsNotificationTarget` (bool) | **Keep** | Repurpose as the grid's Primary column. Optional cosmetic rename to `IsPrimary` at PR 4. | — |
| `IsVerified` (bool) | Keep | — | — |
| `Visibility` (`ContactFieldVisibility?`) | Keep | Separate profile-visibility concern; untouched. | — |
| `IsOAuth` (bool) | **Drop** | Replaced by `ProviderKey IS NOT NULL` | 3 |
| `DisplayOrder` (int) | **Drop** | Alphabetical sort | 3 |

### Identity DI

- `IdentityOptions.User.RequireUniqueEmail = false` (uniqueness enforced at the UserEmails layer).
- `.AddUserStore<HumansUserStore>()` registered in PR 2.

### Identity surgery (PR 2)

- Override `User.Email`, `User.NormalizedEmail`, `User.EmailConfirmed`, `User.NormalizedUserName` as `virtual` overrides:
  - **Getters** compute from `UserEmails` (`Email` = first `IsVerified` row, ordered by `IsNotificationTarget` desc).
  - **Setters** throw `InvalidOperationException`.
- `HumansUserStore : IUserStore<User>, IUserEmailStore<User>, IUserPasswordStore<User>` routes Identity's `FindByEmailAsync` / `GetEmailAsync` / `GetEmailConfirmedAsync` through `IUserEmailService`. Write methods (`SetEmailAsync`, `SetEmailConfirmedAsync`) throw `NotSupportedException`.
- `UserConfiguration` adds `b.Ignore(...)` for the four overridden properties.

## Behavior

### OAuth callback (`AccountController.ExternalLoginCallback`)

```
1. ExternalLoginSignInAsync(provider, sub)
   ├── match → load User
   │           load UserEmail WHERE (Provider, ProviderKey) = (provider, sub)
   │           if row.Email != claim.Email:
   │               update row.Email
   │               audit log "email rename detected: <old> -> <new>, sub=<sub>"
   │           sign in, return
   └── no match → step 2

2. FindUserByVerifiedEmailAsync(claim.Email) on UserEmails
   ├── found → AddLoginAsync(user, info)
   │           set Provider = provider, ProviderKey = sub on the matched UserEmail row
   │           sign in, return
   └── not found → step 3

3. IUserService.FindOrCreateUserByEmail(claim.Email, source: OAuthCallback)
   creates User + UserEmail (IsVerified=true, Provider/ProviderKey set)
   AddLoginAsync(user, info)
   sign in
```

The existing `FindUserByAnyEmailAsync` (verified-or-unverified) fallback and `User.Email`-based reads at `AccountController.cs:172-242` are removed; the structured 3-step flow above replaces them. The existing "lockout-then-relink-by-email" branch is removed (covered by the deletion guard preventing the lockout state from being created).

### `IUserService.FindOrCreateUserByEmail(email, source, ct)`

Single funnel for User + UserEmail creation. Behavior by source:

| Source | Behavior |
|--------|----------|
| `MailingListSignup` | If found, no-op on User (caller adjusts `CommunicationPreference` + sends double-opt-in). If not, create User + UserEmail (`IsVerified=false`, `IsNotificationTarget=true`); trigger double-opt-in. |
| `TicketCheckout` | If found, attach Ticket to that User. If not, create User + UserEmail (`IsVerified=true` post-Stripe + delivery), attach Ticket. Write `EventParticipation` row (`Year`, `Status=Ticketed`, `Source=TicketSync`). |
| `OAuthCallback` | If found, `AddLoginAsync` (handled in OAuth callback step 2). Funnel only fires for genuinely-new humans: create User + UserEmail (`IsVerified=true`, `Provider`/`ProviderKey` set), `AddLoginAsync`. |
| `MagicLinkSignIn` | **Lookup-only — never creates.** Returns null when no User has the email. |

Architecture test forbids `dbContext.Users.Add(...)` outside this funnel and Identity's own internal paths.

### Profile email grid (`Views/Profile/Emails.cshtml`)

```
Email                        Verified   Primary   Google
peter.drier@gmail.com           ✓         ●         ○
peter@nobodies.team             ✓         ○         ●
peter+tix@gmail.com             ✓         ○         ○
                                                            [Add email]
```

- `Verified` — read-only indicator.
- `Primary` — radio, exclusive across rows; row must be `IsVerified`.
- `Google` — radio, at most one selected; user-controlled freely (no constraint to OAuth-tied rows).
- `Add email` button → prompts for address → magic-link verification email → row appears in grid only after verify-click.
- **Inline cross-User merge:** if the entered address matches a UserEmail on another User, prompt:
  > *"This address is associated with another account [N tickets, profile/no profile]. Merge into yours?"*
  → magic-link to entered address proves ownership → call `AccountMergeService.MergeAsync(source, target)`.

### Linked OAuth Accounts panel (below the grid)

```
Linked sign-in methods
─────────────────────
Google (peter@nobodies.team)               [Unlink]
Google (peter.drier@gmail.com)             [Unlink]
                                            [Link another Google account]
Magic link via any verified email above
```

- Lists `AspNetUserLogins` rows. Display label resolves the linked email via `UserEmails` join on `(Provider, ProviderKey)`.
- `Unlink` → `UserManager.RemoveLoginAsync`, subject to deletion guard.
- `Link another Google account` → triggers OAuth challenge from authenticated session → callback returns to a page that calls `AddLoginAsync(currentUser, info)`.

### Deletion guard

Applied to `UserEmailService.DeleteEmailAsync` and `UserManager.RemoveLoginAsync`:

- **Block** if the operation would leave the user with **zero verified UserEmails AND zero AspNetUserLogins rows**.
- Error: *"Cannot remove your last sign-in method. Add another verified email or link an OAuth provider first."*

Replaces the existing `IsOAuth`-based block, which prevented deletion of the OAuth row even when the user had other valid auth methods.

### Unsubscribe link (`/preferences?token=...`)

- Token (matching the existing magic-link token format) encodes `(UserId, optional MessageCategory, expiry)`. Long-lived (~1 year) — these links live in mail histories.
- Page renders the existing `CommunicationPreference` matrix using `MessageCategoryExtensions.ActiveCategories`.
- If the token includes a category, that row is pre-focused with a one-click *"unsubscribe from <category>"* button.
- All updates set `UpdateSource = "MagicLink"`.
- No login required.
- Email templates inject the link into their footer. Applies to all transactional and marketing emails (`MessageCategory != System` and `!= CampaignCodes`, since those are always-on).

### Account merge

`AccountMergeService` (existing engine). New trigger surfaces:

- Profile email grid → Add email → cross-User collision detected (PR 4)
- Account settings → "I have another account" → enter email (PR 4)
- Magic-link sign-in while already authenticated as another User (PR 4)
- Admin-driven from `DuplicateAccountService` reports (existing)

Conflict resolution rules to **verify or add** in `AccountMergeService`:

- **UserEmails**: union; if `Email` collides between source and target, fold rows (OR-combine flags; `IsPrimary`/`IsGoogle` stay on target's existing row).
- **AspNetUserLogins**: re-FK from `source.UserId` to `target.UserId`. Constraint conflict (same `(Provider, ProviderKey)` on both Users) shouldn't happen given backfill; if it does, error out + require admin.
- **EventParticipation** (per `Year`): highest-status wins (`Attended` > `Ticketed` > `NotAttending` > `NoShow`).
- **CommunicationPreference** (per `Category`): most-recent `UpdatedAt` wins.
- **Tickets**, **RoleAssignment**, **AuditLog**: re-FK source → target, no conflict resolution needed.

## Migration / backfill

### PR 3 — populate `Provider`/`ProviderKey` on UserEmails

For each User with at least one `AspNetUserLogins` row:
1. Read the user's `UserEmails` and `AspNetUserLogins` rows.
2. For each AspNetUserLogins entry, find the UserEmail to tag:
   - Prefer the row with `IsOAuth = true` AND `Email` matching the historical Google email (resolved via `User.GoogleEmail` / `GetGoogleServiceEmail()` if set).
   - Fall back to the row matching `User.Email`.
   - Fall back to first row by `DisplayOrder`.
3. Set `Provider = "Google"`, `ProviderKey = AspNetUserLogins.ProviderKey` on that UserEmail.
4. If multiple AspNetUserLogins entries for the same user can't be uniquely matched, populate the first match and emit a warning to the migration log; admin reviews post-migration.

### PR 3 — populate `IsGoogle`

- Set `IsGoogle = true` on the row matching `User.GetGoogleServiceEmail()` (if set).
- Otherwise leave all rows `IsGoogle = false`. User picks via the grid post-migration.

### PR 2 — drop Identity columns

- Verified by EF migration reviewer agent (per `CLAUDE.md` migration gate).
- Coordinated with PR 1 so no production reads or writes remain when columns are dropped.

## PR sequence

Each PR independently shippable to production with no continuity loss; numbered in dependency order.

### Continuity / rollback summary

| Transition | Schema risk | Rollback |
|------------|-------------|----------|
| → PR 1 | None | Revert deploy |
| PR 1 → PR 2 | Drops 4 columns + 2 indexes; brief deploy downtime, no in-flight inconsistency | Forward-only after deploy |
| PR 2 → PR 3 | Adds Provider/ProviderKey/IsGoogle; backfills; drops IsOAuth/DisplayOrder | Forward-only after deploy |
| PR 3 → PR 4 | None | Revert deploy |
| PR 4 → PR 5 | None | Revert deploy |
| PR 5 → PR 6 | None | Revert deploy |

In-flight magic-link tokens, OAuth sessions, and auth cookies remain valid across all PRs (no token-format or storage-format changes).

### PR 1 — Decouple application code from Identity columns

**Replaces:** nobodies-collective/Humans#506

- Sweep all **reads** of `User.Email` / `NormalizedEmail` / `UserName` / `NormalizedUserName` to UserEmails queries.
- **Stop writes** to the four Identity columns in all User-creation paths:
  - `AccountController.ExternalLoginCallback:247-249` — set `UserName = user.Id.ToString()`; leave `Email`/`NormalizedEmail`/`EmailConfirmed` at defaults (null/null/false). UserEmail row still created via `AddOAuthEmailAsync` at line 263.
  - `AccountProvisioningService.cs:121` — same treatment.
  - `DevLoginController.cs:242, 598` — same treatment.
  - This is **the load-bearing prerequisite** for PR 2's setter-throws override; without it, PR 2 breaks user creation in production.
- Port the OAuth-callback link-by-email path from `User.Email` to UserEmails.
- Replace the `IsOAuth`-based deletion guard with the "preserve at least one auth method" invariant.
- Delete `GoogleAdminService.BackfillEmails` action + service method + admin UI entry point.
- Delete the `MagicLinkService.cs:77, 155, 171` Identity-column fallback queries.
- Anonymization paths (`OnboardingService:657`, `DuplicateAccountService:355`, `AccountMergeService:203`, `ProcessAccountDeletionsJob:171`) stop writing the four Identity email columns.
- DI: `RequireUniqueEmail = false`.
- Architecture test forbids reads **and writes** of the four Identity columns outside `Infrastructure/Data/`.
- **No schema changes.**

**Risk:** Medium (touches many call sites). Mitigation: architecture test + browser smoke for sign-in flows (magic link, Google OAuth, account with multi-email).

### PR 2 — Override Identity properties + drop columns

**Replaces:** nobodies-collective/Humans#560

- Override `User.Email`, `User.NormalizedEmail`, `User.EmailConfirmed`, `User.NormalizedUserName` (compute from UserEmails / throw on set).
- Implement `HumansUserStore : IUserStore<User>, IUserEmailStore<User>, IUserPasswordStore<User>`.
- `UserConfiguration` adds `b.Ignore(...)` for the four overrides.
- EF migration drops the four columns + `EmailIndex` + `UserNameIndex`.
- Architecture test extended to forbid writes/mappings.

**Risk:** High (Identity surgery). Mitigation: HumansUserStore unit tests + full sign-in regression (magic link via any verified email, OAuth via Google, multi-email user, admin user impersonation if any).

### PR 3 — UserEmails modernization + rename detection

**Replaces:** nobodies-collective/Humans#505 + #507

- Add `Provider`, `ProviderKey` columns on `UserEmail`; unique constraint where both non-null.
- Add `IsGoogle` column; partial unique constraint (one true per `UserId`).
- Drop `IsOAuth`, `DisplayOrder`.
- Drop `User.GoogleEmail`, `User.GetGoogleServiceEmail()`.
- Backfill per the rules above.
- OAuth callback (success branch): rename detection (lookup by `(Provider, ProviderKey)`, compare claim, update if differ).
- OAuth callback (link-by-email branch): on `AddLoginAsync`, set `Provider`/`ProviderKey` on the matched UserEmail row.
- `GoogleWorkspaceSyncService.cs:2039+`: read from `IsGoogle`-flagged row (fallback to first `Provider != null` row).
- `GoogleAdminService` queries change from `IsOAuth` to `Provider != null`.
- Architecture test forbids references to deleted symbols.

**Risk:** Medium. Mitigation: backfill verified against staging snapshot before prod migration; rename-detection covered by a smoke test (manually rename a Workspace alias, sign in, confirm UserEmail updated + audit log entry).

### PR 4 — Profile email grid + Linked Accounts UI + merge UX

**New issue.**

- Replace `Views/Profile/Emails.cshtml` with the grid (Verified / Primary / Google).
- Inline cross-User merge detection on Add Email → magic-link verify → `AccountMergeService`.
- Linked OAuth Accounts panel below the grid (display + Unlink + "Link another Google account").
- Delete the IsOAuth pill at `Views/Profile/AdminDetail.cshtml:309`.
- Delete the "Sign in" badge at `Views/Profile/Emails.cshtml:54`.
- Delete the `@if (!email.IsOAuth)` delete-button guard at `Views/Profile/Emails.cshtml:126`.
- Verify/add `AccountMergeService` conflict resolution per the rules above.
- New `ProfileController` action for the in-session OAuth re-challenge ("Link another Google account").

**Risk:** Low-medium (UI rework + merge engine touchups; engine itself is existing code).

### PR 5 — Unsubscribe link

**New issue.**

- New `PreferencesController` with `GET /preferences?token=...` route.
- Token format reuses `MagicLinkService` token mechanism (signed payload with expiry).
- Page renders `CommunicationPreference` matrix; pre-focuses the category from the token if present.
- One-click "unsubscribe from <category>" action sets `OptedOut=true` for that category.
- All transactional + marketing email templates get the unsubscribe-link footer (`MessageCategory != System` and `!= CampaignCodes`).
- `UpdateSource = "MagicLink"` for all changes via this path.

**Risk:** Low. Mitigation: token-tampering test, expired-token test, audit verification.

### PR 6 — Single User-creation funnel

**New issue.**

- `IUserService.FindOrCreateUserByEmail(string email, FindOrCreateSource source, CancellationToken ct)`.
- Used by:
  - `AccountController.ExternalLoginCallback` step 3
  - Mailing-list signup endpoint
  - Ticket-checkout flow (post-Stripe webhook handler)
  - `MagicLinkService` does NOT create — uses `FindByEmailAsync` only
- Architecture test forbidding `dbContext.Users.Add(...)` outside this funnel + `HumansUserStore` (Identity's required path).
- Verify: every existing User-creation site is migrated to use the funnel; no orphan paths.

**Risk:** Medium (touches multiple entry points). Mitigation: comprehensive test per source + deduplication regression (create user via mailing-list, then try ticket purchase with same email — must reuse, not create).

## Affected files (rough)

### Domain
- `src/Humans.Domain/Entities/User.cs` — virtual overrides (PR 2), drop `GoogleEmail` (PR 3)
- `src/Humans.Domain/Entities/UserEmail.cs` — schema changes (PR 3)

### Infrastructure
- `src/Humans.Infrastructure/Data/Configurations/UserConfiguration.cs` — `Ignore()` for overrides (PR 2)
- `src/Humans.Infrastructure/Data/Configurations/UserEmailConfiguration.cs` — column mappings + unique constraint (PR 3)
- `src/Humans.Infrastructure/Identity/HumansUserStore.cs` — new (PR 2)
- `src/Humans.Infrastructure/Migrations/{ts}_DropIdentityEmailColumns.cs` — PR 2
- `src/Humans.Infrastructure/Migrations/{ts}_UserEmailsModernization.cs` — PR 3 (schema + backfill)
- `src/Humans.Infrastructure/Services/UserEmailService.cs` — deletion guard (PR 1), Provider/ProviderKey writes (PR 3)
- `src/Humans.Infrastructure/Services/UserService.cs` — `FindOrCreateUserByEmail` (PR 6)
- `src/Humans.Infrastructure/Services/MagicLinkService.cs` — drop fallbacks (PR 1)
- `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` — `IsGoogle` read (PR 3)
- `src/Humans.Infrastructure/Services/GoogleAdminService.cs` — drop `BackfillEmails` (PR 1) + `IsOAuth` refs (PR 3)
- `src/Humans.Infrastructure/Services/AccountMergeService.cs` — verify/add conflict resolution (PR 4)
- `src/Humans.Infrastructure/Services/AccountProvisioningService.cs` — funnel (PR 6)
- `src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs` — drop Identity-column writes (PR 1)

### Web
- `src/Humans.Web/Controllers/AccountController.cs` — OAuth callback restructure (PR 1, PR 3)
- `src/Humans.Web/Controllers/ProfileController.cs` — grid actions (PR 4)
- `src/Humans.Web/Controllers/PreferencesController.cs` — new (PR 5)
- `src/Humans.Web/Controllers/DevLoginController.cs` — port creation paths to funnel (PR 6)
- `src/Humans.Web/Views/Profile/Emails.cshtml` — grid rewrite (PR 4)
- `src/Humans.Web/Views/Profile/AdminDetail.cshtml` — drop IsOAuth pill (PR 4)
- `src/Humans.Web/Views/Preferences/Index.cshtml` — new (PR 5)
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — `RequireUniqueEmail = false` (PR 1), `AddUserStore<HumansUserStore>()` (PR 2)
- Email templates (Razor + HTML) — unsubscribe-link footer injection (PR 5)

### Tests
- `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs` — extended assertions across PRs 1, 2, 3, 6
- Service tests for `UserService.FindOrCreateUserByEmail` (PR 6)
- HumansUserStore tests (PR 2)
- Rename-detection smoke test (PR 3)
- Cross-User add-email merge tests (PR 4)
- Token tampering / expiry tests for `/preferences` (PR 5)
- Test fixtures: stop setting `Email` / `NormalizedEmail` / `IsOAuth` / `DisplayOrder` on Users; create UserEmail rows with `Provider`/`ProviderKey` instead.

## Open questions for implementation phase

Decisions to make at PR time, captured here so they're not invented again:

1. **Backfill ambiguity.** When a User has multiple `AspNetUserLogins` rows but the historical sub-to-email mapping is ambiguous, the migration picks first-match + warns. Decide at PR 3 whether ambiguous cases require admin review pre-migration or are tolerable warnings.
2. **Token format for unsubscribe links.** Reuse the `MagicLinkService` signed-payload format (preferred — single token implementation), or use a separate scheme.
3. **Cosmetic rename of `IsNotificationTarget` → `IsPrimary`.** The semantics line up; the column rename is optional polish at PR 4.
4. **"Link another Google account" UX.** Default is OAuth dance from authenticated session; reconfirm at PR 4 whether to add a magic-link confirmation step on top.

## Why this over alternatives

- **vs Person/User/Profile entity split.** Doesn't unlock anything required by the 8 goals. Defer.
- **vs keeping `IsOAuth` flag.** Useless bool; `Provider`/`ProviderKey` carry the actual information needed for rename detection.
- **vs storing OAuth → email link on `AspNetUserLogins`.** The email row is the user-meaningful entity; storing its OAuth lineage on the row whose meaning depends on it is the more direct mapping. Avoids extending Identity tables.
- **vs removing auto-link-by-email at OAuth callback.** Would create duplicate Users in three common scenarios (mailing-list-then-Google, tickets-then-Google, manually-added-then-Google). At 1k-user scale, "one human, one User" is a hard requirement; auto-link enforces it. The theoretical Workspace-rename-then-reassign attack is admin-controlled and acceptable risk.
- **vs `User.IsSubscribedToMarketing` bool.** `CommunicationPreference` (User × MessageCategory matrix with `OptedOut` + `InboxEnabled`) already exists.
