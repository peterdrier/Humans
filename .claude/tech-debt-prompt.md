# Tech Debt Reduction — Claude Autonomous Prompt

## Mission

You are autonomously improving a ~70k-line ASP.NET Core 10 Clean Architecture codebase (membership management for a Spanish nonprofit). It grew quickly and patterns have diverged. Your job is to **find and fix the most impactful tech debt** — duplicated logic, inconsistent patterns, misplaced responsibilities, and unnecessary complexity.

**You decide what to work on.** Scan the codebase, identify the highest-value improvements, fix them, verify the build passes, commit, and move on. Use your judgement about what matters most.

**Work autonomously.** Do not stop for milestone reports. Find the next opportunity, fix it, verify the build passes, commit, push the branch to `origin`, and move to the next one.

---

## PRIORITY FOCUS: §15 Service-Ownership Migration

The codebase is mid-migration to the **§15 Profile Section Pattern** — every service owns its data behind a repository, no cross-service DB access, cross-section reads go through owning-service interfaces. This is the highest-value tech debt in the project right now. **Before picking generic cleanups, check whether a §15 lead is unfinished.**

**Read these first** (they are authoritative and current):
- `docs/architecture/design-rules.md` §2 (Service Ownership), §6 (Nav-Strip), §15 (full pattern), §15i (known current violations list)
- `docs/architecture/tech-debt-2026-04-23.md` — prioritized backlog of §15 leads with file:line citations
- `docs/architecture/service-data-access-map.md` — per-section ownership map

**Highest-value §15 leads (as of 2026-04-23 — verify against current code before acting):**

