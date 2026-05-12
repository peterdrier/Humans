<!-- freshness:triggers
  src/Humans.Application/Interfaces/Holded/**
  src/Humans.Infrastructure/Services/Holded/**
  src/Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs
-->

# Holded — Section Invariants

Thin typed-`HttpClient` surface to the Holded accounting API. Owned narrowly: v1 ships only the four methods the Expenses section needs. The broader Finance/Holded reconciliation described in `Finance.md` is forward-looking and will extend this same surface without breaking consumers.

## Concepts

- A **Purchase Document** in Holded is the org's incoming invoice/expense record. Expenses creates one per approved expense report.
- The **API key** is bound from the `HOLDED_API_KEY` environment variable only — never `appsettings.json`. Never logged.
- Errors are classified at the client boundary: `HoldedTransientException` (5xx, network, timeout) is retry-eligible; `HoldedPermanentException` (4xx) is not.

## Data Model

None. Holded owns no Humans tables in v1.

## Routing

None. Holded has no UI in v1.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Other sections (Expenses) | Call `IHoldedClient` via DI. |
| Any human | None directly. |

## Invariants

- API key is read from `HOLDED_API_KEY` env var only and is never written to logs, audit entries, or error messages.
- All HTTP calls go through one typed `HttpClient` (`HoldedClient`). No raw `HttpClient.Send` elsewhere.
- Currency is EUR-only. Multi-currency is out of scope.
- 5xx and network failures throw `HoldedTransientException`. 4xx failures throw `HoldedPermanentException`. Consumers choose retry policy.

## Negative Access Rules

- The Holded section **does not** read or write any Humans table.
- The Holded section **does not** maintain its own background sync / pull job in v1. (`HoldedSyncJob`, `holded_transactions`, etc. described in `Finance.md` are future work.)

## Triggers

None. The client is pure on-demand.

## Cross-Section Dependencies

None outbound. Inbound: Expenses calls `IHoldedClient`. Future Finance work will extend.

## Architecture

**Owning section:** `Holded`
**Owning services:** `IHoldedClient` (impl `HoldedClient`)
**Owned tables:** none
**Status:** (A) New section.

- `IHoldedClient` lives in `Humans.Application/Interfaces/Holded/`.
- `HoldedClient` lives in `Humans.Infrastructure/Services/Holded/` and is the single typed `HttpClient` to Holded.
- Registered via `services.AddHoldedSection(config)` in `Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs`.
- `HoldedClientOptions.ApiKey` is bound from the `HOLDED_API_KEY` env var at startup.
- **GDPR** — no `IUserDataContributor`. Holded owns no per-user data.

### Future evolution

When the broader Finance/Holded sync described in `docs/sections/Finance.md` ships, it adds:
- a recurring pull job (`HoldedSyncJob`) that imports purchase docs into a `holded_transactions` table,
- additional client methods (list / search / update payments) on the same `IHoldedClient` surface,
- the unmatched-queue UI under `/Finance`.

The current four-method surface stays stable; new methods get added alongside.
