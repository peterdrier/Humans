# Section Align — Notifications

**Run started:** 2026-05-14 | **Mode:** Existing-section (fresh align branch) | **Worktree:** `.worktrees/section-align-notifications`
**Branch:** `align/notifications` (off `origin/main` @ `3f3c36891`)
**Canonical section name proposal:** **Notifications** (plural — already used by Application/Infrastructure folders + namespaces + route prefix + section doc; only Web-layer controller and views folder are singular)

The Notifications section is Status (A) Migrated (per `docs/sections/Notifications.md`, PR for `nobodies-collective/Humans#550`, 2026-04-22) and the alignment posture reflects that — the inventory found a small drift set, no blocking violations.

---

## Axis 1 — boundary integrity

1. **Name consistency.** Two singular outliers in Web layer (everything else plural):
   - `src/Humans.Web/Controllers/NotificationController.cs` — rename to `NotificationsController.cs`
   - `src/Humans.Web/Views/Notification/` — rename to `Views/Notifications/`
   - Plural everywhere else: `Humans.Application.Interfaces.Notifications`, `Humans.Application.Services.Notifications`, `Humans.Infrastructure.Repositories.Notifications`, `Humans.Infrastructure.Data.Configurations.Notifications`, `src/Humans.Web/Models/NotificationViewModels.cs`, `src/Humans.Web/Extensions/Sections/NotificationsSectionExtensions.cs`, `docs/sections/Notifications.md`. Route attribute on `NotificationController` is already `[Route("Notifications")]` — rename is purely C#/folder, no URL change.
