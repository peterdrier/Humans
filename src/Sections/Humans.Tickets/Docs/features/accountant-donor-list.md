<!-- freshness:triggers
  src/Sections/Humans.Tickets/Controllers/TicketController.cs
  src/Sections/Humans.Tickets/Services/TicketQueryService.cs
  src/Sections/Humans.Tickets.Contracts/TicketConstants.cs
-->
<!-- freshness:flag-on-change
  The disclosure contract: who can download, what columns leave the system, the donation split
  rule, and the audit entry. Review when the export's policy, columns, VIP threshold, or audit
  action change.
-->

# Accountant Donor List

## Business Context

The association's accountant (Acountax) needs, once per event year, a list of the people who
donated through ticketing, so donations can be declared separately from taxable ticket income.
Two things count as a donation (rule agreed with the accountant, see `TicketConstants`):

- **VIP ticket**: the part of a seat's price above `VipThresholdEuros` (315). The first 315 is a
  10%-VAT ticket; the rest is a VAT-free donation.
- **Separate donation**: a standalone donation line added at checkout (`TicketOrder.DonationAmount`).

Neither Ticket Tailor nor Stripe can produce this: the vendor taxes the whole VIP price, and a
Stripe charge amount cannot tell two 315 tickets from one ticket plus a 315 donation. Only Humans
holds buyer identity together with the per-seat prices.

This is a **bulk export of personal data leaving the system** and is governed by
[`memory/code/data-exports-are-audited.md`](../../../../../memory/code/data-exports-are-audited.md):
the columns, the role, and the existence of the endpoint were Peter's decisions (2026-09-30), not
an implementation default. Requested via the finance repo; implemented in peterdrier/Humans#1872.

## User Stories

**As an Admin**, I can download the donor list for the accountant from `/Tickets/SalesAggregates`
("Ticketing Donations" button) so the totals tie to the monthly accountant report on the same page.

Acceptance criteria:

- `GET /Tickets/Export/Donations` returns `ticketing-donations.csv` with columns
  `Date, Order ID, Name, Email, Type, Amount` and a final `Total` row.
- One row per donation, not per order: an order with both a VIP premium and a separate donation
  yields two rows. Orders without a donation are absent.
- Only `Paid` orders. Refunded and pending orders never appear.
- `Type` is `VIP ticket` or `Separate donation`. The VIP amount is
  Σ (price − 315) over the order's `Valid`/`CheckedIn` seats priced above 315 — the same seat set
  and rule the monthly accountant report uses, so the CSV total equals that report's donations total.
- `Date` is the purchase date in **Europe/Madrid**, the zone the accountant report buckets by.
- Rows are ordered oldest first.
- Buyers whose PII was erased (GDPR) appear with their tombstoned name/email; the amounts stay.

**As the Board**, I can see that a download happened: every call writes an audit entry
`TicketDonationsExported` on entity type `Tickets` with the actor, the number of donation rows,
and the total in EUR.

**As a TicketAdmin or Board member**, I cannot download this file. The action carries
`PolicyNames.AdminOnly`, tightening the controller-wide `TicketAdminBoardOrAdmin`; the button is
only rendered for admins. Anonymous callers are denied by the same policy.

## Data Model

No schema change. Reads `TicketOrder` (buyer name/email, `DonationAmount`, `PurchasedAt`,
`PaymentStatus`, `VendorOrderId`) and its `TicketAttendee` rows (`Price`, `Status`).

- `OrderExportRow` (existing) gained `VendorOrderId`, `PurchasedAt`, `VipDonations`.
- `DonationExportRow` (new, section-internal): `Date, VendorOrderId, BuyerName, BuyerEmail, Type, Amount`.
- `AuditAction.TicketDonationsExported` (new).

## Workflow

1. Admin opens `/Tickets/SalesAggregates`, clicks **Ticketing Donations**.
2. `TicketController.ExportDonations` (`AdminOnly`) resolves the current user and calls
   `ITicketService.GetDonationExportDataAsync(actorUserId)`.
3. `TicketQueryService` filters paid orders, splits each into its donation rows, dates them in
   Europe/Madrid, writes the audit entry, returns the rows.
4. The controller renders the CSV through `HumansCsv` and appends the `Total` row.

## Related

- [`Tickets.md`](../Tickets.md) — endpoint table, audit list, and the monthly accountant recap
  the totals must match.
- [`authorization.md`](../authorization.md) — policy row for `ExportDonations`.
- The older `/Tickets/Export/Orders` and `/Tickets/Export/Attendees` CSVs are unaudited and
  TicketAdmin-wide: recorded as debt `TICKETS-9` in [`debt.yml`](../debt.yml), not a precedent.
