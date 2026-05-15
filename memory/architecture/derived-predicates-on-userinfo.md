---
name: Derived user/profile predicates live on UserInfo, not ProfileInfo
description: When adding a calculated property that answers a question about a user (IsActive, IsStub, NeedsConsentReview, HasRequiredIdentityFields, etc.), put it on `UserInfo`. Do NOT add to `ProfileInfo`. ProfileInfo is a flat projection of Profile fields; UserInfo is the canonical "everything-about-a-person" surface and the going-forward read API.
---

`UserInfo` is the one-stop-shop read model (see [`iuserservice-onestop-userinfo`](iuserservice-onestop-userinfo.md)). Every derived predicate about a user — including ones that read off `Profile` fields — lives on `UserInfo`, not on `ProfileInfo`.

`ProfileInfo` stays a flat immutable projection of `Profile` columns. Don't add `IsX`/`HasX`/`NeedsX` properties to it.

**Why:** Callers should never have to chain `info.Profile?.X` to ask a "does this user need / have / can do …" question. If every predicate ends up on `UserInfo`, the answer is always one short property access regardless of whether the user has a profile yet. Two homes for predicates fragments the read API and forces callers to know which one to look at.

**How to apply:**

1. New derived property answering a question about a user → on `UserInfo`.
2. If the predicate reads `Profile` fields, the `UserInfo` property absorbs the null-`Profile` check internally (return `false` when no profile, or whatever the safe default is for the predicate's intent).
3. Don't reintroduce helpers on `ProfileInfo` even when "they'd only be called by `UserInfo`" — read the `Profile` fields directly in the `UserInfo` body, even if that means duplicating a short check across two `UserInfo` properties. The body is short; the home discipline is what matters.
4. Domain entity methods on `Profile` (e.g. `Profile.HasRequiredIdentityFields()`) remain — they're used at write paths. The lift to `UserInfo` is a parallel read-side property.

**Examples already on `UserInfo`:**
- `BurnerName`, `Email`, `EmailConfirmed`, `IsDeletionPending`
- `PrimaryEmail`, `GoogleEmail`, `AllVerifiedEmails`, `MarketingOptedOut`
- `HasTicket`, `HasTicketForYear(year)`
- `IsStub`, `IsActive`, `HasRequiredIdentityFields`, `NeedsConsentReview`
- `FullName` lives on `ProfileInfo` as a trivial concatenation — that's a **field formatter**, not a predicate; keep it where it is.

**Counter-example (don't do):**
- ❌ `ProfileInfo.IsStub` (was briefly considered, rejected — moved to `UserInfo`).
- ❌ `ProfileInfo.HasRequiredIdentityFields` (was added, removed, briefly considered for re-add — moved to `UserInfo` instead).

**Related:**
- [`iuserservice-onestop-userinfo`](iuserservice-onestop-userinfo.md) — the broader direction this rule enforces.
- [`interface-method-additions-are-debt`](interface-method-additions-are-debt.md) — adding properties to `UserInfo` is fine; adding methods to `IUserService` / `IProfileService` is not.
