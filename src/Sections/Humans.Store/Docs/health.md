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
and the camp cannot start a second payment. When the money is confirmed the balance drops; when
the mandate is rejected the balance stays owed. If the payment processor's notification never
arrives, an admin can see exactly which payments the processor holds that the collective does
not, and pull them in.

When a camp is done ordering, the store admin issues the bill. That produces a real, legally
numbered Spanish sales document at the accountants' system, freezes the camp's prices at that
moment, and closes the order to further changes. What kind of document depends on how much is
known about the buyer: with a name, address and tax id it is a full factura; with less it is a
simplified one, and only up to the legal ceiling for those — above it, the details are required
rather than the document being quietly downgraded. Deposits are billed separately as refundable
liabilities, never as income.

An admin can see, for any year, what each counterparty owes, what quantity of each item was
ordered across everyone, and the two crossed against each other.

## 2. The shapes

The question-shapes across the section's whole external surface.

| # | Shape | Surface |
|---|---|---|
| 1 | *What can be ordered this year?* | `GET /Store`, `GET /Store/Admin/Catalog` |
| 2 | *Change what can be ordered* | `GET/POST /Store/Admin/Catalog/Edit[/{id}]`, `POST …/Save`, `POST …/Deactivate/{id}` |
| 3 | *Open or close a counterparty's order* | `POST /Store/Order/Create/{campSeasonId}`, `POST /Store/Team/{teamId}/Create`, `POST /Store/Order/{id}/Delete` |
| 4 | *What is on this order and what does it cost?* | `GET /Store/Order/{id}` |
| 5 | *Change what is on this order* | `POST /Store/Order/{id}/AddLine`, `…/RemoveLine`, `…/UpdateCounterparty` |
| 6 | *Pay it* | `POST /Store/Order/{id}/Pay`, `POST /Store/StripeWebhook` |
| 7 | *Did the money arrive?* | `GET /Store/Admin/Payments`, `POST /Store/Admin/Payments/RecordMissing` |
| 8 | *Bill it* | `POST /Store/Order/{id}/IssueInvoice` |
| 9 | *What was ordered in total?* | `GET /Store/Admin/Summary`, and the `/Admin` dashboard tile |

No shape here is reachable from another section: the whole surface is `internal`, and the tile
that renders shape 9 on `/Admin` is Store's own class, composed by the Shell.

## 3. Structure

Written from the shapes, not from today's layout.

- **One aggregate, one repository.** Order (with its lines and payments), Product, and Invoice
  are one section's data behind one `IStoreRepository` over one `StoreDbContext`. Nothing else
  reads `store_*`.
- **One service.** Every shape is a method on `Service`; the shapes are too entangled — pricing
  feeds the order page, issuance, the summary and deletion alike — to split without duplicating
  the pricing rule.
- **Pricing lives in exactly one pure function.** `BalanceCalculator.Compute(order, currentPrices)`
  answers "what does this order cost" for the order page, the summary, deletion's zero-balance
  gate and issuance's freeze. Any second place that adds a line up is a defect.
- **Authorization is resource-based and lives in one handler.** Every "may this actor do this to
  this order" question — including the per-product ordering deadline — is answered by
  `OrderAuthorizationHandler` against one of its resources (the order, a create context, a
  line context). No controller re-derives a rule; no service re-checks a role.
- **Controllers split by audience, not by verb.** `StoreController` is the counterparty's
  surface, `StoreAdminController` the admin's, `StoreStripeWebhookController` the payment
  processor's. All only translate.
- **Vendor systems are reached through their own sections' contracts** — `IStripeService`,
  `IHoldedClient` — never their internals.
- **No public contract seam.** Nothing outside the section reads Store, so nothing is public
  except `Section` and `StoreResource`. A read contract gets added the day a second section
  actually needs one — not in advance for the section's own admin tile.

## 4. Invariants

- An order has exactly one counterparty: `CampSeasonId` xor `TeamId`. Service-enforced.
- Team orders are non-billable: no payment, no counterparty, no invoice, at any privilege level.
- At most one order per camp season, and a camp season is itself one camp's one year — so the
  create guard tests for *any* existing order, never for one matching the season's year. A year
  comparison there passes a legacy `Year = 0` row and hands the season a second order.
