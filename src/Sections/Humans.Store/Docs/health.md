# Store — target shape

Derived fresh each section-doctor run, before any scan. History at the bottom.

## 1. What the section does

Camps and departments order physical goods from the collective ahead of the event, and the
collective bills the camps for them.

A store admin publishes a list of things that can be ordered for this year's event, each with a
price, a VAT rate, an optional refundable deposit, and a date after which it can no longer be
ordered. A camp lead opens an order for their camp and adds quantities to it; the order behaves
like a running tab — while it is open its total tracks whatever the current published price is,
so a price correction reaches every open order without anyone re-entering anything. A department
coordinator does the same for their department, except a department's order is never billed: it
exists so suppliers can see total demand.

A camp lead pays their balance by card or bank mandate. Card money is confirmed immediately;
mandate money is captured but not yet confirmed, and until it clears it does not count as paid
and the camp cannot start a second payment. If the payment processor's notification never
arrives, an admin can see exactly which payments the processor holds that the collective does
not, and pull them in. Money that moves outside the processor's checkout is entered by hand on
the order: a store admin credits returned deposits, a finance admin books a refund sent back by
hand, and only a full admin may erase a payment row entered in error.

When a camp is done ordering and its balance is settled, the store admin issues the bill. That
produces a real, legally numbered Spanish sales document at the accountants' system, freezes the
camp's prices at that moment, and closes the order to further changes. With a name, address and
tax id on file it is a full factura; with less it is a simplified one, and only up to the legal
ceiling for those. Deposits are billed separately as refundable liabilities, never as income.

An admin can see, for any year, what each counterparty owes, what quantity of each item was
ordered across everyone, and the two crossed against each other. The finance side can pull the
same year's order lines and settled payments as machine-readable rows, priced exactly as the
admin sees them, to split the payment processor's lump payouts by item in the books. Two
one-off repair screens bring old rows up to the current shape: orders from before orders carried
their own year, and payments from before the payment method was stored as text.

## 2. The shapes

| # | Shape | Surface |
|---|---|---|
| 1 | *What can be ordered this year?* | `GET /Store`, `GET /Store/Admin/Catalog` |
| 2 | *Change what can be ordered* | `GET /Store/Admin/Catalog/Edit[/{id}]`, `POST /Store/Admin/Catalog/Save`, `POST /Store/Admin/Catalog/Deactivate/{id}` |
| 3 | *Open or close a counterparty's order* | `POST /Store/Order/Create/{campSeasonId}`, `POST /Store/Team/{teamId}/Create`, `POST /Store/Order/{id}/Delete` |
| 4 | *What is on this order and what does it cost?* | `GET /Store/Order/{id}` |
| 5 | *Change what is on this order* | `POST /Store/Order/{id}/AddLine`, `…/RemoveLine`, `…/UpdateCounterparty` |
| 6 | *Pay it* | `POST /Store/Order/{id}/Pay`, `POST /Store/StripeWebhook` |
| 7 | *Did the money arrive?* | `GET /Store/Admin/Payments`, `POST /Store/Admin/Payments/RecordMissing` |
| 8 | *Correct the money by hand* | `POST /Store/Order/{id}/RecordPayment`, `POST /Store/Order/{id}/Payment/{paymentId}/Delete` |
| 9 | *Bill it* | `POST /Store/Order/{id}/IssueInvoice` · `StoreInvoiceIssuer` |
| 10 | *What was ordered and paid in a year?* | `GET /Store/Admin/Summary`, the `/Admin` dashboard tile, and `IStoreAccountingRead` (served by Backdoor at `/api/backdoor/store/order-lines` and `/api/backdoor/store/payments`) |
| 11 | *Bring legacy rows up to shape* | `GET /Store/Admin/OrderYears`, `POST /Store/Admin/OrderYears/Repair`, `GET /Store/Admin/PaymentMethods`, `POST /Store/Admin/PaymentMethods/Repair` |

Shape 10 is the only one another section reaches: Backdoor reads it through
`IStoreAccountingRead`. Shape 11 is two one-off data repairs that retire themselves — once no row
is left to repair the page says so and nothing else in the section depends on it.

## 3. Structure

- **One aggregate, one repository.** Order (with its lines and payments), Product, and Invoice
  are one section's data behind one `IStoreRepository` over one `StoreDbContext`. Nothing else
  reads `store_*`.
