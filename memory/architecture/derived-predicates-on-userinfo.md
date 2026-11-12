---
name: derived-predicates-on-userinfo
description: "Derived user predicates (IsActive, IsStub, IsRejected) go on `UserInfo`, never `ProfileInfo` or `Profile`; one needing another section's data lives with that section."
---

Every derived predicate about a user lives on `UserInfo`, including ones that read `Profile` fields.
`ProfileInfo` stays a flat projection of `Profile` columns: no `IsX` / `HasX` / `NeedsX` on it.

**Why:** callers should never chain `info.Profile?.X` to ask a question about a user, and two homes for
predicates force every caller to know which one to look in.

**How to apply:**
1. New derived property about a user → `UserInfo` (`IsStub`, `IsActive`, `HasRequiredNameFields`,
   `IsRejected` are there).
2. If it reads `Profile` fields, the `UserInfo` property absorbs the null-profile check and returns the
   safe default.
3. No helper on `ProfileInfo` "only `UserInfo` would call"; read the fields in the `UserInfo` body, even
   if two properties repeat a short check.
4. No parallel predicate on the `Profile` entity for write paths; inline the field checks at the write
   site.
5. A trivial field formatter (`ProfileInfo.FullName`) is not a predicate and stays put.

**Exception: the predicate needs another section's data.** It lives with that section, as an extension
over `UserInfo` or on that section's own read model: consent-review predicates are extension methods in
Consent's contracts (`ConsentReviewPredicates`), ticket/onsite state is on Tickets'
`EventParticipationInfo`, marketing opt-out is Email's `ICommunicationPreferenceService`.

**Related:** [`iuserservice-onestop-userinfo`](iuserservice-onestop-userinfo.md) ·
[`interface-method-additions-are-debt`](interface-method-additions-are-debt.md) (properties on
`UserInfo` are fine; new interface methods are not)
