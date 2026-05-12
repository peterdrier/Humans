# Section Align - Scanner
**Run started:** 2026-05-12 | **Mode:** existing-section | **Worktree:** `H:\source\humans\.worktrees\section-align-scanner`
**Branch:** `align/scanner` (off `origin/main` @ `092915124`)
**Canonical section name proposal:** Scanner

Phase 0 inventory across three axes per `.claude/skills/section-align/SKILL.md`:
1. **Boundary integrity** - clean section name, addressable routes/views/models, no cross-section DB access except User <-> Profile.
2. **Internal cohesion** - no EF leak from service layer, caching only in service layer, proper interfaces, reusable ViewComponents, architecture-test coverage.
3. **Test focus** - section-aligned tests in one canonical folder, coverage maps to invariants/negatives/triggers, redundancy pruned.

Scanner is intentionally small: one MVC controller, two Razor views, and one browser module. It owns no tables, no repositories, no services, and no server-side state.

---

## Axis 1 - Boundary integrity

### 1.1 Section name consistency - clean

Canonical name `Scanner` is consistent across:

| Surface | Current state |
|---------|---------------|
| Section doc | `docs/sections/Scanner.md` |
| Feature spec | `docs/features/scanner/scanner-barcode.md` |
| Controller | `src/Humans.Web/Controllers/ScannerController.cs` |
| Views | `src/Humans.Web/Views/Scanner/{Index,Barcode,_ViewStart}.cshtml` |
| Browser module | `src/Humans.Web/wwwroot/js/scanner/barcode.js` |
| Resource keys | `Scanner_*` in all six `SharedResource*.resx` files |

No competing section name was found. `Scanner` is not present in `IssueSectionRouting`, which is covered under 1.15 because it affects issue routing, not code ownership.

### 1.2 Controller existence - clean

`ScannerController` exists and owns both Scanner routes:

| Route | Controller action | Status |
|-------|-------------------|--------|
| `GET /Scanner` | `ScannerController.Index` | clean |
| `GET /Scanner/Barcode` | `ScannerController.Barcode` | clean |

No foreign controller hosts Scanner routes.

### 1.3 URL surface - clean

All Scanner routes live under `/Scanner/*`. There are no `/Admin/Scanner/*` routes, no aliases, and no generic top-level controller hosting Scanner concerns.

The admin-shell sidebar links to `Scanner/Index` via `AdminNavTree`; this is navigation only and does not make `/Admin/*` a Scanner route.

### 1.4 Views folder - clean

`src/Humans.Web/Views/Scanner/` exists and owns:

- `Index.cshtml`
- `Barcode.cshtml`
- `_ViewStart.cshtml`

No Scanner-owned page views are parked in `Views/Shared/`.

### 1.5 ViewModel placement - clean

Scanner has no server-side ViewModel types. The pages are static Razor surfaces plus localized strings.

### 1.6 Controller-base leak - clean

No Scanner helpers, models, or route helpers are present on `HumansControllerBase` or other controller base classes.

### 1.7 Extensions placement - acceptable

There is no `ScannerSectionExtensions.cs`. This is acceptable for the current section shape because Scanner registers no services, repositories, hosted services, configuration, or decorators.

If Scanner gains any DI surface later, add `src/Humans.Web/Extensions/Sections/ScannerSectionExtensions.cs` and wire it through `InfrastructureServiceCollectionExtensions`.

### 1.8 Role surface - clean

Scanner uses `[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]`. The admitted roles are `TicketAdmin`, `Board`, and `Admin`; the domain-scoped admin role has the required `*Admin` suffix.

No separate `ScannerAdmin` role exists. That is currently consistent with the section doc: Scanner is a camera tool whose first use case is TicketTailor ticket stubs, but it owns no data.

### 1.9 Inbound cross-section DB access - none

Scanner owns no DbSets and no tables. No other section can read or write Scanner-owned tables.

### 1.10 Inbound EF navigations - none

Scanner owns no entities. No inbound navigation properties exist.

### 1.11 Outbound cross-section access - none

Scanner has no repository and no service. The only cross-section relationship is authorization/nav coupling to the Tickets access policy. There are no calls to Tickets services, repositories, DbSets, or EF navigations.

### 1.12 Controller -> DbContext - clean

`ScannerController` has no constructor and injects no `HumansDbContext`, services, repositories, or caches.

### 1.13 Migrations - clean

No Scanner migrations exist and none are needed.

### 1.14 Section invariant doc - mostly clean

`docs/sections/Scanner.md` exists, follows the required section-doc shape for a no-table presentational section, and agrees with `docs/architecture/design-rules.md` section 8:

| Doc claim | Status |
|-----------|--------|
| No services | matches code |
| No owned tables | matches design-rules section 8 |
| `TicketAdminBoardOrAdmin` gate | matches controller |
| No server-side writes | matches controller and browser module |
| Browser stop releases tracks | matches `barcode.js` |

