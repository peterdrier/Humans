# BoxOffice — Data Access

## BoxOffice

Project: `src/Sections/Humans.BoxOffice`. **No DbContext, no repository, no tables:** an adapter behind Tickets'
`ITicketingPlatform` port. `BoxOfficePlatform` (Scoped) keeps payment-lifecycle calls on its existing Stripe.NET
client and key. Ledger, fee and read-probe calls use `IStripeService` on the named `BoxOffice` account;
Tickets owns every row about the orders it charges. Invariants: `Docs/BoxOffice.md`.
