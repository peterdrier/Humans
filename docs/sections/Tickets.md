# Tickets — Section Invariants

External ticket vendor sync (orders + attendees), Stripe-fee enrichment, auto-matching to humans by email, event-participation derivation.

## Concepts

- **Ticket Orders** and **Ticket Attendees** are records synced from an external ticket vendor. They are not manually created in the system.
- A **Ticket Order** represents a purchase (one per transaction). It is enriched with Stripe fee data (payment method, Stripe fee, application fee) during sync.
- A **Ticket Attendee** represents an individual ticket holder (one per issued ticket, multiple per order).
- **Auto-matching** links orders and attendees to humans in the system by email address.
- **Ticket Sync** is a background job that pulls order and attendee data from the vendor.

## Data Model

### TicketOrder

**Table:** `ticket_orders`

Ticket purchase order synced from vendor (one per purchase). Enriched with Stripe fee data (`PaymentMethod`, `StripeFee`, `ApplicationFee`) during sync.

Cross-domain nav `TicketOrder.MatchedUser → MatchedUserId` (Users/Identity). Target: strip nav, keep FK only.
Aggregate-local: `TicketOrder.Attendees`.

### TicketAttendee

**Table:** `ticket_attendees`

Individual ticket holder (issued ticket, multiple per order).

Cross-domain nav `TicketAttendee.MatchedUser → MatchedUserId`. Target: strip nav, keep FK only.
Aggregate-local: `TicketAttendee.TicketOrder`.

### TicketSyncState

Singleton tracking ticket sync operational state (last sync time, last error).

**Table:** `ticket_sync_states`

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| TicketAdmin, Board, Admin | View the ticket dashboard (sales, revenue, fee breakdowns). View orders and attendees |
| TicketAdmin, Admin | Trigger ticket sync. Generate discount codes. Export ticket data |
| Admin | Manage ticket sync configuration. Execute manual vendor API operations |

## Invariants

- Ticket orders and attendees are synced from the external vendor — they cannot be manually created or edited.
- Orders are enriched with Stripe fee data during sync.
- Orders and attendees are auto-matched to humans by email address during sync.
- Ticket sync state is a singleton tracking the last sync time and status.

## Negative Access Rules

- Board **cannot** trigger ticket sync, generate codes, or export data. Board can only view the dashboard, orders, and attendees.
- TicketAdmin **cannot** manage sync configuration or execute manual vendor API operations.
- Regular humans have no access to ticket management or the ticket dashboard.

## Triggers

- When ticket sync runs, new orders and attendees are imported and existing ones are updated.
- Auto-matching runs during sync: orders and attendees are matched to humans by email.
- Ticket sync derives `EventParticipation` records: valid ticket → Ticketed, checked-in → Attended (permanent).
- When a user's last valid ticket is voided/transferred, their TicketSync-sourced participation record is removed.
- Ticket purchase overrides a `NotAttending` declaration.
- "Who Hasn't Bought" excludes humans who declared not attending.

## Cross-Section Dependencies

