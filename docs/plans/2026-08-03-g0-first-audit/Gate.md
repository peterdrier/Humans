# Gate — G0 First Audit

**Section:** Gate · **Kind:** vertical (new, admits/writes at the event door) · **Audited:** 2026-08-05 @ 94535e688

**Scope note:** Gate is not yet in `reforge.surface-score.json` (confirmed: `python3 -c "... 'Gate' in data['sections']"` → `False`), so `reforge ownership-violations --owner Gate ...` silently no-ops (it returns `0 ownership-violations` even for a garbage table name passed as a sanity check — the owner isn't registered, so the tool has nothing to check against). This audit falls back to direct grep verification instead of citing that reforge output as evidence. Back-propagating Gate into the reforge config is frozen-inventory follow-up item #1 (`2026-08-03-proposed-frozen-section-inventory.md`).

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository, in-section | **PASS** | `grep -rl "GateScanEvent\|GateSettings\|GateStaffPin" src --include=*.cs` outside `Domain/Entities`, `Infrastructure/Data/Configurations/Gate`, `Infrastructure/Repositories/Gate`, `Application/Interfaces/Gate`, `Application/Services/Gate`, and Migrations returns only `IGateRepository.cs` (the interface itself), `Domain/Enums/AuditAction.cs` (an unrelated enum member), and `GateSectionExtensions.cs` (DI wiring). No other repository or DbContext touches `gate_scan_events`, `gate_settings`, or `gate_staff_pins`. |
| 2 | One writer-service per table | **PASS** | `GateService` (`src/Humans.Application/Services/Gate/GateService.cs`) is the only class injecting `IGateRepository`; all three tables are written exclusively through it (`RecordScanAsync`, settings save, PIN set/reset). |
| 3 | No EF entity leaks across the boundary | **PASS** | `IGateService` (`src/Humans.Application/Interfaces/Gate/IGateService.cs`) returns only DTOs — `GateScanResult`, `GateDecisionResult`, `GateSettingsDto`, `GateLeaderboard`, `GateVendorBackfillSnapshot`, `GateRosterEntry` — never `GateScanEvent`/`GateSettings`/`GateStaffPin`. `ApplicationServiceEntityReadReturns.baseline.txt` has zero Gate entries. |
| 4 | No cross-section EF joins (zero baseline entries) | **PASS** | Zero Gate entries in any of the 5 architecture-test baseline files (`ApplicationServiceEntityReadReturns`, `DisplaySortInControllers`, `NoDestructiveMigrationOps`, `NoLinqAtDbLayer`, `NoStartupGuards`) — confirmed by grep across all five. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no baseline rows owned by Gate | **PASS** | `grep -rn "Grandfathered" src/Humans.Infrastructure/Data/Configurations/Gate/` and the Gate controllers/services returns nothing. `GateScanEventConfiguration`/`GateSettingsConfiguration`/`GateStaffPinConfiguration` declare no navigation properties (bare-Guid cross-section links only, per `docs/sections/Gate.md`'s Data Model section). |
| 6 | Controllers thin — no HUM0031 grandfathers | **PASS** | The only `HUM0031` grandfather in `src/Humans.Web/Controllers/` repo-wide is on `ProfileController.cs` (unrelated to Gate). `GateController.cs`, `GateVendorBackfillAdminController.cs`, and `TicketsGateAdminController.cs` (gate-account credential admin, owns no Gate tables — reads only `IUserServiceRead` + `GateTerminalAccountSeeder`) carry no grandfather. |
| 7 | `docs/sections/Gate.md` exists and matches reality | **PASS** | The doc exists and is current: cross-section deps listed (Tickets, EarlyEntry, Shifts, Users, Auth) match the actual `GateService` constructor deps found in `GateServiceTests.cs`; the routing table matches `GateController`/`GateVendorBackfillAdminController`; config keys (`Gate:SupervisorPin`, `Gate:VendorMirrorEnabled`, `Gate:RosterTeamId`) match usages found in code. No drift found. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests real Postgres, zero EF-InMemory | **FAIL** | No `GateRepositoryTests.cs` file exists at all (`find tests -iname "*GateRepository*"` → empty). The only indirect repository coverage is through `GateServiceTests.cs`, which runs over EF-InMemory (see predicate 2) — there is no dedicated, direct repository test in any backend. |
| 2 | Service tests mock repo/`I…ServiceRead`, zero `HumansDbContext` | **FAIL** | `GateServiceTests.cs:27` extends `ServiceTestHarness`, which builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` (`ServiceTestHarness.cs:54,71`), and constructs a concrete `GateRepository` (`Humans.Infrastructure.Repositories.Gate` import present) rather than `Substitute.For<IGateRepository>()`. Same harness-inherited EF-InMemory false-negative pattern the original G0 pass corrected for Camps/Auth/Budget/etc. — a grep for the literal `HumansDbContext` in the file would miss it. |
| 3 | Invariants/triggers from `docs/sections/Gate.md` each have a test | **PASS (spot-check)** | Verdict precedence + fail-safe AMBER cutoff — `GateAdmissionRulesTests.cs`. Server-authoritative decision (client "ID confirmed" can't override a STOP) — `GateServiceTests.cs` (doc-referenced explicitly in its class summary). PIN claim/override flow, throttle — `GateControllerOverridePinTests.cs`, `GateControllerClaimTests.cs`. Vendor check-in backfill — `GateVendorBackfillControllerTests.cs`. Gate login — `AccountControllerGateLoginTests.cs`. Not exhaustively line-mapped against every bullet in the doc (e.g. the auto-clear timeout UX rules are client-side JS, not unit-testable). |
| 4 | No skipped tests without an issue ref | **PASS** | `grep -rn "Skip\s*="` across all Gate test files (`Architecture/GateArchitectureTests.cs`, `Controllers/*Gate*Tests.cs`, `Services/Gate/*.cs`) → no matches. |
| 5 | Tests grouped under the section | **PASS** | `Services/Gate/*.cs` is a dedicated folder; `Architecture/GateArchitectureTests.cs` and `Controllers/*Gate*Tests.cs` follow the same by-kind foldering every other section uses (Controllers tests are never nested per-section anywhere in the repo — same convention Camps/Scanner were scored PASS against). |

## G1 gap list

None found. Gate's ownership boundary is clean — the only open item is config back-propagation (frozen-inventory follow-up #1), which is tracked there already, not fresh debt from this audit.

## G3 gap list

1. **No `GateRepositoryTests.cs` — repository coverage is only indirect, via an EF-InMemory harness** (predicate 1). Fix: add a dedicated repository test file against the real-Postgres shared fixture (#764/#766 scope), covering the `AdmitDedupeKey` unique-index race path that `docs/sections/Gate.md` itself flags as a known test gap ("the concurrent index-collision path isn't covered by unit tests — the EF in-memory provider can't enforce unique indexes"). No-migration-needed: **y**.
2. **`GateServiceTests` extends `ServiceTestHarness` and constructs a real `GateRepository` over EF-InMemory instead of mocking `IGateRepository`** (predicate 2) — same corrected pattern as Camps/Auth/Budget/Governance/Consent in the original G0 pass. Fix: convert to `Substitute.For<IGateRepository>()` per #766. No-migration-needed: **y** (test-only change).

## G2 queue notes

Gate is not in `docs/plans/2026-08-03-demolition-inventory.md` (drafted before Gate was admitted as a section on 2026-08-03). Two migrations exist (`AddGateSection`, `AddGateStaffPinAdminEnrolled`), both additive — no dead columns/tables found during this audit. Nothing queued.
