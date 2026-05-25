# Analyzer & Architecture-Test Consolidation

**Status:** plan / awaiting Peter review · **Branch:** `chore/analyzer-consolidation` · **Date:** 2026-05-25
**Driving rule:** [`memory/architecture/universal-enforcement-over-per-section.md`](../../../memory/architecture/universal-enforcement-over-per-section.md)

## Goal

Collapse the per-section architecture enforcers (analyzers + the 48 `tests/Humans.Application.Tests/Architecture/*ArchitectureTests.cs` files, ~280 methods) into **universal** enforcers derived from the hard rules, and delete the per-section instances they subsume. Orchestrator + worker-agent swarm, big-bang, not a multi-week rollout.

**Non-goal — and the discipline that governs this doc:** do not invent a universal rule to justify a consolidation. Every row below is tagged **grounded** (cites `peters-hard-rules.md`, `design-rules.md`, a `memory/` atom, a `Rules/*.cs` class, or a `HUM####` analyzer) or **proposed** (similar tests exist, no governing rule — a DECISION for Peter, never a silent migration). This bit twice in discussion; it's load-bearing.

## A1 — resolved design (grill 2026-05-25; supersedes the "ownership-map" framing elsewhere in this doc)

A1 is a **single-repository-per-table** analyzer (renamed from "cross-section DbSet write"). Resolved branches:

- **Q1 — enforce both hard rules verbatim:** "only the repository writes its section's tables" (per-Lane) AND "a table must only exist in one repository" (per-table 1:1 within a Lane). A Lane may hold several repositories; they must not share a table.
- **Q2 — no declared ownership map.** Ownership is *emergent*: whichever single repository references a table IS its owner.
- **Q3 — count reads AND writes** (any `DbSet` reference, not just `Add/Update/Remove`). Rationale: in the §15 cache-first design a *foreign read* bypasses the owning section's cache → stale reads, so reads are a coherence bug, not soft coupling. Mechanism: across the Infrastructure compilation build `table → {repository types referencing its DbSet}`; **N>1 → diagnostic at each access site**. With HUM0009 (only repositories touch the DbContext) this yields "each table accessed by exactly one repository, in exactly one Lane." Cross-Lane sharing is subsumed (owner + foreigner both touch it → N≥2).
- **Q4 — grandfather at (repo, table) granularity.** Add an optional `scope` param to `GrandfatheredAttribute` (the DbSet name); this analyzer matches (ruleId + scope). `AllowMultiple` already lets a repo list one per shared table. New sharing of an unlisted table still errors. `scope` defaults null; existing analyzers ignore it (no churn). Severity Error + grandfathered + a `WarningsNotAsErrors;HUM0025` ledger entry.
- **Q5 — staged detection.** Phase 1 (this PR): direct `DbSet` references only (`ctx.X`, `ctx.Set<T>()`). Phase 2 (tracked fast-follow): reads via navigation/`Include` (closes the residual intra-Lane cache gap; cross-Lane navs already blocked by HUM0024).

Accepted gaps: (a) sole-foreign-writer — N=1 but the repo's `[Section]` Lane ≠ the table's `Configurations/<Lane>/` folder — not caught by the count; optional cheap cross-check later. (b) nav/`Include` reads until Phase 2.

Counting unit = top-level types implementing `IRepository` in the Infrastructure compilation; framework stores (Identity `UserStore` etc.) are not repositories and don't count (their DbContext access is already governed by HUM0009 grandfathers). HUM id: **HUM0025** (HUM0024 taken; HUM0023 was taken mid-flight by the Events write analyzer in #769, now subsumed here). Initial `(repo, table)` grandfather set: derived empirically from a whole-compilation build, cross-checked against `docs/architecture/service-data-access-map.md` §"Cross-Section Analysis".

