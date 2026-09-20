# Finish section split

Status: proposed implementation plan. Each work package below becomes one or more focused tasks and PRs; this is not an unattended debt-sweep queue. Implementation PR links and completion evidence belong beside the corresponding checkbox.

## Outcome

Adding an optional section, or disabling one, should require no edits to other sections, Base, or Shell once the relevant generic contribution interfaces exist. Required platform sections remain present. Sections own their data and migrations; interactions cross public contracts, never tables or EF entities.

The physical split and many composition interfaces already exist. Remaining work removes logical dependencies, completes ownership cutovers, and makes the add/disable lifecycle work end to end.

## Where it stands

Measured against `origin/main` at the time of writing, from the section `.csproj` reference graph and Shell source:

- Sections are assemblies, discovered by reflection. `Sections:Active` / `ISection.IsActive` exists and its dependency guard is derived from real assembly references (nobodies-collective/Humans#1081). Shell's own data is the data-protection key table and nothing else.
- Nav, admin nav, tiles, chrome slots, jobs, health checks, endpoints, policies, GDPR export/erasure, migrations and localization are all discovered. None of them needs a Shell edit per section.
- Data access is clean: no section reaches another's `DbContext`, and there is no raw SQL under `src/Sections`.
- Shell itself pins Users, Auth, Backdoor, Email, GoogleIntegration and Tickets: the guard reads Shell's compiled references the same way, and those come from sign-in glue, `MembershipRequiredFilter`'s Backdoor-scheme check, and the three `Extensions/Infrastructure/*` settings bindings (package 10).
- What pins sections today is the **core depending on the optional**. Users references Governance, Shifts, Onboarding, Tickets, Campaigns, Notifications, GoogleIntegration, Consent and Camps (the last as an implementation reference, for a view component on the profile page; Events reaches that page through the `IUserPart` seam and is not referenced). Auth references Teams, Email and Notifications. Because the activation guard reads those references, none of the sections Users or Auth name can be switched off.
- The sections neither Shell nor another section references by assembly: Cantina, Debug, Development, Expenses, Gate, Guide, MailerLite, Monitor, Rideshare, Scanner, Search, Store, TicketTailor, Tour. These are the candidates for disable testing, not proven independent: the guard sees assembly references only, and a DI registration one section supplies for another's port is invisible to it. TicketTailor is the counterexample: it is the only registrar of `ITicketVendorService`, which Tickets requires, so deactivating it passes the guard and fails at first resolution.

## Agreed design direction

- Prefer `Section.cs : ISection, ...` implementing the applicable composition interfaces so its capabilities are visible together. Use explicit implementations for conflicting signatures; partial files or separate contribution classes remain available for readability. Business logic stays in services. See [contribution rules](../../memory/architecture/section-contribution-seams.md) and nobodies-collective/Humans#1088.
- Prefer one project per section. Use `.Contracts` leaves when a real dependency cycle requires them ([`section-project-cycle-fix`](../../memory/architecture/section-project-cycle-fix.md)); neither assembly references nor interface placement excuse accessing internals. An implementation reference from an orchestrating section to the sections it orchestrates is expected ([`orchestrator-sections-reference-orchestrated`](../../memory/architecture/orchestrator-sections-reference-orchestrated.md)).
- Reuse existing contribution interfaces before proposing new ones. New public surface requires a concrete caller and review. A genuinely new platform capability can need a one-time generic Base/Shell change; subsequent sections implementing it should not.
- The activation guard stays derived from assembly references ([`no-literal-ties-in-guardrails`](../../memory/process/no-literal-ties-in-guardrails.md)). The packages below make it less restrictive by removing references, never by declaring a required list beside it.
- Section-owned migrations are allowed when needed. Coordinate changes to the same context; preserve migration generation/review discipline and shipped history. Data removal is a separately reviewed cutover step.
- Use existing analyzers and appropriately scoped tests. Do not add blanket architecture-test baselines or new CI gates to substitute for removing coupling.

## Work packages, ordered by value and impact

Priority is impact, not a requirement to serialize every PR. Independent packages can progress together; prerequisites are called out below.

### 1. Remove optional-domain dependencies from the required member core

The single highest-leverage package: every section Users or Auth references is pinned active until this lands.

- [ ] Rework deletion, email reconciliation, suspension and merge participation around existing contributor patterns. Audit `AccountDeletionService`, `UserEmailService`, `NonCompliantMemberSuspension`, `UserParticipationBackfillService`, `UsersAudienceService`, and Governance's `MembershipCalculator` first.
- [ ] Profile and admin-detail composition: adopt the `IUserPart` fan-out already designed in nobodies-collective/Humans#1112 and #1044, so the profile page, `AdminHumanDetailViewModelBuilder`, `SectionAdminTiles` and `SectionThingsToDo` in Users compose contributed pieces instead of naming Governance, Tickets, Shifts, Campaigns, Camps and Events. This is what removes the Camps and Events implementation references from `Humans.Users.csproj`.
- [ ] Users' outbound cache flushes (`IShiftAuthorizationInvalidator`, `IShiftViewInvalidator`, `IConsentCacheInvalidator`) move to package 2's bus.
- [ ] Auth: `RoleAssignmentService` reaches Teams, Email and Notifications. Respect the existing Peter-led Auth inversion work and its `DontFix` markers rather than silently absorbing it into this task; record what remains.
- [ ] Keep canonical identity and lifecycle state with their owners. Distinguish GDPR export composition from Users' deletion scheduling/cancellation. Optional sections handle their own consequences.

Done when required member operations, the profile page and the admin member page function with every optional section deactivated, and every present participant still receives the required action, audit, and deletion/merge work. Preserve consent and Volunteer admission invariants. The done-state depends on package 2 for invalidation and shares the composition seam with package 6.

### 2. Replace point-to-point cache invalidation with the in-process event bus

- [ ] Settle the open design in nobodies-collective/Humans#799 first: failure semantics (a failing subscriber must not fail the writer), handler lifetimes, and whether publish happens inside or after the writer's transaction. Then land it: a writer publishes a fact about its own domain; caches subscribe. No writer names another section's invalidator.
- [ ] Migrate every `I<X>Invalidator` injection site and retire the interfaces as their last subscriber moves. The HUM0028 grandfathers go with them (`grandfathered-hum0028-invalidators` in the debt ledger).

Done when no section injects another section's invalidator, `[CrossSectionWrite]` sites that existed only to flush a cache are gone, and package 1's Users flushes and package 8's connector flushes ride the bus. Prerequisite for the done-state of packages 1 and 8.

### 3. Complete event-settings ownership

- [ ] Move shared event configuration reads **and writes** from Shifts to the existing Settings owner (nobodies-collective/Humans#1104, #809, #864). Reconcile event identifiers and carry existing data through the cutover. Starting points: `Humans.Settings/Services/Service.cs`, `IBurnSettingsService`, `IBurnSettingsInfo` in `Humans.Shifts.Contracts`, and the existing carry workflow.
- [ ] Verify consumers use Settings, then retire the carry workflow and approve removal of obsolete storage separately. Remove Shifts references only where no genuine shift-domain use remains. `Humans.Testing` carries `BurnSettingsInfo` fixtures that follow the type.

Done when shared event dates/year continue working with Shifts disabled, existing data is verified after migration, and there is one authoritative write path. This does not claim that every current Shifts consumer can lose its dependency.

### 4. Open crosscut contracts to section-owned contributions

- [ ] Remove the need to edit central domain catalogs for every feature: audit actions, notification sources/mappings, and feature-specific email factory methods. Starting points: `AuditAction`, `NotificationSource`, `NotificationSourceMapping`, `IEmailMessageFactory`. GDPR export section keys are done: `GdprExportSections` is gone, each contributor declares its own constants, and `GdprExportDependencyInjectionTests`/`GdprErasureCoverageTests` derive their rosters by reflection (nobodies-collective/Humans#1116).
- [ ] Preserve stored identifiers, historical rendering, export compatibility, and delivery behavior while moving domain meaning to its owner. Separate contracts from service implementations; do not turn `Section` into an email or audit business service.

Done when a section adds its own audited action, notification/email content, and export contribution without editing the crosscut's feature catalog. Existing records remain readable.

### 5. Make roles, policies and the Base registries section-owned

- [ ] Replace central optional-domain role/policy inventories and the fixed admin-role aggregation with contributed metadata, reusing `ISectionPolicies` where appropriate. Start with Base's `RoleNames`/`PolicyNames`/`RoleGroups` and Shell authorization composition.
- [ ] Preserve existing role identifiers and assignments, management restrictions, and deny behavior. Make policy diagnostics reflect available contributions; the current missing-policy diagnostic logs errors rather than throwing.
- [ ] The other hand-maintained per-section registries in Base follow the same rule as [`base-ui-registries-are-section-populated`](../../memory/architecture/base-ui-registries-are-section-populated.md): `AccessMatrixDefinitions`, `SectionHelpContent`, `CacheKeys` (key builders and the cache-stats metadata table). Each becomes a contribution or a section-side registration; Base keeps the seam, never the rows.
- [ ] Single-owner strays leave Base for their owner: the Google-only `TempDataKeys`, the Guide and community-KB settings and GitHub content sources, `GoogleSyncSource`, `_VolunteerSearchScript.cshtml`. `SystemTeamType`/`SystemTeamIds` are shared infrastructure but grow per section; decide whether the system-team sync can take a contributed descriptor instead of an enum member.

Done when a new optional section can declare its roles, policies, access matrix, help content and cache keys without changing Base or Shell, and disabling it neither grants access nor breaks unrelated admin access.

### 6. Finish page and search composition

- [ ] Replace fixed profile cards, user-admin detail panels, and Home feature cards with section contributions, using the package-1 seam (nobodies-collective/Humans#1112, #1044). Move domain-specific actions to their owning section, retaining stable routes where needed.
- [ ] Make Search aggregate contributed categories/results instead of injecting its fixed set of domain services. Keep authorization and result presentation section-owned; concurrency is not required for independence.
- [ ] Let section navigation resolve labels from its own resources rather than requiring additions to a central shared resource set. Settle nobodies-collective/Humans#1090 (Search and Tour as chrome or as sections) the same way.
- [ ] `MembershipRequiredFilter.ExemptControllers` in Shell is a by-name list of section controllers reachable before a member is Active; make it a seam property.

Done when profile, admin, Home, navigation, and search work with a participating section absent, and a new participant edits only itself. Profile/admin composition must carry the target member and authorization context, not assume the current user. Preserve all supported cultures and existing access restrictions.

### 7. Define and implement optional-section lifecycle

Lower priority than its position suggests: it is the future multi-event direction, sequenced after the dependency packages and not needed for any current deployment.

- [ ] Establish the required platform set and distinguish operational disablement from complete removal. Audit `SectionActivation`, `IsActive`, feature flags, and dependencies on services supplied by other sections, such as Tickets' vendor provider.
- [ ] Keep the activation guard derived from assembly references; packages 1 to 6 are what make it permissive. Extend it to cover port registrations one section supplies for another (the TicketTailor case above), derived the same way, never declared.
- [ ] **Decided (Peter, 2026-09-14): start anyway.** nobodies-collective/Humans#1081 asked the guard to fail startup on an unmet dependency; [`no-startup-guards`](../../memory/architecture/no-startup-guards.md) wins. The guard becomes a boot-time error log plus a `/Debug/Sections` diagnostic; the deactivated section's routes 404 and anything injecting its services fails at that request, never at boot.
- [ ] Deactivation itself is future direction, not current work: the target is a stored section on/off configuration (database or config, undecided) for when other events run this codebase with their own module set. Nothing in this plan needs a section switched off today; packages 1 to 6 are justified by independence alone.
- [ ] Define how retained personal data, pending work, migrations, and reactivation behave when a feature is off. Ensure export/deletion remain available for retained data and jobs do not execute against absent services. Reuse existing recurring-job cleanup.

Done when representative optional sections can be disabled and re-enabled without breaking required services, losing data obligations, or leaving invalid scheduled work. `IsActive = false` is runtime activation, not removal of the shipped assembly. Physical project removal is a separate packaging check.

Prerequisite for final disable acceptance: packages 1 to 6 remove the domain dependencies this lifecycle must handle. Resolve the required set and retained-data semantics before implementing dependent lifecycle changes.

### 8. Finish crosscut and connector ownership

- [ ] Remove domain orchestration from generic crosscuts and provider infrastructure. Audit `NotificationMeterProvider`, `HumansMetricsService` (push model, nobodies-collective/Humans#580), Campaigns callbacks in `EmailOutboxProcessor`, `HoldedNightlySync`, and Teams' Google-specific event construction.
- [ ] Put each workflow in its domain owner or a table-free orchestrator, using narrow contracts. Contributors invalidate their own caches through package 2's bus. `CachingTeamService.GetTeamDirectoryAsync` resolves `IRoleAssignmentService` sideways, violating [`decorators-talk-only-to-inner`](../../memory/architecture/decorators-talk-only-to-inner.md) today; it is ledgered in Teams' `Docs/debt.yml` so the sweep reaches it before this package does.

Done when removing an optional integration or campaign domain leaves generic notifications/email and required member operations usable. Preserve delivery/retry and synchronization behavior; no new orchestration layer without a concrete need.

### 9. Let the compiler enforce the boundary

- [ ] Internalize implementation-only write interfaces and rename `I<X>ServiceRead` to `I<X>Service` (nobodies-collective/Humans#1058). A write another section genuinely needs stays on the public contract, reviewed; what becomes a compile error is reaching a write nobody agreed to export.
- [ ] Fold the `.Contracts` leaves no cycle pins (nobodies-collective/Humans#1066, #1041) and retire the analyzers, baselines and `[Grandfathered]` entries the assembly boundary subsumes (nobodies-collective/Humans#1010).

Done when a section's public surface is its `Contracts/` folder or leaf plus the exceptions design-rules already enumerates (`Section`, the `<Section>Resource` marker, migrations, `Jobs/`, and framework-discovered view components and tag helpers), and the remaining analyzers are the ones the compiler cannot express.

### 10. Remove remaining assembly and tooling roll calls

- [ ] Replace the explicit section project list in `Humans.Web.csproj` with a repository-appropriate inclusion convention. Move the `Extensions/Infrastructure/*` settings bindings (Email, Google, ticket-vendor port) and `MembershipRequiredFilter`'s Backdoor-scheme check behind seams, so Shell's compiled references shrink to the declared identity core (Users, Auth) and the guard stops pinning Backdoor, Email, GoogleIntegration and Tickets.
- [ ] Remove hand-maintained section inventories from migration tooling and contributor expectations where discovery can supply them. Audit `SECTION_DB_CONTEXTS` in `build.yml`, GDPR contributor expectations, and tests reaching into another section's context.
- [ ] Consolidate existing contribution declarations on `Section` when touching a section (nobodies-collective/Humans#1088); do not make a repository-wide cosmetic rewrite a prerequisite for independence.

Done when adding a conventional section does not require a Shell project-list or migration-tooling edit, and section tests use the relevant public boundaries. Startup discovery and build-time project inclusion are separate concerns and both must work.

### 11. Prove the transition and retire obsolete paths

- [ ] Run the add and disable acceptance scenarios below. Close remaining ownership exceptions against code evidence, including obsolete carry/history screens and stale architecture documentation; keep each removal scoped to its verified replacement.
- [ ] Link implementation PRs here and close the umbrella task only when the end-state evidence is recorded. Do not equate moved files, smaller constructors, or a clean build with independence.

Depends on the preceding packages. Final verification can expose follow-up tasks; it must not bless grandfathered coupling as the target design.

## Acceptance scenarios

**Add:** Introduce a representative section using the established interfaces. It owns a context/migration, localized navigation and UI, a role/policy, search contribution, audit behavior, and personal-data export/deletion where applicable. Build, migrate, run, and exercise it without editing Base, Shell, or another section. Check project inclusion and migration tooling as well as runtime discovery.

**Disable and re-enable:** Exercise representative sections such as Camps, Store, and Rideshare with existing data, plus an optional provider dependency. Verify boot, required member flows, profile/admin/Home/search, positive and negative authorization, job cleanup, GDPR export/deletion, and data-preserving reactivation. Record which dependencies are truly required and why. Removing an assembly must be tested separately if physical removal is claimed.

**Per implementation PR:** Verify affected invariants, all applicable cultures, authorization deny cases, audit trails, GDPR paths, migrations, navigation, and section-owned tests. Cross-section changes receive the repository's wider test gate. Documentation-only planning needs no build or tests.

## Tracking and decisions

Use this document as the umbrella plan, with one scoped task per work package and smaller PRs where cutovers require stages. Each task records affected owners, existing interfaces considered, acceptance criteria, dependencies, and any migration/data-retirement step. The issues linked in each package are the existing tracking; extend them rather than duplicating them into an unattended sweep.

Before lifecycle implementation, confirm the exact required section set and retained-data behavior. Let the implementer choose minor interface/file organization details within the agreed rules; proposed new public contracts still need review. This plan approves the direction, not an unreviewed collection of new APIs or destructive schema changes.
