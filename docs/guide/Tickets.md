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

Ticket data syncs automatically. Attendees are auto-matched to humans by email, so if the email on the issued ticket matches an email on your profile (OAuth, verified, or even unverified), your ticket shows up on your Dashboard on its own.

## Key pages at a glance

- **Your Ticket card** — card on your Dashboard (`/Dashboard`) showing your ticket status
- **Tickets dashboard** (`/Tickets`) — summary cards, Volunteer Ticket Coverage, participation breakdown, daily sales chart, problems list, recent orders
- **Orders** (`/Tickets/Orders`) — paginated orders with donation/VAT columns
- **Attendees** (`/Tickets/Attendees`) — paginated attendees with VIP badges and taxable/donation split
- **Codes** (`/Tickets/Codes`) — discount-code redemption tied to campaigns (read-only here; codes are *generated* on the Campaign detail page)
- **Gate List** (`/Tickets/GateList`) — door check-in list
- **Who Hasn't Bought** (`/Tickets/WhoHasntBought`) — active Volunteers without matched tickets
- **Sales Aggregates** (`/Tickets/SalesAggregates`) — weekly and quarterly reports

## As a Volunteer

### See whether you have a ticket

Your Dashboard (`/Dashboard`) shows a Ticket Status card. If at least one valid attendee record is matched to you, it confirms you have a ticket and shows the count when you have more than one. If nothing is matched, it shows a button linking out to the vendor's purchase page (and a "Different email?" link to your profile emails). If ticketing is not configured at all, you see a warning instead.

Matching is by **attendee email**, not buyer email. The sync compares each attendee email against every email on every user (OAuth, verified, and unverified), normalized so gmail/googlemail aliases collide. If you bought tickets for other people but not one for yourself, you do not count as having a ticket — buyer-only matches don't earn coverage.

![TODO: screenshot — Dashboard Ticket Status card in the "has ticket" state]

### Get your ticket matched

If you have paid but your card still says you do not have a ticket, the attendee email on the order probably is not on your profile. Go to `/Profile/Me/Emails`, add the email you used at checkout, verify it, and the next sync picks it up. You buy tickets on the vendor's site — the Dashboard ticket card links out when you do not already have one matched.

## As a Board member / Admin (Ticket Admin)

Ticket Admin, Admin, and Board all see the Tickets dashboard. Board can view everything but cannot trigger sync, export, or generate codes — those require Ticket Admin or Admin.

### Dashboard, orders, and attendees

`/Tickets` shows five summary cards across the top — Tickets Sold (with a break-even progress bar against capacity), Gross Revenue (with a fees percentage), Net Revenue (with the Stripe / TT split), Avg. Gross Price, and Tickets Remaining. Below them are a Volunteer Ticket Coverage card (matched-attendee Volunteers as a percentage of active Volunteers, colour-coded by coverage band), a Participation Breakdown donut (Has Ticket / No Ticket / Not Coming for the active event year), a Daily Sales chart with a 7-day rolling average line, a Fees by Payment Method table when fees are present, an Attention list (unmatched orders, sync errors, low-remaining warning), and Recent Orders. The coverage card links through to "Who Hasn't Bought?".

`/Tickets/Orders` lists every order with search, sort, filter (payment status, ticket type, matched/unmatched), and per-order donation, VAT, discount, payment-method, and Stripe-fee columns. `/Tickets/Attendees` lists every issued ticket with a VIP badge above the VIP threshold (315 EUR) and the split between taxable portion and VIP donation premium.

![TODO: screenshot — `/Tickets/Orders` with the paginated order list, donation and VAT columns]

### Trigger a sync

Sync runs on a schedule (every 15 minutes by default); Ticket Admins and Admins can also trigger one manually with the **Sync Now** button at the bottom of the dashboard. A sync pulls new and updated orders and attendees from the vendor since the last successful sync (using `LastSyncAt` as the cursor), upserts them, re-runs email matching, enriches paid orders with Stripe fee data when a payment-intent id is present, recomputes VAT using the VIP split, marks used vendor codes as redeemed on their campaign grants, and reconciles `EventParticipation` rows for matched users.

A separate **Full Re-sync** button (Admin-only, with a confirmation prompt) clears the cursor and re-fetches every order from the vendor. Use this only when you suspect the incremental sync has missed historical data.

### Codes and sales reports

`/Tickets/Codes` shows which discount codes have been used and ties them back to their campaigns. Code *generation* happens in the Campaigns section — open the relevant campaign at `/Campaign/Detail/{id}` and use the generate-codes action there (Ticket Admin or Admin only). Board can view this page and the redemption table but cannot generate codes.

`/Tickets/SalesAggregates` gives weekly (Monday–Sunday) and quarterly (calendar Q1–Q4, matching the Spanish tax convention) views of revenue, Donations, VIP Donations, VAT, and Net. Figures come from the VIP split logic, not the vendor's own tax line items.

### Who hasn't bought yet

`/Tickets/WhoHasntBought` lists active Volunteers with no matched tickets, excluding those who have declared they are not attending this year. Filter by team, membership tier, or ticket-status, and search across name and any verified email (so you can find humans whose ticket was bought under a secondary address). Operational companion to the Volunteer Ticket Coverage card.

### Backfilling participation

When you import historical attendance from outside the vendor (e.g. from a previous year's spreadsheet), Admins can paste a CSV of `UserId,Status` rows at `/Tickets/Participation/Backfill`. The page is scoped to the active event year and writes through `IUserService.BackfillParticipationsAsync`. Ticket Admin and Board do not have access to this page.

### Sync configuration

Vendor `EventId`, sync interval, and break-even target live in `appsettings`'s `TicketVendor` section; the API key comes from the `TICKET_VENDOR_API_KEY` environment variable. None of these are editable from inside the app — they're set at deploy time. Stripe enrichment requires `STRIPE_API_KEY` to be set; if it's missing, fee columns stay empty but everything else still syncs.

## Related sections

- [Profiles](Profiles.md) — tickets match by verified email addresses, and the ticketing notification category is locked on when you have a matched ticket
