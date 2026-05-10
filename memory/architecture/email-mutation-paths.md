# Email Mutation Paths

HARD RULE. There is exactly one way to modify an existing email address in this system. `UserEmail.Email` is written only by the OAuth sign-in callback, matched on `(Provider, ProviderKey)`. `User.Email` is a vestigial ASP.NET Identity field — it is a computed projection of the user's verified `IsPrimary` `UserEmail` row, never written by application code.

## `UserEmail.Email` — the only mutation primitive

**Write site:** `UserEmailRepository.UpdateEmailAsync(string provider, string providerKey, string newEmail, Instant updatedAt, CancellationToken ct)`. Matches one row by `(Provider, ProviderKey)` — globally unique per the entity's service-enforced invariant — and updates `Email` + `UpdatedAt`. Returns `bool` (true when the row was found and updated; false when no row matched).

**Sole caller:** the OAuth sign-in callback in `AccountController`. It already has the authoritative triple — `info.LoginProvider`, `info.ProviderKey`, and the email claim — from the OIDC token. On every Google sign-in where the matched row's `Email` differs from the incoming claim email, the callback calls `UpdateEmailAsync` to bring the row into agreement.

Architecture test pins this: only `AccountController` may call `UpdateEmailAsync`. Any other caller is a build break.

**Forbidden:** `(userId, oldEmail, newEmail)` matching, `(userId, Provider != null)` matching, any admin-triggered "fix this email" flow, any `_userManager.SetEmailAsync`, any direct `UPDATE user_emails SET email = ...` outside the one primitive. None of these can produce a correct rewrite — admin lacks the OAuth `sub` in the authoritative moment, and matching by email or `Provider != null` is non-deterministic for users with multiple Google accounts (or future Microsoft / Apple identities).

## `User.Email` — vestigial Identity field, computed

The ASP.NET Identity `User.Email` column exists only because Identity machinery touches it. Application code must treat it as derived:

```
User.Email = UserEmails.Where(IsVerified).OrderByDescending(IsPrimary).First().Email
```

The `User` entity already enforces this via an `Email` getter override (see `User.cs` — `Email` property override and the `IdentityEmailColumn` diagnostic accessor for the legacy underlying column).

**Application code MUST NOT write `User.Email`.** It changes only as a consequence of underlying data changing:
- The `IsPrimary` row's `Email` is rewritten by the OAuth callback above (the only path that mutates an existing row's `Email`)
- The `IsPrimary` flag flips between rows (via the existing primary-flip flow in `UserEmailService`)
- A row is added or removed in a way that changes which row is the `IsPrimary` verified row

No service writes `User.Email` directly. No admin button writes `User.Email` directly. The legacy `base.Email` column is read-only-for-diagnostic via `User.IdentityEmailColumn` and otherwise ignored application-wide.

## Operations that do NOT mutate `UserEmail.Email`

- **Account merge** — reparents rows (changes `UserId`), deduplicates by deleting one of two rows with the same address; never modifies the `Email` field.
- **Account provisioning** — `INSERT`s new rows for new users; never rewrites.
- **Admin email backfill** — its sole legitimate job is to `INSERT` missing `UserEmail` rows for Google identities the directory reports but our DB lacks. It never rewrites existing rows.
- **Profile UI email add/remove** — `INSERT` and `DELETE` only; never modifies `Email`.
- **Workspace sync, group sync, audit, reads** — never mutate `Email`.

## Why

A user can have multiple Google accounts (and future Microsoft / Apple identities) on a single `User`. The OAuth identity is uniquely keyed by `(Provider, ProviderKey)` — Google's stable `sub`. Matching by email string, by `Provider != null`, or by "first row for this user" is non-deterministic and corrupts the wrong row for multi-account users.

The OAuth callback is the only path that holds the authoritative `(Provider, ProviderKey, newEmail)` triple in the same atomic moment Google asserts the rename. Every other surface (admin pages, jobs, syncs) operates on stale or partial state and cannot produce a correct rewrite. Auto-heal on next sign-in is correct and sufficient; admin-triggered rewriting is forbidden.
