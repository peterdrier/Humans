# Store Team Orders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let department coordinators place non-billable orders against the existing Store catalog so supplier-aggregation totals reflect their demand.

**Architecture:** Make `StoreOrder` polymorphic — either `CampSeasonId` (billable, today's flow, unchanged) or `TeamId` (non-billable, no payment/invoice path, stays `Open` forever). Service layer enforces "exactly one counterparty"; DB does not. Catalog and `OrderableUntil` gate are reused as-is. `Year` is added to the row, populated lazily.

**Tech Stack:** ASP.NET Core MVC, EF Core (PostgreSQL), NodaTime, NSubstitute + xUnit, resource-based authorization. Existing Store section conventions: `IRepository` + `IApplicationService` + `IDbContextFactory` (§15b Singleton). Cross-section reads go through `ITeamServiceRead` (existing methods only — its `[SurfaceBudget(4)]` is full).

**Branch:** `feat/store-team-orders` (worktree at `H:\source\Humans\.worktrees\store-team-orders`).

**Spec:** [`docs/superpowers/specs/2026-05-27-store-team-orders-design.md`](../specs/2026-05-27-store-team-orders-design.md).

---

## Task 1: Domain entity + EF config + migration

**Files:**
- Modify: `src/Humans.Domain/Entities/StoreOrder.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderConfiguration.cs`
- Generate: `src/Humans.Infrastructure/Data/Migrations/*_StoreOrderTeamCounterpartyAndYear.cs`

- [ ] **Step 1: Make `CampSeasonId` nullable, add `TeamId` and `Year` on `StoreOrder`**

In `src/Humans.Domain/Entities/StoreOrder.cs` change:

```csharp
public Guid? CampSeasonId { get; set; }

/// <summary>
/// Cross-section linkage to <c>Team</c> — bare Guid, no EF navigation, no FK constraint
/// (per <c>memory/architecture/no-cross-section-ef-joins.md</c>). Resolved at the service
/// layer via <c>ITeamServiceRead.GetTeamAsync</c>. Exactly one of <see cref="CampSeasonId"/>
/// and <see cref="TeamId"/> is non-null; the invariant is service-enforced, not DB-enforced.
/// </summary>
public Guid? TeamId { get; set; }

/// <summary>
/// Event year the order's catalog draws from. Always set on write. For camp orders this
/// mirrors <c>CampSeason.Year</c>; for team orders it is the active event year at create time.
/// </summary>
public int Year { get; set; }
```

- [ ] **Step 2: Update `StoreOrderConfiguration`**

In `src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderConfiguration.cs`:

- `CampSeasonId`: drop `.IsRequired()` if present; keep its existing index.
- Add `builder.Property(o => o.TeamId);` (nullable).
- Add `builder.HasIndex(o => o.TeamId);`
- Add `builder.Property(o => o.Year).IsRequired();`

No new FK constraints (cross-section linkage stays bare-Guid).

- [ ] **Step 3: Build to confirm config compiles**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Generate migration**

Run from repo root: `dotnet ef migrations add StoreOrderTeamCounterpartyAndYear --project src/Humans.Infrastructure --startup-project src/Humans.Web`
Expected: three new files under `src/Humans.Infrastructure/Data/Migrations/` (`*_StoreOrderTeamCounterpartyAndYear.cs`, `.Designer.cs`, snapshot update). Do not hand-edit any of them.

- [ ] **Step 5: Re-run build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Domain/Entities/StoreOrder.cs \
        src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderConfiguration.cs \
        src/Humans.Infrastructure/Data/Migrations/
git commit -m "feat(store): StoreOrder gains nullable TeamId/CampSeasonId + Year"
```

---

## Task 2: DTOs — polymorphic `OrderDto` + counterparty enum

**Files:**
- Modify: `src/Humans.Application/Services/Store/Dtos/OrderDto.cs`
- Modify: `src/Humans.Application/Services/Store/Dtos/StoreIndexData.cs`
- Create: `src/Humans.Domain/Enums/StoreOrderCounterpartyType.cs`

- [ ] **Step 1: Add `StoreOrderCounterpartyType` enum**

Create `src/Humans.Domain/Enums/StoreOrderCounterpartyType.cs`:

```csharp
namespace Humans.Domain.Enums;

public enum StoreOrderCounterpartyType
{
    Camp = 0,
    Team = 1,
}
```

- [ ] **Step 2: Extend `OrderDto`**

Rewrite `src/Humans.Application/Services/Store/Dtos/OrderDto.cs`:

```csharp
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public record OrderDto(
    Guid Id,
    Guid? CampSeasonId,
    Guid? TeamId,
    StoreOrderCounterpartyType CounterpartyType,
    string CounterpartyDisplayName,
    int Year,
    string? Label,
    StoreOrderState State,
    string? CounterpartyName,
    string? CounterpartyVatId,
    string? CounterpartyAddress,
    string? CounterpartyCountryCode,
    string? CounterpartyEmail,
    Guid? IssuedInvoiceId,
    IReadOnlyList<OrderLineDto> Lines,
    decimal LinesSubtotalEur,
    decimal VatTotalEur,
    decimal DepositTotalEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
```

- [ ] **Step 3: Extend `StoreIndexData` with unified counterparty list**

Rewrite `src/Humans.Application/Services/Store/Dtos/StoreIndexData.cs`:

```csharp
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public sealed record StoreIndexData(
    int Year,
    IReadOnlyList<ProductDto> Catalog,
    IReadOnlyList<StoreCounterpartyOrders> Counterparties,
    bool ShowNoOrdersMessage);

public sealed record StoreCounterpartyOrders(
    StoreOrderCounterpartyType CounterpartyType,
    Guid CounterpartyId,
    string DisplayName,
    int Year,
    IReadOnlyList<OrderDto> Orders);

public sealed record StoreOrderPageData(
    OrderDto Order,
    IReadOnlyList<ProductDto> Catalog,
    string CounterpartyDisplayName,
    bool CanEdit,
    bool CanPay,
    bool IsStripeConfigured);
```

The old `StoreCampSeasonOrders` record is replaced. The old `StoreOrderPageData.CampName` becomes `CounterpartyDisplayName`.

- [ ] **Step 4: Build (will fail at callers — expected)**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build fails — `StoreService`, `StoreController`, view models, and tests still reference the old shapes. Those fixes happen in subsequent tasks.

- [ ] **Step 5: Commit (DTO shape only)**

```bash
git add src/Humans.Application/Services/Store/Dtos/OrderDto.cs \
        src/Humans.Application/Services/Store/Dtos/StoreIndexData.cs \
        src/Humans.Domain/Enums/StoreOrderCounterpartyType.cs
git commit -m "feat(store): OrderDto + StoreIndexData go polymorphic"
```

---

## Task 3: Repository — team-scoped queries

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs`
- Modify: `tests/Humans.Application.Tests/Architecture/Baselines/*` (only if architecture baseline lists the surface — check after build)

- [ ] **Step 1: Add team-order methods to `IStoreRepository`**

In `src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs`, after `GetOrdersForCampSeasonsWithLinesAndPaymentsAsync`:

```csharp
/// <summary>
/// Returns the single open team order for <paramref name="teamId"/>, or null. The
/// "one order per team per year" invariant is service-enforced; this method returns
/// the first match if more exist.
/// </summary>
Task<StoreOrder?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default);

/// <summary>
/// Returns every <see cref="StoreOrder"/> whose <c>TeamId</c> is in
/// <paramref name="teamIds"/>, with <c>Lines</c> eager-loaded. Empty input returns an
/// empty list without a round-trip. Used by the admin summary cross-tab.
/// </summary>
Task<IReadOnlyList<StoreOrder>> GetOrdersForTeamsWithLinesAsync(
    IReadOnlyCollection<Guid> teamIds,
    int year,
    CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `StoreRepository`**

In `src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs`, follow the existing camp-scoped method shape (open `IDbContextFactory` context, `.AsNoTracking()` for reads, eager-load `Lines` where indicated). Example:

```csharp
public async Task<StoreOrder?> GetOrderForTeamAsync(Guid teamId, int year, CancellationToken ct = default)
{
    await using var db = await contextFactory.CreateDbContextAsync(ct);
    return await db.Set<StoreOrder>()
        .AsNoTracking()
        .Include(o => o.Lines)
        .Where(o => o.TeamId == teamId && o.Year == year)
        .FirstOrDefaultAsync(ct);
}

public async Task<IReadOnlyList<StoreOrder>> GetOrdersForTeamsWithLinesAsync(
    IReadOnlyCollection<Guid> teamIds,
    int year,
    CancellationToken ct = default)
{
    if (teamIds.Count == 0) return [];
    await using var db = await contextFactory.CreateDbContextAsync(ct);
    return await db.Set<StoreOrder>()
        .AsNoTracking()
        .Include(o => o.Lines)
        .Where(o => o.TeamId.HasValue && teamIds.Contains(o.TeamId.Value) && o.Year == year)
        .ToListAsync(ct);
}
```

- [ ] **Step 3: Build (other callers still broken — expected)**

Run: `dotnet build Humans.slnx -v quiet`
Expected: same DTO-related caller errors from Task 2, plus the new repo methods compile. No new errors out of the repo files themselves.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs \
        src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs
git commit -m "feat(store): repo gains team-scoped order queries"
```

---

## Task 4: Service — `IStoreService` shape + team write methods

**Files:**
- Modify: `src/Humans.Application/Interfaces/Store/IStoreService.cs`
- Modify: `src/Humans.Application/Services/Store/StoreService.cs`
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs` (new)

- [ ] **Step 1: Add team-order methods to `IStoreService`**

In `src/Humans.Application/Interfaces/Store/IStoreService.cs` under the camp-lead Orders section:

```csharp
// Orders (team coordinator)
Task<Guid> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default);
Task<OrderDto?> GetOrderForTeamAsync(Guid teamId, CancellationToken ct = default);
```

- [ ] **Step 2: Write failing test for `CreateTeamOrderAsync`**

Create `tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs`. Mirror the existing `StoreServiceTests.cs` SUT setup (NSubstitute for `IStoreRepository`, `IAuditLogService`, `ICampService`, `IClock`, `IShiftManagementService`, `IStripeService`, `ITeamServiceRead`, `ILogger<StoreService>`). Add:

```csharp
[Fact]
public async Task CreateTeamOrderAsync_writes_order_with_team_id_and_active_year()
{
    var teamId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    _shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026, TimeZoneId = "Europe/Madrid" });
    _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
        .Returns(new TeamInfo(teamId, "Kitchen", null, "kitchen", true, false,
            SystemTeamType.None, false, true, false, false, Instant.FromUtc(2026,1,1,0,0),
            new List<TeamMemberInfo>(), ParentTeamId: null,
            ManagementRoleHolderUserIds: new HashSet<Guid> { userId }));
    _repo.GetOrderForTeamAsync(teamId, 2026, Arg.Any<CancellationToken>()).Returns((StoreOrder?)null);

    var id = await _service.CreateTeamOrderAsync(teamId, userId);

    await _repo.Received(1).AddOrderAsync(Arg.Is<StoreOrder>(o =>
        o.TeamId == teamId &&
        o.CampSeasonId == null &&
        o.Year == 2026 &&
        o.State == StoreOrderState.Open));
    Assert.NotEqual(Guid.Empty, id);
}

