<!-- freshness:triggers
  src/Sections/Humans.Calendar/Controllers/**
-->
<!-- freshness:flag-on-change
  The section's authorization posture: [Authorize] on CalendarController and the one [AllowAnonymous] feed action.
-->

# Calendar — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CalendarController` | Class | `[Authorize]` (authenticated) | — |
| `ICalFeedApiController` | Action | `AllowAnonymous` | — (personal iCal feed at `/api/ical/{userId:guid}/{token:guid}.ics`; secret is the user's stored `ICalToken`; all failure modes return 404 — no oracle distinguishing unknown user from wrong token) |
