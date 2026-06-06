# Event Ticketing — Requirements

We run a single annual general-admission event for a Spanish nonprofit. Everyone is fundamentally GA; price varies by program, the experience does not. Currency is EUR. This states our business needs, not the vendor's implementation — "ideally" means preference, not requirement.

## Tickets, Pricing & Capacity

- One public ticket, sold in **date-driven price waves** (this year: €275 / €295 / €315). That front-loads sales ahead of deadlines and is about as complex as it gets.
- **One global event capacity cap**; all sales draw from it. A code may optionally be flagged to bypass the cap.
- **Donations** are either a line item on an order, or any amount paid above a **manually-set donation line**. With the line at €200, a €500 ticket = €200 income (VAT due) + €300 donation. Donations are **0% VAT**, non-transferable, and reported separately.
- **Last-minute / gate sales**: a hidden ticket type unlocked by a code (e.g. `LastMinute2026`), sold through the normal web flow so gate staff can confirm the buyer knows what they're showing up to.

## Codes & Special Programs

- Nearly every special price is a code: **1 code = 1 ticket at a set price**, with an optional **"valid until" (purchase-by) date**.
- Staff set up programs via the website. Each program defines: price, purchase-by date, **refundable-until date** (independent of purchase-by), and transferable yes/no. Examples:
  - Low Income — €100, non-transferable, refundable until 1 July
  - Supporter — €250, transferable, valid until 1 May
  - Local — €150, non-transferable, refundable until 1 July
  - Youth — €125, non-transferable, refundable until 1 July
  - Carer — free, attached to another ticket for someone needing support
- A **Carer ticket is rescinded automatically** if the ticket it's attached to is transferred or refunded.
- **API**: codes can be granted, revoked, and queried on demand, so we never manage a pool. Revocation is allowed **only while a code is unused**. A query returns whether the code was used and, if so, the order details.

## Orders & Attendees

- Standard order history and ticket components via API.
- An order may hold multiple tickets; **each ticket carries the recipient's legal name + email**.
- Configurable opt-ins on the order form: marketing-list consent, plus simple acknowledgements ("I promise to read the survival guide", etc.).
- **Scan / attendance data via API**: who attended, and when.

## Changes — Transfers & Refunds

### Transfers

- Tickets are transferable unless the program forbids it. A transfer **cancels the old ticket, issues a new code** to the new holder, and notifies both parties — we do not manage any money between them.
- The original holder is told which of their ticket(s) remain valid ("You now have one ticket for this event: X12345, Peter Drier").
- Ideally automatable via API ("Transfer X1234 to Sally Smith — sally@gmail.com").
- A transferred ticket **does not inherit Early Entry** (EE is tied to a person). Donations never transfer — tickets only.
- Order history shows the **transfer chain** (X1234 was Jim Jones, transferred to Sally Smith on 3 July, by ___).
- Optional per-event **transfer cutoff date**.

### Refunds

- Non-transferable tickets can be refunded (ideally self-service) up to the program's refundable-until date, optionally minus a **fixed fee** (e.g. €5), to the **original payment method only**.
- *Open: donation portion on a refund — assume retained, not refunded. Confirm.*

## Gate & Scanning

- **Single entry** — one valid scan per ticket.
- Every **scan failure returns detailed, actionable info**:
  - **Already scanned** — when, by whom, which scanner (was it 3 seconds ago on this same device?)
  - **Invalid** — never a valid ticket in this system
  - **Cancelled** — refunded or other reason; when and by whom
  - **Transferred** — date, to whom, processed by whom
- Each scanner has a **responsible operator** on record (so we know Sally scanned Bob in).
- **Sync reliability is critical** — we've seen ~15% of scans never sync at another event:
  - Scanner must **display live sync stats**. Working offline is fine; *unsynced* scans are not.
  - An explicit **"I'm done — sync and close"** step (may run days after the event). Closing out performs a **GDPR wipe** of attendee data from the device.
  - We can **report which scanners haven't closed out**, to chase them — many operators use personal devices and will forget.
  - **Late-sync double-scans** (same ticket scanned offline on two devices) are detected and reported once everything syncs.

## Early Entry

- Some people enter early. We track it; ideally the vendor can **annotate the ticket** ("Early Entry — 17 May — Art Project").
- When EE info changes, **email an updated ticket**. The QR / barcode does not change — only the text.
- Ideally the scanner understands EE, with **admin-configurable leeway** at the operator's discretion (a Wednesday EE ticket arriving Tuesday 22:45 is fine).

## Perception

- **Why** someone got a given price (Low Income, Supporter, Local, …) is not shown publicly — we're all GA. Internal / admin / gate views **do** show the type, since handling differs by type.
- The one public exception may be a discreet **"non-transferable" marker** (e.g. a small emoji), at our discretion.

## Reporting & Finance

- **Period revenue report** (monthly / quarterly) with a **4-way split**: # tickets, revenue, VAT, donations.
- Free / comped tickets count toward **issued and revenue**; we don't track sold-vs-gifted counts.
- **Fee classification**: every euro spent on fees is classified by reason — ticketing fee, payment fee by method (Visa / iDEAL / Klarna: revenue / fee / %), refund fee, chargeback / dispute, etc.

## Platform, API & Data

- Currency **EUR**. **API timestamps UTC**; human-facing deadlines are **local Spain time**.
- We lean on the **API** heavily (codes, transfers, scan data), so it should be clean, documented, and have a test environment. Rate limits ideally **surfaced inline** (a "requests remaining" header) so batch loops can throttle before starvation. **Webhooks** are a nice-to-have over polling.
- **Email & "My Account"**: the vendor notifies attendees on ticketing events (purchase, transfer, EE change) and provides a **My Account** area where attendees can view / load their tickets.
- **Standard audit trail** across admin and API actions (who / what / when).
- **Standard GDPR** applies, except **deletion requests are honoured 30 days post-event**.
