# Calendar — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `CalendarController` | Class | `[Authorize]` (authenticated) | — |
| `ICalFeedApiController` | Action | `AllowAnonymous` | — (personal iCal feed at `/api/ical/{userId:guid}/{token:guid}.ics`; secret is the user's stored `ICalToken`; all failure modes return 404 — no oracle distinguishing unknown user from wrong token) |
