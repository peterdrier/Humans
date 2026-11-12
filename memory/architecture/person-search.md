---
name: person-search
description: HARD RULE. Person search routes only through `IUserService.SearchUsersAsync`, rendered via `<vc:human-search>`/`<vc:user-search-result>`. Emergency contacts aren't searchable.
---

Every person search goes through one service method and one of two UI components. Don't fork either.

**Service:** `IUserService.SearchUsersAsync(query, PersonSearchFields fields, limit = 10, ct)` returning
`HumanSearchResult`s. `PersonSearchFields` (`src/Humans.Base/Users/PersonSearchFields.cs`) is a flags
enum:

| Flag | Matches | Who |
|---|---|---|
| `Name` | `User.BurnerName`, the display name | public |
| `Bio` | bio, city, interests, CV, pronouns, all-profiles contact fields | public |
| `ExactName` | `BurnerName`, folded full-string equality (collision counts) | public |
| `LegalName` | legal first/last name; deanonymizes burners | admin/coordinator |
| `Admin` | verified emails and non-public contact fields | admin/board |

Composites: `PublicAll = Name | Bio`, `ManageAll = PublicAll | LegalName` (pickers that find by real
name without private contact data), `AdminAll = ManageAll | Admin`.

**Invariants:**
- **Emergency-contact data is never searchable**, under any flag combination.
- The service always filters to "not rejected, not deleted". Suspended and unapproved profiles stay
  searchable in public scopes ([`profile-visibility-acceptable`](../product/profile-visibility-acceptable.md)).
- **The controller is the auth boundary.** A non-admin endpoint passing `Admin` or `LegalName` is a
  programmer error caught in review; every call site names its flags literally.
- Results come back unordered; sort in the controller or view
  ([`display-sort-in-controllers`](display-sort-in-controllers.md)). `limit` is a safety cap, not a
  presentation choice.

**The two UI patterns:**
- **`<vc:human-search>`** — inline picker inside a form; sets a hidden user id; backed by
  `/api/profiles/search`.
- **`<vc:user-search-result>`** — one result row keyed by user id (owned by Users); the caller wraps rows
  in a `.list-group` and owns its empty state. Used by the header search's Humans bucket.

**`allow-email` on the picker:** a query containing `@` resolves as an exact, case-insensitive match on
a verified email with exactly one owner, returning at most one person. Exact-only means no enumeration,
so it is safe on non-admin surfaces (ticket-transfer recipient lookup). The fuzzy email search behind
`Admin` stays admin-gated.

**Deliberately separate:** shift-volunteer search (`ShiftVolunteerSearchBuilder`: people against shifts,
different inputs and output), and the tickets "has not bought" admin filter (matches in memory against
emails it already loaded). The admin users directory is a paged list, not a search result.

**Why one method:** per-scope methods each accreted variants nobody authorized, a predicate parameter
makes call sites impossible to audit by grep, and the flags *are* the public/admin split.
