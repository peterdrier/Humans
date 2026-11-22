---
name: burnername-is-the-display-name
description: HARD RULE. A person's name renders only through `<vc:human>` with its profile-popup link (no link only inside a control or on the person's own page; HUM0041) — never `DisplayName` or a name string in a view model.
---

`User.BurnerName` is the only name the UI ever renders. `User.DisplayName` is a legacy field that can't
be removed yet.

**Why:** Peter: *"if the user has a profile, then BurnerName is the only thing we ever show for them."*
Mixing the two leaks the legacy field into search results and member lists.

**How to apply:**
- **Render through `<vc:human>`** (text / avatar / avatar-name / card), `<vc:profile-card>` or
  `<vc:human-summary>`. They resolve the name through the cached `IUserService.GetUserInfoAsync`. Raw
  `@x.DisplayName`, a hand-built profile link, or an avatar-plus-name flex container is a violation.
  Don't pass a `display-name` override; let the component fetch.
- **Keep the link.** The name opens the profile popup. `HumanLink` has only two no-link values, each a
  reason: `InsideControl` (inside another link, button or the popup itself) and `Kiosk` (Gate kiosk
  screens, locked away from profiles). A person's own profile page links too. Signed-out viewers get no link
  automatically. Anything else links; a new no-link use needs Peter's OK.
- **In code, `UserInfo.BurnerName`** is the accessor: the burner name when non-blank, the erasure
  sentinel on a GDPR tombstone, else blank. Never restore a `DisplayName` fallback.
- **Raw `DisplayName` has three legitimate readers:** GDPR export while the column exists, debug
  screens, and infrastructure labels in `UserRepository` (merge / purge / delete).
- **Don't copy the name into a view model.** A VM carrying a user id (`UserId`, `MemberId`,
  `ReporterUserId`, …) must not carry that user's name as a string beside it; pass the id and render
  with `<vc:human user-id="…">`. The copy bypasses the cache and the resolver, goes stale on rename, and
  invites the next person to print it.

**Exceptions:**
- **Audit snapshots** (`RejectedByName`, `ResolvedByName` with no paired id) deliberately freeze a name
  at the time of an action.
- **Legal and financial identity:** SEPA payee names and Holded purchase-document contacts use the
  legal first and last name (falling back to `DisplayName` only when blank). Never `BurnerName`: the
  bank rejects a payee that doesn't match the account holder.

**Enforced by:** the `HumanLink` type, and HUM0041 (a view writing `UserInfo.BurnerName`). A name copied into a
view model is not caught; review catches it.

**Related:** [`iuserservice-onestop-userinfo`](iuserservice-onestop-userinfo.md)