- **One service, two internal helpers.** Every shape is a method on `Service`; pricing feeds the
  order page, issuance, deletion, the summary and the accounting export alike, so splitting would
  duplicate the pricing rule. Issuance's Holded pipeline lives in `StoreInvoiceIssuer` and the
  live-price and counterparty-name reads both of them need in `StoreOrderReader`; neither is
  registered in DI — `Service` constructs them. The one read another section needs is exposed by
  `Service` implementing `IStoreAccountingRead`, registered as a forward to the same scoped
  instance.
- **Pricing lives in exactly one pure function.** `BalanceCalculator.Compute(order, currentPrices)`
  answers "what does this order cost" everywhere. Any second place that adds a line up is a
  defect. The year-wide views (summary, accounting export) share one year-loading step so their
  totals cannot drift apart.
- **Authorization is resource-based and lives in one handler.** Every "may this actor do this to
  this order" question — including the per-product ordering deadline and the narrower refund and
  payment-delete grants — is answered by `OrderAuthorizationHandler`. No controller re-derives a
  rule; no service re-checks a role.
- **Controllers split by audience.** `StoreController` is the counterparty's surface (and hosts
  the per-order admin money actions, gated per order), `StoreAdminController` the admin's,
  `StoreStripeWebhookController` the payment processor's.
- **Vendor systems through their own sections' contracts** — `IStripeService`, `IHoldedClient`.
- **Public surface is the accounting read and nothing else** — `IStoreAccountingRead`, its two
  row DTOs and the two enums they carry, plus `Section` and `StoreResource`. The admin summary's
  DTO graph stays `internal`.

## 4. Invariants

- An order has exactly one counterparty: `CampSeasonId` xor `TeamId`, set only at create —
  `Services/Service.cs:444` (camp) and `Services/Service.cs:509` (team).
- Team orders are non-billable at every privilege level: the service refuses counterparty edits,
  checkout, payment recording and issuance (`Services/Service.cs:612`, `Services/Service.cs:663`,
  `Services/Service.cs:720`, `Services/Service.cs:774`, `Services/Service.cs:1138`), and the
  handler never grants a billing operation on one, even to a store admin
  (`Authorization/OrderAuthorizationHandler.cs:63`).
- At most one order per camp season, tested for *any* existing order, never for one matching the
  season's year — `Services/Service.cs:437`.
- At most one team order per department per year, departments only —
  `Services/Service.cs:495` and `Services/Service.cs:501`.
- Camp orders run `Open → InvoiceIssued`, one way — the only writer of the state is
  `StoreInvoiceIssuer.CommitInvoiceAsync`, behind the open-only guard at
  `Services/StoreInvoiceIssuer.cs:55`.
- An `Open` order is priced at the live catalog, an `InvoiceIssued` one at its frozen line
  snapshots — `Services/BalanceCalculator.cs:57`. Issuance rewrites the snapshots from the live
  price first — `Services/StoreInvoiceIssuer.cs:75`.
- Lines change only while `Open` (`Services/Service.cs:538`, `Services/Service.cs:590`), and for
  everyone but a store admin only up to the product's `OrderableUntil`
  (`Authorization/OrderAuthorizationHandler.cs:212`).
- Money records outlive their order: an order holding any payment row cannot be deleted —
  `Services/Service.cs:472`.
- Only `Paid` money counts toward a balance — `Services/BalanceCalculator.cs:84`; a `Pending`
  payment blocks starting another — `Services/Service.cs:674`.
- Payment ingestion is idempotent on the Stripe PaymentIntent id whichever path records it —
  `Services/Service.cs:715`, backed by the filtered unique index at
  `Data/Configurations/PaymentConfiguration.cs:25`.
- Deposit returns never add up to more than the order's deposits —
  `Services/Service.cs:791`; a refund is stored negative and needs a reference —
  `Services/Service.cs:769`.
- Issuance is idempotent locally and remotely: the open-only guard runs before any Holded call
  (`Services/StoreInvoiceIssuer.cs:55`), Holded is searched by tag first
  (`Services/StoreInvoiceIssuer.cs:83`), and adoption is refused when totals differ
  (`Services/StoreInvoiceIssuer.cs:89`).
- A new document is issued only on a zero balance and is approved before anything is written
  locally — `Services/StoreInvoiceIssuer.cs:113` and `Services/StoreInvoiceIssuer.cs:170`. Every
  line's revenue account and the deposit liability account must exist in Holded's chart —
  `Services/StoreInvoiceIssuer.cs:300` and `Services/StoreInvoiceIssuer.cs:332`.
