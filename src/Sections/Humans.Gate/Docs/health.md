# Gate — Health

## Target shape

### What the section does

At the event door, a staffer scans a ticket QR on a rugged kiosk tablet. The system answers
ADMIT (green), STOP (red), ASK-FOR-ID (blue) or GET-A-SUPERVISOR (amber), with one reason
line. Tickets come from two places, the ticket vendor and Humans' own checkout, and both end in
the same check-in record. A guest without their code is found by name. A supervisor can push
through a too-early, unconfirmed-Early-Entry or child-without-ID admit, or undo an admit made by
mistake, by typing a shared PIN. The ticket team sees arrivals per event day and can undo a
check-in with a stated reason. Each agent decision is the venue's durable admission record; an
admit also marks the guest as having attended this year's event, is optionally mirrored to the
ticket vendor, and stops the ticket from being resold. Ticket admins set when general entry opens
on the Settings page. Scans older than the retention window are purged; a person's gate history
is exportable and erasable; account merges re-point their scans.

### The shapes

| Question | Surface | Notes |
|---|---|---|
| May this ticket enter? | `GET /Gate`, `GET /Gate/Evaluate`, `POST /Gate/Decision` | Scan → verdict card → agent decision → durable row (+ attendance, + best-effort vendor mirror) |
| Which ticket is this guest's? | `GET /Gate/Find` | Name search; a pick runs the normal scan |
| Take an admit back | `POST /Gate/Undo` (kiosk, PIN), `POST /Gate/Report/Undo` (ticket team, reason) | One undo path in the service, two ways to authorize it |
| Who has arrived? | `GET /Gate/Report` | Per event day, against tickets held |
| Has this ticket been used? | `IGateAdmissions.GetAdmittedAsync` | Tickets' read before it offers or sells a returned ticket |
| Who scanned what? | `GET /Gate/Leaderboard` | 7-day tallies; one row in practice (shared gate account) |
| Configure the gate | `/Settings#gate`, posting to `POST /Gate/Admin` | Cutoff + minor age |
| Staff PINs *(dormant)* | `GET /Gate/Admin`, `POST /Gate/Admin/SetPin`, `POST /Gate/Admin/ResetPin` | No nav entry; serves only the bypassed claim flow |
| Recover missed vendor mirrors *(temporary)* | `GET/POST /Gate/Admin/VendorCheckInBackfill` (`RunOne`, `Run`) | Remove after use |
| Who is scanning? *(retired)* | `Claim` GET/POST, `ClaimPin`, `EndShift`, `/Gate/Search` | Unreachable; kept, deletion shelved under nobodies-collective/Humans#933 for the 2027 rewrite |
| Housekeeping | `GateRetentionJob`, `GateVendorCheckInJob`, `IUserDataContributor`, `IUserMerge` | Purge, mirror, GDPR, merge |

### Structure

The reachable shapes imply: a kiosk controller (scan, decide, find, undo, leaderboard, plus the
settings POST the Settings tab targets), a report controller for the ticket team, the temporary
backfill controller, one service holding every decision (evaluate, record, undo, report, search),
the pure decision table (`GateAdmissionRules`) kept apart so its precedence is testable on its own,
a small admit reader for Tickets (`GateAdmissionReads`, separate because one class would be a DI
cycle with Tickets), one repository over the owned tables, the two jobs, and the in-memory stores
(PIN throttle, mirror ledger). No caching decorator, no resx. The personal-PIN subsystem serves no
reachable flow and is shelved, not structural (see Seams).

### Invariants

- The cutoff is compared with the server clock (`Services/GateService.cs:69`); `ClientScanAt` is
  written as audit data and the kiosk always sends null (`Controllers/GateController.cs:67`).
- Unset cutoff → every otherwise-admissible scan is AMBER; invalid and duplicate still STOP first
  (`Services/GateAdmissionRules.cs:47`); the terminal shows a banner (`Views/Gate/Index.cshtml:19`).
- A barcode has at most one live admit: unique index on `AdmitDedupeKey`
  (`Data/Configurations/GateScanEventConfiguration.cs:34`), pre-check (`Data/GateRepository.cs:36`);
  the losing racer records `RejectedDuplicate` (`Services/GateService.cs:173`).
- A client flag never turns a STOP into an admit: the decision is re-evaluated server-side and an
  override verifies the shared PIN first (`Services/GateService.cs:162`); unset PIN refuses, a
  failed PIN counts against the terminal-wide throttle (`Services/GateService.cs:205`).
- Undo needs the shared PIN at the kiosk (`Services/GateService.cs:324`) or `TicketsPolicies.Manage`
  plus a reason on the report (`Controllers/GateReportController.cs:46`); it is refused while the
  vendor holds or may receive the check-in (`Services/GateService.cs:353`), frees the barcode and
  is audited (`Data/GateRepository.cs:61`, `Services/GateService.cs:360`).
