---
name: No Identity-derived column reads from Application or Web
description: HARD RULE. Application/Web code must not read `User.Email`/`NormalizedEmail`/`UserName`/`NormalizedUserName`; use `UserInfo.Email` / `IUserEmailService` instead. Enforced by HUM0019.
---

# No Identity-derived column reads from Application or Web

HARD RULE. Application and Web code MUST NOT read the four Identity-derived `User` columns:
`Email`, `NormalizedEmail`, `UserName`, `NormalizedUserName`.

These properties are virtual overrides on `User`:

- `User.Email` / `User.EmailConfirmed` / `User.NormalizedEmail` are computed projections of the
  user's verified `UserEmails` rows (preferring `IsPrimary`).
- `User.UserName` / `User.NormalizedUserName` are anchored to `User.Id` so the Identity
  uniqueness validator always sees a non-empty unique value.

Application reads through these properties are a vestigial coupling to ASP.NET Identity — they
silently fall back to `base.Email` when `UserEmails` is not loaded, which is the wrong answer.
The canonical read paths are:

- `UserInfo.Email` / `UserInfo.PrimaryEmail` (the materialised UserEmails projection on the §15
  cached snapshot).
- `IUserEmailService.GetPrimaryEmailAsync(userId, ct)` — direct UserEmails lookup.
- `IUserEmailRepository.GetByUserIdReadOnlyAsync(userId, ct)` for raw rows.
- For OAuth identity lookups: `AspNetUserLogins` via `UserManager.{Add,Find,Remove}LoginAsync`.

## Enforcement

- **HUM0019** (`IdentityColumnReadAnalyzer`) reports reads of the four properties from
  `Humans.Application` / `Humans.Web` as warnings. Listed in
  `WarningsNotAsErrors` until the remaining legacy reads are migrated.
- **HUM0002** (`IdentityColumnWriteAnalyzer`) blocks writes (errors).
- **HUM0003** (`IdentityFindByEmailAnalyzer`) blocks `UserManager.FindByEmailAsync` /
  `FindByNameAsync`, which query these columns directly.

## Exempt

- `Humans.Domain` — `User.cs` declares the overrides.
- `Humans.Infrastructure` (excluding `Data/` configurations) — Identity machinery (claims
  factory, cookie sign-in, repository internal includes) legitimately reads the columns.
- Background jobs that project via `IUserService.GetUserInfosAsync` and read `UserInfo.Email`
  are not violations (UserInfo.Email is its own DTO property).

## Why

The two-source-of-truth problem (`AspNetUsers.Email` vs `UserEmails.Email`) caused every rename
bug PR #477 patched. Issue nobodies-collective/Humans#506 collapses application reads onto the
single canonical store (`UserEmails`); HUM0019 enforces that going forward.

See also: `memory/architecture/email-mutation-paths.md` for the write-side counterpart.
