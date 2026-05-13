# Event Guide Route Renaming

## Summary

Rename the Event Guide user-facing routes to match Elsewhere terminology:

- replace top-level `EventGuide/...` routes with `Events/...`
- replace camp-scoped `Camps/{slug}/Events/...` routes with `Barrios/{slug}/Events/...`
- replace `api/guide/...` routes with `api/events/...`

This is a pre-release route cleanup. No backward-compatibility aliases are required because the Event Guide has not been released publicly.

## Goals

- Use `Barrios` rather than `Camps` in all user-facing Event Guide URLs.
- Shorten the main guide area from `EventGuide` to `Events`.
- Make the API prefix consistent with the UI route naming.
- Keep internal persistence and domain naming unchanged for now (`Camp`, `camp_*`, `GuideEvent.CampId`).

## Non-Goals

- Renaming database tables, entity types, DTO property names, or service/repository namespaces.
- Renaming the broader `CampController` area outside the Event Guide scope.
- Preserving old `EventGuide/...`, `Camps/...`, or `api/guide/...` URLs.

## Current Routes

### Web

- `/EventGuide/MySubmissions`
- `/EventGuide/Submit`
- `/EventGuide/Submit/{eventId}/Edit`
- `/EventGuide/Submit/{eventId}/Withdraw`
- `/EventGuide/Schedule`
- `/EventGuide/Browse`
- `/EventGuide/Browse/Favourite/{eventId}`
- `/EventGuide/Schedule/Unfavourite/{eventId}`
- `/EventGuide/Moderate`
- `/EventGuide/Moderate/Approve`
- `/EventGuide/Moderate/Reject`
- `/EventGuide/Moderate/RequestEdit`
- `/EventGuide/Dashboard`
- `/EventGuide/Export`
- `/EventGuide/Export/Csv`
- `/EventGuide/Export/PrintGuide`
- `/Camps/{slug}/Events`
- `/Camps/{slug}/Events/New`
- `/Camps/{slug}/Events/{eventId}/Edit`
- `/Camps/{slug}/Events/{eventId}/Withdraw`
- `/Barrios/{slug}/Events`
- `/Barrios/{slug}/Events/New`
- `/Barrios/{slug}/Events/{eventId}/Edit`
- `/Barrios/{slug}/Events/{eventId}/Withdraw`

### API

- `/api/guide/events`
- `/api/guide/events/{id}`
- `/api/guide/camps`
- `/api/guide/camps/{id}`
- `/api/guide/categories`
- `/api/guide/preferences`
- `/api/guide/favourites`
- `/api/guide/favourites/{eventId}`

## Proposed Routes

### Web

- `/Events/MySubmissions`
- `/Events/Submit`
- `/Events/Submit/{eventId}/Edit`
- `/Events/Submit/{eventId}/Withdraw`
- `/Events/Schedule`
- `/Events/Browse`
- `/Events/Browse/Favourite/{eventId}`
- `/Events/Schedule/Unfavourite/{eventId}`
- `/Events/Moderate`
- `/Events/Moderate/Approve`
- `/Events/Moderate/Reject`
- `/Events/Moderate/RequestEdit`
- `/Events/Dashboard`
- `/Events/Export`
- `/Events/Export/Csv`
- `/Events/Export/PrintGuide`
- `/Barrios/{slug}/Events`
- `/Barrios/{slug}/Events/New`
- `/Barrios/{slug}/Events/{eventId}/Edit`
- `/Barrios/{slug}/Events/{eventId}/Withdraw`

### API

- `/api/events/events`
- `/api/events/events/{id}`
- `/api/events/barrios`
- `/api/events/barrios/{id}`
- `/api/events/categories`
- `/api/events/preferences`
- `/api/events/favourites`
- `/api/events/favourites/{eventId}`

## Rationale

- `Events` is concise and reads naturally in navigation.
- `Barrios` is the correct Elsewhere user-facing term even if the underlying entity remains `Camp`.
- `api/events` aligns the API prefix with the main feature area and avoids leaking the implementation term `guide`.
- No alias burden keeps the codebase simpler because this feature is not yet public.

## Implementation Scope

### Route Attributes

Update route prefixes in:

- `src/Humans.Web/Controllers/EventGuideController.cs`
- `src/Humans.Web/Controllers/ModerationController.cs`
- `src/Humans.Web/Controllers/EventGuideDashboardController.cs`
- `src/Humans.Web/Controllers/EventGuideExportController.cs`
- `src/Humans.Web/Controllers/CampEventsController.cs`
- `src/Humans.Web/Controllers/Api/GuideApiController.cs`

### Internal Links

Update controller/view links in:

- `src/Humans.Web/Views/Shared/_Layout.cshtml`
- `src/Humans.Web/Views/EventGuide/*`
- `src/Humans.Web/Views/CampEvents/*`
- `src/Humans.Web/Views/Camp/Details.cshtml`
- any `Url.Action(...)`/redirect references to `EventGuide`, `CampEvents`, or `/api/guide/...`

### Docs and Tests

Update references in:

- `docs/features/26-events.md`
- `docs/features/27-guide-browser.md`
- `docs/sections/EventGuide.md`
- rename `docs/sections/EventGuide.md` to `docs/sections/Events.md` and update its title, route table, and canonical route references to the new `Events`/`Barrios`/`api/events` naming
- tests that hardcode `EventGuide/...` or `api/guide/...`

## Migration Constraints

- Internal type names such as `CampEventsController`, `GuideEvent`, `GuideApiController`, and `CampEventOverlap` may remain unchanged in this refactor.
- Database schema remains unchanged.
- No redirects or dual-route aliases should be added.

## Risks

- Hardcoded links in views, comments, tests, or email URLs may be missed.
- The PWA or any preview-only external consumer using `/api/guide/...` will break if not updated in the same change.
- User-visible copy may still say “camp” in places tied to the API or email templates.

## Rollout Plan

1. Change route attributes and internal MVC links.
2. Change API route prefix and barrio resource naming.
3. Rename `docs/sections/EventGuide.md` to `docs/sections/Events.md` and update section-doc wording to the new canonical names.
4. Update tests and remaining docs to the new canonical URLs.
5. Run focused tests and a full web build.
6. Verify navigation manually:
   - main Events nav
   - individual submission flow
   - barrio event flow
   - moderation
   - dashboard/export
   - favourites/schedule
   - API endpoints used by the guide browser

## Verification

- `dotnet build src/Humans.Web/Humans.Web.csproj`
- targeted controller/integration tests covering guide routes
- manual smoke test of the renamed URLs