1. **Repository pattern inconsistency.** 6 repositories still use the old Scoped + `HumansDbContext` shape; 20 use the §15 Singleton + `IDbContextFactory` shape. Flip the stragglers (BudgetRepository, CampaignRepository, FeedbackRepository, RoleAssignmentRepository, ShiftSignupRepository — and leave ApplicationRepository alone if #533's no-decorator decision still stands).
2. **Jobs injecting `HumansDbContext` directly (§2c violations).** `ProcessAccountDeletionsJob`, `ProcessEmailOutboxJob`, `ProcessGoogleSyncOutboxJob`, `SendAdminDailyDigestJob`, `SendBoardDailyDigestJob`, `SendReConsentReminderJob`, `SuspendNonCompliantMembersJob`. Route reads/writes through owning-service interfaces.
3. **Cross-domain nav reads missing `#pragma warning disable CS0618`.** `GoogleWorkspaceSyncService`, `SystemTeamSyncJob`, residual sites in `TeamService`. Either add the pragma + migrate off the nav via an in-memory join, or wrap with the pragma if the nav is still declared.
4. **CalendarService not in the §15i migration list.** `src/Humans.Infrastructure/Services/CalendarService.cs` still lives in Infrastructure and has a cross-domain `.Include(e => e.OwningTeam)`. Needs the full migration (repository + Application-layer move + `ITeamService` stitch for owning-team display).
5. **§15i documentation drift.** `design-rules.md` has stale counts ("~25 services", "14 repositories") — the real numbers are 4 business services and 26 repos. Update if you touch adjacent text.
6. **Cross-domain navigation properties on User.** `User.Profile`, `User.UserEmails`, `User.TeamMemberships`, `User.RoleAssignments`, `User.Applications`, `User.ConsentRecords`, `User.CommunicationPreferences`, `User.GetEffectiveEmail()` still read freely from ~15 call sites (TeamService, GoogleWorkspaceSyncService, SendBoardDailyDigestJob, SyncLegalDocumentsJob, SystemTeamSyncJob, SuspendNonCompliantMembersJob, ProfileController). Small migration increments welcome.

**§15 working rules:**
- **One section per PR/commit.** Never half-migrate a section. If you extract a repository, finish the full stack (service + repo + cross-service interface changes) in the same commit.
- **Cross-section reads** go through `IOtherService.GetXAsync` methods — never `Include(otherSection)` across domain boundaries. Use §6b "in-memory join" (batched `GetByIdsAsync` + `ToDictionary`) to stitch display fields.
- **Cross-domain navs** get `[Obsolete]` marking per §6c; remaining read sites wrap with `#pragma warning disable CS0618` until the nav-strip follow-up.
- **New services from day one** follow §15 — repository the same day, not "migrate later."

---

## Architecture (4 layers)

```
src/Humans.Domain/          — Entities, enums, value objects (pure, no dependencies)
src/Humans.Application/     — Interfaces, DTOs, constants (business rules)
src/Humans.Infrastructure/  — EF Core, external services, Hangfire jobs
src/Humans.Web/             — Controllers, Razor views, authorization, tag helpers
tests/Humans.Application.Tests/ — Unit tests
```

Key entry points:
- `src/Humans.Web/Program.cs` — DI, middleware, startup
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — service registration
- `src/Humans.Infrastructure/Extensions/RecurringJobExtensions.cs` — Hangfire job setup

---

## EXCLUSION ZONES — DO NOT TOUCH

These files/directories are **completely off-limits**. Changes here risk data loss, broken migrations, or silent runtime failures:

```
src/Humans.Infrastructure/Data/HumansDbContext.cs
src/Humans.Infrastructure/Data/EntityConfigurations/**
src/Humans.Infrastructure/Migrations/**
```

Also do not modify:
- **Entity classes** in `src/Humans.Domain/Entities/` — do not rename properties, remove properties, change types, or restructure. Properties that appear unused are accessed via reflection (serialization, change tracking, cloning). Removing them silently breaks functionality.
- **Any `[JsonPropertyName]`, `[JsonInclude]`, `[JsonConstructor]`, `[JsonPolymorphic]`, or `[JsonDerivedType]` attributes** — these control serialization of persisted data.
- **Migration files** — never edit, never create manually.
- **ConsentRecord** — append-only table with database triggers preventing UPDATE/DELETE.
- **Test files** in `tests/` — don't modify existing tests. You may add new tests if useful but don't change existing ones.
- **Known-load-bearing lazy `IServiceProvider` resolutions.** `TeamService` uses lazy `IServiceProvider` resolution for `IEmailService` specifically to break the `UserService → TeamService → EmailService → UserEmailService → UserService` cycle. Do NOT "clean this up" — it goes away when `AccountMergeService` migrates to `Humans.Application.Services.Profile`. Check for a comment explaining why before removing any lazy-resolve pattern.
- **`IAuthorizationService` in service constructors.** Injecting `IAuthorizationService` pulls every authorization handler's transitive dependencies and can create construction cycles that only surface at startup. Services stay auth-free (§design-rules §2); authorization happens in controllers.

---

## CODING RULES (must follow during all changes)

1. **NodaTime for all dates/times** — Use `Instant`, `LocalDate`, `ZonedDateTime`. Never `DateTime`, `DateTimeOffset`, or `DateOnly`.
2. **Explicit StringComparison** — Every `.Equals()`, `.Contains()`, `.StartsWith()`, `.EndsWith()`, `.IndexOf()`, `.Replace()` on strings must specify `StringComparison.Ordinal` or `StringComparison.OrdinalIgnoreCase`.
3. **No enum comparison operators in LINQ-to-SQL** — Enums are stored as strings. `>`, `<`, `>=`, `<=` produce wrong results. Use `.Contains()` with explicit value lists.
4. **Magic string avoidance** — Use `nameof()` for action/controller names, `RoleNames.*` constants for role checks, constants or enums for repeated strings.
5. **Font Awesome 6 only** — `fa-solid fa-*` syntax. Bootstrap Icons (`bi bi-*`) are NOT loaded.
6. **UI terminology** — User-facing text says "humans" not "members"/"volunteers"/"users". The word "humans" stays in English across all locales. Use "birthday" not "date of birth".
7. **No concurrency tokens** — Do not add `IsConcurrencyToken()`, `[ConcurrencyCheck]`, or row versioning anywhere.
8. **Every new page needs a nav link** — If you create a controller action returning a view, add a navigation link to it.
9. **Admin pages don't need localization** — Views under `/Admin/*` and `/TeamAdmin/*` don't need `@Localizer[]` calls.

---

## Build Command

```
dotnet build Humans.slnx -v q && dotnet test Humans.slnx -v q --filter "FullyQualifiedName~Application"
```

---

## What "good" looks like

- **One way to do each thing.** If caching, error handling, or authorization uses 3 different patterns, pick the best one and consolidate.
- **Controllers are thin.** A controller action should: validate input, call a service, map the result, return a view. Business logic (LINQ transforms, multi-step mutations, domain decisions) belongs in services.
- **Services own their domain.** A TeamService should only contain team logic. If it has methods that query role assignments, budget data, or shift schedules, those methods belong elsewhere.
- **Large files are fine if cohesive.** A 2000-line service is not a problem if every method relates to the same domain. Do NOT split files just to reduce line count.
- **Shared patterns use shared code.** If the same 5-line pattern appears in 10 controllers, extract it. If a ViewComponent exists but views use inline HTML instead, fix the views.
- **Constants over magic strings.** Role names, cache keys, action names — all should reference constants or use `nameof()`.

## What to look for

**First, check the §15 priority section above.** If there's a specific §15 lead you can knock down, do that — it's higher-value than generic cleanup. Also check `docs/architecture/tech-debt-2026-04-23.md` for the current backlog with file:line citations; don't re-discover what's already catalogued there.

Otherwise use your judgement. Scan the codebase and prioritize by impact. Here are the *kinds* of smells worth finding — but don't limit yourself to these:

### Pattern divergence
When the same thing is done multiple ways across the codebase. Examples: caching (GetOrCreateAsync vs TryGetValue+Set vs Get+Set), error handling (TempData vs ModelState vs nothing), authorization (attributes vs inline checks vs service calls). Pick the best pattern and consolidate.

### Misplaced responsibilities
Methods or logic in the wrong layer or the wrong service. A controller doing business logic. A service containing methods that belong to a different domain. Code that queries across domain boundaries when it should delegate.

### Duplicated logic
The same LINQ query, the same validation check, the same mapping logic appearing in multiple places. Extract to a shared method in the appropriate service or helper.

### Inconsistent conventions
Naming (`GetXAsync` vs `FetchXAsync` vs `LoadXAsync`), method signatures, parameter ordering, return types. When you find inconsistency, standardize on the dominant convention.

### Unused abstraction or missing abstraction
A ViewComponent that exists but isn't used (views use inline HTML instead). A pattern that's repeated 10 times but has no shared helper. An interface with a single implementation that adds no value.

---

## SAFETY CHECKS

Before every change, verify:

1. **Am I touching an EF entity, migration, or DbContext configuration?** → STOP, skip this change.
2. **Am I renaming a property on a class that might be serialized to JSON?** → STOP, skip this change.
3. **Am I removing a property that looks unused?** → STOP, it's likely used via reflection.
4. **Am I changing a method signature on an interface in `Application/`?** → Check all implementations AND all callers.
5. **Am I changing authorization?** → Verify the attribute/policy matches the original access level exactly.
6. **Am I removing a lazy `IServiceProvider` resolution?** → Check for a comment explaining why. If it exists to break a ctor cycle (common in §15 migrations), leave it alone.
7. **Am I splitting a large "god class" service?** → STOP. Large cohesive services are preferred over fragmented ones here; splitting breaks caching and cross-method state. Do NOT split services to reduce line count.
8. **Am I about to half-migrate a §15 section?** → STOP. If you extract a repository you must also move the service to `Humans.Application.Services.X`, stitch cross-section reads through owning-service interfaces, and commit everything together.
9. **Does `dotnet build Humans.slnx` still pass?** → Must pass after every change.
10. **Does `dotnet test Humans.slnx` still pass?** → Must pass after every change.
