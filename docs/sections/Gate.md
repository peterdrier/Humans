<!-- freshness:triggers
  src/Humans.Web/Controllers/GateController.cs
  src/Humans.Web/Views/Gate/**
  src/Humans.Web/wwwroot/js/gate/**
  src/Humans.Web/wwwroot/css/gate/**
  src/Humans.Application/Services/Gate/**
  src/Humans.Application/Interfaces/Gate/**
  src/Humans.Domain/Entities/GateScanEvent.cs
  src/Humans.Domain/Entities/GateSettings.cs
  src/Humans.Infrastructure/Repositories/Gate/**
  src/Humans.Infrastructure/Jobs/GateRetentionJob.cs
  src/Humans.Infrastructure/Jobs/GateVendorCheckInJob.cs
-->
<!-- freshness:flag-on-change
  Admission verdict precedence, server-authoritative dedupe, the attendance-gateway posture,
  the GDPR/merge/retention wiring, and the vendor check-in mirror — review when Gate services,
  entities, controller, or jobs change.
-->

# Gate — Section Invariants

Gate ticket scanning that **decides entry** at the event door and writes the durable
admission record. Distinct from the read-only `Scanner` section, which must never check anyone in.

## Concepts

- **Gate scan** — a staffer scans a Ticket Tailor QR on the rugged tablet. The Gate service
  evaluates it (validity, duplicate, Early Entry cutoff) against the **server clock**, the agent
  confirms the photo ID with an explicit Yes/No, and the outcome is recorded as one append-only
  `gate_scan_events` row. This makes Humans the **system of record for gate admission** — a
  deliberate posture distinct from the rest of the app, where ticket check-in is inbound-only from
  the vendor.
- **Verdict precedence** (`GateAdmissionRules.Evaluate`, fresh per scan): not-found/void → STOP;
  already-admitted-locally or vendor-checked-in → STOP duplicate; **cutoff not configured →
  AMBER** (fail-safe — see below); `now ≥ cutoff` → needs-ID; before cutoff + unmatched → AMBER
  (Early Entry unknown — never a silent too-early stop); before cutoff + Early Entry covers today
  → needs-ID (early); else → STOP too-early.
- **Cutoff must be configured before doors** — `gate_settings.GeneralEntryOpensAt` defaults to the
  `Instant.MinValue` "not configured" sentinel. While unset, **every scan fails safe to AMBER**
  (`CutoffNotConfigured` → `Unresolved`) rather than silently admitting everyone, and the terminal
  shows a loud "cutoff not set" banner. **Operational step:** an admin must set the general-entry
  cutoff in `Gate ▸ Admin` before opening the doors. To run with no Early-Entry gating at all,
  set the cutoff to a past instant (explicitly "general entry already open") — never leave it unset.
- **Server-authoritative decision** — `RecordDecisionAsync` re-evaluates server-side, so a client
  "ID confirmed" can never turn a STOP into an admit. `scannedByUserId` is the session-claimed
  staffer, taken from the session, never the request body. The child/ID-waiver path requires a
  server-verified supervisor PIN (`Gate:SupervisorPin`).
- **Dedupe authority** — a unique index on `GateScanEvent.AdmitDedupeKey` (the barcode for admit
  verdicts, null otherwise) makes the first admit per barcode win atomically across all lanes;
  Postgres excludes nulls so reject rows never collide. An explicit pre-check covers the common case.
  _Known test gap:_ the concurrent index-collision path isn't covered by unit tests (the EF in-memory
  provider can't enforce unique indexes) — a Postgres-backed race test is tracked in the debt-ledger
  inbox (2026-06-29).
- **Vendor check-in mirror** — on an admit the controller enqueues `GateVendorCheckInJob`
  (fire-and-forget) which calls `ITicketVendorService.CreateCheckInAsync` (TicketTailor
  `POST /v1/check_ins`). Best-effort only; `gate_scan_events` remains the dedupe authority.
  **Gated behind `Gate:VendorMirrorEnabled` (default off).** Payload field names were verified
  read-only against the live API (2026-06-29): the `check_in` object is
  `{ issued_ticket_id, check_in_at (unix s), event_id, event_series_id, quantity }`, so the body
  uses `check_in_at` (not the earlier guess `checked_in_at`) with the id in the body, not the path.
  Still unverified (needs one live POST): whether create requires `event_id`. The flag stays off
  until that POST is confirmed — a wrong body 4xx-fails silently and permanently.
- **Vendor-checked-in dedupe signal is currently dead** (pre-existing Tickets bug, not Gate).
  The `CheckedInAtVendor` precedence input depends on the Tickets sync detecting check-ins, but
  that sync parses a nested `check_in` object the live API no longer returns (it returns a
  top-level `checked_in` string) — see the debt-ledger inbox (2026-06-29). Until Tickets is fixed,
  cross-device duplicate detection relies solely on the gate's own `gate_scan_events` unique index
  (which is the authority anyway), so admission correctness is unaffected.
- **Retention** — `GateRetentionJob` purges `gate_scan_events` older than `Gate:RetentionDays`
  (default 365) daily, because gate scans are attendance/movement data.

## Data Model

- **`gate_scan_events`** (owned) — append-only. `OccurredAt` (server), `ScannedByUserId`,
  `Barcode`, `TicketAttendeeId?`, `GuestUserId?`, `Verdict`, `LaneId?`, `ClientScanAt?`
  (audit only, never trusted for the cutoff), `Note?`, `AdmitDedupeKey?`. Cross-section links are
  bare Guid columns (no nav/FK).
- **`gate_settings`** (owned) — singleton (`Id` = 1). `GeneralEntryOpensAt` (UTC `Instant`),
  `MinorAgeThresholdYears`.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Gate` | GET | `ScannerAccess` | Scan terminal (redirects to Claim if no active scanner) |
| `/Gate/Evaluate` | GET | `ScannerAccess` | Live verdict card for a barcode (write-free) |
| `/Gate/Decision` | POST | `GateAdmit` | Record the agent's Yes/No decision; enqueue vendor mirror on admit |
| `/Gate/Claim` | GET (`ScannerAccess`) / POST (`GateAdmit`) | — | Claim the scanning session as a Humans user |
| `/Gate/Leaderboard` | GET | `ScannerAccess` | Per-staffer scan tallies |
| `/Gate/Admin` | GET/POST | `TicketAdminOrAdmin` | Gate settings (cutoff, minor age threshold) |

The **write** actions (`Decision`, `Claim` POST) require the dedicated `GateAdmit` policy
rather than the read-only `ScannerAccess`, so the gate's admit surface never rides on the
Scanner read gate. Both are satisfied by the shared gate-terminal account today; they are
kept separate so they can diverge. The supervisor-PIN child-waiver on `Decision` is
throttled per source IP (its own bucket, mirroring `/Account/GateLogin`).

## Invariants

- The cutoff is evaluated against the server clock; `ClientScanAt` never influences it.
- A barcode can be admitted at most once (atomic unique index + pre-check); re-entry is governed
  by a physical wristband, not a second scan.
- `scannedByUserId` is server-derived from the claimed session; the child/ID-waiver needs a
  server-verified supervisor PIN (attempts throttled per source IP).
- **Attribution is honor-system, not authenticated.** On the shared kiosk account, `ScannedByUserId`
  is the staffer-asserted session claim (whoever tapped "claim"), taken from the session rather than
  the request body so it can't be forged on the wire — but it is **not** an independently
  authenticated identity. This is intended for the leaderboard/audit trail; do not treat it as proof
  of who physically scanned.
- Gate participates in GDPR export (`IUserDataContributor`), account merge (`IUserMerge` re-FKs
  `GuestUserId`/`ScannedByUserId`), and retention purge.
- The gate view shows name + verdict + one reason line only; Early-Entry source, the previous
  scanner's id, and internal GUIDs stay server-side.

## Cross-Section Dependencies

- **Tickets** — `ITicketServiceRead.GetTicketOrdersAsync` (barcode lookup) and
  `ITicketVendorService.CreateCheckInAsync` (vendor mirror, via the Gate job).
  **Cache freshness:** the barcode lookup reads the cached Tickets projection, so a ticket bought,
  transferred, or refunded at the door won't scan correctly until the next Tickets sync. The window
  is the Tickets cache/sync interval, not real-time.
- **Early Entry** — `IEarlyEntryService.GetForUserAsync`.
- **Shifts** — `IBurnSettingsService.GetActiveAsync` (event time zone for "today"); and
  `IShiftManagementService.GetActiveAsync`/`GetBrowseShiftsAsync` from `GateController` to
  pre-fill the claim screen with the gate-shift roster (opt-in via `Gate:RosterTeamId`; see
  Configuration). Read-only consumption of the existing Shifts service from the Web layer,
  mirroring how other controllers read shift signups — no new Shifts surface.
  _Cross-section read approved by Peter (verbal, 2026-06-29)._
- **Users** — `IUserServiceRead` (scanner name; leaderboard rendering via `<vc:human>`).

## Configuration

- `Gate:SupervisorPin` — PIN that authorizes the child-without-ID waiver on `/Gate/Decision`.
- `Gate:VendorMirrorEnabled` — default off; gates the TicketTailor check-in mirror (see Vendor
  check-in mirror above).
- `Gate:RosterTeamId` — **optional** GUID of the Shifts department/team that staffs the gate.
  When set, `/Gate/Claim` shows that team's signed-up volunteers as one-tap picks; the name/email
  search remains for anyone not on the roster. Unset → search only (no behaviour change). Only
  volunteers whose gate **shift starts within ±2 hours of now** (event-local time, from
  `EventSettings.TimeZoneId`) are shown, so the list tracks shift change rather than the whole
  event roster. De-dupes one pick per human; excludes refused/bailed/cancelled/no-show signups.

## Architecture

**Owning service:** `GateService` (`Humans.Application.Services.Gate`) — also implements
`IUserMerge` and `IUserDataContributor`.
**Owned tables:** `gate_scan_events`, `gate_settings` via `IGateRepository`.
**Jobs:** `GateRetentionJob` (recurring), `GateVendorCheckInJob` (enqueued on admit).
**Decorator decision:** none — gate reads must be live (a stale verdict admits or blocks the wrong
person), mirroring the read-through Scanner section.
**Layout:** the tablet-facing views (`Claim`, scan terminal `Index`, `Leaderboard`) use the
**chromeless kiosk layout** `_GateLayout` — full-bleed, no admin nav/sidebar/breadcrumb, so the
rugged tablet shows only the gate UI. The admin settings page (`Admin`) overrides back to
`_AdminLayout` (a desktop admin task). The shared `GateTerminal` system account (no roles/teams)
sees only this kiosk; on the device, Edge Assigned Access removes browser chrome too.
**Status:** new section (gate-scanner feature). Posture change (attendance gateway) pending Peter's
sign-off.
