# TicketTailor — Data Access

## TicketTailor

Project: `src/Sections/Humans.TicketTailor` — services under `Services/`. **No
DbContext, no repository, no tables:** the section is the adapter behind Tickets'
`ITicketVendorService` port; Tickets owns every local row mirrored from the vendor.
Invariants: `Docs/TicketTailor.md`.

### TicketTailorService (typed HttpClient, Production only)

No repository. Implements `ITicketVendorService` against the Ticket Tailor v1 HTTP
API over an injected `HttpClient`. Cache-free — Tickets' `CachingTicketVendorService`
holds event summaries in front of it; there is no DB access here.

### StubTicketVendorService (Scoped, every other environment)

No repository. A deterministic in-memory event. The environment name decides which
implementation is bound (`Section.cs`), never the presence of a key.

---
