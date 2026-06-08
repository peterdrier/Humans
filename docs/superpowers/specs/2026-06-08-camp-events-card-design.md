# Camp Events Card — Design

**Date:** 2026-06-08
**Branch:** `feat/camp-events-card`
**Sections touched:** Events (new cross-section read surface + ViewComponent + favourite-toggle action), Camps (one view edit)

## Goal

Surface a camp's published events on its detail page. On `/Barrios/{slug}` (and the `/Camps/{slug}` alias), show a compact card listing the **approved** events that camp is hosting this edition, with the same per-row favourite (heart) toggle the `/Events/Browse` page uses. The card sits **between the Roster card and the About card**.

## Decisions (locked)

- **Reuse, not relift.** The data already exists behind `IEventService.GetApprovedEventsAsync(campId, …)` (approved-only, served from the T-03 cache). The card reuses it via a ViewComponent — no new query, no new service method, no copy of the Browse view.
- **ViewComponent, invoked with ids only.** A new Events-owned `CampEventsViewComponent` takes the camp's `Guid` (+ `slug`) and renders the card. The Camps view passes only ids, so **no Event types leak into Camps**.
- **Auth-gated.** The card renders for authenticated Humans users only — never on the anonymous/public camp view. Gated at the call site, matching the existing "My membership" card (`Details.cshtml:305`).
- **Approved-only, identical for everyone.** Not lead-aware; leads keep the existing "Manage Events" button for drafts/pending.
- **Cross-section read via `IEventServiceRead`.** This card is Events' first external consumer, so we introduce the read interface (per the hard rules: cross-section reads go through `I<Section>ServiceRead`). It is a **plain interface segregation** over the existing service — **no new cache layer / read-model projection** this round.
- **Favourites reuse the existing (per-surface) pattern.** A thin `ToggleCampFavourite` action reuses `IEventService.ToggleFavouriteAsync` and redirects back to the camp page, mirroring the existing `Unfavourite`→Schedule action. The favourites API is acknowledged tech debt; a cleaner design is deferred to next year (owner: Peter).
- **Plain chronological list.** Sorted by `StartAt` ascending; not day-grouped like Browse. Recurring events are listed **once** (no occurrence expansion → no gate-opening-date dependency).

## Components

### 1. `IEventServiceRead` (new) — `src/Humans.Application/Interfaces/Events/IEventServiceRead.cs`

The cross-section read surface. Three method declarations **relocated** off `IEventService` (no behaviour change):

```csharp
public interface IEventServiceRead
{
    Task<IReadOnlyList<ApprovedEventView>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);

    Task<EventGuideSettingsView?> GetGuideSettingsAsync(CancellationToken ct = default); // TimeZoneId for local-time render

    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default); // per-row heart state
}

public interface IEventService : IEventServiceRead { /* remaining members unchanged */ }
```

`CachingEventService` already implements all three (via `IEventService`); no implementation change.

### 2. DI registration — `src/Humans.Web/Extensions/Sections/EventsSectionExtensions.cs`

One line, forwarding the read interface to the same Singleton that backs `IEventService` (reads stay on the existing cache):

```csharp
services.AddSingleton<IEventServiceRead>(sp => sp.GetRequiredService<CachingEventService>());
```

### 3. `CampEventsViewComponent` (new) — `src/Humans.Web/ViewComponents/CampEventsViewComponent.cs`

- Injects `IEventServiceRead` + `ILogger`.
- `InvokeAsync(Guid campId, string campSlug)`:
  - Resolve current user id from `UserClaimsPrincipal` (`ClaimTypes.NameIdentifier`); on no/invalid id → `Content(string.Empty)`.
  - `events = GetApprovedEventsAsync(campId, null, null, null, [])`. If empty → `Content(string.Empty)` (**card auto-hides**).
  - `settings = GetGuideSettingsAsync()` → `TimeZoneId`; resolve `DateTimeZone` (fallback UTC if null).
  - `favouriteIds = GetFavouriteEventIdsAsync(userId)`.
  - Project to VM rows: convert `StartAt` (`Instant`) → local `DateTime` via the zone, compute end (`+DurationMinutes`), carry `Title`, `CategoryName`, `VenueName`, `LocationNote`, `Host`, `IsRecurring`, and `IsFavourited = favouriteIds.Contains(Id)`. Order by `StartAt`.
  - Return `View(vm)`.
- Wrap the body in try/catch → log + `Content(string.Empty)` (matches `MyCampsViewComponent` / `ShiftSignupsViewComponent` resilience).

### 4. View-model — `src/Humans.Web/Models/Events/CampEventsCardViewModel.cs`

```csharp
public class CampEventsCardViewModel
{
    public string CampSlug { get; set; } = string.Empty;     // toggle return target
    public List<CampEventsCardRow> Rows { get; set; } = [];
}

public class CampEventsCardRow
{
    public Guid EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }      // local
    public int DurationMinutes { get; set; }
    public string? VenueName { get; set; }
    public string? LocationNote { get; set; }
    public string? Host { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsFavourited { get; set; }
}
```