Minor doc polish candidate: add one sentence under Architecture explaining why `ScannerSectionExtensions.cs` is intentionally absent, so future alignment runs do not misread it as drift.

### 1.15 Operational routing/docs gaps - follow-up needed

These are not core boundary violations, but they are Scanner-adjacent drift:

| Gap | Current behavior | Impact |
|-----|------------------|--------|
| Issue section routing | `IssueSectionRouting` and `IssueSectionInference` do not define `Scanner` or map `/Scanner` | Feedback/issues filed from `/Scanner` infer `Section = null`, falling to Admin-only instead of the same operators who can access Scanner |
| Area labels | `AreaLabelMap` has no Scanner label | Even if a Scanner section value is created, it would display as raw `Scanner` unless mapped |
| Authorization inventory | `docs/authorization-inventory.md` lacks ScannerController | The generated authorization doc is stale for this section |
| Controller audit | `docs/controller-architecture-audit.md` lacks ScannerController routes | The generated controller audit is stale for this section |

Decision resolved 2026-05-12: `/Scanner` issues route as their own `Scanner` section with `TicketAdmin` and `Board` visibility.

---

## Axis 2 - Internal cohesion

### 2.1 EF leakage from service layer - not applicable

No `Humans.Application.Services.Scanner/` namespace exists. This is correct for a browser-only section with no business logic.

### 2.2 Caching placement - clean

No Scanner code uses `IMemoryCache`, `MemoryCache`, `IDistributedCache`, or a cache decorator. There is no server-side data to cache.

### 2.3 DI lifetimes - not applicable

Scanner has no DI registrations. No repository/service lifetime checks apply.

### 2.4 Repository pattern - not applicable

Scanner owns no tables and has no repository. No repository should be added unless Scanner gains server-side state.

### 2.5 Shared visual components - none needed

No Scanner ViewComponent, TagHelper, or shared partial exists. Current markup is local to `Views/Scanner`.

If Scanner decoded state is later rendered on another section page, prefer a ViewComponent over a shared partial.

### 2.6 Interface budget + segregation - not applicable

Scanner exposes no public Application interfaces.

### 2.7 Architecture test coverage - small gap

Generic guardrails already cover part of the shape:

| Invariant | Existing coverage |
|-----------|-------------------|
| Controller has some auth | `EndpointAuthorizationTests.AllControllerActions_HaveAuthorizeOrAllowAnonymous` |
| Policy role matrix | `AuthorizationPolicyTests.Role_policy_matrix_matches_expected_roles` includes `TicketAdminBoardOrAdmin` |
| No controller DbContext | `ControllerDbContextInjectionAnalyzer` and analyzer tests |

Missing focused guardrail:

- Pin that `ScannerController` specifically requires `PolicyNames.TicketAdminBoardOrAdmin`, not merely any `[Authorize]`.
- Pin that `ScannerController` remains GET-only and constructor-free while it is documented as client-only/no-server-state.

These can be either:

1. added to `EndpointAuthorizationTests.CriticalEndpointPolicies` plus a small Scanner-specific reflection test, or
2. grouped in `tests/Humans.Web.Tests/Controllers/ScannerControllerTests.cs`.

Option 1 is lower-maintenance.

---

## Axis 3 - Test focus

### 3.1 Test folder placement - no Scanner folder yet

Current Scanner-related tests are incidental:

| Test file | What it covers |
|-----------|----------------|
| `tests/Humans.Application.Tests/Authorization/AuthorizationPolicyTests.cs` | `TicketAdminBoardOrAdmin` role membership |
| `tests/Humans.Application.Tests/ViewComponents/AdminSidebarViewComponentTests.cs` | Scanner sidebar item visibility/active state |
| `tests/e2e/tests/admin-shell.spec.ts` | Scanner appears for Admin, Board, and TicketAdmin in the admin shell |

No test file directly maps to `ScannerController` or `barcode.js`.

Recommended canonical homes:

- Controller/policy shape: existing `tests/Humans.Application.Tests/Authorization/EndpointAuthorizationTests.cs` or a small `tests/Humans.Web.Tests/Controllers/ScannerControllerTests.cs`.
- Browser behavior: `tests/e2e/tests/scanner.spec.ts` if we add camera/API mocks through Playwright.

### 3.1a 1-to-1 map

| Production surface | Test file | Status |
|--------------------|-----------|--------|
| `ScannerController` | none direct | missing focused policy/no-state coverage |
| `Views/Scanner/Index.cshtml` | admin-shell e2e only | enough for nav, not page content |
| `Views/Scanner/Barcode.cshtml` | none | missing browser-flow coverage |
| `wwwroot/js/scanner/barcode.js` | none | missing browser-flow coverage |

### 3.2 Coverage map

