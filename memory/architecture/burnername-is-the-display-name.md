---
name: BurnerName is THE display name when a Profile exists
description: For any human with a Profile row, BurnerName is the only public-facing name we render. User.DisplayName is a legacy field — fallback only when no Profile exists. FullProfile.DisplayName resolves this — never read user.DisplayName for rendering.
---

When a `Profile` row exists for a `User`, `Profile.BurnerName` is the only name we ever render in the UI. `User.DisplayName` is a legacy field we can't remove; it's a fallback only when no Profile exists. `FullProfile.DisplayName` is the resolved name — `Profile.BurnerName ?? User.DisplayName`.

**Why:** Peter's hard rule (issue #691 review): "if the user has a profile, then BurnerName is the only thing we ever show for them." Mixing the two in the rendering path leaks the legacy field into public surfaces (search results, member lists, etc.).

**How to apply:**

- Render via `<vc:human>` or via `FullProfile.DisplayName` — both already resolve correctly.
- `FullProfile.Create` (both overloads) sets `DisplayName = !string.IsNullOrWhiteSpace(profile.BurnerName) ? profile.BurnerName : user.DisplayName`. Don't undo this.
- Don't introduce new code paths that read `user.DisplayName` for rendering. The only legitimate reads of `user.DisplayName` are: (1) the `FullProfile` resolution above, (2) the no-Profile fallback in `HumanViewComponent` (pre-onboarding users), (3) infrastructure mutations (merge / purge / delete labels in `UserRepository`).
- Search-results, team rosters, audit-log labels, etc., must NOT pass an explicit `display-name`/override — let the VC fetch.

**Carve-outs (legal/financial identity, NOT display):**

- SEPA pain.001 payee name + Holded purchase document contact name (`ExpenseReportService.SubmitAsync` payee snapshot). The bank rejects transfers when the payee name doesn't match the bank-account holder's legal identity, so these records must use `Profile.FirstName + " " + Profile.LastName` (the legal-name fields), falling back to `User.DisplayName` only when the legal-name fields are blank. Never use `BurnerName` for financial records — a pseudonym does not match the bank account.

**Related:** `src/Humans.Application/FullProfile.cs` (resolution); `src/Humans.Web/ViewComponents/HumanViewComponent.cs` (rendering path); `src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs:126` (search uses BurnerName as primary name match).
