# section-doctor — TicketTailor — 2026-10-10

- Invocation: scheduled unattended daily run, no arguments (routine prompt: skip Phase 8; gh GraphQL unavailable, PR list built via REST)
- Anchor commit: `6f6e1f640` (origin/main at branch point); branch `section-doctor/2026-10-10T011616Z`.
- Budget: 2.5h.
- PR: peterdrier/Humans#1944

## Assessment summary

Re-doctor from BASE `342d9522`. Since the last run, daily debt sweeps moved the three list reads onto one private page walk (`FetchPagesAsync`), timed each page, and added guards against malformed pagination and an issue response without an id. The target was regenerated from behaviour: the section moved (one page walk, new guards), and the earlier target was also wrong on one point — it credited `TicketVendorPortArchitectureTests` with enforcing `internal sealed`, which that test does not check.

Independence check: pass — findings 1, 3 and 10 come from the target (the one page walk, the enforcement cite for the implementations, the stub's two void spellings).

- 2026-10-10 session: `gh pr list` failed (GraphQL is blocked in this cloud environment); the selector's PR list was built from the REST pulls endpoint in the same shape.
- 2026-10-10 session: `reforge surface-score` ran before any build and refused on an unrestored solution; no score was used, and no strike depended on one.
- 2026-10-10 session: no UI in this section, so no render was needed.
- 2026-10-10 session: the routine prompt said to skip Phase 8, ignore the verify-migrations-apply job, and open the PR against peterdrier/Humans; this run followed it. The run branch is `section-doctor/<TS>` per the skill, not the session's default branch.
- 2026-10-10 session: the final trace prints MISS for `links.next`, the `.gte` wire filters and the stub's test-user email. Each was read by hand and is true (`TicketTailorService.cs:127`, `:152`; `StubTicketVendorService.cs:35`); the gate's defect is peterdrier/Humans#1943. In this file, the MISS on the deleted test name and on the stale path in finding 13 is intended.
- 2026-10-10 session: the doc and comment strikes were small enough that main applied them directly rather than through a sonnet executor.

## Findings

1. `Docs/TicketTailor.md` described the shared page walk in three separate invariant bullets (guards, one loop, per-page timing), and its timeout bullet narrated the superseded 30s ceiling.
2. The `internal sealed` invariant in `Docs/TicketTailor.md` restated the Negative Access Rules.
3. The previous target cited `TicketVendorPortArchitectureTests` as enforcing `internal sealed`; it pins the injection sites and the set of implementations only. HUM0034 is what keeps the types internal.
4. `Docs/TicketTailor.md` freshness triggers missed files the doc makes claims about: the section's tests, `TicketVendorServiceKeys`, `ITicketVendorMirror`, Tickets' `Section.cs`, the Actors classes and `LoggerTimingExtensions`.
5. "Every other method throws `HttpRequestException`" was pinned for orders and check-in only.
6. "The client never retries a check-in" had no pinning assertion.
7. `GetOrdersAsync_HandlesPagination` duplicated the shared page-walk tests; the reviewer found that orders accumulation across pages needed one assertion to replace it.
8. Comments restating code: the issued-ticket mapping comment, the stub's check-in no-op comment; the `DeterministicHash` comment said what rather than why; the per-page timing test comment narrated "is the fix".
9. Off-section: `src/Sections/Humans.Tickets/Docs/features/ticket-vendor-integration.md` called `TicketTailorService` the only implementation of the vendor port.
10. The stub voids a ticket to status `voided` while its unpaid fixture tickets carry `void`. Tickets maps both to `TicketAttendeeStatus.Void` (`src/Sections/Humans.Tickets/Services/TicketSyncService.cs:557`), so nothing observable differs.
11. Off-section: `docs/architecture/dependency-graph.md` colours `TicketTailorService` with the tickets class.
12. Off-section: `Tickets.md` gives approximate stub totals; a dated probe and a dated plan name pre-split paths.
13. Inbox review — nobodies-collective/Humans#962, nobodies-collective/Humans#906, nobodies-collective/Humans#580, nobodies-collective/Humans#524, nobodies-collective/Humans#127: keep. Each names TicketTailor only in passing, and none carries TicketTailor work. nobodies-collective/Humans#524 cites `src/Humans.Domain/Enums/ContactSource.cs`, which now lives at `src/Sections/Humans.Users.Contracts/ContactSource.cs`; Peter may want to fix that path.

## Debt verified

The section ledger has no rows, so there was nothing to verify. The cap was not reached.

## Worked

- 1, 2, 4 — invariant doc: one page-walk bullet, duplicate bullet cut, prior-state clause cut, triggers completed.
- 3 — target regenerated; the implementations invariant now cites HUM0034 and the declaration, and the arch test for what it actually pins.
- 5 — `NonWriteMethods_ThrowRawHttpRequestExceptionOnApiError` covers every read and discount codes.
- 6 — the check-in failure test asserts a single request.
- 7 — the subsumed test was deleted; the timing theory's orders arm asserts both pages accumulate. Reviewer (critical tier) APPROVE-with-correction, correction applied. Checks run: every list read routes through the shared walk, the raw-exception path for each non-write method, the `DiscountCodeSpec` shape, single-POST check-in, the blast grep for deleted names, and the comment cuts against the docs.
- 8 — comments cut or rewritten as the Comments and History threads proposed.
- 9 — the claim was corrected in Tickets' feature doc (sweep of a claim about this section's type).

