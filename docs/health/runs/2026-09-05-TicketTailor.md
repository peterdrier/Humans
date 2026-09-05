# TicketTailor — section doctor, 2026-09-05

- **Invocation:** unattended daily run, no arguments. Phase 8 (inline round) skipped.
- **Anchor commit:** `10199a23` (`origin/main`)
- **Branch:** `section-doctor/2026-09-05T181538Z` (cloud run, repo root — no worktree)
- **Budget:** 2.5h, single PR.
- **PR:** peterdrier/Humans#pending

## Assessment summary

First doctor pass over TicketTailor, the adapter behind Tickets' `ITicketVendorService` port
(reforge 28, loc=886, cogP95=12, cogMax=23): a live Ticket Tailor v1 client bound
in Production, a deterministic in-memory stub bound everywhere else, no tables, no UI. The
target shape ([`health.md`](../../../src/Sections/Humans.TicketTailor/Docs/health.md),
written this run before any scan) finds the code shaped right: one method per port method,
the environment name as the only switch, the stub as one fixture.

No live behavior bug. What the section had instead: no invariant doc at all, with the
invariants sitting in a `Contracts/README.md` titled after a project that does not exist
(finding 1); a data-access map and three comments describing a credential-based switch and a
Shell-owned health check, both years stale (findings 2, 3); comment drift of the recognizable
kind — decision history, provenance tags, a live-verification diary, next-line restatements
(findings 8–10); two naming mechanisms for the wire shape and seven unread wire fields
(finding 6); three copies of the test scaffolding and dead test-project references
(findings 5, 7); and a matrix of untested invariants, the environment switch first among them
(finding 4). Striking surfaced one code-vs-comment disagreement in the stub (finding 21) and
one wrong claim in the port's own doc comment (finding 22).

## Ranked findings

Value = bug surface removed, then concepts removed, then words removed.

