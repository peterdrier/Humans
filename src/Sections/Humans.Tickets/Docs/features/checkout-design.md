# Ticket checkout (2027): design record

Dated record of the design session with Peter on 2026-11-20/21: what was decided and why. How it is built lives in
[`checkout.md`](checkout.md); where the two differ, `checkout.md` and the code are current and this file is history.

## Why

Nobodies leaves TicketTailor for the 2027 event (early June; sales open around 2 January). Three options are built
behind one interface and run side by side so the choice can be made on evidence: pretix, Hi.Events, and Humans' own
Box Office on Stripe.

## Principle

Every ticketing rule is a rule about a person, and Humans is the only system that knows people. So Humans owns the
catalogue, the price, the named ticket, the QR code and the resale queue. A backend only takes a payment at a price
Humans dictates, reports the outcome, refunds an exact amount, and offers the payment methods.

- **One purchase path, account first.** Buying from a Humans profile is the main route. A signed-out visitor sees the
  public prices, gets an emailed link to sign in or sign up, then buys as a member. No pay-first guest checkout
  (Peter, 2026-11-21).
- **No discount codes.** Humans knows what the buyer is entitled to and asks for a checkout at that price.
- **No vendor catalogue.** Waves and ticket classes are configured once, in Humans.
- **Tickets is the router.** It owns the port, the per-event backend choice and the shared order and ticket records,
  and holds nothing specific to a backend. Each backend is its own section (`Humans.BoxOffice`, `Humans.Pretix`,
  `Humans.HiEvents`). The port may be extended as the work needs without asking Peter (standing approval, 2026-11-21:
  "be smart about it, don't be wasteful").

## Price rule

What a person pays is the wave price on their pricing date, replaced by a class price if they have one, minus their
credits, plus any donation.

1. **Wave price.** Waves end on a date (for example 225, 250, 275, 300). Capacity is capped (3,000 for 2027).
2. **Pricing date.** Today, unless the person has a pin. Applying for low income pins that day's wave. An applicant
   who gets no allocation keeps a buy button at the pinned wave price. In 2026 this was a painful manual process;
   the pin exists so nobody manages it by hand.
3. **Class price.** At most one of low income (one fixed price, probably 125 for 2027), student (under 26), local,
   child. A companion ticket is free.
4. **Credits.** Fixed amounts (25, 50, 100) that stack on anything.
5. **Donation.** A separate line with a purpose (general, creativity, low income pool), with or without a ticket.
   A VIP button (400, 500, 1,000) is the current wave price as the ticket plus the remainder as a donation, and must
   read that way at checkout. The ticket plus donation VAT split was validated by the lawyer or accountant in 2026.

Class prices and credits are never transferable. Sales stop about a week before the event, decided late.

## Tickets and people

Every ticket carries one person's legal name, which must match their ID at the gate. That is what keeps the event
private under Spanish law.

- **No transfers.** A rename is a rare admin exception for a full price ticket. Third-party resale is never allowed.
- **Buying for someone else.** The buyer enters the other person's name and email; Humans creates that person's
  account and the ticket is theirs. The buyer is only the payer.
- **Dependents.** A child or companion ticket hangs off one adult's ticket and is returned with it.
- **Verification.** Humans stores the result of a check (who, when, what), never the document.
- **Early entry** is a privilege managed by Camps and Shifts and attached to the ticket, not a ticket type.

## Return and resale

Discounts are non-refundable, but nobody should be stuck with a ticket they cannot use. A holder who cannot come
offers their ticket back; they are refunded only when someone else buys it, and until then it is still theirs.

- Offered tickets sell before fresh stock, oldest offer first, only at the current public price. The buyer gets a new
  ticket in their own name and the old one is voided. Buyers with a class price or a pinned wave take fresh stock.
- The holder is refunded the ticket price they paid, without fees unless the law requires otherwise. The association
  keeps the difference. Donations are not refunded; a request is handled by hand.
- Resale can stay closed until a sales threshold, and sales have a mode: open, resale only, closed.
- The holder can withdraw before it sells. Unsold when sales close means no refund, and they may still attend.
- **Directed resale.** The holder names one person by email, who buys that specific ticket ahead of the queue through
  a single-use link. Otherwise it is ordinary resale; no money moves between the two people.

## The whole journey

Selling a ticket is the small part (Peter, 2026-11-21, on the first Box Office build). The design covers the order
page with plain copy, the ticket rules checkbox recorded on the order, the mailing list opt-in, the ticket email with
QR code, the receipt, the follow-up inviting the buyer to take shifts, self-service returns, and the ticketing team's
tools: order lookup, order timeline, resale queue, refund list, reports. These are built once in Tickets over the
shared records so they work the same on every backend, and the backend's own emails are switched off.

Several events can sell at once with overlapping windows. Everything is keyed by event and nothing new may assume a
single current event.

## Gate

The guided check-in stays: the ticket is valid and not already admitted, the date is allowed (general entry is open,
or the holder's early entry covers today), and the name matches the photo ID. Humans' gate record is the only check-in
record; no backend is told about check-ins.

**Out of scope: signing anything at the gate.** Paper signatures, a volunteer agreement prompt and any use of the
Consent module were removed from this work by Peter on 2026-11-21. Do not design or build for them unless he raises it.

## The three options, as first found

From reading the repos on 2026-11-21, before anything ran:

| | pretix | Hi.Events | Box Office |
|---|---|---|---|
| Order at a price Humans sets | Stock order API | Needed an endpoint added to our fork | Humans creates the Stripe checkout |
| Refund an exact amount | Yes | Yes | New code; refunds were dashboard-only |
| Price by payment method | Built in | No | No |
| Hosted instance usable | Expected | No, needs the fork | Not applicable |
| What we carry | A Django app to host | A Laravel app to host, plus our patch | More code in Humans |

Hi.Events' licensed `ee/` features (reserved seating, its own box office, SSO) are not used.

## Open at the end of the session

For Peter: whether a child gets their own account; when resale opens; how long a wave pin lasts; whether Mollie is
wanted beside Stripe; whether only adult tickets can be offered for resale; how a partner accepts the ticket rules.

For a Spanish consumer law lawyer: whether the price may differ by payment method, and whether a voluntary return may
leave out the payment fee. For the accountant: donation certificates and their registration.

Side quests named and left out: paying manually by IBAN, and a phone scanner app (pretixSCAN is Apache 2.0 and could
be forked later).
