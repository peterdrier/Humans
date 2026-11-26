<!-- freshness:triggers
  src/Sections/Humans.Tickets/Services/Delivery/**
  src/Sections/Humans.Tickets/Services/TicketsEmails.cs
  src/Sections/Humans.Tickets/Controllers/TicketCodeController.cs
  src/Sections/Humans.Tickets/Views/Tickets/Buy/Mine.cshtml
  src/Sections/Humans.Tickets/ViewComponents/IssuedTicketsViewComponent.cs
-->

# Ticket delivery (2027)

Design: "The whole journey" in the Ticketing 2027 design doc. People get confused without a ticket that has a barcode,
so every named person gets their own ticket by email, and can open it in Humans at any time.

## What is sent

| Mail | To | Category | When |
|------|----|----------|------|
| `ticket_issued` | each valid or offered ticket with an email, to its holder; a ticket without one (a child) rides on its adult's mail | System | as soon as the order's tickets are issued |
| `ticket_order_receipt` | the buyer | System | with the tickets |
| `ticket_join_invitation` | each holder of an adult or companion ticket with an account | VolunteerUpdates (opt-out honoured, never locked) | two days later, only if the ticket is still `Valid` |
| `ticket_payment_ended` | the buyer | System | when an order that reached its platform ends `Failed` (declined) or `Expired` (lapsed unpaid), with a link to try again (B16, B17) |
| `ticket_resale_sold` | the holder of a resold ticket | System | at the sale; says the refund follows in the evening batch (H11) |
| `ticket_resale_unsold` | the holder whose offer is still open at sales close, unless the event was cancelled | System | once; no refund, the ticket still admits them (H12) |
| `ticket_issued` again | the holder of a renamed ticket | System | after a team rename, with fresh ids (H14) |

- **Ticket email:** one card per ticket (the holder's, then any child riding on it): event, holder name, ticket kind, the QR code of the ticket's `Barcode` (the code the gate scans),
  the barcode as text, a link to `/Tickets/Buy/Mine`, and the ticket rules (named ticket matching photo ID, no
  outside resale, resale through Humans refunds the ticket price once it sells and never the donation, private event of
  the association). When someone else paid, it says who.
- **Receipt:** order reference (`TicketCheckoutOrder.Reference`, also shown on the buyer and admin order pages), paid date in the event's time zone, payment
  method, each ticket line with its VAT rate, each donation line marked without VAT, total, any refund so far, and
  "a receipt, not an invoice" (no invoices for tickets).
- **Invitation:** asks the holder to complete their profile and pick a shift, linking `/Profile` and `/Shifts`. It requires a resolved, non-tombstoned account, skips unavailable accounts even if the ticket retains an address, and identifies live merge recipients by their survivor id.

Each holder's mail renders in their profile language once they have signed in, otherwise in the language the order
was placed in (an account that has never signed in has the default language, not a choice); the receipt in the order's language. All copy lives in `TicketsResource`
in the seven cultures, and every template has a sample in `/Email/EmailPreview`.

## QR image

Mail clients block `data:` images, so the email's `<img>` loads `/Tickets/Code/{token}`, anonymous because a mail
client fetches it signed out. The token is the barcode under a Data Protection purpose (`TicketCodeImage`), so the
endpoint never renders text Humans did not issue. The QR uses error correction level Q, which survives a scuffed
screen. `/Tickets/Buy/Mine` embeds the same image as a `data:` URI.

## Repeats and resends

`TicketsIssuedAsync` only enqueues a Hangfire job (`TicketDeliveryService.DeliverAsync`), so a failure while sending
is retried instead of lost inside the paid transition. Each mail's `EmailMessage.DeliveryId` derives from the template
and the ticket or order id, so a retry queues each mail once while the outbox keeps the first row. A ticket with no
address is logged and counted as skipped. Each delivery and resend goes on the order timeline (`TicketsEmailed`,
`TicketsResent` with the actor, `TicketMailSkipped`) as well as the audit log. The payment, resale and rename mails
go through the same hand-off (`ITicketDelivery`), each audited `TicketHolderNotified`; the payment and resale ones have
stable delivery ids, and each sends nothing once the order or ticket has moved on. The audit says what was handed to Email; Email applies
opt-outs itself. A TicketsAdmin's **Resend** on the admin order page (refused when no ticket is live) sends
everything again with fresh ids and no second invitation, audited with the actor.

## Donation record

Donation lines on paid Humans orders join the accountant donor list (`/Tickets/Export/Donations`,
[accountant-donor-list.md](accountant-donor-list.md)) with type `Donation: <purpose>` and the same order reference as
the receipt, so one file covers TicketTailor and Humans orders.

## Not yet

- A PDF ticket attachment (Email has no attachments).
- Donor tax id for the Spanish donation certificate (modelo 182): Humans does not collect it.
