# Email Identity & OAuth Decoupling — Design Spec

**Date:** 2026-04-27 (revised 2026-04-28, 2026-04-29)
**Status:** Approved for PR breakdown
**Supersedes:** nobodies-collective/Humans#505, #506, #507, #560

**Revision 2026-04-28 (PR 1 / PR 2 split):** Read sweep moved from PR 1 to PR 2.
PR 1 stops writes only and adds an admin-triggered backfill button + restructures the
OAuth-callback new-account-creation branch to remove silent-duplicate risk. PR 2 absorbs
the read sweep.

**Revision 2026-04-29 (no column drops; "stop using" only):** Schema changes are
forward-only and dangerous. Every PR in this sequence achieves its goal by
**ceasing to use** the legacy column or field — never by dropping it. Property
overrides + read sweeps + write-stop are the migration; the column on disk is
left alone. Concretely:

- **PR 2 does NOT drop `Email` / `NormalizedEmail` / `EmailConfirmed` / `UserName` / `NormalizedUserName`** from `aspnetusers`. Override getters fall back to `base.X` so legacy LINQ-on-column queries keep working unchanged. Setters write through to `base.X` (not `throw`) so any unswept site doesn't break.
- **PR 2 does NOT add `b.Ignore(...)`** in `UserConfiguration`, **does NOT introduce `HumansUserStore`**, and ships **no EF migration**.
- **PR 3 does NOT drop `IsOAuth`, `DisplayOrder`, `User.GoogleEmail`, or `GetGoogleServiceEmail()`.** It adds `Provider`/`ProviderKey`/`IsGoogle` to `UserEmail` and routes new code through them. The legacy fields stay.
- **The whole sequence may run all the way through PR 6 with the legacy columns still present.** Drops are a separate, optional, deferred concern — only consider them after the full sequence has been verified end-to-end in production, and only if there is a concrete operational reason to do so. There is no rush.

The earlier draft of PR 2 attempted a column drop + `Ignore()` + `HumansUserStore`; the cost of that approach was real (preview environment users disappeared from the UI on first deploy). The revised PR 2 is override + read sweeps only.

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
| `Email`, `NormalizedEmail`, `EmailConfirmed`, `UserName`, `NormalizedUserName` | **Stop using** (virtual-property override + read sweep route through `UserEmails`). Column kept on disk indefinitely; drop is deferred and may never happen. | 2 |
| `GoogleEmail` (and `GetGoogleServiceEmail()` helper) | **Stop using** (read sweeps route through new `IsGoogle` / `Provider != null` UserEmail rows). Field kept indefinitely. | 3 |
| `LastLoginAt`, `DisplayName`, `ProfilePictureUrl`, `CreatedAt`, etc. | Keep | — |

### `user_emails` table (`UserEmail`)

| Column | Action | Notes | PR |
|--------|--------|-------|-----|
| `Provider` (string, nullable) | **Add** | `"Google"` or null. Future: `"Apple"`, `"Microsoft"`. | 3 |
| `ProviderKey` (string, nullable) | **Add** | OIDC `sub` for the linked OAuth identity. **Single-row-per-pair is service-enforced** (lookup-then-update inside one transaction); no DB unique index per `feedback_db_enforcement_minimal` (service is the contract). | 3 |
| `IsGoogle` (bool) | **Add** | User-controlled in the grid; not auto-derived. **At-most-one-true-per-`UserId` is service-enforced** (clear all sibling rows in the same transaction before setting); no DB partial unique index per `feedback_db_enforcement_minimal`. | 3 |
| `IsNotificationTarget` (bool) | **Keep** | Repurpose as the grid's Primary column. Optional cosmetic rename to `IsPrimary` at PR 4. | — |
| `IsVerified` (bool) | Keep | — | — |
| `Visibility` (`ContactFieldVisibility?`) | Keep | Separate profile-visibility concern; untouched. | — |
| `IsOAuth` (bool) | **Stop using** (replaced by `ProviderKey IS NOT NULL`). Column kept indefinitely. | | 3 |
| `DisplayOrder` (int) | **Stop using** (alphabetical sort). Column kept indefinitely. | | 3 |

