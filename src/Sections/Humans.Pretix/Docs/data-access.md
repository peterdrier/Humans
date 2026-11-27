# Pretix — Data Access

Project: `src/Sections/Humans.Pretix`. **No DbContext, no repository, no tables:** the section is
the pretix adapter behind Tickets' `ITicketingPlatform` port. Invariants: `Docs/Pretix.md`.

### PretixPlatform (typed HttpClient, Transient; registered Scoped as `ITicketingPlatform`)

No repository. Implements the port against the pretix REST API v1. Cache-free. Monetary order operations first read the parent event and require explicit EUR; they never infer currency from a stored order or a previous call.
