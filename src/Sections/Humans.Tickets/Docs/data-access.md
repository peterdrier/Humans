# Tickets — Data Access

## Tickets

Project: `src/Sections/Humans.Tickets` is split across **two projects
plus a contracts leaf**: `src/Sections/Humans.Tickets` (orders, attendees,
transfers, sync orchestration, the admin surface — everything `internal
sealed`), `src/Sections/Humans.Tickets.Contracts` (the leaf:
`ITicketServiceRead`, `ITicketSync`, `ITicketTransferQueue`,
`ITicketDiscountCodes`, `ITicketVendorMirror`), and
`src/Sections/Humans.TicketTailor` (the vendor adapter — the sole
implementation of the vendor port; owns no tables, publishes nothing, and
references `Humans.Tickets` directly to name the port).
`ITicketVendorService` (the vendor port itself) lives in
`src/Sections/Humans.Tickets/Contracts/`. **DbContext:** `TicketsDbContext`.
`TicketRepository` and `TicketTransferRepository` both inject
`IDbContextFactory<TicketsDbContext>` directly. Owns `TicketOrders`,
`TicketAttendees`, `TicketSyncStates`, `TicketTransferRequests`.

The section's public surface is small: `ITicketServiceRead` (2 members,
`[SurfaceBudget(2)]`), `ITicketSync` (2), `ITicketTransferQueue` (1),
`ITicketDiscountCodes` (1), `ITicketVendorMirror` (1). `TicketDashboardDtos`
(24 public types), the transfer wizard, and the admin decision DTOs are
`internal`. Campaigns' grant waves call `ITicketDiscountCodes.GenerateAsync`
(in the application's own `TicketDiscountCodeRequest`/`TicketDiscountKind`
vocabulary — `TicketVendorGateway` in `Humans.Tickets` maps that to the
port's `DiscountCodeSpec`/`DiscountType` at the edge), and
`GateVendorCheckInJob` mirrors a gate admission through
`ITicketVendorMirror` instead of the port. `TicketVendorPortArchitectureTests`
pins that only `Humans.Tickets` may inject `ITicketVendorService`,
its own `TicketVendorHealthCheck` included. `TicketStubViewComponent` lives in the
section's own `Contracts/` folder — its model names Tickets DTOs, so it
can't live in `Humans.UI`.

The read path is split: `TicketQueryService` is the **inner** read service,
registered keyed under `CachingTicketQueryService.InnerServiceKey`
(`"ticket-query-inner"`), and is wrapped by the Singleton
`CachingTicketQueryService` decorator (now `src/Sections/Humans.Tickets/Services/Stores/`).
The decorator is the registered
`ITicketService`, the budgeted cross-section `ITicketServiceRead`, and the
`ITicketCacheInvalidator`. External sections inject `ITicketServiceRead`
(two-method surface: `GetTicketOrdersAsync` + `GetUserTicketHoldingsAsync`)
rather than the full `ITicketService`. Tickets caching is entirely
`TrackedCache`-based: an orders slice (`Tickets.Orders`, warmed on startup)
and a user-holdings slice (`Tickets.UserHoldings`, lazy with a 5-minute
freshness deadline embedded in the cached value). The only `IMemoryCache`
key the section still uses is `TicketEventSummary:{eventId}`.

### TicketQueryService (Scoped, keyed `"ticket-query-inner"` — inner of CachingTicketQueryService)

Repositories: `ITicketRepository`, `ITicketTransferRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R |
| TicketSyncStates | R |
| TicketTransferRequests | R (approved transfers joined into the orders projection — void attendees carry recipient/decided-at) |

The inner service holds no cache — invalidation methods are no-ops on the
inner; `CachingTicketQueryService` intercepts. Cross-section calls via
`IBudgetService`, `ICampaignServiceRead` (read-split surface), `IUserService`,
`IUserEmailService`, `ITeamServiceRead` (read-split surface),
`IShiftManagementService`, plus `IClock`. Implements `IUserDataContributor`
(the GDPR contributor is the inner, one per section).

`ComputeUserTicketCountAsync` matches a user's tickets by fetching the
user's verified emails (`IUserEmailService.GetVerifiedEmailsForUserAsync`)
and the valid attendee emails (`ITicketRepository.GetValidAttendeeEmailsAsync`),
then intersecting them **in-memory** for case-consistent comparison — no
extra repository round-trip.

Email-to-user matching for ticket sync routes through
`IUserServiceRead.GetAllUserInfosAsync` (see
`TicketSyncService.BuildEmailLookupAsync`); no cross-section `UserEmails`
reads remain.

`TicketAttendees` has a `Barcode` column, synced from Ticket Tailor.
`TicketAttendeeInfo` in the cached orders projection carries `Barcode`
plus transfer detail (`TransferredToName` / `TransferredAt` for void
attendees, resolved from approved `TicketTransferRequests` via the
`ITicketTransferRepository` read in `TicketQueryService`). The admin
attendee search predicate in `TicketRepository` matches barcode alongside
name/email. The Scanner gate card (`ScannerController`) resolves a barcode
by filtering `ITicketServiceRead.GetTicketOrdersAsync` in memory.

`TicketAttendees` also has a `CheckedInAt` column — `TicketSyncService`
syncs vendor check-ins from the TicketTailor `/check_ins` endpoint so the
onsite roster and the Gate section see gate check-ins made directly at the
vendor. `TicketAttendeeInfo` in the cached orders projection carries
`CheckedInAt` alongside `Barcode`. Transfers of gate-checked-in tickets are
blocked — the transfer flow respects `CheckedInAt`. `TicketTransferService`
has an automated, flag-gated TicketTailor void(-to-hold)+reissue path
(`ProcessTransferAsync`) via `ITicketVendorService`.

### CachingTicketQueryService (Singleton, `Humans.Tickets.Services.Stores`)

| Cache | Type | Read | Write | Invalidate |
|-------|------|------|-------|------------|
| `TrackedCache<Guid, TicketOrderInfo>` (`Tickets.Orders`, warmed on startup) | Per-Entity | yes | yes (warm + lazy) | `ITicketCacheInvalidator` (clear-all on transfer / contact-import / merge / sync) |
| `TrackedCache<Guid, CachedUserTicketHoldings>` (`Tickets.UserHoldings`, lazy, 5-min freshness inside value) | Per-User | yes | yes (lazy load) | `ITicketCacheInvalidator` (per-user evict on transfer/merge; clear-all on contact import) |
| `TicketEventSummary:{eventId}` (`IMemoryCache`) | 15 min | (removed by `InvalidateVendorEventSummary`) | | `ITicketCacheInvalidator.InvalidateVendorEventSummary` |

Implements `ITicketService`, `ITicketServiceRead`, `ITicketCacheInvalidator`,
`IHostedService` (its `StartAsync` warms the orders slice). Resolves the keyed
Scoped inner per-call via `IServiceScopeFactory`. Both `TrackedCache`
instances are surfaced on `/Debug/CacheStats`.
`GetDashboardStatsAsync` is a straight pass-through to the inner (compute-only,
no read-through cache — see `TicketDashboardStats` note in the Cache Inventory).

### TicketSyncService (Scoped)

Repositories: `ITicketRepository`, `ITicketTransferRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R/W |
| TicketAttendees | R/W |
| TicketSyncStates | R/W |
| TicketTransferRequests | R |

| Cache Key | TTL | Read | Write | Invalidate |
|-----------|-----|------|-------|------------|
| `TicketEventSummary:{eventId}` (via `ITicketCacheInvalidator.InvalidateVendorEventSummary`) | 15 min | | | yes (per event) |
| `Tickets.Orders` / `Tickets.UserHoldings` tracked slices (via `ITicketCacheInvalidator`) | per-process | | | yes |

Cross-section calls via `ITicketVendorService`, `IStripeService`,
`IUserServiceRead`, `IUserService`, `ICampaignService`,
`IShiftManagementService`, `ITicketCacheInvalidator`. Implements
`ITicketSyncService`, `IUserMerge`. `BuildEmailLookupAsync` builds the
verified-email → user-id map by fanning out over `IUserServiceRead.GetAllUserInfosAsync`.

### TicketTransferService (Scoped)

Repositories: `ITicketRepository`, `ITicketTransferRepository`.

| Table | R/W |
|-------|-----|
| TicketOrders | R |
| TicketAttendees | R/W |
| TicketTransferRequests | R/W |

Cross-section calls via `IUserServiceRead`, `IUserEmailService`,
`IEmailService`, `IEmailMessageFactory`, `IAuditLogService`, plus
`ITicketVendorService` (`ProcessTransferAsync` runs the automated,
flag-gated TicketTailor void(-to-hold)+reissue; the next ticket sync
reconciles local attendee rows). Invalidates ticket caches via
`ITicketCacheInvalidator` (`InvalidateAfterTransfer`, called from
`ApproveAsync` — approval mutates the cached order projection's transfer
detail, so the orders slice and both users' holdings are evicted).
Transfers of gate-checked-in tickets are refused — `CheckedInAt` respected.
No `IMemoryCache` directly.

Tickets is also read from the outside by `TicketingBudgetService`, which
consumes `ITicketServiceRead.GetTicketOrdersAsync` to build the budget's
ticketing actuals. It is a Budget service and is documented in
[Budget's map](../../Humans.Budget/Docs/data-access.md).

### AttendeeContactImportService (Scoped)

Repository: `ITicketRepository`.

| Table | R/W |
|-------|-----|
| TicketAttendees | R |

Cross-section calls via `IUserEmailService`, `IAccountProvisioningService`,
`IUserService`, `IShiftManagementService`, `ITicketCacheInvalidator`,
`IAuditLogService`. Imports attendee contact data into the system; clears
ticket caches via `InvalidateAfterContactImport`. No `IMemoryCache` directly.

### OnsiteRosterService (Scoped)

No repository. "Who's onsite" roster orchestrator. Pure read
orchestration over `IUserServiceRead`, `IShiftManagementService`,
`ICampServiceRead`, `ITeamServiceRead`, `IRoleAssignmentService`. Implements
`IOnsiteRosterService`, `IApplicationService`. No direct DB access, no cache.

`TicketAttendeeOwnership` is a stateless helper (current-owner predicate),
no DI dependencies.

### TicketVendorGateway (Scoped)

No repository. Thin §15-compliant facade over `ITicketVendorService` (the
vendor port, `src/Sections/Humans.Tickets/Contracts/`) — same
shape as `GoogleTranslationService`. Implements `ITicketDiscountCodes` +
`ITicketVendorMirror` (`Humans.Tickets.Contracts`) so cross-section callers
(`CampaignService`'s discount-code grant waves) depend on the section's own
leaf rather than the raw vendor port. `GateVendorCheckInJob` (`Humans.Gate`)
is the other consumer, via `ITicketVendorMirror`. No DB access, no cache.

---


