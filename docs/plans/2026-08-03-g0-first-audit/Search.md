# Search — G0 First Audit

**Section:** Search · **Kind:** orchestrator (frozen-inventory ruling: "Orchestrator") · **Audited:** 2026-08-05 @ 94535e688

**Scope note:** Search's `reforge.surface-score.json` entry (`paths`/`symbols`/`serviceInterfaces: [ISearchService]`) covers the global `/Search` page only. `src/Humans.Web/ViewComponents/HumanSearchViewComponent.cs` — a reusable inline person-picker widget backed directly by `IUserServiceRead` — is a distinct, Users-owned UI component that happens to share the word "search"; it is correctly excluded from the reforge Search paths and is out of scope here.

Search owns zero database tables — `SearchService` fans out to other sections' read interfaces and scores/ranks in memory — so G1 predicates 1/2/4 are vacuously satisfied, the same pattern as Guide/Scanner/Gdpr.

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository, in-section | **N/A** | No owned tables. `SearchService` (`src/Humans.Application/Services/Search/SearchService.cs`) injects only `IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`, `IEventServiceRead`, `IConfiguration` — no repository, no `HumansDbContext`. |
| 2 | One writer-service per table | **N/A** | Search never writes. |
| 3 | No EF entity leaks across the boundary | **PASS** | `ISearchService.SearchAsync` returns `GlobalSearchResults` (a DTO of `HumanSearchResult`/`GlobalSearchResult` records), never an EF entity. Zero Search entries in `ApplicationServiceEntityReadReturns.baseline.txt`. |
| 4 | No cross-section EF joins (zero baseline entries) | **PASS** | No repository/DbContext to join across. Zero baseline entries across all 5 baseline files. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no baseline rows owned by Search | **PASS** | No `[Grandfathered]` hits under `Application/Interfaces/Search/`, `Application/Services/Search/`, or `SearchController.cs`. |
| 6 | Controllers thin — no HUM0031 grandfathers | **PASS** | `SearchController.cs` is 79 lines: `Index` (parses request, calls `ISearchService`), `RunSearchAsync`, `BuildViewModel`, `SortByScore` — display sorting is explicitly the controller's job per the hard rules ("Controllers ... are responsible for formatting, sorting, filtering") and `ISearchService`'s own doc comment ("Display ordering lives in SearchController"). No `HUM0031` grandfather present. |
| 7 | `docs/sections/Search.md` exists and matches reality | **FAIL** | Doesn't exist (`ls docs/sections/ | grep -i search` → no hits). A *feature* doc exists at `docs/features/global/global-search.md` (referenced from `ISearchService.cs`'s own doc comment), but that's not the section-invariants doc the G1 predicate requires. |

**Observation (not scored as a gap):** `SearchService` is declared `: ISearchService` where `ISearchService : IApplicationService`. It matches `IOrchestrator`'s exact definition verbatim from `IOrchestrator.cs`'s doc comment — *"coordinates ≥2 sections through their public service interfaces, owns no tables, and injects no repository"* — and the frozen-inventory decision record explicitly classifies Search as an "Orchestrator" alongside Gdpr, which correctly implements `IOrchestrator`. Search implementing `IApplicationService` instead is not flagged by the `HUM0026`/`HUM0027` analyzer (that pair only forbids implementing *both* markers, or an `IOrchestrator` injecting a repository — it doesn't require an orchestrator-shaped `IApplicationService` to switch), so this isn't a predicate failure, but it's a real classification mismatch between the docs and the code worth a follow-up decision.

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests real Postgres, zero EF-InMemory | **N/A** | No repository. |
| 2 | Service tests mock repository/`I…ServiceRead`, zero `HumansDbContext` | **FAIL** | **No test file exists for `SearchService` or `SearchController` at all.** `grep -rli "searchservice\|globalsearch" tests --include=*.cs` matches only `GateControllerClaimTests.cs` (an unrelated incidental string match), and `find tests -iname "*SearchController*"` is empty. `tests/Humans.Application.Tests/Services/Profiles/PersonSearchMatcherTests.cs` tests `PersonSearchMatcher`/`PersonSearchFields` — a Profiles-owned name-matching helper `SearchService` doesn't itself contain — not `SearchService`'s own bucket-scoring/ranking/`onlyType` filter logic. |
| 3 | Invariants/triggers each have a test | **FAIL** | No `docs/sections/Search.md` to test against (predicate 7), and no tests exist regardless (predicate 2) — so none of the documented-in-code behaviors (min-2-char query gate, per-type `onlyType` short-circuit, exact/prefix/contains scoring tiers, the `Features:Events` flag gating the Events bucket, "public surface only — admin fields never reach `/Search`") have any test coverage. |
| 4 | No skipped tests without an issue ref | **PASS (vacuous)** | No test file exists to carry a skip. |
| 5 | Tests grouped under the section | **FAIL** | No Search-named test grouping exists anywhere to move at G5 — direct consequence of predicate 2. |

## G1 gap list

1. **`docs/sections/Search.md` doesn't exist** (predicate 7). Fix: write it, distinguishing the global `/Search` page (this section) from the `HumanSearchViewComponent` picker widget (Users-owned) per the scope note above — that distinction isn't written down anywhere today and is exactly the kind of thing a future contributor would get wrong. No-migration-needed: **y**.
2. **`SearchService` implements `IApplicationService`, not `IOrchestrator`, despite matching the orchestrator definition exactly and being classified as one in the frozen inventory** (see Observation above). Fix (follow-up, not required by any failing predicate): either reclassify `SearchService`/`ISearchService` to `IOrchestrator`, or correct the frozen-inventory doc's classification — flag for Peter, low urgency since no analyzer currently depends on the answer. No-migration-needed: **y**.

## G3 gap list

1. **`SearchService`/`SearchController` have zero test coverage of any kind** (predicates 2, 3, 5) — the min-length query gate, the four-bucket fan-out, the `onlyType` filter, the exact/prefix/contains scoring tiers, the `Features:Events` gate, and the public-surface-only privacy guarantee ("hidden teams, non-public camp seasons, admin-only rotas, and admin-only profile fields are excluded for everyone, regardless of role" per `ISearchService.cs`'s own doc comment) are all unverified by any automated test. This is the single largest gap found across all five sections in this audit — a privacy-relevant guarantee with no regression protection. Fix: add `SearchServiceTests.cs` mocking the five injected `*ServiceRead`/service interfaces, covering at minimum the query-length gate, `onlyType` short-circuit, and score-tier boundaries; add `SearchControllerTests.cs` for the sort/view-model assembly. No-migration-needed: **y** (test-only change, no schema involved).

## G2 queue notes

Search owns no tables — nothing to demolish or rename. Not in `docs/plans/2026-08-03-demolition-inventory.md` (drafted before this row was admitted).
