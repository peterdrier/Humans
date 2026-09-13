# Finish section split

Status: proposed implementation plan. Each work package below becomes one or more focused tasks and PRs; this is not an unattended debt-sweep queue. Implementation PR links and completion evidence belong beside the corresponding checkbox.

## Outcome

Adding an optional section, or disabling one, should require no edits to other sections, Base, or Shell once the relevant generic contribution interfaces exist. Required platform sections remain present. Sections own their data and migrations; interactions cross public contracts, never tables or EF entities.

The physical split and many composition interfaces already exist. Remaining work removes logical dependencies, completes ownership cutovers, and makes the add/disable lifecycle work end to end.

## Agreed design direction

- Prefer `Section.cs : ISection, ...` implementing the applicable composition interfaces so its capabilities are visible together. Use explicit implementations for conflicting signatures; partial files or separate contribution classes remain available for readability. Business logic stays in services. See [contribution rules](../../memory/architecture/section-contribution-seams.md).
- Prefer one project per section. Use `.Contracts` leaves when a real dependency cycle requires them; neither assembly references nor interface placement excuse accessing internals.
- Reuse existing contribution interfaces before proposing new ones. New public surface requires a concrete caller and review. A genuinely new platform capability can need a one-time generic Base/Shell change; subsequent sections implementing it should not.
- Section-owned migrations are allowed when needed. Coordinate changes to the same context; preserve migration generation/review discipline and shipped history. Data removal is a separately reviewed cutover step.
- Use existing analyzers and appropriately scoped tests. Do not add blanket architecture-test baselines or new CI gates to substitute for removing coupling.

## Work packages, ordered by value and impact

Priority is impact, not a requirement to serialize every PR. Independent packages can progress together; prerequisites are called out below.

### 1. Complete event-settings ownership

- [ ] Move shared event configuration reads **and writes** from Shifts to the existing Settings owner. Reconcile event identifiers and carry existing data through the cutover. Starting points: `Humans.Settings/Services/Service.cs`, `IBurnSettingsService`, and the existing carry workflow.
- [ ] Verify consumers use Settings, then retire the carry workflow and approve removal of obsolete storage separately. Remove Shifts references only where no genuine shift-domain use remains.

Done when shared event dates/year continue working with Shifts disabled, existing data is verified after migration, and there is one authoritative write path. This does not claim that every current Shifts consumer can lose its dependency.

### 2. Remove optional-domain dependencies from required member lifecycle

- [ ] Rework deletion, email reconciliation, suspension and merge participation around existing contributor patterns. Audit `AccountDeletionService`, `UserEmailService`, `NonCompliantMemberSuspension`, and Governance's `MembershipCalculator` first.
- [ ] Keep canonical identity and lifecycle state with their owners. Distinguish GDPR export composition from Users' deletion scheduling/cancellation. Optional sections handle their own consequences and cache invalidation.

Done when required member operations function without optional sections and every present participant still receives the required action, audit, and deletion/merge work. Preserve consent and Volunteer admission invariants. Respect the existing Peter-led Auth inversion work and its `DontFix` markers rather than silently absorbing it into this task.

### 3. Open crosscut contracts to section-owned contributions

- [ ] Remove the need to edit central domain catalogs for every feature: audit actions, notification sources/mappings, feature-specific email factory methods, and GDPR export section keys. Starting points: `AuditAction`, `NotificationSource`, `NotificationSourceMapping`, `IEmailMessageFactory`, and `GdprExportSections`.
- [ ] Preserve stored identifiers, historical rendering, export compatibility, and delivery behavior while moving domain meaning to its owner. Separate contracts from service implementations; do not turn `Section` into an email or audit business service.

Done when a section adds its own audited action, notification/email content, and export contribution without editing the crosscut's feature catalog. Existing records remain readable.

### 4. Make domain roles and policies section-owned

- [ ] Replace central optional-domain role/policy inventories and the fixed admin-role aggregation with contributed metadata, reusing `ISectionPolicies` where appropriate. Start with Base's `RoleNames`/`PolicyNames` and Shell authorization composition.
- [ ] Preserve existing role identifiers and assignments, management restrictions, and deny behavior. Make policy diagnostics reflect available contributions; the current missing-policy diagnostic logs errors rather than throwing.

Done when a new optional section can declare its roles and policies without changing Base or Shell, and disabling it neither grants access nor breaks unrelated admin access.

### 5. Finish page and search composition

- [ ] Replace fixed profile cards, user-admin detail panels, and Home feature cards with section contributions. Move domain-specific actions to their owning section, retaining stable routes where needed.
- [ ] Make Search aggregate contributed categories/results instead of injecting its fixed set of domain services. Keep authorization and result presentation section-owned; concurrency is not required for independence.
- [ ] Let section navigation resolve labels from its own resources rather than requiring additions to a central shared resource set.