### Identity DI

- `IdentityOptions.User.RequireUniqueEmail = false` (uniqueness enforced at the UserEmails layer).
- **No custom user store.** Default Identity `UserStore<User>` is retained; the User entity's virtual property overrides are sufficient to route reads through `UserEmails`. The earlier draft introduced `HumansUserStore`; that is **not** part of the revised plan.

### Identity surgery (PR 2)

- Override `User.Email`, `User.NormalizedEmail`, `User.EmailConfirmed`, `User.UserName`, `User.NormalizedUserName` as `virtual` overrides:
  - **Getters** compute from `UserEmails` (`Email` = first `IsVerified` row ordered by `IsNotificationTarget` desc) with **`?? base.X` fallback** so cloned-prod data without UserEmails rows continues to read the legacy column.
  - **Setters write through to `base.X`.** Do **not** throw — any unswept caller (legacy LINQ on the column, third-party code path) must continue to work.
  - `UserName` falls back to `Id.ToString()` so Identity's username-uniqueness validator always sees a non-empty unique value without callers writing it.
- **No `b.Ignore(...)` in `UserConfiguration`.** The columns remain mapped; reads against the `aspnetusers.email` column directly (`db.Users.Where(u => u.Email == ...)`) continue to work, just with the legacy column value rather than the override-computed one. The decoupling goal is met by the override + read sweeps; the legacy LINQ sites are a separate concern (see "deferred legacy LINQ refactor" below).

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

### PR 3 — populate `Provider`/`ProviderKey` on UserEmails (admin button, not migration body)

Same pattern as PR 1's UserEmail-row backfill button. Idempotent (safe to re-run). Audited per UserEmail row updated. For each User with at least one `AspNetUserLogins` row:
1. Read the user's `UserEmails` and `AspNetUserLogins` rows.
2. For each AspNetUserLogins entry, find the UserEmail to tag:
   - Prefer the row with `IsOAuth = true` AND `Email` matching the historical Google email (resolved via `User.GoogleEmail` / `GetGoogleServiceEmail()` if set).
   - Fall back to the row matching `User.Email`.
   - Fall back to first row by `DisplayOrder`.
3. Set `Provider = "Google"`, `ProviderKey = AspNetUserLogins.ProviderKey` on that UserEmail.
4. If multiple AspNetUserLogins entries for the same user can't be uniquely matched, populate the first match and emit a warning to the migration log; admin reviews post-migration.

### PR 3 — populate `IsGoogle` (same admin button)