[Fact]
public async Task CreateTeamOrderAsync_throws_when_team_already_has_order_this_year()
{
    var teamId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    _shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026, TimeZoneId = "Europe/Madrid" });
    _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
        .Returns(new TeamInfo(teamId, "Kitchen", null, "kitchen", true, false,
            SystemTeamType.None, false, true, false, false, Instant.FromUtc(2026,1,1,0,0),
            new List<TeamMemberInfo>(), ParentTeamId: null,
            ManagementRoleHolderUserIds: new HashSet<Guid> { userId }));
    _repo.GetOrderForTeamAsync(teamId, 2026, Arg.Any<CancellationToken>())
        .Returns(new StoreOrder { Id = Guid.NewGuid(), TeamId = teamId, Year = 2026 });

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => _service.CreateTeamOrderAsync(teamId, userId));
}

[Fact]
public async Task CreateTeamOrderAsync_throws_when_team_is_a_subteam()
{
    var teamId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    _shifts.GetActiveAsync().Returns(new EventSettings { Year = 2026, TimeZoneId = "Europe/Madrid" });
    _teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
        .Returns(new TeamInfo(teamId, "Sub", null, "sub", true, false,
            SystemTeamType.None, false, true, false, false, Instant.FromUtc(2026,1,1,0,0),
            new List<TeamMemberInfo>(), ParentTeamId: Guid.NewGuid()));

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => _service.CreateTeamOrderAsync(teamId, userId));
}
```

(Adjust constructor positional args of `TeamInfo` to match the actual record — see `ITeamService.cs` for current order.)

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~StoreServiceTeamOrdersTests"`
Expected: tests fail to compile (no `_teams` field, no `CreateTeamOrderAsync`).

