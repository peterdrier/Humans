---
name: BurnerName is the display name (write-through sync)
description: HARD RULE. When a Profile exists, `Profile.BurnerName` is the only name we render — `User.DisplayName` is a legacy field sourced from the auth provider and we don't own it. `IProfileService.SaveProfileAsync` write-through-syncs `User.DisplayName ← Profile.BurnerName` in the same operator action so the two cannot drift on any new write. Direct reads of `User.DisplayName` are limited to the four legitimate categories below; everything else routes through `FullProfile.DisplayName` (BurnerName-aware) or fetches BurnerName via `IProfileService`.
---

`User.DisplayName` is **not** what we display. It is the legacy auth-provider name (B2C / Google) — whatever the human registered with externally. Mixing it into UI / email / notification surfaces is a PII leak: the human's chosen `Profile.BurnerName` is what they want shown, not their legal/registered name.

## The two-prong rule (issue #692)

**1. Write-through sync on Profile save.** `IProfileService.SaveProfileAsync` writes `User.DisplayName ← Profile.BurnerName` in the same operator action that persists the profile (`ProfileService.SaveProfileCoreAsync`, after the profile-row write). After the first save, the two fields cannot drift on any new write.

The `displayName` parameter that used to live on `IProfileService.SaveProfileAsync` is **gone** — the BurnerName from the request is the source of truth, callers no longer pass it separately, and the rule cannot be subverted by a caller forwarding the wrong string.

**2. Read paths route through the BurnerName-aware surface.**

- **`FullProfile.DisplayName`** resolves to `Profile.BurnerName` when present, falling back to `User.DisplayName` only for Stub-state pre-onboarding rows. It is the canonical "what name do we show this human as" lookup.
- **View-model assemblers** that have a Profile in scope use `profile?.BurnerName ?? user.DisplayName`.
- **View-model assemblers** that load only Users in bulk batch-fetch profiles via `IProfileService.GetByUserIdsAsync` and do the same resolution.
- **Email / notification recipient labels** call `IProfileService.GetFullProfileAsync(userId)` and read `.DisplayName`.

## Legitimate reads of `User.DisplayName` (the only four)

1. **`HumanViewComponent`** / `HumanLinkTagHelper` no-Profile fallback for pre-onboarding humans.
2. **`FullProfile.Create` resolution** itself — that's where the `BurnerName ?? DisplayName` rule lives.
3. **`UserRepository` lifecycle mutations** — `"Merged User"` / `"Purged (...)"` / `"Deleted User"` labels; the assignment of an initial DisplayName at sign-up; GDPR data export of the User row (humans see exactly what's stored on the User row, separate from the Profile slice).
4. **`HumansUserClaimsPrincipalFactory`** — the `Name` claim has legacy semantics tied to the auth identity, not the rendered display.

Sorting / searching that naturally falls on `User.DisplayName` after a `.Where`/`.OrderBy` (e.g., shift volunteer search, team member sort) is correct after write-through because `User.DisplayName == Profile.BurnerName` for any post-onboarding row. Mark these reads with an inline comment pointing to this atom; do not refactor them into round-trips through `IProfileService` for in-memory string comparison.

## Why the structural rule, not just the sync

Sync alone makes existing reads "happen to be correct." Migrating reads to `FullProfile.DisplayName` / batched profile lookups makes the rule **read at the call site** — future code can't accidentally introduce a leak by reading `user.DisplayName` thinking it's the canonical display field, because the codebase consistently resolves through Profile.

No data backfill — see [`process/no-data-backfills`](../process/no-data-backfills.md). Pre-existing drift from before this rule landed is left alone; on the next Profile save the row self-corrects. If existing drift becomes a visible problem, an admin scanner is appropriate; do not add one speculatively.