| Section-doc item | Current coverage | Gap |
|------------------|------------------|-----|
| All routes require `TicketAdminBoardOrAdmin` | Generic auth presence plus policy matrix | exact ScannerController policy not pinned |
| No scanner endpoint writes server state | controller currently has only GETs and no constructor | not pinned |
| Decoded values do not leave the browser | code inspection only | no automated browser/network test |
| Camera stream released on Stop/page unload | code inspection only | no automated browser test |
| Cannot be used as check-in gateway | no check-in calls/routes in Scanner | not pinned, but current code has no server path |
| Future server round-trip must be a new route/tool | doc only | not enforceable until such a tool exists |
| Triggers: none | code inspection only | no test needed beyond no-server-state guardrail |

Phase 2 should add exact policy/no-state coverage. Browser-flow coverage is useful but should be one Playwright test, not a new JS unit framework.

### 3.3 Redundancy flags

No Scanner-specific redundant tests exist. The admin-sidebar tests and e2e matrix cover separate behavior and should stay.

### 3.4 Test-to-section ratio

Approximate production LOC:

| Surface | LOC |
|---------|-----|
| `ScannerController.cs` | 18 |
| `Views/Scanner/Index.cshtml` | 26 |
| `Views/Scanner/Barcode.cshtml` | 77 |
| `wwwroot/js/scanner/barcode.js` | 216 |
| Total | 337 |

Direct Scanner test LOC: 0. Incidental admin-shell/sidebar coverage exists, but the section is under-tested relative to its browser-only invariants.

### 3.5 Brittleness signals

No Scanner tests exist, so no Scanner test brittleness was found.

Potential future brittleness to avoid: static tests that assert `barcode.js` contains the string `track.stop()` would be implementation-shape tests. Prefer a browser-level test that observes `MediaStreamTrack.stop()` being called.

### 3.6 Mutation signal

No `local/stryker-runs/scanner` report exists, and no `stryker-scanner-config.json` exists. Stryker is not a good fit for this pass because Scanner has no Application service/repository surface; the meaningful behavior is browser API interaction in `barcode.js`.

Skip Stryker unless Scanner gains non-trivial server-side Application code.

---

## Test-attribute gate

- Baseline from `docs/testing/mutation-testing.md`: 2139 test attributes.
- Phase 0 net delta: +0 / -0 = 0.
- Phase 2 expected delta: likely +1 to +3. Justification: Scanner currently has no direct tests for documented access/no-state/browser-stop invariants.

---

## Stop conditions tripped

None.

Resolved 2026-05-12: Option A. `Scanner` is a first-class issue section. `/Scanner` maps to `IssueSectionRouting.Scanner`; TicketAdmin and Board handlers see that queue; Admin remains implicit.

---

## Follow-up /section-align targets

- **Issues** - if Option A or B is not handled in this Scanner pass, the Issues section needs its own alignment/freshness pass because issue section inference and routing do not currently know Scanner.

No DB-boundary follow-up sections were found.

---

## Phase plan

### Phase 1 - Surface alignment

No controller/view/model/route moves needed.

Optional doc-only cleanup if doing Phase 1 anyway:

- Add an explicit note to `docs/sections/Scanner.md` that `ScannerSectionExtensions.cs` is intentionally absent while the section has no DI registrations.

### Phase 2 - Architecture and focused tests

1. Pin `ScannerController` to `PolicyNames.TicketAdminBoardOrAdmin` in `EndpointAuthorizationTests` or `ScannerControllerTests`. **Done 2026-05-12.**
2. Add a small guardrail that Scanner remains client-only while documented that way. **Done 2026-05-12:**
   - no constructor dependencies on `ScannerController`
   - only GET actions on `ScannerController`
3. Add one Playwright browser test if practical. **Done 2026-05-12:**
   - login as TicketAdmin
   - visit `/Scanner/Barcode`
   - mock `navigator.mediaDevices.getUserMedia` and `window.BarcodeDetector`
   - click Scan, verify a decoded value appears
   - click Stop, verify the mocked media track was stopped
   - assert no app POST/request is made by the scan action
4. Resolve the issue-routing decision. **Done 2026-05-12:** added `Scanner` to `IssueSectionRouting`, `IssueSectionInference`, `AreaLabelMap`, and related tests.

### Phase 3 - Simplify / prune

Likely no Phase 3 code work. If the Playwright test is added, keep it to one behavior-level flow and do not add a JS test framework.

### Phase 4 - Docs

1. Refresh `docs/authorization-inventory.md` so `ScannerController` appears with `TicketAdminBoardOrAdmin`. **Done 2026-05-12.**
2. Refresh `docs/controller-architecture-audit.md` so `/Scanner` and `/Scanner/Barcode` appear. **Done 2026-05-12.**
3. If issue routing changes, update any issue-routing docs/tests that enumerate known areas. **Done 2026-05-12.**
4. Re-check `docs/sections/Scanner.md` and `docs/features/scanner/scanner-barcode.md` after tests land. **Done 2026-05-12.**