- [ ] **Step 3: Add `ITeamServiceRead` dependency to `StoreService` ctor**

In `src/Humans.Application/Services/Store/StoreService.cs`:

```csharp
public class StoreService(
    IStoreRepository repo,
    IAuditLogService audit,
    ICampService campService,
    ITeamServiceRead teamService,
    IClock clock,
    IShiftManagementService shifts,
    IStripeService stripeService,
    ILogger<StoreService> logger) : IStoreService
```

Update DI registration in `src/Humans.Web/Program.cs` (or wherever Store services register) — `ITeamServiceRead` should already be registered by the Teams section; no new registration needed, just a constructor signature change.

- [ ] **Step 4: Implement `CreateTeamOrderAsync` and `GetOrderForTeamAsync`**

Add to `StoreService`:

```csharp
public async Task<Guid> CreateTeamOrderAsync(Guid teamId, Guid actorUserId, CancellationToken ct = default)
{
    var team = await teamService.GetTeamAsync(teamId, ct)
        ?? throw new InvalidOperationException($"Team {teamId} not found.");
    if (team.ParentTeamId is not null)
        throw new InvalidOperationException("Team orders are restricted to departments (top-level teams).");

    var activeEvent = await shifts.GetActiveAsync();
    var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;

    var existing = await repo.GetOrderForTeamAsync(teamId, year, ct);
    if (existing is not null)
        throw new InvalidOperationException($"Team {teamId} already has a Store order for {year}.");

    var now = clock.GetCurrentInstant();
    var order = new StoreOrder
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        CampSeasonId = null,
        Year = year,
        State = StoreOrderState.Open,
        CreatedAt = now,
        UpdatedAt = now,
    };
    await repo.AddOrderAsync(order, ct);
    await audit.LogAsync(AuditEvent.StoreOrderCreated, actorUserId, order.Id.ToString(),
        $"Team: {team.Name}", ct);
    return order.Id;
}

public async Task<OrderDto?> GetOrderForTeamAsync(Guid teamId, CancellationToken ct = default)
{
    var activeEvent = await shifts.GetActiveAsync();
    var year = activeEvent?.Year > 0 ? activeEvent.Year : clock.GetCurrentInstant().InUtc().Year;
    var order = await repo.GetOrderForTeamAsync(teamId, year, ct);
    if (order is null) return null;
    return await MapOrderToDtoAsync(order, ct);
}
```

