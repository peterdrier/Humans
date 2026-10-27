# Gate — Health

## Target shape

*Derived fresh each doctor run (before any scan), per `.claude/skills/section-doctor/SKILL.md` 3c.*

### What the section does

At the event door, a staffer scans a ticket QR on a rugged kiosk tablet. The system says
ADMIT (green), STOP (red), ASK-FOR-ID (blue), or GET-A-SUPERVISOR (amber), with one reason
line. A supervisor can push through a too-early, unconfirmed-Early-Entry or child-without-ID
admit by typing a shared PIN. The agent's decision on each scan is recorded as the venue's
durable admission record (a refusal that ends at the verdict card writes no row — only
`POST /Gate/Decision` writes); an admit also marks the guest as having attended this year's
event, and is optionally mirrored back to the ticket vendor. Ticket admins set when general
entry opens (and a minor-age number nothing reads yet) on the Settings page. The kiosk shows a
7-day per-scanner tally. Scans older than the retention window are purged; a person's gate
history is exportable and erasable under GDPR; account merges re-point their scans.

### The shapes

| Question | Surface | Notes |
|---|---|---|
| May this barcode enter? | `GET /Gate`, `GET /Gate/Evaluate`, `POST /Gate/Decision` | One flow: scan → verdict card → agent decision → durable row (+ attendance projection, + best-effort vendor mirror) |
| Who scanned what? | `GET /Gate/Leaderboard` | 7-day tallies by scanner id — one row in practice, since every scan attributes to the shared gate account |
| Configure the gate | `/Settings#gate` (tab posts to `POST /Gate/Admin`) | Cutoff + minor age (peterdrier/Humans#1634) |
| Staff PINs *(dormant)* | `GET /Gate/Admin`, `POST /Gate/Admin/SetPin`, `POST /Gate/Admin/ResetPin` | Linked from the admin nav ("Staff PINs"), but the PINs it manages serve only the bypassed claim flow |
| Recover missed vendor mirrors | `GET/POST /Gate/Admin/VendorCheckInBackfill` (`RunOne`, `Run`) | Temp page — remove after use |
| Who is scanning? *(retired)* | `Claim` GET/POST, `ClaimPin`, `EndShift`, `/Gate/Search` | Unreachable since peterdrier/Humans#1075; deletion tracked in nobodies-collective/Humans#933, shelved for the 2027 gate rewrite |
| Housekeeping | `GateRetentionJob`, `GateVendorCheckInJob`, `IUserDataContributor`, `IUserMerge` | Retention purge; fire-and-forget mirror; GDPR export/erase; merge re-point |

### Structure

The reachable shapes imply: one kiosk controller (scan/decide/leaderboard) plus the settings
POST it hosts for the Settings tab, one admin backfill controller, one service holding the
decision orchestration, a pure decision table (`GateAdmissionRules`) kept separate so precedence
is exhaustively testable, one repository over the owned tables (`gate_scan_events`,
`gate_settings`, `gate_staff_pins`), the Hangfire jobs (`GateRetentionJob`,
`GateVendorCheckInJob`), and the in-memory stores (PIN throttle, mirror ledger). No caching
decorator — verdicts must be live. No resx — the kiosk is single-locale staff UI by design. The
personal-PIN subsystem (claim flow, `gate_staff_pins`, the Staff PINs page, roster pre-fill from
Shifts, supervisor-role reads from Auth, `AuthorizeOverrideAsync`) serves no reachable kiosk
flow; Peter shelved its removal with all gate work until the 2027 rewrite (see Seams), so it is
recorded, not a structural need.

### Invariants

- The cutoff comparison uses the server clock only (`Services/GateService.cs:68`);
  `ClientScanAt` is audit data, written and never read (`Services/GateService.cs:436`).
- Unset cutoff → every otherwise-admissible scan AMBERs — invalid and duplicate still STOP
  first (`Services/GateAdmissionRules.cs:47`); the terminal shows a loud banner
  (`Views/Gate/Index.cshtml:19`).
- A barcode admits at most once: unique index on `AdmitDedupeKey`
  (`Data/Configurations/GateScanEventConfiguration.cs:32`), pre-check for the common case
  (`Data/GateRepository.cs:24`); the losing racer records `RejectedDuplicate`
  (`Services/GateService.cs:119`).
- A client flag can never turn a STOP into an admit: `RecordDecisionAsync` re-evaluates
  server-side (`Services/GateService.cs:110`); overrides admit only with `OverrideByUserId` set
  (`Services/GateService.cs:411`), which the controller sets only after server-verifying the
  shared supervisor PIN (`Controllers/GateController.cs:75`), timing-safe compare
  (`Controllers/GateController.cs:327`).
- Unset override PIN fails closed with an explicit "not configured" card
  (`Controllers/GateController.cs:300`).
- Override PIN entry throttles terminal-wide on `override:shared` (`Controllers/GateController.cs:305`):
  `GatePinThrottle.MaxFailures` tries, then a `GatePinThrottle.Lockout` lockout that only a
  correct PIN clears (`Services/Stores/GatePinThrottle.cs:47`); a lockout never blocks scanning.
- Scan attribution is the authenticated principal, never the request body
  (`Controllers/GateController.cs:61`).
- An admit with a matched guest writes the Attended participation row for the active year via
  `ITicketWrite.RecordParticipationAsync`; failure there never fails the admit
  (`Services/GateService.cs:179`).
- Vendor mirror is best-effort, non-retrying (`Jobs/GateVendorCheckInJob.cs:16`), gated by
  `Gate:VendorMirrorEnabled` (`Jobs/GateVendorCheckInJob.cs:37`). The backfill claims the ledger
  atomically before enqueueing (`Controllers/GateVendorBackfillAdminController.cs:86`), so
  overlapping backfill sends never double-post; live admits mark the ledger *after* their
  enqueue (`Services/GateService.cs:148`), so a backfill click in that instant can still re-send
  one — accepted, not a guarantee.
- Write routes carry `GateAdmit` (`Controllers/GateController.cs:55`), read routes
  `ScannerAccess` (`Controllers/GateController.cs:23`); the staff-PIN and settings routes
  `TicketAdminOrAdmin` (`Controllers/GateController.cs:201`); the backfill page `AdminOnly`
  (`Controllers/GateVendorBackfillAdminController.cs:16`). The card carries name + verdict + one
  reason; EE source, prior scanner identity, and GUIDs stay server-side
  (`Models/GateViewModels.cs:22`).
- GDPR: export is data-minimized — no barcode, no other person's ids
  (`Services/GateService.cs:375`); erasure clears `GuestUserId`/`OverrideByUserId` and deletes the
  PIN row but keeps `ScannedByUserId` (`Data/GateRepository.cs:71`); merge re-points every user
  column (`Data/GateRepository.cs:95`).

### Seams

- **Per-person claim flow** (peterdrier/Humans#1075 bypassed it; nobodies-collective/Humans#933 tracks
  deletion, shelved by Peter with all gate work until the 2027 rewrite): the
  `Claim`/`ClaimPin`/`EndShift`/`Search` actions, `gate_staff_pins` + PIN service methods,
  `AuthorizeOverrideAsync`, roster pre-fill (`Gate:RosterTeamId`, Shifts read), and the Staff
  PINs page all exist for a flow that may or may not return. Until then, changes here neither
  extend nor delete that subsystem.
- **Minor-age threshold**: stored and edited on `/Settings#gate`, read by no admission or kiosk
  code. The child-without-ID waiver is a supervisor-PIN override, not an age comparison.
- **Vendor check-in backfill page** is explicitly temporary ("remove after use").

### Deliberately not done

- No caching decorator: a stale verdict admits or blocks the wrong person.
- No localization/resx: staff-facing single-locale terminal, inline English by design.
- No per-supervisor attribution on overrides: one shared PIN authorizes but cannot attribute —
  accepted trade (doc'd in Gate.md).
- No retry on the vendor mirror (`Attempts = 0`): non-idempotent vendor API; a lost mirror is
  acceptable, `gate_scan_events` is authoritative.
- No FK/navigation to other sections' tables: bare Guid columns per the cross-section rule.
- No Postgres-backed race test for the dedupe index: EF-InMemory tests are the project's
  standard (`memory/process/ef-inmemory-is-fine.md`); the pre-check path is what the tests pin.
- No un-admit / void: admits are irreversible today; nobodies-collective/Humans#906 carries it
  as a requirement for the 2027 rewrite.

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
- Kiosk JS: quiet-timer submit (no reliance on wedge Enter-suffix), anti-mistap arming delay,
  typing `logout` is the only way off the route-locked gate account.
- Both jobs are `public` (HUM0034 allows nothing else public; Hangfire needs the concrete
  types — the retention job via the section's own `SectionJobs` registration, the mirror job
  via the enqueue's serialized method reference). That is also why `IGateScanRetention` is a
  public Contracts interface with no caller outside the section: a public job cannot take the
  internal `IGateService`.
- The settings form lives in the Settings tab view component but posts to `GateController`'s
  `Admin` action, naming the controller explicitly — inside `/Settings` the ambient route would
  resolve `Admin` against the wrong controller.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-31 | First pass: comments/docs/UI copy re-anchored to the shipped shared-PIN security model; dead override-picker client remnants cut; GDPR-erasure, merge, retention and deny-path invariants pinned | peterdrier/Humans#1574 |
| 2 | 2026-10-27 | Kiosk and docs point at the real cutoff setting; comments stop claiming an age waiver and live per-staffer override authority; export minimization and server-time verdicts pinned | pending |
