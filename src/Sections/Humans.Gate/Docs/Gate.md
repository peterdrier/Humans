<!-- freshness:triggers
  src/Sections/Humans.Gate/**
  src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs
  src/Sections/Humans.Tickets.Contracts/**
  src/Sections/Humans.EarlyEntry/**
  src/Sections/Humans.Shifts/Contracts/**
  src/Sections/Humans.Settings/Contracts/**
  src/Humans.Base/Users/IUserService.cs
  src/Humans.Base/Users/PersonSearchFields.cs
  src/Sections/Humans.Auth/Contracts/**
  tests/Humans.Integration.Tests/Controllers/GatePageRenderTests.cs
-->
<!-- freshness:flag-on-change
  Admission verdict precedence, server-authoritative dedupe, the attendance-gateway posture,
  the GDPR/merge/retention wiring, and the vendor check-in mirror — review when Gate services,
  entities, controller, or jobs change.
-->

# Gate — Section Invariants

Gate ticket scanning that **decides entry** at the event door and writes the durable
admission record. Distinct from the read-only `Scanner` section, which must never check anyone in.

**Purpose:** one fast, fail-safe admission decision per ticket at the door. The server clock
decides, and the result is one append-only record (an undo only stamps it): Humans is the system of
record for gate admission.

**Good looks like:** an agent scans, sees a red, amber or green card, confirms the photo ID with
Yes or No, and moves on. Nobody is silently admitted when settings are missing. Every admit and
override can be traced afterwards.

**Deliberate choices:** the vendor mirror is best-effort and off unless enabled; scans are purged
after the retention window.

**Out of scope:** any signing or consent step at the gate, and an alert for no-show build shifts.

## Concepts

- **Gate scan** — a staffer scans a Ticket Tailor QR on the rugged tablet. The Gate service
  evaluates it (validity, duplicate, Early Entry cutoff) against the **server clock**, the agent
  confirms the photo ID with an explicit Yes/No, and the outcome is recorded as one append-only
  `gate_scan_events` row. Humans is the **system of record for gate admission**, unlike the rest of
  the app, where ticket check-in is inbound-only from the vendor.
- **Ticket sources, one check-in record.** A scanned code is looked up first among the current
  event's Ticket Tailor attendees, then among tickets Humans' own checkout issued
  (`ITicketGateLookup.FindIssuedTicketAsync`, live), accepted only when the ticket's event is the
  active event. Checkout tickets: `Valid` scans valid; `Offered` (resale queue) STOPs with
  "withdraw it first" while it can still sell, and is valid once sales close; `Resold` and `Void`
  STOP. Tickets reads `IGateAdmissions` (`GateAdmissionReads`, live) so an admitted ticket is never
  offered or sold. A checkout scan row carries `CheckoutTicketId` instead of `TicketAttendeeId`, is
  never mirrored to a vendor and never appears in the vendor backfill: the gate row is its only
  check-in record. A child or companion ticket without an account takes the Early Entry of the
  adult it belongs to (`IssuedTicketInfo.EntryUserId`) and STOPs while that adult's ticket is on
  resale; with no guest account, no attendance row is written.
- **Find by name** (`/Gate/Find`, G3) — a guest without their QR code is found by the ticket's
  name, or by the burner or legal name of the human behind it (`IUserService.SearchUsersAsync`
  with `Name | LegalName`, matched against the attendee's `MatchedUserId` or the checkout ticket's
  holder or entry user). Email never matches. Accent- and case-blind, at least 2 letters, the
  first 20 matches. The list shows only the ticket's own name and type, never the name it matched
  on; picking one runs the normal scan, so the photo ID check still decides. The panel clears
  itself after 60s idle so names don't linger on the shared kiosk. Each name edit immediately clears
  the previous choices and invalidates pending responses and errors before the next debounced lookup
  starts.
- **Undo an admit** (G6) — at the kiosk, "Undo — admitted by mistake" sits behind the shared
  supervisor PIN (same throttle as overrides); `/Gate/Report` offers it to `TicketsPolicies.Manage`
  with a required reason. Undo nulls `AdmitDedupeKey`, stamps `UndoneAt`/`UndoneByUserId`/`UndoReason`
  (so the barcode scans again and `IGateAdmissions` stops counting it), writes the `GateAdmitUndone`
  audit entry, and reverts the guest's Attended row to Ticketed (`ITicketWrite.UndoCheckInAsync`)
  unless another live admit of the same guest stands within 30 days. A Ticket Tailor admit cannot be
  undone while the vendor mirror is on or the vendor shows the ticket checked in: the vendor has no
  undo, so the sync would mark the guest Attended again and the rescan would STOP as a duplicate.
  Humans-issued tickets have no vendor copy and always undo.
- **Arrivals report** (`/Gate/Report`, G8) — per event day (event time zone): admitted, early,
  overrides, refused and undone, against tickets held (Ticket Tailor non-void plus checkout tickets
  valid or on resale), and each check-in of the chosen day. A scan counts only when its ticket
  belongs to the active event. The report links to the on-site roster (`/Tickets/Admin/Onsite`).
- **Verdict precedence** (`GateAdmissionRules.Evaluate`, fresh per scan): not-found/void → STOP;
  already-admitted-locally or vendor-checked-in → STOP duplicate; **cutoff not configured →
  AMBER**; `now ≥ cutoff` → needs-ID; before cutoff + unmatched → AMBER (Early Entry unknown,
  never a silent too-early stop); before cutoff + Early Entry covers today → needs-ID (early);
  else → STOP too-early.
- **Cutoff must be configured before doors** — `gate_settings.GeneralEntryOpensAt` defaults to the
  `Instant.MinValue` "not configured" sentinel. While unset, every scan fails safe to AMBER
  (`CutoffNotConfigured` → `Unresolved`) and the terminal shows a loud "cutoff not set" banner. An
  admin sets it in `Settings ▸ Gate` (`/Settings#gate`) before doors open. To run with no
  Early-Entry gating, set it to a past instant; never leave it unset.
- **Server-authoritative decision** — `RecordDecisionAsync` re-evaluates server-side, so a client
  "ID confirmed" can never turn a STOP into an admit. `scannedByUserId` is the authenticated gate
  account, taken from the principal, never the request body. Overrides (see below) admit only after
  the service verifies the shared PIN and its throttle; a forged `childWithAdult`/`overrideEarly`
  flag alone never admits.
- Claim roster loading carries request cancellation through the active-event lookup and the Shifts
  roster read. Roster buttons, PIN headings and PIN confirmation labels render through Human by user
  ID; the roster keeps its name order and view models carry no name copies.
- **Personal staff PINs (`gate_staff_pins`)** — dormant (nobodies-collective/Humans#933): the
  terminal scans as the shared gate account and skips the claim step. The table, service methods,
  admin page and views remain. Claim and override authority are decoupled via `AdminEnrolled`;
  `AuthorizeOverrideAsync` checks three factors; first-time set is double-entry; set and reset are
  audited (`GateStaffPinSet`/`GateStaffPinReset`); PIN values are never logged.
- **Supervisor override** — a too-early scan (e.g. a Friday Early-Entry ticket on Wednesday) STOPs
  with a precise reason (the holder's EE date vs today, or "no early entry · general entry opens
  …"; date only, never the EE source). An **unconfirmed-Early-Entry** scan (the guest may hold EE
  the terminal can't see) goes AMBER with "search by name". Both cards offer a **supervisor
  override**: the supervisor enters the shared PIN (`Gate:SupervisorPin`) on the keypad, and the
  admit is recorded as `AdmittedEarlyOverride`. The same mechanism backs the child-without-ID waiver
  (`AdmittedChildWithAdult`). The shared PIN authorizes but cannot attribute: `OverrideByUserId` is
  the gate account, and the service derives it from the authenticated scanner.
- **PIN brute-force throttle** — `GatePinThrottle`: 5 failures → a 15-minute lockout that does not
  self-reset (only a correct PIN clears it), on one terminal-wide bucket (`override:shared`). A
  lockout blocks only overrides, never scanning. See Invariants.
- **Dedupe authority** — a unique index on `GateScanEvent.AdmitDedupeKey` (the barcode for admit
  verdicts, null otherwise) makes the first admit per barcode win atomically across all lanes;
  Postgres excludes nulls so reject rows never collide. An explicit pre-check covers the common case.
  Only a violation of `ix_gate_scan_events_admit_dedupe_key` becomes a duplicate-admit outcome;
  unrelated persistence failures propagate.
- **Vendor check-in mirror** — on an admit, `GateService.RecordDecisionAsync` enqueues
  `GateVendorCheckInJob` (fire-and-forget), which calls Tickets' `ITicketVendorMirror.CreateCheckInAsync`
  (Ticket Tailor `POST /v1/check_ins`; Tickets is the only door to ticketing). Best-effort:
  `gate_scan_events` stays the dedupe authority. **Gated behind `Gate:VendorMirrorEnabled` (default
  off)**; disabled jobs skip the attempt.
  - Enqueue failures are logged without failing the recorded admit; the ledger is marked only after
    enqueue succeeds. Jobs carry the scan's original admit time, so delays cannot change the vendor
    timestamp.
  - The endpoint is form-encoded, requires `issued_ticket_id` and `quantity`, accepts `check_in_at`
    (unix s); `event_id` is optional. The API key needs Event-manager (or Admin) scope; confirm it
    before enabling the flag.
  - Check-ins are not idempotent, so the job runs with `Attempts = 0` (no retry; a missed mirror is
    acceptable). An enabled job observes shutdown before the POST, then finishes that attempt
    regardless of cancellation.
  - Every enqueued mirror is claimed atomically in the in-memory `GateVendorMirrorLedger`
    (`TryMarkSent`, 24 h retention); the live admit path marks it only while the flag is on.
  - The one-off **backfill page** (`/Gate/Admin/VendorCheckInBackfill`, `AdminOnly`, temp — remove
    after use) recovers admits from the last 30 days that never mirrored
    (`GateService.GetVendorCheckInBackfillAsync`). It diffs local admits against vendor check-in
    status, allows a single test send before the bulk run, sends each with its ORIGINAL admit time,
    and skips anything the ledger claimed until the ticket sync confirms it.
    `EnqueueVendorCheckInBackfillAsync` rechecks the diff, refuses sends while mirroring is off, and
    claims each ticket before enqueueing; the controller only dispatches and formats feedback. An
    empty or unknown single-ticket id enqueues nothing.
- **Vendor-checked-in dedupe signal** — `GateService.EvaluateAsync` treats either the synced
  `TicketAttendee.CheckedInAt` or legacy `Status == CheckedIn` as an existing vendor check-in. The
  gate's own unique index stays the authority.
- **Retention** — `GateRetentionJob` purges `gate_scan_events` older than `Gate:RetentionDays`
  (default 365) daily, because gate scans are attendance/movement data.

## Data Model

- **`gate_scan_events`** (owned) — append-only, except an undo, which nulls `AdmitDedupeKey` and
  stamps `UndoneAt?`, `UndoneByUserId?`, `UndoReason?`. `OccurredAt` (server), `ScannedByUserId`,
  `Barcode`, `TicketAttendeeId?` (Ticket Tailor), `CheckoutTicketId?` (Humans checkout),
  `GuestUserId?`, `Verdict`, `LaneId?`, `ClientScanAt?`
  (audit only, never trusted for the cutoff), `Note?`, `OverrideByUserId?` (the authorizing
  supervisor on an override admit), `AdmitDedupeKey?`. Cross-section links are bare Guid columns
  (no nav/FK); `GuestUserId`/`ScannedByUserId`/`OverrideByUserId`/`UndoneByUserId` are re-pointed on account merge.
  A live admit is a row whose `AdmitDedupeKey` is set; the leaderboard and vendor backfill count only those.
- **`gate_settings`** (owned) — singleton (`Id` = 1). `GeneralEntryOpensAt` (UTC `Instant`),
  `MinorAgeThresholdYears` (stored and edited; nothing reads it yet).
- **`gate_staff_pins`** (owned) — one row per staffer, key = bare `UserId`. `PinHash`
  (Identity `IPasswordHasher`), `CreatedAt`, `UpdatedAt`, `AdminEnrolled` (override authority —
  see the PIN bullet above). The merge survivor keeps its PIN.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Gate` | GET | `ScannerAccess` | Scan terminal (scans as the authenticated gate account — no claim step) |
| `/Gate/Evaluate` | GET | `ScannerAccess` | Live verdict card for a barcode (write-free) |
| `/Gate/Decision` | POST | `GateAdmit` | Record the agent's Yes/No decision (incl. supervisor override); enqueue vendor mirror on admit |
| `/Gate/Find` | GET | `ScannerAccess` | Name search results partial for the kiosk (G3) |
| `/Gate/Undo` | POST | `GateAdmit` | Undo a live admit with the shared supervisor PIN; returns a verdict card |
| `/Gate/Report` | GET | `TicketsPolicies.Read` | Arrivals report: daily numbers and the check-ins of one day |
| `/Gate/Report/Undo` | POST | `TicketsPolicies.Manage` | Undo one check-in from the report, reason required |
| `/Gate/Claim` | GET (`ScannerAccess`) / POST (`GateAdmit`) | — | Pick who is scanning → hands off to the PIN keypad |
| `/Gate/ClaimPin` | POST | `GateAdmit` | Set/verify the staffer's PIN, then stamp the scanning session. Claim and ClaimPin POSTs require the posted id to be an active member that resolves to itself: a merged-away id fails closed rather than claiming as its survivor |
| `/Gate/EndShift` | POST | `GateAdmit` | End the claimed scanning session |
| `/Gate/Search` | GET | `ScannerAccess` | Claim-flow name search |
| `/Gate/Leaderboard` | GET | `ScannerAccess` | Service tally DTOs rendered directly, ordered by admitted then total scans (descending); names resolved by the Human component |
| `/Gate/Admin` | GET/POST | `TicketsPolicies.Manage` | GET = Staff PINs page; POST = saves the cutoff + minor-age settings submitted from `/Settings#gate` |
| `/Gate/Admin/SetPin` | POST | `TicketsPolicies.Manage` | Admin enrol/change any staffer's PIN (incl. supervisors) |
| `/Gate/Admin/ResetPin` | POST | `TicketsPolicies.Manage` | Admin clear a staffer's PIN (they re-enrol on next claim) |
| `/Gate/Admin/VendorCheckInBackfill` | GET | `AdminOnly` | One-off vendor check-in backfill page (temp — remove after use) |
| `/Gate/Admin/VendorCheckInBackfill/RunOne`, `…/Run` | POST | `AdminOnly` | Send one test / all pending backfill check-ins (see Vendor check-in mirror) |

The write actions (`Decision`, `Undo`, `EndShift`, `Claim`/`ClaimPin` POST) require the dedicated
`GateAdmit` policy rather than the read-only `ScannerAccess`, so the admit surface never rides on
the Scanner read gate. `GateAdmit` admits the shared gate-terminal account and leaves out Board,
which reads the gate but admits no one. Decision and Undo overrides are authorized by the shared
PIN (`Gate:SupervisorPin`) under `GatePinThrottle`; see Invariants.

The claim flow (`Claim`, `ClaimPin`, `EndShift`, `Search`) and the Staff PINs page (`Admin`) have
no links and are shelved under nobodies-collective/Humans#933.

## Invariants

- Scan decisions and staff-PIN administration resolve the signed-in account before
  owner calls. Merged accounts use the survivor as scanner/audit actor; missing
  accounts receive 401 and cancelled account lookups perform no action. The seeded
  gate-terminal account retains its stable scanner ID.

- Verdict-card requests reject redirects and display the request-failure card; login or access-denied pages cannot become admission verdicts.
- Resetting the shared PIN keypad cancels its pending delayed completion. Closing the supervisor override with Cancel cannot submit the PIN afterward; a reopened panel submits only its new entry.

- The cutoff is evaluated against the server clock; `ClientScanAt` never influences it.
- Decision requests require an explicit ID confirmation (`true` or `false`); malformed input or a missing confirmation returns 400 before PIN checks or scan writes. The service also rejects a normalized barcode over 128 Unicode characters, a lane id over 64, or a note over 1000 before PIN checks or side effects; the controller maps that refusal to 400. Boundary values are retained without truncation.
- A barcode can be admitted at most once (atomic unique index + pre-check); re-entry is governed
  by a physical wristband, not a second scan.
- `scannedByUserId` is server-derived from the authenticated principal (the gate account); an
  override (child/ID-waiver, too-early, or unconfirmed-EE) needs the server-verified shared
  supervisor PIN (`Gate:SupervisorPin`), recorded as `OverrideByUserId`.
- **Override authorization fails closed inside `GateService.RecordDecisionAsync`.** The input carries override intent and the PIN, never a trusted override identity. An invalid, unconfigured or locked-out PIN returns a structured refusal before any scan, attendance write or vendor job. The PIN comparison is timing-safe (fixed-width hashes),
  and an unset `Gate:SupervisorPin` refuses every override with an explicit "not configured" card —
  a missing key is never a free pass.
- **Override PIN entry is throttled terminal-wide.** `GatePinThrottle` (5 tries → 15-min
  non-self-resetting lockout; a correct PIN clears it) on the single `override:shared` bucket. A
  lockout blocks only overrides — scanning and every other card continue — so a fumbled PIN can
  never stop the line, while the shared PIN can't be ground through the ~10k space at the kiosk.
- Scan **attribution is the shared gate account**: the claim flow is dormant, so the leaderboard
  and `ScannedByUserId` do not identify individual staffers.
- An admit with a matched guest immediately writes the guest's Attended participation row for the
  active event's year — consumed-Early-Entry guards (Camps) and other attendance readers see a gate
  check-in in real time, never contingent on the vendor mirror or the ticket sync. Unmatched
  barcodes and no-active-event still admit; only the projection is skipped.
- Gate participates in GDPR export (`IUserDataContributor`; scans as guest, scanner or undoer), account
  merge (`IUserMerge` re-FKs `GuestUserId`/`ScannedByUserId`/`OverrideByUserId`/`UndoneByUserId`), and retention purge.
- The gate view shows name + verdict + one reason line only; Early-Entry source, the previous
  scanner's id, and internal GUIDs stay server-side.
- **A result card auto-clears** back to the Ready screen so the previous guest's name never lingers
  on the shared kiosk. An ADMIT clears in ~10s, a STOP/AMBER refusal in ~30s, and a terminal card
  shows a "Next ticket" button with a live countdown. Any tap on the card, or typing or scanning into
  the scan field, pushes the deadline back. The ID-confirm card has only a ~60s safety timeout; the
  supervisor-override panel pauses the timer while a PIN is entered.
- **No dead-ends / minimal-training affordances** (kiosk is chromeless, no browser back). Every
  screen has a visible way out: Leaderboard has "← Back to scanning". The scan screen has an
  always-on "?" cheat-sheet (green=admit / red=stop / amber=supervisor), STOP cards spell out the
  action ("Do not admit · if disputed, get the gate lead"), the ID-confirm card has a "Wrong ticket —
  scan again" escape and a visible countdown, the override panel has Cancel, and the freshness line
  taps to reload.
- **Logout escape hatch** — the route-restriction middleware hides every normal logout affordance from
  the gate account, so typing `logout` (any case) into the scan field is the only way off it: a local
  confirm card (client-side only, auto-clears) POSTs the standard `/Account/Logout` on a confirm tap.
  Confirm arms after the same anti-mistap delay as Yes/No, so a scanned barcode that *encodes* "logout"
  can never knock the terminal offline by itself.

## Cross-Section Dependencies

- **Audit actions:** declared on `GateAuditActions` (`src/Sections/Humans.Gate/GateAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `GateStaffPinSet`, `GateStaffPinReset`, `GateAdmitUndone`.
- **Tickets** —
  - Reads: `ITicketGateLookup.FindIssuedTicketAsync` (checkout-issued tickets, live) and
    `GetIssuedTicketsForEventAsync` (name search and arrivals report); `ITicketService.GetTicketOrdersAsync`
    (Ticket Tailor barcode lookup). Tickets in turn reads Gate's `IGateAdmissions` before offering or
    selling a returned ticket.
  - Mirror: `ITicketVendorMirror.CreateCheckInAsync` (via the Gate job).
  - Writes: `ITicketWrite.RecordParticipationAsync` projects an admit with a matched guest onto the
    guest's `EventParticipation` row (Attended, `CheckedInAt` = admit instant, year = the active
    event's). It is the upsert the ticket sync uses; Attended-is-permanent and CheckedInAt-write-once
    make the two writers commute. Without it the consumed-Early-Entry guards (Camps) never learn of a
    gate check-in, since the vendor mirror is best-effort and off by default.
    _Cross-section write approved by Peter (2026-07-02, camp-EE-revoke-after-check-in fix)._
    `ITicketWrite.UndoCheckInAsync` reverses it on undo (Attended → Ticketed, TicketSync,
    `CheckedInAt` cleared); the next ticket sync converges the row.
  - **Cache freshness:** the barcode lookup reads the cached Tickets projection, so a ticket bought,
    transferred, or refunded at the door won't scan correctly until the next Tickets sync.
- **Early Entry** — `IEarlyEntryService.GetForUserAsync`.
- **Settings / Shifts** — `ISettingsService.GetActiveEventSettings` (event time zone for "today";
  active event/year for the roster pre-fill and the Attended write) and Shifts'
  `IShiftService.GetBrowseShiftsAsync` (`GetShiftRosterAsync` pre-fills the claim screen when
  `Gate:RosterTeamId` is set). Read-only use of existing Shifts surface.
  _Cross-section read approved by Peter (verbal, 2026-06-29)._
- **Users** — `IUserService` (resolving the signed-in account to its survivor for scanner/actor attribution; the
  claim-search lookup; the kiosk name search by burner or legal name; names render via `<vc:human>`).
- **Auth** — `IRoleAssignmentService.HasActiveRoleAsync` (is this user a supervisor? Used only by the
  dormant per-PIN claim/override methods, shelved under nobodies-collective/Humans#933).

## Configuration

- `Gate:SupervisorPin` — the shared supervisor override PIN, required for the too-early /
  unconfirmed-EE override and the child-without-ID waiver. **Unset → overrides fail closed**
  (refused with a "not configured" card); set it before doors open.
- `Gate:VendorMirrorEnabled` — default off; gates the Ticket Tailor check-in mirror.
- `Gate:RetentionDays` — retention window in days for `gate_scan_events` (default 365);
  `<= 0` disables the purge.
- `Gate:RosterTeamId` — optional GUID of the Shifts team that staffs the gate. When set, the dormant
  `/Gate/Claim` lists that team's volunteers whose gate shift starts within ±2 hours of now as
  one-tap picks (one per human; refused/bailed/cancelled/no-show signups excluded), using Shifts'
  resolved `AbsoluteStart` (08:00 local for all-day shifts). `/Gate/Search` covers anyone else:
  burner-name matching only, never email, each result with a masked org-public email. Unset → search only.

## Architecture

**Project:** `src/Sections/Humans.Gate`, its own assembly. Everything in it is `internal`
except `Section` and `Contracts/`: `IGateAdmissions` (Tickets' read of admits); there is no `GateResource` because the
kiosk carries no resource keys (every string is inline English by design — a staff-facing,
single-locale terminal).
**Owning service:** `GateService` (`Humans.Gate.Services`) — also implements `IUserMerge`,
`IUserDataContributor`.
**Owned tables:** `gate_scan_events`, `gate_settings`, `gate_staff_pins` via `IGateRepository`.
**Jobs:** both internal and owned by `Humans.Gate/Jobs/`. `GateRetentionJob` (recurring) is registered by the
section's own `ISectionJobs` (`ISectionJobs`, discovered by Shell — cron `45 3 * * *`);
`GateVendorCheckInJob` is enqueued fire-and-forget on admit (Hangfire serializes the method
reference, so it needs no registration).
**Decorator decision:** none — gate reads must be live (a stale verdict admits or blocks the wrong
person), mirroring the read-through Scanner section.
**Layout:** the tablet-facing views in `Views/Gate/` (`Claim`, scan terminal `Index`, `Leaderboard`)
use the **chromeless kiosk layout** `_GateLayout` (section's own `Views/Shared/`), set by
`Views/Gate/_ViewStart.cshtml`: full-bleed, no admin nav/sidebar/breadcrumb. The Staff PINs page
(`Admin`) overrides back to Shell's `_AdminLayout`. `Views/GateReport/Index.cshtml` (Arrivals report,
a desktop page) and the vendor check-in backfill page live outside `Views/Gate/`, so `_GateLayout`
does not apply to them; they get the Shell's layout rule. The shared `GateTerminal` system account
(no roles/teams) sees only this kiosk; on the device, Edge Assigned Access removes browser chrome too.
**Static assets:** `wwwroot/css/gate/` and `wwwroot/js/gate/` ship with the RCL and are served
at `/_content/Humans.Gate/…`. `GatePageRenderTests` (in
`Humans.Integration.Tests`, a PR check) asserts both the `?v=` cache-buster and a
200 on each file.
**Status:** attendance-gateway posture signed off by Peter: a gate admit is the check-in of record
and writes the Attended participation row directly (see Tickets under Cross-Section Dependencies).
