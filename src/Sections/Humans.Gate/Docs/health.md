# Gate — Health

## Target shape

*Derived fresh each doctor run (before any scan), per `.claude/skills/section-doctor/SKILL.md` 3c.*

### What the section does

At the event door, a staffer scans a ticket QR on a rugged kiosk tablet. The system says
ADMIT (green), STOP (red), ASK-FOR-ID (blue), or GET-A-SUPERVISOR (amber), with one reason
line. A supervisor can push through a too-early or child-without-ID admit by typing a shared
PIN. The agent's decision on each scan is recorded as the venue's durable admission record
(a refusal that ends at the verdict card writes no row — only `POST /Gate/Decision` writes); an admit
also marks the guest as having attended this year's event, and is optionally mirrored back
to the ticket vendor. Admins set when general entry opens and the minor-age threshold, and
can see a per-scanner tally. Scans older than the retention window are purged; a person's
gate history is exportable and erasable under GDPR; account merges re-point their scans.

### The shapes

| Question | Surface | Notes |
|---|---|---|
| May this barcode enter? | `GET /Gate`, `GET /Gate/Evaluate`, `POST /Gate/Decision` | One flow: scan → verdict card → agent decision → durable row (+ attendance projection, + best-effort vendor mirror) |
| Who scanned what? | `GET /Gate/Leaderboard` | 7-day tallies by scanner id |
| Configure the gate | `GET/POST /Gate/Admin`, `POST /Gate/Admin/SetPin`, `POST /Gate/Admin/ResetPin` | Cutoff + minor age; staff-PIN enrol/reset |
| Recover missed vendor mirrors | `GET/POST /Gate/Admin/VendorCheckInBackfill` (`RunOne`, `Run`) | Temp page — remove after use |
| Who is scanning? *(retired)* | `Claim` GET/POST, `ClaimPin`, `EndShift`, `/Gate/Search` | Unreachable since peterdrier#1075; deletion tracked upstream (nobodies-collective/Humans#933) |
| Housekeeping | `GateRetentionJob`, `GateVendorCheckInJob`, `IUserDataContributor`, `IUserMerge` | Retention purge; fire-and-forget mirror; GDPR export/erase; merge re-point |

### Structure

The reachable shapes imply: one kiosk controller (scan/decide/leaderboard) plus one admin
surface (settings, backfill), one service holding the decision orchestration, a pure decision
table (`GateAdmissionRules`) kept separate so precedence is exhaustively testable, one
repository over three owned tables, two Hangfire jobs, and two in-memory stores (PIN throttle,
mirror ledger). No caching decorator — verdicts must be live. No resx — the kiosk is
single-locale staff UI by design. The personal-PIN subsystem (claim flow, `gate_staff_pins`,
roster pre-fill from Shifts, supervisor-role reads from Auth) serves no reachable page today;
its continued presence is a pending ruling (see Seams), not a structural need.

### Invariants

- The cutoff comparison uses the server clock only; `ClientScanAt` is audit data.
- Unset cutoff → every otherwise-admissible scan AMBERs — invalid and duplicate still STOP
  first (never a silent admit); the terminal shows a loud banner.
- A barcode admits at most once: unique index on `AdmitDedupeKey` (atomic), pre-check for the
  common case; the losing racer records `RejectedDuplicate`.
- A client flag can never turn a STOP into an admit: `RecordDecisionAsync` re-evaluates
  server-side; overrides admit only with `OverrideByUserId` set, which the controller sets only
  after server-verifying the shared supervisor PIN (`Gate:SupervisorPin`), timing-safe compare.
- Unset override PIN fails closed (explicit "not configured" card).
- Override PIN entry throttles terminal-wide (5 tries / 15-min non-self-resetting lockout on
  `override:shared`); a lockout never blocks scanning.
- Scan attribution is the authenticated principal, never the request body.
- An admit with a matched guest writes the Attended participation row for the active year via
  `IUserService.SetParticipationFromTicketSyncAsync`; failure there never fails the admit.
- Vendor mirror is best-effort, non-retrying (TicketTailor check-ins double-record), gated by
  `Gate:VendorMirrorEnabled`, and ledger-claimed atomically so live path + backfill never
  double-post.
- Write routes carry `GateAdmit`, read routes `ScannerAccess`; the admin pages
  `TicketAdminOrAdmin`; the backfill page `AdminOnly`. The card shows name + verdict + one
  reason; EE source, prior scanner identity, and GUIDs stay server-side.
- GDPR: export is data-minimized (no barcode, no other person's ids); erasure clears
  `GuestUserId`/`OverrideByUserId` and deletes the PIN row but keeps `ScannedByUserId`
  (Art. 17(3) operational record); merge re-points all three user columns.

### Seams

- **Per-person claim flow** (peterdrier#1075 bypassed it; upstream #933 tracks deletion): the
  `Claim`/`ClaimPin`/`EndShift`/`Search` actions, `gate_staff_pins` + PIN service methods,
  roster pre-fill (`Gate:RosterTeamId`, Shifts read), and the admin PIN enrolment UI all exist
  for a flow that may or may not return. Until Peter rules, changes here should neither extend
  nor delete that subsystem.
- **Vendor check-in backfill page** is explicitly temporary ("remove after use").

### Deliberately not done

- No caching decorator: a stale verdict admits or blocks the wrong person.
- No localization/resx: staff-facing single-locale terminal, inline English by design.
- No per-supervisor attribution on overrides: one shared PIN authorizes but cannot attribute —
  accepted trade (doc'd in Gate.md).
- No retry on the vendor mirror (`Attempts = 0`): non-idempotent vendor API; a lost mirror is
  acceptable, `gate_scan_events` is authoritative.
- No FK/navigation to other sections' tables: bare Guid columns per the cross-section rule.
- No Postgres-backed race test for the dedupe index (EF in-memory can't enforce unique
  indexes) — tracked as known debt, not a gap to fill ad hoc.

### Load-bearing weirdness

- `Instant.MinValue` as the "cutoff not configured" sentinel, surfaced as `IsCutoffConfigured`
  — the fail-safe AMBER behavior hangs off it.
- The duplicate-race loser records a *second* row (`RejectedDuplicate`) — append-only log
  wants the attempt recorded, not swallowed.
- `AdminEnrolled` on `gate_staff_pins` deliberately has no EF default value — every write sets
  it explicitly (bool-sentinel pitfall).
- `GateVendorCheckInJob` method signatures are frozen (Hangfire serializes method references);
  the backfill variant is a separate method on purpose.
- The mirror ledger marks live admits only while `Gate:VendorMirrorEnabled` is on — marking
  while off would hide exactly the rows the backfill exists to recover.
- Kiosk JS: quiet-timer submit (no reliance on wedge Enter-suffix), 350 ms anti-mistap arming,
  typing `logout` is the only way off the route-locked gate account.
- Both jobs are `public` (HUM0034 allows nothing else public; Hangfire needs the concrete
  types — the retention job via the section's own `SectionJobs` registration, the mirror job
  via the enqueue's serialized method reference).

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-31 | First pass: comments/docs/UI copy re-anchored to the shipped shared-PIN security model; dead override-picker client remnants cut; GDPR-erasure, merge, retention and deny-path invariants pinned | peterdrier/Humans#1574 |
