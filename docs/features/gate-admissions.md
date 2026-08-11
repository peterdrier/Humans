<!-- freshness:triggers
  src/Sections/Humans.Gate/**
  src/Humans.Infrastructure/Jobs/GateRetentionJob.cs
  src/Humans.Infrastructure/Jobs/GateVendorCheckInJob.cs
-->
<!-- freshness:flag-on-change
  Historical design draft for the Gate section — src/Sections/Humans.Gate/Docs/Gate.md is the authoritative
  current-state doc. Review the Status note and the "resolved" annotations when Gate services,
  entities, controller, or jobs change materially.
-->

# Gate Admissions — gate ticket scanning that decides entry

> Status: **SHIPPED** (peterdrier#1066 plus follow-ups) — this design draft is kept for the
> rationale. The posture change and the new public surface were signed off by Peter
> (attendance-gateway posture 2026-07-02). The authoritative current-state doc is
> [`Gate.md`](../../src/Sections/Humans.Gate/Docs/Gate.md); where this draft and shipped behaviour
> disagree, that doc wins. Notable post-draft changes: personal staff/supervisor PINs were
> shipped, then disabled (peterdrier#1075) in favour of a shared override PIN
> (`Gate:SupervisorPin`, peterdrier#1079); scans attribute to the shared gate account (no
> claim step); an admit writes the guest's Attended participation row (peterdrier#1081); a
> mirror ledger + one-off vendor check-in backfill page recover unsent vendor check-ins
> (peterdrier#1080–#1083).

## Why

At the event gate, staff scan a Ticket Tailor QR on a rugged tablet and must
decide entry against three layers:

1. **Ticket valid** — a current-event Ticket Tailor ticket, not void/refunded, not already used.
2. **Name matches government photo ID** — a manual, no-exceptions check by the agent.
3. **Early Entry** — before the general-entry cutoff (noon Mon 6 Jul 2026), the
   holder also needs an Early Entry grant on Humans covering today.

The existing `Scanner` section is, by documented invariant, a **read-only lookup
tool** ("not a check-in tool", "no owned tables", "do not add POST actions"). It
cannot host an admission/check-in capability. This feature therefore lives in a
**new `Gate` section**.

## Architectural decision (resolved — signed off by Peter, 2026-07-02)

The app today deliberately is **not an attendance gateway**: ticket check-in
state is inbound-only (synced from Ticket Tailor), and `Scanner` is read-only.
This feature makes Humans the **system of record for gate admission** via an
append-only `gate_scan_events` table. That is a real posture change. It is
implemented as a new vertical section so the `Scanner` invariant is untouched,
but the decision to admit-and-record from inside Humans is yours to bless.

## What's in this PR (Phase 1a — backend, tested)

- **`Gate` section** (`Humans.Application.{Interfaces,Services}.Gate`,
  `Humans.Infrastructure.Repositories.Gate`, `Humans.Domain.Entities.GateScanEvent`
  / `GateSettings`).
- **Pure admission rules** (`GateAdmissionRules.Evaluate`) — the precedence below,
  exhaustively unit-tested.
- **`GateScanEvent`** — append-only audit + leaderboard source + the atomic
  duplicate guard.
- **`GateSettings`** — singleton, server-side general-entry cutoff `Instant`
  (not a calendar date) + minor age threshold.
- **`IGateService`** — evaluate a scan, record the agent's decision, leaderboard,
  settings.
- **Server-authoritative dedupe** — unique index on a per-admit dedupe key plus an
  explicit pre-check.
- **GDPR / data-lifecycle** — `GateService` implements `IUserDataContributor`
  (data-minimized DSAR export) and `IUserMerge` (re-FKs `GuestUserId`/`ScannedByUserId`
  on account merge); `GateRetentionJob` purges scans older than `Gate:RetentionDays`.
- **Vendor check-in mirror** — `ITicketVendorService.CreateCheckInAsync`
  (+ `TicketTailorService` `POST /v1/check_ins` and `StubTicketVendorService` impls);
  `GateVendorCheckInJob` enqueued fire-and-forget on admit. `gate_scan_events` stays
  the dedupe authority; the vendor POST is only a dashboard/offline-app mirror.
- **Terminal UI + auth** — `GateController` (`/Gate`, `ScannerAccess`; settings
  `TicketAdminOrAdmin`), tap-to-claim (session-derived `scannedByUserId`), merged
  ID-confirm (green only on Yes), supervisor-PIN-gated child waiver, leaderboard, and
  the tablet JS/CSS (colour-blind-safe verdicts, anti-mistap Yes/No, sounds/haptics,
  neutral flash, keyboard-wedge auto-submit).
- Service + unit tests; a Gate architecture test; GDPR-contributor wiring tests.

## Remaining / open (for Peter)

- **Posture sign-off** + approval of the new public surface (`ITicketVendorService.CreateCheckInAsync`,
  the Gate section interfaces).
- **Runtime smoke test** — built but not run here; the Razor views + JS flow need a live
  smoke test against a DB before merge.
- **Localization** — gate views use plain English (staff/ops tool); wire `@Localizer` if wanted.
- **Hardening to consider** — a dedicated `GateAdmit` policy (stronger than read-only
  `ScannerAccess`), a chromeless kiosk layout. _Both shipped: `GateAdmit` gates the
  write actions and the terminal uses the chromeless `_GateLayout`._
- **Name-mismatch fix link** — on a `RejectedNameMismatch`, auto-email the holder's
  per-attendee email (`TicketAttendee.AttendeeEmail`, the custom-field email, not
  the buyer email) a link into the existing transfer flow (sign-in / create
  account). Reuses Peter's `TicketTransferService`.

## Admission precedence (`GateAdmissionRules.Evaluate`)

First match wins, computed fresh per scan against the **server** clock:

1. not found / void / refunded → **STOP (invalid)**
2. already admitted locally / already checked in at vendor → **STOP (duplicate)**
3. cutoff not configured → **AMBER (fail-safe)** — Early-Entry gating is undecidable,
   so never a silent admit while the cutoff is unset (added post-draft)
4. general entry open (`now ≥ cutoff`) → **needs ID check**
5. before cutoff, ticket not matched to a Human → **AMBER (Early Entry unknown)** —
   supervisor + name search; never a silent too-early stop
6. before cutoff, matched, Early Entry covers today → **needs ID check (early)**
7. before cutoff, matched, no covering Early Entry → **STOP (too early)**

The ID check is then an explicit, recorded Yes/No: **Yes** → `Admitted` /
`AdmittedEarly` (and only then is the admit recorded); **No** → `RejectedNameMismatch`
(ticket not burned); **Child with adult** → `AdmittedChildWithAdult`.
`RecordDecisionAsync` re-evaluates server-side, so a client "ID confirmed" can
never turn a STOP into an admit.

## Data model

- **`gate_scan_events`** — append-only. `OccurredAt` (server), `ScannedByUserId`,
  `Barcode`, `TicketAttendeeId?`, `GuestUserId?`, `Verdict`, `LaneId?`,
  `ClientScanAt?` (audit only, never trusted for the cutoff), `Note?`,
  `AdmitDedupeKey?`. Cross-section links are bare Guid columns (no nav/FK), per
  `no-cross-section-ef-joins`. Unique index on `AdmitDedupeKey` = at most one admit
  per barcode, atomically, across all lanes (Postgres excludes nulls so rejects
  never collide). An explicit pre-check provides the same guard under the EF
  in-memory test provider.
- **`gate_settings`** — singleton (`Id` = 1). `GeneralEntryOpensAt` (UTC `Instant`),
  `MinorAgeThresholdYears`. Default = the `Instant.MinValue` "not configured" sentinel:
  every scan fails safe to AMBER (`CutoffNotConfigured`) until an admin sets a real
  cutoff — never "general entry already open" by default (changed post-draft).

## Cross-section reads (via existing interfaces only — no new surface)

`ITicketServiceRead.GetTicketOrdersAsync` (find attendee by normalized barcode),
`IEarlyEntryService.GetForUserAsync` (EE date + sources),
`IBurnSettingsService.GetActiveAsync` (event time zone → "today"), `IClock` (cutoff).

## Tablet / operations (not code)

OneRugged M80N, Windows 11: Edge kiosk via Assigned Access; built-in imager in
keyboard-wedge mode + Enter suffix; 4G LTE as wifi failover; mains/power-bank +
hot-swap spare battery; brightness ~50–60%. Wristband issued on every green admit
governs re-entry (QR scan stays one-time). Pre-doors self-test: scan a known-good
barcode and confirm a green verdict before opening.

## Testing

- `GateAdmissionRulesTests` — every precedence branch + boundaries (13).
- `GateServiceTests` — admit, dedupe-on-rescan, name-mismatch (ticket not burned),
  child-with-adult, too-early (even with ID confirmed), unmatched→amber, early
  admit, leaderboard tally.
- `GateArchitectureTests` — namespace, repository-only DB access, read-interface
  ticket reads.

## Peer review — folded in vs. required-before-go-live

Two independent reviews (correctness/architecture + security/privacy/ops) ran on this slice. Folded in already:

- `scannedByUserId` removed from the wire DTO (`GateDecisionInput`) — now a server-derived
  parameter on `RecordDecisionAsync`, so audit attribution can't be forged from the request body.
- Gate added to the section index and the repository-ownership map.

**Controller MUST enforce when the UI lands (do not merge the UI without these):**

- `ScannerAccess` (or a stronger dedicated `GateAdmit`) policy on every endpoint; anti-CSRF on POSTs.
- `scannedByUserId` taken from the authenticated session, never the request body.
- `ChildWithAdult` (the ID-waiver path) gated behind a server-verified supervisor PIN and recorded with the supervisor's id.
- Agent view-model projected to **name + verdict only** — keep `EarlyEntrySource`,
  `PreviousAdmitByUserId`, and internal GUIDs server-side (don't serialize them to the tablet).

**GDPR / data-lifecycle follow-ups (before this is the live system of record):**

- Implement `IUserMerge` for Gate (re-FK `GuestUserId` / `ScannedByUserId` on account merge) — otherwise a merge orphans gate rows.
- Implement `IUserDataContributor` so gate entries appear in DSAR exports (requires registering the contributor in `GdprExportDependencyInjectionTests.ExpectedContributorTypes` + DI forwarding).
- Define a retention period + anonymization/purge job and record the lawful basis (this is attendance/movement data).

**Test notes:**

- The dedupe's atomic unique-index backstop can't be exercised by the EF in-memory test provider; the explicit pre-check path *is* tested. A Postgres-backed integration test for the concurrent-admit race is a follow-up.
- Unrelated pre-existing failure on `main` (confirmed on clean `upstream/main`, not introduced here): `ShiftSignupServiceEarlyEntryTests.SignUpRangeAsync_WarnsWhenLaterEarlyEntryDayIsFull`.

## Open decisions for Peter (all since resolved)

1. Bless the attendance-gateway posture change (new `Gate` section). _Done —
   signed off 2026-07-02._
2. Approve the deferred new surface: `ITicketVendorService.CreateCheckInAsync`
   (+ impls) and the `ITicketService` seam Gate calls for the writeback mirror.
   _Done — shipped._
3. Confirm `gate_scan_events` retention / GDPR basis (it's a movement record).
   _Done — `GateRetentionJob` purges after `Gate:RetentionDays` (default 365)._
4. Whether to add a PIN-gated supervisor override for the AMBER / name-mismatch
   paths (currently strict: stop + async fix link). _Done, after iteration —
   shipped as per-staffer enrolled PINs, then replaced by the shared override PIN
   (`Gate:SupervisorPin`) on the too-early / unconfirmed-EE / child-waiver paths
   (peterdrier#1075 / peterdrier#1079)._
