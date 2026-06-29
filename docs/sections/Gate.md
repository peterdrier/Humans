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
  **Gated behind `Gate:VendorMirrorEnabled` (default off)** until the check-in request payload
  is verified against a live TicketTailor API key — a wrong body 4xx-fails silently and
  permanently, so the mirror stays off until confirmed.
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
- **Shifts** — `IBurnSettingsService.GetActiveAsync` (event time zone for "today").
- **Users** — `IUserServiceRead` (scanner name; leaderboard rendering via `<vc:human>`).

## Architecture

**Owning service:** `GateService` (`Humans.Application.Services.Gate`) — also implements
`IUserMerge` and `IUserDataContributor`.
**Owned tables:** `gate_scan_events`, `gate_settings` via `IGateRepository`.
**Jobs:** `GateRetentionJob` (recurring), `GateVendorCheckInJob` (enqueued on admit).
**Decorator decision:** none — gate reads must be live (a stale verdict admits or blocks the wrong
person), mirroring the read-through Scanner section.
**Status:** new section (gate-scanner feature). Posture change (attendance gateway) pending Peter's
sign-off.
