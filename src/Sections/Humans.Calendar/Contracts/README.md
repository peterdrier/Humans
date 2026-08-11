# Contracts — deliberately empty

Everything in this folder is `public`; outside it only two types are — `Section` and
`CalendarResource`, the latter because the boot localization diagnostic discovers resource
markers via `GetExportedTypes()`. Everything else is `internal`. This folder holds a
section's cross-section surface only: `I<Section>ServiceRead`, canonical read DTOs, and
domain events.

Calendar has none. Its whole fan-in was Shell's `CalendarController` and view models, and
both moved in with the section; no other section and nothing in Base reads a calendar event
(design §9 B4; G5-SECTION-TEMPLATE.md step 5b).

`ICalendarServiceRead` survives, but `internal` and inside `Services/`: the split it draws
is between the section's cached read path and its write path, not between sections. It is
not an empty placeholder — it has four members and the section's own controller injects it —
so the "delete the empty `I<Section>ServiceRead`" rule does not apply.

The personal iCal feed does **not** run through here. `ICalendarFeedContributor` /
`IICalFeedService` are a separate Base-owned fan-out (`Humans.Application.Interfaces.ICalFeed`)
that Shifts and Events implement and Calendar does not; the naming collision is the only
thing the two have in common.
