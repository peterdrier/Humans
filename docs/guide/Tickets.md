<!-- freshness:triggers
  src/Humans.Web/Views/Ticket/**
  src/Humans.Web/Controllers/TicketController.cs
  src/Humans.Application/Services/Tickets/**
  src/Humans.Domain/Entities/TicketAttendee.cs
  src/Humans.Domain/Entities/TicketOrder.cs
  src/Humans.Domain/Entities/TicketSyncState.cs
  src/Humans.Domain/Entities/TicketingProjection.cs
  src/Humans.Domain/Constants/TicketConstants.cs
  src/Humans.Infrastructure/Data/Configurations/Tickets/**
-->
<!-- freshness:flag-on-change
  Ticket dashboard, sales/attendees/orders views, sync triggering, codes/redemption, gate list, and Volunteer Ticket Coverage. Review when ticket views, sync service, or ticket entities change.
-->

# Tickets

## What this section is for

The Tickets section tracks event ticket sales. Tickets are sold through an external vendor, not through this app — this section mirrors the vendor's data (orders, attendees, redeemed codes) and matches it against humans so the ticketing team can report on sales and see who has not bought yet.

Ticket data syncs automatically. Orders and attendees are auto-matched to humans by email, so if the email you used at checkout matches a verified profile email, your ticket shows up on your homepage on its own.

## Key pages at a glance

- **Your Ticket card** — homepage sidebar widget showing your ticket status
- **Tickets dashboard** (`/Tickets`) — summary cards, daily sales chart, problems list
- **Orders** (`/Tickets/Orders`) — paginated orders with donation/VAT columns
- **Attendees** (`/Tickets/Attendees`) — paginated attendees with VIP badges and taxable/donation split
- **Codes** (`/Tickets/Codes`) — code redemption tied to campaigns
- **Gate List** (`/Tickets/GateList`) — door check-in list
- **Who Hasn't Bought** (`/Tickets/WhoHasntBought`) — active humans without purchases
- **Sales Aggregates** (`/Tickets/SalesAggregates`) — weekly and quarterly reports

## As a Volunteer

### See whether you have a ticket

Your homepage has a "Your Ticket" sidebar card. If attendees are matched to you, it confirms you have a ticket and shows the count. If nothing is matched, it shows a button linking out to the vendor. If ticketing is not configured, you see a warning.

Matching checks attendee records linked to your user ID first, then falls back to your verified emails against each attendee email. If you bought tickets for other people but not one for yourself, you do not count as having a ticket — the attendee email matters, not the buyer email.

![TODO: screenshot — homepage sidebar "Your Ticket" card in the "has ticket" state]

### Get your ticket matched

If you have paid but your card still says you do not have a ticket, the attendee email probably does not match any verified profile email. Go to `/Profile/Me/Emails`, add the email you used at checkout, verify it, and the next sync picks it up. You buy tickets on the vendor's site — the "Your Ticket" card links out when you do not already have one matched.

## As a Board member / Admin (Ticket Admin)

Ticket Admin, Admin, and Board all see the Tickets dashboard. Board can view everything but cannot trigger sync, export, or generate codes — those require Ticket Admin or Admin.

### Dashboard, orders, and attendees

`/Tickets` shows summary cards (average net price with fees deducted, and Volunteer Ticket Coverage as a percentage of active Volunteers matched as attendees), a daily sales chart, and a problems list. The coverage card links through to "Who Hasn't Bought?".

`/Tickets/Orders` lists every order with search, sort, filter, and per-order donation/VAT columns. `/Tickets/Attendees` lists every issued ticket with a VIP badge above the VIP threshold and the split between taxable portion and VIP donation premium.

![TODO: screenshot — `/Tickets/Orders` with the paginated order list, donation and VAT columns]

### Trigger a sync

Sync runs on a schedule; Ticket Admins and Admins can also trigger one manually. A sync pulls new orders and attendees, updates existing ones, re-runs email matching, enriches orders with Stripe fee data, computes VAT using the VIP split, and marks used codes as redeemed on their campaign grants.

### Codes and sales reports

`/Tickets/Codes` shows which codes have been used and ties them back to their campaigns. Ticket Admins and Admins can also generate new vendor codes for campaigns here; Board cannot.

`/Tickets/SalesAggregates` gives weekly (Monday–Sunday) and quarterly (Spanish tax Q1–Q4) views of revenue, Donations, VIP Donations, VAT, and Net. Figures come from the VIP split logic, not the vendor's own tax line items.

### Who hasn't bought yet

`/Tickets/WhoHasntBought` lists active humans with no matched purchases, excluding those who have declared they are not attending. Operational companion to the Volunteer Ticket Coverage card.

### Sync configuration

Sync configuration (vendor event ID, interval, API credentials) and manual vendor API operations are Admin-only, not Ticket Admin.

## Related sections

- [Profiles](Profiles.md) — tickets match by verified email addresses, and the ticketing notification category is locked on when you have a matched ticket
