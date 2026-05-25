# Store Summary admin aggregate views — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill the existing `/Store/Summary` Phase 5 stub with three admin-facing aggregate projections (by-camp, by-item, camps × products cross-tab) over a single event year.

**Architecture:** One new repo method loads all orders for the year's camp-seasons with `Lines` + `Payments` eager-loaded. The service builds all three projections in-memory using `BalanceCalculator` for camp totals and straight LINQ for the rest. A single composite `StoreSummaryDto` flows through a new `StoreAdminController.Summary` action into a Razor view stacked as three sections. Authorization reuses the existing `StoreCatalogAdmin` policy, which already gates StoreAdmin/FinanceAdmin/Admin.

**Tech stack:** ASP.NET Core MVC, EF Core (`HumansDbContext` via `IDbContextFactory`), NodaTime, NSubstitute + xUnit for unit tests, AwesomeAssertions for assertions, Bootstrap 5 in the Razor view.

**Spec:** [`docs/superpowers/specs/2026-05-18-store-summary-aggregates-design.md`](../specs/2026-05-18-store-summary-aggregates-design.md)

---

## File map

**Create:**
- `src/Humans.Application/Services/Store/Dtos/StoreSummaryDto.cs` — composite DTO + product aggregate + cross-tab row/column types.
- `src/Humans.Web/Models/Store/StoreSummaryViewModel.cs` — view model wrapping the DTO plus the selected year and the dropdown.
- `src/Humans.Web/Views/StoreAdmin/Summary.cshtml` — Razor view, three stacked sections, client-side paid-status filter.
- `tests/Humans.Application.Tests/Services/Store/StoreSummaryAggregateTests.cs` — service-layer unit tests for `GetStoreSummaryAsync`.
- `tests/Humans.Integration.Tests/Controllers/StoreSummaryControllerTests.cs` — integration test: auth-gated route returns 200 and renders all three sections.

**Modify:**
- `src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs` — add `GetOrdersForCampSeasonsWithLinesAndPaymentsAsync`.
- `src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs` — implement the new repo method.
- `src/Humans.Application/Interfaces/Store/IStoreService.cs` — remove `GetAllOrderSummariesAsync`, add `GetStoreSummaryAsync`.
- `src/Humans.Application/Services/Store/StoreService.cs` — remove the obsolete stub, implement the new method.
- `src/Humans.Web/Controllers/StoreAdminController.cs` — add `[HttpGet("Summary")] Summary` action.
- `src/Humans.Web/ViewComponents/AdminNavTree.cs` — add a "Store summary" link under the "Money" group.
- `docs/sections/Store.md` — flip `/Store/Summary` from "stub" to live; mention three projections.

---

## Task 1 — Composite DTO

**Files:**
- Create: `src/Humans.Application/Services/Store/Dtos/StoreSummaryDto.cs`

- [ ] **Step 1: Write the DTO file**

Create `src/Humans.Application/Services/Store/Dtos/StoreSummaryDto.cs`:

```csharp
namespace Humans.Application.Services.Store.Dtos;

public sealed record StoreSummaryDto(
    int Year,
    IReadOnlyList<OrderSummaryDto> ByCamp,
    IReadOnlyList<ProductAggregateDto> ByItem,
    StoreCrossTabDto CrossTab);

public sealed record ProductAggregateDto(
    Guid ProductId,
    string ProductName,
    int TotalQty,
    decimal TotalRevenueEur);

public sealed record StoreCrossTabDto(
    IReadOnlyList<StoreCrossTabColumn> Products,
    IReadOnlyList<StoreCrossTabRow> Camps);

public sealed record StoreCrossTabColumn(
    Guid ProductId,
    string ProductName,
    int TotalQty);

public sealed record StoreCrossTabRow(
    Guid CampSeasonId,
    string CampName,
    int TotalQty,
    IReadOnlyDictionary<Guid, int> QtyByProduct);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Humans.Application/Humans.Application.csproj -v quiet`
