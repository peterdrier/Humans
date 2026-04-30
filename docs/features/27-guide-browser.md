# In-App Guide Browser

## Business Context

The published event guide is currently only consumable through the standalone PWA via the `/api/guide` endpoints. Humans members have no way to browse approved events from within the Humans app itself. Adding a simple, read-only guide browser inside Humans lets members discover events, filter by day/category/camp, and access their favourites and personal schedule without leaving the platform. This complements the PWA (which targets anonymous attendees on-site) by providing a first-class experience for logged-in humans.

## User Stories

### US-27.1: Browse Approved Events
**As a** logged-in human
**I want to** browse all approved events within the Humans app
**So that** I can discover what's happening without switching to the PWA

**Acceptance Criteria:**
- Page at `/EventGuide/Browse` showing all approved events
- Events displayed as cards with: title, category badge, date/time, duration, venue (camp name or shared venue name)
- Sorted by day then start time by default
- Recurring events shown as separate entries per occurrence
- Empty state when no approved events exist yet
- Gated by the `Features:EventGuide` toggle

### US-27.2: Filter and Search Events
**As a** logged-in human
**I want to** filter events by day, category, and camp, and search by keyword
**So that** I can quickly find events I'm interested in

**Acceptance Criteria:**
- Day filter: toggle buttons for each event day (multi-select, derived from GuideSettings date range and timezone)
- Category filter: dropdown listing active categories
- Venue filter: dropdown listing active GuideSharedVenues (replaces camp filter; individual events use shared venues)
- Keyword search: matches against title and description (case-insensitive)
- Filters are combinable (AND logic)
- Filters update results without full page reload (use partial views or minimal JS)
- Active filters are visible and clearable

### US-27.3: View Event Detail
**As a** logged-in human
**I want to** see full details of an event
**So that** I can decide whether to attend

**Acceptance Criteria:**
- Clicking an event card expands it inline or navigates to a detail section
- Detail shows: title, full description, category, date/time, duration, location (camp or venue name + grid address + location note), submitter display name
- If the event is recurring, show all occurrence dates
- Back/close returns to the filtered list preserving filter state

### US-27.4: Favourite Events from the Browser
**As a** logged-in human
**I want to** favourite/unfavourite events directly from the guide browser
**So that** I can build my personal schedule while browsing

**Acceptance Criteria:**
- Each event card shows a favourite toggle (heart icon or similar)
- Toggle calls the existing `/api/guide/favourites/{eventId}` POST/DELETE endpoints
- Favourite state is reflected immediately without full page reload
- A "Show favourites only" filter option to see only favourited events
- Favourites persist via existing `UserEventFavourite` records

### US-27.5: Respect Category Opt-Out Preferences
**As a** logged-in human
**I want** my category opt-out preferences to apply in the guide browser
**So that** I don't see events in categories I've excluded

**Acceptance Criteria:**
- Events in categories the user has opted out of are hidden by default
- A toggle or notice allows the user to temporarily show all categories
- Opt-out preferences are read from existing `UserGuidePreference` records

## Data Model

No new entities required. The browser reads from existing tables:

| Entity | Usage |
|--------|-------|
| `GuideEvent` | Source of all approved events |
| `EventCategory` | Category names, slugs, sensitive flag |
| `Camp` | Camp names for camp-anchored events |
| `GuideSharedVenue` | Venue names for individual events |
| `UserEventFavourite` | Current user's favourited events |
| `UserGuidePreference` | Current user's excluded categories |
| `GuideSettings` + `EventSettings` | Date range and timezone for day derivation |

## Routes

| Route | Purpose |
|-------|---------|
| `/EventGuide/Browse` | Main guide browser page |

## UI Notes

- Keep the design simple and consistent with the existing Humans UI (Bootstrap cards, same nav patterns)
- Day tabs at the top, filter bar below, event cards in the main area
- Mobile-friendly: cards stack vertically, filters collapse into a dropdown on small screens
- No JavaScript framework required — server-rendered partials with minimal JS for favourites and filter toggling

## Authorization

| Role | Access |
|------|--------|
| Any authenticated human | Full read access to the guide browser |
| Anonymous | No access (redirect to login) |

## Related Features

- **Event Guide Management** (26): Source of all event data and the moderation workflow
- **Favourites and Schedule** (US-26.8): Reuses `UserEventFavourite` and the `/EventGuide/Schedule` page
- **Category Opt-Out** (US-26.7): Reuses `UserGuidePreference`
- **Feature Toggle**: Gated by `Features:EventGuide` in appsettings, same as all other guide UI
