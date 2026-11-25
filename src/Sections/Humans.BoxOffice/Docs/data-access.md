# BoxOffice — Data Access

## BoxOffice

Project: `src/Sections/Humans.BoxOffice`. **No DbContext, no repository, no tables:** an adapter behind Tickets'
`ITicketingPlatform` port. `BoxOfficePlatform` (Scoped) calls Stripe's API through Stripe.NET with its own
key; Tickets owns every row about the orders it charges. Invariants: `Docs/BoxOffice.md`.