Expected: build succeeds; new types are referenced from nowhere yet (that's fine).

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Services/Store/Dtos/StoreSummaryDto.cs
git commit -m "Add StoreSummaryDto composite for admin aggregate views"
```

---

## Task 2 — Repository method: orders for a set of camp seasons with lines + payments

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs`

- [ ] **Step 1: Add the interface method**

In `src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs`, under the `// Orders` section (right after `GetAllOrdersAsync`):

```csharp
/// <summary>
/// Returns every <see cref="StoreOrder"/> whose <c>CampSeasonId</c> is in
/// <paramref name="campSeasonIds"/>, with <c>Lines</c> and <c>Payments</c>
/// eager-loaded. Empty input returns an empty list without a round-trip.
/// </summary>
Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
    IReadOnlyCollection<Guid> campSeasonIds,
    CancellationToken ct = default);
```

- [ ] **Step 2: Implement it in the repository**

In `src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs`, under the `// Orders` section (near `GetAllOrdersAsync`):

```csharp
public async Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
    IReadOnlyCollection<Guid> campSeasonIds,
    CancellationToken ct = default)
{
    if (campSeasonIds.Count == 0)
        return Array.Empty<StoreOrder>();

    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.StoreOrders.AsNoTracking()
        .Where(o => campSeasonIds.Contains(o.CampSeasonId))
        .Include(o => o.Lines)
        .Include(o => o.Payments)
        .ToListAsync(ct);
}
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs \
        src/Humans.Infrastructure/Repositories/Store/StoreRepository.cs
git commit -m "Add IStoreRepository.GetOrdersForCampSeasonsWithLinesAndPayments"
```

---

## Task 3 — Service method: `GetStoreSummaryAsync` (replaces `GetAllOrderSummariesAsync`)

`GetAllOrderSummariesAsync` is an unimplemented stub with no production callers — replace it cleanly rather than keeping both.

**Files:**
- Modify: `src/Humans.Application/Interfaces/Store/IStoreService.cs`
- Modify: `src/Humans.Application/Services/Store/StoreService.cs`
- Create: `tests/Humans.Application.Tests/Services/Store/StoreSummaryAggregateTests.cs`

- [ ] **Step 1: Write the failing test file**

Create `tests/Humans.Application.Tests/Services/Store/StoreSummaryAggregateTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Store;

public class StoreSummaryAggregateTests
{
    private readonly IStoreRepository _repo = Substitute.For<IStoreRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly IStripeService _stripe = Substitute.For<IStripeService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly StoreService _service;

    public StoreSummaryAggregateTests()
    {
        _service = new StoreService(_repo, _audit, _camps, _clock, _shifts, _stripe, NullLogger<StoreService>.Instance);
    }

    [HumansFact]
    public async Task Empty_year_returns_empty_projections()
    {
        _camps.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>());
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.Year.Should().Be(2026);
        result.ByCamp.Should().BeEmpty();
        result.ByItem.Should().BeEmpty();
        result.CrossTab.Products.Should().BeEmpty();
        result.CrossTab.Camps.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Single_order_single_line_projects_to_all_three_views()
    {
        var seasonId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        _camps.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [seasonId] = new("Camp Alpha", "alpha", null, null, Guid.NewGuid())
            });

        var product = new StoreProduct
        {
            Id = productId,
            Year = 2026,
            Name = "Tent",
            Description = "x",
            UnitPriceEur = 10m,
            VatRatePercent = 21m,
            DepositAmountEur = null,
            OrderableUntil = new LocalDate(2026, 12, 31),
            IsActive = true
        };
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns([product]);

        var order = new StoreOrder
        {
            Id = orderId,
            CampSeasonId = seasonId,
            State = StoreOrderState.Open,
            Lines =
            {
                new StoreOrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    ProductId = productId,
                    Qty = 3,
                    UnitPriceSnapshot = 10m,
                    VatRateSnapshot = 21m,
                    DepositAmountSnapshot = null
                }
            },
            Payments =
            {
                new StorePayment
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    AmountEur = 20m,
                    Method = StorePaymentMethod.Manual,
                    ReceivedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
                }
            }
        };
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(seasonId)),
                Arg.Any<CancellationToken>())
            .Returns([order]);

        var result = await _service.GetStoreSummaryAsync(2026);

        // By-camp
        result.ByCamp.Should().HaveCount(1);
        var camp = result.ByCamp[0];
        camp.OrderId.Should().Be(orderId);
        camp.CampName.Should().Be("Camp Alpha");
        camp.TotalDueEur.Should().Be(36.30m); // 3 * 10 + VAT 6.30
        camp.PaymentsTotalEur.Should().Be(20m);
        camp.BalanceEur.Should().Be(16.30m);

        // By-item
        result.ByItem.Should().HaveCount(1);
        result.ByItem[0].ProductId.Should().Be(productId);
        result.ByItem[0].TotalQty.Should().Be(3);
        result.ByItem[0].TotalRevenueEur.Should().Be(36.30m);

        // Cross-tab
        result.CrossTab.Products.Should().HaveCount(1);
        result.CrossTab.Products[0].TotalQty.Should().Be(3);
        result.CrossTab.Camps.Should().HaveCount(1);
        result.CrossTab.Camps[0].CampName.Should().Be("Camp Alpha");
        result.CrossTab.Camps[0].TotalQty.Should().Be(3);
        result.CrossTab.Camps[0].QtyByProduct[productId].Should().Be(3);
    }

    [HumansFact]
    public async Task Multiple_camps_and_products_produce_consistent_totals()
    {
        var (seasonA, seasonB) = (Guid.NewGuid(), Guid.NewGuid());
        var (productX, productY) = (Guid.NewGuid(), Guid.NewGuid());

        _camps.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [seasonA] = new("Camp Alpha", "alpha", null, null, Guid.NewGuid()),
                [seasonB] = new("Camp Bravo", "bravo", null, null, Guid.NewGuid())
            });

        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new StoreProduct { Id = productX, Year = 2026, Name = "X", Description = "x",
                UnitPriceEur = 5m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = true },
            new StoreProduct { Id = productY, Year = 2026, Name = "Y", Description = "y",
                UnitPriceEur = 7m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = true }
        ]);

        StoreOrder Order(Guid id, Guid season, params (Guid pid, int qty, decimal price)[] lines)
            => new()
            {
                Id = id,
                CampSeasonId = season,
                State = StoreOrderState.Open,
                Lines = [.. lines.Select(l => new StoreOrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = id,
                    ProductId = l.pid,
                    Qty = l.qty,
                    UnitPriceSnapshot = l.price,
                    VatRateSnapshot = 0m,
                    DepositAmountSnapshot = null
                })]
            };

        var orderA = Order(Guid.NewGuid(), seasonA, (productX, 2, 5m), (productY, 1, 7m));
        var orderB = Order(Guid.NewGuid(), seasonB, (productX, 4, 5m));

        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([orderA, orderB]);

        var result = await _service.GetStoreSummaryAsync(2026);

        // By-item totals
        result.ByItem.Single(i => i.ProductId == productX).TotalQty.Should().Be(6);
        result.ByItem.Single(i => i.ProductId == productY).TotalQty.Should().Be(1);

        // Cross-tab column totals match by-item
        var colX = result.CrossTab.Products.Single(c => c.ProductId == productX);
        var colY = result.CrossTab.Products.Single(c => c.ProductId == productY);
        colX.TotalQty.Should().Be(6);
        colY.TotalQty.Should().Be(1);

        // Cross-tab row totals
        result.CrossTab.Camps.Single(r => r.CampName == "Camp Alpha").TotalQty.Should().Be(3);
        result.CrossTab.Camps.Single(r => r.CampName == "Camp Bravo").TotalQty.Should().Be(4);

        // Sum of all cells == sum of column totals == sum of row totals
        var cellSum = result.CrossTab.Camps.Sum(r => r.QtyByProduct.Values.Sum());
        cellSum.Should().Be(result.CrossTab.Products.Sum(c => c.TotalQty));
        cellSum.Should().Be(result.CrossTab.Camps.Sum(r => r.TotalQty));
    }

    [HumansFact]
    public async Task Deactivated_product_with_lines_still_appears()
    {
        var seasonId = Guid.NewGuid();
        var deadProductId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        _camps.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [seasonId] = new("Camp Z", "z", null, null, Guid.NewGuid())
            });
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([
            new StoreProduct { Id = deadProductId, Year = 2026, Name = "Retired", Description = "x",
                UnitPriceEur = 1m, VatRatePercent = 0m, OrderableUntil = new LocalDate(2026,12,31), IsActive = false }
        ]);
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([
                new StoreOrder
                {
                    Id = orderId, CampSeasonId = seasonId, State = StoreOrderState.Open,
                    Lines =
                    {
                        new StoreOrderLine
                        {
                            Id = Guid.NewGuid(), OrderId = orderId, ProductId = deadProductId,
                            Qty = 2, UnitPriceSnapshot = 1m, VatRateSnapshot = 0m
                        }
                    }
                }
            ]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.ByItem.Should().ContainSingle(i => i.ProductId == deadProductId && i.TotalQty == 2);
        result.CrossTab.Products.Should().ContainSingle(p => p.ProductId == deadProductId);
    }

    [HumansFact]
    public async Task Order_for_camp_season_not_in_year_is_excluded()
    {
        // Camp service returns ONE season for 2026; the repo gets that season's id.
        // If the repo somehow returns an order for a different season, we still
        // exclude it (defence-in-depth — the repo filter is the primary gate).
        var inYear = Guid.NewGuid();
        var outOfYear = Guid.NewGuid();
        _camps.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [inYear] = new("InYear", "iy", null, null, Guid.NewGuid())
            });
        _repo.GetAllProductsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([]);
        _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([
                new StoreOrder { Id = Guid.NewGuid(), CampSeasonId = outOfYear, State = StoreOrderState.Open }
            ]);

        var result = await _service.GetStoreSummaryAsync(2026);

        result.ByCamp.Should().BeEmpty();
        result.CrossTab.Camps.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Update `IStoreService` — remove the obsolete method, add the new one**

In `src/Humans.Application/Interfaces/Store/IStoreService.cs`:

Delete this line:
```csharp
    // Summary
    Task<IReadOnlyList<OrderSummaryDto>> GetAllOrderSummariesAsync(int year, CancellationToken ct = default);
```

Replace with:
```csharp
    // Summary
    /// <summary>
    /// Builds the admin aggregate report (by-camp, by-item, camps x products cross-tab)
    /// for the given event year. Used by <c>/Store/Summary</c>.
    /// </summary>
    Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default);