| # | Finding | Value | Disposition |
|---|---|---|---|
| 1 | **No `Docs/TicketTailor.md`.** `Contracts/README.md` carried the invariants under the title `Humans.TicketTailor.Contracts` (no such project); conformance detector D2 hit; with `health.md` and `data-access.md` catalog-ignored the section had zero swept docs. Written per `docs/sections/SECTION-TEMPLATE.md`; README folded to a pointer; row added to `docs/README.md`. | high | **worked** |
| 2 | **`Docs/data-access.md` was false three ways:** stub bound "when no vendor credentials are configured" (the switch is the environment name, `Section.cs`), the live client headed "Scoped" (typed `HttpClient`), the section called "the vendor port" (it is the adapter). Rewritten. | high | **worked** |
| 3 | **"Shell's `TicketVendorHealthCheck`" stale** in `Section.cs`, the Shell's `TicketVendorInfrastructureExtensions.cs` and `TicketVendorArchitectureTests.cs` (the check moved to Tickets at nobodies-collective/Humans#1075); `Section.cs` carried 25 lines of decision history for 20 lines of code. Fixed where this run may write; the test file is finding 14's. | high | **worked** |
| 4 | **Untested invariants:** the environment switch, `CreateCheckInAsync`'s form body and raw `HttpRequestException`, `since` → `updated_at.gte`/`created_at.gte`, stub dataset determinism, the discount-code payload, summary cache hit, capacity fallback, paging for tickets and check-ins, 422 → Validation, Basic auth only with a key, issue transport failure. All pinned in `tests/Humans.TicketTailor.Tests`. | high | **worked** |
| 5 | **Test scaffolding ×3:** three identical `CreateService`, three HTTP doubles, seven void status tests differing only in code/kind, four email tests where three carry the invariant, an `ArgumentException` test that never asserted zero requests. One host, one recording handler, theories. | med | **worked** |
| 6 | **Two naming mechanisms for the wire shape** — `JsonNamingPolicy.SnakeCaseLower` plus `[JsonPropertyName]` on every nested record property, every name equal to the policy output — and seven unread wire fields. Attributes dropped, fields cut. | med | **worked** |
| 7 | **Test csproj:** dead `ProjectReference` on `Humans.Shifts.Contracts`, dead `NodaTime.Testing` package, a comment claiming a `tests/Directory.Build.props` exclusion that does not exist, a template-step citation. Deleted. | med | **worked** |
| 8 | **Section csproj comments:** "Base (TicketVendorSettings)" wrong (it is `Humans.Tickets.Contracts`), a dated ruling quote, lane number, skipped-template-step narration, the 2027-swap paragraph duplicated from the README. Cut. | med | **worked** |
| 9 | **`TicketTailorService.cs` comments:** the 11-line "VERIFIED LIVE" diary, an analyzer apology, next-line restatements, three copies of one provenance ref, a class summary restating the name, a real vendor order id. Cut; the check-in comment kept as four constraints. | med | **worked** |
| 10 | **`StubTicketVendorService.cs` comments:** a bare `#736`, "Development-only" (the rule is every non-Production environment), restatements. Cut/rewritten. | med | **worked** |
| 11 | **`VoidIssuedTicketAsync` / `IssueTicketAsync` opened no `TimeOperation()` scope** unlike every other port method. Added. | low | **worked** |
| 12 | **`StubTicketVendorService.BuildSampleData` is reforge's one longMethod hit** (133 LOC / CC 23) — the single fixture the target blesses. Recorded in the section's `Docs/debt.yml`, not split. | low | **debt.yml** |
| 13 | **Freshness catalog's `data-access.md` entry never triggers on `src/Sections/*/Section.cs`**, so a registration-only change cannot dirty the map — exactly how finding 2's claim went stale. Shared file → sweep queue. | med | **queued** |
| 14 | **`tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs`:** bare `#555`, "Shell's health check", a `Humans.Application.Tests` path, a comment on the wrong const, deleted-assertion narration, dead `Humans.Infrastructure` and never-matching `TicketTailor` prefixes, and `ThePortsAssemblyDoesNotReferenceTheAdapterSection` asserting a build-cycle impossibility ([`no-tests-for-absences`](../../../memory/architecture/no-tests-for-absences.md)). peterdrier/Humans#1589 writes lines 7–24 of this file. | med | **Needs Peter** |
| 15 | **`ITicketVendorService.GetDiscountCodeUsageAsync` / `DiscountCodeStatusDto` have no caller** outside the port and its two adapters, and the live implementation swallows a non-2xx as "not redeemed", unlike every other read. Port is Tickets' (`src/Sections/Humans.Tickets/Contracts/`, peterdrier/Humans#1589 writes it). | med | **Needs Peter** |
| 16 | **`VendorOrderDto.Tickets`** is `[]` from the live client, populated by the stub, never read by Tickets. Tickets-owned DTO. | low | **Needs Peter** |
| 17 | **`docs/architecture/debt-ledger.yml` 2026-06-29 entry** (nested `check_in` mapping, nobodies-collective/Humans#736) describes code that no longer exists — its own prescribed fix (read `/check_ins`, `check_in_at` epoch seconds) is implemented. Runs never mutate existing entries. | med | **Needs Peter** |
| 18 | **Gate's docs assert TicketTailor facts** (form-encoded `POST /v1/check_ins`, required fields, non-idempotency: `Gate.md:67-77`) but trigger only on `src/Sections/Humans.Gate/**`; `gate-admissions.md:65` names `ITicketVendorService.CreateCheckInAsync`, the port Gate is banned from injecting (it calls `ITicketVendorMirror`). Gate is blocked (peterdrier/Humans#1574). | med | **queued** |
| 19 | **Tickets' `ticket-transfer.md` triggers omit `src/Sections/Humans.TicketTailor/**`** while asserting the vendor void+reissue writeback; `Tickets.md:237` credits `TicketVendorArchitectureTests` with pinning "the two adapters" (that is `TicketVendorPortArchitectureTests`). Tickets is blocked (peterdrier/Humans#1589). | low | **queued** |
| 20 | **Inbox:** no open peterdrier/Humans issue names TicketTailor; the ledger's 2026-08-21 re-sync entry is Tickets' and stays; in-app issues unreachable from this container. | — | **no change** |
| 21 | **`StubTicketVendorService.cs` says "Every 5th ticket is scanned" but `(orderIndex * 10 + t) % 5 == 0` reduces to `t == 0`** (orders hold one or two tickets), so the first ticket of every paid order is checked in — 450 of 600. Comment and code disagree and the code looks wrong; changed neither ([`when doc and code disagree`](2026-08-22-Cantina.md)). The new stub test and both docs pin the gate day, not the fraction. | med | **Needs Peter** |
| 22 | **`ITicketVendorService.cs:81` documents `CreateCheckInAsync` as "Safe to retry"**; the vendor call is not idempotent (each POST creates a record) and Gate's job runs with `Attempts = 0` for that reason. Raised by the cut-cluster reviewer. Tickets-owned, peterdrier/Humans#1589 writes the file. | med | **queued** |
| 23 | **The stale "Shell's `TicketVendorHealthCheck`" claim also stands at `src/Sections/Humans.Tickets/Contracts/ITicketVendorService.cs:15` and `src/Sections/Humans.Tickets/Section.cs:29`.** Found sweeping finding 3; peterdrier/Humans#1589 writes both. | low | **queued** |
| 24 | **`GetEventSummaryAsync` caches inline through `IMemoryCache`** rather than behind a caching decorator over the port. One key, one adapter, a vendor-facing boundary — the target records "no decorator" as the decision, but two implementers would differ. | low | **Needs Peter** |
| 25 | **`docs/architecture/section-conformance.yml:67` records "no Docs/ in Settings or TicketTailor"** as a pre-existing hit; both now carry `Docs/`. Read-only to a run. | low | **queued** |
| 26 | **`docs/architecture/dependency-graph.md` lists `TicketVendorService` among services with no cross-section edges**; no such type exists — the adapter's services are `TicketTailorService` and `StubTicketVendorService`. Shared file. | low | **queued** |
| 27 | **Sweep: two queued items in merged run files cannot be applied mechanically.** Settings' finding 14 asks for a per-section `freshness-catalog.yml` entry, and the catalog has no per-section entries for any section (its entries are per-target with wildcard triggers). Agent's `memory: process/debt-ledger-additions` item asks which routing is intended for section test projects — a ruling, not an edit. Both left in place. | — | **Needs Peter** |
| 28 | **Phase 3d lesson:** the combined Conformance+Prose thread returned no detector output; the main thread re-ran the three shell detectors itself (D2 hit, D1 and D3 clean). Proposed edit: run the detectors in the main thread before dispatch and hand their output to the thread in its prompt. | — | **Needs Peter** |

Independence check: pass — findings 1, 4, 5 and 6 came from the target's Structure section
before any scan; 2, 3 and 15 are spec-vs-reality deltas from the behavior read; the scans
(reforge, conformance, grep) contributed 12, 13 and the issue-ref hits only.

## Worked

Findings 1–11, one commit per strike, in the cut → delete → dedup → collapse → rearch order:

- `doctor(TicketTailor): cut stale claims and decision history from docs and comments` — findings 2, 3, 8, 9, 10. Reviewer: rework (a pointer to the not-yet-written invariant doc), then accept.
- `doctor(TicketTailor): delete dead test-project references` — finding 7.
- `doctor(TicketTailor): dedup test scaffolding to one handler and one factory` — finding 5. Reviewer: rework (a missing `using`, an unused parameter), then accept.
- `doctor(TicketTailor): one naming mechanism for the wire records; time the write calls` — findings 6, 11.
- `doctor(TicketTailor): pin the untested invariants` — finding 4.
- `doctor(TicketTailor): add the section invariants doc` — finding 1. Reviewer: rework (no index row, an over-broad "reads throw" claim inherited from the cut strike, port ownership wording), then accept.

Surfaces hit: **localization** — none; the section has no views or resx. **Authorization** — none;
no controllers or routes; the invariant doc's Negative Access Rules name who may not inject the
port, pinned by the existing `TicketVendorPortArchitectureTests`. **Audit** — unchanged; the
adapter performs no action of its own. **GDPR** — untouched; no personal data is stored here.
**Invariant doc** — created (finding 1) and consistent with the struck code. **Migrations** —
none; no schema. **Navigation** — `docs/README.md` indexes the new doc. **Tests** — 54 passing
in `tests/Humans.TicketTailor.Tests`, up from 32; full `dotnet test Humans.slnx` green before
the push.

## Skipped

Findings 12–28 (dispositions above): 12 to the section's `Docs/debt.yml`; 13, 18, 19, 22, 23,
25, 26 to the sweep queue; 14–17, 21, 24, 27, 28 to Needs Peter; 20 no change.

Off-limits this run under the concurrency contract: `TicketVendorArchitectureTests.cs`, every
`src/Sections/Humans.Tickets/**` file, `docs/guide/Tickets.md` and `debt-ledger.yml` appends
(all peterdrier/Humans#1589); `src/Sections/Humans.Gate/**` (peterdrier/Humans#1574).

Sections passed over as blocked (open doctor PRs): Auth (peterdrier/Humans#1575), Backdoor (peterdrier/Humans#1586), Budget
(peterdrier/Humans#1565), Calendar (peterdrier/Humans#1578), Campaigns (peterdrier/Humans#1564), Camps (peterdrier/Humans#1561), Consent (peterdrier/Humans#1572), EarlyEntry
(peterdrier/Humans#1593), Email (peterdrier/Humans#1587), Feedback (peterdrier/Humans#1566), Gate (peterdrier/Humans#1574), Governance (peterdrier/Humans#1580), Holded (peterdrier/Humans#1583),
Monitor (peterdrier/Humans#1582), Stripe (peterdrier/Humans#1588), Teams (peterdrier/Humans#1594), Tickets (peterdrier/Humans#1589). Feature-active and set aside:
AuditLog, Gdpr, Notifications, Rideshare.

## Threads

Raw per-thread finding counts before consolidation into the ranked list above.

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main | session default (see cost comment) | 8 → findings 1, 5, 6, 7, 14, 15, 16 |
| Behavior & bugs | main | session default (see cost comment) | 8 → findings 2, 11, 15, 17; trace gate on every `health.md` invariant passed |
| Inbox | main | session default (see cost comment) | 3 → finding 20; fork-only scope; in-app issues not reachable from this container |
| Freshness | subagent (doctor-reader) | opus (low effort) | 10 → findings 1, 2, 3, 13, 17, 18, 19 |
| Tests | subagent (doctor-reader) | opus (low effort) | 18 + invariant matrix → findings 4, 5, 7, 14 |
| History | subagent (doctor-reader) | opus (low effort) | 20 → findings 3, 7, 8, 9, 10, 14; its finding 17 ("keep" the props-exclusion comment) verified false |
| Comments | subagent (doctor-reader) | opus (low effort) | 37 → findings 3, 8, 9, 10, 14 |
| Conformance + Prose & surface | subagent (doctor-reader), detectors self-run by main after the thread returned none | haiku | 2 → findings 1 (D2), 10, 14; no InspectCode in this container |
| reforge | background tool | — | score 28: longMethod + cognitiveComplexity on `BuildSampleData` → finding 12. Measured after 3c drafting began; the target came from the 3b reading |
| Second opinion ×3 (cut cluster; dedup; invariant doc) | subagent (doctor-reviewer) | agent-definition default (see cost comment) | 3 reworks, each accepted on the second pass; raised findings 22 and the over-broad reads claim |

## Retro

**What the selector/rubric got wrong:** nothing. TicketTailor was the lower-middle of eight
never-doctored sections by reforge score; at 886 LOC and three source files it fit the budget
with room for the full test matrix.

**Wasted motion:** the History thread's "keep" verdict on the test-csproj comment (finding 7)
cost a verification round — the comment was false. The Conformance thread returned no detector
output and the main thread re-ran the detectors (finding 28). Two build rounds on the new tests
were analyzer feedback (`MA0006`, `MA0002`, a missing `using Xunit;`) that a read of a sibling
test file would have pre-empted. Two auto-compactions landed: one during the Phase 3 assessment,
one mid-Phase 4 between the dedup strike and its reviewer verdict; Phase 5's mandatory re-read of
Phases 5–7 was applied after the second. The second and third reviewer dispatches opened without
a `thread:` marker, so the cost comment names those rows by agent id rather than by thread.

**What the assessment missed that striking revealed:** the stub's every-5th comment versus its
modulus (finding 21) surfaced only while writing the dataset test — the behavior read had
repeated the comment. The port's "Safe to retry" (finding 22) and two more stale health-check
claims in Tickets (finding 23) came out of the reviewer gate and the claim sweep. The invariant
doc inherited an over-broad "reads throw" sentence from this run's own cut strike; the reviewer
caught it, the run did not — a class summary rewritten in a cut is a claim to re-check per
method before a doc quotes it.

**Target diff:** none possible — first doctor pass; `health.md` was written this run. The
target's "every fifth ticket" line was softened to the gate day once finding 21 surfaced, which
is the one place the target was wrong on the first write.

## Needs Peter

- [ ] 14 — strike `TicketVendorArchitectureTests.cs` (bare ref, dead prefixes, the build-cycle test) in a follow-up once peterdrier/Humans#1589 merges, or leave it?
- [ ] 15 — `GetDiscountCodeUsageAsync` / `DiscountCodeStatusDto`: delete from the port, or keep — and if kept, should it throw like every other read?
- [ ] 16 — `VendorOrderDto.Tickets`: drop the field, or keep?
- [ ] 17 — retire the 2026-06-29 debt-ledger entry (its fix shipped)?
- [ ] 21 — stub check-ins: fix the modulus (every 5th ticket, ~120 check-ins) or fix the comment (first ticket of every paid order)?
- [ ] 24 — `GetEventSummaryAsync`: keep the inline `IMemoryCache`, or a caching decorator over the port?
- [ ] 27 — sweep: rule on or drop the two unapplicable queued items (Settings' catalog entry; Agent's `debt-ledger-additions` routing)?
- [ ] 28 — Phase 3d: run the conformance detectors in the main thread and pass their output into the Conformance/Prose prompt?

## Sweep queue

- debt: Infrastructure — `docs/architecture/freshness-catalog.yml`'s `service-data-access-map` entry triggers on `src/Sections/*/Services/**` and `Data/*Repository.cs` but not `src/Sections/*/Section.cs`, so a registration-only change never dirties the map (finding 13, /section-doctor on TicketTailor 2026-09-05).
- debt: Gate — `Docs/Gate.md:67-77` asserts TicketTailor's check-in contract (form-encoded `POST /v1/check_ins`, required fields, non-idempotency) with triggers on `src/Sections/Humans.Gate/**` only; add `src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs`. `Docs/features/gate-admissions.md:65` names `ITicketVendorService.CreateCheckInAsync`, which Gate reaches through `ITicketVendorMirror` (finding 18, /section-doctor on TicketTailor 2026-09-05).
- debt: Tickets — `Docs/features/ticket-transfer.md` triggers omit `src/Sections/Humans.TicketTailor/**` while asserting the vendor void+reissue writeback; `Docs/Tickets.md:237` credits `TicketVendorArchitectureTests` with pinning "the two adapters" — that is `tests/Humans.Web.Tests/Architecture/TicketVendorPortArchitectureTests.cs` (finding 19, /section-doctor on TicketTailor 2026-09-05).
- debt: Tickets — `Contracts/ITicketVendorService.cs:81` says `CreateCheckInAsync` is "Safe to retry"; the vendor call creates a record per POST and Gate's `GateVendorCheckInJob` runs with `Attempts = 0` for that reason. Say "not idempotent; never retried" (finding 22, /section-doctor on TicketTailor 2026-09-05).
- debt: Tickets — "Shell's `TicketVendorHealthCheck`" is stale at `Contracts/ITicketVendorService.cs:15` and `Section.cs:29`; the check is Tickets' own since nobodies-collective/Humans#1075 (finding 23, /section-doctor on TicketTailor 2026-09-05).
- debt: Infrastructure — `docs/architecture/section-conformance.yml:67` records "no Docs/ in Settings or TicketTailor" as a pre-existing detector hit; both sections carry `Docs/<Section>.md` now (finding 25, /section-doctor on TicketTailor 2026-09-05).
- debt: Infrastructure — `docs/architecture/dependency-graph.md` "Services with no cross-section edges" lists `TicketVendorService`, a type that does not exist; the adapter's services are `TicketTailorService` and `StubTicketVendorService` (finding 26, /section-doctor on TicketTailor 2026-09-05).

## File coverage

`generated` = excluded from review per the skill.

**Changed:**
`src/Sections/Humans.TicketTailor/Contracts/README.md` ·
`src/Sections/Humans.TicketTailor/Docs/TicketTailor.md` (new) ·
`src/Sections/Humans.TicketTailor/Docs/data-access.md` ·
`src/Sections/Humans.TicketTailor/Docs/debt.yml` (new) ·
`src/Sections/Humans.TicketTailor/Docs/health.md` (new) ·
`src/Sections/Humans.TicketTailor/Humans.TicketTailor.csproj` ·
`src/Sections/Humans.TicketTailor/Section.cs` ·
`src/Sections/Humans.TicketTailor/Services/StubTicketVendorService.cs` ·
`src/Sections/Humans.TicketTailor/Services/TicketTailorService.cs` ·
`tests/Humans.TicketTailor.Tests/Humans.TicketTailor.Tests.csproj` ·
`tests/Humans.TicketTailor.Tests/SectionRegistrationTests.cs` (new) ·
`tests/Humans.TicketTailor.Tests/Services/StubTicketVendorServiceTests.cs` (new) ·
`tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceCachingTests.cs` ·
`tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceTests.cs` ·
`tests/Humans.TicketTailor.Tests/Services/TicketTailorServiceWriteTests.cs` ·
`tests/Humans.TicketTailor.Tests/Services/TicketTailorTestHost.cs` (new) ·
outside the section: `docs/README.md` ·
`src/Humans.Web/Extensions/Infrastructure/TicketVendorInfrastructureExtensions.cs` ·
`docs/architecture/dependency-graph.md` (sweep commit, Settings' finding 20)

**Reviewed:**
`src/Sections/Humans.TicketTailor/Properties/AssemblyInfo.cs` ·
`tests/Humans.TicketTailor.Tests/Architecture/TicketVendorArchitectureTests.cs` (off-limits: peterdrier/Humans#1589)

**Generated:** none.
