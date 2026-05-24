<!-- freshness:triggers
  src/Humans.Application/Services/Tickets/**
  src/Humans.Application/Interfaces/Tickets/**
  src/Humans.Application/TicketOrderInfo.cs
  src/Humans.Domain/Entities/TicketOrder.cs
  src/Humans.Domain/Entities/TicketAttendee.cs
  src/Humans.Domain/Entities/TicketSyncState.cs
  src/Humans.Domain/Entities/TicketTransferRequest.cs
  src/Humans.Domain/Constants/TicketConstants.cs
  src/Humans.Infrastructure/Data/Configurations/Tickets/**
  src/Humans.Infrastructure/Services/Tickets/**
  src/Humans.Web/Controllers/TicketController.cs
  src/Humans.Web/Controllers/TicketTransferController.cs
  src/Humans.Web/Controllers/TicketsContactsAdminController.cs
-->
<!-- freshness:flag-on-change
  Vendor sync flow, Stripe-fee enrichment, auto-matching to humans by email, and EventParticipation derivation rules — review when Tickets services/entities/controller change.
-->

# Tickets — Section Invariants

External ticket vendor sync (orders + attendees), Stripe-fee enrichment, auto-matching to humans by email, event-participation derivation.

## Concepts

- **Ticket Orders** and **Ticket Attendees** are records synced from an external ticket vendor (Ticket Tailor in production, an in-process stub in dev). They are not manually created in the system.
- A **Ticket Order** represents a purchase (one per transaction). It carries the gross total, currency, vendor discount/donation line-item amounts, computed VAT (using VIP-split logic, not the vendor's tax line), and is enriched with Stripe fee data (payment method, Stripe fee, application fee) during sync via `IStripeService.GetPaymentDetailsAsync` keyed by the vendor's payment-intent id.
- A **Ticket Attendee** represents an individual ticket holder (one per issued ticket, multiple per order). Tickets above `TicketConstants.VipThresholdEuros` (315 EUR) are treated as VIP — the portion above the threshold is a VAT-free donation, the portion at-or-below is taxable at `TicketConstants.VatRate` (10%) inclusive.
- **Auto-matching** links orders to humans by buyer email and attendees to humans by attendee email. The lookup runs only against **verified** `UserEmails` rows under `NormalizingEmailComparer` so gmail/googlemail aliases collapse. A normalized verified email is supposed to be owned by exactly one user — if a normalized email maps to multiple verified users (data-integrity error, should not happen), the email is left unmatched and a `LogError` is emitted. Unverified emails never participate in matching.
- **Ticket Sync** is a background Hangfire job (`TicketSyncJob`, every 15 min by default, configurable via `TicketVendor:SyncIntervalMinutes`) that pulls order and attendee data from the vendor through `ITicketVendorService`.
- **TicketTransferRequest** is a Sender-initiated request to send an issued ticket to another Humans user (the Receiver). Lifecycle: `Pending → Approved | Rejected | Cancelled`. **The ticket team processes the void+reissue manually in the TicketTailor dashboard** — Humans never calls the vendor for a transfer. `Approved` = "marked successful" by the team; `Rejected` = "cancelled with a reason"; `Cancelled` = Sender self-cancel. The next ticket sync reconciles local attendee rows. Request → emails Sender + tickets@; decision → emails Sender + Receiver. (The former automated void+reissue engine was removed; its `VendorResult`/`VendorMessage`/`NewVendorTicketId`/`VendorStepsJson` columns linger as dormant storage pending a post-soak drop PR — see [`memory/architecture/no-drops-until-prod-verified.md`](../../memory/architecture/no-drops-until-prod-verified.md).)
- **Vendor connector** is a thin Infrastructure adapter behind `ITicketVendorService`. Production binds `TicketTailorService` (HTTP client against `https://api.tickettailor.com/v1`); non-production binds `StubTicketVendorService` (deterministic in-memory fixture with ~450 orders / ~600 tickets).
- **Attendee Contact Import** is a manually-triggered admin job (`IAttendeeContactImportService`) that creates a no-profile Humans user for each unmatched ticket attendee whose email doesn't already resolve to an existing UserEmail. Mirrors the Mailer import's plan/apply shape with squatter protection (unverified UserEmail rows are deleted before a fresh verified row is created for the new user). Decoupled from the sync today; Phase 2 will run it automatically at the end of each `TicketSyncService` run.

## Data Model

### TicketOrder

**Table:** `ticket_orders`

Ticket purchase order synced from vendor (one per purchase). Vendor-agnostic identity is `VendorOrderId`; payment-intent linkage is `StripePaymentIntentId` (from the vendor's `txn_id`). Stripe enrichment fields (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) are filled in by `EnrichOrdersWithStripeDataAsync` after the upsert and preserved across re-syncs. `DonationAmount` (standalone vendor donation line items, VAT-exempt) and `DiscountAmount` (absolute value of vendor `gift_card` line items) come from the vendor; `VatAmount` is recomputed locally via VIP-split logic — the vendor's tax line is intentionally ignored because Ticket Tailor mis-applies 10% to the full ticket price.

Cross-domain nav `TicketOrder.MatchedUser → MatchedUserId` (Users/Identity). Target: strip nav, keep FK only.
Aggregate-local: `TicketOrder.Attendees`.

### TicketAttendee

**Table:** `ticket_attendees`

Individual ticket holder (issued ticket, multiple per order). Vendor-agnostic identity is `VendorTicketId`. `AttendeeEmail` is nullable — some vendor flows don't capture per-ticket email; in that case `MatchedUserId` stays null. `Status` is normalized from the vendor string into `TicketAttendeeStatus` (`Valid` / `CheckedIn` / `Void`). Only `Valid` and `CheckedIn` count as revenue or as ticket coverage.

Cross-domain nav `TicketAttendee.MatchedUser → MatchedUserId`. Target: strip nav, keep FK only.
Aggregate-local: `TicketAttendee.TicketOrder`.

### TicketSyncState

**Table:** `ticket_sync_states`

Singleton (`Id` always 1) tracking ticket sync operational state. `VendorEventId` records the event currently being synced. `LastSyncAt` doubles as the resume cursor passed to the vendor's `updated_at.gte` filter on the next run. `SyncStatus` is `Idle` / `Running` / `Error`; if a sync is found stuck in `Running` for >30 min, `GetDashboardStatsAsync` auto-resets it to `Error` with a stale-state message. `FullResync` clears `LastSyncAt` so the next run pulls all orders again.

### TicketTransferRequest

**Table:** `ticket_transfer_requests`

Sender-initiated transfer request. `OriginalTicketAttendeeId` FK → `ticket_attendees`. `SenderUserId` / `ReceiverUserId` FK → users. `ReceiverLegalName` (snapshot of `Profile.FullName`) and `ReceiverEmail` (snapshot of primary email) are captured at request time and used by the ticket team + notification emails. `Status` is `TicketTransferStatus` (`Pending` / `Approved` / `Rejected` / `Cancelled`). `DecidedByUserId` FK → users (the admin who decided). `AdminNotes` is the free-text success note or the required cancellation reason. `RequestedAt` / `DecidedAt` are UTC timestamps. **Dormant columns** `VendorResult` / `VendorMessage` / `NewVendorTicketId` / `VendorStepsJson` remain from the removed automated-writeback engine and are no longer read or written; a follow-up PR drops them post-soak.

**Indexes:** `(SenderUserId, Status)` for the homepage card; `Status` for the admin queue. No uniqueness constraints — multiple Pending transfers per attendee are allowed.

**Cross-section FKs:** `SenderUserId`, `ReceiverUserId`, `DecidedByUserId` → Users (FK only, no nav).

### EventParticipation (derived, not owned)

`event_participations` is owned by the User section (per peterdrier/Humans PR #243). The Tickets section *derives* its `TicketSync`-sourced rows during each sync — see Triggers below. Tickets must never query or mutate the table directly.


## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated user with attendees on their orders (Sender) | Send any `Valid` attendee from their own order to another Humans user; cancel a `Pending` transfer they created |
| TicketAdmin, Board, Admin | View the ticket dashboard, orders, attendees, codes, gate list, sales aggregates, and the "Who Hasn't Bought" report (controller-wide policy `TicketAdminBoardOrAdmin`) |
| TicketAdmin, Admin | Trigger an incremental ticket sync. Export attendee/order CSV. Generate discount codes for campaigns (Campaign section, policy `TicketAdminOrAdmin`). Approve or reject pending transfer requests from `/Tickets/Admin/Transfers` (policy `TicketAdminOrAdmin`). Import attendee contacts (preview + selectively apply) from `/Tickets/Admin/Contacts` (policy `TicketAdminOrAdmin`) |
| Admin | Trigger a full re-sync (clears the `LastSyncAt` cursor). Open and submit the participation backfill page (`/Tickets/Participation/Backfill`) |

## Invariants

- Ticket orders and attendees are synced from the external vendor — they cannot be manually created or edited from this app.
- Stripe enrichment (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) is preserved across re-syncs and only re-run for orders that have a `StripePaymentIntentId` and are still missing fee data; if `IStripeService.IsConfigured` is false the pass is silently skipped.
- VAT is computed locally per order using VIP-split logic on attendees with `Status` in (`Valid`, `CheckedIn`); orders not in `Paid` status carry `VatAmount = 0`. The vendor's tax line is intentionally ignored.
- All `/Tickets` dashboard aggregate metrics (`TicketsSold`, `Revenue`, `NetRevenue`, fee totals, `UnmatchedOrderCount`, the daily-sales chart, the per-payment-method fee breakdown) are computed only over orders with `PaymentStatus == Paid`; ticket counts within those are further restricted to attendees with `Status` in (`Valid`, `CheckedIn`). Refunded/Cancelled/Pending orders are still synced and visible on `/Tickets/Orders` (with the new Status column on Recent Orders) but never contribute to dashboard totals. The "unmatched orders" badge links to `/Tickets/Orders?filterMatched=false&filterPaymentStatus=Paid` so the count and the drill-down agree.
- Auto-matching uses normalized email comparison (`NormalizingEmailComparer`) against verified UserEmails rows only. Collisions among verified rows (a data-integrity error, should not happen) leave the email unmatched and emit `LogError`; nobody gets the ticket match. Buyer match writes `TicketOrder.MatchedUserId`; attendee match writes `TicketAttendee.MatchedUserId` independently.
- A user "has a ticket" iff at least one `Valid` or `CheckedIn` `TicketAttendee` is matched to their `UserId`. Buyer-only matches do not count — purchasing tickets for others does not give the buyer ticket coverage.
- Only `Valid` attendees can be sent.
- Receiver is chosen through the standard `<vc:human-search>` picker (`scope=Name`, `allow-email=true`): burner-name search, or an exact case-insensitive verified-email match (`IUserEmailService.GetUserIdByExactEmailAsync`) returning at most one person. The recipient must resolve to a Humans user with a legal name. Receivers may already hold other tickets — allowed.
- Sender cannot send to themselves.
- Admin decisions make **no vendor call and no attendee mutation**: "Mark transfer successful" sets `Approved`; "Cancel transfer" requires a reason and sets `Rejected`. The ticket team performs the void+reissue manually in TicketTailor, and the next ticket sync reconciles `ticket_attendees`.
- Request creation emails the Sender + `tickets@nobodies.team`; a decision emails the Sender + Receiver.
- `TicketSyncState` is a singleton row (Id = 1). `LastSyncAt` is the resume cursor passed back to the vendor as `updated_at.gte` on the next run. A sync stuck in `Running` for >30 minutes is auto-reset to `Error` by `GetDashboardStatsAsync` (crash recovery).

## Negative Access Rules

- Board **cannot** trigger any sync (incremental or full), export CSV, or open the participation backfill page.
- Board **cannot** approve or reject transfer requests — transfer review is gated by `TicketAdminOrAdmin`. Board can view ticket data but the transfer side-effects (vendor void+reissue, local attendee mutation) are admin-only.
- Board **cannot** trigger attendee contact import — same `TicketAdminOrAdmin` gate as the sync.
- TicketAdmin **cannot** trigger a Full Re-sync or open the participation backfill page (both `AdminOnly`).
- Nobody can edit ticket configuration (vendor `EventId`, API key, sync interval) from inside the app — those values come from `appsettings`'s `TicketVendor` section and the `TICKET_VENDOR_API_KEY` environment variable, set at deploy time.
- Regular humans have no access to `/Tickets/*` (dashboard, orders, attendees, codes, gate list, who-hasn't-bought, sales aggregates) — the controller-wide policy is `TicketAdminBoardOrAdmin`.
- A user **cannot** send an attendee they do not own (the order's `MatchedUserId` must equal the Sender's user id; validated in `TicketTransferService.CreateRequestAsync`).

## Routing

| Route | Method | Auth Policy | Purpose |
|-------|--------|-------------|---------|
| `/Tickets` | GET | `TicketAdminBoardOrAdmin` | Summary dashboard |
| `/Tickets/Orders` | GET | `TicketAdminBoardOrAdmin` | Paginated order list |
| `/Tickets/Attendees` | GET | `TicketAdminBoardOrAdmin` | Paginated attendee list |
| `/Tickets/Codes` | GET | `TicketAdminBoardOrAdmin` | Discount code redemption tracking |
| `/Tickets/GateList` | GET | `TicketAdminBoardOrAdmin` | Gate list |
| `/Tickets/WhoHasntBought` | GET | `TicketAdminBoardOrAdmin` | Active Volunteers without a ticket |
| `/Tickets/SalesAggregates` | GET | `TicketAdminBoardOrAdmin` | Weekly + quarterly aggregate reports |
| `/Tickets/Sync` | POST | `TicketAdminOrAdmin` | Trigger incremental sync |
| `/Tickets/FullResync` | POST | `AdminOnly` | Trigger full re-sync |
| `/Tickets/Admin/Contacts` | GET | `TicketAdminOrAdmin` | Preview attendee-contact-import plan |
| `/Tickets/Admin/Contacts/Apply` | POST | `TicketAdminOrAdmin` | Apply selected attendees |
| `/Tickets/Participation/Backfill` | GET + POST | `AdminOnly` | CSV import of participation records |
| `/Tickets/Export/Attendees` | GET | `TicketAdminOrAdmin` | CSV export of attendees |
| `/Tickets/Export/Orders` | GET | `TicketAdminOrAdmin` | CSV export of orders |
| `/Welcome` | GET | `[AllowAnonymous]` | Post-purchase landing page (WelcomeController) |

`/Welcome` is an intentional post-purchase landing route owned by Tickets logic while physically handled by `WelcomeController` in `Humans.Web`; it is documented here to avoid it being treated as a routing boundary drift in future alignments.

## Triggers

- When ticket sync runs: vendor orders and issued tickets are upserted into `ticket_orders` / `ticket_attendees` (existing rows keyed by `VendorOrderId` / `VendorTicketId` retain their `Id` and their already-enriched fields), Stripe fees are enriched for newly-paid orders, VAT is recomputed for every order, vendor discount codes are matched to `CampaignGrants` via `ICampaignService.MarkGrantsRedeemedAsync`, and event participation is reconciled. On success, `LastSyncAt` is set to the start-of-sync instant; `_cache.Remove(CacheKeys.TicketEventSummary(eventId))` drops the per-event vendor summary owned by `TicketTailorService`, and `ITicketCacheInvalidator.InvalidateAll()` drops the decorator's per-order `TicketOrderInfo` projection (which forces re-warm from the freshly-upserted rows on the next read). Per-user `UserTicketCount:{userId}` and `UserTicketHoldings:{userId}` entries are intentionally excluded from `InvalidateAll` — they have no enumerable key set and expire naturally via their 5-minute TTL.
- `EventParticipation` derivation (Ticket Tailor's active vendor event only, scoped to `EventSettings.Year` from `IShiftManagementService.GetActiveAsync`):
  - For each user with at least one matched attendee: any `CheckedIn` ticket → `ParticipationStatus.Attended`; otherwise any `Valid` ticket → `ParticipationStatus.Ticketed`. Both write through `IUserService.SetParticipationFromTicketSyncAsync` with `ParticipationSource.TicketSync`.
  - For each prior `(TicketSync, Ticketed)` row in the year: if the user no longer has any `Valid`/`CheckedIn` matched ticket, the row is removed via `IUserService.RemoveTicketSyncParticipationAsync`. `Attended` rows are never removed by sync — being checked in is permanent.
  - Self-declared `NotAttending` rows are owned by the User section and are never overwritten or removed by ticket sync; ticket purchase does not flip a user's prior `NotAttending` declaration in this section's code path.
- "Who Hasn't Bought" lists active Volunteers-team members (via `ITeamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers)`) minus those whose current-year `EventParticipation.Status` is `NotAttending`. `HasTicket` is true when the user appears in the union of matched attendee user-ids and matched order user-ids (see `GetAllMatchedUserIdsAsync`).
- The Volunteer Ticket Coverage card on `/Tickets` divides matched-attendee Volunteers (`UserIdsWithTickets`) by total active Volunteers — buyer-only matches are excluded by construction.
- Code redemption: vendor discount codes attached to orders are pushed back to Campaigns via `ICampaignService.MarkGrantsRedeemedAsync` so each `CampaignGrant.RedeemedAt` reflects the order's `PurchasedAt`.
- When an account merge accepts, `TicketSyncService.ReassignAsync` (via `IUserMerge`) re-FKs `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and the new `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId` columns from source to target, then calls `ITicketCacheInvalidator.InvalidateAfterUserMerge(sourceUserId, targetUserId)` to drop the projection AND both users' per-user TTL entries (`UserTicketCount`, `UserTicketHoldings`). The per-user eviction is the T-07 fix for an earlier gap where merged users' homepage tickets card and ticket-holdings widget could lag up to 5 minutes after the fold. `AccountMergeService.FoldAsync` fans out across all `IUserMerge` registrations; the Tickets section participates because `TicketSyncService` implements `IUserMerge`.
- Audit actions written by ticket transfer: `TicketTransferRequested` (on `CreateRequestAsync`), `TicketTransferCancelled` (on `CancelAsync`), `TicketTransferApproved` (on `ApproveAsync` — includes vendor outcome in metadata), `TicketTransferRejected` (on `RejectAsync`).
- On transfer decision (approve = mark successful, or cancel with reason): only the request row changes (`Status`, `DecidedByUserId`, `DecidedAt`, `AdminNotes`) and Sender + Receiver are emailed. No vendor call, no attendee mutation — the team's manual TicketTailor void/reissue is reconciled by the next ticket sync.
- On attendee contact import apply: for selected unmatched attendees, `MatchedUserId` is set (via `UpsertAttendeesAsync`), new users are provisioned (via `IAccountProvisioningService` with `ContactSource.TicketTailor`, Stub Profile + verified `UserEmail`), squatter unverified rows are deleted first, `EventParticipation(Ticketed, TicketSync)` is written for each newly-matched user, ticket caches are invalidated via `ITicketQueryService.InvalidateAfterContactImport`, and a single `AuditAction.TicketContactsImported` row records the summary.

### TicketDashboardStats cache (ghost cache key)

`CacheKeys.TicketDashboardStats` is **invalidator-only**. `TicketQueryService.GetDashboardStatsAsync` is the canonical producer of the `TicketDashboardStats` DTO and is called fresh on every `TicketController.Index` request — `CachingTicketQueryService.GetDashboardStatsAsync` is a pass-through to the inner. The cache key and the `Metadata` row (5 min, Static) exist so a future caching wrapper can be added without renaming things. Treat this as a documented placeholder, not a live cache; see `docs/architecture/service-data-access-map.md` for the cross-cutting rule.

## Cross-Section Dependencies

- **Campaigns:** `ICampaignService` — Tickets reads campaign + grant data for the Codes page (`GetCodeTrackingAsync`) and pushes redemptions back during sync (`MarkGrantsRedeemedAsync`). Discount-code *generation* lives in the Campaigns section's `CampaignController` (which calls `ITicketVendorService.GenerateDiscountCodesAsync` directly); the `/Tickets/Codes` page only reports redemption status, it does not create codes.
- **Users/Identity:** `IUserService` — `GetAllUsersAsync` / `GetByIdsAsync` for stitching matched-user names into orders/attendees lists; `SetParticipationFromTicketSyncAsync` / `RemoveTicketSyncParticipationAsync` / `GetAllParticipationsForYearAsync` / `BackfillParticipationsAsync` for derived `EventParticipation` writes (User section owns `event_participations` per peterdrier/Humans PR #243). `IAccountProvisioningService.FindOrCreateUserByEmailAsync` is consumed by `AttendeeContactImportService` to provision Humans users for unmatched ticket attendees.
- **Profiles:** `IUserEmailService` — `GetAllUserEmailLookupEntriesAsync` builds the sync-time email→userId index; `GetVerifiedEmailsForUserAsync` and `SearchUserIdsByVerifiedEmailAsync` back the per-user ticket probe and the Who-Hasn't-Bought email search; `GetNotificationEmailsByUserIdsAsync` hydrates the report; `GetUserIdByExactEmailAsync` resolves transfer recipients by exact email; `GetPrimaryEmailAsync` snapshots recipient email at request creation time. `IProfileService.GetByUserIdsAsync` supplies `MembershipTier`; `IProfileService.SearchHumansByNameAsync` (filtered to `MatchField == "Burner Name"`) resolves transfer recipients by burner name. Called by `IAccountMergeService` (Profiles section) — `ITicketSyncService.ReassignToUserAsync` re-FKs `TicketOrder.MatchedUserId` and `TicketAttendee.MatchedUserId` during account merge fold.
- **Teams:** `ITeamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers)` for the Volunteers cohort used by both the dashboard's coverage card and the Who-Hasn't-Bought list; `GetActiveNonSystemTeamNamesByUserIdsAsync` for team labels on the report.
- **Shifts:** `IShiftManagementService.GetActiveAsync` — active-event lookup for the year used by `EventParticipation` derivation and by the `Participation/Backfill` page (replaces the prior direct `EventSettings` read, PR #545c).
- **Budget:** `IBudgetService` — `GetActiveYearAsync` + `ComputeBudgetSummary` feed the dashboard's break-even calculation; `TicketingBudgetService` bridges paid-order data into Budget's projection writes via `ITicketingBudgetRepository` (Tickets-owned narrow read surface) without ever touching `budget_*` tables directly.
- **GDPR:** `TicketQueryService` implements `IUserDataContributor`, contributing the `TicketOrders` and `TicketAttendeeMatches` slices to the per-user data export.
- **Stripe (Infrastructure):** `IStripeService.GetPaymentDetailsAsync` looks up payment-intent details to populate `PaymentMethod` / `PaymentMethodDetail` / `StripeFee` / `ApplicationFee` per order. Configuration is via `STRIPE_TICKETS_KEY` env var; if unset, enrichment is skipped silently and the dashboard's fee breakdown stays empty.
- **Profiles (account merge):** `TicketSyncService` implements `IUserMerge`; `AccountMergeService.FoldAsync` fans out across all `IUserMerge` registrations, calling `TicketSyncService.ReassignAsync` which delegates to `ITicketRepository.ReassignToUserAsync` to re-FK `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId`.
- **Users/Identity (transfer):** `IUserService.GetByIdAsync` — recipient validation and display-name resolution for transfer requests (extends the existing `IUserService` dependency).
- **Audit (transfer):** `IAuditLogService.LogAsync` — four new actions: `TicketTransferRequested`, `TicketTransferCancelled`, `TicketTransferApproved`, `TicketTransferRejected` (existing Audit dependency, extended).

## Architecture

**Owning services (all in `Humans.Application.Services.Tickets`):**
- `TicketQueryService` — read-side dashboard / orders / attendees / codes / who-hasn't-bought / sales aggregates / per-user ticket probes; also implements `IUserDataContributor` for GDPR export.
- `TicketSyncService` — vendor sync orchestrator (orders + attendees upsert, Stripe enrichment, VAT compute, code redemption push, EventParticipation derivation); also implements `IUserMerge` so account merges re-FK `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId`.
- `TicketTransferService` — transfer request lifecycle: `GetMyAttendeesAsync`, `GetConfirmationAsync`, `CreateRequestAsync`, `CancelAsync`, `ApproveAsync` (mark successful — no vendor call), `RejectAsync` (cancel with reason). Emails Sender + tickets@ on request, Sender + Receiver on decision; never calls the vendor.
- `TicketingBudgetService` — Tickets→Budget bridge (feeds completed-week paid-sales totals into Budget projections via `IBudgetService`).
- `AttendeeContactImportService` — manually-triggered admin job that classifies unmatched ticket attendees and provisions Humans users for them via `IAccountProvisioningService` (plan + apply pattern mirroring the Mailer import; squatter protection deletes unverified UserEmail rows before creating fresh verified ones).

**Owned tables:** `ticket_orders`, `ticket_attendees`, `ticket_sync_states`, `ticket_transfer_requests`

**Authorization note:** transfer authorization is **service-level** — `TicketTransferService` validates ownership and state (e.g. requester owns the attendee, attendee is `Valid`, one pending at a time) in `CreateRequestAsync`, `CancelAsync`, etc. No dedicated `AuthorizationHandler` was added: the controller surface is small and the service guards are sufficient. If a non-controller surface (CLI, internal API) is added in a future PR, a `TicketTransferAuthorizationHandler` should be introduced then.

**Vendor connectors (Infrastructure-only, behind `ITicketVendorService`):**
- `TicketTailorService` — production HTTP client; bound when `IHostEnvironment.IsProduction()`.
- `StubTicketVendorService` — deterministic in-memory fixture; bound in dev/QA/preview. The stub fills in placeholder `EventId`/`ApiKey` so `TicketVendorSettings.IsConfigured` returns true even without env vars.

**Stripe connector (Infrastructure-only):** `StripeService` (`IStripeService`) wraps the Stripe SDK and is consumed by `TicketSyncService` for fee enrichment.

**Status:** (A) Fully §15-compliant. All three section services live in `Humans.Application.Services.Tickets` and route every database read/write through `ITicketRepository` (or, for the Budget bridge, `ITicketingBudgetRepository`). Neither `TicketQueryService.cs`, `TicketSyncService.cs`, nor `TicketingBudgetService.cs` imports `Microsoft.EntityFrameworkCore` or references `HumansDbContext`. Umbrella issue nobodies-collective/Humans#545 closed by sub-tasks #545a (TicketQueryService → Application), #545b (TicketingBudgetService + `ITicketingBudgetRepository`), #545c (TicketSyncService → Application + `IShiftManagementService` / `IUserService` routing). `ITicketVendorService` connector split landed in peterdrier/Humans PR #277.

**Caching:** the §15 caching decorator pattern is applied (T-07, 2026-05-16). `CachingTicketQueryService` (Infrastructure, Singleton) wraps a keyed-inner `TicketQueryService` (Application, Scoped) and composes a nested `OrdersCache : TrackedCache<Guid, TicketOrderInfo>` (with `warmOnStartup: true`) that owns the per-order `TicketOrderInfo` projection — attendees embedded — refreshed wholesale on every section-level invalidation event. Composition (multiple inner caches with different shapes) precludes inheriting `TrackedCache` directly, so the decorator implements `IHostedService` itself and forwards `StartAsync` to the orders cache; the inner cache is not registered as a hosted service to avoid double-warmup. Per-user `UserTicketCount` and `UserTicketHoldings` entries stay as a separate 5-minute-TTL `IMemoryCache`-backed concern owned by the decorator (NOT absorbed into the main projection). The inner is cache-free; an architecture test pins no `IMemoryCache` in the inner's constructor.

**Architecture tests:**
- `tests/Humans.Application.Tests/Architecture/TicketQueryArchitectureTests.cs` — pins namespace, no DbContext, `ITicketRepository` required, cross-section reads via service interfaces only, **no `IMemoryCache` on the inner** (T-07), and the decorator implements both `ITicketQueryService` and `ITicketCacheInvalidator`.
- `tests/Humans.Application.Tests/Architecture/TicketSyncArchitectureTests.cs` — pins namespace, no DbContext, `ITicketRepository` + `ITicketVendorService` + `IUserService` + `ICampaignService` + `IShiftManagementService` all required; no Store dependency.
- `tests/Humans.Application.Tests/Architecture/TicketingBudgetArchitectureTests.cs` — pins namespace, no DbContext, no `IMemoryCache`, `ITicketingBudgetRepository` + `IBudgetService` required.
- `tests/Humans.Application.Tests/Architecture/TicketVendorArchitectureTests.cs` — pins `ITicketVendorService` in Application, no forbidden namespace leakage into signatures, `TicketTailorService` and `StubTicketVendorService` in Infrastructure.
- `tests/Humans.Application.Tests/Architecture/AttendeeContactImportArchitectureTests.cs` — pins `AttendeeContactImportService` namespace, no DbContext, and required cross-section dependencies (`ITicketRepository`, `IUserEmailService`, `IAccountProvisioningService`, `IUserService`, `IShiftManagementService`, `ITicketQueryService`, `IAuditLogService`).

### Repositories

- **`ITicketRepository`** (Tickets-owned) — owns reads/writes for `ticket_orders`, `ticket_attendees`, `ticket_sync_states`. Aggregate-local navs kept (`TicketOrder.Attendees`, `TicketAttendee.TicketOrder`). Cross-domain `MatchedUser` navs are still present on the entities and are not currently `Include`-d by any repo method — joining to `User` is done in-memory via `IUserService.GetByIdsAsync` after the read. Stripping the navs entirely is the only outstanding §15 cleanup for this section.
- **`ITicketingBudgetRepository`** (Tickets-owned, consumed by `TicketingBudgetService`) — narrow read surface for paid-order projections.

### Touch-and-clean guidance

- New cross-section data needs always go through the owning section's interface — `ICampaignService`, `IUserService`, `IProfileService`, `IUserEmailService`, `ITeamService`, `IShiftManagementService`, `IBudgetService`. Do not add `Include`-chains off `MatchedUser` to `ITicketRepository` even though the navs still exist; project in memory by `MatchedUserId`.
- `IMemoryCache` is owned by `CachingTicketQueryService` (the decorator) only. The inner `TicketQueryService` is cache-free per T-07. `TicketSyncService` retains an `IMemoryCache` injection solely to evict the per-event `TicketEventSummary:{eventId}` entry owned by the `TicketTailorService` vendor connector; for everything else it talks to the decorator via `ITicketCacheInvalidator`. Other Tickets-section services (e.g. `TicketTransferService`) that need to invalidate after a write call `ITicketQueryService.InvalidateAfterTransfer(senderUserId, receiverUserId)` instead of touching `IMemoryCache` directly. Do not push `IMemoryCache` into controllers, view components, or other domain services. New invalidation seams go on `ITicketCacheInvalidator` (not `ITicketQueryService`) so the budgeted query surface doesn't grow each time a new write site is added.
- The `TicketDashboardStats` cache key remains invalidator-only (see *TicketDashboardStats cache* under Triggers). The decorator doesn't read-through-cache that DTO; `GetDashboardStatsAsync` still hits the repository on each render — on-demand staleness on the dashboard during sync windows is currently acceptable.
- When extending the Tickets→Budget bridge, add new read methods to `ITicketingBudgetRepository` (Tickets-owned). Projection/line-item writes remain Budget-owned and must route through `IBudgetService`.
- The vendor split is doctrinal: business code talks to `ITicketVendorService` and never to "Ticket Tailor" directly. Any new vendor capability needs an interface method first, then a `TicketTailorService` impl plus a deterministic `StubTicketVendorService` impl so dev/preview environments still exercise the call.
