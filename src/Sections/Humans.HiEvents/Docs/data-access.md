# HiEvents — Data Access

## HiEvents

Project: `src/Sections/Humans.HiEvents` — services under `Services/`. **No DbContext, no repository, no tables:** the
section is the Hi.Events adapter behind Tickets' `ITicketingPlatform` port; Tickets owns every order and ticket.
Invariants: `Docs/HiEvents.md`.

### HiEventsPlatform (typed HttpClient, transient; resolved as a scoped `ITicketingPlatform`)

No repository. Implements `ITicketingPlatform` against the Nobodies fork of the Hi.Events API.

### HiEventsWebhookController

No repository. Verifies the webhook signature and calls `ITicketingPlatformEvents`.

---
