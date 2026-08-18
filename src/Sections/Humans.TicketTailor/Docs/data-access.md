# TicketTailor — Data Access

## TicketTailor

Project: `src/Sections/Humans.TicketTailor` — services under `Services/`. **No
DbContext, no repository, no tables:** the section is the TicketTailor vendor
port; Tickets owns every local row mirrored from it.

### TicketVendorService (Scoped)

No repository. `TicketTailorService` implements `ITicketVendorService` against
the TicketTailor v1 HTTP API over an injected `HttpClient`: order, issued-ticket
and check-in reads (`GetOrdersAsync`, `GetIssuedTicketsAsync`,
`GetCheckInsAsync`, `GetEventSummaryAsync`), discount-code grant and usage
(`GenerateDiscountCodesAsync`, `GetDiscountCodeUsageAsync`), and the write calls
behind gate check-in and ticket transfer (`CreateCheckInAsync`,
`VoidIssuedTicketAsync`, `IssueTicketAsync`). Event summaries are held in
`IMemoryCache` for 15 minutes; nothing else is cached and there is no DB access.
`StubTicketVendorService` is registered instead when no vendor credentials are
configured.

---