- At most one team order per department per year; departments only (`ParentTeamId is null`).
- Camp orders run `Open → InvoiceIssued`, one way. Team orders stay `Open`.
- An `Open` order is priced at the live catalog; an `InvoiceIssued` order is priced at its
  frozen line snapshots. Issuance rewrites the snapshots from the live catalog first, so the
  document and the order agree forever after.
- Lines change only while `Open`, and only up to the product's `OrderableUntil` — the deadline is
  enforced in the authorization handler, and store admins are exempt from it.
- Only `Paid` money counts toward a balance. A captured-but-uncleared mandate (`Pending`) and a
  rejected one (`Failed`) do not, and a `Pending` payment blocks starting a second one.
- Payment ingestion is idempotent on the Stripe PaymentIntent id, whichever path records it —
  webhook, out-of-order settlement event, or admin reconciliation.
- Issuance is idempotent on both sides: locally on `IssuedInvoiceId`, and remotely by searching
  Holded for a document already tagged with the order before creating one. An adopted document
  whose totals no longer match the order refuses loudly rather than reconciling silently.
- Every issued document is approved, never left a draft. Every line books to its product's
  configured revenue account; a missing or unknown account refuses issuance by name. Deposits
  book tax-0 to the configured liability account, and a missing one refuses issuance.
- Spanish VAT applies regardless of the buyer's country; deposits carry no VAT.
- Every mutation writes an audit entry.

## 5. Seams — specified but not built

An unbuilt seam is carried in the docs and, where a shipped migration forces it, in the schema —
never in live code. A method that throws or an accessor nobody calls is not a seam, it is a lie
about what the section does.

- **Manual payment entry** (FinanceAdmin records a bank transfer or a refund as a negative
  amount). No code.
- **Treasury sync** (a recurring job matching Holded bank entries to orders). The
  `store_treasury_sync_state` table, its entity and its EF configuration ship because the
  migration shipped; no code reads or writes them. The matching key the job was designed around
  (`Order.Label`) was abolished.
- **A FinanceAdmin order ledger** at `/Store/Admin/Orders`. No route, no view.

## 6. Deliberately not done

- **No caching decorator.** Admin and camp-lead traffic only, a handful of concurrent users.
- **No second table for team orders.** One polymorphic `Order` row reuses the catalog, line,
  authorization and audit machinery rather than duplicating all of it for the non-billable case;
  the "exactly one counterparty" invariant pays for that in service code, not in a DB constraint.
- **No FK or navigation to `CampSeason` / `Team` / `User`.** Bare `Guid?` columns, resolved
  through the owning sections' read contracts.
- **No architecture test project.** Both of the deleted `StoreArchitectureTests`' assertions
  became vacuous when the section became one assembly; analyzers and the assembly boundary carry
  it now.
- **No B2B reverse-charge path.** Goods change hands in Spain, so place of supply is always
  Spain. `CounterpartyCountryCode` is recorded for the document and never consulted for tax.
- **No `nameof`-derived audit entity types.** They are persisted strings matched by equality;
  a rename must not change what is written or queried.

## Load-bearing weirdness

- **`PaymentStatus.Paid` is deliberately the enum's zero member.** It is what every pre-async row
  and every sync/manual insert means. The `store_payments.Status` column carried a matching
  default only for the `AddStorePaymentStatus` migration and no longer does; `Payment.Status`'s
  C# initializer is the only thing that settles an insert today. A future run should not reason
  about an EF insert sentinel here — there is no store default to be swallowed by.
- **Repricing reads one catalog year — the active event's — not each order's `Year`.** The org
  runs one event at a time, and this is also why legacy rows still at `Year = 0` reprice
  correctly. The admin summary is the exception: it prices a historical year against that year's
  own catalog.
- **Holded writes never take the request's cancellation token.** A torn write there has no local
  compensation.
- **The Holded document tag, not the note, is the recovery key** — Holded's list endpoints
  return tags and not notes.
- **`IStoreRepository` keeps its section prefix** where the rest of the internals drop theirs: it
  derives from `IRepository` and cannot be called that.

## Run history

| Run | Date | reforge surface score | PR |
|---|---|---|---|
| 1 | 2026-08-25 | 231 → 178 (loc=3529 → 3487) | peterdrier/Humans#1520 |
