<!-- freshness:triggers
  src/Sections/Humans.Gate/**
-->
<!-- freshness:flag-on-change
  Historical design draft for the Gate section — src/Sections/Humans.Gate/Docs/Gate.md is the authoritative
  current-state doc. Review the Status note and the "resolved" annotations when Gate services,
  entities, controller, or jobs change materially.
-->

# Gate Admissions — gate ticket scanning that decides entry

> Status: **SHIPPED**. This draft is kept for the rationale. The authoritative current-state doc is
> [`Gate.md`](../Gate.md); where this draft and shipped behaviour disagree, that doc wins.
> Type/namespace names below (e.g. `Humans.Domain.Entities.*`, `Humans.Infrastructure.*`) are the
> draft-era layout and no longer exist; the section now owns its types under `Humans.Gate.*`.

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
  on account merge); as an `IRetentionPeriodContributor` it deletes scans older than the app-wide retention period.
- **Vendor check-in mirror** — `ITicketVendorMirror.CreateCheckInAsync`
  (+ `TicketTailorService` `POST /v1/check_ins` and `StubTicketVendorService` impls);
  `GateVendorCheckInJob` enqueued fire-and-forget on admit. `gate_scan_events` stays
  the dedupe authority; the vendor POST is only a dashboard/offline-app mirror.
  Enabled jobs check shutdown before starting and finish a started POST independently of cancellation;
  vendor failures remain best-effort without retries because check-ins are not idempotent.
- **Terminal UI + auth** — `GateController` (`/Gate`, `ScannerAccess`; settings
  `TicketsPolicies.Manage`), tap-to-claim (session-derived `scannedByUserId`), merged
  ID-confirm (green only on Yes), supervisor-PIN-gated child waiver, leaderboard, and
  the tablet JS/CSS (colour-blind-safe verdicts, anti-mistap Yes/No, sounds/haptics,
  neutral flash, keyboard-wedge auto-submit).
- Service + unit tests; a Gate architecture test; GDPR-contributor wiring tests.

## Remaining / open

- **Name-mismatch fix link** — on a `RejectedNameMismatch`, auto-email the holder's
  per-attendee email (`TicketAttendee.AttendeeEmail`, the custom-field email, not
  the buyer email) a link to fix the name (sign-in / create account).

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

`ITicketService.GetTicketOrdersAsync` (find attendee by normalized barcode),
`IEarlyEntryService.GetForUserAsync` (EE date + sources),
`ISettingsService.GetActiveEventSettings` (event time zone → "today"), `IClock` (cutoff).

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
