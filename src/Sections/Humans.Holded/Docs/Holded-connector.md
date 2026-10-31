<!-- freshness:triggers
  src/Sections/Humans.Holded/Contracts/IHoldedClient.cs
  src/Sections/Humans.Holded/Contracts/Holded*.cs
  src/Sections/Humans.Holded/Services/Holded*Client.cs
  src/Sections/Humans.Holded/Services/HoldedTransport.cs
  src/Sections/Humans.Holded/Services/HoldedJson.cs
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
  (`items[].account`); tags are never written (dead v1 workaround). Only the single GET
  (`GetPurchaseDocumentAsync`) returns `notes` (null when absent or blank) — the v2 list never
  does (verified live 2026-10-21) — which is how Finance confirms a document the push wrote
  twice before deleting it; `DeletePurchaseDocumentAsync` and `DeleteContactAsync` are the
  two deletes, used only by Finance: both by its duplicate removal, and the contact delete also
  to retract a creditor contact minted by a run that lost the member's binding race.
- A **Sales Document** is the org's outgoing revenue record, in two kinds behind one
  `HoldedSalesDocumentKind`: a full `invoice` (identified contact required) and a
  `sales-receipt` (*factura simplificada*, no contact). They share one payload shape and one
  pipeline — create → **approve** → read back — because a Holded draft books no revenue and
  carries no document number. Store creates them per camp order, with `items[].account` per
  line. `items[].account` is the chart account's **id**, not its 8-digit number.
- The **API key** is bound from the `HOLDED_API_KEY_V2` env var only — never `appsettings.json`,
  never logged. Jobs and pages no-op cleanly when it is unset (PR-preview / local dev).
- Errors are classified at the client boundary: `HoldedTransientException` (5xx, network,
  timeout, persistent 429) is retry-eligible; `HoldedPermanentException` (other 4xx, unreadable
  page bodies and malformed creation responses) is not. One carve-out: `ApprovePurchaseDocumentAsync` treats Holded's 400 "Document
  already approved" as success (logged at Warning), so a retried push does not write itself off.
- Every call is metered into the singleton `IHoldedCallLog` (in-memory queue) with its
  `X-RateLimit-*` headers; the Holded section drains it to `holded_api_calls`. The plan-tier
  budget (~2,000 calls/month) is the real allowance — `GET /usage`'s `limit` is Holded's
  billable-overage ceiling, not the plan budget; the usage read exposes only its period and call count.

## Invariants

- Contact creation accepts no custom user id: Holded v2 cannot write it. Finance keeps the user-to-contact binding locally.
- Connector reads omit unused sales status, bank origin, per-line purchase amounts and secondary usage metadata; consumers use document totals, the draft flag and bank reconciliation status.
- The single purchase-document read carries only approval time, notes and payment details. Its callers already know the requested id; totals and tags belong to the list read. Unused detail fields are ignored rather than parsed.

- `HoldedClient` remains the typed-client entry point. Its internal operation clients share one
  `HoldedTransport`, which owns every HTTP send, Bearer authorization, retry, cursor page walk,
  call-meter record and timed-operation log. The metered endpoint names remain the public operation names.
- 429 with `Retry-After` is honored (wait capped at 60 s) and retried once for content-free
  GETs. HTTP-date waits retain their fractional remaining seconds and round up to the delay's millisecond
  precision, so a future retry date is never rounded down to an immediate retry; past dates still retry immediately. Every write, including a bodyless sales-approval POST,
  surfaces it as transient immediately.
- Cursor pagination (`{items, cursor, has_more}`, `limit` ≤ 200) runs to completion or
  **throws** — a truncated list is never returned, because list results feed replace-semantics
  reconciliation where a short fetch would delete live rows.
  Repeated cursors fail immediately instead of refetching pages until the endpoint's cap.
  A page with no `items` array, or with a null item, fails the read too: dropping either would
  hand reconciliation an incomplete or empty list.
- An accepted create (purchase, contact, sales document, expense account) must answer with a
  nonblank id. A null body, a missing or blank id, or an unreadable success body fails as
  `HoldedPermanentException`, so a caller records failure instead of repeating an accepted
  create or persisting an unusable reference.
- An accepted payment or ledger posting never throws on an unreadable id: it returns an
  `unconfirmed:` reference instead, because the money already moved. A payment's id is the one
  new `payments_detail` entry matching amount, date and treasury on a re-read of the document
  (the POST answers `{"status":"paid"}` alone); an entry's id comes from its own response body.
  Callers must not repeat a posting because its reference is unconfirmed.
