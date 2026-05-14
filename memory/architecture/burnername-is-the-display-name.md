---
name: BurnerName is THE display name when a Profile exists
description: For any human with a Profile row, BurnerName is the only public-facing name we render. User.DisplayName is a legacy field — fallback only when no Profile exists. UserInfo.BurnerName / FullProfile.DisplayName resolve this — never read user.DisplayName or UserInfo.DisplayName directly for rendering.
---

When a `Profile` row exists for a `User`, `Profile.BurnerName` is the only name we ever render in the UI. `User.DisplayName` is a legacy field we can't remove; it's a fallback only when no Profile exists.

Canonical resolved accessors:

- **`UserInfo.BurnerName`** — the going-forward accessor. `Profile.BurnerName` when present (non-blank), otherwise `UserInfo.DisplayName` (the legacy Identity column mirror).
- **`FullProfile.DisplayName`** — same semantics on the older `FullProfile` projection; will drain as callers migrate to `UserInfo` (see [`iuserservice-onestop-userinfo`](iuserservice-onestop-userinfo.md)).

**Why:** Peter's hard rule (issue #691 review): "if the user has a profile, then BurnerName is the only thing we ever show for them." Mixing the two in the rendering path leaks the legacy field into public surfaces (search results, member lists, etc.).

**How to apply:**

- Render via `<vc:human>`, `UserInfo.BurnerName`, or `FullProfile.DisplayName` — all three already resolve correctly.
- `UserInfo.BurnerName` is `Profile is not null && !string.IsNullOrWhiteSpace(Profile.BurnerName) ? Profile.BurnerName : DisplayName`. `FullProfile.Create` (both overloads) does the same resolution. Don't undo either.
- Don't read `UserInfo.DisplayName` directly for rendering — it's the raw legacy column mirror. The only legitimate consumers of the raw `UserInfo.DisplayName` / `user.DisplayName` field are: (1) the resolved-accessor implementations above, (2) debug screens (`/Users/Admin/Debug`), (3) infrastructure mutations (merge / purge / delete labels in `UserRepository`).
- Search-results, team rosters, audit-log labels, etc., must NOT pass an explicit `display-name`/override — let the VC fetch.

**Carve-outs (legal/financial identity, NOT display):**

- SEPA pain.001 payee name + Holded purchase document contact name (`ExpenseReportService.SubmitAsync` payee snapshot). The bank rejects transfers when the payee name doesn't match the bank-account holder's legal identity, so these records must use `Profile.FirstName + " " + Profile.LastName` (the legal-name fields), falling back to `User.DisplayName` only when the legal-name fields are blank. Never use `BurnerName` for financial records — a pseudonym does not match the bank account.

**Related:** `src/Humans.Application/FullProfile.cs` (resolution); `src/Humans.Web/ViewComponents/HumanViewComponent.cs` (rendering path); `src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs:126` (search uses BurnerName as primary name match).