- **Campaigns:** TicketAdmin can generate discount codes for campaigns via the ticket vendor integration (`ITicketVendorService`).
- **Profiles:** `IUserEmailService` — ticket orders and attendees are auto-matched against human email addresses.
- **Users/Identity:** `IUserService` — writes derived `EventParticipation` records (User section owns `event_participations` per peterdrier/Humans PR #243).
- **Shifts:** `IShiftManagementService.GetActiveAsync` — active-event lookup (replaces prior direct `_dbContext.EventSettings` read, PR #545c).
- **Budget:** `ITicketingBudgetRepository` (Tickets-owned, shared with Budget via interface) — paid-order lookups for ticketing budget projections.
- **Admin:** Sync configuration and manual vendor operations are Admin-only.

## Architecture

**Owning services:** `TicketQueryService`, `TicketSyncService`, `TicketingBudgetService`
**Owned tables:** `ticket_orders`, `ticket_attendees`, `ticket_sync_states`
**Status:** (B) Partially migrated. `TicketSyncService` and `TicketingBudgetService` moved to `Humans.Application.Services.Tickets` (sub-tasks nobodies-collective/Humans#545b / #545c, 2026-04-22); `ITicketVendorService` connector extracted in peterdrier/Humans PR #277. **`TicketQueryService` remains in `Humans.Infrastructure/Services/`** with direct `HumansDbContext` — pending upstream promotion and the final sub-task under umbrella issue nobodies-collective/Humans#545.

### Target repositories

- **`ITicketRepository`** — owns `ticket_orders`, `ticket_attendees`, `ticket_sync_states`
  - Aggregate-local navs kept: `TicketOrder.Attendees`, `TicketAttendee.TicketOrder`
  - Cross-domain navs stripped: `TicketOrder.MatchedUser` (keep `MatchedUserId` FK only), `TicketAttendee.MatchedUser` (keep `MatchedUserId` FK only)
- **`ITicketingBudgetRepository`** — LANDED in PR for sub-task nobodies-collective/Humans#545b. Narrow read surface for paid-order projections; consumed by Budget.

### Current violations

Observed in `TicketQueryService` (pending #545 final sub-task; baseline 2026-04-15):

- **Cross-domain `.Include()` calls:**
  - `TicketQueryService.cs:570-572` — `.Include(u => u.Profile).Include(u => u.UserEmails).Include(u => u.TeamMemberships).ThenInclude(tm => tm.Team)` on `Users` (Profiles + Teams traversal)
  - `TicketQueryService.cs:339-340` — `.Include(c => c.Grants).ThenInclude(g => g.Code / g.User)` on `Campaign` (Campaigns + Users traversal)
  - `TicketQueryService.cs:699` — `.Include(o => o.MatchedUser)` (Users)
  - `TicketQueryService.cs:768` — `.Include(a => a.MatchedUser)` (Users)
- **Cross-section direct DbContext reads:**
  - `TicketQueryService.cs:337` — `_dbContext.Set<Campaign>()` (Campaigns)
  - `TicketQueryService.cs:569` — `_dbContext.Users` (Users/Identity)
  - `TicketQueryService.cs:584` — `_dbContext.EventSettings` (Shifts)
  - `TicketQueryService.cs:588` — `_dbContext.EventParticipations` (Shifts)
  - ~~`TicketSyncService.cs:440` — `_dbContext.EventSettings` (Shifts)~~ **Resolved in PR #545c (2026-04-22)**: active-event lookup now routes through `IShiftManagementService.GetActiveAsync()`.
  - ~~`TicketSyncService.cs:449, 481` — `_dbContext.EventParticipations` (Shifts)~~ **Resolved in PR #545c (2026-04-22)**: participation reads/writes now route through `IUserService` (User section owns `event_participations` per peterdrier/Humans PR #243).
- **Within-section cross-service direct DbContext reads:**
  - ~~`TicketingBudgetService.cs:44` — reads `_dbContext.TicketOrders` directly.~~ **Resolved in PR #545b (2026-04-22)**: migrated to `Humans.Application.Services.Tickets` and now reads paid orders through the narrow `ITicketingBudgetRepository`.
- **Inline `IMemoryCache` usage in service methods:**
  - `TicketQueryService.cs:38, 42` — direct `_cache.TryGetExistingValue` / `_cache.Set` around ticket counts
  - `TicketQueryService.cs:81` — `_cache.GetOrCreateAsync(CacheKeys.UserIdsWithTickets, ...)`
  - `TicketQueryService.cs:132-133` — `_cache.Remove(...)` / `_cache.InvalidateTicketCaches()` invalidation scattered in service
- **Cross-domain nav properties on this section's entities:**
  - `TicketOrder.MatchedUser` → `User` (Users/Identity)
  - `TicketAttendee.MatchedUser` → `User` (Users/Identity)

### Touch-and-clean guidance

- When touching `TicketQueryService` user-matching code (lines 569-572, 697-699, 767-769), do not add new `.Include(... MatchedUser ...)` or new traversals off `User`. Fetch via `IUserService` / `IProfileService` and project into Ticket DTOs in memory by `MatchedUserId`.
- When touching participation/event logic in `TicketQueryService.cs:584-588`, route through a Shifts-owned interface (`IEventSettingsService` / `IEventParticipationService`) rather than adding more `_dbContext.EventSettings` / `_dbContext.EventParticipations` reads. (`TicketSyncService` already routes these through `IShiftManagementService` / `IUserService` as of PR #545c.)
- When touching code tracking (`TicketQueryService.GetCodeTrackingDataAsync`, ~line 337), do not deepen the `Campaign` / `Grants` / `User` include chain; call `ICampaignService` for campaign + grant data and correlate with local ticket orders in memory.
- When touching cache logic around ticket counts (`TicketQueryService.cs:38-42, 81, 132-133`), keep cache calls confined to this service — do not push `IMemoryCache` into controllers or view components — and prefer adding to `CacheKeys.InvalidateTicketCaches()` over sprinkling new `_cache.Remove` sites, so the eventual caching decorator has a single seam to replace.
- When extending `TicketingBudgetService`, add new Tickets-side read methods to `ITicketingBudgetRepository` (narrow, Tickets-owned) rather than reaching into `HumansDbContext`. Projection/line-item writes remain Budget-owned and must route through `IBudgetService`.