## Skipped

- 10 — no change; both spellings are accepted downstream, and aligning them changes fixture data for no reader.
- 11 — left to the freshness sweep, which regenerates the graph (`docs/architecture/freshness-catalog.yml` entry `dependency-graph`).
- 12 — no change; the totals are true, and dated records are history by design.
- Comments thread proposals not taken: the cents comment on the discount payload carries the unit, which the code cannot say; the csproj `design §5` and `None Include` comments are a convention shared by every section csproj, not this section's call.
- Sections passed over: Events was blocked by its open run PR (peterdrier/Humans#1931).

## Retro

The selector's pick was sound: every change since the last run came through debt-sweep PRs that updated the invariant doc but not the target, so the target was the most drifted artifact.

The wasted motion was the issue search. The search API is blocked in this environment, so all open issues were listed and filtered locally. Every hit mentions TicketTailor only in passing.

Striking found what the assessment missed: the deleted orders paging test was the only orders accumulation check. The reviewer caught it; the Tests thread had called it fully subsumed.

The target diff says the section moved (one page walk, new guards), and the earlier target was wrong on the arch-test cite. The trace gate's MISS on dotted literals cost a hand check per token; filed as peterdrier/Humans#1943.

## Needs Peter

None.

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.TicketTailor/Contracts/README.md` | reviewed (Freshness, Prose) |
| `src/Sections/Humans.TicketTailor/Docs/TicketTailor.md` | changed, reviewed |
| `src/Sections/Humans.TicketTailor/Docs/data-access.md` | reviewed (Freshness, Prose) |
| `src/Sections/Humans.TicketTailor/Docs/debt.yml` | reviewed (Inbox; no rows) |
| `src/Sections/Humans.TicketTailor/Docs/health.md` | changed |
| `src/Sections/Humans.TicketTailor/Humans.TicketTailor.csproj` | reviewed (Freshness, Comments) |
| `src/Sections/Humans.TicketTailor/Properties/AssemblyInfo.cs` | reviewed (Comments) |
| `src/Sections/Humans.TicketTailor/Section.cs` | reviewed (Freshness, Comments, Behavior) |
| `src/Sections/Humans.TicketTailor/Services/StubTicketVendorService.cs` | changed |
| `src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs` | reviewed (Tests) |
| `tests/Humans.TicketTailor.Tests/Humans.TicketTailor.Tests.csproj` | reviewed (Tests, Comments) |
| `tests/Humans.TicketTailor.Tests/SectionRegistrationTests.cs` | reviewed (Tests) |
| `tests/Humans.TicketTailor.Tests/Services/StubTicketVendorServiceTests.cs` | reviewed (Tests) |
| `tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceTests.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceWriteTests.cs` | changed |
| `tests/Humans.TicketTailor.Tests/Services/TicketTailorTestHost.cs` | reviewed (Tests; no dead helpers) |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | main session model | findings 1, 3, 7 |
| Behavior & bugs | main | main session model | finding 10 |
| Freshness | subagent (`doctor-reader`) | opus-low | findings 1, 2, 4, 9, 11, 12 |
| Conformance | subagent (`pd:orch-haiku`) | haiku | clean |
| Tests | subagent (`doctor-reader`) | opus-low | findings 3, 5, 6, 7 |
| Prose & surface | subagent (`pd:orch-haiku`) | haiku | finding 1; InspectCode not installed, not run |
| History | subagent (`doctor-reader`) | opus-low | findings 1, 8 |
| Comments | subagent (`doctor-reader`) | opus-low | finding 8 |
| Inbox | subagent (`doctor-reader`) | opus-low | finding 13 |

