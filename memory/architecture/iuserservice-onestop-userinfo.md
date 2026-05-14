---
name: iuserservice-onestop-userinfo
description: Long-term direction. `IUserService` is the one-stop-shop for every field in `UserInfo` — reads AND writes. New callers prefer it; new write paths land on it; sibling services (`IProfileService`, `IUserEmailService`, `ICommunicationPreferenceService`) drain into it over time.
---

`IUserService` owns the canonical `UserInfo` read-model AND the write paths for every field on it. When you need to read or write anything inside `UserInfo`'s scope — user identity columns, user-email rows, external logins, event participations, profile fields, contact fields, profile languages, volunteer history, communication preferences — prefer `IUserService`.

**Why:** `UserInfo` is already the cached canonical "everything-about-a-person" projection ([UserInfo.cs](../../src/Humans.Application/UserInfo.cs), issue #703). Today the *read* side is consolidated there; the *write* side is still scattered across `IProfileService`, `IUserEmailService`, `ICommunicationPreferenceService`, etc. Long-term, those drain into `IUserService` so there is one service surface for "data about a person." Two services for two halves of the same projection is the tech debt; one-stop-shop is the destination.

**How to apply:**

- New read callers in render/notification/validation paths → use `IUserService.GetUserInfoAsync` (sync-on-hit) rather than calling `IProfileService.GetFullProfileAsync` or stitching `IUserService.GetByIdAsync` + `IProfileService.GetProfileAsync` + `IUserEmailService.GetUserEmailsAsync`. The fields are all on `UserInfo`.
- New write paths for any `UserInfo` field → add them to `IUserService` (subject to [`interface-method-additions-are-debt`](interface-method-additions-are-debt.md): audit existing methods first, ask before adding).
- Existing callers of `IProfileService` / `IUserEmailService` / `ICommunicationPreferenceService` are NOT a refactor target on their own — drain opportunistically when touching that code for other reasons. No big-bang flag day.
- The one genuine exception for external callers: **ASP.NET Identity interop** — `UserManager.UpdateAsync(user)`, `SignInManager`, claims transformation, lockout APIs all want the tracked `User` entity. Use `GetByIdAsync` (or `UserManager.FindByIdAsync`) when the next call is an Identity API. (UserService-internal writes naturally go through `_userRepository` etc.; that's normal section-internal repo usage, not an exception to this rule.)
- Does NOT contradict [`users-profiles-one-section`](users-profiles-one-section.md) — that rule says don't bounce code between `Services.Users` and `Services.Profile` for boundary cleanup; this rule names which interface surface wins inside that one section.

**Related:**

- [`caching-transparent`](caching-transparent.md) — UserInfo IS the §15 stitched-DTO for this section.
- [`interface-method-additions-are-debt`](interface-method-additions-are-debt.md) — adding writes to `IUserService` still requires audit-first + ask-Peter.
- [`users-profiles-one-section`](users-profiles-one-section.md) — same ownership boundary; this rule clarifies the long-term service surface within it.
