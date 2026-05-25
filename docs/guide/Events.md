<!-- freshness:triggers
  src/Humans.Application/Services/Events/**
  src/Humans.Domain/Entities/Event.cs
  src/Humans.Domain/Entities/EventCategory.cs
  src/Humans.Domain/Entities/EventVenue.cs
  src/Humans.Domain/Entities/EventModerationAction.cs
  src/Humans.Domain/Entities/EventFavourite.cs
  src/Humans.Domain/Entities/EventPreference.cs
  src/Humans.Domain/Entities/EventGuideSettings.cs
  src/Humans.Web/Controllers/EventsController.cs
  src/Humans.Web/Controllers/EventsModerationController.cs
  src/Humans.Web/Controllers/EventsDashboardController.cs
  src/Humans.Web/Controllers/EventsExportController.cs
  src/Humans.Web/Controllers/EventsAdminController.cs
  src/Humans.Web/Controllers/Api/EventsApiController.cs
-->
<!-- freshness:flag-on-change
  Event submission window, moderation lifecycle, barrio event authority, favourites, category preferences, bulk upload rules, and print guide export — review when Events services/entities/controllers change.
-->

# Events

## What this section is for

Events is where you find out what's happening at the festival. Volunteers, camps, and collectives submit events for the programme — workshops, performances, rituals, and more — and once they're approved by moderators they appear in the public guide for everyone to browse.

If you run a camp or a workshop space, you can submit events on behalf of your camp during the submission window. Individual volunteers can submit their own events too. Everyone can save events to their personal favourites and hide categories they'd rather not see.

## Key pages at a glance

- **Browse events** (`/Events`) — the full programme of approved events, searchable and filterable by category, venue, or camp
- **Submit a camp event** (`/Events/Barrio/{slug}/Submit`) — submit an event on behalf of your camp (camp & workshop leads)
- **Bulk upload** (`/Events/Barrio/{slug}/BulkUpload`) — upload a CSV of camp events all at once
- **Moderation queue** (`/Events/Moderate`) — review pending submissions (moderators only)
- **Dashboard** (`/Events/Dashboard`) — submission and approval statistics (moderators only)
- **Export** (`/Events/Export`) — download a CSV of events for print production (moderators only)
- **Settings & categories** (`/Events/Admin`) — submission window, categories, and venues (moderators only)

## As a Volunteer

### Browse the programme

Go to `/Events` to see every approved event in the guide. Filter by category, venue, or camp to find what interests you.

### Make it your own

Hide categories you're not interested in, and your choices are saved to your profile and applied whenever you browse. Save individual events to your favourites to build a personal schedule.

### Submit an event

During the submission window, you can submit your own event. Give it a title (up to 80 characters), a description (up to 450 characters), a category, a start time, and a length between 15 minutes and 24 hours. You can optionally add a venue, a location note, and a host name. Once submitted, it waits for a moderator to approve it before it shows up in the public guide.

If your event is sent back for edits or turned down, you'll get an email. You can fix it and resubmit while the window is still open.

![TODO: screenshot — event submission form with title, description, category, start time, and length fields]

### Check your submissions

Your submitted events show their status: **Pending**, **Approved**, **Rejected**, **Resubmit requested**, or **Withdrawn**. You can withdraw a submission at any time.

### Submitting events for your camp (camp & workshop leads)

If you're a camp lead or workshop lead, you can submit and manage events for your camp at `/Events/Barrio/{slug}/Submit`. The same submission window and moderation steps apply, and your camp's events show up in your submissions list.

Got a lot to enter? Use the **bulk upload** at `/Events/Barrio/{slug}/BulkUpload` to add them all from a spreadsheet (CSV). Every row is checked before anything is saved — if one row has a mistake, nothing is saved, so you can fix it and try again.

## As a Board member / Admin (Events Admin)

The tasks below need the **Events Admin** or **Admin** role.

### Review the moderation queue

Go to `/Events/Moderate` to see everything waiting for review. You can **approve** an event, **reject** it, or **send it back** to the submitter for edits — each with an optional reason. Whatever you decide, the submitter is emailed automatically. The decision history is kept; nothing is overwritten. You can only act on events that are still pending, and you can't moderate an event you submitted yourself.

### Manage the programme

From `/Events/Admin`, set the submission window (open and close dates) and the guide publish date, create and reorder event categories, and manage the list of venues people can pick when submitting.

### Dashboard and export

`/Events/Dashboard` gives you the at-a-glance numbers. When it's time for print, download a CSV of approved events from `/Events/Export`.

## Related sections

- [Camps](Camps.md) — camp events are submitted under a camp; camp-lead authority is managed in Camps.
- [Shifts](Shifts.md) — the submission window is tied to the active event's settings.
