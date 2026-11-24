# BoxOffice — Language

Humans' own ticketing platform, taking payment through Stripe. Settled in the 2026-11-24 terminology grill. Ticketing words (order, ticket, buyer, holder, paid, ticketing platform) live in the root [`CONTEXT.md`](../../../../CONTEXT.md) until they move to the Tickets glossary.

## Language

**Box Office**:
The ticketing platform where Humans sells directly and Stripe only takes the payment. "boxoffice" is its key in code.

**Checkout**:
Stripe's hosted payment page, and only that. Never the name for an order.

**Awaiting payment**:
An order whose buyer has not finished on the payment page yet. `Pending` in code.
_Avoid_: pending (on screens)

**Payment on its way**:
An order the buyer finished paying by a slow method, such as a bank debit, whose money has not landed. `Processing` in code.
_Avoid_: processing (on screens)

**Failed** / **Expired**:
The slow payment bounced, or the buyer left the payment page without paying before it timed out.