- Expense-account `account_num` and `id`, and chart account `id`, are required. Missing numbers or missing/null/blank IDs fail the complete read with `HoldedPermanentException` before callers can provision or invoice from an incomplete account map.
- Account numbers, supplier account numbers, ledger entry numbers and ledger line numbers must be integral. Numeric forms such as `40000001.0` are accepted; fractions are rejected instead of truncated. Contact-list parsing retains its existing skip-and-log behavior for unreadable contacts.
- Purchase-document IDs must be nonblank; a missing/blank ID fails the complete read with `HoldedPermanentException`, so Finance never syncs an empty document identity. Approved (`draft: false`) purchase documents require a non-null `payments_pending`; a missing balance fails the complete read with `HoldedPermanentException`. Otherwise Finance could omit an unpaid invoice and post a journal remainder instead. Drafts and unknown approval states may omit the balance and read as 0; neither is bookable.
- Sales-document totals are required: a missing or null `total` fails as a permanent connector error before Store can finish issuance or compare a recovered invoice. Explicit zero remains valid.
- Contact and sales-document detail IDs must be nonblank when returned; an absent/null ID retains the requested-ID fallback. Contact lists skip and log unidentified rows while retaining valid contacts. Tagged sales-document searches reject unidentified matches, including whitespace IDs, rather than returning an incomplete recovery result.
- Accounting-account debit, credit and balance are required decimal strings. Missing or null totals reject the complete page with `HoldedPermanentException`; they never become fabricated zero balances.
- A `ledger-entries` item is exactly `id, entry_number, line, date, type, description,
  doc_description, account, debit, credit, tags[], checked`. `id` is the journal entry's id
  (shared by all its lines) and is read as `EntryId`; `checked` (Holded's manual ledger tick)
  as `Checked`, false when absent. `doc_description` and `tags` are not read.
- `ledger-entries` dates arrive as `DD/MM/YYYY` (parsed via `HoldedLedgerDatePattern` in
  `DateFormattingExtensions`); purchases/contacts dates are ISO. Decimals arrive as strings.
- The live `ledger-entries` API's `end_date` is **exclusive** — an entry dated `end_date` itself
  is never returned. `ListLedgerEntriesAsync` requests `end_date = to + 1 day` so its own `to`
  parameter stays inclusive for callers; without this, same-day entries never reach the ledger
  mirror even though account balances already include them.
- Bank movements (`GET /treasury/accounts/{id}/bank-movements`, read live, never mirrored) carry
  no `date` key: a line's date is `booking_date` (what Holded stamps on the payment leg and ledger
  entry at reconciliation), falling back to `value_date`. Both are ISO 8601 with an offset and the
  date part is taken as given, never shifted to Europe/Madrid. A line with neither date is skipped
  and logged, except a **pending outgoing** one: it could be a SEPA rival, so it fails the read with
  a `HoldedPermanentException` whose `BankMovementId` names it, unless the caller passes that id in
  `skipUnreadableDateIds` (Finance does so after three stuck sweep runs, peterdrier/Humans#1946).
- Currency is EUR-only. Purchase-list and sales-document reads reject explicit non-EUR or blank currency with `HoldedPermanentException`; no foreign amount becomes a euro budget actual, payment or invoice comparison. Missing/null currency retains the existing EUR fallback; reported EUR is case-insensitive. Unsupported purchases fail the complete list rather than being filtered out. Multi-currency conversion is out of scope.
- Line taxes are Holded **tax keys**, scoped by direction: `s_iva_21` / `s_iva_0` on sales
  documents, `p_iva_21` on purchases. The decimal separator is dropped, not rounded —
  7.5% is `s_iva_75`.
- Post-issuance corrections are a *factura rectificativa* in Holded, never a mutation of the
  issued document — the same reason there is no doc-update endpoint below.
- Contacts are **create-only**: `CreateContactAsync` POSTs and never PUTs. v2's contact PUT is a
  full replacement (every omitted field, `supplier_record` included, resets), so a partial update
  mints the contact a second creditor account ([`holded-contact-put-is-full-replacement`](../../../../memory/code/holded-contact-put-is-full-replacement.md)).
- There is **no tag/doc-update endpoint in v2**; recategorizing a pushed doc is done inside
  Holded (reclassify the line) and the ledger mirror picks the correction up.
- The two deletes are `DELETE /api/v2/purchases/{id}` and `DELETE /api/v2/contacts/{id}`. A 404
  on the contact delete (already gone) is success, logged at Error; a 404 on the purchase delete
  throws like any other 4xx. Holded refuses to delete a purchase document that carries a payment.
  The routes are not documented in this repo; unverified against Holded's published reference.

## Cross-Section Dependencies

Inbound: the consumers listed under Architecture, plus this section's own mirror
(ledger, accounts, usage). Outbound: none — the connector owns no tables and no UI.

## Architecture

**Owning surface:** `IHoldedClient`, its DTOs, its typed exceptions and `HoldedClientOptions`
are public in `Humans.Holded/Contracts/` (namespace `Humans.Holded.Contracts`); the impl `HoldedClient` and the `IHoldedCallLog`
singleton are `internal` in `Humans.Holded/Services/`. All of it is registered by this
section's `Section.cs`.

`HoldedClient` composes internal purchases, sales, contacts, accounting and treasury clients over
one `HoldedTransport`. Operation clients own their wire payloads, parsing and endpoint-specific
failure rules; `HoldedJson` holds the shared wire-value readers and null-omitting serializer
options. They add no interfaces or DI registrations, and the public contract stays unchanged.

**Consumers outside the section** (each to move onto `IHoldedService` / `IHoldedWrite`, ledger debt
`CENTRAL-105`; `IHoldedClient` goes internal when none remain): Expenses (`ExpenseHoldedPublisher` and
`ExpenseReportBackgroundProcessor` for the push), Finance (`Service`, `HoldedDocumentSync`, `SepaBankBooking`) and Store
(`Service`, `StoreInvoiceIssuer`) reach it through the public `Contracts/` folder.

**The jobs live with their sections.** `HoldedSyncJob` is in `Humans.Holded/Jobs/`, a shim over
this section's own `IHoldedNightlySync`; `HoldedExpenseOutboxJob` is in `Humans.Expenses/Jobs/`,
a shim over `IExpenseReportBackgroundProcessor`. A job is free to sit in its own section because
`AddOrUpdate<T>(id, …)` is keyed on the job id, not on the declaring type. Neither job references
this section's connector types directly — each section names its own concrete job class in its
`ISectionJobs` and registers that job's DI, and Shell schedules whatever the sections yield.

**GDPR** — the connector owns no per-user data. Finance's own `Service` (exposed as
`IHoldedFinanceService` / `IHoldedFinanceWrite`) is the `IUserDataContributor` that exports the member's
`holded_creditor_contacts` binding.