2. **Controller existence.** `NotificationController.cs` is the sole controller for the section; no notification routes hosted on foreign controllers. Phase 1 rename target above.
3. **URL surface — clean.** All routes under `/Notifications/*` (`NotificationController.cs:12-162`). No `/Admin/Notification*` aliases, no API controllers serving the section. POST routes all `[ValidateAntiForgeryToken]`.
4. **Views folder.** Singular folder rename in Phase 1. Contents (`Index.cshtml`, `_NotificationPopup.cshtml`, `_NotificationRow.cshtml`) move as-is. ViewComponent partial at `Views/Shared/Components/NotificationBell/Default.cshtml` stays put.
5. **ViewModel placement — clean.** All section VMs in `src/Humans.Web/Models/NotificationViewModels.cs` (single file). No grab-bag drift. *Filename will rename to `NotificationsViewModels.cs` in Phase 1 to match the plural normalization.*
6. **Controller-base leak — clean.** `HumansControllerBase.cs` has no notification-specific helpers, VM returns, or parameters.
7. **Extensions placement — clean.** `src/Humans.Web/Extensions/Sections/NotificationsSectionExtensions.cs` already in the right place.
8. **Role surface — clean.** Meter role-gating uses base roles (`Admin`, `Board`, `ConsentCoordinator`, `VolunteerCoordinator`, `CampLead`); no notification-specific role names that would need `*Admin` normalization.
9. **Inbound cross-section DB access — clean.** Grep `_db.Notifications` / `_db.NotificationRecipients` across `src/Humans.Application/` and `src/Humans.Web/` and `src/Humans.Infrastructure/Repositories/` (excluding the section's own repo) returns zero hits. `INotificationRepository` is the sole writer/reader.
10. **Inbound EF navs — clean.** No `public Notification` / `public ICollection<NotificationRecipient>` properties on other-section entities. The only navs are owned-side: `Notification.Recipients`, `NotificationRecipient.Notification`, `NotificationRecipient.User` (FK to identity, intentionally declared but never `Include`d per design-rules §6).
11. **Outbound cross-section access — clean.** `NotificationRepository.cs` `.Include` calls are all intra-section (`n.Recipients`, `nr.Notification`). `NotificationInboxService.cs:27` explicitly comments "we deliberately do NOT `.Include` cross-domain navs" — display data stitched via `IUserService.GetByIdsAsync`. Meter reads route through other sections' public service interfaces (`IProfileService`, `IUserService`, `IGoogleSyncService`, `ITeamService`, `ITicketSyncService`, `IApplicationDecisionService`, `ICampService`).
12. **Controller → DbContext — clean.** `NotificationController.cs` injects only `INotificationInboxService` and `INotificationMeterProvider`. Zero `HumansDbContext` / `IDbContextFactory` references.
13. **Migrations — clean.** `20260401124451_AddNotificationInbox.cs` is plain EF-generated `CreateTable` + `CreateIndex`. No hand-edited SQL, no `migrationBuilder.Sql(...)`, no comments.
14. **Section invariant doc shape — complete.** All required `SECTION-TEMPLATE.md` headings present and substantive (Concepts, Data Model, Actors & Roles, Invariants, Negative Access Rules, Triggers, Cross-Section Dependencies, Architecture incl. Routes + Touch-and-clean). Status block names owning services and tables. Phase 4 may add a small Routes-table update if controller rename surfaces any URL drift (it should not).
15. **Prior review items.** None — fresh align branch.

---

## Axis 2 — internal cohesion

1. **EF leakage from service layer — clean.** No `using Microsoft.EntityFrameworkCore` / `IQueryable` / `DbContext` / `DbSet` in any `Services/Notifications/` file. Architecture tests pin this: `NotificationsArchitectureTests.cs:38, 83, 114` (`NotificationService_HasNoDbContextConstructorParameter` and two siblings).
2. **Caching placement — clean.** All `IMemoryCache` usage in service layer:
   - `NotificationService:41`, `NotificationInboxService:36`, `NotificationMeterProvider:54`, `NotificationEmitter:33`
   - `NotificationBellViewComponent:13` (per-user badge cache — design-rules §15 allows short-TTL ViewComponent caching for badge counts; flagged in the section doc lines 108, 142 as the documented exception). The skill's `feedback_viewcomponent_no_cache` rule applies to general ViewComponents; this one is the canonical badge-count case so it stays, but Phase 0 surfaces it for confirmation.
   - Repository: zero hits. Controller: zero hits.
   - No `CachingNotificationService` decorator — intentional per section doc line 142.
3. **DI lifetimes — clean.** `NotificationsSectionExtensions.cs:21-39`: repo Singleton with `IDbContextFactory`, all services Scoped, **DI-cycle break** verified — `NotificationEmitter` and `NotificationService` registered as separate concretes so `ITeamService`/`IRoleAssignmentService` can inject the narrower `INotificationEmitter` without closing the cycle. `IUserDataContributor` re-exported from `NotificationInboxService` (GDPR contributor).
4. **Repository pattern — clean.** `NotificationRepository`: `sealed`, `IDbContextFactory<HumansDbContext>`, lives at `Infrastructure/Repositories/Notifications/`. Updates + deletes present (correct — notifications are NOT append-only, they get resolved/dismissed/marked-read/cleaned-up); deletes bounded to `DeleteResolvedOlderThanAsync` and `DeleteUnresolvedInformationalOlderThanAsync`.
5. **Shared visual components — clean and minimal.** Single ViewComponent (`NotificationBellViewComponent`) invoked from `_AdminLayout.cshtml:55`, `_Layout.cshtml:127`, `WidgetGallery/Index.cshtml:699`. Two partials inside the section (`_NotificationPopup`, `_NotificationRow`) — intra-section only, correct as partials.
6. **Redundancy vs system-level shared components — clean.** `_NotificationRow.cshtml:50-59` renders initials-only avatar clusters for group-targeted rows (compact, intentional). No inline `@user.DisplayName` + avatar reinventions. No role badge hand-rolls. Recipient display data stitched via `IUserService.GetByIdsAsync`, not by re-querying. Phase 3 has nothing to swap here.
7. **Interface budget + segregation.** Method counts per interface (read from `src/Humans.Application/Interfaces/Notifications/`):

   | Interface | Methods | `InterfaceMethodBudgetTests` entry? | Status |
   |---|---|---|---|
   | `INotificationEmitter` | 1 | n/a (under threshold) | ✓ |
   | `INotificationService` | 3 | n/a | ✓ |
   | `INotificationRecipientResolver` | 2 | n/a | ✓ |
   | `INotificationMeterProvider` | 1 | n/a | ✓ |
   | `INotificationInboxService` | ~11 | **missing** | Phase 2 add baseline entry |
   | `INotificationRepository` | ~17 | **missing** | Phase 2 add baseline entry |

   Confirm the exact counts and add `InterfaceMethodBudgetTests.Budgets` entries for the two ≥10 interfaces in Phase 2. No status-split anti-pattern. No bag-of-flags. No UI-plumbing-leaking-through-main-service. No Service/InboxService read-shape overlap (Service is outbound dispatch; InboxService is inbound reads — clean separation).

7. **Architecture test coverage.** `NotificationsArchitectureTests.cs` covers DbContext-parameter absence and basic dependency-shape checks. **Gap (Phase 2 add):** single-writer rule for `Notifications` and `NotificationRecipients` DbSets. The codebase has the `NoCrossSectionEfJoins.baseline.txt` mechanism but no explicit "only `NotificationRepository` references these DbSets" rule. Add one — reflection or Roslyn scan over `src/` that fails if any class outside `Infrastructure/Repositories/Notifications/` references `_db.Notifications` or `_db.NotificationRecipients`. **Phase 3 candidate:** lines 30-35, 76-79, 107-110 of the file are per-section namespace/sealing checks that should be folded into a generic reflection test in `tests/Humans.Application.Tests/Architecture/Rules/` if one doesn't already cover them — surface but don't auto-prune; check what already exists.

---

## Axis 3 — test focus

1. **Test folder placement.** Section tests scattered across four sibling folders. Canonical home: `tests/Humans.Application.Tests/Notifications/`. Moves planned for Phase 1:

   | Current | Canonical |
   |---|---|
   | `Services/NotificationServiceTests.cs` | `Notifications/NotificationServiceTests.cs` |
   | `Services/NotificationInboxServiceTests.cs` | `Notifications/NotificationInboxServiceTests.cs` |
   | `Services/NotificationMeterProviderTests.cs` | `Notifications/NotificationMeterProviderTests.cs` |
   | `Repositories/NotificationRepositoryTests.cs` | `Notifications/NotificationRepositoryTests.cs` |
   | `Jobs/CleanupNotificationsJobTests.cs` | `Notifications/CleanupNotificationsJobTests.cs` |

   Stays in place (architecture is a separate canonical category): `Architecture/NotificationsArchitectureTests.cs`.

   Stays under `GoogleIntegration/` (SUT is `GoogleRemovalNotificationService` / `GoogleGroupSyncService`, not the notification surface):
   - `GoogleIntegration/GoogleRemovalNotificationServiceTests.cs`
   - `GoogleIntegration/GoogleSyncRemovalNotificationIntegrationTests.cs`

   Inventory verified via SUT inspection of the Google files' first 40 lines.

1a. **1-to-1 production-class ↔ test-file rule.**

   | Production class | Test file | Status |
   |---|---|---|
   | `NotificationService` | `NotificationServiceTests.cs` | ✓ |
   | `NotificationInboxService` | `NotificationInboxServiceTests.cs` | ✓ |
   | `NotificationMeterProvider` | `NotificationMeterProviderTests.cs` | ✓ |
   | `NotificationRepository` | `NotificationRepositoryTests.cs` | ✓ |
   | `CleanupNotificationsJob` | `CleanupNotificationsJobTests.cs` | ✓ |
   | `NotificationEmitter` | — | **missing** (currently tested indirectly via `NotificationServiceTests`) |
   | `NotificationRecipientResolver` | — | **missing** (currently tested via mock from `NotificationServiceTests`) |
   | `NotificationBellViewComponent` | — | **missing** (Web layer) |
   | `NotificationController` | — | **missing** (Web layer — would live in `Humans.Web.Tests`) |

   Decisions for Phase 2:
   - `NotificationEmitter` — has its own production class (separate from `NotificationService` for DI cycle break). 1-to-1 rule requires a dedicated test file. Add `NotificationEmitterTests.cs` exercising `SendAsync` directly with the documented invariants (empty list → warn + skip; InboxEnabled suppression for Informational; Actionable bypasses suppression).
   - `NotificationRecipientResolver` — has its own production class. Add `NotificationRecipientResolverTests.cs` (Team and Role resolution paths, delegating to mocked `ITeamService` / `IRoleAssignmentService`).
   - `NotificationBellViewComponent` — defer. ViewComponent has thin logic (cache get-or-compute + return badge count); arch test pins shape. Note as "not added in this pass; covered by architecture test only" in section doc Phase 4 update.
   - `NotificationController` — `Humans.Web.Tests` doesn't typically host controller unit tests for thin controllers in this repo (verified empty). Defer; the controller is a pass-through to `INotificationInboxService` and the controller-shape conventions are pinned at the framework level.

2. **Coverage map vs section doc — strong, one gap.**

   | Section-doc bullet | Test? |
   |---|---|
   | Resolution is shared across recipients | `NotificationInboxServiceTests.cs:87-98` ✓ |
   | Empty recipient list → log Warning + skip | `NotificationServiceTests.cs:124-135` ✓ |
   | Informational suppressed by `InboxEnabled` | `NotificationServiceTests.cs:138-162` ✓ |
   | Actionable bypasses suppression | `NotificationServiceTests.cs:165-189` ✓ |
   | Actionable cannot be dismissed | `NotificationInboxServiceTests.cs:150-158`; `NotificationRepositoryTests.cs:107-117` ✓ |
   | Non-recipients forbidden (resolve/dismiss/mark-read) | `NotificationRepositoryTests.cs:76-87`; `NotificationInboxServiceTests.cs:110-118` ✓ |
   | Meters never query notifications table | covered structurally — `NotificationMeterProviderTests.cs` mocks every cross-section dep, no `INotificationRepository` injection ✓ |
   | `SendToTeamAsync` / `SendToRoleAsync` recipient resolution | `NotificationServiceTests.cs:192-246` ✓ |
   | Badge cache invalidation after writes | `NotificationServiceTests.cs:249-273`; `NotificationInboxServiceTests.cs:326-360` ✓ |
   | Cleanup: resolved >7d, unresolved informational >30d, actionable never | `CleanupNotificationsJobTests.cs:48-149` ✓ |
   | **`ReassignRecipientsToUserAsync` re-FKs recipient rows on account merge** | **MISSING** |

   Phase 2 add: a `NotificationServiceTests.ReassignRecipientsToUserAsync_*` group covering (a) basic re-FK from source → target, (b) duplicate-collapsing when target already has a recipient row for the same notification, (c) `Notification.ResolvedByUserId` also re-FK'd. This trigger is called from `AccountMergeService` so it's load-bearing for merge correctness.

3. **Redundancy flags — minimal.** Tests follow one-scenario-per-test discipline. The `BulkResolveAsync` / `BulkDismissAsync` mirror pair in `NotificationInboxServiceTests.cs:217-244` and `:248-276` is symmetry by design (Actionable vs Informational class filter); justified. `NotificationRepositoryTests.cs:150-170` and `:172-188` are two cleanup-cutoff tests with different cutoffs (7d vs 30d) — justified. No Phase 3 prune candidates.

4. **Test-to-section ratio.** ~1,612 production LOC : ~1,897 test LOC ≈ **1.18:1**. Appropriate for a fan-in section with recipient-resolution, preference-suppression, cache-invalidation, and cleanup-job concerns.

5. **Brittleness signals — clean.** Tests use `FakeClock` consistently. No private-state reflection. No test-order dependencies. Two tests have explicit `[HumansFact(Timeout=10000)]` on bulk operations — appropriate. No external service calls (Google tests mock all clients).

6. **Mutation signal.** No recent Stryker report under `local/stryker-runs/notifications/`. Decision: don't auto-run; the test suite is strong enough on invariant-coverage grounds. If Phase 3 finds prune candidates that need confidence, run then.

---

## Test-attribute gate (per `docs/testing/mutation-testing.md`)

- Baseline as of last gate update: (consult `docs/testing/mutation-testing.md` at Phase 2 commit time)
- This run's net delta: estimated **+5 / -0 = +5** (`NotificationEmitterTests.cs`, `NotificationRecipientResolverTests.cs`, three `ReassignRecipientsToUserAsync` tests). All five are new behavior coverage for documented invariants/triggers — justification: closing a 1-to-1 gap + a Triggers-section gap surfaced by Axis 3.
- No deletions planned in Phase 3 for this section.

---

## Stop conditions tripped

**None.** All Phase 0 stop conditions clear:
- No canonical-name collision (Notifications plural is already the dominant convention; Web layer is the outlier).
- No cross-section DB fix needed.
- No ambiguous table ownership.
- Section doc complete, no major template gaps.
- Branch clean.
- No outbound API gap (no consumer-side fixes needed — Notifications is producer-side for emit and consumer for meter reads, all already routed through public service interfaces).

---

## Follow-up /section-align targets

**None surfaced.** The cross-section coupling is all to-Notifications-via-public-interface (emit calls from Camps, Issues, Profiles, etc., and meter reads via `IProfileService`/`ICampService`/etc.) — there are no consumer-side cross-section reads where this section reaches into another section's tables, and no producer-side gaps where another section needs an API we don't expose.

---

## Phase plan

### Phase 1 — Surface alignment (Sonnet, mechanical)

The only Axis 1 mechanical work is the singular→plural rename of the Web-layer surface. **Run the full inbound-link sweep before committing each rename** (the skill calls this out specifically — green build ≠ correct rename; `asp-controller="Notification"` strings won't fail CI but will break navigation).

1. **Rename controller class + file:**
   - `git mv src/Humans.Web/Controllers/NotificationController.cs src/Humans.Web/Controllers/NotificationsController.cs`
   - `/reforge` impact for class rename `NotificationController` → `NotificationsController`.
   - Bulk Edit: class name, ctor name, any internal type refs.
   - Route attribute `[Route("Notifications")]` is already plural — no URL change.
   - Inbound-link sweep (whole-word match — `\bNotification\b` would false-positive against e.g. `Notifications`):
     - `git grep -nE 'asp-controller="Notification"' src/` → expect zero, fix any
     - `git grep -nE 'Url\.(Action|RouteUrl|Page)\([^)]*"Notification"' src/`
     - `git grep -nE 'RedirectToAction\([^)]*"Notification"' src/`
     - `git grep -nE '"Notification"' src/Humans.Web/Authorization/ src/Humans.Web/Middleware/ src/Humans.Web/Filters/ src/Humans.Web/ViewComponents/`
     - `git grep -nE '"/Notification/' tests/`
   - Build + test green.

2. **Rename views folder:**
   - `git mv src/Humans.Web/Views/Notification src/Humans.Web/Views/Notifications`
   - Verify view discovery still works (controller name plural now matches folder name plural by convention).
   - Build + test green.

3. **Rename ViewModels file (optional small follow-up for consistency):**
   - `git mv src/Humans.Web/Models/NotificationViewModels.cs src/Humans.Web/Models/NotificationsViewModels.cs`
   - `/reforge` impact for any direct `NotificationViewModels` file reference (there shouldn't be any — files referenced by class, not filename).
   - Skip if Phase 1 budget is tight; the file itself is fine and this is purely cosmetic.

4. **Move section tests to canonical folder:**
   - `git mv tests/Humans.Application.Tests/Services/NotificationServiceTests.cs tests/Humans.Application.Tests/Notifications/NotificationServiceTests.cs` (create dir as needed)
   - Repeat for `NotificationInboxServiceTests.cs`, `NotificationMeterProviderTests.cs` from `Services/`, `NotificationRepositoryTests.cs` from `Repositories/`, `CleanupNotificationsJobTests.cs` from `Jobs/`.
   - Update namespace declarations to `Humans.Application.Tests.Notifications` to match new folder.
   - Build + test green.

5. **Commit + push.** Push at end of Phase 1 for bot review.

### Phase 2 — Fix arch violations + add missing coverage (Sonnet)

1. **Add single-writer architecture test.** New test in `NotificationsArchitectureTests.cs` (or extend `NoCrossSectionEfJoins` baseline): assert that the only file referencing `_db.Notifications` or `_db.NotificationRecipients` is under `src/Humans.Infrastructure/Repositories/Notifications/`. Use Roslyn or reflection-scan pattern consistent with how AuditLog pins its single-writer rule.
2. **Add interface budget entries.** Confirm exact method counts on `INotificationInboxService` and `INotificationRepository`. Add `InterfaceMethodBudgetTests.Budgets` entries with current counts so the ratchet pins them.
3. **Add `NotificationEmitterTests.cs`.** Cover: empty recipient list → log Warning + no DB write; Informational with `InboxEnabled=false` for all recipients → log Information + skip; Actionable bypasses suppression; happy path writes one `Notification` row + N `NotificationRecipient` rows.
4. **Add `NotificationRecipientResolverTests.cs`.** Cover: `GetActiveUserIdsForRoleAsync` delegates to `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` (mock); `GetTeamNotificationInfoAsync` delegates to `ITeamService` (mock); both surfaces handle null/empty correctly.
5. **Add `ReassignRecipientsToUserAsync` coverage** to `NotificationServiceTests.cs`: (a) source → target re-FK with no collisions, (b) target already has a recipient row for the same notification → source row deleted (collapse), (c) `Notification.ResolvedByUserId` also re-FK'd from source → target.
6. **Build + test green. Commit + push for bot review.**

### Phase 3 — `/simplify` pass (Opus, ~3 fixes for ~1,600 LOC section)

Light scope per `feedback_simplify_scope_to_section_size`. Candidate targets:

1. Check whether `NotificationsArchitectureTests.cs:30-35, 76-79, 107-110` namespace/sealing checks are already covered by a generic reflection test elsewhere in `tests/Humans.Application.Tests/Architecture/Rules/`. If yes, delete the section-specific copies. If no, leave them (section-specific arch tests are not drift in themselves — only duplicates of generic patterns are).
2. Audit `NotificationMeterProvider` for any cross-section read that could now use a tighter API (currently uses `IUserService.GetAllUsersAsync` for the pending-deletion meter and derives count in-memory — section doc justifies this at ~500-user scale, so likely keep).
3. Confirm `NotificationBellViewComponent`'s short-TTL `IMemoryCache` is documented as the canonical badge-cache exception in `feedback_viewcomponent_no_cache`. If the rule needs an explicit carve-out, add it. If not, leave.

### Phase 4 — Doc polish (Opus)

1. Update `docs/sections/Notifications.md` Routes table verbatim (no URL changes expected, but confirm controller name reference matches new plural).
2. Update `docs/architecture/dependency-graph.md` — no edge changes expected, but verify the section's inbound/outbound edges still match reality after the Web rename.
3. Update `todos.md` if any item references the singular Notification controller/views paths.
4. `/freshness-sweep` check — the freshness triggers in `docs/sections/Notifications.md:1-11` reference the renamed paths; update those triggers to point at `src/Humans.Web/Controllers/NotificationsController.cs` and add `src/Humans.Web/Views/Notifications/**` if useful.
5. Closing re-run of `/section-align notifications` from the same worktree as the audit gate.
