# Email Mutation Paths

HARD RULE. There is exactly one way to modify an existing email address in this system. `UserEmail.Email` is written only by the OAuth sign-in callback, matched on `(Provider, ProviderKey)`. `User.Email` is a vestigial ASP.NET Identity field — it is a computed projection of the user's verified `IsPrimary` `UserEmail` row, never written by application code.

## `UserEmail.Email` — the only mutation primitive

**Write site:** `UserEmailRepository.UpdateEmailAsync(Guid userId, string provider, string providerKey, string newEmail, Instant updatedAt, CancellationToken ct)`. Upserts on `(Provider, ProviderKey)` — globally unique per the entity's service-enforced invariant — for the given `userId`. Inserts a verified row when the pair is missing; updates `Email` + `UpdatedAt` when present. Removes any other row for the same user that already holds `newEmail` so the partial unique `Email` index does not throw. Reconciles `IsPrimary` on the surviving rows (0 → set current login primary; exactly 1 → leave; 2+ → current login stays primary, others demoted). One DbContext, one transaction.

**Sole caller chain:** the OAuth sign-in callback in `AccountController` → `IUserEmailService.UpdateEmailAsync(userId, …)` → the repo primitive above. The callback already has the authoritative quadruple — `userId`, `info.LoginProvider`, `info.ProviderKey`, and the email claim — from the OIDC token + the signed-in user. On every Google sign-in where the row is missing or the row's `Email` differs from the claim, the callback calls `UpdateEmailAsync` to bring the database into agreement.

XML doc on each declaration names its sole legitimate caller. Build-time enforcement (analyzer that breaks the build on any other call site) is tracked in [nobodies-collective/Humans#695](https://github.com/nobodies-collective/Humans/issues/695) — defer to that issue rather than adding another fragile IL-scan test that the analyzer migration will discard.

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
