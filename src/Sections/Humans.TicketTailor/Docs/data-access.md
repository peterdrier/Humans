# TicketTailor — Data Access

## TicketTailor

Project: `src/Sections/Humans.TicketTailor` — services under `Services/`. **No
DbContext, no repository, no tables:** the section is the adapter behind Tickets'
`ITicketVendorService` port; Tickets owns every local row mirrored from the vendor.
Invariants: `Docs/TicketTailor.md`.

### TicketTailorService (typed HttpClient, Production only)

No repository. Implements `ITicketVendorService` against the Ticket Tailor v1 HTTP
API over an injected `HttpClient`. Event summaries are held in `IMemoryCache` for
15 minutes; nothing else is cached and there is no DB access.

### StubTicketVendorService (Scoped, every other environment)

No repository. A deterministic in-memory event. The environment name decides which
implementation is bound (`Section.cs`), never the presence of a key.

---
