<!-- freshness:triggers
  src/Humans.Web/Controllers/ProfileApiController.cs
  src/Humans.Web/Models/SearchResponseModels.cs
  src/Humans.Web/Views/Shared/_HumanSearchInput.cshtml
  src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs
  src/Humans.Application/DTOs/ProfileSearchResults.cs
  src/Humans.Application/Services/Profile/ContactFieldService.cs
  src/Humans.Application/Services/Profile/UserEmailService.cs
-->
<!-- freshness:flag-on-change
  Detail priority order, avatar visibility rule, or the viewer-access ladder used by ContactFieldService / UserEmailService — review when any of these shift.
-->

# Profile Search Detail (Picker Row Enrichment)

## Business Context

The shared human picker (`_HumanSearchInput`, used by barrio member setup, role-assignment, ticket-transfer admin, etc.) originally rendered each result row as a single line of burner/playa name. When multiple humans share a common Playa name — three "David"s in the same barrio is the canonical case — the picker can't be used to disambiguate without an out-of-band cross-check.

This feature adds a second line of context to each row and an avatar thumbnail, picked per-viewer so privacy rules stay intact.

## User Stories

### US-S.1: Disambiguate by visible contact info
**As a** barrio lead
**I want to** see a second line on each picker row (email / phone / Signal handle / etc.)
**So that** I can tell apart members whose Playa names collide

**Acceptance Criteria:**
- Each result row in the shared human picker shows: optional avatar (32px circular), burner/playa name (bold), and an optional detail line (small muted text) underneath.
- The detail line is computed per result based on what the **current viewer** is allowed to see; the search bit / endpoint does not change visibility.
- If the viewer can see nothing about the target beyond name, the detail line is omitted (no empty space, no placeholder).
- Row height stays bounded — 10 results scroll within the existing 260px dropdown max-height.

### US-S.2: Lookup by userId
**As a** caller that already knows a userId
**I want to** request the same picker row for that single human
**So that** I can pre-fill a picker, render a known reference, or recover from a name collision when search isn't narrowing enough

**Acceptance Criteria:**
- `GET /api/profiles/by-userid/{userId:guid}` returns one `HumanLookupSearchResult` (same shape as the search endpoint elements).
- Authentication required (same `[Authorize]` boundary as search). Privacy gating identical to search-result enrichment.
- 404 when the user does not exist or the profile is rejected.

## Data Model

No new entities. Reads from:
- `Profile` / `User` / `Profile.ProfilePictureData` — already projected into the cached `FullProfile` snapshot.
- `UserEmail` rows (visibility-filtered).
- `ContactField` rows (visibility-filtered).
- `RoleAssignment` (board check is upstream of the visibility ladder, not surfaced here directly).

`HumanSearchResult` (the service-returned DTO) carries `ProfileId` alongside `UserId` so the controller can drive `IContactFieldService.GetVisibleContactFieldsAsync` (which keys by profile id) without a separate `GetByUserIdsAsync` round-trip.

## Detail Priority Order

For each result row, the controller calls `GetSharedDetailAsync(userId, profileId, viewerUserId, ct)` and returns the first non-empty value:

1. **Highest-priority viewer-visible email** from `IUserEmailService.GetVisibleEmailsAsync(userId, accessLevel)`. Ordered: `IsPrimary` first, then alphabetical.
2. **Highest-priority viewer-visible contact field** from `IContactFieldService.GetVisibleContactFieldsAsync(profileId, viewerUserId)`. Ordered by type:
   1. `Phone`
   2. `Signal`
   3. `Telegram`
   4. `WhatsApp`
   5. `Discord`
   6. `Other`
   The obsolete `ContactFieldType.Email` is skipped (`UserEmail` is the canonical email source).
3. `null` — no second line is rendered.

Legal name (`Profile.FirstName + Profile.LastName`) is deliberately **not** part of the priority order, even for self or board viewers. The branch was previously included but produced essentially no value at ~500 users (only the tiny board cohort benefits) while costing a per-search `GetByUserIdsAsync` round-trip. Board members can still see legal name via the profile card on click-through. See `memory/architecture/no-business-logic-in-controllers.md` and the [PR #538 review thread](https://github.com/peterdrier/Humans/pull/538) for context.

## Privacy Gating

Viewer-visibility is delegated to the existing `IContactFieldService.GetViewerAccessLevelAsync(ownerUserId, viewerUserId)` ladder:

| Viewer relationship to target | Access level |
| --- | --- |
| Self | `BoardOnly` (sees everything self has set) |
| Board member | `BoardOnly` |
| Coordinator (any team) | `CoordinatorsAndBoard` |
| Shared team with target | `MyTeams` |
| None of the above | `AllActiveProfiles` |

`GetVisibleEmailsAsync` and `GetVisibleContactFieldsAsync` then filter rows whose `Visibility` is within the viewer's access level. Rows whose `Visibility` is `null` (explicitly private) are always excluded — including from self.

This is the same ladder used by `ProfileCardViewComponent` for the canonical profile page, so the picker row never reveals more than the click-through card would.

## Avatar Rule

Each row renders an `<img>` thumbnail only when `ProfilePictureUrlHelper.BuildEffectiveUrlsAsync` returns a non-null URL for the target. That helper returns a URL only when the target has a **custom-uploaded** profile picture (Google avatar URLs are intentionally excluded per issue nobodies-collective/Humans#532). Custom profile pictures are publicly served by the existing `/Profile/Picture/{id}` endpoint, so the picker is consistent with every other picture-display site (profile card, `<vc:human>`, etc.).

The view defends against broken URLs with `img.onerror = () => img.remove();` — a bad picture URL silently drops the thumbnail without leaving a broken-image icon.

## Caching

- **Search index + ProfileId resolution**: served from the `CachingUserService` `ConcurrentDictionary<Guid, UserInfo>` warmed at startup by the decorator's own `IHostedService.StartAsync` (inherited from `TrackedCache`, see design-rules §15d). No DB hit for the matcher itself; `ProfileId` lives on `UserInfo.Profile.Id`.
- **Single-person lookup endpoint**: served from the same dict via `IUserService.GetUserInfoAsync(userId)`.
- **Per-result email / contact-field reads**: not cached. These remain DB-bound, one round-trip per result row per visibility category. Acceptable at the project's ~500-user scale per `CLAUDE.md` ("prefer in-memory caching over query optimization"). Batching these is a separate, follow-up optimization if the picker latency ever becomes user-visible.

## XSS Posture

The Razor partial renders all user-controlled values (`displayName`, `detail`) via DOM `textContent`, never `innerHTML`. Avatar URLs come from `Url.Action` (server-side route generation), not user input. No raw HTML injection path exists.

## Endpoints

| Method | Route | Returns | Notes |
| --- | --- | --- | --- |
| `GET` | `/api/profiles/search?q={term}&scope={name|...}` | `HumanLookupSearchResult[]` | Existing endpoint. Detail enrichment added by this feature. Up to `MaxResults = 10` rows. |
| `GET` | `/api/profiles/by-userid/{userId:guid}` | `HumanLookupSearchResult` | New. Single-person lookup. 404 if user not found or profile rejected. |

`HumanLookupSearchResult` shape: `{ userId, displayName, detail, profilePictureUrl }`.
