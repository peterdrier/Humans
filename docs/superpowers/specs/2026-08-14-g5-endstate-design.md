# G5 End-State Design — Decisions (nobodies-collective/Humans#866)

**Status: decided.** Design session Peter + Claude, 2026-08-14 (recorded on
[#866](https://github.com/nobodies-collective/Humans/issues/866), comments of 2026-08-14).
This doc amends the end-state sections of
[`2026-08-07-g5-section-project-split-design.md`](2026-08-07-g5-section-project-split-design.md)
and #866's original structure sketch. Where they disagree, **this doc is right**. The
mechanics recipe ([`G5-SECTION-TEMPLATE.md`](../../sections/G5-SECTION-TEMPLATE.md)) is
unaffected.

Fact base: full file-level inventory audited 2026-08-14 against `origin/main` post-#1292
(GoogleIntegration merged, Shifts merged, Camps merged, Users+Profiles lane in flight) —
appendices [`…-inventory-application.md`](2026-08-14-g5-endstate-inventory-application.md)
and [`…-inventory-infra-ui.md`](2026-08-14-g5-endstate-inventory-infra-ui.md).

## The three tiers

```
Humans.Base      ← bottom. An RCL. References NOTHING except Humans.Users.Contracts
                   (sanctioned, "for now — revisit later"; see Base floor below).
                   = today's Humans.Interfaces RENAMED (it is already the floor), plus
                   primitives, base contracts, and Humans.UI's generic view plumbing.
                   Rule: no SECTION types, ever (softened from "no DTOs, ever" when
                   Base absorbed the view plumbing).
Humans.<Section> ← middle. References Base + other sections' .Contracts, or another
                   section's impl project directly where no cycle forces a carve.
                   A .Contracts leaf exists ONLY where a real cycle forces it.
Humans.Web       ← top. DI roll-call, middleware pipeline, chrome (_Layout/_AdminLayout/
                   nav), wwwroot, operational/host services. Mostly DI things — NOT a
                   home for section logic. Rename to Humans.Host: optional, cosmetic.
```

**There is no `Platform/` tier** (supersedes #866's sketch). Auth and AuditLog are
ordinary sections that happen to have wide fan-in; "horizontal" is a role, not a tier.

**Deleted at end state:** `Humans.Application`, `Humans.Infrastructure`, `Humans.Domain`,
`Humans.UI`. `Humans.Interfaces` is renamed to `Humans.Base`, not recreated.

## "G5 complete" is graph-shaped, not file-shaped

Emptying `Humans.Application` is not done. Done is:

1. Application / Infrastructure / Domain / UI deleted; Interfaces → Base rename landed.
2. Every `src/Sections/*` csproj references only: Base, `.Contracts` leaves, and
   cycle-free direct section references. No hub references anywhere.
3. `Humans.Web` references sections + Base and holds no section logic.

Rationale: as of 2026-08-14 every section still references Application + Infrastructure +
Domain + Interfaces + UI, and UI references Application — the hub survives file moves
unless the reference drop is an explicit lane.

## The Base floor (decided 2026-08-14)

- **A leaf may reference Base and any leaf that itself has zero `<ProjectReference>`.**
  Such a chain terminates and cannot cycle, so leaf→leaf is not per se a violation.
  (Amended twice. An early draft said leaves reference "NOTHING (framework only)"; the
  2026-08-14 amendment made it "nothing except Base"; **G5 lane 3c, 2026-08-15**, replaced
  the count-based phrasing with the rule above, because the flat version is wrong in
  practice: `Humans.Users.Contracts` legitimately keeps one edge, to
  `Humans.Onboarding.Contracts`, which itself has zero — `Base → Users.Contracts →
  Onboarding.Contracts → ∅`. This documents what the merged code already does; it is not a
  new decision. The literal zero-reference variant is still available, but it costs a
  public-surface decision on `OnboardingResult`.)

  **The one hard invariant is on Base, not on the leaves:** `Humans.Interfaces` carries zero
  `<ProjectReference>` to any `Humans.*` project. External dependencies — NuGet or shared
  framework — are unrestricted (Peter, 2026-08-15).

  4b work item, **discharged**: the eight leaves that referenced `Humans.Domain` are now
  **zero** — `src/Humans.Domain` was deleted in lane 3b. The two leaf→leaf edges this bullet
  named for unwinding were both false alarms: `Teams.Contracts → Auth.Contracts` was a dead
  `<ProjectReference>` (zero "Auth" hits anywhere under that leaf; deleted in 3c), and
  `Email.Contracts → Events.Contracts` terminates in a zero-reference leaf. **Measured leaf→leaf
  edge set at the end of phase 3 — six, all terminating, zero cycles:**
  `Users.Contracts → Onboarding.Contracts`, `Teams.Contracts → Users.Contracts`,
  `Auth.Contracts → Users.Contracts`, `Email.Contracts → Users.Contracts`,
  `Email.Contracts → Events.Contracts`, `Governance.Contracts → Users.Contracts`.
  The 26 leaves referencing `Humans.Interfaces` are **not** an unwind: that reference becomes a
  Base reference and is legal. Dropping `IApplicationService` inheritance was needed on
  `Humans.Users.Contracts` only (lane 3b); the other 19 marker-using leaves needed nothing.

- **"Framework-free leaf" is not a real property and never was.** `Humans.Interfaces` has
  carried `<FrameworkReference Include="Microsoft.AspNetCore.App" />` since `ISection` landed,
  and `FrameworkReference` flows transitively through `ProjectReference`. Measured in lane 3c
  with `dotnet msbuild <leaf>.csproj -t:ResolvePackageAssets -getItem:FrameworkReference`: all
  26 leaves that reference Base resolve `Microsoft.AspNetCore.App` with
  `IsTransitiveFrameworkReference=true`. Only `Events.Contracts`, `Onboarding.Contracts` and
  `Users.Contracts` — the three with no path to Base — resolve `Microsoft.NETCore.App` alone.
  Several csproj comments used framework-freeness as a *placement oracle* ("the leaf cannot
  name `IActionResult`, so the type goes in the section's `Contracts/` folder"). Those
  placements all stand on other grounds — usually that the type derives from
  `HumansControllerBase` in `Humans.UI`, which no leaf may reference — and lane 3c rewrote the
  justifications in place without moving anything. Do not re-open them.
- **Base may reference `Humans.Users.Contracts` — only that leaf, for now.**
  `UserInfo` + `IUserServiceRead` live there (NOT Base). This keeps
  `HumansControllerBase`/`ApiControllerBase` (typed `IUserServiceRead` dependency,
  inherited by every section's controllers) in Base unchanged.
- Consequence: former "Base residents" that name other sections' read interfaces move to
  their sections — **`AuditViewerService` (+ viewer component) → AuditLog**,
  **`MagicLinkService` → Auth**. Their old stay-in-the-hub justification ("would sit above
  three sections") dissolves once leaf references are legal; the architecture tests
  pinning the old placements retire in the same PRs.

Two constraints on the leaf unwind (Codex P2s on peterdrier/Humans#1293, both confirmed):

- **Analyzer attributes in leaves.** 16 files across the `.Contracts` projects carry
  `[SurfaceBudget]`, `[ExternalWrite]`, or `[Grandfathered]`, whose definitions move to
  Base with `Humans.Interfaces`. **Amended 2026-08-14:** the usages are not a problem —
  leaves may reference Base, so the attributes stay visible and there is no polyfill and
  no duplication. (An earlier draft prescribed per-assembly `internal` polyfills; that
  followed from the "framework only" misreading above and is withdrawn.) What remains is
  the analyzers' side. They resolve these attributes by **hardcoded** metadata name
  (`GetTypeByMetadataName` — `SurfaceBudgetAnalyzer.SurfaceBudgetAttributeFullName`,
  `RequestScopedCancellationOnExternalWriteAnalyzer.ExternalWriteAttributeFullName`,
  `GrandfatheredCheck.AttributeFullName`, all three literals still reading
  `Humans.Application.Architecture.*`).

  **Corrected by G5 lane 3c (2026-08-15) — this bullet's urgency framing was wrong.** All 15 of
  these constants are **namespace** strings, and `GetTypeByMetadataName` resolves by namespace,
  not by assembly. **The 4b-iv rename of `Humans.Interfaces` to `Humans.Base` breaks none of
  them**, because phase 3 preserved every namespace when it filled Base: the attributes still
  live under `Humans.Application.Architecture.*` regardless of which assembly ships them. Only
  a *namespace* move would break them, and no lane is planning one.

  #1057 is still worth landing, for a different reason: **two of these analyzers fail
  silently.** HUM0015/0016 and HUM0033 `return` from `CompilationStart` when resolution yields
  null, registering no action and emitting nothing — no error, no diagnostic, green build. A
  hardcoded literal that can turn enforcement off without a symptom is the defect, independent
  of whether anything is about to move. It is **not** a 4b-iv prerequisite.
  **The constants should not exist.** Fix (nobodies-collective/Humans#1057):
  link the attribute sources into `Humans.Analyzers`
  with `<Compile Include=… Link=…/>` and derive the names via
  `typeof(GrandfatheredAttribute).FullName`, so the class is the single source of truth
  and the rename carries itself. A `ProjectReference` cannot do this — Base already
  references `Humans.Analyzers` (`src/Directory.Build.props` applies it to every `src/`
  project), so the reverse edge is a project-graph cycle; the analyzer is also
  `netstandard2.0` and must keep its load-context closure minimal. It does not gate the
  rename (see the correction above), but verify each analyzer still fires after landing it;
  silently losing enforcement is a FAIL, not a trade-off.
- **`UserInfo`'s object graph.** `UserEmailInfo` names `GoogleEmailStatus`,
  `CommunicationPreferenceInfo` names `MessageCategory`, `ProfileInfo` names
  `MembershipTier` and `ConsentCheckStatus` — four enums the inventory assigns to
  GoogleIntegration, Notifications, Governance, and Consent. A zero-reference
  `Users.Contracts` cannot compile `UserInfo` unless they come along: **those four enums
  ride into `Users.Contracts` with `UserInfo`, overriding the appendix dispositions.**
  Their consuming sections reference the leaf anyway (near-universal). Each enum moves
  home to its owning section when nobodies-collective/Humans#1044 slices its field out of
  the `UserInfo`/`ProfileInfo` graph.

  **Landed, and it was nine types, not four** (G5 lane 3b). PR #1313 had already brought
  `ParticipationStatus`, `ContactSource`, `ParticipationSource` and `LanguageProficiency`
  onto the leaf — all four are named by public leaf types (`EventParticipation`, `User`,
  `ProfileLanguage`), so they could not have lived anywhere else either. Lane 3b moved the
  four above and added a fifth item this bullet missed: `MarkdownContentAttribute`, applied
  to `Profile.MarkdownBio`. That one is a deliberate **internal twin** in the leaf rather
  than a move, because eight section projects apply the `Humans.Interfaces` original — see
  `src/Sections/Humans.Users.Contracts/MarkdownContentAttribute.cs`. `SyncAction`, the last
  enum left in `Humans.Domain`, went to `Humans.GoogleIntegration.Contracts` instead, and
  `src/Humans.Domain` was deleted.

  Three leaves gained a `→ Users.Contracts` edge as a result, all acyclic:
  `Humans.Governance.Contracts` (`MembershipTier`), `Humans.Teams.Contracts`
  (`GoogleEmailStatus`) and — not predicted anywhere, found by the compiler —
  `Humans.Email.Contracts` (`MembershipTier` + `MessageCategory`).

  **The "zero-reference" phrasing is the wrong invariant and should be retired.**
  `Users.Contracts` finished lane 3b at **one** `<ProjectReference>`, to
  `Humans.Onboarding.Contracts`, for `OnboardingResult` — which is the onboarding funnel's
  own vocabulary and cannot be pulled onto the Users leaf without forcing
  `Onboarding.Contracts` to reference back. What actually prevents the cycle is not a count:

  > A leaf may reference Base and any leaf that itself has zero `<ProjectReference>`.
  > `Humans.Users.Contracts` may reference **only** the latter, because Base references it.

  Such a chain terminates, so it cannot cycle whatever Base later references. Both terminal
  leaves — `Humans.Onboarding.Contracts` and `Humans.Events.Contracts` — now carry a csproj
  comment saying their zero-reference state is load-bearing, because nothing in the build
  will catch a violation until 4b-iii moves `HumansControllerBase` into Base.

## Humans.UI retirement (a 4b lane)

`Humans.UI` (~71 files) splits three ways — full verification in the infra/UI appendix:

- **Generic view plumbing → Base**: SharedResource + resx, `Models/Tables/*`, pager,
  tag helpers, `HumansControllerBase`/`ApiControllerBase`, TempData alerts,
  `PolicyNames`/`RoleChecks`, `ApiKeyAuthFilterBase`, generic extensions/partials.
- **Section-flavored strays → owning sections**: Human search/card components → Users;
  `ShiftsSummaryCardViewModel`, `ShiftRoleChecks`, `VolunteerBadgesViewModel` → Shifts;
  `TranslationsGalleryViewModel` → Debug; `AuditLogViewComponent` → AuditLog;
  `FavouriteButtonModel` → Events. Cross-section views invoke by name
  (`Component.InvokeAsync("…")`) — no compile-time refs.
- **Chrome → Web**: layouts, nav partials. `CityPlanningHub` also → Web (its own doc
  comment rules out both the section and Base today; an `ISection` endpoint-mapping seam
  is built only if a second hub ever appears).

Breaking `Humans.UI → Humans.Application` is on the critical path for deleting Application.

## Contracts fold-back — G6, not during G5

The 24 `.Contracts` projects are mostly hub artifacts: Application references 22 of them,
each carve forced by a `Section → Application → Section` cycle, not a real knot. After the
hub dies, fold back any leaf with no remaining external consumer into its section's
`Contracts/` folder. Orphan candidates measured 2026-08-14: Camps, Shifts, Expenses, Gate,
MailerLite, Surveys, Agent, Monitor. Real knots/horizontals keep their leaf. Recorded on
nobodies-collective/Humans#1010.

## Enforcement stance

HUM0034 (the #1013 keystone: public types in section assemblies) is shipped at error
severity and stands **as-is**. Beyond it: **no new analyzers, no ratchets, no baselines.**
Public surface is measured and judged, not policed.

## 4a placement decisions (Peter, 2026-08-14)

| What | Where | Note |
|---|---|---|
| Stripe (`StripeService`, smoke service, `IStripeService`) | **own section `Humans.Stripe`** | vendor-connector shape (TicketTailor/Holded precedent); Store/Finance/Tickets consume its contracts |
| `UserInfo`, `IUserServiceRead`, `UserStateClassifier` | **`Humans.Users.Contracts`** | lane 2, in flight |
| Dashboard cluster (+`UpcomingShiftEntry`) | **Web (chrome)** | no Dashboard section; sections expose their own dashboard panels (view components); DTO dissolves into Shifts' panel |
| `ICalFeedService` + contributor interfaces | **`Humans.Calendar`** | contributor interface in Calendar.Contracts so Shifts/Events implement it |
| `HoldedSyncJob`, Holded client + DTOs | **Holded** | references Finance.Contracts; leaf refs, no cycle |
| `HumanLifecycleService`, `SuspendNonCompliantMembersJob` | **Users** | membership machinery is Users, NOT Governance — Governance is for governance only (votes, membership decisions) |
| `SystemTeamSyncJob` / `ISystemTeamSync` | **Teams** (probable) | verify at lane-scope time |
| EarlyEntry (service/provider/invalidator + caching decorator) | **own section `Humans.EarlyEntry`** | an orchestrator in its own land: sources from Teams/Camps/…, Gate queries it; small is fine |
| `AdminDatabaseDiagnostics`, `HumansMetricsService`, `SystemDbContext` + `Migrations/System` | **Web** | platform-wide operational/host services |
| `MemoryCacheInvalidators.cs` | **split** ✅ 3a-1 | 6 classes → Base; `ActiveTeamsCacheInvalidator` → Teams (internal, per HUM0034 — its DI registration moved from Web to Teams' `Section.cs`); the *interface* `IActiveTeamsCacheInvalidator` → **`Humans.Teams.Contracts`**, not the section, because `Humans.Users` injects it |
| `InfrastructureServiceCollectionExtensions.cs` | **split** | `AddSectionDbContext<T>` → Base; UsersDbContext/SystemDbContext-naming members → Web roll-call |
| `ITicketVendorService` + DTOs | **Tickets** | TicketTailor references Tickets directly — no new leaf |
| ~110 remaining section-owned files | per appendix tables | entities/enums/DTOs to owners; one recurring job per section |
| 60 generic files | **Base** — 60 landed by 3a-1, EF/hosting cluster still owed by 3a-2 | Measured, not estimated. 34 from `Humans.Application` (ConfigurationRegistry cluster + `GitHubSettings` + `CacheKeys`), 16 from `Humans.Domain` (`SystemTeamType`, `YesNoMaybe`, `GoogleSyncSource`, `EmailOutboxStatus`, the constants, the `Iban*`/`EmailNormalization`/`PlatformDetector` helpers, `SectionAttribute`, `MarkdownContentAttribute`, `ExpiresOnAttribute`), 10 from `Humans.Infrastructure` (GitHub content connectors, Serilog `Logging/*`, `MetersService`, `HtmlPlainTextConverter`, the settings pair, `MemoryCacheInvalidators`). Every namespace preserved. `AuditAction`, `DietaryOptions`, `TicketConstants`, `ProfileCompletion`, `ProfilePictureStorageKeys` were on earlier drafts of this row and had already gone to section leaves by #1310/#1313. |

Zero dead files found — the hub is thin but fully load-bearing.

**Post-G5 follow-ups filed:** `Profile.MembershipTier` → Governance as the first slice of
the `IUserPiece` profile decomposition (nobodies-collective/Humans#1044 — sections
contribute user pieces via DI fan-out, mirroring `IUserDataContributor : IFanout`).
