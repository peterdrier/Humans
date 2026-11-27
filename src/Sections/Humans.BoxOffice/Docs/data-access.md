# BoxOffice — Data Access

## BoxOffice

Project: `src/Sections/Humans.BoxOffice`. **No DbContext, no repository, no tables:** an adapter behind Tickets'
`ITicketingPlatform` port. `BoxOfficePlatform` (Scoped) reads ledger, fees and connection checks through
`IStripeService` on the `Tickets` mirror account. Checkout sessions, refunds and disputes use Stripe.NET with the
shared `STRIPE_TICKETS_KEY`; Tickets owns every row about the orders it charges. Invariants: `Docs/BoxOffice.md`.
