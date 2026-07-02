<!-- freshness:triggers
  src/Humans.Web/Controllers/GateController.cs
  src/Humans.Web/Views/Gate/**
  src/Humans.Web/wwwroot/js/gate/**
  src/Humans.Web/wwwroot/css/gate/**
  src/Humans.Application/Services/Gate/**
  src/Humans.Application/Interfaces/Gate/**
  src/Humans.Domain/Entities/GateScanEvent.cs
  src/Humans.Domain/Entities/GateSettings.cs
  src/Humans.Domain/Entities/GateStaffPin.cs
  src/Humans.Web/Infrastructure/GatePinThrottle.cs
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
  "ID confirmed" can never turn a STOP into an admit. `scannedByUserId` is the authenticated gate
  account, taken from the principal, never the request body. The child/ID-waiver and the early
  overrides admit only when `OverrideByUserId` was recorded, which the controller sets **only**
  after verifying the shared supervisor override PIN (`Gate:SupervisorPin`) server-side — a forged
  `childWithAdult`/`overrideEarly` flag alone never admits, and an unset PIN fails closed
  (overrides refused, never a free pass).
- **Personal staff PINs (`gate_staff_pins`)** — _disabled since peterdrier#1075: the claim step is
  bypassed (the terminal scans as the gate account) and the flow below is unreachable, though the
  table, service methods, admin page, and views remain in place._ Each staffer sets a 4-digit PIN the first time they
  claim the gate (`SetOwnPinAsync`, hashed via Identity's `IPasswordHasher`), reused across shifts.
  Claiming the scanner becomes a PIN entry (`/Gate/ClaimPin`), so the leaderboard attribution is a
  real per-person claim rather than honour-system. **Claim vs override are decoupled by the
  `AdminEnrolled` flag on the PIN.** _Everyone_ — supervisors included — may self-enrol a **claim**
  PIN at the kiosk (`SetOwnPinAsync`, `AdminEnrolled = false`); it attributes scans only. **Override
  authority** is conferred solely by an **admin enrolment** (`/Gate/Admin` → `AdminSetPinAsync`,
  `AdminEnrolled = true`). `AuthorizeOverrideAsync` requires all three, server-checked: an
  admin-enrolled PIN, the correct PIN, AND a currently-held supervisor role — so an attacker
  cold-setting a supervisor's PIN at the anonymous kiosk gains attribution spoofing only (already
  possible for any staffer), never override power. The enrolled-supervisor override picker
  (`GetEnrolledSupervisorIdsAsync`) lists only admin-enrolled supervisors.
  **First-time set** has two guards (claim-only, never on verify): an "is this you?" confirm before a
  PIN is minted in someone's name, and a double-entry (enter twice, must match) so a mis-typed PIN
  can't silently lock a volunteer out (there is no self-service reset — an admin clears it). Every
  PIN **set/reset is audited** (`GateStaffPinSet`/`GateStaffPinReset` with the acting user — the
  staffer on self-enrol, the admin on admin-set/reset); PIN values are never logged.
- **Supervisor override** — a too-early scan (e.g. a Friday Early-Entry ticket scanned on Wednesday)
  STOPs with a precise reason (the holder's EE date vs today, or "no early entry · general entry
  opens …" — date only, never the EE source); an **unconfirmed-Early-Entry** scan (the guest may
  hold EE the terminal can't see) goes AMBER with "search by name". Both cards offer a
  **supervisor override**: the supervisor enters the shared override PIN (`Gate:SupervisorPin`) on
  the keypad in the override panel. On success the admit is recorded as `AdmittedEarlyOverride`.
  One PIN for all supervisors: it **authorizes but cannot attribute** — `OverrideByUserId` records
  the gate account, not which supervisor typed the PIN. The same mechanism backs the
  child-without-ID waiver (`AdmittedChildWithAdult`).
- **PIN brute-force throttle** — `GatePinThrottle`: 5 failures → a 15-minute lockout that does **not**
  self-reset (only a correct PIN clears it). The shared override PIN throttles on a single
  terminal-wide bucket (`override:shared`) — a lockout blocks only the override path, never
  scanning — capping brute-force at 5 tries / 15 min across the ~10k PIN space.
- **Dedupe authority** — a unique index on `GateScanEvent.AdmitDedupeKey` (the barcode for admit
  verdicts, null otherwise) makes the first admit per barcode win atomically across all lanes;
  Postgres excludes nulls so reject rows never collide. An explicit pre-check covers the common case.
  _Known test gap:_ the concurrent index-collision path isn't covered by unit tests (the EF in-memory
  provider can't enforce unique indexes) — a Postgres-backed race test is tracked in the debt-ledger
  inbox (2026-06-29).
- **Vendor check-in mirror** — on an admit the controller enqueues `GateVendorCheckInJob`
  (fire-and-forget) which calls `ITicketVendorService.CreateCheckInAsync` (TicketTailor
  `POST /v1/check_ins`). Best-effort only; `gate_scan_events` remains the dedupe authority.
  **Gated behind `Gate:VendorMirrorEnabled` (default off).** Payload **verified live (2026-06-30)**
  with a real POST → 201: the endpoint is **form-encoded** (not JSON), **requires `issued_ticket_id`
  AND `quantity`**, accepts `check_in_at` (unix s), and `event_id` is optional (derived from the
  ticket). The **API key must have Event-manager (or Admin) scope** — an Order-manager key 403s on
  `/v1/check_ins`. Check-ins are **not idempotent** (each POST creates a new `check_in` record), so
  `GateVendorCheckInJob` runs with `Attempts = 0` (no retry — a retry after a silent success would
  double-record; a missed mirror is acceptable, `gate_scan_events` is authoritative). Before enabling
  the flag, confirm the configured TicketTailor key has Event-manager scope.
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
  (audit only, never trusted for the cutoff), `Note?`, `OverrideByUserId?` (the authorizing
  supervisor on an override admit), `AdmitDedupeKey?`. Cross-section links are bare Guid columns
  (no nav/FK); `GuestUserId`/`ScannedByUserId`/`OverrideByUserId` are re-pointed on account merge.
- **`gate_settings`** (owned) — singleton (`Id` = 1). `GeneralEntryOpensAt` (UTC `Instant`),
  `MinorAgeThresholdYears`.
- **`gate_staff_pins`** (owned) — one row per staffer, key = bare `UserId`. `PinHash`
  (Identity `IPasswordHasher`), `CreatedAt`, `UpdatedAt`. The merge survivor keeps its PIN.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Gate` | GET | `ScannerAccess` | Scan terminal (redirects to Claim if no active scanner) |
| `/Gate/Evaluate` | GET | `ScannerAccess` | Live verdict card for a barcode (write-free) |
| `/Gate/Decision` | POST | `GateAdmit` | Record the agent's Yes/No decision (incl. supervisor override); enqueue vendor mirror on admit |
| `/Gate/Claim` | GET (`ScannerAccess`) / POST (`GateAdmit`) | — | Pick who is scanning → hands off to the PIN keypad |
| `/Gate/ClaimPin` | POST | `GateAdmit` | Set/verify the staffer's PIN, then stamp the scanning session |
| `/Gate/Leaderboard` | GET | `ScannerAccess` | Per-staffer scan tallies |
| `/Gate/Admin` | GET/POST | `TicketAdminOrAdmin` | Gate settings (cutoff, minor age threshold) |
| `/Gate/Admin/SetPin` | POST | `TicketAdminOrAdmin` | Admin enrol/change any staffer's PIN (incl. supervisors) |
| `/Gate/Admin/ResetPin` | POST | `TicketAdminOrAdmin` | Admin clear a staffer's PIN (they re-enrol on next claim) |

The **write** actions (`Decision`, `Claim`/`ClaimPin` POST) require the dedicated `GateAdmit`
policy rather than the read-only `ScannerAccess`, so the gate's admit surface never rides on the
Scanner read gate. Both are satisfied by the shared gate-terminal account today; they are
kept separate so they can diverge. The supervisor override on `Decision` is authorized by the
shared override PIN (`Gate:SupervisorPin`), brute-force throttled via `GatePinThrottle` on a
single terminal-wide bucket — see Invariants. (The `Claim`/`ClaimPin`/`Admin` PIN routes remain
but are unreachable since peterdrier#1075 — nothing links to them.)

## Invariants

- The cutoff is evaluated against the server clock; `ClientScanAt` never influences it.
- A barcode can be admitted at most once (atomic unique index + pre-check); re-entry is governed
  by a physical wristband, not a second scan.
- `scannedByUserId` is server-derived from the authenticated principal (the gate account); an
  override (child/ID-waiver, too-early, or unconfirmed-EE) needs the server-verified shared
  supervisor PIN (`Gate:SupervisorPin`), recorded as `OverrideByUserId`.
- **Override authorization fails closed.** The PIN comparison is timing-safe (fixed-width hashes),
  and an unset `Gate:SupervisorPin` refuses every override with an explicit "not configured" card —
  a missing key is never a free pass. The shared PIN authorizes but cannot attribute:
  `OverrideByUserId` is the gate account, not the individual supervisor.
- **Override PIN entry is throttled terminal-wide.** `GatePinThrottle` (5 tries → 15-min
  non-self-resetting lockout; a correct PIN clears it) on the single `override:shared` bucket. A
  lockout blocks only overrides — scanning and every other card continue — so a fumbled PIN can
  never stop the line, while the shared PIN can't be ground through the ~10k space at the kiosk.
- Scan **attribution is the shared gate account** (peterdrier#1075): the personal-PIN claim flow is
  bypassed, so the leaderboard and `ScannedByUserId` no longer identify individual staffers.
- Gate participates in GDPR export (`IUserDataContributor`), account merge (`IUserMerge` re-FKs
  `GuestUserId`/`ScannedByUserId`), and retention purge.
- The gate view shows name + verdict + one reason line only; Early-Entry source, the previous
  scanner's id, and internal GUIDs stay server-side.
- **A result card auto-clears** back to the Ready screen so the previous guest's name never lingers on
  the shared kiosk (PII) and the screen is never stuck on the last scan. Timeouts vary by outcome (a
  quick ADMIT clears in ~10s; a STOP/AMBER refusal that gets explained to the guest lingers ~30s); a
  terminal card also shows a "Next ticket" button with a live countdown. Any tap on the card pushes the
  deadline back (active use keeps it up). The interim ID-confirm card has a longer ~60s **safety**
  timeout only (it shows a name but the operator is mid-decision); the supervisor-override panel pauses
  the timer while a PIN is being entered.
- **No dead-ends / minimal-training affordances** (kiosk is chromeless — no browser back). Every screen
  has a visible way out: Leaderboard has "← Back to scanning"; the PIN keypad has "← Not you?". "End shift"
  (hand over to the next operator) is a deliberate two-tap button that POSTs `GateController.EndShift` to
  clear the scanner session **server-side**, so a walk-away can't leave the next person's scans attributed
  to whoever last claimed. The scan screen has an always-on
  "?" help cheat-sheet (green=admit / red=stop / amber=supervisor), STOP cards spell out the action
  ("Do not admit · if disputed, get the gate lead"), the ID-confirm card has a "Wrong ticket — scan
  again" escape and a visible auto-clear countdown, the override panel has "← Wrong person", and the
  freshness line taps to reload.

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
- **Users** — `IUserServiceRead` (scanner name; supervisor/claim display names; leaderboard
  rendering via `<vc:human>`).
- **Auth** — `IRoleAssignmentService.HasActiveRoleAsync` (is this user a supervisor?) and
  `GetActiveUserIdsInRoleAsync` (enumerate enrolled supervisors for the override tap-list).

## Configuration

- `Gate:SupervisorPin` — the shared supervisor override PIN (one PIN for all supervisors),
  required for the too-early / unconfirmed-EE override and the child-without-ID waiver. **Unset →
  overrides fail closed** (refused with a "not configured" card) — set it before doors open.
  _(History: retired 2026-07-01 in favour of per-staffer enrolled PINs, restored after
  peterdrier#1075 dropped the per-staffer PIN flow.)_
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
**Owned tables:** `gate_scan_events`, `gate_settings`, `gate_staff_pins` via `IGateRepository`.
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
