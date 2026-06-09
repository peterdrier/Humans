# Personal iCal Feed — Design

**Date:** 2026-06-09
**Issue:** nobodies-collective/Humans#161 (iCal bullet only; CSV exports and stats dashboard stay open)

## Summary

A per-user iCal subscription feed at `GET /api/ical/{userId:guid}/{token:guid}.ics`, built by fanning out across sections that contribute calendar items — same shape as the GDPR export fanout (`IUserDataContributor`). First two contributors: Shifts (signups the user is on) and Events (guide events the user has favourited). An admin-facing `<vc:user-calendar>` view component on the user admin detail page renders the same aggregated items the API serializes, for visibility into what the feed emits.

## Decisions made

- **Token scheme: stored `User.ICalToken`** (existing `Guid?`, src/Humans.Domain/Entities/User.cs). A derivable token (Data Protection or HMAC, like email unsubscribe links) was rejected: calendar URLs are handed to third parties (Google fetches server-side) and live for years, so per-user revocation matters; a derivable scheme can only revoke by rotating the server secret, breaking every subscriber. The stored token already ships with lazy minting (`ShiftsController.EnsureICalUrlAsync`), a regenerate button (`RegenerateIcal`), and clearing on GDPR-delete and account merge.
- **URL shape: `/api/ical/{userId}/{token}.ics`** — uid in the URL makes validation a cached `IUserServiceRead` lookup by id + token compare; no lookup-by-token query or index. The previously displayed `/ICal/{token}.ics` shape was never served, so changing it breaks nothing.
- **Shift scope: Confirmed + Pending** signups only. Issue #161's "cancelled/bailed/noshow history in descriptions" is deliberately out of scope (note this on the issue).
- **No caching** of feed output. ~500 users; a request is two cached lookups and two indexed queries.

## Fanout contract

New files in `src/Humans.Application/Interfaces/ICalFeed/`:

```csharp
public interface ICalendarFeedContributor : IFanout
{
    Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct);
}

public sealed record CalendarFeedItem(
    string Uid,            // stable across fetches: "shift-{signupId}@humans.nobodies.team", "event-{eventId}-{occurrenceDate:yyyyMMdd}@humans.nobodies.team"
    string Source,         // contributing section, e.g. "Shifts", "Events"; emitted as ICS CATEGORIES
    string Summary,
    string? Description,
    Instant Start,
    Instant End,
    string? Location,
    string? Url);          // absolute deep link back into the app; emitted as ICS URL

// The base URL is hardcoded to https://humans.nobodies.team for the 2026 cycle
// (Peter's call, 2026-06-10) — no per-event public detail pages exist, so shifts
// link to /Shifts/Mine and events to /Events/Schedule. Make it configurable for 2027.
```

Contributors return absolute `Instant`s — each section resolves its own wall-clock times through `EventSettings.TimeZoneId`. The serializer emits UTC; calendar clients localize.

## Coordinator — `ICalFeedService`

`IICalFeedService` in `src/Humans.Application/Interfaces/ICalFeed/`, implementation in `src/Humans.Application/Services/ICalFeed/`. An orchestrator (`IApplicationService`, no repositories). Injects `IUserServiceRead` + `IEnumerable<ICalendarFeedContributor>`.

Two methods:

- `Task<IReadOnlyList<CalendarFeedItem>> GetFeedItemsAsync(Guid userId, CancellationToken ct)` — sequential fan-out (mirrors `GdprExportService`), merged and sorted by `Start`. Used by the view component (no token check — caller is already authorized server-side). Unknown user yields an empty list.
- `Task<string?> GetFeedIcsAsync(Guid userId, Guid token, CancellationToken ct)` — validates the token (user exists, not merged/deleted, `ICalToken` set and equal), then calls `GetFeedItemsAsync` and serializes with Ical.Net 5.2.2 (already referenced by Application). Returns `null` on any validation failure — controller maps to 404 with no oracle distinguishing unknown user from wrong token.

Calendar metadata: `PRODID` for the app, `X-WR-CALNAME: Nobodies`. Empty item list still yields a valid calendar.

DI: new `ICalFeedSectionExtensions` in `src/Humans.Web/Extensions/Sections/` mirroring `GdprSectionExtensions`. Each contributor registers in its own section extension: `services.AddScoped<ICalendarFeedContributor>(sp => sp.GetRequiredService<XService>())`.

## Endpoint

New thin `ICalFeedApiController` in `src/Humans.Web/Controllers/Api/`: anonymous `GET /api/ical/{userId:guid}/{token:guid}.ics` → `GetFeedIcsAsync` → 404 or `File(utf8Bytes, "text/calendar")` with a filename. Try/catch with logging per project error-handling rule.

`ShiftsController.EnsureICalUrlAsync` updates to emit the two-segment URL. Token minting/regeneration/clearing lifecycles are untouched.

## Contributors

**Shifts** — `ShiftSignupService` implements `ICalendarFeedContributor`, registered in the Shifts section extension. `GetByUserAsync(userId)` → filter `Confirmed` + `Pending` → resolve times via `Shift.GetAbsoluteStart/End(EventSettings)`. Summary = rota name (+ team name); Pending signups get a `(pending)` suffix in the summary. Description carries the rota's `PracticalInfo` and shift `Description`.

**Events** — `EventService` implements `ICalendarFeedContributor`, registered in the Events section extension (alongside its existing `IUserDataContributor` registration). `GetFavouritesWithEventsAsync(userId)` → filter still-`Approved` → expand recurring events via the existing `GetOccurrenceInstants(gateOpeningDate, timeZone)` (one item per occurrence; `End` = occurrence + `DurationMinutes`). Location = venue/camp name + `LocationNote`; description includes `Host` and category name.

The community Calendar section is an obvious later third contributor; not in scope.

## Admin view component — `<vc:user-calendar>`

`UserCalendarViewComponent` in `src/Humans.Web/ViewComponents/`, view at `Views/Shared/Components/UserCalendar/Default.cshtml`. Takes `user-id`. Calls `IICalFeedService.GetFeedItemsAsync(userId)` — the exact items the API serializes, one code path.

Renders a card: table sorted by `Start` with source badge, summary, start–end, location; item count in the header; a line noting whether the user's feed token is provisioned (`UserInfo.ICalToken` null or not). **Never displays the secret URL or token.** Try/catch with logging, rendering an error-state card body on failure (matches `ShiftSignupsViewComponent`).

Placement: `Views/UsersAdmin/AdminDetail.cshtml` (after `<vc:shift-signups>`), plus a Widget Gallery entry following the existing card pattern.

## Testing

- Contributor unit tests: status filtering (Confirmed/Pending in, others out; non-Approved favourites out), recurrence expansion, timezone resolution, pending annotation.
- `ICalFeedService` tests: aggregation + sort across contributors, token validation matrix (no user / merged / null token / wrong token / right token), ICS output parses back with Ical.Net, empty feed is valid.
- Controller/integration: wrong token → 404, right token → 200 `text/calendar`. (Regenerating the token kills the old URL via the same wrong-token comparison path — covered by the wrong-token unit cases rather than a DB-seeded integration test.)
- View component test if existing components have them; otherwise covered by service tests.

## Out of scope

- CSV exports and stats dashboard from #161.
- Cancelled/bailed/noshow shift history in the feed.
- Community Calendar contributor.
- Feed output caching.
