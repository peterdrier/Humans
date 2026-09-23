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
| `ICalendarFeedContributor` | implemented by `Humans.Shifts` (`ShiftSignupService`), `Humans.Events` (`EventService`) and `Humans.Workgroups` (`WorkgroupCalendarContributor`) |
| `CalendarFeedItem` | the item every contributor returns; `Humans.Scanner` renders it on the ticket card |
| `IICalFeedService` | `Humans.Scanner`'s ticket card; the section's own `ICalFeedApiController` |
| `UserCalendarViewComponent` / `UserCalendarViewModel` | `<vc:user-calendar>` in Debug's widget gallery; the `IUserPart` seam (`SectionUserParts`) in Users' admin detail |

**Folder, not a `.Contracts` leaf.** A leaf exists only where a cycle forces one, and a
contributor fan-out inverts the arrow: the implementers reference Calendar, Calendar
references none of them. Its only outbound section edge is `Humans.Users.Contracts`
(`IUserServiceRead`, for the missing/merged-user guard on the feed), and no consumer of this folder lives
in Base — Shifts, Events, Workgroups, Scanner and Debug are all above it in the graph, and Users
renders the view component by name without a project reference at all. The view component also
derives from ASP.NET's `ViewComponent` and lives with the Razor views it renders, which belong to
the section project.
