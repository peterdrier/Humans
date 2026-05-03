# Store Section Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the `/Store` section per `docs/superpowers/specs/2026-04-30-store-section-design.md` — per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance.

**Architecture:** New `Store` section owns its data end-to-end. Existing `IStripeService` and `HoldedClient` connectors are extended (no new connector classes). Phases ship as independent PRs and stack on each other; each phase is testable on its own.

**Tech Stack:** .NET 10, ASP.NET Core MVC, EF Core (Postgres), NodaTime, Hangfire, xUnit, AwesomeAssertions, Stripe SDK, Holded HTTP API.

---

## Phase Map

Each phase = one PR to `peterdrier/Humans:main`. Push after each phase. Phases are ordered by dependency.

| # | Phase | Headline outcome |
|---|---|---|
| 1 | Foundation | Domain entities, enums, EF migration, `StoreAdmin` role, empty service skeleton, section invariants doc. No UI. Builds + tests green. |
| 2 | Camp Lead order UX | `/Store` browse + add/remove lines + balance display. No payments yet. |
| 3 | Catalog admin | `/Store/Admin/Catalog` CRUD for `StoreProduct` (StoreAdmin role). |
| 4 | Holded probe + connector extension | Richer probe committed; `HoldedClient` gains contact upsert + invoice POST + treasury list. No call sites yet. |
| 5 | Invoice issuance | `/Store/Admin/Orders` + "Issue invoice" / "Issue all" + `/Store/Summary`. |
| 6 | Stripe checkout + webhook | Pay button on `/Store`; checkout session creation; webhook ingestion. |
| 7 | Treasury sync job | `StoreTreasurySyncJob` polls Holded treasury, auto-matches by Order Label. |

**Pre-execution checklist:**
- [ ] Pull the latest `origin/main` into the worktree and rebase if needed.
- [ ] Confirm `STRIPE_WEBHOOK_SECRET` and `HOLDED_API_KEY` already exist in dev secrets (they should — both connectors are configured).

**Standing project rules every phase must obey** (memory + CLAUDE.md):
- Never hand-edit migrations. Use `dotnet ef migrations add ...`. Run `.claude/agents/ef-migration-reviewer.md` before committing migrations.
- No startup guards. The app must always boot.
- No concurrency tokens.
- All dates/times use NodaTime (`Instant`, `LocalDate`).
- `dotnet build -v quiet`, `dotnet test -v quiet`. Don't pipe through `tail`/`head`/`grep`.
- Don't combine `cd && cmd` in one Bash call.
- Don't drop hard storage in the same PR that ships its replacement (not relevant here, but always true).
- Push after each phase (3–5 task batches).

---

## Phase 1 — Foundation

**PR title:** `feat(store): foundation — domain, migration, role, service skeleton (peterdrier#TBD)`

**Branch:** `store-foundation`

**Files:**
- Create: `src/Humans.Domain/Entities/StoreProduct.cs`
- Create: `src/Humans.Domain/Entities/StoreOrder.cs`
- Create: `src/Humans.Domain/Entities/StoreOrderLine.cs`
- Create: `src/Humans.Domain/Entities/StorePayment.cs`
- Create: `src/Humans.Domain/Entities/StoreInvoice.cs`
- Create: `src/Humans.Domain/Entities/StoreTreasurySyncState.cs`
- Create: `src/Humans.Domain/Enums/StoreOrderState.cs`
- Create: `src/Humans.Domain/Enums/StorePaymentMethod.cs`
- Modify: `src/Humans.Domain/Constants/RoleNames.cs` (add `StoreAdmin` constant; add to `BoardManageableRoles`)
- Create: `src/Humans.Application/Interfaces/Store/IStoreService.cs`
- Create: `src/Humans.Application/Interfaces/Store/IStoreRepository.cs`
- Create: `src/Humans.Application/Services/Store/StoreService.cs` (skeleton — methods throw NotImplementedException)
- Create: `src/Humans.Application/Services/Store/Dtos/OrderDto.cs`
- Create: `src/Humans.Application/Services/Store/Dtos/OrderLineDto.cs`
- Create: `src/Humans.Application/Services/Store/Dtos/ProductDto.cs`
- Create: `src/Humans.Application/Services/Store/Dtos/OrderSummaryDto.cs`
- Create: `src/Humans.Infrastructure/Repositories/StoreRepository.cs` (skeleton)
- Create: `src/Humans.Infrastructure/Data/Configurations/Store/StoreProductConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderLineConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Store/StorePaymentConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Store/StoreInvoiceConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Store/StoreTreasurySyncStateConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs` (add `DbSet<>` properties)
- Modify: `src/Humans.Web/Extensions/InfrastructureExtensions.cs` (or wherever DI binds repos/services — register `IStoreService`, `IStoreRepository`)
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddStoreSection.cs` (auto-generated)
- Create: `docs/sections/Store.md`
- Modify: `docs/architecture/freshness-catalog.yml` (add Store entry)
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTests.cs` (skeleton)
- Test: `tests/Humans.Infrastructure.Tests/Repositories/StoreRepositoryTests.cs` (skeleton)
- Test: `tests/Humans.Domain.Tests/Entities/StoreOrderTests.cs` (entity invariant tests)

### Task 1.1 — Branch + scaffolding

- [ ] **Step 1: Verify clean state and create branch**

```bash
git fetch origin --quiet
git worktree add .worktrees/store-foundation -b store-foundation origin/main
cd /h/source/Humans/.worktrees/store-foundation
```

- [ ] **Step 2: Confirm baseline build is green**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded. 0 errors.

### Task 1.2 — Add `StoreOrderState` and `StorePaymentMethod` enums

- [ ] **Step 1: Create the enum files**

`src/Humans.Domain/Enums/StoreOrderState.cs`:

```csharp
namespace Humans.Domain.Enums;

public enum StoreOrderState
{
    Open = 0,
    InvoiceIssued = 1
}
```

`src/Humans.Domain/Enums/StorePaymentMethod.cs`:

```csharp
namespace Humans.Domain.Enums;

public enum StorePaymentMethod
{
    Stripe = 0,
    BankTransfer = 1,
    Manual = 2
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded.

### Task 1.3 — Add the six entity classes

- [ ] **Step 1: Create `StoreProduct`**

`src/Humans.Domain/Entities/StoreProduct.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreProduct
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal UnitPriceEur { get; set; }
    public decimal VatRatePercent { get; set; }
    public decimal? DepositAmountEur { get; set; }
    public LocalDate OrderableUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
}
```

> **Year-handle decision (deferred in spec):** Using a plain `int Year` for now. If the codebase needs to attach to `CampSettings.PublicYear` or a `CampSeason.Year` semantically, that's a constraint enforced at write time, not a FK — keeps Store independent of Camp's storage shape.

- [ ] **Step 2: Create `StoreOrder`**

`src/Humans.Domain/Entities/StoreOrder.cs`:

```csharp
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreOrder
{
    public Guid Id { get; set; }
    public Guid CampSeasonId { get; set; }
    public string? Label { get; set; }
    public StoreOrderState State { get; set; } = StoreOrderState.Open;

    public string? CounterpartyName { get; set; }
    public string? CounterpartyVatId { get; set; }
    public string? CounterpartyAddress { get; set; }
    public string? CounterpartyCountryCode { get; set; }
    public string? CounterpartyEmail { get; set; }

    public Guid? IssuedInvoiceId { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public ICollection<StoreOrderLine> Lines { get; set; } = new List<StoreOrderLine>();
    public ICollection<StorePayment> Payments { get; set; } = new List<StorePayment>();
}
```

- [ ] **Step 3: Create `StoreOrderLine`**

`src/Humans.Domain/Entities/StoreOrderLine.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreOrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Qty { get; set; }
    public decimal UnitPriceSnapshot { get; set; }
    public decimal VatRateSnapshot { get; set; }
    public decimal? DepositAmountSnapshot { get; set; }
    public Instant AddedAt { get; set; }
    public Guid AddedByUserId { get; set; }

    public StoreOrder? Order { get; set; }
}
```

- [ ] **Step 4: Create `StorePayment`**

`src/Humans.Domain/Entities/StorePayment.cs`:

```csharp
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StorePayment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public decimal AmountEur { get; set; }
    public StorePaymentMethod Method { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? ExternalRef { get; set; }
    public Instant ReceivedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    public StoreOrder? Order { get; set; }
}
```

- [ ] **Step 5: Create `StoreInvoice`**

`src/Humans.Domain/Entities/StoreInvoice.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreInvoice
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string HoldedDocId { get; set; } = string.Empty;
    public string HoldedDocNumber { get; set; } = string.Empty;
    public Instant IssuedAt { get; set; }
    public Guid IssuedByUserId { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}