Replaces: `NotificationDbSetWriteAnalyzer` (HUM0022), `EventDbSetWriteAnalyzer` (HUM0023, merged in #769 after this plan was drafted), and the `Only_AuditLogRepository_Writes_*` ratchet test (the Events ratchet was already retired by #769).

## Current state (main @ 9355223d6)

Analyzers: HUM0001–0024 (HUM0023 added by #769). Active git pattern is "convert ratchet → analyzer" (concurrency HUM0007, obsolete-nav HUM0021, cross-section-EF-join HUM0024, **Notification DbSet writes HUM0022**, **Event DbSet writes HUM0023**).

**The fork:** DbSet-write ownership is enforced three ways for one rule — Notifications → analyzer `NotificationDbSetWriteAnalyzer` (HUM0022, hardcodes Notification DbSets + `NotificationRepository`); Events → analyzer `EventDbSetWriteAnalyzer` (HUM0023, hardcodes the seven event_* DbSets + `EventRepository`; #769 promoted it from the former ratchet test); AuditLog → ratchet test `Only_AuditLogRepository_Writes_AuditLogEntries_DbSet`. Two hardcoded per-section analyzers + one ratchet, same rule.

Two live `SaveChangesInterceptor`s: `UserInfoSaveChangesInterceptor`, `LegalDocumentSaveChangesInterceptor`. `IOrchestrator` marker does **not** exist ([`orchestrator-marker`] atom is "to build").

## Existing universal enforcers (the targets to consolidate toward)

Reflection ratchet/rule classes in `tests/.../Architecture/Rules/`:
- `IRepositoryImplementationsAreSealedRule` — all `IRepository` impls sealed.
- `RepositoryImplementationsLiveInInfrastructureRule` — impls under `Humans.Infrastructure.Repositories.*`.
- `ApplicationServicesTakeNoDbContextRule` — no service ctor takes `HumansDbContext`/`IDbContextFactory`.
- `ApplicationServicesTakeNoMemoryCacheRule` — no service ctor takes `IMemoryCache` unless allowlisted (allowlist currently lists `CampService`/`CalendarService` etc. — see drift note below).
- `NoBusinessLogicInControllersRule` (Skip), `DisplaySortInControllersRule`, `NoLinqAtDbLayerRule`, `NoDestructiveMigrationOpsRule`, `NoStartupGuardsRule`.
- `ServiceBoundaryArchitectureTests` — universal, holds the `RepositoryOwners` map + two ratchet scans: `Application_service_read_methods_do_not_add_new_entity_return_types` and `Application_services_do_not_add_cross_section_repository_injections`.

Analyzers: HUM0007 concurrency, HUM0008 controller-no-DbContext, HUM0009 only-IRepository-uses-DbContext, HUM0012 service namespace, HUM0013 repo-interface namespace, HUM0014 web-no-repo-injection, HUM0017 cross-section repo injection, HUM0020 caching-decorator-no-repo, HUM0021 obsolete-nav read, HUM0024 cross-section EF join.

**Drift to fix:** the `ApplicationServicesTakeNoMemoryCacheRule` allowlist names `CampService`/`CalendarService` as cachers, while `CampsArchitectureTests`/`CalendarArchitectureTests` assert those services inject *no* `IMemoryCache` (caching moved to decorators). Two sources of truth disagree — pick the universal rule, reconcile the allowlist, in Wave B.

## Pattern → enforcer catalog (the Wave B mapping)

Counts are approximate (from a `public void` census across the 48 files). "Verdict" is the decided action so workers execute a mapping, not re-derive one.

| # | Per-section pattern (example) | ~N | Owned by (grounded) | Verdict |
|---|---|---|---|---|
| A | `*Repository_IsSealed` / `..._sealed_implementation` | ~30 | `IRepositoryImplementationsAreSealedRule` | **delete** |
| B | `*Service_HasNoIMemoryCacheConstructorParameter`, `DoesNotInjectIMemoryCache`, `DoesNotInjectAnyCachingNamespaceMember` | ~17 | `ApplicationServicesTakeNoMemoryCacheRule` | **delete** + reconcile allowlist drift |
| C | `*Service_HasNoDbContext/DbSetConstructorParameter` | ~6 | `ApplicationServicesTakeNoDbContextRule` | **delete** |
| D | `*Service_LivesIn*Namespace`, `AssemblyIsHumansApplication`, `I*Service_LivesInApplicationInterfaces*` | ~15 | HUM0012 (impl side) | **delete** impl-side; interface-namespace has a small gap — leave those few |
| E | `I*Repository_LivesInApplicationInterfacesRepositoriesNamespace` | ~6 | HUM0013 | **delete** |
| F | `*Repository_LivesInInfrastructure*`, `AssemblyIsHumansInfrastructure`, `ImplementationLivesInHumansInfrastructure` | ~8 | `RepositoryImplementationsLiveInInfrastructureRule` | **delete** |
| G | `*Service_TakesRepository` / `TakesRepositoryInterface` (positive wiring) | ~25 | n/a — fires on *absent* code, can't be an analyzer; low value | **delete** (noise) |
| I | `*Constructor_HasNoCrossSectionRepositories`, `RoutesCrossSectionDataThroughOwningServices`, `TakesNoOtherSectionRepository`, `depends_only_on_section_services_not_repositories` | ~12 | HUM0017 + `ServiceBoundary` cross-section ratchet | **delete** (cross-section-repo half) |
| L | `Only_*Repository_Writes_*_DbSet` (Events/Notifications/AuditLog) | 3 | **A1 → new HUM0025** | **delete after A1 green**, cite HUM0025 |
| N2 | cross-domain nav stripping: `*_HasNoUserNavigationProperty`, `*_KeepsUserIdForeignKey`, `..._HaveNoCrossDomainNavigationProperties`, `*OwningTeamNavIsObsolete` | ~6 | HUM0021 + HUM0024 (partial) | **mostly delete**; verify coverage first, keep any FK-keep assertion not covered |

### Proposed (NO governing rule — DECISION required, NOT in this PR)
| # | Pattern | ~N | Note |
|---|---|---|---|
| H | `*Service_ConstructorTakesNoStoreType` | ~10 | "no-store §15d" appears only in test comments, not a documented rule. Decide: make it a rule (one universal enforcer) or drop. |
| J/K | caching-decorator shape + read/write split: `Caching*_IsSealed`, `...LivesInInfrastructureServices<Section>`, `Implements I*ServiceRead`, `..._ResolveToSameSingleton`, `I*Service_InheritsI*ServiceRead`, `I*ServiceRead_ExposesNoEntityTypes` | ~37 | HUM0020 + `ServiceBoundary` entity-return ratchet cover part. Rest = consolidate into one analyzer (decorator shape) + one universal reflection test (DI-singleton/inherits — can't be analyzers). Decide scope. |
| M | connector/SDK isolation: `*_DoesNotReferenceGoogleSdkTypes`, `HasNoReferenceToStripeNet`, `Exposes*No*SdkTypesInSignatures`, `AllDtoTypesLiveInApplicationDtos` | ~20 | Consolidate to one assembly-reference test (forbidden-SDK list) + one signature analyzer. Forbidden list is partly per-connector. Decide. |
| N1 | append-only repos: `I*Repository_HasNoUpdateOrDeleteMethods` (Consent/AuditLog/CityPlanning/Events-moderation) | ~4 | No unifying rule — doctrine is "only audit-log immutability is doctrinal" ([`db-enforcement-minimal`], [`consent-record-immutable`] uses DB triggers). Decide before any `IAppendOnlyRepository` marker. |

## Tier-3 keep candidates (genuine one-offs)

Re-verify each in Wave B with the "can it be restated generally?" test before keeping. **Verified** = body read this session; **inferred** = rationale guessed from the method name, MUST be read before trusting.

**Verified (body read):**
- Camps — `PublicCampDetail_DoesNotExposeEarlyEntryState` (EE admin-only, #490); `CampInfo_Active_ReturnsLatestSeasonByYear` (behavioral unit test — relocate out of Architecture/); `CampRoleService_IsTheOnlyCampsSideGoogleGroupMembershipSource` + `CampRoleService_DoesNotDependOnGoogleSyncOrProvisioning` (#740 — **caveat:** generalizable the moment a 2nd section claims Google groups; keep only while Camps is the sole claimant).
- Events — `EventsRoutes_UseEventsSlug`, `EventsRoutes_DoNotExposeOldEventGuideOrCampsSlugs` (slug/migration guard); `EventService_ImplementsIUserDataContributor` (owns user-scoped event_favourites/event_preferences → GDPR export); `CachingEventService_IsItsOwnHostedService` (self-hosting warmup).
- Mailer — `IMailerLiteService_OnlyAllowsAudienceWrites`, `AllAudiences_UseHumansPrefix`, `AllAudiences_HaveUniqueGroupNamesAndKeys`.
- Holded — `HoldedExceptions_AreClassified_TransientOrPermanent`.
- Membership — `MembershipCalculator_DoesNotTakeTeamServiceDirectly` + `..._DoesNotTakeRoleAssignmentServiceDirectly` (must route via `IMembershipQuery` to break the `ISystemTeamSync` DI cycle).

**Inferred (READ BODY before trusting/keeping):**
- User — `NoOAuthTokenInUserEmailServiceOrRepositoryMethodNames`.
- TicketQuery — `ITicketServiceRead_DoesNotExposeHasCurrentEventTicketAsync`, `CachingTicketQueryService_IsTheOnlyImplementationOfITicketCacheInvalidator`, `TicketQueryService_DoesNotImplementITicketCacheInvalidator`.
- ShiftView — `IShiftView_AllMethodsReturnValueTask`, `ShiftUserView_IsRecord`, `ShiftRotaView_IsRecord`.
- Calendar — `CalendarEventInfo_IsImmutableRecord`. Consent — `IConsentRepository_HasAddAsyncMethod`. TicketVendor — `ITicketVendorService_AllDtoTypesLiveInApplicationDtos`.

**Reclassified — NOT a keep:** `CampInfoSaveChangesInterceptor_IsNotPresent` is the per-class form of the interceptor smell; it belongs to the cross-section-write / interceptor family, not Tier-3 (see interceptors below).

## Interceptors (proposed — per-interceptor DECISION; out of this PR)

The principle (Peter's, grounded in [`crosscut-purity`]): an interceptor existing at all flags a cross-section write / a cache-invalidation the owning section's service+decorator should own. Two live ones, ask **individually**:
- `UserInfoSaveChangesInterceptor` — post-commit refreshes the UserInfo cache from changed User/UserEmail/EventParticipation/IdentityUserLogin/CommunicationPreference entities; catches writes bypassing IUserService. Spans many sections' writes; the most defensible (crosscut cache) but the widest-reaching.
- `LegalDocumentSaveChangesInterceptor` — post-commit `ILegalDocumentCacheInvalidator.InvalidateAll()` on `legal_documents`/`document_versions` writes. Single-section flush.

## Severity discipline (grounded: [`analyzer-exceptions-via-attributes`])

`TreatWarningsAsErrors=true` **stays** (Peter: better to fix per-PR than let warnings accumulate). Every new analyzer encodes its migration severity at the source, one of two shapes:
- **Shape 2 (default) — Error + `[Grandfathered("HUM####", reason)]`** on each finite existing violator. New violation → Error (fails the PR that adds it); known debt rides as a visible warning. Add the id to `Directory.Build.props` `WarningsNotAsErrors` (with a comment + exit condition) so the grandfathered warning survives global TWAE; delete that line when the last `[Grandfathered]` is gone.
- **Shape 1 — `defaultSeverity: Warning` + a commented, exit-tagged `WarningsNotAsErrors` entry** — only when there's no clean per-site grandfather and a visible whole-codebase warning window is accepted (HUM0017/0019 today).

Forbidden: blanket `Directory.Build.props` downgrade of an Error rule; `Info`/`NoWarn`/`#pragma`/`SuppressMessage`/baseline for live architecture debt (all hide it). The `WarningsNotAsErrors` list is the visible migration ledger, not waste.

## Execution — orchestrator + workers, two waves

Orchestrator = main session (Opus). Workers = `Agent` with `isolation: "worktree"`; mechanical work on Sonnet, analyzer-authoring on Opus ([`model-tiering`]). One integration branch → **one draft PR** ([`no-direct-to-main`], one-branch-one-PR, [`wip-prs-as-draft`]). Orchestrator owns shared files (`AnalyzerReleases.Unshipped.md`, `Directory.Build.props`, `memory/INDEX.md`) and the final `dotnet build`/`test`.

### Wave A — universal analyzers (analyzer project only; no test-file edits)
- **A1 — universal `CrossSectionDbSetWriteAnalyzer` (HUM0025).** Generalize HUM0022 using HUM0024's ownership map (`DbSet → owning section`; allowed writer = that section's `IRepository`). Shape 2. Delete `NotificationDbSetWriteAnalyzer` + its test + the HUM0022 catalog row. Grandfather existing violators (early run already surfaced `UserRepository` + ≥1 other — expect a real list). Does NOT touch section test files.
- **A2 — orchestrator-no-repository analyzer. DEFERRED** (own follow-up): needs `IOrchestrator` marker created + a labeling judgment on which services are orchestrators. Don't block A1/B.

Gate: A integrated, `dotnet build Humans.slnx -v quiet` green.

### Wave B — per-section test cleanup (test files only; partitioned by file group, after A1 green)
N Sonnet workers, each owning a disjoint set of `*ArchitectureTests.cs`. Delete patterns **A, B, C, D(impl), E, F, G, I, L, N2** per the catalog, each deletion citing its enforcer in the PR. Leave Tier-3 keeps and anything uncertain; flag each kept/uncertain method in the PR body. No file owned by two workers. Reconcile the IMemoryCache allowlist drift here.

### Decision gates (parallel with Peter; block only the items they name)
Interceptors (individually); proposed patterns H, J/K-extras, M, N1; Tier-3 inferred-rationale verification. None block Wave A/B.

## Acceptance criteria
- One universal cross-section-write analyzer; `NotificationDbSetWriteAnalyzer` + Events/AuditLog `Only_*Writes*` ratchet tests gone.
- New violations in HUM0025 emit Error; existing violators carry `[Grandfathered]`; no blanket `Directory.Build.props` downgrade.
- `dotnet build` + `dotnet test` green; net test count down; **every deleted test maps to a cited enforcer** (no rule silently lost).
- IMemoryCache allowlist drift reconciled. Intention atom + `INDEX.md` line in this PR.

## Out of scope (this PR)
Interceptor removal; proposed patterns H/J-K-extras/M/N1; A2; Tier-3 re-derivation beyond deleting clearly-covered methods.
