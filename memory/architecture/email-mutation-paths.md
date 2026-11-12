---
name: email-mutation-paths
description: HARD RULE. `UserEmail.Email` is written only by the OAuth-callback reconcile primitive (`(Provider, ProviderKey)` match); `User.Email` is vestigial, never written by app code.
---

HARD RULE. One service entry point rewrites an email address, and `User.Email` is never written.

## Who is authoritative for what

- **`AspNetUserLogins`**: `(Provider, ProviderKey) → UserId`. Mutated only through
  `UserManager.{Add,Find,Remove}LoginAsync`; its shape never changes.
- **`UserEmail`**: verified `Email → UserId` (magic-link login and every email lookup).
- **`UserEmail.Provider` / `ProviderKey`**: a per-row tag saying which of *this user's* rows matches an
  OAuth identity, so a provider rename rewrites the right row. Never used for cross-user lookup.

## The only primitive that rewrites `UserEmail.Email`

`IUserEmailService.ReconcileOAuthIdentityAsync(userId, provider, providerKey, claimEmail, claimEmailVerified, ct)`:

1. Tagged row already at the claim email → `NoChange`.
2. Another user verified-holds the claim email: unverified claim → block, audit
   `OAuthRenameCollisionBlocked`, return `CrossUserBlocked`, no mutation. Verified claim → Google wins:
   delete the other user's row, write paired `OAuthRenameCollision` + `UserEmailDisplacedByOAuthRename`
   audits, continue, return `CrossUserDisplaced`.
3. Tagged row elsewhere + a sibling row holds the claim → move the tag onto the sibling, union
   `IsPrimary`, force `IsVerified`, delete the old row → `TagMoved`.
4. Tagged row elsewhere + no sibling → rewrite `Email` in place → `EmailRewritten`.
5. No tagged row + sibling holds the claim → attach the tag → `TagMoved`.
6. No tagged row, no sibling, no collision → insert a verified tagged row → `NewRowCreated`.

Then it runs `EnsurePrimaryInvariantAsync` for every affected user, invalidates `FullProfile`, commits,
and calls GoogleIntegration's `ClearIfAddressAsync` for any address that left. The service writes every
audit row; the controller writes none.

**Sole caller:** `ExternalLoginService.CompleteExternalLoginAsync`, once per OAuth-success path. It
swallows reconcile exceptions, because sign-in must never block on reconcile; new-user creation is the
exception and rolls the half-built account back. Don't widen the allowlist to a general-purpose service
such as `AccountProvisioningService`.

**Enforcement:** analyzers `HUM0005` (pins the service method to `ExternalLoginService`) and `HUM0006`
(pins `IUserEmailRepository.ApplyReconcilePlanAsync` to `UserEmailService`) in
`src/Humans.Analyzers/EmailMutationPathsAnalyzer.cs`.

**Forbidden:** any admin "fix this email" flow, `_userManager.SetEmailAsync`, any direct update of
`user_emails.email`, any other caller of the primitive.

## `User.Email` is computed

The `Email` getter override on `User` returns the verified `IsPrimary` `UserEmail` row's address. No
service or admin button writes it; it changes only because the underlying rows change. The legacy
Identity column is readable for diagnostics through `User.IdentityEmailColumn` and otherwise ignored.

## Operations that never rewrite an address

Account merge (reparents and dedupes rows), provisioning (inserts), admin email backfill (inserts missing
rows only, attaches no OAuth tag), profile email add/remove (insert and delete), sync jobs and reads.

Drift between the two stores shows on the per-user admin emails diagnostic and self-heals on the user's
next OAuth sign-in.

**Why:** two stores answering "which user owns this identity?" caused rename drift, failed backfills,
cross-user unique collisions and lagging primary flags. Only the OAuth callback holds
`(userId, provider, providerKey, claimEmail, claimEmailVerified)` at the moment the provider asserts it;
every other surface works on stale or partial state and cannot produce a correct rewrite.
