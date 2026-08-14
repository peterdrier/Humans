# `Humans.TicketTailor.Contracts` — deliberately empty

This section publishes nothing. It is an *adapter*: one implementation of
`Humans.Tickets.Contracts.ITicketVendorService`, the vendor-agnostic port owned by
`Humans.Tickets` — this project takes a direct `ProjectReference` on that section to name it
(nobodies-collective/Humans#866, G5 lane 4b-2g; it used to live in Base).

Nothing outside this project may name `TicketTailorService` or `StubTicketVendorService` —
both are `internal sealed`, and the only thing that binds them is `Section.Register`.

Consumers of ticketing talk to **`Humans.Tickets`**, through `Humans.Tickets.Contracts`.
Tickets is the application's only door to ticketing; it is the one section that injects the
port, alongside Shell's `TicketVendorHealthCheck`, which probes the connector deliberately.
`TicketVendorPortArchitectureTests` pins that pair.

When the 2027 vendor lands, this project is deleted and `Humans.<NewVendor>` is added.
Nothing in `Humans.Tickets` or in any consumer changes — which is the whole
reason the connector is a section of its own rather than a folder inside Tickets.