- Undo reverts Attended only when no other live admit of the guest stands within the event window
  (`Services/GateService.cs:403`).
- Scan and undo attribution is the authenticated principal, never the request body
  (`Controllers/GateController.cs:68`, `Controllers/GateController.cs:93`).
- An admit with a matched guest writes Attended for the active year; a failure there never fails
  the admit (`Services/GateService.cs:184`).
- Vendor mirror is best-effort and never retried (`Jobs/GateVendorCheckInJob.cs:16`), off unless
  `Gate:VendorMirrorEnabled` (`Jobs/GateVendorCheckInJob.cs:29`); backfill claims the ledger before
  enqueueing (`Services/GateService.cs:539`); checkout-issued tickets never mirror
  (`Services/GateService.cs:498`).
- Name search never matches email and shows only the ticket's own name
  (`Services/GateService.cs:290`, `Services/GateService.cs:299`).
- Write routes carry `GateAdmit` (`Controllers/GateController.cs:49`), kiosk reads `ScannerAccess`
  (`Controllers/GateController.cs:22`), the report `TicketsPolicies.Read`
  (`Controllers/GateReportController.cs:16`), staff PINs and settings `TicketsPolicies.Manage`
  (`Controllers/GateController.cs:209`), the backfill page `AdminOnly`
  (`Controllers/GateVendorBackfillAdminController.cs:12`).
- GDPR: export carries no barcode or other person's ids (`Services/GateService.cs:663`); erasure
  clears `GuestUserId`/`OverrideByUserId` and the PIN row (`Data/GateRepository.cs:93`); merge
  re-points every user column (`Data/GateRepository.cs:113`).

### Seams

- **Per-person claim flow** (unreachable; kept under nobodies-collective/Humans#933 until the 2027
  rewrite): `Claim`/`ClaimPin`/`EndShift`/`Search`,
  `gate_staff_pins` and its service methods, `AuthorizeOverrideAsync`, roster pre-fill
  (`Gate:RosterTeamId`, Shifts read) and the Staff PINs page. Changes neither extend nor delete it.
- **Minor-age threshold**: stored and edited, read by nothing.
- **Vendor check-in backfill page**: temporary.
- **Demo story**: none yet (`Section.cs` carries `NoDemoStory`).

### Deliberately not done

- No caching decorator: a stale verdict admits or blocks the wrong person.
- No resx: staff-facing single-locale terminal; the report is a ticket-team admin page.
- No per-supervisor attribution: one shared PIN authorizes but cannot attribute.
- No vendor-mirror retry (`Attempts = 0`): check-ins are not idempotent.
- No vendor undo: Ticket Tailor has none, so a vendor-held admit is not undone in Humans.
- No event column on scans: a scan belongs to the event its ticket belongs to.
- No FK or navigation to other sections' tables.
- No Postgres-backed race test for the dedupe index (`memory/process/ef-inmemory-is-fine.md`).

### Load-bearing weirdness

- `Instant.MinValue` is the "cutoff not configured" sentinel behind `IsCutoffConfigured`; the
  fail-safe AMBER hangs off it.
- The duplicate-race loser records a second row (`RejectedDuplicate`): the attempt is kept.
- Undo nulls `AdmitDedupeKey` on the row instead of writing a new one; "live admit" means the key
  is set (`GateScanEvent.IsLiveAdmit`).
- `AdminEnrolled` on `gate_staff_pins` has no EF default: every write sets it.
- The mirror ledger marks live admits only while the mirror flag is on; marking while off would hide
  the rows the backfill exists to recover.
- `GateVendorCheckInJob.ExecuteAsync` serves live and backfill sends with the original admit time.
- Kiosk JS: quiet-timer submit, anti-mistap arming delay, typing `logout` is the only way off the
  route-locked account, the find panel clears after 60 s idle.
- The Settings tab posts to `GateController`'s `Admin` action by explicit controller name, since
  inside `/Settings` the ambient route resolves `Admin` against the wrong controller.
- The "Terminal" admin nav entry points at Tickets' `TicketsGateAdmin` page (Tickets owns the
  terminal account).

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-31 | First pass: comments/docs/UI copy re-anchored to the shipped shared-PIN security model; dead override-picker client remnants cut; GDPR-erasure, merge, retention and deny-path invariants pinned | peterdrier/Humans#1574 |
| 2 | 2026-10-27 | Kiosk and docs point at the real cutoff setting; comments stop claiming an age waiver and live per-staffer override authority; export minimization and server-time verdicts pinned | peterdrier/Humans#2168 |
| 3 | 2026-11-24 | Docs and authorization spec catch up with undo, find by name and the arrivals report; report policies, undo merge re-point and the 30-day arrival window pinned | peterdrier/Humans#2604 |