```

- [ ] **Step 3: Run the new test to verify it fails (no implementation)**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter FullyQualifiedName~StoreSummaryAggregateTests -v quiet`
Expected: build error (`GetStoreSummaryAsync` undefined on `StoreService`) OR `NotSupportedException` once the stub is removed.

- [ ] **Step 4: Remove obsolete stub and implement `GetStoreSummaryAsync`**

In `src/Humans.Application/Services/Store/StoreService.cs`:

Delete the existing `GetAllOrderSummariesAsync` method (it throws `NotSupportedException("Phase 5")`).

Add the new method at the end of the class:

```csharp
public async Task<StoreSummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default)
{
    var seasonsForYear = await _campService.GetCampSeasonDisplayDataForYearAsync(year, ct);
    var products = await _repo.GetAllProductsForYearAsync(year, ct);

    var orders = seasonsForYear.Count == 0
        ? (IReadOnlyList<StoreOrder>)Array.Empty<StoreOrder>()
        : await _repo.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync(
            seasonsForYear.Keys.ToList(), ct);

    // Defence-in-depth: only keep orders whose season is actually in the year.
    var ordersInYear = orders
        .Where(o => seasonsForYear.ContainsKey(o.CampSeasonId))
        .ToList();

    var productNames = products.ToDictionary(p => p.Id, p => p.Name);

    // ---- By-camp ----
    var byCamp = ordersInYear
        .Select(o =>
        {
            var totals = BalanceCalculator.Compute(o);
            var totalDue = totals.LinesSubtotalEur + totals.VatTotalEur + totals.DepositTotalEur;
            var campName = seasonsForYear[o.CampSeasonId].Name;
            return new OrderSummaryDto(
                o.Id,
                o.CampSeasonId,
                campName,
                o.Label,
                o.State,
                totalDue,
                totals.PaymentsTotalEur,
                totals.BalanceEur);
        })
        .OrderBy(s => s.CampName, StringComparer.Ordinal)
        .ToList();

    // ---- By-item ----
    var byItem = ordersInYear
        .SelectMany(o => o.Lines.Select(l => new
        {
            l.ProductId,
            l.Qty,
            Subtotal = l.Qty * l.UnitPriceSnapshot,
            Vat = Math.Round(l.Qty * l.UnitPriceSnapshot * l.VatRateSnapshot / 100m, 2, MidpointRounding.AwayFromZero),
            Deposit = l.DepositAmountSnapshot is { } d ? l.Qty * d : 0m
        }))
        .GroupBy(x => x.ProductId)
        .Select(g => new ProductAggregateDto(
            g.Key,
            productNames.TryGetValue(g.Key, out var n) ? n : "(unknown)",
            g.Sum(x => x.Qty),
            g.Sum(x => x.Subtotal + x.Vat + x.Deposit)))
        .OrderByDescending(p => p.TotalQty)
        .ThenBy(p => p.ProductName, StringComparer.Ordinal)
        .ToList();

    // ---- Cross-tab ----
    var productColumns = byItem
        .Select(p => new StoreCrossTabColumn(p.ProductId, p.ProductName, p.TotalQty))
        .OrderBy(c => c.ProductName, StringComparer.Ordinal)
        .ToList();

    var campRows = ordersInYear
        .GroupBy(o => o.CampSeasonId)
        .Select(g =>
        {
            var perProduct = g
                .SelectMany(o => o.Lines)
                .GroupBy(l => l.ProductId)
                .ToDictionary(lg => lg.Key, lg => lg.Sum(l => l.Qty));
            var total = perProduct.Values.Sum();
            return new StoreCrossTabRow(
                g.Key,
                seasonsForYear[g.Key].Name,
                total,
                perProduct);
        })
        .OrderBy(r => r.CampName, StringComparer.Ordinal)
        .ToList();

    return new StoreSummaryDto(
        year,
        byCamp,
        byItem,
        new StoreCrossTabDto(productColumns, campRows));
}
```

