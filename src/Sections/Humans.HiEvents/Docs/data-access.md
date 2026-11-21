# HiEvents — Data Access

## HiEvents

Project: `src/Sections/Humans.HiEvents` — services under `Services/`. **No DbContext, no repository, no tables:** the
section is the Hi.Events adapter behind Tickets' `ITicketCheckoutBackend` port; Tickets owns every order and ticket.
Invariants: `Docs/HiEvents.md`.

### HiEventsCheckoutBackend (typed HttpClient, Scoped)

No repository. Implements `ITicketCheckoutBackend` against the Nobodies fork of the Hi.Events API.

### HiEventsWebhookController

No repository. Verifies the webhook signature and calls `ITicketCheckoutEvents`.

---