- A counterparty-less order above the simplified-invoice ceiling is refused —
  `Services/StoreInvoiceIssuer.cs:133`.
- Deposits carry no VAT — `Services/StoreInvoiceIssuer.cs:358`.
- The accounting export selects orders by their persisted year, never through the counterparty,
  so an order whose camp or team has since gone still exports — `Data/Repository.cs:155`.
- A legacy order's year is only ever written from its own camp season, and each write is audited
  — `Services/Service.cs:408`.

## 5. Seams — specified but not built

An unbuilt seam is carried in the docs and, where a shipped migration forces it, in the schema —
never in live code.

- **Bank-transfer / cash entry** (FinanceAdmin records money received outside Stripe). The
  `BankTransfer` and `Manual` enum members exist; nothing writes them — the hand-entry path
  accepts only deposit returns and refunds.
- **Treasury sync** (a recurring job matching Holded bank entries to orders). Its
  `store_treasury_sync_state` cursor table was dropped unused (peterdrier/Humans#1924); the
  matching key it was designed around (`Order.Label`) was abolished and Finance now owns the
  bank-movement feed.
- **A FinanceAdmin order ledger** at `/Store/Admin/Orders`. No route, no view.
- **Payment method read from the string column.** `Payment.MethodName` is written on every insert
  and backfilled by the repair screen; every read still goes through the int `Method`. The
  cut-over and the int column's removal are a later schema change.

## 6. Deliberately not done

- **No caching decorator.** Admin and camp-lead traffic only, a handful of concurrent users.
- **No second table for team orders.** One polymorphic `Order` row reuses the catalog, line,
  authorization and audit machinery; "exactly one counterparty" is paid for in service code, not
  a DB constraint.
- **No FK or navigation to `CampSeason` / `Team` / `User`.** Bare `Guid?` columns, resolved
  through the owning sections' read contracts.
- **No architecture test project.** Analyzers and the assembly boundary carry it.
- **No B2B reverse-charge path.** Goods change hands in Spain; `CounterpartyCountryCode` is
  recorded for the document and never consulted for tax.
- **No `nameof`-derived audit entity types.** They are persisted strings matched by equality.
- **No read-time year repair.** `GetOrderAsync` loads an order before its caller authorizes
  access, so repairing there would write on behalf of someone not yet allowed to see the order;
  only the admin repair and an authorized add-line write the year.
- **No localization of the admin pages or admin-only flash messages.** Catalog, summary,
  payments and repair pages are store-admin-only (`memory/code/localization-admin-exempt.md`).

## Load-bearing weirdness

- **`PaymentStatus.Paid` is deliberately the enum's zero member.** It is what every pre-async row
  and every sync insert means; `Payment.Status`'s C# initializer settles an insert, and the column
  carries no store default.
- **The order page reprices against one catalog year — the active event's — not each order's
  `Year`.** The org runs one event at a time, which is also why legacy rows still at `Year = 0`
  reprice correctly there. The year-wide views (summary, accounting export) are the exception:
  they price a year against that year's own catalog.
- **The summary and the accounting export select a year's orders differently, on purpose.** The
  summary walks the year's live camp seasons and departments — it is a picture of who is ordering;
  the export walks the persisted year — it is the books, and must not lose a row whose
  counterparty has gone. Per order their prices agree.
- **Holded and Stripe writes never take the request's cancellation token.** A torn write there
  has no local compensation.
- **The Holded document tag, not the note, is the recovery key** — Holded's list endpoints return
  tags and not notes.
- **Recovery runs before the balance gate.** A document Holded approved for an attempt whose local
  save failed is adopted even if payments moved since; only a draft still needs a zero balance.
- **`Payment` carries the method twice** (`Method` int, `MethodName` string) during the
  int-to-string move; see the seam above.
- **`IStoreRepository` keeps its section prefix** where the rest of the internals drop theirs: it
  derives from `IRepository` and cannot be called that.

## Run history

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-25 | first doctor run | peterdrier/Humans#1520 |
| 2 | 2026-09-27 | admin pages take the catalog year from the service; docs stop describing unbuilt payment paths as live | peterdrier/Humans#1829 |
| 3 | 2026-10-21 | docs and comments tell the true authorization story; the order page decides its money grants in one place | peterdrier/Humans#2057 |
