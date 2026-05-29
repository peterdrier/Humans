---
name: Email Mutation Paths
description: HARD RULE. `UserEmail.Email` is written only by the OAuth-callback reconcile primitive via `(Provider, ProviderKey)` match; `User.Email` is a vestigial Identity field computed from the verified `IsPrimary` row, never written by application code.
---

# Email Mutation Paths

HARD RULE. The OAuth-callback write path is collapsed to a single service entry point with `AspNetUserLogins` as the authoritative store for OAuth identity. `User.Email` is a vestigial ASP.NET Identity field — it is a computed projection of the user's verified `IsPrimary` `UserEmail` row, never written by application code.

## Authority model

- **`AspNetUserLogins`** (ASP.NET Identity-managed) is authoritative for `(Provider, ProviderKey) → UserId`. Mutated only via `UserManager.{Add,Find,Remove}LoginAsync`. We never alter the table shape.
- **`UserEmail`** is authoritative for `Email → UserId` (verified). Used by magic-link login and every email-based lookup.
- **`UserEmail.Provider` / `UserEmail.ProviderKey`** is a per-row tag indicating *which row of this user's emails currently corresponds to a given OAuth identity*. Used to decide which row to rewrite when the provider asserts a rename. **Never used for cross-user identity lookup** — `AspNetUserLogins` answers that.

## `UserEmail.Email` — the only mutation primitive

**Write site:** `IUserEmailService.ReconcileOAuthIdentityAsync(Guid userId, string provider, string providerKey, string claimEmail, bool claimEmailVerified, CancellationToken ct) -> Task<OAuthReconcileResult>`. Internal policy ladder, all inside one reconcile flow:

1. **Tagged row at claim email** → `NoChange`.
2. **Cross-user collision pre-check** — when another user verified-holds the claim email:
   - `claimEmailVerified == false` → block, audit `OAuthRenameCollisionBlocked`, return `CrossUserBlocked` with the mother-of-all diagnostic. No mutation.
   - `claimEmailVerified == true` → Google wins. Delete the other user's row, write paired `OAuthRenameCollision` (signing user) + `UserEmailDisplacedByOAuthRename` (displaced user) audits, proceed with the mutation, return `CrossUserDisplaced`. When the delete leaves the displaced user with zero verified rows, the audit description and `DisplacedUserLeftWithoutVerifiedEmail` flag reflect that.
3. **Tagged row at a different email + sibling holds claim** → tag-move: move `(Provider, ProviderKey)` onto the matching row, **union** `IsPrimary` and `IsGoogle` (matching row keeps any `true` flag from either side), force `IsVerified = true`, delete the old row. Return `TagMoved`.
4. **Tagged row at a different email + no sibling** → rewrite the row's `Email` in place. Return `EmailRewritten`.
5. **No tagged row + sibling holds claim** → attach the tag onto the sibling. Return `TagMoved`.
6. **No tagged row + no sibling + no cross-user collision** → insert a verified tagged row. Return `NewRowCreated`.

After the data change, the service runs `EnsurePrimaryInvariantAsync` and `EnsureGoogleInvariantAsync` on every affected user (signing + displaced), invalidates `FullProfile`, and commits.

**Audit ownership:** every audit row for every outcome is written by the service inside the reconcile flow. The controller writes no audit rows on the OAuth path.

**Sole caller:** `AccountController.ExternalLoginCallback`, calling reconcile exactly once per OAuth-success path — five paths: existing-user sign-in, already-authenticated link, lockout-relink, email-match link, new-user creation. The controller swallows reconcile exceptions: **sign-in must never block on reconcile**.

Build-time enforcement: analyzers `HUM0005` (service-caller) and `HUM0006` (repo-caller) in `src/Humans.Analyzers/EmailMutationPathsAnalyzer.cs`. HUM0005 pins `IUserEmailService.ReconcileOAuthIdentityAsync` to `AccountController` as its sole caller; HUM0006 pins `IUserEmailRepository.ApplyReconcilePlanAsync` to `UserEmailService`. Any other call site fails the build with a pointer back to this atom. Pattern + catalogue in `docs/architecture/code-analysis.md`.

