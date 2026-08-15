# Contracts — the personal iCal feed, and nothing else

Everything in this folder is `public`; outside it only two types are — `Section` and
`CalendarResource`, the latter because the boot localization diagnostic discovers resource
markers via `GetExportedTypes()`. Everything else is `internal`.

The calendar *events* half of the section has no cross-section surface at all. Its whole
fan-in was Shell's `CalendarController` and view models, and both moved in with the section;
no other section and nothing in Base reads a calendar event (design §9 B4;
G5-SECTION-TEMPLATE.md step 5b). `ICalendarServiceRead` survives, but `internal` and inside
`Services/`: the split it draws is between the section's cached read path and its write path,
not between sections.

What this folder holds is the **personal iCal feed** (`ICalFeedService` and its contributor
fan-out), moved in from Base by G5 lane 4b-2c (nobodies-collective/Humans#866):

| Type | Who consumes it |
|---|---|
| `ICalendarFeedContributor` | implemented by `Humans.Shifts` (`ShiftSignupService`) and `Humans.Events` (`EventService`) |
| `CalendarFeedItem` | the item both contributors return; `Humans.Scanner` renders it on the ticket card |
| `IICalFeedService` | `Humans.Scanner`'s ticket card; the section's own `ICalFeedApiController` |
| `UserCalendarViewComponent` / `UserCalendarViewModel` | `<vc:user-calendar>` in Shell's widget gallery; `Component.InvokeAsync("UserCalendar", …)` in Users' admin detail |

**Folder, not a `.Contracts` leaf.** A leaf exists only where a cycle forces one, and a
contributor fan-out inverts the arrow: the implementers reference Calendar, Calendar
references none of them. Its only outbound section edge is `Humans.Users.Contracts`
(`IUserServiceRead`, for the stored `ICalToken` check), and no consumer of this folder lives
in Base — Shifts, Events, Scanner and Shell are all above it in the graph. The view component
needs the ASP.NET framework reference the section project already has, which a framework-free
leaf could not carry anyway.
