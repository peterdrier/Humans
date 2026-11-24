# Humans — Ubiquitous Language

Canonical vocabulary for the Humans codebase. This glossary is the single source of truth for terms; where prose elsewhere uses a different word for one of these concepts, that prose is wrong (and the hard rules win over this glossary where they overlap). Definitions only — no implementation detail.

## Architecture roles

> Organizing idea (from the 2026-05-25 grill): a service's role is defined by its **width** — how many lanes it touches — not by its position in a top/bottom stack. Width is a cost, not a feature.

**Lane**:
One section's domain — its tables plus the logic over them. A service's reach is measured in lanes.

**Width**:
How many lanes a service touches. Lower is better; more is worse. A **Section** is width-1; a **Crosscut** is full-width but logic-free; an **Orchestrator** is width-few.

**Section**:
A vertical slice that owns its domain model and database tables and is the sole gateway to them. Touches exactly one lane — its own.
_Avoid_: vertical, vertical slice, module

**Foundational** (descriptor, not a role):
A **Section** that every member necessarily has — **User** and **Profile**. Universality gives it maximal in-degree (almost everything calls it), so to avoid service loops its outbound width into other sections is pinned to **zero**: it may call only Crosscuts, never up into another Section.
_Avoid_: foundational layer (it's not a layer/tier — just a Section with an outbound-width-zero constraint)

**Crosscut**:
A section whose service every other section may call and which carries **no** section-specific logic — a tool (Audit, Email, Notification, Metrics). It owns its **own** data (e.g. the audit log) but reaches into no other section's. (Owning *no* data is the **Orchestrator**, not the Crosscut.)
_Avoid_: horizontal, cross-cutting service, leaf, utility

**Orchestrator**:
A service that **owns no tables** and exists because the action crosses multiple sections; it coordinates ≥2 sections through their interfaces and holds only coordination logic — reaching data only through other sections' services, never a repository. Width (lanes coordinated) is a cost to minimize. Examples: GDPR export (fans out to every `IUserDataContributor`), Onboarding (user/profile + shifts + consent). The moment a thing owns a table it is a **Section**, not an Orchestrator — no matter how much it coordinates.
_Avoid_: director

**Homed** vs **Owns**:
*Homed* is where a service's code lives (namespace / controller proximity); *owns* is table ownership. An Orchestrator may be **homed** in a section's lane for controller-wiring convenience while **owning** no lane. "Which lane is X in?" must be answered as homed-or-owns, never conflated.

## Section glossaries

Words that belong to one section live in that section's own glossary. Words used across sections stay in this file.

- [Agent](src/Sections/Humans.Agent/Docs/CONTEXT.md): the member help chat (Assistant)
- [Audit](src/Sections/Humans.AuditLog/Docs/CONTEXT.md): the audit log, its entries and the history shown on pages

## Finance

**Reconciliation** (recon):
Checking that two records of the same money agree, across all of finance: ticket orders against a ticketing platform, Stripe against our orders, Pleo against Holded. It is made of **checks**, each named for the two things it compares. A **difference** is anything the two sides disagree on; a check **balances** when none is left.

**Gross**:
The first number: money charged, before refunds, chargebacks, fees, taxes or anything else come out.

**Net**:
Gross less refunds, chargebacks and fees.

**Fee**:
What the payment processor (Stripe) takes from us on a payment. Never something the buyer pays.
_Avoid_: surcharge (there are none)

**Payout**:
Stripe sending money from its balance to our bank account.

**Paid**:
An order whose money arrived. It stays paid for good: refunds and chargebacks are amounts recorded against it, never states that replace it.

**Chargeback**:
Money the buyer's bank took back from us.
_Avoid_: dispute (Stripe's word for the process, not the money)

## Ticketing

> From the 2026-11-22 ticketing grill. Kept here until it grows too big, then it moves to a Tickets-section glossary linked from this file.

**Order**:
Everything one buyer pays for at once, settled as a single payment. It may hold several tickets for several people, for one event or more, plus donations.
_Avoid_: checkout, purchase, transaction (as the noun for the whole thing)

**Ticket**:
One person's entry to one event: the code the gate scans.
_Avoid_: attendee, pass

**Buyer**:
The member who pays for an order.
_Avoid_: payer, customer, purchaser

**Holder**:
The person whose legal name is on a ticket. A buyer holds their own ticket if they bought one. When a ticket is resold, the old holder stops being a holder and the new buyer holds a new ticket.
_Avoid_: owner, attendee

**Partner**:
A holder on an order who is not its buyer, usually a friend of the buyer.
_Avoid_: guest, plus-one

**Resale**:
A holder who cannot come puts their ticket up for someone else to buy. It waits in the **resale queue** as a **resale offer**, and they stay its holder until it sells. They are refunded only once it sells.
_Avoid_: return (ambiguous with refund), transfer

**Directed resale**:
Resale where the holder sends a buy link to people they choose; the first of them to buy gets the ticket.

**Refund**:
Money paid back on an order by the ticketing team, outside resale. Very rare, kept for cases like a death or serious illness just before the event; there is no self-service path.

**Rename**:
The ticketing team puts a ticket in another person's name to fix a mistake, such as a buyer who bought a ticket for their partner under their own name. Rare (about 50 of 2,000 in 2026).
_Avoid_: transfer

**Child ticket**:
A ticket in a child's name that belongs to one adult's ticket. It cannot be resold on its own; it is refunded and voided with its adult ticket. Moving a child to another adult means voiding it and issuing a new one under the new adult.

**Companion ticket**:
A free ticket, approved by the ticketing team, in the name of someone who helps a holder (for example a wheelchair user's helper). It belongs to the holder's ticket, is never resold on its own, and is voided with it.

**Wave**:
A dated price step for an event's general ticket (for example 225 until a date, then 250). The wave on sale today sets the public price.

**Wave pin**:
A hold on the wave price from the day a person qualified for it (for example when they applied for low income). It lasts the whole sales season of that event.

**Class price**:
One fixed price that replaces the wave price for a group (low income, student, local). Comes from the person's grants and never travels with the ticket.

**Discount**:
A fixed amount off (such as 25, 50 or 100) that stacks on any price. Comes from the person's grants and never travels with the ticket.
_Avoid_: credit (implies something given in trade, which is a tax problem). The database still stores the rule kind as `Credit`.

**Grant** (in ticketing):
A discount program's statement that a person qualifies (low income, student, local, early access). Ticketing acts on the grant alone; how the program checked the person is its own business, and ticketing never sees the evidence.

**VIP ticket** ("Very Unimportant Person"):
A shortcut that buys a ticket at the current wave price plus a large donation, at a fixed total (such as 400, 500 or 1,000). It carries no privileges; the name is an inside joke.
_Avoid_: supporter total (the configured totals' old name), upgrade, premium

**Event**:
The burn people attend, one system-wide record. Everything in ticketing hangs off an event; today one event sells at a time, but nothing may assume that, since other groups using Humans run overlapping events.

**Ticketing settings**:
One event's ticketing configuration: its ticketing platform, sales mode, waves, prices and switches.
_Avoid_: sales event, sales setup (the old names; the table is still `ticket_sales_events`); not the older TicketTailor sync settings

**Sales mode**:
Where an event's sales stand: **Closed**; **Invited only** (early sales and test purchases, only for people with an early access grant); **Open**; **Resale only** (fresh stock stops and only resale offers sell).

**Ticketing platform**:
The system that takes payment for an event's orders at the price Humans sets. Each event uses one: **Box Office** (Humans' own, on Stripe), **pretix** or **Hi.Events**.
_Avoid_: backend, checkout backend (the old names), vendor (TicketTailor only)

**Void**:
A ticket stops admitting anyone. No money moves; a refund is its own step.

**Cancel**:
Close an order that was never paid, or a whole event. A cancelled event voids every ticket and refunds the ticket money. A paid order is never cancelled: it is refunded and its tickets voided.

**Override**:
An Admin breaking a ticketing rule on purpose (restoring a voided ticket, a companion past capacity), with a reason, audited. Ordinary team actions (rename, void, refund) are not overrides.

**Order timeline**:
An order's history: every step, who took it and when. It only grows; nothing on it is edited or removed.

**Flag**:
Humans marking an order for the ticketing team to look at (a late payment for someone who already holds a ticket, a held-back resale refund). It goes on the order timeline and the audit log.

**Resale refund**:
What a holder gets back once their resold ticket's sale is confirmed. Paid in batches for convenience, not at the moment of sale.

**Scan**:
Reading a ticket's code at the gate.

**Admit**:
The gate letting a holder in: the ticket is valid and their name matches their ID. Humans keeps one **admission** per ticket, the only check-in record anywhere; no ticketing platform is told.
_Avoid_: check-in (except as the report's name)

**Early entry**:
A privilege from Camps or Shifts that lets a holder in before general entry opens. Not a kind of ticket.

**Participation**:
A member's status for one event year: not attending, ticketed, attended or no-show. Internal: it answers "does this person have a ticket" for shift planning and mailing cohorts.

**Carrier product** (builders only):
The one hidden ticket product and one donation product in a pretix or Hi.Events event, so the platform charges whatever Humans prices. No catalogue is mirrored there.

**Platform order** (builders only):
A ticketing platform's copy of a Humans order, linked by the Humans order id.

**Webhook** (builders only):
A nudge from a ticketing platform to re-read it. The truth always comes from that re-read, or from the five-minute poll that catches missed nudges.

**TicketTailor order** / **TicketTailor attendee** (retired):
The 2026 records synced in from TicketTailor. Kept as history; nothing new is built on them.

## Tasks

> From the 2026-11-22 Tasks design. Code uses these words; screen wording stays within `memory/product/humans-terminology.md`.

**Task**:
A piece of work with an end. In code the type is `TaskItem`, because `Task` is taken by .NET.

**Team** (of a task):
What a task belongs to. The screen never says "owner", which reads as a person.

**Slot**:
One person needed. A task with 4 slots needs 4 people.

**Assign / unassign**:
A manager puts a person on a task, or takes them off.

**Claim**:
A person takes a free slot themselves. The button says something like "I'll do this".

**Release**:
A person gives their own slot back.

**Needs approval**:
A claim on this task waits for a manager's answer. `NeedsApproval` in code.

**Request**:
A claim that is waiting for that answer. A manager approves or declines it.

**Priority** (of a task):
How much the task matters. Same levels as roles.
_Avoid_: criticality

**Blocked**:
A status picked by hand: the task is held up by something outside, such as a permit.

**Waiting on**:
Shown automatically when a task this one depends on is not finished.

**Manager** (of a task):
Anyone who holds `Tasks.Manage` for the task's team. Not Teams' sub-team manager.

## Flagged ambiguities

- The same concept has three names across docs and must converge here:
  - **Crosscut** ← "horizontal" (hard-rules), "cross-cutting service" (design-rules), "leaf" (memory atoms).
  - **Section** ← "vertical" / "vertical slice" (hard-rules), "section" (design-rules / atoms).
  - **Orchestrator** ← "director" (memory atoms).
- **EventParticipation** reads as an Events concept but its EF config lives in `Configurations/Shifts/` → the `event_participations` table is owned by the **Shifts** Lane. Name is a smell; ownership follows the config. (Surfaced 2026-05-25 grill — confirm the true owner with Peter.)
- Headwords above are the grill's opinionated picks; final naming is Peter's (hand-edit).
- **Orphan** is *not* a role. It named the instinct "this could stand alone instead of being bolted onto User" (the merge-id mistake). Resolved: cross-lane needs go to an **Orchestrator** (e.g. an Audit orchestrator that gathers merged ids and calls Audit *with* the list), keeping the Crosscut pure. Don't re-introduce "orphan" as a role.

## Example dialogue

> **Dev:** "Audit needs the member's display name — can `AuditLogService` call `IUserServiceRead`?"
> **Architect:** "No. Audit is a **Crosscut** — every **Section** calls it, so it must hold no section logic and reach into no **Section**. Put the cross-lane work in an **Orchestrator**: it gathers the data (the display name, or the merged-account ids) and calls Audit *with* the list. Audit never reaches out."
> **Dev:** "So a Crosscut is just a Section that everyone uses?"
> **Architect:** "Essentially yes — a Crosscut owns its own **Lane** (the audit log) but carries no *other*-section logic. The role that owns **no** lane at all is the **Orchestrator**. The moment a Crosscut carries section-specific logic, it has stopped being a Crosscut."