**Forbidden:** any admin-triggered "fix this email" flow, any `_userManager.SetEmailAsync`, any direct `UPDATE user_emails SET email = ...` outside the reconcile primitive, any caller of `ReconcileOAuthIdentityAsync` other than `AccountController`. None of these can produce a correct rewrite — admin lacks the OAuth `sub` and `email_verified` claim in the authoritative moment.

## `User.Email` — vestigial Identity field, computed

The ASP.NET Identity `User.Email` column exists only because Identity machinery touches it. Application code must treat it as derived:

```
User.Email = UserEmails.Where(IsVerified).OrderByDescending(IsPrimary).First().Email
```

The `User` entity already enforces this via an `Email` getter override (see `User.cs` — `Email` property override and the `IdentityEmailColumn` diagnostic accessor for the legacy underlying column).

**Application code MUST NOT write `User.Email`.** It changes only as a consequence of underlying data changing:
- The `IsPrimary` row's `Email` is rewritten by reconcile (the only path that mutates an existing row's `Email`).
- The `IsPrimary` flag flips between rows (via the existing primary-flip flow in `UserEmailService`).
- A row is added or removed in a way that changes which row is the `IsPrimary` verified row.

No service writes `User.Email` directly. No admin button writes `User.Email` directly. The legacy `base.Email` column is read-only-for-diagnostic via `User.IdentityEmailColumn` and otherwise ignored application-wide.

## Per-user admin diagnostic

`/Profile/{userId}/Admin/Emails` shows the `UserEmail` grid alongside the `AspNetUserLogins` table for the user. Per-row disagreement flags surface the two ways the two stores can drift:

- A `UserEmail` row is tagged with `(Provider, ProviderKey)` but no `AspNetUserLogins` row for this user matches → flag on the `UserEmail` row.
- An `AspNetUserLogins` row exists but no `UserEmail` row of this user is tagged with the same `(Provider, ProviderKey)` → flag on the login row.

Both disagreements self-heal on the user's next OAuth sign-in via reconcile's `TagMoved` / `NewRowCreated` branches.

The global `/Profile/Admin/EmailProblems` scanner's `GhostExternalLogins` finding deep-links into this per-user diagnostic — there is no inline cleanup button on the scanner.

## Operations that do NOT mutate `UserEmail.Email`

- **Account merge** — reparents rows (changes `UserId`), deduplicates by deleting one of two rows with the same address; never modifies the `Email` field.
- **Account provisioning** — `INSERT`s new rows for new users; never rewrites.
- **Admin email backfill** — its sole legitimate job is to `INSERT` missing `UserEmail` rows for legacy Identity-email values. It never rewrites existing rows and does not attempt to attach an OAuth tag (the next OAuth sign-in's reconcile does that via `TagMoved`).
- **Profile UI email add/remove** — `INSERT` and `DELETE` only; never modifies `Email`.
- **Workspace sync, group sync, audit, reads** — never mutate `Email`.

## Why

A user can have multiple OAuth identities on a single `User`. Each identity is uniquely keyed by `(Provider, ProviderKey)` — the provider's stable `sub`. Two stores answering "which user owns this identity?" is the root cause of every bug PR #477 patched: rename detection drifting, backfill failing on legacy users, cross-user `23505` collisions, `IsPrimary` / `IsGoogle` reconciliation lagging, corrupted state where the two stores disagree. Collapsing to one authoritative store + one reconcile entry point removes the cause.

The OAuth callback is the only path that holds the authoritative `(userId, provider, providerKey, claimEmail, claimEmailVerified)` quintuple in the same atomic moment the provider asserts the identity. Every other surface (admin pages, jobs, syncs) operates on stale or partial state and cannot produce a correct rewrite. Auto-heal on next sign-in is correct and sufficient; admin-triggered rewriting is forbidden.