- [ ] **Step 5: Run all Store tests to verify the new ones pass and nothing regressed**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter FullyQualifiedName~Services.Store -v quiet`
Expected: all tests pass, including the five new `StoreSummaryAggregateTests`.

- [ ] **Step 6: Build the whole solution to catch any consumer of the deleted interface method**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds. (Spec verified no production callers of `GetAllOrderSummariesAsync` exist.)

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/Store/IStoreService.cs \
        src/Humans.Application/Services/Store/StoreService.cs \
        tests/Humans.Application.Tests/Services/Store/StoreSummaryAggregateTests.cs
git commit -m "Replace GetAllOrderSummariesAsync stub with GetStoreSummaryAsync"
```

---

## Task 4 — Controller action + view model

**Files:**
- Create: `src/Humans.Web/Models/Store/StoreSummaryViewModel.cs`
- Modify: `src/Humans.Web/Controllers/StoreAdminController.cs`

- [ ] **Step 1: Create the view model**

Create `src/Humans.Web/Models/Store/StoreSummaryViewModel.cs`:

```csharp
using Humans.Application.Services.Store.Dtos;

namespace Humans.Web.Models.Store;

public sealed class StoreSummaryViewModel
{
    public required StoreSummaryDto Summary { get; init; }
}
```

