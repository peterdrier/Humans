<!-- freshness:triggers
  src/Humans.Application/Services/Calendar/**
  src/Humans.Domain/Entities/CalendarEvent.cs
  src/Humans.Domain/Entities/CalendarEventException.cs
  src/Humans.Infrastructure/Data/Configurations/Calendar/**
  src/Humans.Web/Controllers/CalendarController.cs
-->
<!-- freshness:flag-on-change
  Calendar event/recurrence rules, soft-delete, audit-log triggers, and the open-edit model — review when Calendar service/entities/controller change.
-->

# Calendar

## What this section is for

The Calendar is a shared community calendar for all of the org's teams. It shows one-off events and repeating ones — team meetings, regular workshops, planning sessions, whatever a team wants to put on the radar.

Anyone signed in can browse the calendar, add events to any team, and edit or cancel existing ones. Every change is written to the audit log, so there's always a clear record of who did what. The calendar is deliberately open — keeping a record is the safeguard, not locking people out.

## Key pages at a glance

- **Month view** (`/Calendar`) — the main calendar grid for the current month; filter by team with `?teamId`
- **List view** (`/Calendar/List`) — the same month as a simple list
- **Agenda** (`/Calendar/Agenda`) — what's coming up, from today onwards (the next 60 days by default)
- **One team's calendar** (`/Calendar/Team/{teamId}`) — the month grid for a single team
- **Event detail** (`/Calendar/Event/{id}`) — full details, including the next few times a repeating event happens
- **Add an event** (`/Calendar/Event/Create`) — the new-event form
- **Edit an event** (`/Calendar/Event/{id}/Edit`) — change an event or a whole repeating series

## As a Volunteer

Anyone signed in can do everything on the calendar — view, add, edit, cancel, and manage repeating events. There are no role restrictions. Every change is recorded in the audit log.

### Browse what's on

Use the month grid at `/Calendar`, the list at `/Calendar/List`, or the upcoming agenda at `/Calendar/Agenda`. You can filter any of them by team, or look at one team's calendar at `/Calendar/Team/{teamId}`. Open any event for the full details.

![TODO: screenshot — month grid with a team filter and a mix of one-off and repeating events]

### Add an event

Go to `/Calendar/Event/Create`. Every event needs a title, a start time, and a team it belongs to. For timed events you also set an end time, and you can add a description, a location, and a link. For repeating events, set how often it repeats and the timezone (the default is Madrid time).

### Edit or cancel

Open an event and use edit or delete. Editing a repeating event changes every future occurrence. If you only want to change **one** occurrence of a repeating event — a different time or place that week — edit just that occurrence. To drop a single occurrence without affecting the rest, cancel that one. Deleted events disappear from the calendar but stay in the audit record.

## Related sections

- [Teams](Teams.md) — every calendar event belongs to a team.
- [Admin](Admin.md) — calendar changes show up in the audit log.
