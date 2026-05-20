---
name: Person search uses one bit-flag service method and two canonical UI patterns
description: HARD RULE. All person-search call sites route through `IProfileService.SearchProfilesAsync(query, PersonSearchFields, limit)`. UI is one of two patterns — `<vc:human-search>` (inline picker) or `_HumanSearchResults` (page-style). Admin-bit fields require admin auth at the controller. Emergency-contact data is never searchable. Shift volunteer search is exempt.
---

Person search shows up across the app — Camp role assignment, team-admin member picker, public profile search page, admin humans list, ticket-transfer recipient lookup, etc. Today they all consolidate behind a single service method and two UI partials. Don't fork.

**Service API — single method, bit-flag input:**

```csharp
[Flags] public enum PersonSearchFields
{
    None = 0,
    Name = 1,    // Profile.BurnerName + User.DisplayName
    Bio  = 2,    // bio, city, interests, CV, pronouns + AllActiveProfiles ContactFields
    Admin = 4,   // verified emails + non-public ContactFields (BoardOnly / CoordinatorsAndBoard / MyTeams)
    PublicAll = Name | Bio,
    AdminAll  = Name | Bio | Admin,
}

Task<IReadOnlyList<HumanSearchResult>> SearchProfilesAsync(
    string query, PersonSearchFields fields, int limit = 10, CancellationToken ct = default);
```

**Implicit scope:** the service always filters to "not rejected, not deleted". Suspended profiles surface only when the `Admin` bit is set.

**Hard invariants:**

- **Emergency-contact data is never searchable.** Regardless of bit-flag combination. `Profile.EmergencyContactName` / `EmergencyContactPhone` are skipped by every branch of `PersonSearchMatcher`.
- **Auth boundary is the controller, not the service.** Services are auth-free per design-rules §6. A non-admin endpoint passing `Admin` is a programmer error caught in code review, not a runtime check. The bit-flag is auditable at a glance — every call site reads `PersonSearchFields.X` literally.
- **Service returns matches in unspecified order.** Display ordering happens at the controller / view per [`display-sort-in-controllers.md`](display-sort-in-controllers.md). Don't push a `sortBy` parameter into the service.
- **Limit is a safety cap, not a presentation choice.** It protects against fan-out under broad queries. Controllers may further `.Take(N)` after their own `OrderBy`.

**Two canonical UI patterns:**

| Pattern | Component | When |
|---|---|---|
| Inline picker / autocomplete | `<vc:human-search>` (`HumanSearchViewComponent`) | Pick a single person inside a form. Sets a hidden `userId` field on selection. Backed by `/api/profiles/search`. Typed params: `field-name`, `instance-key`, `placeholder`, `scope`, `exclude-user-ids`, `selected-user-id` (optional prefill). |
| Page-style search results | `_HumanSearchResults` | Browse / find-then-act. Renders a list of cards. Used by `/Profile/Search` (public, `PublicAll`) and `/Profile/Admin` (`AdminAll`). |

Don't roll a third. If you need a new search surface, route it through one of these.

**Out-of-scope carve-outs:**

- `ShiftDashboardController.SearchVolunteers` and `ShiftVolunteerSearchBuilder` are intentionally separate. They search *people-against-shifts* (filtering by shift signups, availability, qualifications), not generic person-find. Different inputs, different output shape, different scope rules.
- `/Tickets/HasNotBought` admin search reaches `IUserEmailService.SearchUserIdsByVerifiedEmailAsync` directly because it cross-references purchase data, not profile data.

**Rejected design alternatives:**

- `SearchProfilesAsync(predicate)` — too permissive. Audit-by-grep is impossible when every call site can pass arbitrary lambdas.
- One method per scope (`SearchHumansAsync`, `SearchHumansByNameAsync`, …) — what we replaced. Each method accreted one tiny variant; the service surface grew without anyone deliberately authorizing the expansion.
- Splitting public vs admin into two interfaces — defeats the budget ratchet. The bit-flag *is* the public/admin split, sitting on a single method.
