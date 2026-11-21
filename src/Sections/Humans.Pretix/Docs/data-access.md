# Pretix — Data Access

Project: `src/Sections/Humans.Pretix`. **No DbContext, no repository, no tables:** the section is
the pretix adapter behind Tickets' `ITicketCheckoutBackend` port. Invariants: `Docs/Pretix.md`.

### PretixCheckoutBackend (typed HttpClient, Scoped)

No repository. Implements the port against the pretix REST API v1. Cache-free.