`MapOrderToDtoAsync` is the existing private mapper — Step 5 updates it to fill `CounterpartyDisplayName`.

- [ ] **Step 5: Update `MapOrderToDtoAsync` (or equivalent) to fill the new DTO fields**

Find the existing order-to-DTO mapper inside `StoreService`. Update it to:
- Pass through `CampSeasonId`, `TeamId`, `Year`.
- Set `CounterpartyType = order.TeamId.HasValue ? Team : Camp`.
- Resolve `CounterpartyDisplayName`:
  - Camp: existing `campService.GetCampSeasonByIdAsync(...).Name` lookup.
  - Team: `teamService.GetTeamAsync(...).Name` lookup.
  - Fallback to `"(unknown)"` if either lookup returns null.

- [ ] **Step 6: Run team-order tests, expect green**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~StoreServiceTeamOrdersTests"`
Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/Store/IStoreService.cs \
        src/Humans.Application/Services/Store/StoreService.cs \
        tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs
git commit -m "feat(store): CreateTeamOrderAsync + GetOrderForTeamAsync"
```

---

## Task 5: Service — guard rails on billable-only methods

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs`
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs`

- [ ] **Step 1: Write failing guard-rail tests**

Append to `StoreServiceTeamOrdersTests.cs`:

```csharp
[Theory]
[MemberData(nameof(BillableOnlyMethods))]
public async Task Billable_only_methods_throw_on_team_orders(string methodName, Func<StoreService, Guid, Task> invoke)
{
    var orderId = Guid.NewGuid();
    var teamOrder = new StoreOrder { Id = orderId, TeamId = Guid.NewGuid(), CampSeasonId = null, Year = 2026 };
    _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(teamOrder);
    _repo.GetOrderWithLinesAndPaymentsAsync(orderId, Arg.Any<CancellationToken>()).Returns(teamOrder);

    await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(_service, orderId));
}