### 5. View — `src/Humans.Web/Views/Shared/Components/CampEvents/Default.cshtml`

Card chrome matching sibling cards (`<div class="card mb-4">` + `<div class="card-header"><i class="fa-solid fa-calendar-days me-1"></i> Events</div>`). Each row:

- Left: day + time range (`StartAt`–end), title, category badge, then a muted line with `📍 VenueName / LocationNote`, `👤 Host`, and a recurring marker when `IsRecurring`. Reuses the same duration/format idiom as `Browse.cshtml`.
- Right (upper corner): antiforgery POST form to `ToggleCampFavourite` with `slug` + `eventId`; button `btn-outline-danger` (not favourited) ↔ `btn-danger` (favourited), `fa-heart`. Same markup family as `Browse.cshtml:116–129`, minus the Browse filter-state hidden fields.

### 6. Favourite-toggle action — `src/Humans.Web/Controllers/EventsController.cs`

```csharp
[HttpPost("Barrio/{slug}/Favourite/{eventId:guid}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ToggleCampFavourite(string slug, Guid eventId)
{
    var user = await GetCurrentUserInfoAsync();
    if (user == null) return Challenge();
    await guide.ToggleFavouriteAsync(user.Id, eventId);
    return RedirectToAction("Details", "Camp", new { slug });
}
```

Route follows the existing surface-prefixed favourite convention (`Browse/Favourite/{eventId}`, `Schedule/Unfavourite/{eventId}`) → full path `/Events/Barrio/{slug}/Favourite/{eventId}`. Reuses the existing `ToggleFavouriteAsync` write. Per-surface redirect (back to the camp page) instead of extending the `Browse/Favourite` action, which round-trips Browse filter state.

### 7. Camp detail edit — `src/Humans.Web/Views/Camp/Details.cshtml`

Between the Roster partial (`:95`) and the About card (`:98`):

```cshtml
@if (User.Identity?.IsAuthenticated == true)
{
    <vc:camp-events camp-id="@Model.Id" camp-slug="@Model.Slug" />
}
```

## Data flow

```
CampController.Details (GET, /Barrios/{slug})
  → Details.cshtml [auth gate]
      → <vc:camp-events camp-id camp-slug>
          → IEventServiceRead.GetApprovedEventsAsync(campId,…)  (T-03 cache)
          → IEventServiceRead.GetGuideSettingsAsync()           (TimeZoneId)
          → IEventServiceRead.GetFavouriteEventIdsAsync(userId)
          → CampEventsCardViewModel → Default.cshtml

Heart click (POST /Events/Barrio/{slug}/Favourite/{eventId})
  → EventsController.ToggleCampFavourite
      → IEventService.ToggleFavouriteAsync(userId, eventId)
      → redirect → CampController.Details (heart re-rendered)
```

## Error handling

- Anonymous / unresolved user → render nothing (card absent).
- No approved events for the camp → render nothing (card absent).
- Any read failure inside the VC → logged, render nothing (never breaks the camp page).
- Toggle without an authenticated user → `Challenge()` (login), same as existing favourite actions.

## Testing

- **Architecture** (`tests/Humans.Application.Tests/Architecture/EventsArchitectureTests.cs`): assert `IEventService : IEventServiceRead`; assert `IEventServiceRead` is registered and resolves to the same instance as `IEventService` (the caching Singleton).
- **ViewComponent** (`tests/Humans.Application.Tests/ViewComponents/CampEventsViewComponentTests.cs`, alongside the existing VC tests): approved events for the camp project to ordered rows with correct `IsFavourited`; empty event list → `Content(string.Empty)`; unauthenticated principal → `Content(string.Empty)`.
- **Controller** (`tests/Humans.Web.Tests/Controllers/EventsControllerTests.cs`): `ToggleCampFavourite` calls `ToggleFavouriteAsync(userId, eventId)` and redirects to `Camp/Details` with the slug; unauthenticated → `Challenge()`.

## Out of scope (explicit)

- No favourites-API redesign (deferred to next year).
- No caching decorator / read-model projection for `IEventServiceRead`.
- No occurrence/recurrence expansion in the card.
- No changes to the anonymous/public camp view.
- No day-grouping, filtering, or search in the card.

## Build sequence

1. Add `IEventServiceRead`; make `IEventService : IEventServiceRead` (relocate the three declarations). Build.
2. Register `IEventServiceRead` forward in `EventsSectionExtensions`. Build.
3. Add `CampEventsCardViewModel`, `CampEventsViewComponent`, `Default.cshtml`. Build.
4. Add `ToggleCampFavourite` action. Build.
5. Edit `Details.cshtml` to invoke the VC behind the auth gate. Build.
6. Tests (arch + VC + controller). `dotnet test Humans.slnx -v quiet`.
7. Manual smoke on `/Barrios/{slug}` (logged in): card shows between Roster and About, hearts toggle and persist.
</content>
</invoke>