- [ ] **Step 2: Add the controller action**

In `src/Humans.Web/Controllers/StoreAdminController.cs`, add this action (place it after `Catalog` for readability) — keep imports tidy at the top of the file:

```csharp
[HttpGet("Summary")]
public async Task<IActionResult> Summary(int? year, CancellationToken ct)
{
    var activeEvent = await _shifts.GetActiveAsync();
    var defaultYear = activeEvent?.Year > 0 ? activeEvent.Year : _clock.GetCurrentInstant().InUtc().Year;
    var selectedYear = year ?? defaultYear;

    var summary = await _storeService.GetStoreSummaryAsync(selectedYear, ct);
    return View(new StoreSummaryViewModel { Summary = summary });
}
```

Add the using at the top if not already present:
```csharp
using Humans.Web.Models.Store;
```

(The route prefix `[Route("Store/Admin")]` makes this `/Store/Admin/Summary`. The spec mentions `/Store/Summary` — that's the externally-facing label; the actual route stays under `/Store/Admin/...` consistent with the rest of the admin controller.)

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/Store/StoreSummaryViewModel.cs \
        src/Humans.Web/Controllers/StoreAdminController.cs
git commit -m "Add StoreAdminController.Summary action and view model"
```

---

## Task 5 — Razor view

**Files:**
- Create: `src/Humans.Web/Views/StoreAdmin/Summary.cshtml`

- [ ] **Step 1: Write the view**

Create `src/Humans.Web/Views/StoreAdmin/Summary.cshtml`:

```cshtml
@model Humans.Web.Models.Store.StoreSummaryViewModel
@{
    ViewData["Title"] = "Store summary";
    var s = Model.Summary;
}

<div class="d-flex justify-content-between align-items-center mb-4">
    <h1 class="mb-0">Store summary</h1>
    <form method="get" class="d-flex gap-2 align-items-center mb-0">
        <label for="year" class="form-label mb-0">Year</label>
        <input type="number" name="year" id="year" value="@s.Year"
               class="form-control form-control-sm" style="width: 6rem;" />
        <button type="submit" class="btn btn-sm btn-secondary">Go</button>
    </form>
</div>

<vc:temp-data-alerts />

@* =====================  By camp  ===================== *@
<section class="card mb-4">
    <div class="card-header d-flex justify-content-between align-items-center">
        <h2 class="h5 mb-0">By camp</h2>
        <div class="d-flex gap-2 align-items-center">
            <label for="paid-filter" class="form-label mb-0 small">Show</label>
            <select id="paid-filter" class="form-select form-select-sm" style="width: 9rem;">
                <option value="all" selected>All</option>
                <option value="paid">Paid</option>
                <option value="partial">Partial</option>
                <option value="unpaid">Unpaid</option>
            </select>
        </div>
    </div>
    <div class="card-body p-0">
        @if (s.ByCamp.Count == 0)
        {
            <p class="text-muted m-3 mb-3">No orders for @s.Year.</p>
        }
        else
        {
            <table class="table table-sm mb-0" id="by-camp-table">
                <thead>
                    <tr>
                        <th>Camp</th>
                        <th>Label</th>
                        <th>State</th>
                        <th class="text-end">Total due (€)</th>
                        <th class="text-end">Paid (€)</th>
                        <th class="text-end">Balance (€)</th>
                    </tr>
                </thead>
                <tbody>
                @foreach (var row in s.ByCamp)
                {
                    string paidClass =
                        row.BalanceEur <= 0m ? "paid" :
                        row.PaymentsTotalEur > 0m ? "partial" : "unpaid";
                    <tr data-paid-status="@paidClass">
                        <td>
                            <a asp-controller="Store" asp-action="Order" asp-route-id="@row.OrderId">
                                @row.CampName
                            </a>
                        </td>
                        <td>@row.Label</td>
                        <td>@row.State</td>
                        <td class="text-end">@row.TotalDueEur.ToString("F2")</td>
                        <td class="text-end">@row.PaymentsTotalEur.ToString("F2")</td>
                        <td class="text-end">@row.BalanceEur.ToString("F2")</td>
                    </tr>
                }
                </tbody>
            </table>
        }
    </div>
</section>

@* =====================  By item  ===================== *@
<section class="card mb-4">
    <div class="card-header"><h2 class="h5 mb-0">By item</h2></div>
    <div class="card-body p-0">
        @if (s.ByItem.Count == 0)
        {
            <p class="text-muted m-3 mb-3">No lines for @s.Year.</p>
        }
        else
        {
            <table class="table table-sm mb-0">
                <thead>
                    <tr>
                        <th>Product</th>
                        <th class="text-end">Total qty</th>
                        <th class="text-end">Total revenue (€)</th>
                    </tr>
                </thead>
                <tbody>
                @foreach (var row in s.ByItem)
                {
                    <tr>
                        <td>@row.ProductName</td>
                        <td class="text-end">@row.TotalQty</td>
                        <td class="text-end">@row.TotalRevenueEur.ToString("F2")</td>
                    </tr>
                }
                </tbody>
            </table>
        }
    </div>
</section>

@* =====================  Cross-tab  ===================== *@
<section class="card mb-4">
    <div class="card-header"><h2 class="h5 mb-0">Camps × products (qty)</h2></div>
    <div class="card-body p-0 table-responsive">
        @if (s.CrossTab.Camps.Count == 0 || s.CrossTab.Products.Count == 0)
        {
            <p class="text-muted m-3 mb-3">Nothing to plot for @s.Year.</p>
        }
        else
        {
            <table class="table table-sm table-bordered mb-0">
                <thead>
                    <tr>
                        <th>Camp</th>
                        @foreach (var col in s.CrossTab.Products)
                        {
                            <th class="text-end">@col.ProductName</th>
                        }
                        <th class="text-end">Total</th>
                    </tr>
                </thead>
                <tbody>
                @foreach (var camp in s.CrossTab.Camps)
                {
                    <tr>
                        <td>@camp.CampName</td>
                        @foreach (var col in s.CrossTab.Products)
                        {
                            var qty = camp.QtyByProduct.TryGetValue(col.ProductId, out var q) ? q : 0;
                            <td class="text-end">@(qty == 0 ? "" : qty.ToString())</td>
                        }
                        <td class="text-end fw-semibold">@camp.TotalQty</td>
                    </tr>
                }
                </tbody>
                <tfoot>
                    <tr class="fw-semibold">
                        <td>Total</td>
                        @foreach (var col in s.CrossTab.Products)
                        {
                            <td class="text-end">@col.TotalQty</td>
                        }
                        <td class="text-end">@s.CrossTab.Products.Sum(c => c.TotalQty)</td>
                    </tr>
                </tfoot>
            </table>
        }
    </div>
</section>

<script>
    (function () {
        const select = document.getElementById('paid-filter');
        if (!select) return;
        select.addEventListener('change', function () {
            const v = select.value;
            document.querySelectorAll('#by-camp-table tbody tr').forEach(tr => {
                tr.style.display = (v === 'all' || tr.dataset.paidStatus === v) ? '' : 'none';
            });
        });
    })();
</script>
```

- [ ] **Step 2: Build to verify the Razor compiles**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj -v quiet`
Expected: build succeeds with no Razor errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/StoreAdmin/Summary.cshtml
git commit -m "Add /Store/Admin/Summary Razor view with three projections"
```

---

## Task 6 — Admin sidebar entry

**Files:**
- Modify: `src/Humans.Web/ViewComponents/AdminNavTree.cs`

- [ ] **Step 1: Add the nav entry**

In `src/Humans.Web/ViewComponents/AdminNavTree.cs`, locate the `new("Money", [...])` group (around lines 30–33). Add a second entry to it so the group becomes:

```csharp
new("Money", [
    new("Finance",        "Finance",      "Index",   null, null, "fa-solid fa-coins",        PolicyNames.FinanceAdminOrAdmin),
    new("Store catalog",  "StoreAdmin",   "Catalog", null, null, "fa-solid fa-tags",         PolicyNames.StoreCatalogAdmin),
    new("Store summary",  "StoreAdmin",   "Summary", null, null, "fa-solid fa-chart-column", PolicyNames.StoreCatalogAdmin)
]),
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/ViewComponents/AdminNavTree.cs
git commit -m "Add Store summary entry to admin nav"
```

---

## Task 7 — Integration test for the controller action

**Files:**
- Create: `tests/Humans.Integration.Tests/Controllers/StoreSummaryControllerTests.cs`

- [ ] **Step 1: Write the integration test**

Create `tests/Humans.Integration.Tests/Controllers/StoreSummaryControllerTests.cs`:

```csharp
using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

public class StoreSummaryControllerTests(HumansWebApplicationFactory factory)
    : IntegrationTestBase(factory)
{
    [HumansFact(Timeout = 60000)]
    public async Task Volunteer_GET_admin_summary_returns_403_or_redirect()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var resp = await Client.GetAsync("/Store/Admin/Summary");

        ((int)resp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Forbidden,
            (int)HttpStatusCode.Found,
            (int)HttpStatusCode.Redirect);
    }

    [HumansFact(Timeout = 60000)]
    public async Task StoreAdmin_GET_admin_summary_returns_200_with_three_sections()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("store-admin"));

        var resp = await Client.GetAsync("/Store/Admin/Summary");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("By camp");
        body.Should().Contain("By item");
        body.Should().Contain("Camps × products");
    }
}
```

(Sufficient: one negative-auth case + one positive-render case. Admin and FinanceAdmin reach the same policy gate as StoreAdmin; no need for separate role tests.)

- [ ] **Step 2: Run the new tests**

Run: `dotnet test tests/Humans.Integration.Tests/Humans.Integration.Tests.csproj --filter FullyQualifiedName~StoreSummaryControllerTests -v quiet`
Expected: both tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Integration.Tests/Controllers/StoreSummaryControllerTests.cs
git commit -m "Add integration tests for /Store/Admin/Summary"
```