public static IEnumerable<object[]> BillableOnlyMethods()
{
    yield return new object[] { "UpdateCounterpartyAsync",
        (Func<StoreService, Guid, Task>)((s, id) =>
            s.UpdateCounterpartyAsync(id, new OrderCounterpartyInput("N", null, null, null, null), Guid.NewGuid())) };
    yield return new object[] { "RecordManualPaymentAsync",
        (Func<StoreService, Guid, Task>)((s, id) =>
            s.RecordManualPaymentAsync(id, 1m, StorePaymentMethod.Manual, null, null, Guid.NewGuid())) };
    yield return new object[] { "CreateStripeCheckoutSessionAsync",
        (Func<StoreService, Guid, Task>)((s, id) => Task.Run(async () =>
        {
            var dto = await s.GetOrderAsync(id);
            await s.CreateStripeCheckoutSessionAsync(dto!, 1m, "https://x");
        })) };
    yield return new object[] { "IssueInvoiceAsync",
        (Func<StoreService, Guid, Task>)((s, id) => s.IssueInvoiceAsync(id, Guid.NewGuid())) };
}
```

Run: `dotnet test ... --filter "Billable_only_methods_throw_on_team_orders"`
Expected: fails (current code doesn't guard).

- [ ] **Step 2: Add guard at the top of each method**

In `StoreService.cs`, the first action of each of the following methods is to load the order (most already do) and throw if `order.TeamId is not null`. Helper:

```csharp
private static void EnsureBillable(StoreOrder order)
{
    if (order.TeamId is not null)
        throw new InvalidOperationException("Team orders are non-billable.");
}
```

Apply at the top of these (after the existing `repo.GetOrder…` load):

- `UpdateCounterpartyAsync` / `UpdateCounterpartyWithResultAsync`
- `RecordManualPaymentAsync`
- `CreateStripeCheckoutSessionAsync` — for this one, branch on `OrderDto.CounterpartyType`.
- `RecordStripePaymentAsync` — load by `orderId` and guard.
- `HandleStripeCheckoutWebhookEventAsync` — guard at the order-lookup branch.
- `IssueInvoiceAsync`.

- [ ] **Step 3: Re-run tests**

Run: `dotnet test ... --filter "Billable_only_methods_throw_on_team_orders"`
Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Store/StoreService.cs \
        tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs
git commit -m "feat(store): guard rails — billable-only methods reject team orders"
```

---

## Task 6: Service — lazy `Year` backfill on camp order saves

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs`
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs`

- [ ] **Step 1: Write failing test**

Append:

```csharp
[Fact]
public async Task Add_line_to_camp_order_with_zero_year_backfills_year_from_season()
{
    var orderId = Guid.NewGuid();
    var seasonId = Guid.NewGuid();
    var productId = Guid.NewGuid();
    var order = new StoreOrder
    {
        Id = orderId,
        CampSeasonId = seasonId,
        TeamId = null,
        Year = 0, // legacy row, never re-saved since column added
        State = StoreOrderState.Open,
    };
    _repo.GetOrderByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(order);
    _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>())
        .Returns(new CampSeasonInfo(seasonId, Guid.NewGuid(), "Camp X", Year: 2025));
    _repo.GetProductByIdAsync(productId, Arg.Any<CancellationToken>())
        .Returns(new StoreProduct
        {
            Id = productId, Year = 2025, IsActive = true,
            OrderableUntil = LocalDate.MaxIsoValue,
            UnitPriceEur = 10m, VatRatePercent = 21m,
        });

    await _service.AddLineAsync(orderId, productId, 1, Guid.NewGuid());

    await _repo.Received(1).UpdateOrderAsync(Arg.Is<StoreOrder>(o => o.Year == 2025));
}
```

Run: fails (no backfill yet).

- [ ] **Step 2: Add backfill to camp-touching mutators**

In `StoreService.cs`, factor out:

```csharp
private async Task EnsureYearPopulatedAsync(StoreOrder order, CancellationToken ct)
{
    if (order.Year != 0) return;
    if (order.CampSeasonId is { } seasonId)
    {
        var season = await campService.GetCampSeasonByIdAsync(seasonId, ct);
        if (season is not null) order.Year = season.Year;
    }
}
```

Call it in: `AddLineAsync`, `RemoveLineAsync`, `UpdateCounterpartyAsync`, `RecordManualPaymentAsync`, `RecordStripePaymentAsync` — anywhere a camp order is loaded and saved. Persist the change in the same `UpdateOrderAsync` call the method already makes (or add an explicit one when the method does not currently mutate the order row).

- [ ] **Step 3: Re-run test**

Run: same filter.
Expected: pass.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Store/StoreService.cs \
        tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs
git commit -m "feat(store): lazy Year backfill on camp order writes"
```

---

## Task 7: Service — `GetIndexDataAsync` includes coordinated teams

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs`
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Index_data_lists_coordinated_departments_alongside_led_camps()
{
    var userId = Guid.NewGuid();
    var deptId = Guid.NewGuid();
    _shifts.GetActiveAsync().Returns(new ActiveEventInfo(2026, "Europe/Madrid"));
    _campService.GetCampLeadSeasonIdForYearAsync(userId, 2026, Arg.Any<CancellationToken>()).Returns((Guid?)null);
    _teams.GetTeamsAsync(Arg.Any<CancellationToken>())
        .Returns(new Dictionary<Guid, TeamInfo>
        {
            [deptId] = new TeamInfo(deptId, "Build", null, "build", true, false,
                SystemTeamType.None, false, true, false, false, Instant.FromUtc(2026,1,1,0,0),
                new List<TeamMemberInfo>(), ParentTeamId: null,
                ManagementRoleHolderUserIds: new HashSet<Guid> { userId }),
        });
    _repo.GetOrderForTeamAsync(deptId, 2026, Arg.Any<CancellationToken>()).Returns((StoreOrder?)null);
    _repo.GetActiveProductsForYearAsync(2026, Arg.Any<CancellationToken>())
        .Returns(new List<StoreProduct>());

    var data = await _service.GetIndexDataAsync(userId, isPrivilegedReader: false);

    Assert.Single(data.Counterparties);
    Assert.Equal(StoreOrderCounterpartyType.Team, data.Counterparties[0].CounterpartyType);
    Assert.Equal(deptId, data.Counterparties[0].CounterpartyId);
    Assert.False(data.ShowNoOrdersMessage);
}
```

- [ ] **Step 2: Update `GetIndexDataAsync`**

In `StoreService.cs`, rewrite the index data builder so the loop builds `StoreCounterpartyOrders` for both:
- The user's lead camp-season (existing logic, mapped to `CounterpartyType.Camp`).
- Every department (`ParentTeamId is null`) in `teamService.GetTeamsAsync()` where `ManagementRoleHolderUserIds.Contains(userId)`. Use `repo.GetOrderForTeamAsync(teamId, year)` to load the existing order (may be null — coordinator sees "Create order" CTA).

`ShowNoOrdersMessage` = `Counterparties.Count == 0 && !isPrivilegedReader`.

- [ ] **Step 3: Run test, expect green**

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Store/StoreService.cs \
        tests/Humans.Application.Tests/Services/Store/StoreServiceTeamOrdersTests.cs
git commit -m "feat(store): index lists coordinated departments + their orders"
```

---

## Task 8: Service — `GetOrderPageDataAsync` resolves team counterparty

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs`

- [ ] **Step 1: Branch on `OrderDto.CounterpartyType`**

In `GetOrderPageDataAsync`, replace the `campService.GetCampSeasonByIdAsync(order.CampSeasonId, ct)` call with branching:

```csharp
string counterpartyName = order.CounterpartyType switch
{
    StoreOrderCounterpartyType.Camp when order.CampSeasonId is { } sid =>
        (await campService.GetCampSeasonByIdAsync(sid, ct))?.Name ?? "(unknown camp)",
    StoreOrderCounterpartyType.Team when order.TeamId is { } tid =>
        (await teamService.GetTeamAsync(tid, ct))?.Name ?? "(unknown team)",
    _ => "(unknown)",
};
return new StoreOrderPageData(
    order, catalog, counterpartyName, canEdit,
    canPayAuthorized && order.BalanceEur > 0 && order.CounterpartyType == StoreOrderCounterpartyType.Camp,
    stripeService.IsStoreCheckoutConfigured);
```

The `CanPay` flag is forced false for team orders so the Pay button never renders even if auth somehow granted it.

- [ ] **Step 2: Build + run existing Store tests**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~Store"`
Expected: all green. (Old `StoreServiceTests` may still expect `CampName` on `StoreOrderPageData` — update the assertions to `CounterpartyDisplayName` if any fail.)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Services/Store/StoreService.cs \
        tests/Humans.Application.Tests/Services/Store/StoreServiceTests.cs
