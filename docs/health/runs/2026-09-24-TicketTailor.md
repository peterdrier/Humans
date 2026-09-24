# section-doctor — TicketTailor — 2026-09-24

- Invocation: unattended daily routine, no arguments (Phase 8 skipped per routine prompt)
- Anchor commit: `8a4567d53` (origin/main at branch point); branch `section-doctor/2026-09-24T011651Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1817

## Assessment summary

Re-doctor from BASE `d2154c7d6`. Since the first run, Tickets took over the vendor event-summary cache (peterdrier/Humans#1738) and the adapter lost its `IMemoryCache`; the docs, target and one test fixture still described the old arrangement. The target was regenerated from behaviour: the section moved (the cache left), the earlier target was not wrong. New in this target: invariants cite their enforcing lines, one mapping per wire record, a methods-then-helpers-then-records layout, and `BuildSampleData` recorded as load-bearing.

Independence check: pass — items 2, 6 and 10 come from the target (one mapping per wire record, the client's layout, the per-page timing weirdness).

- 2026-09-24 session: the first `reforge surface-score` ran before any build and refused on an unrestored solution; it was re-run after `dotnet build` and reported the section with `BuildSampleData` as its only material score.
- 2026-09-24 session: no UI in this section, so no render was needed; every strike is covered by `tests/Humans.TicketTailor.Tests`.
- 2026-09-24 session: the routine prompt said to skip Phase 8; this run followed it.

## Findings

1. Docs still describe the adapter as caching the event summary under `CacheKeys.TicketEventSummary` (`Docs/TicketTailor.md` Base line, `Docs/health.md` purpose, Snapshot row and a weirdness bullet), the test csproj comment names a MemoryCache, and the invariant doc's freshness triggers miss `CachingTicketVendorService.cs`.
2. `TtIssuedTicket` is mapped to `VendorTicketDto` twice in `TicketTailorService`; the issue path drops `Barcode`.
3. `SectionRegistrationTests` registers `IMemoryCache` and `IClock`, which neither binding needs since the cache moved.
4. The client summary and the invariant doc say only list and event reads throw `HttpRequestException`; discount codes and check-in do too.
5. The failure-kind switch names `>= 500` and then discards to the same `Transient`.
6. `TicketTailorService` interleaves port methods, private helpers and wire records, and `ResolveAttendeeEmail` is wider than its callers need.
7. `CreateCheckInAsync` did not dispose its form content, unlike void and issue.
8. Comments restate adjacent code or record a superseded value: the timeout comment in `Section.cs`, the stub's check-in and mirror comments, the check-in wire-record comment.
9. No discriminating test for the earliest positive scan and its `created_at` fallback, nor for issue's 401/403/404/429 classification.
10. `GetOrdersAsync` times each page; `GetIssuedTicketsAsync` and `GetCheckInsAsync` time their whole paginated loop, so a long multi-page ticket or check-in sync can log a false timing Error the way orders did before nobodies-collective/Humans#946.
11. Inbox review — peterdrier/Humans#1653: close. The decorator shipped in peterdrier/Humans#1738; the one unmet acceptance line (move `CacheKeys.TicketEventSummary` to Tickets) is moot because nothing reads the key, ledgered as finding 12. nobodies-collective/Humans#946: close. Every proposed fix is in the tree (`TicketSyncService` transient catch for a timeout, the raised client timeout, per-page timing, token isolation); its key-files paths predate the section split.
12. `CacheKeys.TicketEventSummary` and its metadata row in Base have no reader left.
13. `src/Sections/Humans.Tickets/Docs/data-access.md` and a caching invariant in `src/Sections/Humans.Tickets/Docs/Tickets.md` still describe a `TicketEventSummary:{eventId}` cache entry.
14. Off-section ledger rows the Inbox thread flagged: `CENTRAL-23` (re-sync resurrecting erased PII) looks answered by the `PiiErasedAt` guard in `TicketRepository`; `TICKETS-7` calls `EventSummaryCache` hand-rolled when it already extends `TrackedCache`. Not verified to the evidence bar by this run; left for their owners.

## Debt verified

- TTAILOR-1 — closed — the target now records `BuildSampleData` as load-bearing fixture complexity, which is the retirement the row's own blocked_on asked for — `src/Sections/Humans.TicketTailor/Docs/health.md` Load-bearing weirdness, last bullet.

Every row in the section ledger was verified; the cap was not reached.

## Worked

- Findings 1 and 4: docs and the client summary brought in line with the cache move and the full error contract; `health.md` regenerated; freshness trigger added.
- Finding 2: one `ToVendorTicket` mapping for list and issue; `IssueTicketAsync_MapsResponseToVendorTicketDto` now pins `Barcode`. Reviewer (critical tier) approved with a `health.md` anchor refresh, applied; it checked the single construction site, the only consumers, and that the new assertion fails on revert.
- Finding 3: dead registration-test setup cut.
- Finding 5: redundant switch arm dropped.
- Finding 6: client reordered; `ResolveAttendeeEmail` narrowed to private. Reviewer (critical tier) approved; it checked that the sorted diff differs in that one line and that no caller sits outside the class.
- Findings 7 and 8: check-in form content disposed; comment trims.
- Finding 9: tests added for the earliest-scan time and the issue status map.
- Findings 12 and 13: ledgered as `CENTRAL-66` and `TICKETS-8`.

## Skipped

- Finding 10: changes what the timing log reports for the other list reads; Peter's call (Needs Peter). State: kept by Peter.
- Finding 11: existing issues are read-only to a run; verdicts are in Needs Peter. State: both closed on Peter's answer.
- Finding 14: off-section rows; recorded here for their owners, not edited.
- Blocked this run: Gdpr (open section-doctor PR) and Settings (recent pushed branch).

## Retro

**Selector.** TicketTailor was a fair pick: the churn since its last run was the cache move, and the move had left the docs describing the old shape — exactly what a re-doctor exists to catch.

**Wasted motion.** The first reforge run went out before any build and refused on an unrestored solution; starting the build at Phase 1 would have let the tool run once.

**What striking revealed.** Deduping the issued-ticket mapping surfaced that the issue path had silently dropped `Barcode`; the assessment saw the duplication but not the divergence. The reorder moved every `file:line` cite, so anchoring invariants belongs after the last structural strike.

**Target diff.** The section moved rather than the earlier target being wrong: the cache left, and the target now says so under deliberately-not-done. The fixture's complexity moved from the debt ledger into load-bearing weirdness, where the earlier run's own row said it belonged.

## Needs Peter

- [x] 10 — time each page in the issued-ticket and check-in reads too, as orders does? **Keep** (Peter): the whole-loop timing stays.
- [x] 11 — close peterdrier/Humans#1653 and nobodies-collective/Humans#946 as shipped? **Closed** (Peter), both as completed.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.TicketTailor/Contracts/README.md` | reviewed |
| `src/Sections/Humans.TicketTailor/Docs/TicketTailor.md` | changed |
| `src/Sections/Humans.TicketTailor/Docs/data-access.md` | reviewed |
| `src/Sections/Humans.TicketTailor/Docs/debt.yml` | changed |
| `src/Sections/Humans.TicketTailor/Docs/health.md` | changed |
| `src/Sections/Humans.TicketTailor/Humans.TicketTailor.csproj` | reviewed |
| `src/Sections/Humans.TicketTailor/Properties/AssemblyInfo.cs` | reviewed |
| `src/Sections/Humans.TicketTailor/Section.cs` | changed |
| `src/Sections/Humans.TicketTailor/Services/StubTicketVendorService.cs` | changed |
| `src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs` | reviewed |
| `tests/Humans.TicketTailor.Tests/Humans.TicketTailor.Tests.csproj` | changed |
| `tests/Humans.TicketTailor.Tests/SectionRegistrationTests.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Services/StubTicketVendorServiceTests.cs` | reviewed |
| `tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceTests.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceWriteTests.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Services/TicketTailorTestHost.cs` | reviewed |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | opus | 2, 5, 6, 10 |
| Behavior & bugs | main | opus | 7 |
| Freshness | subagent (`doctor-reader`) | opus low | 1, 12, 13 |
| Conformance | subagent (`general-purpose`) | haiku | none — every detector clean |
| Tests | subagent (`doctor-reader`) | opus low | 3, 9 |
| Prose & surface | subagent (`general-purpose`) | haiku | none; InspectCode not run (no tool output) |
| History | subagent (`doctor-reader`) | opus low | 8 |
| Comments | subagent (`doctor-reader`) | opus low | 4, 8 |
| Inbox | subagent (`doctor-reader`) | opus low | 11, 14 |

