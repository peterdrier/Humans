<!-- freshness:triggers
  src/Sections/Humans.Holded.Contracts/IHoldedClient.cs
  src/Sections/Humans.Holded.Contracts/Holded*.cs
  src/Sections/Humans.Holded/Services/HoldedClient.cs
  src/Sections/Humans.Holded/Services/HoldedCallLog.cs
-->

# Holded — Connector Invariants

Thin typed-`HttpClient` surface to the **Holded API v2** (`https://api.holded.com/api/v2`,
Bearer auth, cursor pagination). This doc covers the *connector*. The **Holded vertical
section** it belongs to (ledger mirror, sync, `/Holded` admin screen) has its own doc:
[`Holded.md`](Holded.md).

## Concepts

- A **Purchase Document** in Holded is the org's incoming invoice/expense record. Expenses
  creates one per approved expense report, **booked to its 629 expense account at creation**
  (`items[].account`); tags are never written (dead v1 workaround).
- The **API key** is bound from the `HOLDED_API_KEY_V2` env var only — never `appsettings.json`,
  never logged. Jobs and pages no-op cleanly when it is unset (PR-preview / local dev).
- Errors are classified at the client boundary: `HoldedTransientException` (5xx, network,
  timeout, persistent 429) is retry-eligible; `HoldedPermanentException` (other 4xx, unreadable
  page bodies) is not.
- Every call is metered into the singleton `IHoldedCallLog` (in-memory queue) with its
  `X-RateLimit-*` headers; the Holded section drains it to `holded_api_calls`. The plan-tier
  budget (~2,000 calls/month) is the real allowance — `GET /usage`'s `limit` is Holded's
  billable-overage ceiling, displayed but never budgeted against.

## Invariants

- All HTTP calls go through one typed `HttpClient` (`HoldedClient`); Bearer auth via
  `Authorization` header.
- 429 with `Retry-After` is honored (wait capped at 60 s) and retried once for content-free
  requests; content-bearing requests surface it as transient immediately.
- Cursor pagination (`{items, cursor, has_more}`, `limit` ≤ 200) runs to completion or
  **throws** — a truncated list is never returned, because list results feed replace-semantics
  reconciliation where a short fetch would delete live rows.
- `ledger-entries` dates arrive as `DD/MM/YYYY` (parsed via `HoldedLedgerDatePattern` in
  `DateFormattingExtensions`); purchases/contacts dates are ISO. Decimals arrive as strings.
- Currency is EUR-only. Multi-currency is out of scope.
- There is **no tag/doc-update endpoint in v2**; recategorizing a pushed doc is done inside
  Holded (reclassify the line) and the ledger mirror picks the correction up.

## Cross-Section Dependencies

Inbound: Expenses (doc push via outbox), Finance (provisioning, contacts, doc sync), Holded
section (ledger/accounts/usage). Outbound: none — the connector owns no tables and no UI.

## Architecture

**Owning surface:** `IHoldedClient`, its DTOs, its typed exceptions and `HoldedClientOptions`
are public on `Humans.Holded.Contracts`; the impl `HoldedClient` and the `IHoldedCallLog`
singleton are `internal` in `Humans.Holded/Services/`. All of it is registered by this
section's `Section.cs` (G5 lane 4b-2f, nobodies-collective/Humans#866) — it used to sit in
Base behind `Humans.Web`'s `AddHoldedConnector`.

**Why the leaf and not a `Contracts/` folder:** two consumers are outside the section —
Expenses (`ExpenseReportService`, via `IHoldedClient.IsConfigured`) and Finance (`Service`).
A folder inside `Humans.Holded` would make those sections reach into a section-internal
folder and cycle.

**The jobs are not in Base.** The "Hangfire serializes the declaring type name" concern that
used to keep `HoldedSyncJob` and `HoldedExpenseOutboxJob` in Base turned out to be false —
`AddOrUpdate<T>(id, …)` is keyed on the job id, not the type — so both moved to their own
section's `Jobs/` folder at G5 (lane 5b-5): `HoldedSyncJob` here, a shim over this section's
own `IHoldedNightlySync`; `HoldedExpenseOutboxJob` in `Humans.Expenses/Jobs/`, a shim over
`IExpenseReportBackgroundProcessor`. Neither job references this section's connector types
directly — Shell's roll-call still names the concrete classes for Hangfire scheduling, and
each section registers its own job's DI.

**GDPR** — the connector owns no per-user data. Finance's own `Service` (exposed as
`IHoldedFinanceService`) is the `IUserDataContributor` that exports the member's
`holded_creditor_contacts` binding.