git commit -m "feat(store): order page data resolves team counterparty name"
```

---

## Task 9: Auth handler — branch on counterparty type

**Files:**
- Modify: `src/Humans.Web/Authorization/Requirements/StoreOrderAuthorizationHandler.cs`
- Modify: `src/Humans.Web/Authorization/Requirements/StoreOrderCreateContext.cs` (if exists — extend to carry teamId too)
- Test: `tests/Humans.Web.Tests/Authorization/StoreOrderAuthorizationHandlerTests.cs` (new or existing)

- [ ] **Step 1: Find or define `StoreOrderCreateContext`**

Run: `grep -rn "StoreOrderCreateContext" src/`. If it exists, extend:

```csharp
public record StoreOrderCreateContext(Guid? CampSeasonId, Guid? TeamId);
```

Update existing call sites that construct it.

- [ ] **Step 2: Write failing auth-handler tests**

Add tests:
- Coordinator of a team can `View` / `AddLine` / `RemoveLine` on a team order.
- Non-coordinator denied on the same operations.
- `EditCounterparty` and `Pay` denied for any team order (defense-in-depth).

Use the existing tests file's NSubstitute pattern; inject `ITeamServiceRead` mock alongside the `ICampService` mock.

- [ ] **Step 3: Extend the handler**

In `StoreOrderAuthorizationHandler.cs`, rewrite the switch and lookup:

```csharp
public class StoreOrderAuthorizationHandler(
    ICampService campService,
    ITeamServiceRead teamService) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        var pending = context.PendingRequirements.OfType<StoreOrderOperationRequirement>().ToList();
        if (pending.Count == 0) return;

        Guid? campSeasonId; Guid? teamId; StoreOrderState? orderState;
        switch (context.Resource)
        {
            case OrderDto order:
                campSeasonId = order.CampSeasonId; teamId = order.TeamId; orderState = order.State; break;
            case StoreOrderCreateContext create:
                campSeasonId = create.CampSeasonId; teamId = create.TeamId; orderState = null; break;
            default: return;
        }

        if (RoleChecks.CanAdministerStore(context.User))
        {
            foreach (var req in pending) context.Succeed(req);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId)) return;

        bool authorized = false;
        if (campSeasonId is { } sid)
        {
            var season = await campService.GetCampSeasonByIdAsync(sid);
            if (season is not null && await campService.IsUserCampLeadAsync(userId, season.CampId))
                authorized = true;
        }
        else if (teamId is { } tid)
        {
            var team = await teamService.GetTeamAsync(tid);
            if (team?.ManagementRoleHolderUserIds?.Contains(userId) == true && team.ParentTeamId is null)
                authorized = true;
        }
        if (!authorized) return;

        foreach (var req in pending)
        {
            // Team orders never allow EditCounterparty or Pay regardless of coordinator status.
            if (teamId is not null &&
                (req == StoreOrderOperationRequirement.EditCounterparty ||
                 req == StoreOrderOperationRequirement.Pay))
                continue;

            var isMutating = req == StoreOrderOperationRequirement.AddLine
                || req == StoreOrderOperationRequirement.RemoveLine
                || req == StoreOrderOperationRequirement.EditCounterparty;
            if (isMutating && orderState is not null and not StoreOrderState.Open) continue;
            context.Succeed(req);
        }
    }
}
```

- [ ] **Step 4: Run handler tests, expect green**

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Authorization/Requirements/StoreOrderAuthorizationHandler.cs \
        src/Humans.Web/Authorization/Requirements/StoreOrderCreateContext.cs \
        tests/Humans.Web.Tests/Authorization/StoreOrderAuthorizationHandlerTests.cs
git commit -m "feat(store): auth handler branches on team coordinator"
```

---

## Task 10: Controller — create-team-order route + Index/Order view branching

**Files:**
- Modify: `src/Humans.Web/Controllers/StoreController.cs`
- Modify: `src/Humans.Web/Models/Store/StoreIndexViewModel.cs`
- Modify: `src/Humans.Web/Models/Store/StoreOrderViewModel.cs`
- Modify: `src/Humans.Web/Views/Store/Index.cshtml`
- Modify: `src/Humans.Web/Views/Store/Order.cshtml`

- [ ] **Step 1: Add `CreateTeamOrder` POST**

In `StoreController.cs`:

```csharp
[HttpPost("Team/{teamId:guid}/Create")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> CreateTeamOrder(Guid teamId, CancellationToken ct)
{
    var (errorResult, user) = await RequireCurrentUserAsync();
    if (errorResult is not null) return errorResult;

    var createContext = new StoreOrderCreateContext(CampSeasonId: null, TeamId: teamId);
    var auth = await authService.AuthorizeAsync(User, createContext, StoreOrderOperationRequirement.Create);
    if (!auth.Succeeded) return Forbid();

    var orderId = await storeService.CreateTeamOrderAsync(teamId, user.Id, ct);
    return RedirectToAction(nameof(Order), new { id = orderId });
}
```

