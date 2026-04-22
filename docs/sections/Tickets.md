# Tickets — Section Invariants

## Concepts

- **Ticket Orders** and **Ticket Attendees** are records synced from an external ticket vendor. They are not manually created in the system.
- A **Ticket Order** represents a purchase (one per transaction). It is enriched with Stripe fee data (payment method, Stripe fee, application fee) during sync.
- A **Ticket Attendee** represents an individual ticket holder (one per issued ticket, multiple per order).
- **Auto-matching** links orders and attendees to humans in the system by email address.
- **Ticket Sync** is a background job that pulls order and attendee data from the vendor.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
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
- Ticket sync derives EventParticipation records: valid ticket -> Ticketed, checked-in -> Attended (permanent).
- When a user's last valid ticket is voided/transferred, their TicketSync-sourced participation record is removed.
- Ticket purchase overrides a NotAttending declaration.
- "Who Hasn't Bought" excludes humans who declared not attending.

## Cross-Section Dependencies

- **Campaigns**: TicketAdmin can generate discount codes for campaigns via the ticket vendor integration.
- **Profiles**: Ticket orders and attendees are auto-matched against human email addresses.
- **Admin**: Sync configuration and manual vendor operations are Admin-only.
- **Event Participation**: Ticket sync auto-creates/updates EventParticipation records. Admin can backfill historical data via Tickets > Backfill.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `TicketQueryService`, `TicketSyncService`, `TicketingBudgetService`
**Owned tables:** `ticket_orders`, `ticket_attendees`, `ticket_sync_states`

## Target Architecture Direction

> **Status:** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the repository/store/decorator pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

### Target repositories

- **`ITicketRepository`** — owns `ticket_orders`, `ticket_attendees`, `ticket_sync_states`
  - Aggregate-local navs kept: `TicketOrder.Attendees`, `TicketAttendee.TicketOrder`
  - Cross-domain navs stripped: `TicketOrder.MatchedUser` (keep `MatchedUserId` FK only), `TicketAttendee.MatchedUser` (keep `MatchedUserId` FK only)

### Current violations

Observed in this section's service code as of 2026-04-15:

- **Cross-domain `.Include()` calls:**
  - `TicketQueryService.cs:570-572` — `.Include(u => u.Profile).Include(u => u.UserEmails).Include(u => u.TeamMemberships).ThenInclude(tm => tm.Team)` on `Users` (Profiles + Teams traversal)
  - `TicketQueryService.cs:339-340` — `.Include(c => c.Grants).ThenInclude(g => g.Code / g.User)` on `Campaign` (Campaigns + Users traversal)
  - `TicketQueryService.cs:699` — `.Include(o => o.MatchedUser)` (Users)
  - `TicketQueryService.cs:768` — `.Include(a => a.MatchedUser)` (Users)
- **Cross-section direct DbContext reads:**
  - `TicketQueryService.cs:337` — `_dbContext.Set<Campaign>()` (Campaigns section)
  - `TicketQueryService.cs:569` — `_dbContext.Users` (Users/Identity section)
  - `TicketQueryService.cs:584` — `_dbContext.EventSettings` (Shifts section)
  - `TicketQueryService.cs:588` — `_dbContext.EventParticipations` (Shifts section)
  - ~~`TicketSyncService.cs:440` — `_dbContext.EventSettings` (Shifts section)~~ **Resolved in PR #545c (2026-04-22):** active-event lookup now routes through `IShiftManagementService.GetActiveAsync()`.
  - ~~`TicketSyncService.cs:449,481` — `_dbContext.EventParticipations` (Shifts section)~~ **Resolved in PR #545c (2026-04-22):** participation reads/writes now route through `IUserService` (User section owns `event_participations` per PR #243).
- **Within-section cross-service direct DbContext reads:**
  - ~~`TicketingBudgetService.cs:44` — reads `_dbContext.TicketOrders` directly.~~ **Resolved in PR #545b (2026-04-22):** `TicketingBudgetService` was migrated to `Humans.Application.Services.Tickets` and now reads paid orders through the narrow `ITicketingBudgetRepository` (Tickets-side). No cross-section DbContext reads remain on the Budget→Tickets bridge.
- **Inline `IMemoryCache` usage in service methods:**
  - `TicketQueryService.cs:38,42` — direct `_cache.TryGetExistingValue` / `_cache.Set` around ticket counts
  - `TicketQueryService.cs:81` — `_cache.GetOrCreateAsync(CacheKeys.UserIdsWithTickets, ...)`
  - `TicketQueryService.cs:132-133` — `_cache.Remove(...)` / `_cache.InvalidateTicketCaches()` invalidation scattered in service
- **Cross-domain nav properties on this section's entities:**
  - `TicketOrder.MatchedUser` → `User` (Users/Identity)
  - `TicketAttendee.MatchedUser` → `User` (Users/Identity)

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- When touching `TicketQueryService` user-matching code (around lines 569-572, 697-699, 767-769), do not add new `.Include(... MatchedUser ...)` or new traversals off `User`. Fetch via `IUserService` / `IProfileService` and project into Ticket DTOs in-memory by `MatchedUserId`.
- When touching participation/event logic in `TicketQueryService.cs:584-588`, route through a Shifts-owned interface (`IEventSettingsService` / `IEventParticipationService`) rather than adding more `_dbContext.EventSettings` / `_dbContext.EventParticipations` reads. (`TicketSyncService` already routes these through `IShiftManagementService` / `IUserService` as of PR #545c.)
- When touching code tracking (`TicketQueryService.GetCodeTrackingDataAsync`, ~line 337), do not deepen the `Campaign` / `Grants` / `User` include chain; call `ICampaignService` for campaign + grant data and correlate with local ticket orders in memory.
- When touching cache logic around ticket counts (`TicketQueryService.cs:38-42,81,132-133`), keep cache calls confined to this service — do not push `IMemoryCache` into controllers or view components — and prefer adding to `CacheKeys.InvalidateTicketCaches()` over sprinkling new `_cache.Remove` sites, so the eventual caching decorator has a single seam to replace.
- When extending `TicketingBudgetService`, add new Tickets-side read methods to `ITicketingBudgetRepository` (narrow, Tickets-owned) rather than reaching into `HumansDbContext`. Projection/line-item writes remain Budget-owned and must route through `IBudgetService`.
