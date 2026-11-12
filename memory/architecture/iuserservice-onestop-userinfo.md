---
name: iuserservice-onestop-userinfo
description: "Long-term direction: `IUserService` is the one-stop-shop for every `UserInfo` field, reads and writes — prefer it for new callers; sibling services drain into it over time."
---

`IUserService` owns the cached `UserInfo` read model
([UserInfo.cs](../../src/Humans.Base/Users/UserInfo.cs)) and, as the destination, the write paths for
every field on it: identity columns, emails, external logins, profile and contact fields, languages,
volunteer history.

**Why:** one service surface for "data about a person". Reads are consolidated on `UserInfo`; some
writes still sit on sibling services (`IUserEmailService`), and two services for two halves of one
projection is the debt.

**How to apply:**
- New read callers (render, notification, validation) use `IUserService.GetUserInfoAsync` instead of
  stitching several lookups.
- New write paths for a `UserInfo` field go on `IUserService`, subject to
  [`interface-method-additions-are-debt`](interface-method-additions-are-debt.md).
- Existing sibling-service callers are drained when that code is touched for another reason. No
  flag-day refactor.
- **Exception: ASP.NET Identity interop.** `UserManager`, `SignInManager`, claims transformation and
  lockout APIs need the tracked `User` entity; use `GetByIdAsync` or `UserManager.FindByIdAsync` when
  the next call is an Identity API.
- Data another section owns about a person (tickets, consent review, communication preferences) stays
  with that section — [`derived-predicates-on-userinfo`](derived-predicates-on-userinfo.md).

**Related:** [`users-profiles-one-section`](users-profiles-one-section.md) ·
[`caching-transparent`](caching-transparent.md)