- [ ] **Step 2: Update `StoreIndexViewModel`** to carry `Counterparties` (renamed from `CampSeasons`).

- [ ] **Step 3: Update `Index.cshtml`** to iterate `Counterparties` and render an action per row:
- If `Orders.Count > 0`: link to the existing order.
- Else if `CounterpartyType == Team`: render a "Create order" POST form to `/Store/Team/{id}/Create`.
- Else if `CounterpartyType == Camp` and no orders: existing camp-create flow (unchanged).

- [ ] **Step 4: Update `Order.cshtml`** to skip the counterparty form, payments list, Pay button, and Issue Invoice section when `Order.CounterpartyType == Team`. Add a small footer: "Non-billable — counts toward supplier totals only."

- [ ] **Step 5: Build + run web tests**

Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Store"`
Expected: green.

- [ ] **Step 6: Manual smoke**

Run: `dotnet run --project src/Humans.Web`
Verify:
- Sign in as a department coordinator with no camp-lead role → `/Store` shows their department with a "Create order" button.
- Click it → redirects to `/Store/Order/{id}` showing an empty team order.
- Add a line → it persists.
- The Pay button and counterparty form are not rendered.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/StoreController.cs \
        src/Humans.Web/Models/Store/ \
        src/Humans.Web/Views/Store/
git commit -m "feat(store): team coordinator UI — create + order page"
```

---

## Task 11: Summary — merge team orders into the cross-tab

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs` (`GetStoreSummaryAsync`)
- Modify: `src/Humans.Application/Services/Store/Dtos/StoreSummaryDto.cs`
- Modify: `src/Humans.Web/Views/Store/Summary.cshtml`
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTests.cs` (extend)

- [ ] **Step 1: Extend `StoreSummaryDto`** with a counterparty-type column on each row of the by-counterparty list. The cross-tab columns become "counterparties" (camps + teams) instead of "camps".

- [ ] **Step 2: Write failing test**

A summary call against a year with one camp order (qty 3) and one team order (qty 5) for the same product produces a row total of 8 on the by-product roll-up and two distinct rows in the by-counterparty list.

- [ ] **Step 3: Update `GetStoreSummaryAsync`**

Load both camp-scoped orders (existing call) and team-scoped orders (new `repo.GetOrdersForTeamsWithLinesAsync` — pass every dept teamId from `teamService.GetTeamsAsync()` filtered by `ParentTeamId == null`). Merge into the same by-counterparty / cross-tab structures.

- [ ] **Step 4: Update `Summary.cshtml`** to render a "Type" column on the by-counterparty table.

- [ ] **Step 5: Run summary tests, expect green**

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Store/StoreService.cs \
        src/Humans.Application/Services/Store/Dtos/StoreSummaryDto.cs \
        src/Humans.Web/Views/Store/Summary.cshtml \
        tests/Humans.Application.Tests/Services/Store/StoreServiceTests.cs
git commit -m "feat(store): summary merges team orders into cross-tab"
```

---

## Task 12: Docs — update `docs/sections/Store.md`

**Files:**
- Modify: `docs/sections/Store.md`

- [ ] **Step 1: Update the invariants doc**

In the "Concepts" section, add: "A **Store Order** may be owned by a `CampSeason` (billable, full lifecycle) or by a `Team` (non-billable, stays Open, department-level only)."

In "Data Model → StoreOrder", reflect the nullable `CampSeasonId`, the new `TeamId`, and the new `Year` column. Note the lazy-fill rule for `Year`.

In "Actors & Roles", add a Coordinator row: "Coordinator (department): view / create / add-line / remove-line on their department's team order. No pay, no counterparty edit."

In "Invariants", add: "An order has exactly one counterparty — `CampSeasonId` xor `TeamId`. Invariant is service-enforced, not DB-enforced." And: "Team orders never transition out of `Open`. Payment / invoice / Stripe paths reject team orders."

In "Cross-Section Dependencies", add `ITeamServiceRead` (existing methods).

- [ ] **Step 2: Commit**

```bash
git add docs/sections/Store.md
git commit -m "docs(store): document team-orders counterparty"
```

---

## Task 13: Final verification + push

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all green.

- [ ] **Step 2: Push**

```bash
git push
```

- [ ] **Step 3: Open PR**

Use `gh pr create` per the repo workflow. Target `peterdrier/Humans:main`. Title: `feat(store): team orders as non-billable counterparty`.
