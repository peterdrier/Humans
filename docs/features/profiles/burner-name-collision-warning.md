<!-- freshness:triggers
  src/Humans.Web/Controllers/ProfileApiController.cs
  src/Humans.Application/Services/Profiles/PersonSearchFields.cs
  src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs
  src/Humans.Infrastructure/Services/Users/CachingUserService.cs
  src/Humans.Web/Models/SearchResponseModels.cs
  src/Humans.Web/Views/Profile/Edit.cshtml
-->
<!-- freshness:flag-on-change
  Exact-match semantics (accent-/case-folded full-string equality), the self/rejected/tombstone exclusion set, or the burner-name-count endpoint contract — review when any of these shift.
-->

# Burner-Name Collision Warning

## Business Context

Burner names are not unique — two humans can legitimately share one. But a user
typing a new burner name on the edit-profile page has no way of knowing they are
picking a name many other humans already use, which makes them harder to tell
apart in search, popovers, and coordinator listings. This feature adds a live,
non-blocking nudge on `/Profile/Me/Edit`: as the user types, it tells them how
many **other** humans already use that exact burner name.

It is a *warning*, never a *block* — duplicate burner names remain valid. The
count is informational so the user can choose a more distinctive name if they
want to.

## User Stories

### US-BNC.1: Live collision count while editing my burner name
**As a** user editing my own profile
**I want to** see how many other humans already use the burner name I'm typing
**So that** I can choose a more distinctive name if I'd rather not collide

**Acceptance Criteria:**
- On `/Profile/Me/Edit`, typing in the burner-name field fires a debounced
  (350 ms) lookup against `GET /api/profiles/burner-name-count`.
- The warning shows the count and the typed name, localized singular/plural, and
  hides when the count is `0` (or the field holds fewer than 2 characters).
- The match is **exact**: accent-/case-folded full-string equality on the
  resolved display name — not substring, token, or prefix. `José` collides with
  `jose`; `Peter` does not collide with `Peter Pan`.
- The count **excludes the editing user themselves**, and excludes rejected
  profiles and tombstoned (profile-less) users.
- The count is **uncapped** — it reports the true number of collisions, never
  a clamped subset.
- The warning is best-effort: transient fetch failures are swallowed and never
  block editing or saving.

### US-BNC.2: The self-exclusion cannot be spoofed
**As the** system
**I want to** identify "the editing user" from the authenticated session, not a
client-supplied id
**So that** the count can't be skewed by a caller passing an arbitrary id

**Acceptance Criteria:**
- `GET /api/profiles/burner-name-count` takes only `name`; the user to exclude is
  resolved server-side via `ResolveCurrentUserOrUnauthorizedAsync()`.
- The endpoint is `[Authorize]`; an unresolvable session fails closed.

## Endpoints

| Method | Route | Auth | Returns |
|---|---|---|---|
| `GET` | `/api/profiles/burner-name-count?name={name}` | `[Authorize]` | `BurnerNameCountResult` `{ count }` — exact-name collisions excluding the session user (200); `{ count: 0 }` for a blank/whitespace name |

`BurnerNameCountResult` is a typed record in `Humans.Web.Models`
(per [`memory/code/search-endpoint-response-shape.md`](../../../memory/code/search-endpoint-response-shape.md)
— search/JSON endpoints return stable records, not anonymous objects).

## Data Model

No schema changes. The count is computed in memory off the `CachingUserService`
`UserInfo` snapshot — no new DB query.

`PersonSearchFields.ExactName = 1 << 4` — a new public enum value (its own bit;
existing `Name` / `PublicAll` / `ManageAll` / `AdminAll` are unchanged). When set,
`PersonSearchMatcher` matches by accent-/case-folded full-string equality on the
resolved burner name, reusing the existing `Fold` helper.

Because `ExactName` is name-equality semantics, `CachingUserService.SearchUsersAsync`
**skips its GUID-by-id fast path** when the `ExactName` bit is set: a GUID-shaped
burner name must match by name, and a typed GUID must never resolve to the user
whose `Id` equals the text. The fast path (paste a UserId to jump to that human)
still applies to general search.

## View Wiring

`Edit.cshtml` renders a `d-none` warning `<div>` below the burner-name field
carrying the singular/plural message templates as `data-*` attributes. A
debounced `input` handler in the existing nonce'd script block fetches the count
action with the typed name (no id is sent — CSP-safe: `data-*` + `addEventListener`),
selects the singular/plural template by count, substitutes `{0}` (count) and
`{1}` (typed name), and toggles visibility. A monotonic sequence guard discards
responses superseded by a later keystroke.

Localization keys `ProfileEdit_BurnerNameCollisionOne` /
`ProfileEdit_BurnerNameCollisionMany` are defined in `SharedResource.resx` and the
five locale siblings (es/ca/fr/de/it).

## Related

- [`docs/sections/Profiles.md`](../../sections/Profiles.md) — Profile section invariants.
- [`docs/features/profiles/profile-search-detail.md`](profile-search-detail.md) — the person-search endpoint and matcher this feature extends.
- [`memory/code/search-endpoint-response-shape.md`](../../../memory/code/search-endpoint-response-shape.md) — typed search/JSON responses.
