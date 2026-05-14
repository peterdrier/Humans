---
name: no-paving-obsolete-fields
description: When migrating a read or write, use the canonical replacement field/predicate — not the obsolete one. Don't pave the cow path. Fires whenever code touches a `[Obsolete]`-flagged member, a `#pragma warning disable HUM_*_OBSOLETE` ed property, or a legacy helper that has a canonical successor (e.g. `Profile.IsSuspended` → `Profile.State`, `Profile.HasRequiredIdentityFields()` → `Profile.State == ProfileState.Stub`).
---

When migrating a caller to a new service / read-model / interface, do NOT carry the call site's pre-existing reliance on a legacy/obsolete field, predicate, or helper. Switch to the canonical replacement at the same time as the migration. New code is never written against the obsolete field — full stop, even if the obsolete one is "still there and still works."

**Why:** Migrations are the only realistic window to retire the legacy member. If new code (and freshly-migrated old code) keeps reading the obsolete field, the dead member never reaches zero callers, and the codebase carries two parallel sources of truth indefinitely. Peter has explicitly stopped this multiple times: "stop using the damn obsolete field. migrate to the replacement, don't pave the cow path." Two readers of the same fact = an invariant waiting to be violated.

**How to apply:**

- `Profile.IsSuspended` (bool, `[Obsolete]`-flagged with `HUM_PROFILE_ISSUSPENDED`) → `Profile.State == ProfileState.Suspended`.
- `Profile.HasRequiredIdentityFields()` (predicate) → `Profile.State == ProfileState.Stub` for the "is this a stub?" question; the State enum is the canonical lifecycle marker. The predicate exists to *derive* State; downstream code reads State.
- `User.DisplayName` for public rendering → `UserInfo.BurnerName` / `<vc:human>` (see [`burnername-is-the-display-name`](../architecture/burnername-is-the-display-name.md)).
- `IProfileService.GetFullProfileAsync` for reads → `IUserService.GetUserInfoAsync` (see [`iuserservice-onestop-userinfo`](../architecture/iuserservice-onestop-userinfo.md)).
- Any `[Obsolete]` attribute or `#pragma warning disable HUM_*_OBSOLETE` you see while editing is a stop-sign for the line under it: replace it with the canonical successor, don't propagate it.
- If you genuinely can't find a canonical replacement — STOP and ask. Don't invent one and don't suppress the obsolete warning.
- Applies to tests too: when porting a test off the obsolete field, the test setup uses the new field. Don't seed Profile entities with the obsolete bool just because that's what the old test did.

**Exceptions:** Identity / framework interop sites that must use the legacy `User.DisplayName` for the underlying Identity column (e.g. claims transformation, `UserManager.UpdateAsync`), and the State-derivation code itself in `ProfileService` / `CachingProfileService` that *writes* both the legacy bool and the new enum during the dual-write window.

**Related:**

- [`burnername-is-the-display-name`](../architecture/burnername-is-the-display-name.md) — concrete instance for the User.DisplayName → BurnerName migration.
- [`iuserservice-onestop-userinfo`](../architecture/iuserservice-onestop-userinfo.md) — the broader IProfileService → IUserService consolidation this rule supports.