---

## Task 8 — Section doc update

**Files:**
- Modify: `docs/sections/Store.md`

- [ ] **Step 1: Flip the stub note to live**

In `docs/sections/Store.md`:

Find the `## Routing` section and change:
```
- `/Store/Summary` — FinanceAdmin per-camp + per-item summary. **Not yet implemented (Phase 5 stub).**
```
to:
```
- `/Store/Admin/Summary` — FinanceAdmin/StoreAdmin/Admin aggregate report: by-camp, by-item, camps × products cross-tab for a given year. Reuses `PolicyNames.StoreCatalogAdmin`.
```

Find the bottom paragraph ("Implementation status: ...") and remove `GetAllOrderSummariesAsync` from the list of methods that throw `NotSupportedException`.

- [ ] **Step 2: Commit**

```bash
git add docs/sections/Store.md
git commit -m "Update Store section doc: /Store/Admin/Summary is live"
```

---

## Final validation

- [ ] **Step 1: Full solution build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: zero errors, zero warnings introduced.

- [ ] **Step 2: Full test pass**

Run: `dotnet test Humans.slnx -v quiet`
Expected: green.

- [ ] **Step 3: Manual smoke (recommended)**

Start the app locally:
```bash
dotnet run --project src/Humans.Web
```
Sign in as an Admin user, navigate to `/Store/Admin/Summary` via the admin sidebar (Money → Store summary). Verify:
- The page renders with three sections.
- Year selector defaults to the active event year.
- Paid-status dropdown filters by-camp rows correctly.
- Cross-tab cells line up with by-item totals.

- [ ] **Step 4: Push and open PR**

```bash
git push
gh pr create --base main --title "feat(store): admin aggregate summary views" \
   --body "Closes the /Store/Summary Phase 5 stub. Spec: docs/superpowers/specs/2026-05-18-store-summary-aggregates-design.md"
```
