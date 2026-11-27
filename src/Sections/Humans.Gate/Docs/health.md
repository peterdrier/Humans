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
admit also marks the guest as having attended this year's event and stops the ticket from being
resold. Ticket admins set when general entry opens on the Settings page. Scans older than the
retention window are purged; a person's gate history is exportable and erasable; account merges
re-point their scans.

### The shapes

| Question | Surface | Notes |
|---|---|---|
| May this ticket enter? | `GET /Gate`, `GET /Gate/Evaluate`, `POST /Gate/Decision` | Scan → verdict card → agent decision → durable row (+ attendance) |
| Which ticket is this guest's? | `GET /Gate/Find` | Name search; a pick runs the normal scan |
| Take an admit back | `POST /Gate/Undo` (kiosk, PIN), `POST /Gate/Report/Undo` (ticket team, reason) | One undo path in the service, two ways to authorize it |
| Who has arrived? | `GET /Gate/Report` | Per event day, against tickets held |
| Has this ticket been used? | `IGateAdmissions.GetAdmittedAsync` | Tickets' read before it offers or sells a returned ticket |
| Configure the gate | `/Settings#gate`, posting to `POST /Gate/Admin` | General-entry cutoff |
| Housekeeping | `GateRetentionJob`, `IUserDataContributor`, `IUserMerge` | Purge, GDPR, merge |

### Structure

A kiosk controller (scan, decide, find, undo, plus the settings POST the Settings tab targets), a
report controller for the ticket team, one service holding every decision (evaluate, record, undo,
report, search), the pure decision table (`GateAdmissionRules`) kept apart so its precedence is
testable on its own, a small admit reader for Tickets (`GateAdmissionReads`, separate because one
class would be a DI cycle with Tickets), one repository over the owned tables, the retention job,
and the PIN throttle. No caching decorator, no resx.

### Invariants

- The cutoff is compared with the server clock (`Services/GateService.cs:51`); `ClientScanAt` is
  written as audit data and the kiosk always sends null (`Controllers/GateController.cs:60`).
- Unset cutoff → every otherwise-admissible scan is AMBER; invalid and duplicate still STOP first
  (`Services/GateAdmissionRules.cs`); the terminal shows a banner (`Views/Gate/Index.cshtml`).
- A barcode has at most one live admit: unique index on `AdmitDedupeKey`
  (`Data/Configurations/GateScanEventConfiguration.cs`), pre-check (`Data/GateRepository.cs:35`);
  the losing racer records `RejectedDuplicate` (`Services/GateService.cs:156`).
- A client flag never turns a STOP into an admit: the decision is re-evaluated server-side and an
  override verifies the shared PIN first (`Services/GateService.cs:142`); unset PIN refuses, a
  failed PIN counts against the terminal-wide throttle (`Services/GateService.cs:174`).
- Undo needs the shared PIN at the kiosk (`Services/GateService.cs:263`) or `TicketsPolicies.Manage`
  plus a reason on the report (`Controllers/GateReportController.cs:46`); it is refused when Ticket
  Tailor's own scanner checked the ticket in, per the synced ticket data
  (`Services/GateService.cs:324`), frees the barcode and is audited (`Data/GateRepository.cs:61`,
  `Services/GateService.cs:302`).
- Undo reverts Attended only when no other live admit of the guest stands within the event window
  (`Services/GateService.cs:336`).
- Scan and undo attribution is the authenticated principal, never the request body.
- An admit with a matched guest writes Attended for the active year; a failure there never fails
  the admit (`Services/GateService.cs:200`).
- Name search never matches email and shows only the ticket's own name
  (`Services/GateService.cs:224`).
- Write routes carry `GateAdmit` (`Controllers/GateController.cs:42`), kiosk reads `ScannerAccess`
  (`Controllers/GateController.cs:18`), the report `TicketsPolicies.Read`
  (`Controllers/GateReportController.cs:16`), settings `TicketsPolicies.Manage`
  (`Controllers/GateController.cs:93`).
- GDPR: export carries no barcode or other person's ids (`Services/GateService.cs:434`); erasure
  clears `GuestUserId`/`OverrideByUserId` (`Data/GateRepository.cs:87`); merge re-points every
  user column (`Data/GateRepository.cs:104`).

### Seams

- **Ticket Tailor scanning**: kept until the 2027 ticketing is live; then the vendor lookup and the
  vendor check-in undo refusal go.
- **Demo story**: none yet (`Section.cs` carries `NoDemoStory`).

### Deliberately not done

- No caching decorator: a stale verdict admits or blocks the wrong person.
- No resx: staff-facing single-locale terminal; the report is a ticket-team admin page.
- No per-supervisor attribution: one shared PIN authorizes but cannot attribute.
- No per-staffer sign-in: the kiosk runs on one shared gate account.
- No check-in mirror to Ticket Tailor: gate admits live in Humans only.
- No vendor undo: Ticket Tailor has none, so a ticket its own scanner checked in is not undone in
  Humans.
- No event column on scans: a scan belongs to the event its ticket belongs to.
- No FK or navigation to other sections' tables.
- No Postgres-backed race test for the dedupe index (`memory/process/ef-inmemory-is-fine.md`).

### Load-bearing weirdness

- `Instant.MinValue` is the "cutoff not configured" sentinel behind `IsCutoffConfigured`; the
  fail-safe AMBER hangs off it.
- The duplicate-race loser records a second row (`RejectedDuplicate`): the attempt is kept.
- Undo nulls `AdmitDedupeKey` on the row instead of writing a new one; "live admit" means the key
  is set (`GateScanEvent.IsLiveAdmit`).
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