Done when profile, admin, Home, navigation, and search work with a participating section absent, and a new participant edits only itself. Profile/admin composition must carry the target member and authorization context, not assume the current user. Preserve all supported cultures and existing access restrictions. Do not invent an `IUserPiece` split without demonstrating ownership and callers.

### 6. Define and implement optional-section lifecycle

- [ ] Establish the required platform set and distinguish operational disablement from complete removal. Audit `SectionActivation`, `IsActive`, feature flags, and dependencies on services supplied by other sections, such as Tickets' vendor provider.
- [ ] Replace overly broad assembly-based activation assumptions with behavior consistent with actual required services and optional contributions. Keep actionable diagnostics consistent with the no-startup-guards rule; do not merely suppress missing dependency failures.
- [ ] Define how retained personal data, pending work, migrations, and reactivation behave when a feature is off. Ensure export/deletion remain available for retained data and jobs do not execute against absent services. Reuse existing recurring-job cleanup.

Done when representative optional sections can be disabled and re-enabled without breaking required services, losing data obligations, or leaving invalid scheduled work. `IsActive = false` is runtime activation, not removal of the shipped assembly. Physical project removal is a separate packaging check.

Prerequisite for final disable acceptance: packages 1–5 remove the domain dependencies this lifecycle must handle. Resolve the required set and retained-data semantics before implementing dependent lifecycle changes.

### 7. Finish crosscut and connector ownership

- [ ] Remove domain orchestration from generic crosscuts and provider infrastructure. Audit `NotificationMeterProvider`, Campaigns callbacks in `EmailOutboxProcessor`, `HoldedNightlySync`, and Teams' Google-specific event construction.
- [ ] Put each workflow in its domain owner or a table-free orchestrator, using narrow contracts. Make merge and other contributors invalidate their own caches; remove sideways service calls from caching decorators such as `CachingTeamService`.

Done when removing an optional integration or campaign domain leaves generic notifications/email and required member operations usable. Preserve delivery/retry and synchronization behavior; no new orchestration layer without a concrete need.

### 8. Remove remaining assembly and tooling roll calls

- [ ] Replace the explicit section project list in `Humans.Web.csproj` with a repository-appropriate inclusion convention. Audit remaining Shell DI hooks and Base domain types; move domain-owned registration/configuration while retaining rightful Shell platform context.
- [ ] Remove hand-maintained section inventories from migration tooling and contributor expectations where discovery can supply them. Audit `SECTION_DB_CONTEXTS`, GDPR contributor expectations, and tests reaching into another section's context.
- [ ] Consolidate existing contribution declarations on `Section` when touching a section; do not make a repository-wide cosmetic rewrite a prerequisite for independence.

Done when adding a conventional section does not require a Shell project-list or migration-tooling edit, and section tests use the relevant public boundaries. Startup discovery and build-time project inclusion are separate concerns and both must work.

### 9. Prove the transition and retire obsolete paths

- [ ] Run the add and disable acceptance scenarios below. Close remaining ownership exceptions against code evidence, including obsolete carry/history screens and stale architecture documentation; keep each removal scoped to its verified replacement.
- [ ] Link implementation PRs here and close the umbrella task only when the end-state evidence is recorded. Do not equate moved files, smaller constructors, or a clean build with independence.

Depends on the preceding packages. Final verification can expose follow-up tasks; it must not bless grandfathered coupling as the target design.

## Acceptance scenarios

**Add:** Introduce a representative section using the established interfaces. It owns a context/migration, localized navigation and UI, a role/policy, search contribution, audit behavior, and personal-data export/deletion where applicable. Build, migrate, run, and exercise it without editing Base, Shell, or another section. Check project inclusion and migration tooling as well as runtime discovery.

**Disable and re-enable:** Exercise representative sections such as Camps, Store, and Rideshare with existing data, plus an optional provider dependency. Verify boot, required member flows, profile/admin/Home/search, positive and negative authorization, job cleanup, GDPR export/deletion, and data-preserving reactivation. Record which dependencies are truly required and why. Removing an assembly must be tested separately if physical removal is claimed.

**Per implementation PR:** Verify affected invariants, all applicable cultures, authorization deny cases, audit trails, GDPR paths, migrations, navigation, and section-owned tests. Cross-section changes receive the repository's wider test gate. Documentation-only planning needs no build or tests.

## Tracking and decisions

Use this document as the umbrella plan, with one scoped task per work package and smaller PRs where cutovers require stages. Each task records affected owners, existing interfaces considered, acceptance criteria, dependencies, and any migration/data-retirement step. Link existing debt and issues instead of duplicating them into an unattended sweep.

Before lifecycle implementation, confirm the exact required section set and retained-data behavior. Let the implementer choose minor interface/file organization details within the agreed rules; proposed new public contracts still need review. This plan approves the direction, not an unreviewed collection of new APIs or destructive schema changes.
