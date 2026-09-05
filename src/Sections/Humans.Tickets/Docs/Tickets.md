<!-- freshness:triggers
  src/Sections/Humans.Tickets/**
  src/Sections/Humans.Tickets.Contracts/**
  src/Sections/Humans.TicketTailor/**
  src/Sections/Humans.Stripe/Contracts/IStripeService.cs
  src/Sections/Humans.Onboarding/Controllers/WelcomeController.cs
  src/Sections/Humans.Scanner/Controllers/ScannerController.cs
  src/Sections/Humans.Gate/Services/GateService.cs
  src/Sections/Humans.Gate/Jobs/GateVendorCheckInJob.cs
  tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs
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
- **Ticket Sync** is a background Hangfire job (`TicketSyncJob`, interval from `TicketVendor:SyncIntervalMinutes` — default 15, `appsettings.json` sets 5) that pulls order, attendee, and gate check-in data from the vendor through `ITicketVendorService`.
- **TicketTransferRequest** is a Sender-initiated request to send an issued ticket to another Humans user (the Receiver). Lifecycle: `Pending → Approved | Rejected | Cancelled`. The ticket team either runs the **automated TicketTailor void(-to-hold)+reissue** (`ProcessTransferAsync` — the "Process transfer" button) or voids+reissues by hand and records the outcome (`ApproveAsync` = "mark successful"). Both are admin-only (`TicketAdminOrAdmin`). `Approved` = transferred; `Rejected` = "cancelled with a reason"; `Cancelled` = Sender self-cancel. The next ticket sync reconciles local attendee rows. Request → emails Sender + tickets@; decision → emails Sender + Receiver. (The automated path records its outcome in `VendorResult`/`VendorMessage`/`NewVendorTicketId`/`VendorHoldId`; the unused `VendorStepsJson` column stays dormant pending a post-soak drop PR — see [`memory/architecture/no-drops-until-prod-verified.md`](../../../../memory/architecture/no-drops-until-prod-verified.md).)
- **Vendor connector** is a thin adapter (`Humans.TicketTailor`) behind `ITicketVendorService`. Production binds `TicketTailorService` (HTTP client against `https://api.tickettailor.com/v1`); non-production binds `StubTicketVendorService` (deterministic in-memory fixture with ~450 orders / ~600 tickets).
- **Attendee Contact Import** is a manually-triggered admin job (`IAttendeeContactImportService`) that creates a no-profile Humans user for each unmatched ticket attendee whose email doesn't already resolve to an existing UserEmail. Mirrors the MailerLite import's plan/apply shape with squatter protection (unverified UserEmail rows are deleted before a fresh verified row is created for the new user). Runs only when an admin triggers it; the sync does not invoke it.

## Data Model

### TicketOrder

**Table:** `ticket_orders`

Ticket purchase order synced from vendor (one per purchase). Vendor-agnostic identity is `VendorOrderId`; payment-intent linkage is `StripePaymentIntentId` (from the vendor's `txn_id`). Stripe enrichment fields (`PaymentMethod`, `PaymentMethodDetail`, `StripeFee`, `ApplicationFee`) are filled in by `EnrichOrdersWithStripeDataAsync` after the upsert and preserved across re-syncs. `DonationAmount` (standalone vendor donation line items, VAT-exempt) and `DiscountAmount` (absolute value of vendor `gift_card` line items) come from the vendor; `VatAmount` is recomputed locally via VIP-split logic — the vendor's tax line is intentionally ignored because Ticket Tailor mis-applies 10% to the full ticket price.

Cross-domain nav `TicketOrder.MatchedUser` stripped (FK `MatchedUserId` retained; nav property removed). No FK-join in `ITicketRepository`; join to User is done in-memory via `IUserServiceRead.GetUserInfosAsync`.
Aggregate-local: `TicketOrder.Attendees`.

### TicketAttendee

**Table:** `ticket_attendees`

Individual ticket holder (issued ticket, multiple per order). Vendor-agnostic identity is `VendorTicketId`. `AttendeeEmail` is nullable — some vendor flows don't capture per-ticket email; in that case `MatchedUserId` stays null. `Status` is normalized from the vendor string into `TicketAttendeeStatus` (`Valid` / `CheckedIn` / `Void`). Only `Valid` and `CheckedIn` count as revenue or as ticket coverage. `Barcode` is the Ticket Tailor `issued_ticket.barcode` value (the code printed on the ticket and encoded in its QR), nullable, lazy-filled during sync; a Full Re-sync backfills existing rows. It is indexed and searchable (the `/Tickets/Attendees` search matches barcode alongside name and email). Ticket Tailor gives each ticket two identities — the object id `ti_…` (`VendorTicketId`) and the short scannable `barcode` — and offers **no lookup-by-barcode endpoint** (retrieve is by `ti_…`; list filters by event/time/cursor only). Every barcode→ticket resolution therefore runs against our own synced rows and the cached `TicketOrderInfo` projection, never a live vendor call. `CheckedInAt` is the gate-scan time sourced from the vendor's check-in resource (TicketTailor `/check_ins`) during sync — orthogonal to `Status` (a scanned ticket stays `Valid`; check-in is a separate vendor concept), write-once per ticket (earliest scan wins, never cleared by later syncs), null until checked in.

Cross-domain nav `TicketAttendee.MatchedUser` stripped (FK `MatchedUserId` retained; nav property removed). No FK-join in `ITicketRepository`; join to User is done in-memory via `IUserServiceRead.GetUserInfosAsync`.
Aggregate-local: `TicketAttendee.TicketOrder`.

**`TicketAttendeeInfo` read projection** (part of `TicketOrderInfo`): carries `Barcode` and `CheckedInAt` plus, for `Void` attendees, `TransferredToName` and `TransferredAt` sourced from the Approved `TicketTransferRequest` for the attendee (read via `ITicketTransferRepository` in `TicketQueryService.GetTicketOrdersAsync`). These fields are used by the Scanner ticket-lookup card (`/Scanner/Tickets`).

### TicketSyncState

**Table:** `ticket_sync_state`

Singleton (`Id` always 1) tracking ticket sync operational state. `VendorEventId` records the event currently being synced. `LastSyncAt` doubles as the resume cursor passed to the vendor's `updated_at.gte` filter on the next run. `SyncStatus` is `Idle` / `Running` / `Error`; if a sync is found stuck in `Running` for >30 min, `GetDashboardStatsAsync` auto-resets it to `Error` with a stale-state message. `FullResync` clears `LastSyncAt` so the next run pulls all orders again.

### TicketTransferRequest

**Table:** `ticket_transfer_requests`

Sender-initiated transfer request. `OriginalTicketAttendeeId` FK → `ticket_attendees`. `SenderUserId` / `ReceiverUserId` FK → users. `ReceiverLegalName` (snapshot of `Profile.FullName`) and `ReceiverEmail` (snapshot of primary email) are captured at request time and used by the ticket team + notification emails. `Status` is `TicketTransferStatus` (`Pending` / `Approved` / `Rejected` / `Cancelled`). `DecidedByUserId` FK → users (the admin who decided). `AdminNotes` is the free-text success note or the required cancellation reason. `RequestedAt` / `DecidedAt` are UTC timestamps. `VendorResult` / `VendorMessage` / `NewVendorTicketId` / `VendorHoldId` carry the automated void+reissue outcome (written by `ProcessTransferAsync` / `RetryReissueAsync`; `NotAttempted`/null for manual transfers); `VendorHoldId` retains the held seat from a void-to-hold whose reissue failed, enabling one-click **Retry reissue**. The unused `VendorStepsJson` column is dormant and drops in a follow-up PR post-soak.

**Indexes:** `(SenderUserId, Status)` for the homepage card; `Status` for the admin queue. No uniqueness constraints — multiple Pending transfers per attendee are allowed.

**Cross-section FKs:** `SenderUserId`, `ReceiverUserId`, `DecidedByUserId` → Users (FK only, no nav).

### EventParticipation (derived, not owned)

`event_participations` is owned by the User section. The Tickets section *derives* its `TicketSync`-sourced rows during each sync — see Triggers below. Tickets must never query or mutate the table directly.


## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated user with attendees on their orders (Sender) | Send any `Valid` attendee from their own order to another Humans user; cancel a `Pending` transfer they created |
| TicketAdmin, Board, Admin | View the ticket dashboard, orders, attendees, codes, gate list, sales aggregates, and the "Who Hasn't Bought" report (controller-wide policy `TicketAdminBoardOrAdmin`) |
| TicketAdmin, Admin | Trigger an incremental ticket sync. Export attendee/order CSV. Generate discount codes for campaigns (Campaign section, policy `TicketAdminOrAdmin`). Approve or reject pending transfer requests from `/Tickets/Admin/Transfers` (policy `TicketAdminOrAdmin`). Import attendee contacts (preview + selectively apply) from `/Tickets/Admin/Contacts` (policy `TicketAdminOrAdmin`). Set/rotate the gate-terminal password from `/Tickets/Admin/Gate` (policy `TicketAdminOrAdmin`; see `src/Sections/Humans.Scanner/Docs/features/gate-terminal-login.md`) |
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
| `/Tickets/SalesAggregates` | GET | `TicketAdminBoardOrAdmin` | Weekly + quarterly aggregate reports, by ticket type, discount codes by campaign |
| `/Tickets/Sync` | POST | `TicketAdminOrAdmin` | Trigger incremental sync |
| `/Tickets/FullResync` | POST | `AdminOnly` | Trigger full re-sync |
| `/Tickets/Transfers` | GET | `[Authorize]` | Transfer wizard: the viewer's sendable tickets |
| `/Tickets/Transfers/Confirm` | POST | `[Authorize]` | Transfer wizard: confirm recipient |
| `/Tickets/Transfers` | POST | `[Authorize]` | Transfer wizard: submit the request |
| `/Tickets/Transfers/Cancel` | POST | `[Authorize]` | Sender cancels a `Pending` request |
| `/Tickets/Admin/Transfers` | GET | `TicketAdminOrAdmin` | Transfer queue (`?tab=pending` default, `?tab=all`) plus the order-drift table |
| `/Tickets/Admin/Transfers/Detail/{id}` | GET | `TicketAdminOrAdmin` | One request with its audit trail |
| `/Tickets/Admin/Transfers/Decide` | POST | `TicketAdminOrAdmin` | Process / retry / mark successful / cancel with reason |
| `/Tickets/Admin/Contacts` | GET | `TicketAdminOrAdmin` | Preview attendee-contact-import plan |
| `/Tickets/Admin/Contacts/Apply` | POST | `TicketAdminOrAdmin` | Apply selected attendees |
| `/Tickets/Admin/Onsite` | GET | `ScannerAccess` | "Who's Onsite" roster |
| `/Tickets/Admin/Gate` | GET | `TicketAdminOrAdmin` | Gate-terminal account status card |
| `/Tickets/Admin/Gate/SetPassword` | POST | `TicketAdminOrAdmin` | Set/rotate the gate-terminal password |
| `/Tickets/Participation/Backfill` | GET + POST | `AdminOnly` | CSV import of participation records |
| `/Tickets/Export/Attendees` | GET | `TicketAdminOrAdmin` | CSV export of attendees |
| `/Tickets/Export/Orders` | GET | `TicketAdminOrAdmin` | CSV export of orders |
| `/Welcome` | GET | `[AllowAnonymous]` | Post-purchase landing page (`WelcomeController`, Onboarding section) |

`/Welcome` is an intentional post-purchase landing route owned by Tickets logic while physically handled by `WelcomeController` in `Humans.Onboarding`; it is documented here to avoid it being treated as a routing boundary drift in future alignments.

## Triggers

- When ticket sync runs: vendor orders and issued tickets are upserted into `ticket_orders` / `ticket_attendees` (existing rows keyed by `VendorOrderId` / `VendorTicketId` retain their `Id` and their already-enriched fields; API-issued transfer reissues — null vendor order id — keep the locally-snapshotted `Price` so revenue/VAT stay anchored to the original order), gate check-ins pulled from the vendor's `/check_ins` are applied onto the attendee rows in a standalone pass keyed by `VendorTicketId` (`ITicketRepository.ApplyCheckInsAsync`, write-once `CheckedInAt`), Stripe fees are enriched for newly-paid orders, VAT is recomputed for every order, vendor discount codes are matched to `CampaignGrants` via `ICampaignService.MarkGrantsRedeemedAsync`, and event participation is reconciled. On success, `LastSyncAt` is set to the start-of-sync instant; `ITicketCacheInvalidator.InvalidateVendorEventSummary(eventId)` removes the per-event vendor summary owned by the ticket cache decorator, and `ITicketCacheInvalidator.InvalidateAll()` clears both tracked slices: per-order `TicketOrderInfo` and per-user `UserTicketHoldings`. The order projection re-warms from the freshly-upserted rows on the next read; user holdings reload on demand and keep their 5-minute freshness window inside the tracked entry.
- `EventParticipation` derivation (Ticket Tailor's active vendor event only, scoped to `EventSettings.Year` from `IBurnSettingsService.GetActiveAsync`):
  - For each user with at least one matched attendee: any gate-checked-in ticket (`CheckedInAt` set — read from the persisted attendee rows, not inferred from `Status`, since TicketTailor keeps a scanned ticket `valid`) → `ParticipationStatus.Attended`, passing the earliest scan across the user's tickets down as the participation `CheckedInAt`; otherwise any `Valid` ticket → `ParticipationStatus.Ticketed`. Both write through `IUserService.SetParticipationFromTicketSyncAsync` with `ParticipationSource.TicketSync`.
  - For each prior `(TicketSync, Ticketed)` row in the year: if the user no longer has any `Valid`/`CheckedIn` matched ticket, the row is removed via `IUserService.RemoveTicketSyncParticipationAsync`. `Attended` rows are never removed by sync — being checked in is permanent.
  - Self-declared `NotAttending` rows are owned by the User section, but a matched ticket overrides the declaration: a ticket is a physical thing, so sync flips a prior `NotAttending` to `Ticketed` (via the same `SetParticipationFromTicketSyncAsync` path) when the user holds a `Valid`/`CheckedIn` ticket. Only `Attended` rows are never overwritten.
- "Who Hasn't Bought" lists active Volunteers-team members (`ITeamServiceRead.GetTeamAsync(SystemTeamIds.Volunteers)`, active members only) minus those whose current-year `EventParticipation.Status` is `NotAttending`. `HasTicket` is true when the user appears in the union of matched attendee user-ids and matched order user-ids.
- The Volunteer Ticket Coverage card on `/Tickets` divides current-event matched-attendee Volunteers by total active Volunteers — buyer-only matches are excluded by construction.
- Code redemption: vendor discount codes attached to orders are pushed back to Campaigns via `ICampaignService.MarkGrantsRedeemedAsync` so each `CampaignGrant.RedeemedAt` reflects the order's `PurchasedAt`.
- When an account merge accepts, `TicketSyncService.ReassignAsync` (via `IUserMerge`) re-FKs `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId` from source to target, then calls `ITicketCacheInvalidator.InvalidateAfterUserMerge(sourceUserId, targetUserId)` to drop the order projection and both users' `UserTicketHoldings` tracked entries. The per-user eviction is required: without it a merged user's homepage tickets card and holdings widget lag up to 5 minutes behind the fold. `AccountMergeService.FoldAsync` fans out across all `IUserMerge` registrations; the Tickets section participates because `TicketSyncService` implements `IUserMerge`.
- Audit actions written by ticket transfer: `TicketTransferRequested` (on `CreateRequestAsync`), `TicketTransferCancelled` (on `CancelAsync`), `TicketTransferApproved` (on `ApproveAsync`, and on a successful `ProcessTransferAsync` / `RetryReissueAsync`), `TicketTransferAutoFailed` (on a failed `ProcessTransferAsync` / `RetryReissueAsync`), `TicketTransferRejected` (on `RejectAsync`).
- On transfer decision (approve = mark successful, or cancel with reason): only the request row changes (`Status`, `DecidedByUserId`, `DecidedAt`, `AdminNotes`) and Sender + Receiver are emailed. The automated path (`ProcessTransferAsync`) additionally voids+reissues at TicketTailor and writes the swapped attendee rows in the same flow; the manual path (`ApproveAsync`) mutates nothing locally and defers to the next ticket sync. `ITicketCacheInvalidator.InvalidateAfterTransfer(senderUserId, receiverUserId)` is called on every lifecycle transition (request creation, Sender cancel, admin reject, admin approve, automated process/retry) because pending state is baked into `UserTicketHoldings` (the "transfer pending" stamp on the stub), and approve/reject also affects the `TransferredToName`/`TransferredAt` fields carried by the order projection for void attendees.
- On attendee contact import apply: for selected unmatched attendees, `MatchedUserId` is set (via `UpsertAttendeesAsync`), new users are provisioned (via `IAccountProvisioningService` with `ContactSource.TicketTailor`, Stub Profile + verified `UserEmail`), squatter unverified rows are deleted first, `EventParticipation(Ticketed, TicketSync)` is written for each newly-matched user, ticket caches are invalidated via `ITicketCacheInvalidator.InvalidateAfterContactImport`, and a single `AuditAction.TicketContactsImported` row records the summary.
- On GDPR erasure (`IUserDataContributor.EraseForUserAsync`): buyer/attendee names and emails on the user's order and attendee rows, and the receiver snapshot on their transfer requests, are tombstoned in place (`ITicketRepository.EraseUserPiiAsync`, `ITicketTransferRepository.ErasePiiForUserAsync`); rows and vendor ids are kept for finance and sync reconciliation. Declared retention: sales-record.

### TicketDashboardStats cache (ghost cache key)

`CacheKeys.TicketDashboardStats` is a **ghost key**: nothing reads, writes or evicts it. `TicketQueryService.GetDashboardStatsAsync` is the canonical producer of the `TicketDashboardStats` DTO and is called fresh on every `TicketController.Index` request — `CachingTicketQueryService.GetDashboardStatsAsync` is a pass-through to the inner. The cache key and the `Metadata` row (5 min, Static) exist so a future caching wrapper can be added without renaming things. Treat this as a documented placeholder, not a live cache; see `docs/architecture/service-data-access-map.md` for the cross-cutting rule.

## Cross-Section Dependencies

Outbound (what Tickets injects; the project references are the authority — `Humans.Tickets.csproj`):

- **Campaigns:** `ICampaignService` — `GetCodeTrackingAsync` for the Codes page; `MarkGrantsRedeemedAsync` pushes redemptions back during sync. Discount-code *generation* is invoked from Campaigns via `ITicketDiscountCodes.GenerateAsync` (served by `TicketVendorGateway`); `/Tickets/Codes` only reports redemption status, it does not create codes.
- **Users:** `IUserServiceRead` — `GetAllUserInfosAsync` builds the sync-time verified-email → user index and hydrates reports; `GetUserInfosAsync` / `GetUserInfoAsync` stitch matched-user names into orders/attendees/holdings and validate transfer recipients. `IUserService` — `SetParticipationFromTicketSyncAsync` / `RemoveTicketSyncParticipationAsync` / `GetAllParticipationsForYearAsync` for derived `EventParticipation` writes (sync and contact import); `ApplyProfileOnboardingMutationAsync` when the gate-terminal account is provisioned. `IUserEmailService` — `GetVerifiedEmailsForUserAsync` backs the per-user ticket probe; `GetNotificationEmailsByUserIdsAsync` hydrates the who-hasn't-bought report; `GetPrimaryEmailAsync` snapshots the recipient email at request creation; `GetDistinctVerifiedUserIdsAsync` / `FindAnyEmailRowByAddressAsync` / `DeleteEmailAsync` drive contact-import squatter protection. `IAccountProvisioningService.FindOrCreateUserByEmailAsync` provisions users for unmatched attendees. `IUserParticipationBackfillService` backs `/Tickets/Participation/Backfill`. `IProfileEditorService.SaveProfileAsync` and `UserManager<User>` are used only by `GateTerminalAccountSeeder`.
- **Teams:** `ITeamServiceRead.GetTeamAsync(SystemTeamIds.Volunteers)` for the Volunteers cohort used by both the dashboard's coverage card and the Who-Hasn't-Bought list; `GetTeamsAsync` for team labels on the report and the onsite roster.
- **Shifts:** `IBurnSettingsService.GetActiveAsync` — the narrow read-only supplier of the active year (`BurnSettingsInfo`) for `EventParticipation` derivation, the backfill page, the who-hasn't-bought report and the onsite roster; do not reach for `IShiftManagementService`.
- **Camps:** `ICampServiceRead.GetCampsForYearAsync` for camp labels on the onsite roster.
- **Governance:** `IRoleAssignmentService.GetActiveForUserAsync` for role labels on the onsite roster.
- **Budget:** `IBudgetServiceRead` — `GetActiveYearAsync` + `ComputeBudgetSummary` feed the dashboard's break-even calculation. The Tickets→Budget bridge is Budget's: `Humans.Budget.Services.TicketingBudgetService` reads through `ITicketServiceRead` and writes through Budget's `IBudgetService`.
- **EarlyEntry:** `IEarlyEntryService.GetForUserAsync` — the viewer's own earliest entry date on the holder-facing stub surfaces (see Invariants).
- **Stripe:** `IStripeService.GetPaymentDetailsAsync` populates `PaymentMethod` / `PaymentMethodDetail` / `StripeFee` / `ApplicationFee` per order. Configured via `STRIPE_TICKETS_KEY`; if `IsConfigured` is false, enrichment is skipped silently and the dashboard's fee breakdown stays empty.
- **Audit:** `IAuditLogService.LogAsync` — the transfer actions above plus `TicketContactsImported`. `<vc:audit-log>` on the transfer detail page is the AuditLog section's component.
- **Email:** `IEmailService.SendAsync` with `IEmailMessageFactory` (`TicketTransferRequested`, `TicketTransferTeamNotification`, `TicketTransferDecision`).
- **GDPR:** `TicketQueryService` implements `IUserDataContributor` — export slices `TicketOrders` and `TicketAttendeeMatches`; erasure tombstones as described under Triggers.
- **Users (account merge, inbound-by-registration):** `TicketSyncService` implements `IUserMerge`; `AccountMergeService.FoldAsync` calls `ReassignAsync`, which delegates to `ITicketRepository.ReassignToUserAsync`.

Inbound (who injects `Humans.Tickets.Contracts`):

- **`ITicketServiceRead`** (`GetTicketOrdersAsync`, `GetUserTicketHoldingsAsync`; no `SurfaceBudget` pinned) — Users (profile, guest orders, account deletion hold, audiences), MailerLite audiences, Shifts, Surveys, Teams admin, Budget, Gate (`GateService` barcode admits), Scanner (`/Scanner/Tickets` lookup card), Agent. Read-only; nobody writes back.
- **`ITicketDiscountCodes`** — Campaigns' grant waves. **`ITicketVendorMirror`** — Gate's `GateVendorCheckInJob` mirrors admits to the vendor (best-effort, behind `Gate:VendorMirrorEnabled`, default off; Gate's own `gate_scan_events` remains the dedupe authority). **`ITicketSync`** — Notifications' `NotificationMeterProvider`. **`ITicketTransferQueue.CountPendingAsync`** — consumed only by this section's own `SectionAdminNav` badge; no cross-section caller today.

## Architecture

**Owning services (all in `Humans.Tickets.Services`, all `internal sealed`):**
- `TicketQueryService` — read-side dashboard / orders / attendees / codes / who-hasn't-bought / sales aggregates / per-user ticket probes; also implements `IUserDataContributor` for GDPR export and erasure.
- `TicketSyncService` — vendor sync orchestrator (orders + attendees upsert, Stripe enrichment, VAT compute, code redemption push, EventParticipation derivation); also implements `IUserMerge` so account merges re-FK `TicketOrder.MatchedUserId`, `TicketAttendee.MatchedUserId`, and `TicketTransferRequest.SenderUserId` / `TicketTransferRequest.ReceiverUserId`.
- `TicketTransferService` — transfer request lifecycle: `GetMyAttendeesAsync`, `GetConfirmationAsync`, `CreateRequestAsync`, `CancelAsync`, `ApproveAsync` (mark successful — no vendor call), `ProcessTransferAsync` (automated void(-to-hold)+reissue), `RetryReissueAsync` (one-click reissue from the held seat after a partial failure), `RejectAsync` (cancel with reason). Emails Sender + tickets@ on request, Sender + Receiver on decision.
- `TicketVendorGateway` — the section's forwarding edge onto its vendor port, and the only place in the codebase that names both the application's ticketing vocabulary (`TicketDiscountCodeRequest` / `TicketDiscountKind`) and the port's (`DiscountCodeSpec` / `DiscountType`). Serves `ITicketDiscountCodes` (Campaigns' grant waves) and `ITicketVendorMirror` (`GateVendorCheckInJob`'s gate-admission mirror).
- `AttendeeContactImportService` — manually-triggered admin job that classifies unmatched ticket attendees and provisions Humans users for them via `IAccountProvisioningService` (plan + apply pattern mirroring the MailerLite import; squatter protection deletes unverified UserEmail rows before creating fresh verified ones).
- `OnsiteRosterService` — the "Who's Onsite" join of checked-in attendees with user, team, camp and role labels.
- `GateTerminalAccountSeeder` — provisions the roleless gate-terminal user lazily on first password set and rotates its password; backs `/Tickets/Admin/Gate`.

**Owned tables:** `ticket_orders`, `ticket_attendees`, `ticket_sync_state`, `ticket_transfer_requests`

**Authorization note:** transfer authorization is **service-level** — `TicketTransferService` validates ownership and state (e.g. requester owns the attendee, attendee is `Valid`, one pending at a time) in `CreateRequestAsync`, `CancelAsync`, etc. No dedicated `AuthorizationHandler` was added: the controller surface is small and the service guards are sufficient. If a non-controller surface (CLI, internal API) is added in a future PR, a `TicketTransferAuthorizationHandler` should be introduced then.

**Vendor connectors — their own section, `Humans.TicketTailor`:**
Ticketing is three things, and conflating them is what a vendor change punishes. The **section**
(`Humans.Tickets`) is the application's only door to ticketing. The **port**
(`ITicketVendorService`, `Humans.Tickets/Contracts/`) is the vendor-agnostic contract, owned by
this section. The **adapter** (`Humans.TicketTailor`) is one implementation of it, and takes a direct
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
Shell (`TicketVendorInfrastructureExtensions`) — the settings belong to the port, which `TicketSyncService` and the section's own
`TicketVendorHealthCheck` also read, and deleting the adapter must not take them with it.

**Only `Humans.Tickets` may inject `ITicketVendorService`:** the section's services and its own
`TicketVendorHealthCheck` (`Health/`, which probes the connector deliberately). Everything else asks Tickets,
through `Humans.Tickets.Contracts`. Pinned by
`tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs`.

**`TicketsGateAdminController` writes no Tickets table.** `/Tickets/Admin/Gate` drives this section's
`GateTerminalAccountSeeder` to set the shared gate terminal's password, and sits under this route prefix
because ticket admins are who rotate that credential. Whether it eventually lands in `Humans.Gate` is Gate's call.

**Stripe connector:** `IStripeService` (`Humans.Stripe`) wraps the Stripe SDK and is consumed by `TicketSyncService` for fee enrichment.

**Public surface:** the `Humans.Tickets.Contracts` leaf publishes `ITicketServiceRead`, `ITicketSync`,
`ITicketTransferQueue`, `ITicketDiscountCodes` and `ITicketVendorMirror`. The `TicketDashboardDtos` surface, the transfer wizard and the admin decision DTOs are internal.
Tickets ships both a `.Contracts` leaf *and* a `Contracts/` folder: the
leaf carries what cross-section consumers need, the folder carries public surface that is ASP.NET plumbing
(`TicketStubViewComponent`) or the vendor port. The split is a judgement about what cross-section consumers should
have to see, **not** a compiler constraint: every leaf referencing `Humans.Base` resolves `Microsoft.AspNetCore.App` transitively. All section services route every database
read/write through `ITicketRepository` / `ITicketTransferRepository`; no service imports `Microsoft.EntityFrameworkCore` or references `TicketsDbContext`.

**Caching:** `CachingTicketQueryService` (`Services/Stores/`, Singleton — `ApplicationServicesTakeNoMemoryCacheRule` sweeps `Humans.*.Services` and would flag it in `Services/`) wraps a keyed-inner `TicketQueryService` (Scoped) and composes two tracked slices: `OrdersCache : TrackedCache<Guid, TicketOrderInfo>` (with `warmOnStartup: true`) keyed by ticket order id, and `UserHoldingsCache : TrackedCache<Guid, CachedUserTicketHoldings>` keyed by user id. The order slice owns the per-order `TicketOrderInfo` projection with attendees embedded; the user slice owns `UserTicketHoldings` and stores the 5-minute freshness deadline in the tracked value. The warm path calls the normal `ITicketService.GetTicketOrdersAsync` read method on the keyed inner, not a cache-named helper. Composition (multiple inner caches with different shapes) precludes inheriting `TrackedCache` directly, so the decorator implements `IHostedService` itself and forwards `StartAsync` to both tracked caches; the user slice has `warmOnStartup: false`. The only remaining `IMemoryCache` ticket entry owned by the decorator is per-event `TicketEventSummary` — whose key stays in Base's `CacheKeys` because `TicketTailorService` *populates* the same entry and the adapter must not name the section; the inner is cache-free and an architecture test pins no `IMemoryCache` in the inner's constructor.

**Architecture tests:**
- `tests/Humans.Tickets.Tests/Architecture/TicketQueryArchitectureTests.cs` — sealed inner + decorator, the decorator implements `ITicketService` / `ITicketServiceRead` / `ITicketCacheInvalidator` and is the only implementation of the invalidator, and `ITicketServiceRead` exposes no entity types.
- `tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs` — **the one that matters for the vendor swap**: only `Humans.Tickets` injects `ITicketVendorService`, its own `TicketVendorHealthCheck` included, every implementation of it lives in `Humans.TicketTailor`, and the Tickets leaf names none of the port's vocabulary.
- `tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs` — pins the port in `Humans.Tickets.Contracts` (the `Humans.Tickets` assembly, not the leaf), no HTTP/vendor-SDK type in its signatures, and the two adapters in `Humans.TicketTailor.Services`.
- `tests/Humans.Integration.Tests/Controllers/TicketsPageRenderTests.cs` — render check: every admin page, the transfer wizard's copy in English and Spanish, the Shell access-matrix widget invoked by name, and the volunteer's `302 → /Account/AccessDenied`.

### Repositories

- **`ITicketRepository`** (Tickets-owned) — owns reads/writes for `ticket_orders`, `ticket_attendees`, `ticket_sync_state`. Aggregate-local navs kept (`TicketOrder.Attendees`, `TicketAttendee.TicketOrder`). Cross-domain `MatchedUser` nav properties have been stripped from both entities; FK (`MatchedUserId`) is retained and joining to `User` is done in-memory via `IUserServiceRead.GetUserInfosAsync` after the read.
- **`ITicketTransferRepository`** (Tickets-owned) — owns reads/writes for `ticket_transfer_requests`; the state machine's writes go through it and nowhere else.

The Tickets→Budget bridge is Budget's: `Humans.Budget.Services.TicketingBudgetService` reads paid-order data through `ITicketServiceRead` and delegates all writes to Budget's `IBudgetService`; its tests live at `tests/Humans.Budget.Tests/Services/TicketingBudgetServiceTests.cs`.

### Touch-and-clean guidance

- New cross-section data needs always go through the owning section's interface — `ICampaignService`, `IUserServiceRead` / `IUserService`, `IUserEmailService`, `ITeamServiceRead`, `IBurnSettingsService`, `IBudgetServiceRead`, `ICampServiceRead`, `IRoleAssignmentService`, `IEarlyEntryService`. The `MatchedUser` nav properties have been stripped from both entities; do not re-add them. Project by `MatchedUserId` in memory via `IUserServiceRead.GetUserInfosAsync`.
- `IMemoryCache` is owned by `CachingTicketQueryService` (the decorator) only, and only for the per-event `TicketEventSummary:{eventId}` entry. The inner `TicketQueryService` and write-side `TicketSyncService` are cache-free; sync invalidates the per-event summary via `ITicketCacheInvalidator.InvalidateVendorEventSummary` and clears tracked ticket slices via `ITicketCacheInvalidator.InvalidateAll`. Other Tickets-section services (e.g. `TicketTransferService`) that need to invalidate after a write call `ITicketCacheInvalidator.InvalidateAfterTransfer(senderUserId, receiverUserId)` instead of touching `IMemoryCache` directly. Do not push `IMemoryCache` into controllers, view components, or other domain services. New invalidation seams go on `ITicketCacheInvalidator` (not `ITicketServiceRead`) so the budgeted query surface doesn't grow each time a new write site is added.
- The `TicketDashboardStats` cache key remains a ghost key (see *TicketDashboardStats cache* under Triggers). The decorator doesn't read-through-cache that DTO; `GetDashboardStatsAsync` still hits the repository on each render — on-demand staleness on the dashboard during sync windows is currently acceptable.
- When extending the Tickets→Budget bridge, remember it lives in Budget: source new read data from `ITicketServiceRead` (adding methods there only if the existing `GetTicketOrdersAsync` read model is insufficient), and edit `TicketingBudgetService` in `src/Sections/Humans.Budget/Services/`. Projection/line-item writes stay Budget-owned.
- The vendor split is doctrinal: business code talks to `ITicketVendorService` and never to "Ticket Tailor" directly. Any new vendor capability needs an interface method first, then a `TicketTailorService` impl plus a deterministic `StubTicketVendorService` impl so dev/preview environments still exercise the call.