```

- [ ] **Step 6: Create `StoreTreasurySyncState`**

`src/Humans.Domain/Entities/StoreTreasurySyncState.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreTreasurySyncState
{
    public int Id { get; set; } = 1;
    public Instant? LastSyncAt { get; set; }
    public string SyncStatus { get; set; } = "Idle";
    public string? LastError { get; set; }
}
```

- [ ] **Step 7: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded.

### Task 1.4 — Add `StoreAdmin` role

- [ ] **Step 1: Modify `RoleNames.cs`**

Add after the `FinanceAdmin` block:

```csharp
    /// <summary>
    /// Store Administrator — manages the Store catalog (products, prices, VAT, deposits, deadlines).
    /// </summary>
    public const string StoreAdmin = "StoreAdmin";
```

Add `StoreAdmin` to the `BoardManageableRoles` set.

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded.

### Task 1.5 — Add EF configurations

- [ ] **Step 1: Create the configuration files**

Pattern for each: implements `IEntityTypeConfiguration<T>`, sets table name (snake_case plural), declares keys, indexes, value conversions for NodaTime where needed (the project has existing `Instant`/`LocalDate` conventions — match the `BudgetConfiguration` style for reference).

`src/Humans.Infrastructure/Data/Configurations/Store/StoreProductConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreProductConfiguration : IEntityTypeConfiguration<StoreProduct>
{
    public void Configure(EntityTypeBuilder<StoreProduct> b)
    {
        b.ToTable("store_products");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000).IsRequired();
        b.Property(x => x.UnitPriceEur).HasColumnType("numeric(12,2)");
        b.Property(x => x.VatRatePercent).HasColumnType("numeric(5,2)");
        b.Property(x => x.DepositAmountEur).HasColumnType("numeric(12,2)");
        b.HasIndex(x => new { x.Year, x.IsActive });
    }
}
```

`src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreOrderConfiguration : IEntityTypeConfiguration<StoreOrder>
{
    public void Configure(EntityTypeBuilder<StoreOrder> b)
    {
        b.ToTable("store_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(100);
        b.Property(x => x.CounterpartyName).HasMaxLength(200);
        b.Property(x => x.CounterpartyVatId).HasMaxLength(50);
        b.Property(x => x.CounterpartyAddress).HasMaxLength(500);
        b.Property(x => x.CounterpartyCountryCode).HasMaxLength(2);
        b.Property(x => x.CounterpartyEmail).HasMaxLength(320);
        b.Property(x => x.State).HasConversion<int>();
        b.HasIndex(x => x.CampSeasonId);
        b.HasIndex(x => x.State);
        b.HasMany(x => x.Lines).WithOne(l => l.Order!).HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Payments).WithOne(p => p.Order!).HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
```

`src/Humans.Infrastructure/Data/Configurations/Store/StoreOrderLineConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreOrderLineConfiguration : IEntityTypeConfiguration<StoreOrderLine>
{
    public void Configure(EntityTypeBuilder<StoreOrderLine> b)
    {
        b.ToTable("store_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.UnitPriceSnapshot).HasColumnType("numeric(12,2)");
        b.Property(x => x.VatRateSnapshot).HasColumnType("numeric(5,2)");
        b.Property(x => x.DepositAmountSnapshot).HasColumnType("numeric(12,2)");
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.ProductId);
    }
}
```

`src/Humans.Infrastructure/Data/Configurations/Store/StorePaymentConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StorePaymentConfiguration : IEntityTypeConfiguration<StorePayment>
{
    public void Configure(EntityTypeBuilder<StorePayment> b)
    {
        b.ToTable("store_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.AmountEur).HasColumnType("numeric(12,2)");
        b.Property(x => x.Method).HasConversion<int>();
        b.Property(x => x.StripePaymentIntentId).HasMaxLength(200);
        b.Property(x => x.ExternalRef).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.StripePaymentIntentId).IsUnique().HasFilter("\"StripePaymentIntentId\" IS NOT NULL");
    }
}
```

`src/Humans.Infrastructure/Data/Configurations/Store/StoreInvoiceConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreInvoiceConfiguration : IEntityTypeConfiguration<StoreInvoice>
{
    public void Configure(EntityTypeBuilder<StoreInvoice> b)
    {
        b.ToTable("store_invoices");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.OrderId).IsUnique();
        b.HasIndex(x => x.HoldedDocId).IsUnique();
        b.Property(x => x.HoldedDocId).HasMaxLength(100);
        b.Property(x => x.HoldedDocNumber).HasMaxLength(50);
        b.Property(x => x.RequestPayload).HasColumnType("jsonb");
        b.Property(x => x.ResponsePayload).HasColumnType("jsonb");
    }
}
```

`src/Humans.Infrastructure/Data/Configurations/Store/StoreTreasurySyncStateConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Store;

public class StoreTreasurySyncStateConfiguration : IEntityTypeConfiguration<StoreTreasurySyncState>
{
    public void Configure(EntityTypeBuilder<StoreTreasurySyncState> b)
    {
        b.ToTable("store_treasury_sync_state");
        b.HasKey(x => x.Id);
        b.Property(x => x.SyncStatus).HasMaxLength(20);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
```

- [ ] **Step 2: Add DbSets to `HumansDbContext`**

Modify `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add:

```csharp
public DbSet<StoreProduct> StoreProducts => Set<StoreProduct>();
public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();
public DbSet<StoreOrderLine> StoreOrderLines => Set<StoreOrderLine>();
public DbSet<StorePayment> StorePayments => Set<StorePayment>();
public DbSet<StoreInvoice> StoreInvoices => Set<StoreInvoice>();
public DbSet<StoreTreasurySyncState> StoreTreasurySyncStates => Set<StoreTreasurySyncState>();
```

The `OnModelCreating` `ApplyConfigurationsFromAssembly` call should auto-pick the new configurations — verify by reading the existing method.

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded.

### Task 1.6 — Generate the migration

- [ ] **Step 1: Add the migration**

Project folders may differ — verify the EF startup project name first. Then:

```bash
dotnet ef migrations add AddStoreSection \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web \
  --output-dir Migrations
```

Expected: Migration files created in `src/Humans.Infrastructure/Migrations/`.

- [ ] **Step 2: Read the migration to confirm only Store tables are added**

Open the new migration file. Verify: only `store_*` table creations + indexes + the seed for `store_treasury_sync_state` if EF emitted one.

> **HARD RULE:** Do not hand-edit the migration. If the migration includes anything unexpected (column changes on unrelated tables), stop and investigate why — don't paper over with edits.

- [ ] **Step 3: Run the EF migration reviewer agent**

```
Use the agent at .claude/agents/ef-migration-reviewer.md, asking it to review
the new migration. Do not commit until it passes with no CRITICAL issues.
```

- [ ] **Step 4: Apply migration to dev DB**

```bash
dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

Expected: Done.

- [ ] **Step 5: Build + test**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Expected: green.

### Task 1.7 — Add service skeleton

- [ ] **Step 1: Create the DTOs**

`src/Humans.Application/Services/Store/Dtos/ProductDto.cs`:

```csharp
using NodaTime;

namespace Humans.Application.Services.Store.Dtos;

public record ProductDto(
    Guid Id,
    int Year,
    string Name,
    string Description,
    decimal UnitPriceEur,
    decimal VatRatePercent,
    decimal? DepositAmountEur,
    LocalDate OrderableUntil,
    bool IsActive);
```

`src/Humans.Application/Services/Store/Dtos/OrderLineDto.cs`:

```csharp
using NodaTime;

namespace Humans.Application.Services.Store.Dtos;

public record OrderLineDto(
    Guid Id,
    Guid OrderId,
    Guid ProductId,
    string ProductName,
    int Qty,
    decimal UnitPriceSnapshot,
    decimal VatRateSnapshot,
    decimal? DepositAmountSnapshot,
    Instant AddedAt);
```

`src/Humans.Application/Services/Store/Dtos/OrderDto.cs`:

```csharp
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public record OrderDto(
    Guid Id,
    Guid CampSeasonId,
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

`src/Humans.Application/Services/Store/Dtos/OrderSummaryDto.cs`:

```csharp
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store.Dtos;

public record OrderSummaryDto(
    Guid OrderId,
    Guid CampSeasonId,
    string CampName,
    string? Label,
    StoreOrderState State,
    decimal TotalDueEur,
    decimal PaymentsTotalEur,
    decimal BalanceEur);
```

- [ ] **Step 2: Create `IStoreRepository`**

`src/Humans.Application/Interfaces/Store/IStoreRepository.cs`:

```csharp
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Store;

public interface IStoreRepository
{
    // Products
    Task<IReadOnlyList<StoreProduct>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default);
    Task<StoreProduct?> GetProductByIdAsync(Guid productId, CancellationToken ct = default);
    Task AddProductAsync(StoreProduct product, CancellationToken ct = default);
    Task UpdateProductAsync(StoreProduct product, CancellationToken ct = default);

    // Orders
    Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<StoreOrder?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default);
    Task<StoreOrder?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<StoreOrder>> GetAllOrdersWithAggregatesAsync(int year, CancellationToken ct = default);
    Task AddOrderAsync(StoreOrder order, CancellationToken ct = default);
    Task UpdateOrderAsync(StoreOrder order, CancellationToken ct = default);

    // Lines
    Task AddLineAsync(StoreOrderLine line, CancellationToken ct = default);
    Task RemoveLineAsync(Guid lineId, CancellationToken ct = default);

    // Payments
    Task AddPaymentAsync(StorePayment payment, CancellationToken ct = default);
    Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default);

    // Invoices
    Task AddInvoiceAsync(StoreInvoice invoice, CancellationToken ct = default);

    // Treasury sync state
    Task<StoreTreasurySyncState> GetOrCreateTreasurySyncStateAsync(CancellationToken ct = default);
    Task UpdateTreasurySyncStateAsync(StoreTreasurySyncState state, CancellationToken ct = default);
}
```

- [ ] **Step 3: Create `IStoreService`**

`src/Humans.Application/Interfaces/Store/IStoreService.cs`:

```csharp
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Store;

public interface IStoreService
{
    // Catalog (read)
    Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default);

    // Catalog (write — StoreAdmin)
    Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default);
    Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default);
    Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default);

    // Orders (camp lead)
    Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default);
    Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default);
    Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default);
    Task RemoveLineAsync(Guid lineId, Guid actorUserId, CancellationToken ct = default);

    // Counterparty (camp lead pre-issuance, FinanceAdmin always)
    Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default);

    // Payments (FinanceAdmin)
    Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default);

    // Invoice issuance (FinanceAdmin) — implemented in Phase 5
    Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default);

    // Summary
    Task<IReadOnlyList<OrderSummaryDto>> GetAllOrderSummariesAsync(int year, CancellationToken ct = default);
}

public record OrderCounterpartyInput(
    string? Name,
    string? VatId,
    string? Address,
    string? CountryCode,
    string? Email);
```

- [ ] **Step 4: Skeleton `StoreService`**

`src/Humans.Application/Services/Store/StoreService.cs`:

```csharp
using Humans.Application.Interfaces.Store;
using Humans.Application.Services.Store.Dtos;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Store;

public class StoreService : IStoreService
{
    private readonly IStoreRepository _repo;

    public StoreService(IStoreRepository repo) => _repo = repo;

    public Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task<Guid> CreateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 3");

    public Task UpdateProductAsync(ProductDto draft, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 3");

    public Task DeactivateProductAsync(Guid productId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 3");

    public Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task RemoveLineAsync(Guid lineId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 2");

    public Task RecordManualPaymentAsync(Guid orderId, decimal amountEur, StorePaymentMethod method, string? externalRef, string? notes, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 5");

    public Task IssueInvoiceAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 5");

    public Task<IReadOnlyList<OrderSummaryDto>> GetAllOrderSummariesAsync(int year, CancellationToken ct = default)
        => throw new NotImplementedException("Phase 5");
}
```

- [ ] **Step 5: Skeleton `StoreRepository`**

`src/Humans.Infrastructure/Repositories/StoreRepository.cs` — implement every method by reading/writing the relevant `DbSet<>`. Use existing repos as a pattern (`HoldedRepository.cs`, `BudgetRepository.cs`). Materialize results — no `IQueryable` returned.

Minimum code (all methods implemented; full implementations are mechanical):

```csharp
using Humans.Application.Interfaces.Store;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories;

public class StoreRepository : IStoreRepository
{
    private readonly HumansDbContext _db;

    public StoreRepository(HumansDbContext db) => _db = db;

    public async Task<IReadOnlyList<StoreProduct>> GetActiveProductsForYearAsync(int year, CancellationToken ct = default)
        => await _db.StoreProducts.AsNoTracking()
            .Where(p => p.Year == year && p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

    public Task<StoreProduct?> GetProductByIdAsync(Guid productId, CancellationToken ct = default)
        => _db.StoreProducts.FirstOrDefaultAsync(p => p.Id == productId, ct);

    public async Task AddProductAsync(StoreProduct product, CancellationToken ct = default)
    {
        _db.StoreProducts.Add(product);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateProductAsync(StoreProduct product, CancellationToken ct = default)
    {
        _db.StoreProducts.Update(product);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StoreOrder>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
        => await _db.StoreOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .Where(o => o.CampSeasonId == campSeasonId)
            .ToListAsync(ct);

    public Task<StoreOrder?> GetOrderByIdAsync(Guid orderId, CancellationToken ct = default)
        => _db.StoreOrders.FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public Task<StoreOrder?> GetOrderWithLinesAndPaymentsAsync(Guid orderId, CancellationToken ct = default)
        => _db.StoreOrders
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

    public async Task<IReadOnlyList<StoreOrder>> GetAllOrdersWithAggregatesAsync(int year, CancellationToken ct = default)
    {
        // Year filter hop: Order has no Year directly; filter via product Year on first line, OR
        // accept current implementation: return all orders, caller filters. Simpler at this scale.
        return await _db.StoreOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct);
    }

    public async Task AddOrderAsync(StoreOrder order, CancellationToken ct = default)
    {
        _db.StoreOrders.Add(order);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateOrderAsync(StoreOrder order, CancellationToken ct = default)
    {
        _db.StoreOrders.Update(order);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddLineAsync(StoreOrderLine line, CancellationToken ct = default)
    {
        _db.StoreOrderLines.Add(line);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveLineAsync(Guid lineId, CancellationToken ct = default)
    {
        var line = await _db.StoreOrderLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null) return;
        _db.StoreOrderLines.Remove(line);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddPaymentAsync(StorePayment payment, CancellationToken ct = default)
    {
        _db.StorePayments.Add(payment);
        await _db.SaveChangesAsync(ct);
    }

    public Task<bool> StripePaymentIntentExistsAsync(string paymentIntentId, CancellationToken ct = default)
        => _db.StorePayments.AnyAsync(p => p.StripePaymentIntentId == paymentIntentId, ct);

    public async Task AddInvoiceAsync(StoreInvoice invoice, CancellationToken ct = default)
    {
        _db.StoreInvoices.Add(invoice);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<StoreTreasurySyncState> GetOrCreateTreasurySyncStateAsync(CancellationToken ct = default)
    {
        var s = await _db.StoreTreasurySyncStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
        {
            s = new StoreTreasurySyncState { Id = 1 };
            _db.StoreTreasurySyncStates.Add(s);
            await _db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task UpdateTreasurySyncStateAsync(StoreTreasurySyncState state, CancellationToken ct = default)
    {
        _db.StoreTreasurySyncStates.Update(state);
        await _db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 6: Register in DI**

Find the project's DI extension method that wires repos + services (look for where `IBudgetRepository` / `IBudgetService` are registered — same pattern). Add:

```csharp
services.AddScoped<IStoreRepository, StoreRepository>();
services.AddScoped<IStoreService, StoreService>();
```

- [ ] **Step 7: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded.

### Task 1.8 — Section invariants doc

- [ ] **Step 1: Create `docs/sections/Store.md`**

Use `docs/sections/SECTION-TEMPLATE.md` as the base. Fill: Concepts, Data Model (per-entity tables, mirroring this plan), Actors & Roles, Invariants (lifted from spec's "Section invariants"), Negative Access Rules, Triggers (audit-log emissions), Cross-Section Dependencies, Architecture (owning service `IStoreService`, owned tables list, migration status A — owned end-to-end).

Top of file:

```markdown
<!-- freshness:triggers
  src/Humans.Application/Services/Store/**
  src/Humans.Application/Interfaces/Store/**
  src/Humans.Domain/Entities/Store*.cs
  src/Humans.Infrastructure/Data/Configurations/Store/**
  src/Humans.Infrastructure/Repositories/StoreRepository.cs
  src/Humans.Web/Controllers/Store*.cs
  src/Humans.Web/Authorization/Requirements/StoreOrder*.cs
-->
```

- [ ] **Step 2: Add Store entry to freshness catalog**

Modify `docs/architecture/freshness-catalog.yml` — add `docs/sections/Store.md` mirroring the entries for other section docs.

### Task 1.9 — Smoke tests

- [ ] **Step 1: Domain entity test**

`tests/Humans.Domain.Tests/Entities/StoreOrderTests.cs`:

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using AwesomeAssertions;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class StoreOrderTests
{
    [Fact]
    public void New_order_defaults_to_open_state()
    {
        var o = new StoreOrder();
        o.State.Should().Be(StoreOrderState.Open);
        o.Lines.Should().BeEmpty();
        o.Payments.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Repository round-trip test**

Use the project's existing integration-test fixture (look for `HumansWebApplicationFactory` and how `BudgetRepositoryTests` use it). Write one test that creates a product, fetches it back, asserts equality.

- [ ] **Step 3: Run tests**

```bash
dotnet test Humans.slnx -v quiet
```

Expected: green.

### Task 1.10 — Commit + push + PR

- [ ] **Step 1: Stage**

```bash
git add src/ tests/ docs/sections/Store.md docs/architecture/freshness-catalog.yml
git status
```

- [ ] **Step 2: Commit**

```bash
git commit -m "feat(store): foundation — entities, migration, role, service skeleton

Adds the Store section's domain entities, EF migration, StoreAdmin role,
and skeleton service/repository surface. No UI yet — Phase 2 wires up the
camp-lead order flow.

Refs design: docs/superpowers/specs/2026-04-30-store-section-design.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 3: Push**

```bash
git push -u origin store-foundation
```

- [ ] **Step 4: Open PR**

```bash
gh pr create --repo peterdrier/Humans --base main --head store-foundation \
  --title "feat(store): foundation — entities, migration, role, service skeleton" \
  --body "Phase 1 of the /Store section per docs/superpowers/specs/2026-04-30-store-section-design.md.

## Summary
- 6 new entities (StoreProduct, StoreOrder, StoreOrderLine, StorePayment, StoreInvoice, StoreTreasurySyncState)
- 2 new enums (StoreOrderState, StorePaymentMethod)
- New StoreAdmin role
- IStoreService + IStoreRepository (skeleton — methods throw NotImplementedException, filled in Phases 2–7)
- EF migration AddStoreSection (auto-generated, EF migration reviewer passed)
- Section invariants doc

## Test plan
- [ ] CI green on peterdrier/Humans
- [ ] Migration applies cleanly to preview DB
- [ ] No new orphan pages (no UI yet, expected)"
```

---

## Phase 2 — Camp Lead order UX

**PR title:** `feat(store): camp-lead order UX — browse, add/remove lines, balance`

**Branch:** `store-camp-lead`

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs` — implement read paths, order creation, line add/remove, balance calculation, counterparty edit.
- Create: `src/Humans.Application/Services/Store/BalanceCalculator.cs` — pure calculator, testable in isolation.
- Create: `src/Humans.Web/Controllers/StoreController.cs`
- Create: `src/Humans.Web/Authorization/Requirements/StoreOrderAuthorizationHandler.cs`
- Create: `src/Humans.Web/Authorization/Requirements/StoreOrderOperationRequirement.cs`
- Create: `src/Humans.Web/Views/Store/Index.cshtml` — catalog + camp's orders
- Create: `src/Humans.Web/Views/Store/Order.cshtml` — single order detail
- Modify: `src/Humans.Web/Views/Shared/_Nav*.cshtml` — add `/Store` link visible to camp leads
- Test: `tests/Humans.Application.Tests/Services/Store/BalanceCalculatorTests.cs`
- Test: `tests/Humans.Application.Tests/Services/Store/StoreServiceTests.cs` — fill out
- Test: `tests/Humans.Web.Tests/Controllers/StoreControllerTests.cs` (or integration test pattern)

### Task 2.1 — Branch from `store-foundation`

- [ ] **Step 1: Create branch**

```bash
cd /h/source/Humans
git fetch origin --quiet
git worktree add .worktrees/store-camp-lead -b store-camp-lead origin/store-foundation
cd /h/source/Humans/.worktrees/store-camp-lead
```

(Phase 2 stacks on Phase 1's branch. After Phase 1 merges, rebase onto `origin/main`.)

### Task 2.2 — `BalanceCalculator` (TDD)

- [ ] **Step 1: Write the failing tests**

`tests/Humans.Application.Tests/Services/Store/BalanceCalculatorTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Services.Store;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Store;

public class BalanceCalculatorTests
{
    [Fact]
    public void Empty_order_has_zero_balance()
    {
        var order = new StoreOrder();
        var result = BalanceCalculator.Compute(order);
        result.LinesSubtotalEur.Should().Be(0);
        result.VatTotalEur.Should().Be(0);
        result.DepositTotalEur.Should().Be(0);
        result.PaymentsTotalEur.Should().Be(0);
        result.BalanceEur.Should().Be(0);
    }

    [Fact]
    public void Single_line_with_21_percent_vat()
    {
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Qty = 2, UnitPriceSnapshot = 50m, VatRateSnapshot = 21m }
            }
        };

        var r = BalanceCalculator.Compute(order);
        r.LinesSubtotalEur.Should().Be(100m);
        r.VatTotalEur.Should().Be(21m);
        r.DepositTotalEur.Should().Be(0m);
        r.BalanceEur.Should().Be(121m);
    }

    [Fact]
    public void Deposit_lines_excluded_from_vat_added_to_total()
    {
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Qty = 1, UnitPriceSnapshot = 30m, VatRateSnapshot = 21m, DepositAmountSnapshot = 100m }
            }
        };

        var r = BalanceCalculator.Compute(order);
        r.LinesSubtotalEur.Should().Be(30m);
        r.VatTotalEur.Should().Be(6.30m);
        r.DepositTotalEur.Should().Be(100m);
        r.BalanceEur.Should().Be(136.30m);
    }

    [Fact]
    public void Payments_reduce_balance_negative_payment_treated_as_refund()
    {
        var order = new StoreOrder
        {
            Lines = new List<StoreOrderLine>
            {
                new() { Qty = 1, UnitPriceSnapshot = 100m, VatRateSnapshot = 21m }
            },
            Payments = new List<StorePayment>
            {
                new() { AmountEur = 50m, Method = StorePaymentMethod.Stripe },
                new() { AmountEur = -10m, Method = StorePaymentMethod.Manual }
            }
        };

        var r = BalanceCalculator.Compute(order);
        r.PaymentsTotalEur.Should().Be(40m);
        r.BalanceEur.Should().Be(81m);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~BalanceCalculatorTests"
```

Expected: compile failure or test failure.

- [ ] **Step 3: Implement `BalanceCalculator`**

`src/Humans.Application/Services/Store/BalanceCalculator.cs`:

```csharp
using Humans.Domain.Entities;

namespace Humans.Application.Services.Store;

public static class BalanceCalculator
{
    public record Result(
        decimal LinesSubtotalEur,
        decimal VatTotalEur,
        decimal DepositTotalEur,
        decimal PaymentsTotalEur,
        decimal BalanceEur);

    public static Result Compute(StoreOrder order)
    {
        decimal subtotal = 0m;
        decimal vat = 0m;
        decimal deposits = 0m;

        foreach (var line in order.Lines)
        {
            var lineSubtotal = line.Qty * line.UnitPriceSnapshot;
            subtotal += lineSubtotal;
            vat += Math.Round(lineSubtotal * line.VatRateSnapshot / 100m, 2, MidpointRounding.AwayFromZero);
            if (line.DepositAmountSnapshot is { } deposit)
                deposits += line.Qty * deposit;
        }

        var payments = order.Payments.Sum(p => p.AmountEur);
        var balance = subtotal + vat + deposits - payments;

        return new Result(subtotal, vat, deposits, payments, balance);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~BalanceCalculatorTests"
```

Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Store/ src/Humans.Application/Services/Store/BalanceCalculator.cs
git commit -m "feat(store): pure BalanceCalculator with VAT + deposit + signed payments"
```

### Task 2.3 — Implement `StoreService` read paths (TDD)

- [ ] **Step 1: Write tests for `GetActiveCatalogAsync`, `GetOrdersForCampSeasonAsync`, `GetOrderAsync`**

Use the existing service-test pattern (look at `BudgetServiceTests.cs`). Use Moq or NSubstitute (whichever the codebase uses) for `IStoreRepository`. Cover: empty catalog → empty list; multi-product catalog ordered by name; orders mapped to DTOs with balance via `BalanceCalculator`.

- [ ] **Step 2: Run to fail**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~StoreServiceTests"
```

Expected: NotImplementedException.

- [ ] **Step 3: Implement read methods**

In `StoreService.cs`, replace the three throws:

```csharp
public async Task<IReadOnlyList<ProductDto>> GetActiveCatalogAsync(int year, CancellationToken ct = default)
{
    var products = await _repo.GetActiveProductsForYearAsync(year, ct);
    return products.Select(MapProduct).ToList();
}

public async Task<IReadOnlyList<OrderDto>> GetOrdersForCampSeasonAsync(Guid campSeasonId, CancellationToken ct = default)
{
    var orders = await _repo.GetOrdersForCampSeasonAsync(campSeasonId, ct);
    var products = await _repo.GetActiveProductsForYearAsync(DateTime.UtcNow.Year, ct);
    var productNames = products.ToDictionary(p => p.Id, p => p.Name);
    return orders.Select(o => MapOrder(o, productNames)).ToList();
}

public async Task<OrderDto?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
{
    var o = await _repo.GetOrderWithLinesAndPaymentsAsync(orderId, ct);
    if (o is null) return null;
    var products = await _repo.GetActiveProductsForYearAsync(DateTime.UtcNow.Year, ct);
    var productNames = products.ToDictionary(p => p.Id, p => p.Name);
    return MapOrder(o, productNames);
}

private static ProductDto MapProduct(StoreProduct p) =>
    new(p.Id, p.Year, p.Name, p.Description, p.UnitPriceEur, p.VatRatePercent,
        p.DepositAmountEur, p.OrderableUntil, p.IsActive);

private static OrderDto MapOrder(StoreOrder o, IReadOnlyDictionary<Guid, string> productNames)
{
    var balance = BalanceCalculator.Compute(o);
    var lines = o.Lines.Select(l => new OrderLineDto(
        l.Id, l.OrderId, l.ProductId,
        productNames.GetValueOrDefault(l.ProductId, "(unknown product)"),
        l.Qty, l.UnitPriceSnapshot, l.VatRateSnapshot, l.DepositAmountSnapshot, l.AddedAt)).ToList();

    return new OrderDto(
        o.Id, o.CampSeasonId, o.Label, o.State,
        o.CounterpartyName, o.CounterpartyVatId, o.CounterpartyAddress, o.CounterpartyCountryCode, o.CounterpartyEmail,
        o.IssuedInvoiceId,
        lines,
        balance.LinesSubtotalEur, balance.VatTotalEur, balance.DepositTotalEur,
        balance.PaymentsTotalEur, balance.BalanceEur);
}
```

> **Year resolution simplification:** `DateTime.UtcNow.Year` is a placeholder for the "current event year". Acceptable for v1 since the year handle on `StoreProduct` is a plain int. If a `Year` entity gets introduced later, swap this for that.

- [ ] **Step 4: Run tests, verify pass, commit**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~StoreServiceTests"
git add src/ tests/
git commit -m "feat(store): implement service read paths with DTO mapping"
```

### Task 2.4 — Implement `CreateOrderAsync`, `AddLineAsync`, `RemoveLineAsync`, `UpdateCounterpartyAsync` (TDD)

- [ ] **Step 1: Write failing tests covering**

  - Creating an Order returns a new Guid; Order is `Open`; can be retrieved.
  - `AddLineAsync` snapshots product price/VAT/deposit at add time.
  - `AddLineAsync` rejects when `now > Product.OrderableUntil` (use a fake clock).
  - `AddLineAsync` rejects when Order state is `InvoiceIssued`.
  - `RemoveLineAsync` removes a line; rejected if past product's deadline; rejected if order is `InvoiceIssued`.
  - `UpdateCounterpartyAsync` updates fields on an `Open` order; rejected on issued order's lines/counterparty (counterparty edit is allowed only while Open per spec).

- [ ] **Step 2: Implement the four methods**

In `StoreService.cs` (sketch — use the patterns from existing services for clock + audit-log injection):

```csharp
public async Task<Guid> CreateOrderAsync(Guid campSeasonId, string? label, Guid actorUserId, CancellationToken ct = default)
{
    var order = new StoreOrder
    {
        Id = Guid.NewGuid(),
        CampSeasonId = campSeasonId,
        Label = label,
        State = StoreOrderState.Open,
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    };
    await _repo.AddOrderAsync(order, ct);
    await _audit.RecordAsync(/* StoreOrderCreated */, ct);
    return order.Id;
}

public async Task AddLineAsync(Guid orderId, Guid productId, int qty, Guid actorUserId, CancellationToken ct = default)
{
    if (qty <= 0) throw new ArgumentException("Qty must be positive", nameof(qty));

    var order = await _repo.GetOrderByIdAsync(orderId, ct)
        ?? throw new InvalidOperationException($"Order {orderId} not found");

    if (order.State != StoreOrderState.Open)
        throw new InvalidOperationException("Cannot add lines to an issued order");

    var product = await _repo.GetProductByIdAsync(productId, ct)
        ?? throw new InvalidOperationException($"Product {productId} not found");

    var today = _clock.GetCurrentInstant().InZone(_eventTimeZone).Date;
    if (today > product.OrderableUntil)
        throw new InvalidOperationException($"Product {product.Name} order deadline ({product.OrderableUntil}) has passed");

    var line = new StoreOrderLine
    {
        Id = Guid.NewGuid(),
        OrderId = order.Id,
        ProductId = product.Id,
        Qty = qty,
        UnitPriceSnapshot = product.UnitPriceEur,
        VatRateSnapshot = product.VatRatePercent,
        DepositAmountSnapshot = product.DepositAmountEur,
        AddedAt = _clock.GetCurrentInstant(),
        AddedByUserId = actorUserId
    };
    await _repo.AddLineAsync(line, ct);
    await _audit.RecordAsync(/* StoreLineAdded */, ct);
}

public async Task RemoveLineAsync(Guid lineId, Guid actorUserId, CancellationToken ct = default)
{
    // Look up via OrderId reachable from the line. Repo currently only has
    // RemoveLineAsync(Guid). Add IStoreRepository.GetLineWithOrderAsync(...) if
    // tests require richer state checks; otherwise apply the OrderableUntil check
    // by loading line + product before delete.
    // ... implementation per tests ...
}

public async Task UpdateCounterpartyAsync(Guid orderId, OrderCounterpartyInput input, Guid actorUserId, CancellationToken ct = default)
{
    var order = await _repo.GetOrderByIdAsync(orderId, ct)
        ?? throw new InvalidOperationException($"Order {orderId} not found");

    if (order.State != StoreOrderState.Open)
        throw new InvalidOperationException("Cannot edit counterparty on an issued order");

    order.CounterpartyName = input.Name;
    order.CounterpartyVatId = input.VatId;
    order.CounterpartyAddress = input.Address;
    order.CounterpartyCountryCode = input.CountryCode;
    order.CounterpartyEmail = input.Email;
    order.UpdatedAt = _clock.GetCurrentInstant();
    await _repo.UpdateOrderAsync(order, ct);
    await _audit.RecordAsync(/* StoreCounterpartyEdited */, ct);
}
```

> Inject `IClock` (NodaTime) and an event time zone. Match existing service patterns. Audit-log calls use the existing `IAuditLogService` API — look at `BudgetService` for the exact method signature.

- [ ] **Step 3: Tests pass, commit**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~StoreServiceTests"
git add src/ tests/
git commit -m "feat(store): create-order, add/remove line, counterparty edit with OrderableUntil gate"
```

### Task 2.5 — `StoreController` + authorization handler

- [ ] **Step 1: Authorization requirement + handler**

Mirror `CampOperationRequirement` / `CampAuthorizationHandler`. Operations: `View`, `AddLine`, `RemoveLine`, `EditCounterparty`. Resource is the `StoreOrder`. Handler resolves the user's `CampLead` rows and matches against `Order.CampSeasonId`'s parent `Camp`. FinanceAdmin and Admin always pass.

- [ ] **Step 2: Controller**

`src/Humans.Web/Controllers/StoreController.cs` — actions:

  - `GET /Store` — list user's camp-seasons, their orders, the catalog. Anonymous-redirect to login if needed.
  - `GET /Store/Order/{id}` — order detail.
  - `POST /Store/Order/{campSeasonId}/Create` — create new order, optional label.
  - `POST /Store/Order/{id}/AddLine` — productId + qty.
  - `POST /Store/Order/{id}/RemoveLine` — lineId.
  - `POST /Store/Order/{id}/UpdateCounterparty` — bound model.

Use `IAuthorizationService.AuthorizeAsync(User, order, requirement)` for resource-based checks. No `isPrivileged` boolean.

- [ ] **Step 3: Views**

`Views/Store/Index.cshtml`, `Views/Store/Order.cshtml`. Match existing camp-lead-facing view conventions (look at `Views/Camp/`). User-facing copy uses "humans" terminology where applicable (not relevant here — no user-list UI).

- [ ] **Step 4: Nav link**

Find the main-nav include for camp-lead-facing links (look in `Views/Shared/`). Add a `/Store` link visible when the user has any active CampLead row OR the StoreAdmin/FinanceAdmin/Admin role. Place per the daily-traffic ordering rule.

- [ ] **Step 5: Build + tests**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

### Task 2.6 — Integration test + commit

- [ ] **Step 1: Integration test**

`tests/Humans.Integration.Tests/Controllers/StoreControllerTests.cs` — using `HumansWebApplicationFactory`:

  - GET `/Store` as a camp lead returns 200 and lists the catalog.
  - POST AddLine → balance grows; GET shows the new line.
  - POST AddLine after `OrderableUntil` (manipulate clock) → 4xx with the correct message.
  - GET `/Store` as a non-lead, non-admin user → 403 or empty.

- [ ] **Step 2: Run all tests**

```bash
dotnet test Humans.slnx -v quiet
```

- [ ] **Step 3: Commit + push + PR**

```bash
git add .
git commit -m "feat(store): camp-lead UX — /Store browse, order CRUD, balance display"
git push -u origin store-camp-lead
gh pr create --repo peterdrier/Humans --base main --head store-camp-lead --title "feat(store): camp-lead order UX" --body "Phase 2 of the /Store section. Stacks on store-foundation."
```

---

## Phase 3 — Catalog admin

**Branch:** `store-catalog-admin`

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs` — implement `CreateProductAsync`, `UpdateProductAsync`, `DeactivateProductAsync`.
- Create: `src/Humans.Web/Controllers/StoreAdminController.cs` (catalog actions only in this phase).
- Create: `src/Humans.Web/Views/StoreAdmin/Catalog.cshtml`, `CatalogEdit.cshtml`.
- Modify: admin nav include — add `/Store/Admin/Catalog` for StoreAdmin/FinanceAdmin/Admin.

### Tasks (TDD same shape as Phase 2)

- [ ] **3.1** Branch from `store-camp-lead`.
- [ ] **3.2** TDD: `CreateProductAsync` — generates Id, persists, audit-log emit.
- [ ] **3.3** TDD: `UpdateProductAsync` — validates Id exists, updates fields, audit-log emit. Snapshots already on existing lines are unaffected (test that updating price doesn't change existing line snapshot — verifies write-time snapshot rule end-to-end).
- [ ] **3.4** TDD: `DeactivateProductAsync` — sets `IsActive = false`, audit-log emit.
- [ ] **3.5** Controller actions:
  - `GET /Store/Admin/Catalog`
  - `GET /Store/Admin/Catalog/Edit/{id?}` (no-id = create)
  - `POST /Store/Admin/Catalog/Save`
  - `POST /Store/Admin/Catalog/Deactivate/{id}`
  - All gated by `[Authorize(Roles = "StoreAdmin,FinanceAdmin,Admin")]` controller-level (catalog mgmt is StoreAdmin's domain; FinanceAdmin and Admin are supersets).
- [ ] **3.6** Views + nav link.
- [ ] **3.7** Integration test: full create-edit-deactivate-disappear-from-catalog cycle.
- [ ] **3.8** Commit + push + PR.

---

## Phase 4 — Holded probe + connector extension

**Branch:** `store-holded-extension`

> **Mandatory pre-coding step (per spec's thin-probe caveat):** Before writing any HoldedClient extensions, do a richer probe and commit the samples. This phase therefore has a "Probe" half before the "Code" half.

**Files:**
- Create: `.holded-invoice-sample.json` (full real outbound invoice doc)
- Create: `.holded-treasury-sample.json` (full real treasury entry)
- Create: `.holded-contact-sample.json` (full real contact body)
- Modify: `.holded-probes.md` (note the new samples + date of richer probe)
- Modify: `src/Humans.Infrastructure/Services/HoldedClient.cs`
- Modify: `src/Humans.Application/Interfaces/Finance/IHoldedClient.cs` (or whatever interface the existing client implements)
- Test: `tests/Humans.Infrastructure.Tests/Services/HoldedClientTests.cs` — add tests using the committed samples as the canned response payload.

### Task 4.1 — Richer probe (manual / human-in-the-loop)

- [ ] **Step 1: In Holded UI**, create one test invoice with:
    - 2 normal taxable lines (different VAT rates: 21% and 10%).
    - 1 deposit-style line (VAT 0%).
    - A counterparty contact with both VAT-id and address fields populated.

- [ ] **Step 2: In Holded UI**, create one bank-transfer entry visible in the treasury list with a non-empty description.

- [ ] **Step 3: Dump the docs via API**

```bash
# Adapt the existing probe script under .holded* — pull the new docs by id.
# Save raw JSON to .holded-invoice-sample.json, .holded-treasury-sample.json,
# .holded-contact-sample.json
```

- [ ] **Step 4: Sanity-check the JSON structures**

Confirm field names (especially: invoice line tax fields, treasury entry id + description fields, contact's `vatnumber` placement). Record any field whose name in `.holded-probes.md` was a guess and is now confirmed/corrected.

- [ ] **Step 5: Commit the samples**

```bash
git add .holded-invoice-sample.json .holded-treasury-sample.json .holded-contact-sample.json .holded-probes.md
git commit -m "probe(holded): real invoice + treasury + contact JSON samples for outbound work"
```

### Task 4.2 — `UpsertContactAsync` (TDD)

- [ ] **Step 1: Tests**

`tests/Humans.Infrastructure.Tests/Services/HoldedClientTests.cs`:

  - `UpsertContactAsync` with VAT-id present → POSTs to contacts endpoint with the right body shape (verify against the committed contact sample).
  - With existing matching VAT-id → returns existing contact id (no duplicate). Implementation can do GET-then-POST or rely on Holded's idempotency if the API supports it; verify against real behavior in the probe.
  - With no VAT-id → match by name + country.

Use `HttpMessageHandler` mock to stub Holded responses with the sample JSON.

- [ ] **Step 2: Implement**

Sketch in `HoldedClient.cs`:

```csharp
public async Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default)
{
    // 1. Try to find by VAT-id (or name+country) via GET /contacts?vatnumber=...
    // 2. If found, return id.
    // 3. Otherwise POST /contacts with the input mapped to Holded's contact body.
    // 4. Return new id.
}

public record HoldedContactInput(
    string Name,
    string? VatId,
    string? Address,
    string? CountryCode,
    string? Email);
```

The exact field-mapping is locked against the committed `.holded-contact-sample.json`.

- [ ] **Step 3: Tests pass, commit**

### Task 4.3 — `CreateInvoiceAsync` (TDD)

- [ ] **Step 1: Tests** — POSTs the invoice body shaped per the committed `.holded-invoice-sample.json`. One taxable line + one deposit line (VAT-0). Returns docId + docNumber from the response.

- [ ] **Step 2: Implement**

```csharp
public async Task<HoldedInvoiceResponse> CreateInvoiceAsync(HoldedInvoiceInput input, CancellationToken ct = default) { /* ... */ }

public record HoldedInvoiceInput(
    string ContactId,
    string Description,
    Instant Date,
    IReadOnlyList<HoldedInvoiceLine> Lines);

public record HoldedInvoiceLine(
    string Name,
    int Units,
    decimal UnitPrice,
    decimal VatRate);

public record HoldedInvoiceResponse(string DocId, string DocNumber);
```

- [ ] **Step 3: Tests pass, commit**

### Task 4.4 — `ListTreasuryEntriesAsync` (TDD)

- [ ] **Step 1: Tests** — GETs `/treasury` (or `/payments` — confirmed by probe), parses to a typed record, filters to entries with `receivedAt > since`.

- [ ] **Step 2: Implement**

```csharp
public async Task<IReadOnlyList<HoldedTreasuryEntry>> ListTreasuryEntriesAsync(Instant since, CancellationToken ct = default) { /* ... */ }

public record HoldedTreasuryEntry(
    string Id,
    decimal Amount,
    string? Description,
    Instant ReceivedAt);
```

- [ ] **Step 3: Tests pass, commit + push + PR**

```bash
git push -u origin store-holded-extension
gh pr create --repo peterdrier/Humans --base main --head store-holded-extension --title "feat(holded): outbound contact upsert + invoice POST + treasury list"
```

---

## Phase 5 — Invoice issuance + Summary

**Branch:** `store-issuance`

**Files:**
- Modify: `src/Humans.Application/Services/Store/StoreService.cs` — implement `RecordManualPaymentAsync`, `IssueInvoiceAsync`, `GetAllOrderSummariesAsync`.
- Create: `src/Humans.Application/Services/Store/InvoicePayloadBuilder.cs` — pure builder, takes order + camp-name fallback, returns `HoldedInvoiceInput`. Testable.
- Modify: `src/Humans.Web/Controllers/StoreAdminController.cs` — add Orders index, per-Order ledger, payment-entry, "Issue invoice" + "Issue all".
- Create: `src/Humans.Web/Controllers/StoreSummaryController.cs`
- Create: `src/Humans.Web/Views/StoreAdmin/Orders.cshtml`, `OrderLedger.cshtml`.
- Create: `src/Humans.Web/Views/StoreSummary/Index.cshtml`.

### Task 5.1 — `InvoicePayloadBuilder` (TDD)

Pure function: order + counterparty-resolver → `HoldedInvoiceInput`.

- [ ] **Step 1: Tests**
  - Single taxable line + 1 deposit line → 2 invoice lines: taxable @VAT, deposit @0%.
  - Empty counterparty name → falls back to camp name (resolver argument).
  - Invoice description format documented (e.g. `"Pedido Store {Order.Label ?? CampName} {Year}"`).

- [ ] **Step 2: Implement** — pure static method, no external deps.

- [ ] **Step 3: Tests pass, commit.**

### Task 5.2 — `IssueInvoiceAsync` (TDD)

- [ ] **Step 1: Tests** — orchestration:
  1. Fetches order with lines.
  2. Resolves camp name via `ICampService.GetCampNameAsync(campSeasonId)` for fallback.
  3. Calls `HoldedClient.UpsertContactAsync`.
  4. Calls `HoldedClient.CreateInvoiceAsync` with payload built by builder.
  5. Inserts `StoreInvoice`, sets `Order.State = InvoiceIssued`, `Order.IssuedInvoiceId`.
  6. Audit log emit.
  - Idempotency: re-issuing an already-issued order → throws `InvalidOperationException` and Holded is NOT called.
  - Failure mid-flight: if `CreateInvoiceAsync` throws, Order remains `Open` (no partial state).

- [ ] **Step 2: Implement.** Use `IHoldedClient` (or whatever interface today's `HoldedClient` is bound to) injected.

- [ ] **Step 3: Tests pass, commit.**

### Task 5.3 — `RecordManualPaymentAsync`

- [ ] **Step 1: Tests** — inserts a `StorePayment` with the right Method, AmountEur signed, RecordedByUserId set; audit-log emit. Allowed regardless of Order state (per spec — payments continue post-issuance).

- [ ] **Step 2: Implement.** Trivially short.

- [ ] **Step 3: Tests pass, commit.**

### Task 5.4 — `GetAllOrderSummariesAsync`

- [ ] **Step 1: Tests** — returns one row per order with camp name (resolved via `ICampService`), balance, payment total.

- [ ] **Step 2: Implement.** Per-camp-name lookups in batch (single `ICampService` batch call if available, else loop).

- [ ] **Step 3: Tests pass, commit.**

### Task 5.5 — Admin controllers + views

- [ ] **Step 1:** `StoreAdminController` actions: `GET Orders`, `GET Orders/{id}`, `POST Orders/{id}/RecordPayment`, `POST Orders/{id}/IssueInvoice`, `POST Orders/IssueAll`.
- [ ] **Step 2:** `StoreSummaryController` action: `GET /Store/Summary` — renders per-barrio rows + per-item rows (qty sold across orders, revenue, deposit total). Exact metrics table layout: TBD with Peter — start with what the Summary spec section names ("per-barrio rows + per-item rows") and iterate.
- [ ] **Step 3:** Views, nav links (admin nav: `/Store/Admin/Orders`, `/Store/Summary`).
- [ ] **Step 4:** Integration tests for the issue-invoice happy path (mocked HoldedClient).
- [ ] **Step 5:** Commit + push + PR.

---

## Phase 6 — Stripe checkout + webhook

**Branch:** `store-stripe`

**Files:**
- Modify: `src/Humans.Application/Interfaces/IStripeService.cs` — add `CreateCheckoutSessionAsync`.
- Modify: `src/Humans.Infrastructure/Services/StripeService.cs` — implement.
- Create: `src/Humans.Web/Controllers/StoreStripeWebhookController.cs`
- Modify: `src/Humans.Web/Controllers/StoreController.cs` — add `POST /Store/Order/{id}/Pay` action that creates a session and redirects.
- Modify: `src/Humans.Web/Views/Store/Order.cshtml` — Pay form.
- Modify: webhook routing — make sure `[AllowAnonymous]` on the webhook endpoint, signature verification matches existing Stripe-webhook pattern (look at any existing Stripe webhooks; if none, follow the Stripe SDK docs).

### Task 6.1 — Extend `IStripeService` (TDD)

- [ ] **Step 1:** Tests for the new method (mock the underlying Stripe HTTP layer):
  - Returns the session URL.
  - Sets `metadata["humans_store_order_id"]`.
  - Includes `successUrl`/`cancelUrl` in the session.

- [ ] **Step 2:** Add the method to `IStripeService`:

```csharp
Task<string> CreateCheckoutSessionAsync(
    Guid storeOrderId,
    decimal amountEur,
    string successUrl,
    string cancelUrl,
    string? customerEmail,
    CancellationToken ct = default);
```

- [ ] **Step 3:** Implement in `StripeService.cs` using the Stripe.NET `SessionService.CreateAsync` with `Mode = "payment"`, `LineItems` = a single line for the amount, `Metadata` set.

- [ ] **Step 4:** Tests pass, commit.

### Task 6.2 — Webhook controller (TDD)

- [ ] **Step 1:** Tests:
  - Invalid signature → 400.
  - `checkout.session.completed` with `humans_store_order_id` metadata → inserts `StorePayment` (Method=Stripe, AmountEur from session, StripePaymentIntentId from session).
  - Same event delivered twice → no duplicate `StorePayment` (dedup via `StripePaymentIntentId` unique index + `StripePaymentIntentExistsAsync`).
  - Unknown event types → 200, no-op.

- [ ] **Step 2:** Implement controller. Reads `STRIPE_WEBHOOK_SECRET` from `IConfiguration`. Uses `EventUtility.ConstructEvent` for verification.

- [ ] **Step 3:** Register route `/Store/StripeWebhook`, `[AllowAnonymous]`.

- [ ] **Step 4:** Tests pass, commit.

### Task 6.3 — Pay button on camp-lead view

- [ ] **Step 1:** `POST /Store/Order/{id}/Pay` action: resolves Order, computes balance, calls `IStripeService.CreateCheckoutSessionAsync`, redirects to the returned URL.
- [ ] **Step 2:** Form on `Order.cshtml` with the amount-input (default = balance owed) and method radio (Stripe enabled; bank transfer = informational only on the camp-lead side).
- [ ] **Step 3:** Integration test: end-to-end through the action (mock IStripeService).
- [ ] **Step 4:** Commit + push + PR.

---

## Phase 7 — Treasury sync job

**Branch:** `store-treasury-sync`

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/StoreTreasurySyncJob.cs`
- Modify: Hangfire registration (look at how `TicketSyncJob` is registered — same pattern, e.g. `IRecurringJob` interface + DI registration).
- Modify: `StoreAdminController` — add a "Run sync now" button + status panel reading from `StoreTreasurySyncState`.

### Task 7.1 — `StoreTreasurySyncJob` (TDD)

- [ ] **Step 1: Tests**
  - Single new treasury entry with description matching exactly one Open Order's `Label` → inserts `StorePayment` (Method=BankTransfer, ExternalRef=Holded entry id, AmountEur=entry.Amount). State cursor updates.
  - Description matches no Order's Label → entry is skipped (left for FinanceAdmin manual entry). State cursor still advances.
  - Description matches multiple Orders' Labels (collision) → skipped, log warning.
  - Re-run with same entry already inserted (deduped by ExternalRef) → no duplicate.
  - HoldedClient throws → state set to Error with the message; subsequent run resumes from cursor.

- [ ] **Step 2: Implement** mirroring `TicketSyncJob` shape — check that for the actual structure (constructor, `RunAsync`, `IRecurringJob` registration).

- [ ] **Step 3: Tests pass, commit.**

### Task 7.2 — Admin UI hooks

- [ ] **Step 1:** Sync-status panel on `Orders.cshtml` (or a new `TreasurySync.cshtml`).
- [ ] **Step 2:** "Run now" button POSTs to a controller action that enqueues the job.
- [ ] **Step 3:** Commit + push + PR.

---

## Self-review (run by plan author)

**Spec coverage check:**

| Spec section | Plan tasks |
|---|---|
| Section ownership | Phase 1 (DI, repo, service, section doc) |
| Connector ownership | Phase 4 (Holded), Phase 6 (Stripe) |
| Data model: 6 entities | Phase 1.3 |
| Data model: 2 enums | Phase 1.2 |
| `StoreAdmin` role | Phase 1.4 |
| EF migration | Phase 1.6 (with reviewer-agent gate) |
| Order state machine | Phase 2.4 (CreateOrder, AddLine), Phase 5.2 (IssueInvoice) |
| Per-product OrderableUntil gate | Phase 2.4 (AddLine, RemoveLine) |
| Free editing pre-cutoff | Phase 2.4 |
| Counterparty edit (Open only) | Phase 2.4 |
| Lines + counterparty read-only after issuance, payments continue | Phase 5.2 (IssueInvoice sets state); Phase 5.3 (manual payment unrestricted on state); Phase 6 (webhook unrestricted on state) |
| Snapshot price/VAT/deposit at add time | Phase 2.4 (test verifies snapshots survive product edits) |
| Multiple Orders per CampSeason, optional Label | Phase 1.3 (entity), Phase 2.4 (CreateOrder) |
| Stripe Checkout + webhook | Phase 6 |
| Holded contact upsert + invoice POST | Phase 4.2, 4.3 |
| Holded treasury sync best-effort match by Label | Phase 7.1 |
| FinanceAdmin manual payment entry + refunds | Phase 5.3 |
| Issue invoice + Issue all + idempotency + roll-back-on-failure | Phase 5.2 |
| `/Store/Summary` per-barrio + per-item | Phase 5.4, 5.5 |
| `/Store/Admin/*` URLs (no `/Admin/Store`) | Phase 3 (catalog), Phase 5 (orders) |
| Audit log emissions | Sprinkled through Phases 2.4, 3, 5.2, 5.3, 6.2, 7.1 |
| Section invariants doc | Phase 1.8 |
| Freshness catalog entry | Phase 1.8 |
| Thin-probe pre-coding gate | Phase 4.1 (mandatory) |
| Year handle deferred | Phase 1.3 (plain `int Year`) |
| Deposit-return UI deferred | Not built (out-of-scope per spec) |
| OrderableUntil admin override deferred | Not built |

No spec gaps found.

**Placeholder scan:** No "TBD"/"TODO" items in code blocks; the year handle is explicitly an `int` with a documented "could be tightened later" note; the Summary metrics table is explicitly named ("per-barrio rows + per-item rows") and elaborated in the spec.

**Type-consistency check:** `OrderDto` shape in Phase 1.7 matches usage in Phase 2.3. `HoldedInvoiceInput` / `HoldedInvoiceLine` defined once in Phase 4.3 and consumed in Phase 5.1's payload builder. `IStoreRepository` method names are stable across phases (no rename).

---

## Execution

This plan is structured for either subagent-driven-development (one agent per phase, two-stage review between) or executing-plans (inline with checkpoint after each phase).