- Set `IsGoogle = true` on the row matching the legacy `User.GoogleEmail` value (read via `EF.Property<string?>(user, "GoogleEmail")` since the C# property is deleted; column remains modeled as a shadow property). If `GoogleEmail` is null, look up by `EF.Property<bool>(ue, "IsOAuth") == true` instead. Set `IsGoogle = false` on all sibling rows.
- Otherwise leave all rows `IsGoogle = false`. User picks via the grid post-migration (PR 4).

### PR 1 — admin backfill button (one-shot)

- New admin endpoint that finds Users missing a UserEmail row and inserts one from `User.Email` / `User.EmailConfirmed`. Idempotent (`WHERE NOT EXISTS`). Audited per insert. Returns operator-visible count.
- Operator clicks on QA, verifies count = 0 (or the expected legacy-orphan count), then on prod, verifies, then PR 2 ships.

### PR 2 — stop reading and writing Identity columns (no schema change)

- **No EF migration.** Columns are not dropped.
- Override + read-sweep are the migration. Once shipped, new code paths route through `UserEmails`; legacy paths that still hit the column directly continue to work.
- Anonymization writes in `UserRepository` (rename / merge / purge / deletion) are removed because they are redundant with the UserEmails-row removal that the same paths perform. Removing the writes does not require dropping the columns.

## PR sequence

Each PR independently shippable to production with no continuity loss; numbered in dependency order.

### Continuity / rollback summary

| Transition | Schema risk | Rollback |
|------------|-------------|----------|
| → PR 1 | None (write-decouple + admin button) | Revert deploy |
| PR 1 → PR 2 | None (override + read sweep; columns kept on disk) | Revert deploy |
| PR 2 → PR 3 | Adds Provider/ProviderKey/IsGoogle to `user_emails`; backfills new columns. Legacy `IsOAuth`/`DisplayOrder` stay on disk. | Revert deploy (additive migration; columns can sit unused if rolled back) |
| PR 3 → PR 4 | None | Revert deploy |
| PR 4 → PR 5 | None | Revert deploy |
| PR 5 → PR 6 | None | Revert deploy |

The decoupling sequence ships **without dropping a single legacy column.** Schema drops, if ever undertaken, are a deferred follow-up after the full sequence has been verified end-to-end in production — not part of this work.

In-flight magic-link tokens, OAuth sessions, and auth cookies remain valid across all PRs (no token-format or storage-format changes).

### PR 1 — Stop writes + admin backfill button

**Replaces:** nobodies-collective/Humans#506 (write-decoupling half only — read sweep deferred to PR 2)

**Scope (writes only — reads stay until PR 2):**

- **Stop writes** to `Email` / `NormalizedEmail` / `EmailConfirmed` / `UserName` / `NormalizedUserName` in all User-creation paths. Set `UserName = user.Id.ToString()` (Identity requires non-empty unique UserName); leave the four email columns at defaults (null/null/false/null). `NormalizedUserName` is auto-populated by Identity from `UserName`. UserEmail rows continue to be created in the same paths via `AddOAuthEmailAsync` / direct entity add.
  - `AccountController.ExternalLoginCallback` — new-User branch (currently around line 245-254).
  - `AccountController.CompleteSignup` — magic-link-signup branch (currently around line 387-395). **Spec previously omitted this site; included here.**
  - `AccountProvisioningService.FindOrCreateUserByEmailAsync` (currently around line 118-126).
  - `DevLoginController.EnsurePersonaAsync` (currently around line 218-227).
  - `DevLoginController.SeedProfilelessUserAsync` (currently around line 573-582).
  - This is **the load-bearing prerequisite** for PR 2's setter-throws override; without it, PR 2 breaks user creation in production.

- **Anonymization writes in `UserRepository`** (lines 274-277, 320-323, 425-428, 496-499) **stay through PR 1**. PR 2 removes them outright (the UserEmails-row removal already clears the email surface area; the Identity-column writes are redundant). The columns themselves remain on disk; removing the writes does not require dropping them. Spec previously claimed these writes were in `OnboardingService` / `DuplicateAccountService` / `AccountMergeService` / `ProcessAccountDeletionsJob`; that was wrong — those services call into UserRepository methods which are the actual write sites.

- **Restructure `AccountController.ExternalLoginCallback` new-User branch** to remove silent-duplicate risk. Today's `RequireUniqueEmail = true` rejects a duplicate-create at the Identity layer if `AddLoginAsync` to an existing user fails and the code falls through. After PR 1 sets `RequireUniqueEmail = false`, that fallback would silently create a duplicate. Fix: after `CreateAsync` succeeds, if `AddLoginAsync` fails, delete the just-created User (best-effort) and return the login error view. Do **not** allow a half-provisioned User to persist.

- **Replace the `IsOAuth`-based deletion guard** in `UserEmailService.DeleteEmailAsync` (line 251-252) with the "preserve at least one auth method" invariant: count remaining verified UserEmail rows + AspNetUserLogins rows after the deletion; block if both would be zero. The OAuth-tied UserEmail row is now deletable as long as another auth method remains. Use `_userManager.GetLoginsAsync(user)` to count the OAuth side.

- **Add admin backfill button** at `/Admin/Users/BackfillUserEmails` (or under existing maintenance area). Idempotent — for each User missing a UserEmail row, insert one from `User.Email` / `User.EmailConfirmed`, audit-log per insert, return count. Operator clicks on QA, verifies, then on prod, verifies, then PR 2 ships. **This replaces the migration-side backfill from earlier drafts of the spec.**

- **DI:** `RequireUniqueEmail = false` in `Program.cs:169`.

- **Architecture test (writes only):** forbid `Email = ` / `NormalizedEmail = ` / `EmailConfirmed = ` / `UserName = ` / `NormalizedUserName = ` writes in `Humans.Application` and `Humans.Web` assemblies. `Humans.Infrastructure` (UserRepository anonymization writes) and `Humans.Web.Controllers.DevLoginController` (transitional — the `UserName = id.ToString()` write) are exempt. Reads are unrestricted in PR 1.

- **No schema changes. No reads removed. Lockout-relink branch and `FindUserByAnyEmailAsync` branch in OAuth callback stay** — they protect legacy data and are removed in PR 2 along with the read sweep.

- **Items previously listed in PR 1 that are removed:** (a) "Delete `GoogleAdminService.BackfillEmails`" — already deleted in upstream; nothing to do. (b) "Delete `MagicLinkService.cs:77, 155, 171` Identity-column fallback queries" — moved to PR 2. (c) "Anonymization paths stop writing Identity columns" — moved to PR 2 (the writes are removed because they are redundant with the UserEmails-row removal; columns themselves stay).

**Risk:** Low (write-only, leaves all read paths intact, no functional regression for legacy data). Mitigation: architecture test + browser smoke for OAuth signin (existing user, new user), magic-link signup (existing user, new user), dev login (legacy persona reuse, new persona seed), email-add+delete flow on a multi-email account, admin backfill button on QA.

### PR 2 — Override Identity properties + read sweep (no schema change)

**Replaces:** nobodies-collective/Humans#560

- **Override `User.Email` / `NormalizedEmail` / `EmailConfirmed` / `UserName` / `NormalizedUserName`** as `virtual` overrides: getters compute from `UserEmails` with `?? base.X` fallback; setters write through to `base.X`. `UserName` falls back to `Id.ToString()` when the base value is null so Identity's validators always see a unique non-empty value. **No `throw` in setters.** The override is non-destructive — it routes new code through `UserEmails` while preserving the legacy column for any LINQ site that still queries it.
- **No EF migration. No `b.Ignore(...)`. No `HumansUserStore`. No column drops.**
- Sweep all reads of `User.Email` / `NormalizedEmail` / `UserName` / `NormalizedUserName` (in callers that materialize User then read the property) to UserEmails queries:
  - `MagicLinkService.SendMagicLinkAsync` step 2 fallback (`FindByEmailAsync`).
  - `MagicLinkService.FindUserByVerifiedEmailAsync` final fallback (`FindByEmailAsync`).
  - `MagicLinkService.FindUserByAnyEmailAsync` step 2 (`GetByNormalizedEmailAsync`) — **method removed** (sole caller `AccountController.ExternalLoginCallback` is also removed).
  - `AccountProvisioningService.FindOrCreateUserByEmailAsync` step 2 (`GetByEmailOrAlternateAsync`).
  - `AccountController.ExternalLoginCallback` lockout-relink branch and `FindUserByAnyEmailAsync` branch — remove entirely (lockout-relink is covered by PR 1's deletion guard; the any-email fallback is redundant under the override semantics).
  - `DevLoginController.EnsurePersonaAsync` (`FindByEmailAsync` legacy-persona path) — route through `IUserEmailService.GetUserIdByVerifiedEmailAsync`.
  - `DevLoginController.SignIn` (`FindByEmailAsync` post-seed path) — route through `IUserEmailService.GetUserIdByVerifiedEmailAsync`.
  - `UserEmailService.GetNotificationTargetEmailsAsync` (User.Email fallback).
  - `GoogleWorkspaceSyncService` and `GoogleAdminService.GetByEmailOrAlternateAsync` — repo method reimplemented to query `user_emails` first, fall back to `User.GoogleEmail`.
  - `DevelopmentDashboardSeeder` LIKE filter — match the seed marker via `UserEmails` instead of `u.Email`. Seeder also creates a verified `UserEmail` row for each dev human.
  - Test fixture (`HumansWebApplicationFactory`) updated to seed `UserEmails` rows.
- **Anonymization writes removed in `UserRepository`** (rename / merge / purge / deletion paths). The `UserEmails`-row removal already clears the email surface area; the four Identity-column writes were redundant. Removing them does **not** require dropping the columns.
- **Architecture test** forbids `set_Email` / `set_NormalizedEmail` / `set_EmailConfirmed` / `set_UserName` / `set_NormalizedUserName` writes in `Humans.Application` and `Humans.Web` solution-wide. **Reads stay legal** — legacy LINQ filter sites (`db.Users.Where(u => u.Email.Contains(...))`) continue to work and are a separate refactor concern (see "deferred legacy LINQ refactor" below).
- **`/Contacts` admin surface removed** as orphan concept (issue §8): controller, three views, view models, service, interface, DI, nav link, dead `IUserService.GetContactUsersAsync` method.
- **Dead methods deleted** per architecture test + reforge enumeration: `IUserService.GetContactUsersAsync`, `IUserRepository.GetByNormalizedEmailAsync`, `IMagicLinkService.FindUserByAnyEmailAsync`.

**Deferred legacy LINQ refactor (out of PR 2 scope):** the architecture test and reforge enumeration surfaced ~5 sites that still filter on `User.Email` directly via EF-translated LINQ — `ProfileService` admin search, `DuplicateAccountService` duplicate detection, `DriveActivityMonitorRepository`, `DevLoginController` dev list query, feedback-message admin path. Each needs a use-case-specific replacement (e.g., `DuplicateAccountService` should join across **verified UserEmails**, not filter `User.Email` alone). These are tracked in a separate issue and **do not block** the override or the read sweep — the column is still there, the queries still work, the decoupling goal is met.

**Risk:** Low-medium. Mitigation: arch test + browser smoke covering magic link via any verified email, OAuth via Google (existing user, new user), multi-email user, dev personas (legacy persona reuse, new persona seed), admin user impersonation if any.

### PR 3 — UserEmails modernization + rename detection (additive only)

**Replaces:** nobodies-collective/Humans#505 + #507

- **Additive schema (AddColumn only — no indexes, no unique constraints):**
  - Add `Provider`, `ProviderKey` columns on `UserEmail`. Single-row-per-pair invariant is **service-enforced**, not DB-enforced (lookup-then-update in one transaction inside `UserEmailService`).
  - Add `IsGoogle` column. At-most-one-true-per-`UserId` invariant is **service-enforced** (clear sibling rows in the same transaction before setting).
  - Migration is 100% auto-generated by `dotnet ef migrations add` — `AddColumn` only, no `migrationBuilder.Sql`, no manual edits, no `CreateIndex` for the invariants above.
- **Stop using legacy fields** (DB columns kept on disk; C# properties/methods deleted in this PR; columns remapped as **EF shadow properties** so the model still knows about the column and the migration scaffolder does not generate a `DropColumn`):
  - **`UserEmail.IsOAuth`** — public C# property deleted. `UserEmailConfiguration` declares `b.Property<bool>("IsOAuth").HasColumnName("IsOAuth").HasDefaultValue(false)`. The backfill reads it via `EF.Property<bool>(ue, "IsOAuth")`; nothing else does. Replaced semantically by `Provider != null` for Auth-side reads; replaced by `IsGoogle` for Google-Workspace-side reads.
  - **`UserEmail.DisplayOrder`** — public C# property deleted. `UserEmailConfiguration` declares `b.Property<int>("DisplayOrder").HasColumnName("DisplayOrder").HasDefaultValue(0)`. No reader survives PR 3; sort is alphabetical.
  - **`User.GoogleEmail` and `User.GetGoogleServiceEmail()`** — public C# property + helper deleted. `UserConfiguration` declares `b.Property<string>("GoogleEmail").HasColumnName("GoogleEmail").IsRequired(false)`. The backfill reads it via `EF.Property<string?>(user, "GoogleEmail")` to map `IsGoogle = true` to the right row.
  - The auto-generated migration must be three `AddColumn` statements (`Provider`, `ProviderKey`, `IsGoogle`) only — no `DropColumn` for the legacy three. If the scaffolder produces drops, the shadow-property declarations are missing or wrong; fix the configuration, do not hand-edit the migration. The `dotnet ef migrations add` snapshot must include the legacy columns as shadow properties.
  - **All DB column drops across the entire 6-PR sequence are aggregated into a single deferred PR — referred to as PR 7.** PR 7 ships only after PRs 2 through 6 have been verified end-to-end in production with a real soak window. Plan stub at `docs/superpowers/plans/2026-04-30-email-oauth-pr7-drop-legacy-columns.md`. See `architecture_no_drops_until_prod_verified` — code drops can roll back via `git revert`; column drops cannot.
- Backfill per the rules in "Migration / backfill" — populates `Provider`/`ProviderKey`/`IsGoogle` on existing `UserEmail` rows from the legacy `AspNetUserLogins` + `User.GoogleEmail` data.
- OAuth callback (success branch): rename detection (lookup by `(Provider, ProviderKey)`, compare claim, update if differ).
- OAuth callback (link-by-email branch): on `AddLoginAsync`, set `Provider`/`ProviderKey` on the matched UserEmail row.
- `GoogleWorkspaceSyncService` reads the canonical Workspace identity from the `IsGoogle`-flagged `UserEmail` row (fallback to first `Provider != null` row).
- `GoogleAdminService` queries shift from `User.GoogleEmail` (now a deleted property) to the `IsGoogle`-flagged `UserEmail` row joined to `User`. (`Provider != null` is an Auth-only signal — Google-Workspace lookups use `IsGoogle`.)
- Backfill of `Provider` / `ProviderKey` / `IsGoogle` runs from a one-shot **admin button**, not from the migration body — same pattern as PR 1's UserEmail-row backfill button. Operator clicks on QA, verifies, clicks on prod, verifies, then the followup column-drop PR ships.
- **Architecture test** forbids new reads or writes of `IsOAuth`, `DisplayOrder`, `User.GoogleEmail`, and `GetGoogleServiceEmail()` solution-wide. The test enforces "stop using" — it does not verify the columns are absent.

**Risk:** Low-medium. The migration is additive only; rollback is "revert deploy + leave the new columns sitting unused." Mitigation: backfill verified against staging snapshot before prod migration; rename-detection covered by a smoke test (manually rename a Workspace alias, sign in, confirm UserEmail updated + audit log entry).

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
- Architecture test forbidding `dbContext.Users.Add(...)` outside this funnel + Identity's own internal paths (default `UserStore<User>`).
- Verify: every existing User-creation site is migrated to use the funnel; no orphan paths.

**Risk:** Medium (touches multiple entry points). Mitigation: comprehensive test per source + deduplication regression (create user via mailing-list, then try ticket purchase with same email — must reuse, not create).

## Affected files (rough)

### Domain
- `src/Humans.Domain/Entities/User.cs` — virtual overrides for `Email` / `NormalizedEmail` / `EmailConfirmed` / `UserName` / `NormalizedUserName` (PR 2). PR 3 deletes the public `GoogleEmail` property and the `GetGoogleServiceEmail()` helper; the column lives on as an EF shadow property declared in `UserConfiguration`.
- `src/Humans.Domain/Entities/UserEmail.cs` — PR 3 adds `Provider`, `ProviderKey`, `IsGoogle` as public properties; deletes the public `IsOAuth` and `DisplayOrder` properties (columns persist as EF shadow properties declared in `UserEmailConfiguration`).

### Infrastructure
- `src/Humans.Infrastructure/Data/Configurations/UserConfiguration.cs` — PR 3 adds `b.Property<string>("GoogleEmail")` shadow-property declaration so the `GoogleEmail` column stays modeled after the C# property is deleted. Untouched by PR 2.
- `src/Humans.Infrastructure/Data/Configurations/UserEmailConfiguration.cs` — column mappings for `Provider`/`ProviderKey`/`IsGoogle` (PR 3, no DB indexes/unique constraints — invariants are service-enforced). Shadow-property declarations for `IsOAuth` and `DisplayOrder` so those columns stay modeled after their C# properties are deleted.
- `src/Humans.Infrastructure/Migrations/{ts}_UserEmailsModernization.cs` — PR 3 (additive schema only — three `AddColumn` calls). **No drops, no `CreateIndex`, no `migrationBuilder.Sql`, no hand-edits — 100% auto-generated.** Backfill runs from an admin button, not from the migration body.
- `src/Humans.Infrastructure/Services/UserEmailService.cs` — deletion guard (PR 1), Provider/ProviderKey writes (PR 3).
- `src/Humans.Infrastructure/Services/UserService.cs` — `FindOrCreateUserByEmail` (PR 6).
- `src/Humans.Infrastructure/Services/MagicLinkService.cs` — drop fallbacks (PR 1).
- `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` — `IsGoogle` read (PR 3).
- `src/Humans.Infrastructure/Services/GoogleAdminService.cs` — `BackfillEmails` already deleted upstream (PR 1) + `IsOAuth` refs replaced with `Provider != null` (PR 3).
- `src/Humans.Infrastructure/Services/AccountMergeService.cs` — verify/add conflict resolution (PR 4).
- `src/Humans.Infrastructure/Services/AccountProvisioningService.cs` — funnel (PR 6).
- `src/Humans.Infrastructure/Jobs/ProcessAccountDeletionsJob.cs` — stop writing Identity columns (PR 1; columns themselves stay).

### Web
- `src/Humans.Web/Controllers/AccountController.cs` — OAuth callback restructure (PR 1, PR 3).
- `src/Humans.Web/Controllers/ProfileController.cs` — grid actions (PR 4).
- `src/Humans.Web/Controllers/PreferencesController.cs` — new (PR 5).
- `src/Humans.Web/Controllers/DevLoginController.cs` — port creation paths to funnel (PR 6).
- `src/Humans.Web/Views/Profile/Emails.cshtml` — grid rewrite (PR 4).
- `src/Humans.Web/Views/Profile/AdminDetail.cshtml` — drop IsOAuth pill (PR 4) [view-template change only; column stays on disk].
- `src/Humans.Web/Views/Preferences/Index.cshtml` — new (PR 5).
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — `RequireUniqueEmail = false` (PR 1). **No custom user store registration.**
- Email templates (Razor + HTML) — unsubscribe-link footer injection (PR 5).

### Tests
- `tests/Humans.Application.Tests/Architecture/UserArchitectureTests.cs` — extended assertions across PRs 1, 2, 3, 6 (forbid setter writes; forbid new reads/writes of stop-using fields). Tests verify "stop using," not "absent."
- Service tests for `UserService.FindOrCreateUserByEmail` (PR 6).
- Rename-detection smoke test (PR 3).
- Cross-User add-email merge tests (PR 4).
- Token tampering / expiry tests for `/preferences` (PR 5).
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
