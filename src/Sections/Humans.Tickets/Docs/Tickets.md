<!-- freshness:triggers
  src/Sections/Humans.Tickets/**
  src/Sections/Humans.Tickets.Contracts/**
  src/Sections/Humans.TicketTailor/**
  src/Sections/Humans.Tickets.Contracts/TicketConstants.cs
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
- **Ticket Sync** is a background Hangfire job (`TicketSyncJob`, every 5 min via `TicketVendor:SyncIntervalMinutes`) that pulls order, attendee, and gate check-in data from the vendor through `ITicketVendorService`.
- **TicketTransferRequest** is a Sender-initiated request to send an issued ticket to another Humans user (the Receiver). Lifecycle: `Pending → Approved | Rejected | Cancelled`. The ticket team either runs the **automated TicketTailor void(-to-hold)+reissue** (`ProcessTransferAsync` — the "Process transfer" button) or voids+reissues by hand and records the outcome (`ApproveAsync` = "mark successful"). Both are admin-only (`TicketAdminOrAdmin`). `Approved` = transferred; `Rejected` = "cancelled with a reason"; `Cancelled` = Sender self-cancel. The next ticket sync reconciles local attendee rows. Request → emails Sender + tickets@; decision → emails Sender + Receiver. (The automated path records its outcome in `VendorResult`/`VendorMessage`/`NewVendorTicketId`/`VendorHoldId`; the unused `VendorStepsJson` column stays dormant pending a post-soak drop PR — see [`memory/architecture/no-drops-until-prod-verified.md`](../../../../memory/architecture/no-drops-until-prod-verified.md).)
- **Vendor connector** is a thin Infrastructure adapter behind `ITicketVendorService`. Production binds `TicketTailorService` (HTTP client against `https://api.tickettailor.com/v1`); non-production binds `StubTicketVendorService` (deterministic in-memory fixture with ~450 orders / ~600 tickets).
- **Attendee Contact Import** is a manually-triggered admin job (`IAttendeeContactImportService`) that creates a no-profile Humans user for each unmatched ticket attendee whose email doesn't already resolve to an existing UserEmail. Mirrors the Mailer import's plan/apply shape with squatter protection (unverified UserEmail rows are deleted before a fresh verified row is created for the new user). Decoupled from the sync today; Phase 2 will run it automatically at the end of each `TicketSyncService` run.

## Data Model

### TicketOrder

**Table:** `ticket_orders`

Ticket purchase order synced from vendor (one per purchase). Vendor-agnostic identity is `VendorOrderId`; payment-intent linkage is `StripePaymentIntentId` (from the vendor's `txn_id`). Stripe enrichment fields (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) are filled in by `EnrichOrdersWithStripeDataAsync` after the upsert and preserved across re-syncs. `DonationAmount` (standalone vendor donation line items, VAT-exempt) and `DiscountAmount` (absolute value of vendor `gift_card` line items) come from the vendor; `VatAmount` is recomputed locally via VIP-split logic — the vendor's tax line is intentionally ignored because Ticket Tailor mis-applies 10% to the full ticket price.

Cross-domain nav `TicketOrder.MatchedUser` stripped (FK `MatchedUserId` retained; nav property removed). No FK-join in `ITicketRepository`; join to User is done in-memory via `IUserService.GetUserInfosAsync`.
Aggregate-local: `TicketOrder.Attendees`.

### TicketAttendee

**Table:** `ticket_attendees`

Individual ticket holder (issued ticket, multiple per order). Vendor-agnostic identity is `VendorTicketId`. `AttendeeEmail` is nullable — some vendor flows don't capture per-ticket email; in that case `MatchedUserId` stays null. `Status` is normalized from the vendor string into `TicketAttendeeStatus` (`Valid` / `CheckedIn` / `Void`). Only `Valid` and `CheckedIn` count as revenue or as ticket coverage. `Barcode` is the Ticket Tailor `issued_ticket.barcode` value (the code printed on the ticket and encoded in its QR), nullable, lazy-filled during sync; a Full Re-sync backfills existing rows. It is indexed and searchable (the `/Tickets/Attendees` search matches barcode alongside name and email). `CheckedInAt` is the gate-scan time sourced from the vendor's check-in resource (TicketTailor `/check_ins`) during sync — orthogonal to `Status` (a scanned ticket stays `Valid`; check-in is a separate vendor concept), write-once per ticket (earliest scan wins, never cleared by later syncs), null until checked in.

Cross-domain nav `TicketAttendee.MatchedUser` stripped (FK `MatchedUserId` retained; nav property removed). No FK-join in `ITicketRepository`; join to User is done in-memory via `IUserService.GetUserInfosAsync`.
Aggregate-local: `TicketAttendee.TicketOrder`.

**`TicketAttendeeInfo` read projection** (part of `TicketOrderInfo`): carries `Barcode` and `CheckedInAt` plus, for `Void` attendees, `TransferredToName` and `TransferredAt` sourced from the Approved `TicketTransferRequest` for the attendee (read via `ITicketTransferRepository` in `TicketQueryService.GetTicketOrdersAsync`). These fields are used by the Scanner ticket-lookup card (`/Scanner/Tickets`).

### TicketSyncState

**Table:** `ticket_sync_states`

Singleton (`Id` always 1) tracking ticket sync operational state. `VendorEventId` records the event currently being synced. `LastSyncAt` doubles as the resume cursor passed to the vendor's `updated_at.gte` filter on the next run. `SyncStatus` is `Idle` / `Running` / `Error`; if a sync is found stuck in `Running` for >30 min, `GetDashboardStatsAsync` auto-resets it to `Error` with a stale-state message. `FullResync` clears `LastSyncAt` so the next run pulls all orders again.

### TicketTransferRequest

**Table:** `ticket_transfer_requests`

Sender-initiated transfer request. `OriginalTicketAttendeeId` FK → `ticket_attendees`. `SenderUserId` / `ReceiverUserId` FK → users. `ReceiverLegalName` (snapshot of `Profile.FullName`) and `ReceiverEmail` (snapshot of primary email) are captured at request time and used by the ticket team + notification emails. `Status` is `TicketTransferStatus` (`Pending` / `Approved` / `Rejected` / `Cancelled`). `DecidedByUserId` FK → users (the admin who decided). `AdminNotes` is the free-text success note or the required cancellation reason. `RequestedAt` / `DecidedAt` are UTC timestamps. `VendorResult` / `VendorMessage` / `NewVendorTicketId` / `VendorHoldId` carry the automated void+reissue outcome (written by `ProcessTransferAsync` / `RetryReissueAsync`; `NotAttempted`/null for manual transfers); `VendorHoldId` retains the held seat from a void-to-hold whose reissue failed, enabling one-click **Retry reissue**. The unused `VendorStepsJson` column is dormant and drops in a follow-up PR post-soak.

**Indexes:** `(SenderUserId, Status)` for the homepage card; `Status` for the admin queue. No uniqueness constraints — multiple Pending transfers per attendee are allowed.

**Cross-section FKs:** `SenderUserId`, `ReceiverUserId`, `DecidedByUserId` → Users (FK only, no nav).

### EventParticipation (derived, not owned)

`event_participations` is owned by the User section (per peterdrier/Humans PR #243). The Tickets section *derives* its `TicketSync`-sourced rows during each sync — see Triggers below. Tickets must never query or mutate the table directly.


## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated user with attendees on their orders (Sender) | Send any `Valid` attendee from their own order to another Humans user; cancel a `Pending` transfer they created |
| TicketAdmin, Board, Admin | View the ticket dashboard, orders, attendees, codes, gate list, sales aggregates, and the "Who Hasn't Bought" report (controller-wide policy `TicketAdminBoardOrAdmin`) |
| TicketAdmin, Admin | Trigger an incremental ticket sync. Export attendee/order CSV. Generate discount codes for campaigns (Campaign section, policy `TicketAdminOrAdmin`). Approve or reject pending transfer requests from `/Tickets/Admin/Transfers` (policy `TicketAdminOrAdmin`). Import attendee contacts (preview + selectively apply) from `/Tickets/Admin/Contacts` (policy `TicketAdminOrAdmin`). Set/rotate the gate-terminal password from `/Tickets/Admin/Gate` (policy `TicketAdminOrAdmin`; see `docs/features/scanner/gate-terminal-login.md`) |
| Admin | Trigger a full re-sync (clears the `LastSyncAt` cursor). Open and submit the participation backfill page (`/Tickets/Participation/Backfill`) |

## Invariants

- Ticket orders and attendees are synced from the external vendor — they cannot be manually created or edited from this app.
- Stripe enrichment (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) is preserved across re-syncs and only re-run for orders that have a `StripePaymentIntentId` and are still missing fee data; if `IStripeService.IsConfigured` is false the pass is silently skipped.
- VAT is computed locally per order using VIP-split logic on attendees with `Status` in (`Valid`, `CheckedIn`); orders not in `Paid` status carry `VatAmount = 0`. The vendor's tax line is intentionally ignored.
- All `/Tickets` dashboard aggregate metrics (`TicketsSold`, `Revenue`, `NetRevenue`, fee totals, `UnmatchedOrderCount`, the daily-sales chart, the per-payment-method fee breakdown) are computed only over orders with `PaymentStatus == Paid`; ticket counts within those are further restricted to attendees with `Status` in (`Valid`, `CheckedIn`). Refunded/Cancelled/Pending orders are still synced and visible on `/Tickets/Orders` (with the new Status column on Recent Orders) but never contribute to dashboard totals. The "unmatched orders" badge links to `/Tickets/Orders?filterMatched=false&filterPaymentStatus=Paid` so the count and the drill-down agree.
- Auto-matching uses normalized email comparison (`NormalizingEmailComparer`) against verified UserEmails rows only. Collisions among verified rows (a data-integrity error, should not happen) leave the email unmatched and emit `LogError`; nobody gets the ticket match. Buyer match writes `TicketOrder.MatchedUserId`; attendee match writes `TicketAttendee.MatchedUserId` independently.
- A user "has a ticket" iff at least one `Valid` or `CheckedIn` `TicketAttendee` is matched to their `UserId`. Buyer-only matches do not count — purchasing tickets for others does not give the buyer ticket coverage.
- Only `Valid` attendees that have not been gate-checked-in can be sent. A gate scan keeps `Status = Valid` and stamps `CheckedInAt`, so `TicketTransferService` guards on `CheckedInAt is null` explicitly (in `CreateRequestAsync`, `GetConfirmationAsync`, and the `CanSendTransfer` flag).
- Receiver is chosen through the standard `<vc:human-search>` picker (`scope=Name`, `allow-email=true`): burner-name search, or an exact case-insensitive verified-email match (`IUserEmailService.GetUserIdByExactEmailAsync`) returning at most one person. The recipient must resolve to a Humans user with a legal name. Receivers may already hold other tickets — allowed.
- Sender cannot send to themselves.
- On every **holder-facing** stub surface (homepage strip, holdings list, transfer wizard) the Early Entry pill is the **viewer's own** earliest entry date, never the attendee's: all three go through the single `TicketStubInfo.From(row, holderEarlyEntry)` mapper, which stamps one value the caller resolved from `IEarlyEntryService` for the *current* viewer. A transfer therefore cannot leak the sender's EE status to the recipient, and the pill can never be present on one holder surface and missing on another.
- The **Scanner gate card is the deliberate exception**: it is staff-facing and must show the *scanned attendee's* EE, not the operator's. `ScannerController` resolves `IEarlyEntryService` for `hit.MatchedUserId` and constructs `TicketStubInfo` directly instead of going through `From`. Do not "consolidate" it onto the shared mapper — that would blank the pill for gate staff.
- Admin decisions: **"Process transfer"** runs the automated TicketTailor void(-to-hold)+reissue and, on success, sets `Approved` and writes the swapped local attendee rows; on a partial failure (`VoidSucceededIssueFailed`) **"Retry reissue"** re-issues from the held seat (one click) and, on success, sets `Approved`; **"Mark successful"** sets `Approved` with no vendor call; **"Cancel transfer"** requires a reason and sets `Rejected`. On vendor failure the request stays `Pending` with the diagnostic recorded; a partial request can't be cancelled, rejected, or re-processed — only Retry or Mark successful. The next ticket sync reconciles `ticket_attendees`.
- Request creation emails the Sender + `tickets@nobodies.team`; a decision emails the Sender + Receiver.
- `TicketSyncState` is a singleton row (Id = 1). `LastSyncAt` is the resume cursor passed back to the vendor as `updated_at.gte` on the next run. A sync stuck in `Running` for >30 minutes is auto-reset to `Error` by `GetDashboardStatsAsync` (crash recovery).

## Negative Access Rules

- Board **cannot** trigger any sync (incremental or full), export CSV, or open the participation backfill page.
- Board **cannot** approve or reject transfer requests — transfer review is gated by `TicketAdminOrAdmin`. Board can view ticket data but the transfer side-effects (vendor void+reissue, local attendee mutation) are admin-only.
- Board **cannot** trigger attendee contact import — same `TicketAdminOrAdmin` gate as the sync.
- Board **cannot** set or rotate the gate-terminal password (`/Tickets/Admin/Gate` is `TicketAdminOrAdmin`).
- The gate-terminal account **cannot** reach any `/Tickets/*` page — it holds no roles; only the Scanner section's `ScannerAccess` policy admits it.
- TicketAdmin **cannot** trigger a Full Re-sync or open the participation backfill page (both `AdminOnly`).
- Nobody can edit ticket configuration (vendor `EventId`, API key, sync interval) from inside the app — those values come from `appsettings`'s `TicketVendor` section and the `TICKET_VENDOR_API_KEY` environment variable, set at deploy time.
- Regular humans have no access to `/Tickets/*` (dashboard, orders, attendees, codes, gate list, who-hasn't-bought, sales aggregates) — the controller-wide policy is `TicketAdminBoardOrAdmin`.
- A user **cannot** send an attendee they do not own (the attendee's `MatchedUserId` must equal the Sender's user id; validated via `TicketAttendeeOwnership.IsCurrentOwner` in `TicketTransferService.CreateRequestAsync`; buyer-only order ownership does not confer send rights).

## Routing

| Route | Method | Auth Policy | Purpose |
|-------|--------|-------------|---------|
| `/Tickets` | GET | `TicketAdminBoardOrAdmin` | Summary dashboard |
| `/Tickets/Orders` | GET | `TicketAdminBoardOrAdmin` | Paginated order list |
| `/Tickets/Attendees` | GET | `TicketAdminBoardOrAdmin` | Paginated attendee list |
| `/Tickets/Codes` | GET | `TicketAdminBoardOrAdmin` | Discount code redemption tracking |
| `/Tickets/GateList` | GET | `TicketAdminBoardOrAdmin` | Placeholder page (gate lookup is handled via `/Scanner/Tickets`) |
| `/Tickets/WhoHasntBought` | GET | `TicketAdminBoardOrAdmin` | Active Volunteers without a ticket |
| `/Tickets/SalesAggregates` | GET | `TicketAdminBoardOrAdmin` | Weekly + quarterly aggregate reports |
| `/Tickets/Sync` | POST | `TicketAdminOrAdmin` | Trigger incremental sync |
| `/Tickets/FullResync` | POST | `AdminOnly` | Trigger full re-sync |
| `/Tickets/Admin/Contacts` | GET | `TicketAdminOrAdmin` | Preview attendee-contact-import plan |
| `/Tickets/Admin/Contacts/Apply` | POST | `TicketAdminOrAdmin` | Apply selected attendees |
| `/Tickets/Admin/Onsite` | GET | `ScannerAccess` | "Who's Onsite" roster |
| `/Tickets/Admin/Gate` | GET | `TicketAdminOrAdmin` | Gate-terminal account status card (**Shell's** — see Architecture) |
| `/Tickets/Admin/Gate/SetPassword` | POST | `TicketAdminOrAdmin` | Set/rotate the gate-terminal password (**Shell's**) |
| `/Tickets/Participation/Backfill` | GET + POST | `AdminOnly` | CSV import of participation records |
| `/Tickets/Export/Attendees` | GET | `TicketAdminOrAdmin` | CSV export of attendees |
| `/Tickets/Export/Orders` | GET | `TicketAdminOrAdmin` | CSV export of orders |
| `/Welcome` | GET | `[AllowAnonymous]` | Post-purchase landing page (WelcomeController) |

`/Welcome` is an intentional post-purchase landing route owned by Tickets logic while physically handled by `WelcomeController` in `Humans.Web`; it is documented here to avoid it being treated as a routing boundary drift in future alignments.

## Triggers

- When ticket sync runs: vendor orders and issued tickets are upserted into `ticket_orders` / `ticket_attendees` (existing rows keyed by `VendorOrderId` / `VendorTicketId` retain their `Id` and their already-enriched fields; API-issued transfer reissues — null vendor order id — keep the locally-snapshotted `Price` so revenue/VAT stay anchored to the original order), gate check-ins pulled from the vendor's `/check_ins` are applied onto the attendee rows in a standalone pass keyed by `VendorTicketId` (`ITicketRepository.ApplyCheckInsAsync`, write-once `CheckedInAt`), Stripe fees are enriched for newly-paid orders, VAT is recomputed for every order, vendor discount codes are matched to `CampaignGrants` via `ICampaignService.MarkGrantsRedeemedAsync`, and event participation is reconciled. On success, `LastSyncAt` is set to the start-of-sync instant; `ITicketCacheInvalidator.InvalidateVendorEventSummary(eventId)` removes the per-event vendor summary owned by the ticket cache decorator, and `ITicketCacheInvalidator.InvalidateAll()` clears both tracked slices: per-order `TicketOrderInfo` and per-user `UserTicketHoldings`. The order projection re-warms from the freshly-upserted rows on the next read; user holdings reload on demand and keep their 5-minute freshness window inside the tracked entry.
- `EventParticipation` derivation (Ticket Tailor's active vendor event only, scoped to `EventSettings.Year` from `IShiftManagementService.GetActiveAsync`):
  - For each user with at least one matched attendee: any gate-checked-in ticket (`CheckedInAt` set — read from the persisted attendee rows, not inferred from `Status`, since TicketTailor keeps a scanned ticket `valid`) → `ParticipationStatus.Attended`, passing the earliest scan across the user's tickets down as the participation `CheckedInAt`; otherwise any `Valid` ticket → `ParticipationStatus.Ticketed`. Both write through `IUserService.SetParticipationFromTicketSyncAsync` with `ParticipationSource.TicketSync`.
  - For each prior `(TicketSync, Ticketed)` row in the year: if the user no longer has any `Valid`/`CheckedIn` matched ticket, the row is removed via `IUserService.RemoveTicketSyncParticipationAsync`. `Attended` rows are never removed by sync — being checked in is permanent.
  - Self-declared `NotAttending` rows are owned by the User section, but a matched ticket overrides the declaration: a ticket is a physical thing, so sync flips a prior `NotAttending` to `Ticketed` (via the same `SetParticipationFromTicketSyncAsync` path) when the user holds a `Valid`/`CheckedIn` ticket. Only `Attended` rows are never overwritten.
- "Who Hasn't Bought" lists active Volunteers-team members (via `ITeamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers)`) minus those whose current-year `EventParticipation.Status` is `NotAttending`. `HasTicket` is true when the user appears in the union of matched attendee user-ids and matched order user-ids.
- The Volunteer Ticket Coverage card on `/Tickets` divides current-event matched-attendee Volunteers by total active Volunteers — buyer-only matches are excluded by construction.
- Code redemption: vendor discount codes attached to orders are pushed back to Campaigns via `ICampaignService.MarkGrantsRedeemedAsync` so each `CampaignGrant.RedeemedAt` reflects the order's `PurchasedAt`.
- When an account merge accepts, `TicketSyncService.ReassignAsync` (via `IUserMerge`) re-FKs `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and the new `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId` columns from source to target, then calls `ITicketCacheInvalidator.InvalidateAfterUserMerge(sourceUserId, targetUserId)` to drop the order projection and both users' `UserTicketHoldings` tracked entries. The per-user eviction is the T-07 fix for an earlier gap where merged users' homepage tickets card and ticket-holdings widget could lag up to 5 minutes after the fold. `AccountMergeService.FoldAsync` fans out across all `IUserMerge` registrations; the Tickets section participates because `TicketSyncService` implements `IUserMerge`.
- Audit actions written by ticket transfer: `TicketTransferRequested` (on `CreateRequestAsync`), `TicketTransferCancelled` (on `CancelAsync`), `TicketTransferApproved` (on `ApproveAsync`), `TicketTransferRejected` (on `RejectAsync`).
- On transfer decision (approve = mark successful, or cancel with reason): only the request row changes (`Status`, `DecidedByUserId`, `DecidedAt`, `AdminNotes`) and Sender + Receiver are emailed. The automated path (`ProcessTransferAsync`) additionally voids+reissues at TicketTailor and writes the swapped attendee rows in the same flow; the manual path (`ApproveAsync`) mutates nothing locally and defers to the next ticket sync. `ITicketCacheInvalidator.InvalidateAfterTransfer(senderUserId, receiverUserId)` is called on every lifecycle transition (request creation, Sender cancel, admin reject, admin approve) because pending state is baked into `UserTicketHoldings` (the "transfer pending" stamp on the stub), and approve/reject also affects the `TransferredToName`/`TransferredAt` fields carried by the order projection for void attendees.
- On attendee contact import apply: for selected unmatched attendees, `MatchedUserId` is set (via `UpsertAttendeesAsync`), new users are provisioned (via `IAccountProvisioningService` with `ContactSource.TicketTailor`, Stub Profile + verified `UserEmail`), squatter unverified rows are deleted first, `EventParticipation(Ticketed, TicketSync)` is written for each newly-matched user, ticket caches are invalidated via `ITicketCacheInvalidator.InvalidateAfterContactImport`, and a single `AuditAction.TicketContactsImported` row records the summary.

### TicketDashboardStats cache (ghost cache key)

`CacheKeys.TicketDashboardStats` is **invalidator-only**. `TicketQueryService.GetDashboardStatsAsync` is the canonical producer of the `TicketDashboardStats` DTO and is called fresh on every `TicketController.Index` request — `CachingTicketQueryService.GetDashboardStatsAsync` is a pass-through to the inner. The cache key and the `Metadata` row (5 min, Static) exist so a future caching wrapper can be added without renaming things. Treat this as a documented placeholder, not a live cache; see `docs/architecture/service-data-access-map.md` for the cross-cutting rule.

## Cross-Section Dependencies

- **Campaigns:** `ICampaignService` — Tickets reads campaign + grant data for the Codes page (`GetCodeTrackingAsync`) and pushes redemptions back during sync (`MarkGrantsRedeemedAsync`). Discount-code *generation* lives in the Campaigns section's `CampaignController` (which calls `ITicketVendorService.GenerateDiscountCodesAsync` directly); the `/Tickets/Codes` page only reports redemption status, it does not create codes.
- **Users/Identity:** `IUserService` — `GetAllUserInfosAsync` / `GetUserInfosAsync` / `GetUserInfoAsync` (all on the inherited `IUserServiceRead`) for stitching matched-user names into orders/attendees lists; `SetParticipationFromTicketSyncAsync` / `RemoveTicketSyncParticipationAsync` / `GetAllParticipationsForYearAsync` / `BackfillParticipationsAsync` for derived `EventParticipation` writes (User section owns `event_participations` per peterdrier/Humans PR #243). `IAccountProvisioningService.FindOrCreateUserByEmailAsync` is consumed by `AttendeeContactImportService` to provision Humans users for unmatched ticket attendees.
- **Profiles:** `IUserEmailService` — `GetAllUserEmailLookupEntriesAsync` builds the sync-time email→userId index; `GetVerifiedEmailsForUserAsync` backs the per-user ticket probe (the Who-Hasn't-Bought email search now matches in-memory against `UserInfo.UserEmails`); `GetNotificationEmailsByUserIdsAsync` hydrates the report; `GetUserIdByExactEmailAsync` resolves transfer recipients by exact email; `GetPrimaryEmailAsync` snapshots recipient email at request creation time. `IProfileService.GetByUserIdsAsync` supplies `MembershipTier`; `IProfileService.SearchHumansByNameAsync` (filtered to `MatchField == "Burner Name"`) resolves transfer recipients by burner name. Called by `IAccountMergeService` (Profiles section) — `ITicketSyncService.ReassignToUserAsync` re-FKs `TicketOrder.MatchedUserId` and `TicketAttendee.MatchedUserId` during account merge fold.
- **Teams:** `ITeamService.GetActiveMemberUserIdsAsync(SystemTeamIds.Volunteers)` for the Volunteers cohort used by both the dashboard's coverage card and the Who-Hasn't-Bought list; `GetActiveNonSystemTeamNamesByUserIdsAsync` for team labels on the report.
- **Shifts:** `IShiftManagementService.GetActiveAsync` — active-event lookup for the year used by `EventParticipation` derivation and by the `Participation/Backfill` page (replaces the prior direct `EventSettings` read, PR #545c).
- **Budget:** `Humans.Budget.Contracts.IBudgetServiceRead` — `GetActiveYearAsync` + `ComputeBudgetSummary` feed the dashboard's break-even calculation (`TicketQueryService` takes the read interface, not the full `IBudgetService`). The Tickets→Budget bridge now sits on the **Budget** side of the boundary: `TicketingBudgetService` is internal to `Humans.Budget.Services` and calls *into* Tickets via `ITicketServiceRead.GetTicketOrdersAsync`, then writes line items through Budget's own `IBudgetService`. Tickets owns neither the service nor its contract.
- **GDPR:** `TicketQueryService` implements `IUserDataContributor`, contributing the `TicketOrders` and `TicketAttendeeMatches` slices to the per-user data export.
- **Stripe (Infrastructure):** `IStripeService.GetPaymentDetailsAsync` looks up payment-intent details to populate `PaymentMethod` / `PaymentMethodDetail` / `StripeFee` / `ApplicationFee` per order. Configuration is via `STRIPE_TICKETS_KEY` env var; if unset, enrichment is skipped silently and the dashboard's fee breakdown stays empty.
- **Profiles (account merge):** `TicketSyncService` implements `IUserMerge`; `AccountMergeService.FoldAsync` fans out across all `IUserMerge` registrations, calling `TicketSyncService.ReassignAsync` which delegates to `ITicketRepository.ReassignToUserAsync` to re-FK `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId`.
- **Users/Identity (transfer):** `IUserServiceRead.GetUserInfoAsync` — recipient validation and display-name resolution for transfer requests (extends the existing `IUserServiceRead` dependency).
- **Audit (transfer):** `IAuditLogService.LogAsync` — four new actions: `TicketTransferRequested`, `TicketTransferCancelled`, `TicketTransferApproved`, `TicketTransferRejected` (existing Audit dependency, extended).
- **Scanner (inbound):** `ScannerController` calls `ITicketServiceRead.GetTicketOrdersAsync` (read-only) to power the `/Scanner/Tickets` barcode-lookup card. No writes back to Tickets from Scanner.
- **Gate (inbound):** `GateService` reads ticket data via `ITicketServiceRead` to resolve barcode admits, and `GateVendorCheckInJob` mirrors gate admits back to the vendor via `ITicketVendorService.CreateCheckInAsync` (best-effort, gated behind `Gate:VendorMirrorEnabled`, default off; Gate's own `gate_scan_events` remains the dedupe authority). No writes to Tickets-owned tables from Gate.

## Architecture

**Owning services (all in `Humans.Tickets.Services`, all `internal sealed`):**
- `TicketQueryService` — read-side dashboard / orders / attendees / codes / who-hasn't-bought / sales aggregates / per-user ticket probes; also implements `IUserDataContributor` for GDPR export.
- `TicketSyncService` — vendor sync orchestrator (orders + attendees upsert, Stripe enrichment, VAT compute, code redemption push, EventParticipation derivation); also implements `IUserMerge` so account merges re-FK `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId`.
- `TicketTransferService` — transfer request lifecycle: `GetMyAttendeesAsync`, `GetConfirmationAsync`, `CreateRequestAsync`, `CancelAsync`, `ApproveAsync` (mark successful — no vendor call), `ProcessTransferAsync` (automated void(-to-hold)+reissue), `RetryReissueAsync` (one-click reissue from the held seat after a partial failure), `RejectAsync` (cancel with reason). Emails Sender + tickets@ on request, Sender + Receiver on decision.
- `TicketVendorGateway` — the section's forwarding edge onto the Base vendor port, and the only place in the codebase that names both the application's ticketing vocabulary (`TicketDiscountCodeRequest` / `TicketDiscountKind`) and the port's (`DiscountCodeSpec` / `DiscountType`). Serves `ITicketDiscountCodes` (Campaigns' grant waves) and `ITicketVendorMirror` (`GateVendorCheckInJob`'s gate-admission mirror).
- `AttendeeContactImportService` — manually-triggered admin job that classifies unmatched ticket attendees and provisions Humans users for them via `IAccountProvisioningService` (plan + apply pattern mirroring the Mailer import; squatter protection deletes unverified UserEmail rows before creating fresh verified ones).

**Owned tables:** `ticket_orders`, `ticket_attendees`, `ticket_sync_state`, `ticket_transfer_requests`

**Authorization note:** transfer authorization is **service-level** — `TicketTransferService` validates ownership and state (e.g. requester owns the attendee, attendee is `Valid`, one pending at a time) in `CreateRequestAsync`, `CancelAsync`, etc. No dedicated `AuthorizationHandler` was added: the controller surface is small and the service guards are sufficient. If a non-controller surface (CLI, internal API) is added in a future PR, a `TicketTransferAuthorizationHandler` should be introduced then.

**Vendor connectors — their own section, `Humans.TicketTailor`:**
Ticketing is three things, and conflating them is what a vendor change punishes. The **section**
(`Humans.Tickets`) is the application's only door to ticketing. The **port**
(`ITicketVendorService`, `Humans.Tickets/Contracts/`) is the vendor-agnostic contract, owned by
this section since G5 lane 4b-2g (nobodies-collective/Humans#866) — it used to sit in Base. The
**adapter** (`Humans.TicketTailor`) is one implementation of it, and takes a direct
`ProjectReference` on `Humans.Tickets` to name the port. The port is deliberately **not** on the
`Humans.Tickets.Contracts` leaf: no Base consumer names it, and the leaf must keep the vendor's
vocabulary away from other sections (they use `ITicketDiscountCodes` / `ITicketVendorMirror`).
When the 2027 vendor lands, the adapter project is deleted and `Humans.<NewVendor>` is added;
nothing in `Humans.Tickets`, in Base or in any consumer changes.

- `TicketTailorService` — production HTTP client; bound when the host environment is Production.
- `StubTicketVendorService` — deterministic in-memory fixture; bound everywhere else. The stub fills in placeholder `EventId`/`ApiKey` so `TicketVendorSettings.IsConfigured` returns true even without env vars.

`Section.Register` in the adapter reads the environment name out of `IConfiguration`
(`ISection.Register` has no `IHostEnvironment`) and fails closed: anything but an exactly-Production
environment name gets the stub, so a developer holding a real `TICKET_VENDOR_API_KEY` still cannot
write to a live ticketing account. The port's `IOptions<TicketVendorSettings>` binding stays in
Shell — the settings belong to the port, which `TicketSyncService` and Shell's
`TicketVendorHealthCheck` also read, and deleting the adapter must not take them with it.

**Only two things may inject `ITicketVendorService`:** `Humans.Tickets` and Shell's
`TicketVendorHealthCheck` (which probes the connector deliberately). Everything else asks Tickets,
through `Humans.Tickets.Contracts`. Pinned by
`tests/Humans.Application.Tests/Architecture/TicketVendorPortArchitectureTests.cs`.

**`TicketsGateAdminController` is not this section's.** `/Tickets/Admin/Gate` writes no Tickets
table and injects no Tickets service — it drives Shell's `GateTerminalAccountSeeder` to set the
shared gate terminal's password, and sits under this route prefix only because ticket admins are
who rotate that credential. It stayed in `Humans.Web`, route unchanged. Whether it eventually
lands in `Humans.Gate` is Gate's call.

**Stripe connector (Infrastructure-only):** `StripeService` (`IStripeService`) wraps the Stripe SDK and is consumed by `TicketSyncService` for fee enrichment.

**Status:** (A) Fully §15-compliant, and moved into its own project at G5
(nobodies-collective/Humans#866): `src/Sections/Humans.Tickets` plus the
`src/Sections/Humans.Tickets.Contracts` leaf and the `src/Sections/Humans.TicketTailor` adapter.
The leaf publishes seven members across five interfaces — `ITicketServiceRead` (2,
`[SurfaceBudget(2)]`), `ITicketSync` (2), `ITicketTransferQueue` (1), `ITicketDiscountCodes` (1)
and `ITicketVendorMirror` (1) — against roughly thirty public members before the move; the whole
`TicketDashboardDtos` surface, the transfer wizard and the admin decision DTOs are now internal.
Tickets is the first section to ship both a `.Contracts` leaf *and* a `Contracts/` folder: the
leaf carries what Base consumers need, the folder carries public surface that is ASP.NET plumbing
(`TicketStubViewComponent`). The split is a judgement about what cross-section consumers should
have to see, **not** a compiler constraint: G5 lane 3c measured (2026-08-15,
`dotnet msbuild <leaf>.csproj -t:ResolvePackageAssets -getItem:FrameworkReference`) that every
leaf referencing `Humans.Interfaces` resolves `Microsoft.AspNetCore.App` transitively, so
"the leaf stays framework-free" — this doc's earlier wording — is false and always was. All section services route every database
read/write through `ITicketRepository`; neither `TicketQueryService.cs` nor `TicketSyncService.cs` imports `Microsoft.EntityFrameworkCore` or references `TicketsDbContext`. Umbrella issue nobodies-collective/Humans#545 closed by sub-tasks #545a (TicketQueryService → Application), #545b (TicketingBudgetService → Application, originally with its own `ITicketingBudgetRepository` — removed in #815), #545c (TicketSyncService → Application + `IShiftManagementService` / `IUserService` routing). `ITicketVendorService` connector split landed in peterdrier/Humans PR #277. The bridge itself has since left this section: with Budget's G5 move, `TicketingBudgetService` became internal to `Humans.Budget.Services` and its `ITicketingBudgetService` contract moved to `Humans.Budget.Contracts`, shrinking to `SyncActualsAsync` (projection refresh and parameter writes are Budget-admin-only, so they stayed off the published interface).

**Caching:** the §15 caching decorator pattern is applied (T-07, 2026-05-16). `CachingTicketQueryService` (`Services/Stores/`, Singleton — `ApplicationServicesTakeNoMemoryCacheRule` sweeps `Humans.*.Services` and would flag it in `Services/`) wraps a keyed-inner `TicketQueryService` (Scoped) and composes two tracked slices: `OrdersCache : TrackedCache<Guid, TicketOrderInfo>` (with `warmOnStartup: true`) keyed by ticket order id, and `UserHoldingsCache : TrackedCache<Guid, CachedUserTicketHoldings>` keyed by user id. The order slice owns the per-order `TicketOrderInfo` projection with attendees embedded; the user slice owns `UserTicketHoldings` and stores the 5-minute freshness deadline in the tracked value. The warm path calls the normal `ITicketService.GetTicketOrdersAsync` read method on the keyed inner, not a cache-named helper. Composition (multiple inner caches with different shapes) precludes inheriting `TrackedCache` directly, so the decorator implements `IHostedService` itself and forwards `StartAsync` to both tracked caches; the user slice has `warmOnStartup: false`. The only remaining `IMemoryCache` ticket entry owned by the decorator is per-event `TicketEventSummary` — whose key stays in Base's `CacheKeys` because `TicketTailorService` *populates* the same entry and the adapter must not name the section; the inner is cache-free and an architecture test pins no `IMemoryCache` in the inner's constructor.

**Architecture tests:**
- `tests/Humans.Tickets.Tests/Architecture/TicketQueryArchitectureTests.cs` — sealed inner + decorator, the decorator implements `ITicketService` / `ITicketServiceRead` / `ITicketCacheInvalidator` and is the only implementation of the invalidator, and `ITicketServiceRead` exposes no entity types.
- `tests/Humans.Tickets.Tests/Architecture/TicketSyncArchitectureTests.cs` — `TicketSyncService`'s constructor takes no store abstraction.
- `tests/Humans.Application.Tests/Architecture/TicketVendorPortArchitectureTests.cs` — **the one that matters for the vendor swap**: only `Humans.Tickets` and Shell's health check inject `ITicketVendorService`, every implementation of it lives in `Humans.TicketTailor`, and the Tickets leaf names none of the port's vocabulary.
- `tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs` — pins the port in `Humans.Tickets.Contracts` (the `Humans.Tickets` assembly, not the leaf), no HTTP/vendor-SDK type in its signatures, and the two adapters in `Humans.TicketTailor.Services`.
- `tests/Humans.Integration.Tests/Controllers/TicketsPageRenderTests.cs` — the §15 step 12 render check: every admin page, the transfer wizard's copy in English and Spanish, the Shell access-matrix widget invoked by name, and the volunteer's `302 → /Account/AccessDenied`.

### Repositories

- **`ITicketRepository`** (Tickets-owned) — owns reads/writes for `ticket_orders`, `ticket_attendees`, `ticket_sync_states`. Aggregate-local navs kept (`TicketOrder.Attendees`, `TicketAttendee.TicketOrder`). Cross-domain `MatchedUser` nav properties have been stripped from both entities; FK (`MatchedUserId`) is retained and joining to `User` is done in-memory via `IUserService.GetUserInfosAsync` after the read.

The Tickets→Budget bridge owns no repository and is no longer a Tickets class: `Humans.Budget.Services.TicketingBudgetService` reads paid-order data through `ITicketServiceRead` (served by `ITicketRepository` on the read side) and delegates all writes to Budget's `IBudgetService`. Its tests moved with it, to `tests/Humans.Budget.Tests/Architecture/TicketingBudgetArchitectureTests.cs`. (The dedicated `ITicketingBudgetRepository` it once used was removed in #815.)

### Touch-and-clean guidance

- New cross-section data needs always go through the owning section's interface — `ICampaignService`, `IUserService`, `IProfileService`, `IUserEmailService`, `ITeamService`, `IShiftManagementService`, `IBudgetServiceRead`. The `MatchedUser` nav properties have been stripped from both entities; do not re-add them. Project by `MatchedUserId` in memory via `IUserService.GetUserInfosAsync`.
- `IMemoryCache` is owned by `CachingTicketQueryService` (the decorator) only, and only for the per-event `TicketEventSummary:{eventId}` entry. The inner `TicketQueryService` and write-side `TicketSyncService` are cache-free per T-07; sync invalidates the per-event summary via `ITicketCacheInvalidator.InvalidateVendorEventSummary` and clears tracked ticket slices via `ITicketCacheInvalidator.InvalidateAll`. Other Tickets-section services (e.g. `TicketTransferService`) that need to invalidate after a write call `ITicketCacheInvalidator.InvalidateAfterTransfer(senderUserId, receiverUserId)` instead of touching `IMemoryCache` directly. Do not push `IMemoryCache` into controllers, view components, or other domain services. New invalidation seams go on `ITicketCacheInvalidator` (not `ITicketServiceRead`) so the budgeted query surface doesn't grow each time a new write site is added.
- The `TicketDashboardStats` cache key remains invalidator-only (see *TicketDashboardStats cache* under Triggers). The decorator doesn't read-through-cache that DTO; `GetDashboardStatsAsync` still hits the repository on each render — on-demand staleness on the dashboard during sync windows is currently acceptable.
- When extending the Tickets→Budget bridge, remember it lives in Budget now: source new read data from `ITicketServiceRead` (adding methods there only if the existing `GetTicketOrdersAsync` read model is insufficient), and edit `TicketingBudgetService` in `src/Sections/Humans.Budget/Services/`. Projection/line-item writes stay Budget-owned.
- The vendor split is doctrinal: business code talks to `ITicketVendorService` and never to "Ticket Tailor" directly. Any new vendor capability needs an interface method first, then a `TicketTailorService` impl plus a deterministic `StubTicketVendorService` impl so dev/preview environments still exercise the call.
