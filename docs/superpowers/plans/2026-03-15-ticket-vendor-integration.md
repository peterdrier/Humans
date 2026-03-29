# Ticket Vendor Integration Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a ticket vendor integration that syncs ticket sales from TicketTailor, auto-matches buyers to humans, and provides a dashboard with revenue stats, daily sales charts, and operational tools for the ticketing team.

**Architecture:** Vendor-agnostic `ITicketVendorService` interface with TicketTailor as first implementation. Hangfire job polls for orders/attendees. New `TicketOrder` and `TicketAttendee` entities with email-based auto-matching. Multi-page `/Tickets` section: summary dashboard (cards + Chart.js daily sales chart), orders list, attendees list, code tracking, gate list stub.

**Tech Stack:** ASP.NET Core MVC, EF Core + PostgreSQL, NodaTime, Hangfire, Chart.js (CDN), xUnit + AwesomeAssertions, Bootstrap 5.3

**Spec:** `docs/superpowers/specs/2026-03-15-ticket-vendor-integration-design.md`

---

## File Structure

### Domain Layer (`src/Humans.Domain/`)
| File | Purpose |
|------|---------|
| `Entities/TicketOrder.cs` | Order entity — one per vendor purchase |
| `Entities/TicketAttendee.cs` | Attendee entity — one per issued ticket |
| `Entities/TicketSyncState.cs` | Singleton tracking sync operational state |
| `Enums/TicketSyncStatus.cs` | Idle, Running, Error |
| `Enums/TicketPaymentStatus.cs` | Paid, Pending, Refunded |
| `Enums/TicketAttendeeStatus.cs` | Valid, Void, CheckedIn |
| `Constants/RoleNames.cs` | Add TicketAdmin constant (modify) |

### Application Layer (`src/Humans.Application/`)
| File | Purpose |
|------|---------|
| `Interfaces/ITicketVendorService.cs` | Vendor-agnostic API interface |
| `Interfaces/ITicketSyncService.cs` | Sync orchestration interface |
| `DTOs/TicketVendorDtos.cs` | VendorOrderDto, VendorTicketDto, VendorEventSummaryDto, DiscountCodeSpec, DiscountCodeStatusDto |

### Infrastructure Layer (`src/Humans.Infrastructure/`)
| File | Purpose |
|------|---------|
| `Data/Configurations/TicketOrderConfiguration.cs` | EF config for TicketOrder |
| `Data/Configurations/TicketAttendeeConfiguration.cs` | EF config for TicketAttendee |
| `Data/Configurations/TicketSyncStateConfiguration.cs` | EF config + seed data |
| `Data/HumansDbContext.cs` | Add DbSets (modify) |
| `Services/TicketTailorService.cs` | TT API client implementing ITicketVendorService |
| `Services/TicketSyncService.cs` | Sync orchestration implementing ITicketSyncService |
| `Jobs/TicketSyncJob.cs` | Hangfire recurring job |
| `Migrations/YYYYMMDDHHMMSS_AddTicketVendorIntegration.cs` | Migration (auto-generated) |

### Web Layer (`src/Humans.Web/`)
| File | Purpose |
|------|---------|
| `Controllers/TicketController.cs` | All /Tickets routes |
| `Models/TicketViewModels.cs` | ViewModels for dashboard, orders, attendees, etc. |
| `Views/Ticket/Index.cshtml` | Summary dashboard — cards, chart, problems |
| `Views/Ticket/Orders.cshtml` | Orders table with search/sort/pagination |
| `Views/Ticket/Attendees.cshtml` | Attendees table with search/sort/pagination |
| `Views/Ticket/Codes.cshtml` | Code tracking page |
| `Views/Ticket/GateList.cshtml` | Stub gate list page |
| `Views/Ticket/WhoHasntBought.cshtml` | Humans without ticket purchases |
| `Views/Shared/_Layout.cshtml` | Add Tickets nav item (modify) |
| `Extensions/InfrastructureServiceCollectionExtensions.cs` | Register services (modify) |
| `Extensions/RecurringJobExtensions.cs` | Register sync job (modify) |

### Tests (`tests/Humans.Application.Tests/`)
| File | Purpose |
|------|---------|
| `Services/TicketSyncServiceTests.cs` | Sync logic, matching, upsert, idempotency |
| `Services/TicketTailorServiceTests.cs` | HTTP response parsing, pagination, error handling |

### Existing File Modifications
| File | Change |
|------|--------|
| `src/Humans.Domain/Entities/CampaignGrant.cs` | Add `RedeemedAt` property |
| `src/Humans.Infrastructure/Data/Configurations/CampaignGrantConfiguration.cs` | Add RedeemedAt config |
| `src/Humans.Web/Controllers/CampaignController.cs` | Add GenerateCodes action, inject ITicketVendorService |
| `src/Humans.Web/Views/Campaign/Detail.cshtml` | Add Redeemed column + Generate Codes button |

---

## Chunk 1: Domain Model & Database

### Task 1: Create ticket enums

**Files:**
- Create: `src/Humans.Domain/Enums/TicketSyncStatus.cs`
- Create: `src/Humans.Domain/Enums/TicketPaymentStatus.cs`
- Create: `src/Humans.Domain/Enums/TicketAttendeeStatus.cs`

- [ ] **Step 1: Create TicketSyncStatus enum**

```csharp
// src/Humans.Domain/Enums/TicketSyncStatus.cs
namespace Humans.Domain.Enums;

public enum TicketSyncStatus
{
    Idle,
    Running,
    Error
}
```

- [ ] **Step 2: Create TicketPaymentStatus enum**

```csharp
// src/Humans.Domain/Enums/TicketPaymentStatus.cs
namespace Humans.Domain.Enums;

public enum TicketPaymentStatus
{
    Paid,
    Pending,
    Refunded
}
```

- [ ] **Step 3: Create TicketAttendeeStatus enum**

```csharp
// src/Humans.Domain/Enums/TicketAttendeeStatus.cs
namespace Humans.Domain.Enums;

public enum TicketAttendeeStatus
{
    Valid,
    Void,
    CheckedIn
}
```

- [ ] **Step 4: Add TicketAdmin to RoleNames**

Modify `src/Humans.Domain/Constants/RoleNames.cs` — add after CampAdmin:

```csharp
/// <summary>
/// Ticket Administrator — can manage ticket vendor integration, trigger syncs,
/// generate discount codes, and export ticket data.
/// </summary>
public const string TicketAdmin = "TicketAdmin";
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Domain/Enums/TicketSyncStatus.cs src/Humans.Domain/Enums/TicketPaymentStatus.cs src/Humans.Domain/Enums/TicketAttendeeStatus.cs src/Humans.Domain/Constants/RoleNames.cs
git commit -m "feat(tickets): add ticket enums and TicketAdmin role constant"
```

---

### Task 2: Create TicketOrder entity

**Files:**
- Create: `src/Humans.Domain/Entities/TicketOrder.cs`

- [ ] **Step 1: Create TicketOrder entity**

```csharp
// src/Humans.Domain/Entities/TicketOrder.cs
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A ticket purchase order synced from the ticket vendor.
/// One record per purchase — may contain multiple attendees (issued tickets).
/// </summary>
public class TicketOrder
{
    public Guid Id { get; init; }

    /// <summary>Vendor's order identifier (e.g. TT order ID). Unique.</summary>
    public string VendorOrderId { get; init; } = string.Empty;

    /// <summary>Buyer's name from the vendor.</summary>
    public string BuyerName { get; set; } = string.Empty;

    /// <summary>Buyer's email from the vendor.</summary>
    public string BuyerEmail { get; set; } = string.Empty;

    /// <summary>Auto-matched user by email. Null if no match found.</summary>
    public Guid? MatchedUserId { get; set; }

    /// <summary>Navigation to matched user.</summary>
    public User? MatchedUser { get; set; }

    /// <summary>Order total amount.</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Currency code (e.g. "EUR").</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Discount/voucher code used, if any.</summary>
    public string? DiscountCode { get; set; }

    /// <summary>Payment status from vendor.</summary>
    public TicketPaymentStatus PaymentStatus { get; set; }

    /// <summary>Vendor event ID at time of sync (for future multi-event).</summary>
    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>Deep link to vendor dashboard for this order.</summary>
    public string? VendorDashboardUrl { get; set; }

    /// <summary>When the purchase was made (from vendor data).</summary>
    public Instant PurchasedAt { get; set; }

    /// <summary>When this record was last synced from the vendor.</summary>
    public Instant SyncedAt { get; set; }

    /// <summary>Individual attendees (issued tickets) for this order.</summary>
    public ICollection<TicketAttendee> Attendees { get; set; } = new List<TicketAttendee>();
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/TicketOrder.cs
git commit -m "feat(tickets): add TicketOrder entity"
```

---

### Task 3: Create TicketAttendee entity

**Files:**
- Create: `src/Humans.Domain/Entities/TicketAttendee.cs`

- [ ] **Step 1: Create TicketAttendee entity**

```csharp
// src/Humans.Domain/Entities/TicketAttendee.cs
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// An individual ticket holder (issued ticket) from the vendor.
/// Multiple attendees can belong to a single order.
/// </summary>
public class TicketAttendee
{
    public Guid Id { get; init; }

    /// <summary>Vendor's issued ticket identifier. Unique.</summary>
    public string VendorTicketId { get; init; } = string.Empty;

    /// <summary>FK to the parent order.</summary>
    public Guid TicketOrderId { get; init; }

    /// <summary>Navigation to parent order.</summary>
    public TicketOrder TicketOrder { get; set; } = null!;

    /// <summary>Ticket holder's name.</summary>
    public string AttendeeName { get; set; } = string.Empty;

    /// <summary>Ticket holder's email (may not always be provided by vendor).</summary>
    public string? AttendeeEmail { get; set; }

    /// <summary>Auto-matched user by email. Null if no match or no email.</summary>
    public Guid? MatchedUserId { get; set; }

    /// <summary>Navigation to matched user.</summary>
    public User? MatchedUser { get; set; }

    /// <summary>Ticket type name (e.g. "Full Week", "Weekend Pass").</summary>
    public string TicketTypeName { get; set; } = string.Empty;

    /// <summary>Individual ticket price.</summary>
    public decimal Price { get; set; }

    /// <summary>Ticket status from vendor.</summary>
    public TicketAttendeeStatus Status { get; set; }

    /// <summary>Vendor event ID at time of sync (for future multi-event).</summary>
    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>When this record was last synced from the vendor.</summary>
    public Instant SyncedAt { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/TicketAttendee.cs
git commit -m "feat(tickets): add TicketAttendee entity"
```

---

### Task 4: Create TicketSyncState entity

**Files:**
- Create: `src/Humans.Domain/Entities/TicketSyncState.cs`

- [ ] **Step 1: Create TicketSyncState entity**

```csharp
// src/Humans.Domain/Entities/TicketSyncState.cs
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Singleton tracking ticket sync operational state.
/// Distinct from SyncServiceSettings which controls sync modes for Google/Discord.
/// This tracks when sync last ran, whether it succeeded, and error details.
/// </summary>
public class TicketSyncState
{
    /// <summary>PK — always 1 (singleton).</summary>
    public int Id { get; init; }

    /// <summary>The vendor event ID currently being synced.</summary>
    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>When the last successful sync completed.</summary>
    public Instant? LastSyncAt { get; set; }

    /// <summary>Current sync status.</summary>
    public TicketSyncStatus SyncStatus { get; set; }

    /// <summary>Error message from last failed sync, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>When SyncStatus last changed.</summary>
    public Instant? StatusChangedAt { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/TicketSyncState.cs
git commit -m "feat(tickets): add TicketSyncState entity"
```

---

### Task 5: Add RedeemedAt to CampaignGrant

**Files:**
- Modify: `src/Humans.Domain/Entities/CampaignGrant.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/CampaignGrantConfiguration.cs`

- [ ] **Step 1: Add RedeemedAt property to CampaignGrant**

Add to `src/Humans.Domain/Entities/CampaignGrant.cs` after existing properties:

```csharp
/// <summary>When the grant's discount code was redeemed (used in a ticket purchase). Null if unused.</summary>
public Instant? RedeemedAt { get; set; }
```

- [ ] **Step 2: Add RedeemedAt configuration**

Add to `src/Humans.Infrastructure/Data/Configurations/CampaignGrantConfiguration.cs` in the Configure method:

```csharp
builder.Property(g => g.RedeemedAt);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Entities/CampaignGrant.cs src/Humans.Infrastructure/Data/Configurations/CampaignGrantConfiguration.cs
git commit -m "feat(tickets): add RedeemedAt to CampaignGrant for code redemption tracking"
```

---

### Task 6: Create EF configurations

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/TicketOrderConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/TicketAttendeeConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/TicketSyncStateConfiguration.cs`

- [ ] **Step 1: Create TicketOrderConfiguration**

```csharp
// src/Humans.Infrastructure/Data/Configurations/TicketOrderConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class TicketOrderConfiguration : IEntityTypeConfiguration<TicketOrder>
{
    public void Configure(EntityTypeBuilder<TicketOrder> builder)
    {
        builder.ToTable("ticket_orders");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.VendorOrderId)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(o => o.VendorOrderId)
            .IsUnique();

        builder.Property(o => o.BuyerName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(o => o.BuyerEmail)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(o => o.TotalAmount)
            .HasPrecision(10, 2);

        builder.Property(o => o.Currency)
            .IsRequired()
            .HasMaxLength(3);

        builder.Property(o => o.DiscountCode)
            .HasMaxLength(100);

        builder.Property(o => o.PaymentStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(o => o.VendorEventId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(o => o.VendorDashboardUrl)
            .HasMaxLength(500);

        builder.Property(o => o.PurchasedAt)
            .IsRequired();

        builder.Property(o => o.SyncedAt)
            .IsRequired();

        builder.HasOne(o => o.MatchedUser)
            .WithMany()
            .HasForeignKey(o => o.MatchedUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(o => o.Attendees)
            .WithOne(a => a.TicketOrder)
            .HasForeignKey(a => a.TicketOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(o => o.BuyerEmail);
        builder.HasIndex(o => o.PurchasedAt);
        builder.HasIndex(o => o.MatchedUserId);
    }
}
```

- [ ] **Step 2: Create TicketAttendeeConfiguration**

```csharp
// src/Humans.Infrastructure/Data/Configurations/TicketAttendeeConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class TicketAttendeeConfiguration : IEntityTypeConfiguration<TicketAttendee>
{
    public void Configure(EntityTypeBuilder<TicketAttendee> builder)
    {
        builder.ToTable("ticket_attendees");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.VendorTicketId)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(a => a.VendorTicketId)
            .IsUnique();

        builder.Property(a => a.AttendeeName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.AttendeeEmail)
            .HasMaxLength(320);

        builder.Property(a => a.TicketTypeName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.Price)
            .HasPrecision(10, 2);

        builder.Property(a => a.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(a => a.VendorEventId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.SyncedAt)
            .IsRequired();

        builder.HasOne(a => a.MatchedUser)
            .WithMany()
            .HasForeignKey(a => a.MatchedUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => a.AttendeeEmail);
        builder.HasIndex(a => a.MatchedUserId);
        builder.HasIndex(a => a.TicketOrderId);
    }
}
```

- [ ] **Step 3: Create TicketSyncStateConfiguration**

```csharp
// src/Humans.Infrastructure/Data/Configurations/TicketSyncStateConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class TicketSyncStateConfiguration : IEntityTypeConfiguration<TicketSyncState>
{
    public void Configure(EntityTypeBuilder<TicketSyncState> builder)
    {
        builder.ToTable("ticket_sync_state");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.VendorEventId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(s => s.SyncStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(s => s.LastError)
            .HasMaxLength(2000);

        // Seed the singleton row
        builder.HasData(new
        {
            Id = 1,
            VendorEventId = string.Empty,
            SyncStatus = TicketSyncStatus.Idle,
        });
    }
}
```

- [ ] **Step 4: Add DbSets to HumansDbContext**

Add to `src/Humans.Infrastructure/Data/HumansDbContext.cs` with the other DbSets:

```csharp
public DbSet<TicketOrder> TicketOrders => Set<TicketOrder>();
public DbSet<TicketAttendee> TicketAttendees => Set<TicketAttendee>();
public DbSet<TicketSyncState> TicketSyncStates => Set<TicketSyncState>();
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 6: Generate migration**

Run: `dotnet ef migrations add AddTicketVendorIntegration --project src/Humans.Infrastructure --startup-project src/Humans.Web`
Expected: Migration files generated

- [ ] **Step 7: Verify migration compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/TicketOrderConfiguration.cs src/Humans.Infrastructure/Data/Configurations/TicketAttendeeConfiguration.cs src/Humans.Infrastructure/Data/Configurations/TicketSyncStateConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs src/Humans.Infrastructure/Migrations/
git commit -m "feat(tickets): add EF configurations, DbSets, and migration"
```

---

## Chunk 2: Application Layer — Interfaces & DTOs

### Task 7: Create vendor DTOs

**Files:**
- Create: `src/Humans.Application/DTOs/TicketVendorDtos.cs`

- [ ] **Step 1: Create DTO records**

```csharp
// src/Humans.Application/DTOs/TicketVendorDtos.cs
using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>Vendor-agnostic order data returned by ITicketVendorService.</summary>
public record VendorOrderDto(
    string VendorOrderId,
    string BuyerName,
    string BuyerEmail,
    decimal TotalAmount,
    string Currency,
    string? DiscountCode,
    string PaymentStatus,
    string? VendorDashboardUrl,
    Instant PurchasedAt,
    IReadOnlyList<VendorTicketDto> Tickets);

/// <summary>Vendor-agnostic issued ticket data.</summary>
public record VendorTicketDto(
    string VendorTicketId,
    string VendorOrderId,
    string AttendeeName,
    string? AttendeeEmail,
    string TicketTypeName,
    decimal Price,
    string Status);

/// <summary>High-level event summary from vendor.</summary>
public record VendorEventSummaryDto(
    string EventId,
    string EventName,
    int TotalCapacity,
    int TicketsSold,
    int TicketsRemaining);

/// <summary>Specification for generating discount codes via vendor API.</summary>
public record DiscountCodeSpec(
    int Count,
    DiscountType DiscountType,
    decimal DiscountValue,
    Instant? ExpiresAt);

/// <summary>Type of discount for code generation.</summary>
public enum DiscountType
{
    Percentage,
    Fixed
}

/// <summary>Redemption status of a discount code from the vendor.</summary>
public record DiscountCodeStatusDto(
    string Code,
    bool IsRedeemed,
    int TimesUsed);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Application/Humans.Application.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/DTOs/TicketVendorDtos.cs
git commit -m "feat(tickets): add vendor-agnostic DTOs for ticket integration"
```

---

### Task 8: Create service interfaces

**Files:**
- Create: `src/Humans.Application/Interfaces/ITicketVendorService.cs`
- Create: `src/Humans.Application/Interfaces/ITicketSyncService.cs`

- [ ] **Step 1: Create ITicketVendorService**

```csharp
// src/Humans.Application/Interfaces/ITicketVendorService.cs
using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces;

/// <summary>
/// Vendor-agnostic interface for ticket platform operations.
/// Implementations wrap vendor-specific APIs (e.g. TicketTailor).
/// </summary>
public interface ITicketVendorService
{
    /// <summary>Fetch orders, optionally since a given timestamp.</summary>
    Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    /// <summary>Fetch issued tickets, optionally since a given timestamp.</summary>
    Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    /// <summary>Get high-level event summary (capacity, sold, remaining).</summary>
    Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default);

    /// <summary>Generate discount codes via vendor API.</summary>
    Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default);

    /// <summary>Check redemption status of discount codes.</summary>
    Task<IReadOnlyList<DiscountCodeStatusDto>> GetDiscountCodeUsageAsync(
        IEnumerable<string> codes, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create ITicketSyncService**

```csharp
// src/Humans.Application/Interfaces/ITicketSyncService.cs
namespace Humans.Application.Interfaces;

/// <summary>
/// Orchestrates syncing ticket data from the vendor into local entities.
/// Handles upsert, email matching, and campaign code redemption tracking.
/// </summary>
public interface ITicketSyncService
{
    /// <summary>
    /// Run a full sync cycle: fetch orders and attendees from vendor,
    /// upsert into local DB, auto-match to users by email, and
    /// update campaign grant redemption status.
    /// </summary>
    Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default);
}

/// <summary>Summary of a sync operation.</summary>
public record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/ITicketVendorService.cs src/Humans.Application/Interfaces/ITicketSyncService.cs
git commit -m "feat(tickets): add ITicketVendorService and ITicketSyncService interfaces"
```

---

## Chunk 3: Infrastructure — TicketTailorService

### Task 9: Create TicketTailor settings and HTTP service

**Files:**
- Create: `src/Humans.Infrastructure/Services/TicketTailorService.cs`

**Reference:** TicketTailor API uses Basic Auth (API key as username, empty password). Base URL: `https://api.tickettailor.com/v1`. Endpoints: `GET /orders`, `GET /issued_tickets`, `GET /events/{id}`. Cursor-based pagination via `starting_after` parameter.

- [ ] **Step 1: Create TicketVendorSettings class**

Add to a new file or within the service file. This holds non-sensitive config from appsettings:

```csharp
// src/Humans.Infrastructure/Services/TicketTailorService.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class TicketVendorSettings
{
    public const string SectionName = "TicketVendor";

    public string Provider { get; set; } = "TicketTailor";
    public string EventId { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 15;
    /// <summary>API key — populated from TICKET_VENDOR_API_KEY env var at DI registration time.
    /// Not stored in appsettings (sensitive). Accessible in settings for testability.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(EventId) && !string.IsNullOrEmpty(ApiKey);
}
```

- [ ] **Step 2: Create TicketTailorService implementation**

Continue in the same file:

```csharp
/// <summary>
/// TicketTailor API client implementing the vendor-agnostic interface.
/// API key comes from TICKET_VENDOR_API_KEY environment variable.
/// Non-sensitive config comes from appsettings TicketVendor section.
/// </summary>
public class TicketTailorService : ITicketVendorService
{
    private const string BaseUrl = "https://api.tickettailor.com/v1";
    private readonly HttpClient _httpClient;
    private readonly ILogger<TicketTailorService> _logger;
    private readonly TicketVendorSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TicketTailorService(
        HttpClient httpClient,
        IOptions<TicketVendorSettings> settings,
        ILogger<TicketTailorService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        var apiKey = _settings.ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            var authBytes = Encoding.ASCII.GetBytes($"{apiKey}:");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
    }

    public async Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        var orders = new List<VendorOrderDto>();
        string? cursor = null;

        do
        {
            // TT API returns newest-first by default. We pass created_at.gte
            // to let the API handle incremental filtering server-side.
            var url = $"{BaseUrl}/orders?event_id={eventId}";
            if (since.HasValue)
                url += $"&created_at.gte={since.Value.ToUnixTimeSeconds()}";
            if (cursor != null)
                url += $"&starting_after={cursor}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtOrder>>(JsonOptions, ct);
            if (body?.Data == null || body.Data.Count == 0)
                break;

            foreach (var order in body.Data)
            {
                var purchasedAt = Instant.FromUnixTimeSeconds(long.Parse(order.CreatedAt));

                orders.Add(new VendorOrderDto(
                    VendorOrderId: order.Id,
                    BuyerName: $"{order.BuyerFirstName} {order.BuyerLastName}".Trim(),
                    BuyerEmail: order.BuyerEmail ?? string.Empty,
                    TotalAmount: (order.Total ?? 0) / 100m, // TT stores amounts in cents
                    Currency: order.Currency?.ToUpperInvariant() ?? "EUR",
                    DiscountCode: order.VoucherCode,
                    PaymentStatus: order.Status ?? "completed",
                    VendorDashboardUrl: null, // TT doesn't expose dashboard URLs via API
                    PurchasedAt: purchasedAt,
                    Tickets: []));
            }

            cursor = body.Links?.Next != null ? body.Data[^1].Id : null;
        } while (cursor != null);

        _logger.LogInformation("Fetched {Count} orders from TicketTailor for event {EventId}",
            orders.Count, eventId);

        return orders;
    }

    public async Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        var tickets = new List<VendorTicketDto>();
        string? cursor = null;

        do
        {
            var url = $"{BaseUrl}/issued_tickets?event_id={eventId}";
            if (since.HasValue)
                url += $"&created_at.gte={since.Value.ToUnixTimeSeconds()}";
            if (cursor != null)
                url += $"&starting_after={cursor}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtIssuedTicket>>(JsonOptions, ct);
            if (body?.Data == null || body.Data.Count == 0)
                break;

            foreach (var ticket in body.Data)
            {
                tickets.Add(new VendorTicketDto(
                    VendorTicketId: ticket.Id,
                    VendorOrderId: ticket.OrderId ?? string.Empty,
                    AttendeeName: $"{ticket.FirstName} {ticket.LastName}".Trim(),
                    AttendeeEmail: ticket.Email,
                    TicketTypeName: ticket.TicketTypeName ?? "Unknown",
                    Price: (ticket.Price ?? 0) / 100m,
                    Status: ticket.Status ?? "valid"));
            }

            cursor = body.Links?.Next != null ? body.Data[^1].Id : null;
        } while (cursor != null);

        _logger.LogInformation("Fetched {Count} issued tickets from TicketTailor for event {EventId}",
            tickets.Count, eventId);

        return tickets;
    }

    public async Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"{BaseUrl}/events/{eventId}", ct);
        response.EnsureSuccessStatusCode();

        var evt = await response.Content.ReadFromJsonAsync<TtEvent>(JsonOptions, ct);

        return new VendorEventSummaryDto(
            EventId: eventId,
            EventName: evt?.Name ?? "Unknown",
            TotalCapacity: evt?.TotalHolds ?? 0,
            TicketsSold: evt?.TotalIssuedTickets ?? 0,
            TicketsRemaining: (evt?.TotalHolds ?? 0) - (evt?.TotalIssuedTickets ?? 0));
    }

    public async Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default)
    {
        // TicketTailor voucher creation endpoint
        var codes = new List<string>();
        for (var i = 0; i < spec.Count; i++)
        {
            var payload = new
            {
                code = $"NOBO-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                type = spec.DiscountType == DiscountType.Percentage ? "percentage" : "monetary",
                value = spec.DiscountType == DiscountType.Percentage
                    ? spec.DiscountValue
                    : spec.DiscountValue * 100, // TT uses cents for monetary
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/voucher_codes", payload, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TtVoucherCode>(JsonOptions, ct);
            if (result?.Code != null)
                codes.Add(result.Code);
        }

        _logger.LogInformation("Generated {Count} discount codes via TicketTailor", codes.Count);
        return codes;
    }

    public async Task<IReadOnlyList<DiscountCodeStatusDto>> GetDiscountCodeUsageAsync(
        IEnumerable<string> codes, CancellationToken ct = default)
    {
        var results = new List<DiscountCodeStatusDto>();

        foreach (var code in codes)
        {
            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/voucher_codes?code={Uri.EscapeDataString(code)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                results.Add(new DiscountCodeStatusDto(code, false, 0));
                continue;
            }

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtVoucherCode>>(JsonOptions, ct);
            var vc = body?.Data?.FirstOrDefault();
            results.Add(new DiscountCodeStatusDto(
                code,
                (vc?.TimesUsed ?? 0) > 0,
                vc?.TimesUsed ?? 0));
        }

        return results;
    }

    // --- TicketTailor API response models ---
    // Must be internal (not private) for System.Text.Json deserialization

    internal record TtPaginatedResponse<T>(
        [property: JsonPropertyName("data")] List<T> Data,
        [property: JsonPropertyName("links")] TtLinks? Links);

    internal record TtLinks(
        [property: JsonPropertyName("next")] string? Next,
        [property: JsonPropertyName("previous")] string? Previous);

    internal record TtOrder(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("buyer_first_name")] string? BuyerFirstName,
        [property: JsonPropertyName("buyer_last_name")] string? BuyerLastName,
        [property: JsonPropertyName("buyer_email")] string? BuyerEmail,
        [property: JsonPropertyName("total")] int? Total,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("voucher_code")] string? VoucherCode,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("created_at")] string CreatedAt);

    internal record TtIssuedTicket(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("ticket_type_name")] string? TicketTypeName,
        [property: JsonPropertyName("price")] int? Price,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("order_id")] string? OrderId);

    internal record TtEvent(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("total_holds")] int? TotalHolds,
        [property: JsonPropertyName("total_issued_tickets")] int? TotalIssuedTickets);

    internal record TtVoucherCode(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("times_used")] int? TimesUsed);
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/TicketTailorService.cs
git commit -m "feat(tickets): add TicketTailorService API client"
```

---

## Chunk 4: Infrastructure — Sync Service & Job

### Task 10: Create TicketSyncService

**Files:**
- Create: `src/Humans.Infrastructure/Services/TicketSyncService.cs`

- [ ] **Step 1: Create TicketSyncService**

```csharp
// src/Humans.Infrastructure/Services/TicketSyncService.cs
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class TicketSyncService : ITicketSyncService
{
    private readonly HumansDbContext _dbContext;
    private readonly ITicketVendorService _vendorService;
    private readonly IClock _clock;
    private readonly TicketVendorSettings _settings;
    private readonly ILogger<TicketSyncService> _logger;

    public TicketSyncService(
        HumansDbContext dbContext,
        ITicketVendorService vendorService,
        IClock clock,
        IOptions<TicketVendorSettings> settings,
        ILogger<TicketSyncService> logger)
    {
        _dbContext = dbContext;
        _vendorService = vendorService;
        _clock = clock;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
        {
            _logger.LogWarning("Ticket vendor not configured (missing EventId or API key), skipping sync");
            return new TicketSyncResult(0, 0, 0, 0, 0);
        }

        var eventId = _settings.EventId;

        var syncState = await _dbContext.TicketSyncStates.FindAsync([1], ct)
            ?? throw new InvalidOperationException("TicketSyncState seed row missing");

        var now = _clock.GetCurrentInstant();

        syncState.SyncStatus = TicketSyncStatus.Running;
        syncState.StatusChangedAt = now;
        syncState.VendorEventId = eventId;
        await _dbContext.SaveChangesAsync(ct);

        try
        {
            var orders = await _vendorService.GetOrdersAsync(syncState.LastSyncAt, eventId, ct);
            var tickets = await _vendorService.GetIssuedTicketsAsync(syncState.LastSyncAt, eventId, ct);

            // Build email → UserId lookup from UserEmails table
            var emailLookup = await BuildEmailLookupAsync(ct);

            var ordersSynced = 0;
            var attendeesSynced = 0;
            var ordersMatched = 0;
            var attendeesMatched = 0;

            foreach (var orderDto in orders)
            {
                var order = await UpsertOrderAsync(orderDto, eventId, emailLookup, now, ct);
                ordersSynced++;
                if (order.MatchedUserId.HasValue)
                    ordersMatched++;
            }

            // IMPORTANT: Save orders before processing attendees so that
            // UpsertAttendeeAsync can find parent orders via DB query.
            // Without this, newly added orders are only in the Change Tracker
            // and FirstOrDefaultAsync won't see them.
            await _dbContext.SaveChangesAsync(ct);

            // Sync all tickets (not grouped by order — we match by VendorTicketId)
            foreach (var ticketDto in tickets)
            {
                var attendee = await UpsertAttendeeAsync(ticketDto, eventId, emailLookup, now, ct);
                if (attendee == null) continue; // Skipped — parent order not found
                attendeesSynced++;
                if (attendee.MatchedUserId.HasValue)
                    attendeesMatched++;
            }

            await _dbContext.SaveChangesAsync(ct);

            // Match discount codes to campaign grants
            var codesRedeemed = await MatchDiscountCodesAsync(ct);

            // Update sync state
            syncState.SyncStatus = TicketSyncStatus.Idle;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastSyncAt = now;
            syncState.LastError = null;
            await _dbContext.SaveChangesAsync(ct);

            var result = new TicketSyncResult(ordersSynced, attendeesSynced,
                ordersMatched, attendeesMatched, codesRedeemed);

            _logger.LogInformation(
                "Ticket sync completed: {OrdersSynced} orders, {AttendeesSynced} attendees, " +
                "{OrdersMatched} order matches, {AttendeesMatched} attendee matches, {CodesRedeemed} codes redeemed",
                result.OrdersSynced, result.AttendeesSynced,
                result.OrdersMatched, result.AttendeesMatched, result.CodesRedeemed);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticket sync failed for event {EventId}", eventId);

            syncState.SyncStatus = TicketSyncStatus.Error;
            syncState.StatusChangedAt = _clock.GetCurrentInstant();
            syncState.LastError = ex.Message;
            await _dbContext.SaveChangesAsync(CancellationToken.None);

            throw;
        }
    }

    private async Task<Dictionary<string, Guid>> BuildEmailLookupAsync(CancellationToken ct)
    {
        // Match against ALL user emails (OAuth, verified, unverified)
        // Case-insensitive: normalize to lower
        // If multiple users share same email, prefer the one where it's the OAuth email
        var userEmails = await _dbContext.Set<UserEmail>()
            .Select(ue => new { Email = ue.Email.ToLower(), ue.UserId, ue.IsOAuth })
            .ToListAsync(ct);

        var lookup = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        // Group by email to detect ambiguity (multiple users sharing same email)
        var grouped = userEmails.GroupBy(e => e.Email, StringComparer.OrdinalIgnoreCase);
        foreach (var group in grouped)
        {
            var entries = group.ToList();
            var distinctUserIds = entries.Select(e => e.UserId).Distinct().ToList();

            if (distinctUserIds.Count == 1)
            {
                lookup[group.Key] = distinctUserIds[0];
            }
            else
            {
                // Multiple users share this email — prefer OAuth
                var oauthEntry = entries.FirstOrDefault(e => e.IsOAuth);
                if (oauthEntry != null)
                {
                    lookup[group.Key] = oauthEntry.UserId;
                }
                else
                {
                    // Still ambiguous — per spec, log warning and leave unmatched
                    _logger.LogWarning("Email {Email} shared by {Count} users with no OAuth owner, leaving unmatched",
                        group.Key, distinctUserIds.Count);
                }
            }
        }

        return lookup;
    }

    private async Task<TicketOrder> UpsertOrderAsync(
        VendorOrderDto dto, string eventId,
        Dictionary<string, Guid> emailLookup, Instant now,
        CancellationToken ct)
    {
        var existing = await _dbContext.TicketOrders
            .FirstOrDefaultAsync(o => o.VendorOrderId == dto.VendorOrderId, ct);

        if (existing != null)
        {
            existing.BuyerName = dto.BuyerName;
            existing.BuyerEmail = dto.BuyerEmail;
            existing.TotalAmount = dto.TotalAmount;
            existing.Currency = dto.Currency;
            existing.DiscountCode = dto.DiscountCode;
            existing.PaymentStatus = ParsePaymentStatus(dto.PaymentStatus);
            existing.VendorDashboardUrl = dto.VendorDashboardUrl;
            existing.SyncedAt = now;
            existing.MatchedUserId = emailLookup.GetValueOrDefault(dto.BuyerEmail);
            return existing;
        }

        var order = new TicketOrder
        {
            Id = Guid.NewGuid(),
            VendorOrderId = dto.VendorOrderId,
            BuyerName = dto.BuyerName,
            BuyerEmail = dto.BuyerEmail,
            TotalAmount = dto.TotalAmount,
            Currency = dto.Currency,
            DiscountCode = dto.DiscountCode,
            PaymentStatus = ParsePaymentStatus(dto.PaymentStatus),
            VendorEventId = eventId,
            VendorDashboardUrl = dto.VendorDashboardUrl,
            PurchasedAt = dto.PurchasedAt,
            SyncedAt = now,
            MatchedUserId = emailLookup.GetValueOrDefault(dto.BuyerEmail),
        };

        _dbContext.TicketOrders.Add(order);
        return order;
    }

    private async Task<TicketAttendee?> UpsertAttendeeAsync(
        VendorTicketDto dto, string eventId,
        Dictionary<string, Guid> emailLookup, Instant now,
        CancellationToken ct)
    {
        var existing = await _dbContext.TicketAttendees
            .FirstOrDefaultAsync(a => a.VendorTicketId == dto.VendorTicketId, ct);

        Guid? matchedUserId = dto.AttendeeEmail != null
            ? emailLookup.GetValueOrDefault(dto.AttendeeEmail)
            : null;

        if (existing != null)
        {
            existing.AttendeeName = dto.AttendeeName;
            existing.AttendeeEmail = dto.AttendeeEmail;
            existing.TicketTypeName = dto.TicketTypeName;
            existing.Price = dto.Price;
            existing.Status = ParseAttendeeStatus(dto.Status);
            existing.SyncedAt = now;
            existing.MatchedUserId = matchedUserId;
            return existing;
        }

        // Resolve parent order FK via VendorOrderId from the ticket DTO
        var parentOrder = await _dbContext.TicketOrders
            .FirstOrDefaultAsync(o => o.VendorOrderId == dto.VendorOrderId, ct);

        if (parentOrder == null)
        {
            _logger.LogWarning("Attendee {VendorTicketId} references unknown order {VendorOrderId}, skipping",
                dto.VendorTicketId, dto.VendorOrderId);
            return null;
        }

        var attendee = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = dto.VendorTicketId,
            TicketOrderId = parentOrder.Id,
            AttendeeName = dto.AttendeeName,
            AttendeeEmail = dto.AttendeeEmail,
            TicketTypeName = dto.TicketTypeName,
            Price = dto.Price,
            Status = ParseAttendeeStatus(dto.Status),
            VendorEventId = eventId,
            SyncedAt = now,
            MatchedUserId = matchedUserId,
        };

        _dbContext.TicketAttendees.Add(attendee);
        return attendee;
    }

    private async Task<int> MatchDiscountCodesAsync(CancellationToken ct)
    {
        // Collect all discount codes from orders
        var ordersWithCodes = await _dbContext.TicketOrders
            .Where(o => o.DiscountCode != null)
            .Select(o => new { o.DiscountCode, o.PurchasedAt })
            .ToListAsync(ct);

        if (ordersWithCodes.Count == 0) return 0;

        var codeStrings = ordersWithCodes
            .Select(o => o.DiscountCode!.ToLower())
            .Distinct()
            .ToList();

        // Batch load all relevant grants in one query (avoids N+1)
        // Use ToLower() for case-insensitive match (string.Equals with StringComparison
        // is not translatable by EF Core)
        var unredeemed = await _dbContext.Set<CampaignGrant>()
            .Include(g => g.Code)
            .Include(g => g.Campaign)
            .Where(g => g.Code != null
                && codeStrings.Contains(g.Code.Code.ToLower())
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed)
                && g.RedeemedAt == null)
            .ToListAsync(ct);

        // Match in memory — if same code in multiple campaigns, take the most recent
        var codesRedeemed = 0;
        foreach (var order in ordersWithCodes)
        {
            if (order.DiscountCode == null) continue;

            var grant = unredeemed
                .Where(g => string.Equals(g.Code!.Code, order.DiscountCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.Campaign.CreatedAt)
                .FirstOrDefault();

            if (grant != null)
            {
                grant.RedeemedAt = order.PurchasedAt;
                unredeemed.Remove(grant); // Don't match same grant twice
                codesRedeemed++;
            }
        }

        if (codesRedeemed > 0)
            await _dbContext.SaveChangesAsync(ct);

        return codesRedeemed;
    }

    private static TicketPaymentStatus ParsePaymentStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "completed" or "paid" => TicketPaymentStatus.Paid,
            "pending" => TicketPaymentStatus.Pending,
            "refunded" => TicketPaymentStatus.Refunded,
            _ => TicketPaymentStatus.Paid
        };

    private static TicketAttendeeStatus ParseAttendeeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "valid" or "active" => TicketAttendeeStatus.Valid,
            "void" or "voided" => TicketAttendeeStatus.Void,
            "checked_in" => TicketAttendeeStatus.CheckedIn,
            _ => TicketAttendeeStatus.Valid
        };
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Services/TicketSyncService.cs
git commit -m "feat(tickets): add TicketSyncService with email matching and code redemption"
```

---

### Task 11: Create TicketSyncJob

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/TicketSyncJob.cs`
- Modify: `src/Humans.Web/Extensions/RecurringJobExtensions.cs`
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

- [ ] **Step 1: Create TicketSyncJob**

```csharp
// src/Humans.Infrastructure/Jobs/TicketSyncJob.cs
using Hangfire;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that syncs ticket data from the vendor.
/// Runs every 15 minutes by default. Can also be triggered manually.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class TicketSyncJob
{
    private readonly ITicketSyncService _syncService;
    private readonly ILogger<TicketSyncJob> _logger;

    public TicketSyncJob(
        ITicketSyncService syncService,
        ILogger<TicketSyncJob> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting ticket sync job");

        try
        {
            var result = await _syncService.SyncOrdersAndAttendeesAsync(cancellationToken);

            _logger.LogInformation(
                "Ticket sync job completed: {Orders} orders, {Attendees} attendees synced",
                result.OrdersSynced, result.AttendeesSynced);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticket sync job failed");
            // Rethrow so Hangfire marks the run as failed and can retry.
            // TicketSyncState has already been updated with the error by
            // the sync service's catch block.
            throw;
        }
    }
}
```

- [ ] **Step 2: Register services in DI**

Add to `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` in the `AddHumansInfrastructure` method:

```csharp
// Ticket vendor integration
services.Configure<TicketVendorSettings>(opts =>
{
    configuration.GetSection(TicketVendorSettings.SectionName).Bind(opts);
    // Populate API key from environment variable (not in appsettings — sensitive)
    opts.ApiKey = Environment.GetEnvironmentVariable("TICKET_VENDOR_API_KEY") ?? string.Empty;
});
services.AddHttpClient<ITicketVendorService, TicketTailorService>();
services.AddScoped<ITicketSyncService, TicketSyncService>();
```

Add the using: `using Humans.Infrastructure.Services;` (if not already present)

- [ ] **Step 3: Register recurring job**

Add to `src/Humans.Web/Extensions/RecurringJobExtensions.cs` at the end of `UseHumansRecurringJobs`:

```csharp
// Sync ticket data from vendor at configured interval (default 15 min).
// Requires TICKET_VENDOR_API_KEY environment variable and TicketVendor:EventId in appsettings.
// Note: Hangfire cron doesn't support dynamic intervals, so we use the config value
// at startup time. Changes to SyncIntervalMinutes require app restart.
var ticketSyncInterval = _.Configuration.GetValue("TicketVendor:SyncIntervalMinutes", 15);
RecurringJob.AddOrUpdate<TicketSyncJob>(
    "ticket-vendor-sync",
    job => job.ExecuteAsync(CancellationToken.None),
    $"*/{ticketSyncInterval} * * * *");
```

- [ ] **Step 4: Add TicketVendor config to appsettings**

Add to `src/Humans.Web/appsettings.json`:

```json
"TicketVendor": {
    "Provider": "TicketTailor",
    "EventId": "",
    "SyncIntervalMinutes": 15
}
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Infrastructure/Jobs/TicketSyncJob.cs src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs src/Humans.Web/Extensions/RecurringJobExtensions.cs src/Humans.Web/appsettings.json
git commit -m "feat(tickets): add TicketSyncJob and register DI services"
```

---

## Chunk 5: Tests

### Task 12: Write TicketSyncService tests

**Files:**
- Create: `tests/Humans.Application.Tests/Services/TicketSyncServiceTests.cs`

**Note:** This is the most critical test file. Since we can't test against real TT data in QA, these tests are our primary safety net for production. Use realistic scale (hundreds of orders) and cover all edge cases.

- [ ] **Step 1: Create test class with setup**

```csharp
// tests/Humans.Application.Tests/Services/TicketSyncServiceTests.cs
using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TicketSyncServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ITicketVendorService _vendorService;
    private readonly TicketSyncService _service;

    public TicketSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 6, 1, 12, 0));
        _vendorService = Substitute.For<ITicketVendorService>();

        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test_123",
            SyncIntervalMinutes = 15
        });

        _service = new TicketSyncService(
            _dbContext, _vendorService, _clock, settings,
            NullLogger<TicketSyncService>.Instance);

        // Seed the sync state singleton
        _dbContext.TicketSyncStates.Add(new TicketSyncState
        {
            Id = 1,
            VendorEventId = string.Empty,
            SyncStatus = TicketSyncStatus.Idle
        });
        _dbContext.SaveChanges();
    }

    public void Dispose() => _dbContext.Dispose();
}
```

- [ ] **Step 2: Add basic sync test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_InsertsNewOrders()
{
    // Arrange
    var orders = new List<VendorOrderDto>
    {
        new("ord_001", "Jane Doe", "jane@example.com", 150m, "EUR",
            null, "completed", null,
            Instant.FromUtc(2026, 5, 15, 10, 0), [])
    };

    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), "ev_test_123", Arg.Any<CancellationToken>())
        .Returns(orders);
    _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), "ev_test_123", Arg.Any<CancellationToken>())
        .Returns(new List<VendorTicketDto>());

    // Act
    var result = await _service.SyncOrdersAndAttendeesAsync();

    // Assert
    result.OrdersSynced.Should().Be(1);
    var dbOrder = await _dbContext.TicketOrders.FirstAsync();
    dbOrder.VendorOrderId.Should().Be("ord_001");
    dbOrder.BuyerName.Should().Be("Jane Doe");
    dbOrder.TotalAmount.Should().Be(150m);
}
```

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/Humans.Application.Tests --filter "TicketSyncServiceTests.SyncOrdersAndAttendeesAsync_InsertsNewOrders" -v n`
Expected: PASS

- [ ] **Step 4: Add email matching test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_MatchesOrderToUserByEmail()
{
    // Arrange — seed a user with email
    var userId = Guid.NewGuid();
    _dbContext.Users.Add(new User { Id = userId, UserName = "jane" });
    _dbContext.Set<UserEmail>().Add(new UserEmail
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Email = "Jane@Example.com", // Different case to test case-insensitivity
        IsOAuth = true,
        IsVerified = true,
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    });
    await _dbContext.SaveChangesAsync();

    var orders = new List<VendorOrderDto>
    {
        new("ord_002", "Jane Doe", "jane@example.com", 150m, "EUR",
            null, "completed", null,
            Instant.FromUtc(2026, 5, 15, 10, 0), [])
    };

    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(orders);
    _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(new List<VendorTicketDto>());

    // Act
    var result = await _service.SyncOrdersAndAttendeesAsync();

    // Assert
    result.OrdersMatched.Should().Be(1);
    var dbOrder = await _dbContext.TicketOrders.FirstAsync();
    dbOrder.MatchedUserId.Should().Be(userId);
}
```

- [ ] **Step 5: Add upsert idempotency test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_UpsertDoesNotCreateDuplicates()
{
    // Arrange — same order synced twice
    var orders = new List<VendorOrderDto>
    {
        new("ord_003", "John Smith", "john@example.com", 200m, "EUR",
            null, "completed", null,
            Instant.FromUtc(2026, 5, 15, 10, 0), [])
    };

    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(orders);
    _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(new List<VendorTicketDto>());

    // Act — sync twice
    await _service.SyncOrdersAndAttendeesAsync();
    await _service.SyncOrdersAndAttendeesAsync();

    // Assert — only one order in DB
    var count = await _dbContext.TicketOrders.CountAsync();
    count.Should().Be(1);
}
```

- [ ] **Step 6: Add discount code redemption test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_MatchesDiscountCodeToCampaignGrant()
{
    // Arrange — seed campaign with code and grant
    var campaign = new Campaign
    {
        Id = Guid.NewGuid(),
        Title = "Nowhere 2026",
        EmailSubject = "Your code",
        EmailBodyTemplate = "Use {{Code}}",
        Status = CampaignStatus.Active,
        CreatedAt = _clock.GetCurrentInstant(),
        CreatedByUserId = Guid.NewGuid()
    };
    _dbContext.Set<Campaign>().Add(campaign);

    var code = new CampaignCode
    {
        Id = Guid.NewGuid(),
        CampaignId = campaign.Id,
        Code = "NOBO25",
        ImportedAt = _clock.GetCurrentInstant()
    };
    _dbContext.Set<CampaignCode>().Add(code);

    var grant = new CampaignGrant
    {
        Id = Guid.NewGuid(),
        CampaignId = campaign.Id,
        CampaignCodeId = code.Id,
        UserId = Guid.NewGuid(),
        AssignedAt = _clock.GetCurrentInstant()
    };
    _dbContext.Set<CampaignGrant>().Add(grant);
    await _dbContext.SaveChangesAsync();

    var orders = new List<VendorOrderDto>
    {
        new("ord_004", "Jane Doe", "jane@example.com", 120m, "EUR",
            "nobo25", "completed", null, // lowercase to test case-insensitive
            Instant.FromUtc(2026, 5, 20, 10, 0), [])
    };

    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(orders);
    _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(new List<VendorTicketDto>());

    // Act
    var result = await _service.SyncOrdersAndAttendeesAsync();

    // Assert
    result.CodesRedeemed.Should().Be(1);
    var updatedGrant = await _dbContext.Set<CampaignGrant>().FirstAsync();
    updatedGrant.RedeemedAt.Should().NotBeNull();
}
```

- [ ] **Step 7: Add sync state error handling test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_SetsErrorStateOnFailure()
{
    // Arrange
    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new HttpRequestException("API connection failed"));

    // Act & Assert
    var act = () => _service.SyncOrdersAndAttendeesAsync();
    await act.Should().ThrowAsync<HttpRequestException>();

    var syncState = await _dbContext.TicketSyncStates.FindAsync(1);
    syncState!.SyncStatus.Should().Be(TicketSyncStatus.Error);
    syncState.LastError.Should().Contain("API connection failed");
}
```

- [ ] **Step 8: Add no-EventId configured test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_SkipsWhenEventIdNotConfigured()
{
    // Arrange — create service with empty EventId
    var settings = Options.Create(new TicketVendorSettings { EventId = "" });
    var service = new TicketSyncService(
        _dbContext, _vendorService, _clock, settings,
        NullLogger<TicketSyncService>.Instance);

    // Act
    var result = await service.SyncOrdersAndAttendeesAsync();

    // Assert
    result.OrdersSynced.Should().Be(0);
    await _vendorService.DidNotReceive()
        .GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 9: Run all tests**

Run: `dotnet test tests/Humans.Application.Tests --filter "TicketSyncServiceTests" -v n`
Expected: All tests PASS

- [ ] **Step 10: Commit**

```bash
git add tests/Humans.Application.Tests/Services/TicketSyncServiceTests.cs
git commit -m "test(tickets): add TicketSyncService tests — matching, upsert, codes, errors"
```

---

## Chunk 6: Web Layer — Controller & ViewModels

### Task 13: Create TicketViewModels

**Files:**
- Create: `src/Humans.Web/Models/TicketViewModels.cs`

- [ ] **Step 1: Create view models**

```csharp
// src/Humans.Web/Models/TicketViewModels.cs
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

public class TicketDashboardViewModel
{
    public int TicketsSold { get; set; }
    public int TotalCapacity { get; set; }
    public decimal Revenue { get; set; }
    public decimal AveragePrice { get; set; }
    public int TicketsRemaining { get; set; }
    public string Currency { get; set; } = "EUR";

    // Daily sales chart data
    public List<DailySalesPoint> DailySales { get; set; } = [];

    // Problems / attention items
    public int UnmatchedOrderCount { get; set; }
    public TicketSyncStatus SyncStatus { get; set; }
    public string? SyncError { get; set; }
    public Instant? LastSyncAt { get; set; }

    // Recent orders (last 10)
    public List<TicketOrderSummary> RecentOrders { get; set; } = [];

    public bool IsConfigured { get; set; }
}

public class DailySalesPoint
{
    public string Date { get; set; } = string.Empty; // "2026-05-15" for Chart.js
    public int TicketsSold { get; set; }
    public decimal? RollingAverage { get; set; } // 7-day rolling avg
}

public class TicketOrderSummary
{
    public Guid Id { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public int TicketCount { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public Instant PurchasedAt { get; set; }
    public bool IsMatched { get; set; }
}

public class TicketOrdersViewModel
{
    public List<TicketOrderRow> Orders { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Search { get; set; }
    public string SortBy { get; set; } = "date";
    public bool SortDesc { get; set; } = true;
    public string? FilterPaymentStatus { get; set; }
    public string? FilterTicketType { get; set; }
    public bool? FilterMatched { get; set; }
    public List<string> AvailableTicketTypes { get; set; } = [];

    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public class TicketOrderRow
{
    public Guid Id { get; set; }
    public Instant PurchasedAt { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerEmail { get; set; } = string.Empty;
    public int AttendeeCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? DiscountCode { get; set; }
    public TicketPaymentStatus PaymentStatus { get; set; }
    public string? VendorDashboardUrl { get; set; }
    public Guid? MatchedUserId { get; set; }
    public string? MatchedUserName { get; set; }
}

public class TicketAttendeesViewModel
{
    public List<TicketAttendeeRow> Attendees { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Search { get; set; }
    public string SortBy { get; set; } = "name";
    public bool SortDesc { get; set; }
    public string? FilterTicketType { get; set; }
    public string? FilterStatus { get; set; }
    public bool? FilterMatched { get; set; }
    public List<string> AvailableTicketTypes { get; set; } = [];

    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public class TicketAttendeeRow
{
    public Guid Id { get; set; }
    public string AttendeeName { get; set; } = string.Empty;
    public string? AttendeeEmail { get; set; }
    public string TicketTypeName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public TicketAttendeeStatus Status { get; set; }
    public Guid? MatchedUserId { get; set; }
    public string? MatchedUserName { get; set; }
    public string VendorOrderId { get; set; } = string.Empty;
}

public class TicketCodeTrackingViewModel
{
    public int TotalCodesSent { get; set; }
    public int CodesRedeemed { get; set; }
    public int CodesUnused { get; set; }
    public decimal RedemptionRate { get; set; } // percentage
    public List<CampaignCodeSummary> Campaigns { get; set; } = [];
    public List<CodeDetailRow> Codes { get; set; } = [];
    public string? Search { get; set; }
}

public class CodeDetailRow
{
    public string Code { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public Guid RecipientUserId { get; set; }
    public string CampaignTitle { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Redeemed", "Sent", "Pending"
    public Instant? RedeemedAt { get; set; }
}

public class CampaignCodeSummary
{
    public Guid CampaignId { get; set; }
    public string CampaignTitle { get; set; } = string.Empty;
    public int TotalGrants { get; set; }
    public int Redeemed { get; set; }
    public int Unused { get; set; }
    public decimal RedemptionRate { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/TicketViewModels.cs
git commit -m "feat(tickets): add ticket view models for dashboard and detail pages"
```

---

### Task 14: Create TicketController

**Files:**
- Create: `src/Humans.Web/Controllers/TicketController.cs`

- [ ] **Step 1: Create controller with dashboard action**

This is a large file. Create it with all actions: Index (dashboard), Orders, Attendees, Codes, GateList, Sync.

```csharp
// src/Humans.Web/Controllers/TicketController.cs
using Hangfire;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Humans.Web.Controllers;

[Authorize(Roles = $"{RoleNames.TicketAdmin},{RoleNames.Admin},{RoleNames.Board}")]
[Route("Tickets")]
public class TicketController : Controller
{
    private readonly HumansDbContext _dbContext;
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TicketController> _logger;

    public TicketController(
        HumansDbContext dbContext,
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        IMemoryCache cache,
        ILogger<TicketController> logger)
    {
        _dbContext = dbContext;
        _vendorService = vendorService;
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var isConfigured = _settings.IsConfigured;
        var syncState = await _dbContext.TicketSyncStates.FindAsync(1);

        if (!isConfigured)
        {
            return View(new TicketDashboardViewModel { IsConfigured = false });
        }

        // Summary stats from local data
        var orders = _dbContext.TicketOrders.AsQueryable();
        var attendees = _dbContext.TicketAttendees.AsQueryable();

        // Count both Valid and CheckedIn — both are sold tickets
        var ticketsSold = await attendees.CountAsync(a =>
            a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn);
        var revenue = await orders.SumAsync(o => o.TotalAmount);
        var avgPrice = ticketsSold > 0 ? revenue / ticketsSold : 0;
        var unmatchedCount = await orders.CountAsync(o => o.MatchedUserId == null);

        // Cache vendor event summary (15-min TTL) to avoid API call on every page load.
        // Per CLAUDE.md: "Prefer in-memory caching over query optimization."
        int totalCapacity = 0;
        try
        {
            var summary = await _cache.GetOrCreateAsync("ticket-event-summary", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return await _vendorService.GetEventSummaryAsync(_settings.EventId);
            });
            totalCapacity = summary?.TotalCapacity ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch event summary from vendor");
        }

        // Daily sales data for chart
        // NodaTime GroupBy is not translatable by EF Core, so load in memory first.
        // At ~1,500 orders this is perfectly fine for a small nonprofit system.
        var orderDates = await orders
            .Select(o => new { o.PurchasedAt, AttendeeCount = o.Attendees.Count })
            .ToListAsync();

        var salesByDate = orderDates
            .GroupBy(o => o.PurchasedAt.InUtc().Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.AttendeeCount));

        // Fill in zero-sale days so chart and rolling average are correct
        var dailySalesPoints = new List<DailySalesPoint>();
        if (salesByDate.Count > 0)
        {
            var startDate = salesByDate.Keys.Min();
            var endDate = salesByDate.Keys.Max();
            var allDays = new List<(LocalDate Date, int Count)>();

            for (var d = startDate; d <= endDate; d = d.PlusDays(1))
            {
                allDays.Add((d, salesByDate.GetValueOrDefault(d, 0)));
            }

            for (var i = 0; i < allDays.Count; i++)
            {
                var (date, count) = allDays[i];
                // 7-day rolling average (including zero-sale days)
                var windowStart = Math.Max(0, i - 6);
                var window = allDays.Skip(windowStart).Take(i - windowStart + 1);
                var rollingAvg = window.Average(d => (decimal)d.Count);

                dailySalesPoints.Add(new DailySalesPoint
                {
                    Date = date.ToString("yyyy-MM-dd", null),
                    TicketsSold = count,
                    RollingAverage = Math.Round(rollingAvg, 1)
                });
            }
        }

        // Recent 10 orders
        var recentOrders = await orders
            .OrderByDescending(o => o.PurchasedAt)
            .Take(10)
            .Select(o => new TicketOrderSummary
            {
                Id = o.Id,
                BuyerName = o.BuyerName,
                TicketCount = o.Attendees.Count,
                Amount = o.TotalAmount,
                Currency = o.Currency,
                PurchasedAt = o.PurchasedAt,
                IsMatched = o.MatchedUserId != null
            })
            .ToListAsync();

        var model = new TicketDashboardViewModel
        {
            TicketsSold = ticketsSold,
            TotalCapacity = totalCapacity,
            Revenue = revenue,
            AveragePrice = avgPrice,
            TicketsRemaining = totalCapacity - ticketsSold,
            DailySales = dailySalesPoints,
            UnmatchedOrderCount = unmatchedCount,
            SyncStatus = syncState?.SyncStatus ?? TicketSyncStatus.Idle,
            SyncError = syncState?.LastError,
            LastSyncAt = syncState?.LastSyncAt,
            RecentOrders = recentOrders,
            IsConfigured = true,
        };

        return View(model);
    }

    [HttpGet("Orders")]
    public async Task<IActionResult> Orders(
        string? search, string sortBy = "date", bool sortDesc = true,
        int page = 1, int pageSize = 25,
        string? filterPaymentStatus = null, string? filterTicketType = null,
        bool? filterMatched = null)
    {
        var query = _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .Include(o => o.MatchedUser)
            .AsQueryable();

        // Search
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(o =>
                o.BuyerName.ToLower().Contains(s) ||
                o.BuyerEmail.ToLower().Contains(s) ||
                (o.DiscountCode != null && o.DiscountCode.ToLower().Contains(s)));
        }

        // Filters
        if (!string.IsNullOrEmpty(filterPaymentStatus) &&
            Enum.TryParse<TicketPaymentStatus>(filterPaymentStatus, true, out var ps))
            query = query.Where(o => o.PaymentStatus == ps);

        if (!string.IsNullOrEmpty(filterTicketType))
            query = query.Where(o => o.Attendees.Any(a => a.TicketTypeName == filterTicketType));

        if (filterMatched == true)
            query = query.Where(o => o.MatchedUserId != null);
        else if (filterMatched == false)
            query = query.Where(o => o.MatchedUserId == null);

        var totalCount = await query.CountAsync();

        // Sort — use ToLowerInvariant() for culture-independent comparison
        query = sortBy.ToLowerInvariant() switch
        {
            "amount" => sortDesc ? query.OrderByDescending(o => o.TotalAmount) : query.OrderBy(o => o.TotalAmount),
            "name" => sortDesc ? query.OrderByDescending(o => o.BuyerName) : query.OrderBy(o => o.BuyerName),
            "tickets" => sortDesc ? query.OrderByDescending(o => o.Attendees.Count) : query.OrderBy(o => o.Attendees.Count),
            _ => sortDesc ? query.OrderByDescending(o => o.PurchasedAt) : query.OrderBy(o => o.PurchasedAt),
        };

        var orders = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new TicketOrderRow
            {
                Id = o.Id,
                PurchasedAt = o.PurchasedAt,
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.Attendees.Count,
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                DiscountCode = o.DiscountCode,
                PaymentStatus = o.PaymentStatus,
                VendorDashboardUrl = o.VendorDashboardUrl,
                MatchedUserId = o.MatchedUserId,
                MatchedUserName = o.MatchedUser != null ? o.MatchedUser.DisplayName : null
            })
            .ToListAsync();

        var ticketTypes = await _dbContext.TicketAttendees
            .Select(a => a.TicketTypeName)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();

        var model = new TicketOrdersViewModel
        {
            Orders = orders,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortDesc = sortDesc,
            FilterPaymentStatus = filterPaymentStatus,
            FilterTicketType = filterTicketType,
            FilterMatched = filterMatched,
            AvailableTicketTypes = ticketTypes,
        };

        return View(model);
    }

    [HttpGet("Attendees")]
    public async Task<IActionResult> Attendees(
        string? search, string sortBy = "name", bool sortDesc = false,
        int page = 1, int pageSize = 25,
        string? filterTicketType = null, string? filterStatus = null,
        bool? filterMatched = null)
    {
        var query = _dbContext.TicketAttendees
            .Include(a => a.MatchedUser)
            .Include(a => a.TicketOrder)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(a =>
                a.AttendeeName.ToLower().Contains(s) ||
                (a.AttendeeEmail != null && a.AttendeeEmail.ToLower().Contains(s)));
        }

        if (!string.IsNullOrEmpty(filterTicketType))
            query = query.Where(a => a.TicketTypeName == filterTicketType);

        if (!string.IsNullOrEmpty(filterStatus) &&
            Enum.TryParse<TicketAttendeeStatus>(filterStatus, true, out var status))
            query = query.Where(a => a.Status == status);

        if (filterMatched == true)
            query = query.Where(a => a.MatchedUserId != null);
        else if (filterMatched == false)
            query = query.Where(a => a.MatchedUserId == null);

        var totalCount = await query.CountAsync();

        query = sortBy.ToLowerInvariant() switch
        {
            "type" => sortDesc ? query.OrderByDescending(a => a.TicketTypeName) : query.OrderBy(a => a.TicketTypeName),
            "price" => sortDesc ? query.OrderByDescending(a => a.Price) : query.OrderBy(a => a.Price),
            "status" => sortDesc ? query.OrderByDescending(a => a.Status) : query.OrderBy(a => a.Status),
            _ => sortDesc ? query.OrderByDescending(a => a.AttendeeName) : query.OrderBy(a => a.AttendeeName),
        };

        var attendees = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new TicketAttendeeRow
            {
                Id = a.Id,
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                Status = a.Status,
                MatchedUserId = a.MatchedUserId,
                MatchedUserName = a.MatchedUser != null ? a.MatchedUser.DisplayName : null,
                VendorOrderId = a.TicketOrder.VendorOrderId
            })
            .ToListAsync();

        var ticketTypes = await _dbContext.TicketAttendees
            .Select(a => a.TicketTypeName)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();

        var model = new TicketAttendeesViewModel
        {
            Attendees = attendees,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortDesc = sortDesc,
            FilterTicketType = filterTicketType,
            FilterStatus = filterStatus,
            FilterMatched = filterMatched,
            AvailableTicketTypes = ticketTypes,
        };

        return View(model);
    }

    [HttpGet("Codes")]
    public async Task<IActionResult> Codes(string? search)
    {
        var campaigns = await _dbContext.Set<Domain.Entities.Campaign>()
            .Where(c => c.Status == CampaignStatus.Active || c.Status == CampaignStatus.Completed)
            .Include(c => c.Grants).ThenInclude(g => g.Code)
            .Include(c => c.Grants).ThenInclude(g => g.User)
            .OrderByDescending(c => c.CreatedAt)
            .AsSplitQuery()
            .ToListAsync();

        var campaignSummaries = campaigns.Select(c =>
        {
            var total = c.Grants.Count;
            var redeemed = c.Grants.Count(g => g.RedeemedAt != null);
            return new CampaignCodeSummary
            {
                CampaignId = c.Id,
                CampaignTitle = c.Title,
                TotalGrants = total,
                Redeemed = redeemed,
                Unused = total - redeemed,
                RedemptionRate = total > 0 ? Math.Round(redeemed * 100m / total, 1) : 0
            };
        }).ToList();

        // Build individual code table (spec requires searchable by code string)
        var allGrants = campaigns.SelectMany(c => c.Grants);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLowerInvariant();
            allGrants = allGrants.Where(g =>
                (g.Code?.Code?.ToLowerInvariant().Contains(s) == true) ||
                g.User.DisplayName.ToLowerInvariant().Contains(s));
        }

        var codeRows = allGrants.Select(g => new CodeDetailRow
        {
            Code = g.Code?.Code ?? "—",
            RecipientName = g.User.DisplayName,
            RecipientUserId = g.UserId,
            CampaignTitle = campaigns.First(c => c.Id == g.CampaignId).Title,
            Status = g.RedeemedAt != null ? "Redeemed" : (g.LatestEmailStatus?.ToString() ?? "Pending"),
            RedeemedAt = g.RedeemedAt,
        }).ToList();

        var totalSent = campaignSummaries.Sum(c => c.TotalGrants);
        var totalRedeemed = campaignSummaries.Sum(c => c.Redeemed);

        var model = new TicketCodeTrackingViewModel
        {
            TotalCodesSent = totalSent,
            CodesRedeemed = totalRedeemed,
            CodesUnused = totalSent - totalRedeemed,
            RedemptionRate = totalSent > 0 ? Math.Round(totalRedeemed * 100m / totalSent, 1) : 0,
            Campaigns = campaignSummaries,
            Codes = codeRows,
            Search = search,
        };

        return View(model);
    }

    [HttpGet("GateList")]
    public IActionResult GateList()
    {
        // Stub page for June implementation
        return View();
    }

    [HttpPost("Sync")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = $"{RoleNames.TicketAdmin},{RoleNames.Admin}")]
    public IActionResult Sync()
    {
        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        TempData["SuccessMessage"] = "Ticket sync triggered. Data will update shortly.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export/Attendees")]
    [Authorize(Roles = $"{RoleNames.TicketAdmin},{RoleNames.Admin}")]
    public async Task<IActionResult> ExportAttendees()
    {
        var attendees = await _dbContext.TicketAttendees
            .Include(a => a.TicketOrder)
            .OrderBy(a => a.AttendeeName)
            .ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Name,Email,Ticket Type,Price,Status,Order ID");
        foreach (var a in attendees)
        {
            csv.AppendLine($"\"{a.AttendeeName}\",\"{a.AttendeeEmail ?? ""}\",\"{a.TicketTypeName}\",{a.Price},{a.Status},\"{a.TicketOrder.VendorOrderId}\"");
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "attendees-export.csv");
    }

    [HttpGet("Export/Orders")]
    [Authorize(Roles = $"{RoleNames.TicketAdmin},{RoleNames.Admin}")]
    public async Task<IActionResult> ExportOrders()
    {
        var orders = await _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .OrderByDescending(o => o.PurchasedAt)
            .ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Date,Purchaser,Email,Tickets,Amount,Currency,Code,Status");
        foreach (var o in orders)
        {
            csv.AppendLine($"\"{o.PurchasedAt.InUtc().Date}\",\"{o.BuyerName}\",\"{o.BuyerEmail}\",{o.Attendees.Count},{o.TotalAmount},{o.Currency},\"{o.DiscountCode ?? ""}\",{o.PaymentStatus}");
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "orders-export.csv");
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/TicketController.cs
git commit -m "feat(tickets): add TicketController with dashboard, orders, attendees, codes, sync actions"
```

---

## Chunk 7: Views

### Task 15: Create dashboard view

**Files:**
- Create: `src/Humans.Web/Views/Ticket/Index.cshtml`

- [ ] **Step 1: Create dashboard view with cards, Chart.js chart, problems section, and recent orders**

Create `src/Humans.Web/Views/Ticket/Index.cshtml` with:
- Summary cards (4 across)
- Chart.js daily sales bar chart with 7-day rolling average line overlay
- Problems/attention section
- Recent orders compact table
- Sync status bar with "Sync Now" button (hidden for Board role)

Include Chart.js via CDN: `https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.min.js`

Reference: Follow the card layout pattern from existing views (Bootstrap 5 `row`/`col-md-3` for cards). Use `<canvas>` element for Chart.js. Pass daily sales data as JSON via `@Html.Raw(Json.Serialize(Model.DailySales))`.

**Important UI terminology:** Per CLAUDE.md, use **"humans"** in all user-facing text — not "members", "users", or "volunteers". E.g. "Matched Human", "Unmatched Humans", "Who Hasn't Bought?" should reference "humans". Internal code (variable names, ViewModels) is unaffected.

- [ ] **Step 2: Build and verify view compiles**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Ticket/Index.cshtml
git commit -m "feat(tickets): add ticket dashboard view with Chart.js daily sales chart"
```

---

### Task 16: Create Orders view

**Files:**
- Create: `src/Humans.Web/Views/Ticket/Orders.cshtml`

- [ ] **Step 1: Create paginated, searchable, sortable orders table**

Standard Bootstrap table with:
- Search input (form GET to same action)
- Column header sort links
- Filter dropdowns (payment status, matched/unmatched)
- Pagination controls (Previous / page numbers / Next)
- Each row: date, purchaser (linked), email, ticket count, amount, code, status badge, matched human link

Reference: Follow table patterns from Campaign Detail view. Use `asp-route-*` for maintaining filter/sort state in pagination links.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Ticket/Orders.cshtml
git commit -m "feat(tickets): add orders list view with search, sort, pagination"
```

---

### Task 17: Create Attendees view

**Files:**
- Create: `src/Humans.Web/Views/Ticket/Attendees.cshtml`

- [ ] **Step 1: Create paginated, searchable attendees table**

Same pattern as Orders: search, sort headers, filter dropdowns, pagination. Columns: name, email, ticket type, price, status badge, matched human link, order reference.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Ticket/Attendees.cshtml
git commit -m "feat(tickets): add attendees list view with search, sort, pagination"
```

---

### Task 18: Create Codes and GateList views

**Files:**
- Create: `src/Humans.Web/Views/Ticket/Codes.cshtml`
- Create: `src/Humans.Web/Views/Ticket/GateList.cshtml`

- [ ] **Step 1: Create code tracking view**

Per-campaign redemption summary cards with progress bars (redeemed/total). Links to Campaign Detail pages.

- [ ] **Step 2: Create gate list stub view**

Simple placeholder page: "Gate list functionality coming in June. For now, use the Attendees page to view ticket holders."

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Ticket/Codes.cshtml src/Humans.Web/Views/Ticket/GateList.cshtml
git commit -m "feat(tickets): add code tracking view and gate list stub"
```

---

### Task 19: Add Tickets nav item to layout

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`

- [ ] **Step 1: Add Tickets nav link**

Add after the Admin nav item block (line ~85 area) in `_Layout.cshtml`:

```html
@if (User.IsInRole("TicketAdmin") || User.IsInRole("Admin") || User.IsInRole("Board"))
{
    <li class="nav-item">
        <a class="nav-link" asp-area="" asp-controller="Ticket" asp-action="Index">Tickets</a>
    </li>
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_Layout.cshtml
git commit -m "feat(tickets): add Tickets nav item for TicketAdmin, Admin, Board roles"
```

---

## Chunk 8: Add TicketAdmin to MembershipRequiredFilter & Finalize

### Task 20: Allow TicketAdmin to bypass MembershipRequiredFilter

**Files:**
- Modify: `src/Humans.Web/Authorization/MembershipRequiredFilter.cs`

- [ ] **Step 1: Add TicketAdmin to the bypass list**

Find the line in `MembershipRequiredFilter.cs` that checks admin roles:
```csharp
if (user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.Board) ||
    user.IsInRole(RoleNames.TeamsAdmin) || user.IsInRole(RoleNames.CampAdmin) ||
```

Add `user.IsInRole(RoleNames.TicketAdmin)` to this condition.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Authorization/MembershipRequiredFilter.cs
git commit -m "feat(tickets): add TicketAdmin to MembershipRequiredFilter bypass"
```

---

### Task 21: Add About page attribution for Chart.js

**Files:**
- Modify: `src/Humans.Web/Views/Home/About.cshtml`

- [ ] **Step 1: Add Chart.js entry**

Add to the Frontend CDN Dependencies section of About.cshtml:

| Chart.js | 4.x | MIT | Chart rendering |

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Home/About.cshtml
git commit -m "docs: add Chart.js attribution to About page"
```

---

### Task 22: Run full test suite and build

- [ ] **Step 1: Build entire solution**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded with no errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Humans.slnx -v n`
Expected: All tests pass

- [ ] **Step 3: Update documentation**

Update `.claude/DATA_MODEL.md` with new entities (TicketOrder, TicketAttendee, TicketSyncState) and CampaignGrant.RedeemedAt.

Create `docs/features/24-ticket-vendor-integration.md` — copy key sections from the design spec.

Update `docs/features/22-campaigns.md` — add note about RedeemedAt and code generation.

- [ ] **Step 4: Commit documentation**

```bash
git add .claude/DATA_MODEL.md docs/features/24-ticket-vendor-integration.md docs/features/22-campaigns.md
git commit -m "docs: add ticket vendor integration feature spec and update data model"
```

- [ ] **Step 5: Final verification**

After all tasks complete, verify git log shows clean history of incremental commits.

---

## Chunk 9: Missing Spec Items — "Who Hasn't Bought?", Campaign UI, Additional Tests

### Task 23: Add "Who Hasn't Bought?" action and view

**Files:**
- Modify: `src/Humans.Web/Controllers/TicketController.cs`
- Modify: `src/Humans.Web/Models/TicketViewModels.cs`
- Create: `src/Humans.Web/Views/Ticket/WhoHasntBought.cshtml`

- [ ] **Step 1: Add ViewModel**

Add to `TicketViewModels.cs`:

```csharp
public class WhoHasntBoughtViewModel
{
    public List<WhoHasntBoughtRow> Humans { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Search { get; set; }
    public string? FilterTeam { get; set; }
    public string? FilterTier { get; set; }
    public List<string> AvailableTeams { get; set; } = [];

    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public class WhoHasntBoughtRow
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Teams { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Add controller action**

Add to `TicketController.cs`:

```csharp
[HttpGet("WhoHasntBought")]
public async Task<IActionResult> WhoHasntBought(
    string? search, string? filterTeam = null, string? filterTier = null,
    int page = 1, int pageSize = 25)
{
    // Get all user IDs that have a matched TicketAttendee
    var matchedUserIds = await _dbContext.TicketAttendees
        .Where(a => a.MatchedUserId != null)
        .Select(a => a.MatchedUserId!.Value)
        .Distinct()
        .ToListAsync();

    // Load users not in matched set, with their profiles, teams, and emails.
    // NOTE: MembershipStatus is COMPUTED (not stored) via ComputeMembershipStatus()
    // which requires role assignments, consent records, etc. Use the existing
    // IMembershipCalculator service or the precomputed IsVolunteer claim.
    // DisplayName is on User, not Profile.
    // At ~500 users, loading all and filtering in-memory is fine.
    var users = await _dbContext.Users
        .Include(u => u.Profile)
        .Include(u => u.UserEmails)
        .Include(u => u.TeamMembers).ThenInclude(tm => tm.Team)
        .Where(u => !matchedUserIds.Contains(u.Id))
        .ToListAsync();

    // Filter to active volunteers (those with the ActiveMember claim set by the system)
    // The simplest reliable check: user has a Profile and is in the Volunteers system team.
    var volunteersTeamId = await _dbContext.Set<Domain.Entities.Team>()
        .Where(t => t.SystemType == Domain.Enums.SystemTeamType.Volunteers)
        .Select(t => t.Id)
        .FirstOrDefaultAsync();

    var activeHumans = users
        .Where(u => u.Profile != null &&
            u.TeamMembers.Any(tm => tm.TeamId == volunteersTeamId))
        .ToList();

    // Apply team filter
    if (!string.IsNullOrEmpty(filterTeam))
    {
        activeHumans = activeHumans
            .Where(u => u.TeamMembers.Any(tm =>
                string.Equals(tm.Team.Name, filterTeam, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // Apply tier filter (Volunteer / Colaborador / Asociado)
    if (!string.IsNullOrEmpty(filterTier))
    {
        activeHumans = activeHumans
            .Where(u => string.Equals(u.Profile?.MembershipTier.ToString(), filterTier, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Apply search
    if (!string.IsNullOrWhiteSpace(search))
    {
        var s = search.ToLowerInvariant();
        activeHumans = activeHumans
            .Where(u => u.DisplayName.ToLowerInvariant().Contains(s) ||
                u.UserEmails.Any(e => e.Email.ToLowerInvariant().Contains(s)))
            .ToList();
    }

    var totalCount = activeHumans.Count;
    var pagedHumans = activeHumans
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(u => new WhoHasntBoughtRow
        {
            UserId = u.Id,
            Name = u.DisplayName,
            Email = u.UserEmails.FirstOrDefault(e => e.IsNotificationTarget)?.Email ?? string.Empty,
            Teams = string.Join(", ", u.TeamMembers.Select(tm => tm.Team.Name)),
            Tier = u.Profile?.MembershipTier.ToString() ?? "Volunteer",
        })
        .ToList();

    // Available teams for filter dropdown
    var teams = await _dbContext.Set<Domain.Entities.Team>()
        .Select(t => t.Name)
        .OrderBy(n => n)
        .ToListAsync();

    var model = new WhoHasntBoughtViewModel
    {
        Humans = pagedHumans,
        TotalCount = totalCount,
        Page = page,
        PageSize = pageSize,
        Search = search,
        FilterTeam = filterTeam,
        FilterTier = filterTier,
        AvailableTeams = teams,
    };

    return View(model);
}
```

Note: `MembershipStatus` is computed (not stored) — it requires role assignments, consent records, etc. The approach above uses Volunteers team membership as the "active" proxy, which is simpler and correct for this use case. `DisplayName` is on `User`, not `Profile`. `MembershipTier` is on `Profile`. Check actual entity shapes during implementation and adjust if needed.

- [ ] **Step 3: Create view**

Standard searchable/paginated table matching the Attendees view pattern. Columns: Name, Email, Teams, Tier. Search input, pagination controls.

- [ ] **Step 4: Add nav link on dashboard**

Add a "Who hasn't bought? (X humans)" link on the `/Tickets` dashboard problems section, linking to `/Tickets/WhoHasntBought`.

- [ ] **Step 5: Build and test**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/TicketController.cs src/Humans.Web/Models/TicketViewModels.cs src/Humans.Web/Views/Ticket/WhoHasntBought.cshtml
git commit -m "feat(tickets): add 'Who Hasn't Bought?' page for outreach"
```

---

### Task 24: Add Campaign Detail UI changes (Redeemed column + Generate Codes button)

**Files:**
- Modify: `src/Humans.Web/Controllers/CampaignController.cs`
- Modify: `src/Humans.Web/Views/Campaign/Detail.cshtml`

- [ ] **Step 1: Add Redeemed column to Campaign Detail grants table**

In `Views/Campaign/Detail.cshtml`, find the grants table and add a "Redeemed" column header. In each grant row, show `grant.RedeemedAt` formatted as date, or "—" if null.

- [ ] **Step 2: Add redemption stats summary**

Above the grants table, add summary: "X of Y codes redeemed (Z%)" — calculate in the controller and pass via ViewBag or extend the existing ViewModel.

- [ ] **Step 3: Add "Generate Codes" button and action**

Add to `CampaignController.cs`:

```csharp
[HttpPost("{id:guid}/GenerateCodes")]
[ValidateAntiForgeryToken]
[Authorize(Roles = $"{RoleNames.TicketAdmin},{RoleNames.Admin}")]
public async Task<IActionResult> GenerateCodes(
    Guid id, int count, string discountType, decimal discountValue,
    string? expiresAt)
{
    var campaign = await _campaignService.GetByIdAsync(id);
    if (campaign == null) return NotFound();
    if (campaign.Status != CampaignStatus.Draft)
    {
        TempData["ErrorMessage"] = "Codes can only be generated for Draft campaigns.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    // Parse optional expiry date
    Instant? expiresAtInstant = null;
    if (!string.IsNullOrEmpty(expiresAt) && LocalDate.TryParseExact(expiresAt, "yyyy-MM-dd", out var expiryDate))
    {
        expiresAtInstant = expiryDate.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
    }

    var spec = new DiscountCodeSpec(
        count,
        discountType == "percentage" ? DiscountType.Percentage : DiscountType.Fixed,
        discountValue,
        expiresAtInstant);

    var codes = await _vendorService.GenerateDiscountCodesAsync(spec);

    // Route through campaign service to properly set ImportOrder
    // (follows existing pattern — controller delegates to service, not direct DB writes)
    await _campaignService.ImportGeneratedCodesAsync(id, codes);

    TempData["SuccessMessage"] = $"Generated {codes.Count} discount codes via ticket vendor.";
    return RedirectToAction(nameof(Detail), new { id });
}
```

Note: This requires:
1. Injecting `ITicketVendorService` into the `CampaignController` constructor (add field + parameter)
2. Adding `ImportGeneratedCodesAsync(Guid campaignId, IReadOnlyList<string> codes)` to `ICampaignService` and `CampaignService` — this method computes the next `ImportOrder` value and inserts codes, following the same pattern as `ImportCodesAsync` but without CSV parsing.

- [ ] **Step 4: Add Generate Codes button to Campaign Detail view**

In `Detail.cshtml`, when campaign is Draft, add a "Generate Codes" section with a form: count input, discount type select (percentage/fixed), discount value input, submit button.

- [ ] **Step 5: Build and test**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/CampaignController.cs src/Humans.Web/Views/Campaign/Detail.cshtml
git commit -m "feat(tickets): add Redeemed column and Generate Codes button to Campaign Detail"
```

---

### Task 25: Add TicketTailorService tests

**Files:**
- Create: `tests/Humans.Application.Tests/Services/TicketTailorServiceTests.cs`

- [ ] **Step 1: Create test class with MockHttpMessageHandler**

```csharp
// tests/Humans.Application.Tests/Services/TicketTailorServiceTests.cs
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TicketTailorServiceTests
{
    private TicketTailorService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var settings = Options.Create(new TicketVendorSettings
        {
            EventId = "ev_test",
            SyncIntervalMinutes = 15,
            ApiKey = "test_key" // API key is now in settings, not env var — safe for parallel tests
        });

        return new TicketTailorService(client, settings,
            NullLogger<TicketTailorService>.Instance);
    }
}

// Simple mock handler for testing HTTP responses
public class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public void EnqueueResponse(HttpStatusCode status, object body)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json")
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        return Task.FromResult(_responses.Dequeue());
    }
}
```

- [ ] **Step 2: Add order parsing test**

```csharp
[Fact]
public async Task GetOrdersAsync_ParsesOrderResponse()
{
    var handler = new MockHttpHandler();
    handler.EnqueueResponse(HttpStatusCode.OK, new
    {
        data = new[]
        {
            new
            {
                id = "ord_001",
                buyer_first_name = "Jane",
                buyer_last_name = "Doe",
                buyer_email = "jane@example.com",
                total = 15000, // cents
                currency = "eur",
                voucher_code = "NOBO25",
                status = "completed",
                created_at = "1716811200" // Unix timestamp
            }
        },
        links = new { next = (string?)null }
    });

    var service = CreateService(handler);
    var orders = await service.GetOrdersAsync(null, "ev_test");

    orders.Should().HaveCount(1);
    orders[0].BuyerName.Should().Be("Jane Doe");
    orders[0].TotalAmount.Should().Be(150m); // cents to euros
    orders[0].DiscountCode.Should().Be("NOBO25");
}
```

- [ ] **Step 3: Add pagination test**

```csharp
[Fact]
public async Task GetOrdersAsync_HandlesPagination()
{
    var handler = new MockHttpHandler();
    // Page 1 — has next
    handler.EnqueueResponse(HttpStatusCode.OK, new
    {
        data = new[] { new { id = "ord_001", buyer_first_name = "A", buyer_last_name = "B",
            buyer_email = "a@b.com", total = 100, currency = "eur",
            voucher_code = (string?)null, status = "completed", created_at = "1716811200" } },
        links = new { next = "has_more" }
    });
    // Page 2 — no next
    handler.EnqueueResponse(HttpStatusCode.OK, new
    {
        data = new[] { new { id = "ord_002", buyer_first_name = "C", buyer_last_name = "D",
            buyer_email = "c@d.com", total = 200, currency = "eur",
            voucher_code = (string?)null, status = "completed", created_at = "1716811200" } },
        links = new { next = (string?)null }
    });

    var service = CreateService(handler);
    var orders = await service.GetOrdersAsync(null, "ev_test");

    orders.Should().HaveCount(2);
}
```

- [ ] **Step 4: Add error handling test**

```csharp
[Fact]
public async Task GetOrdersAsync_ThrowsOnApiError()
{
    var handler = new MockHttpHandler();
    handler.EnqueueResponse(HttpStatusCode.Unauthorized, new { error = "Invalid API key" });

    var service = CreateService(handler);
    var act = () => service.GetOrdersAsync(null, "ev_test");

    await act.Should().ThrowAsync<HttpRequestException>();
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Humans.Application.Tests --filter "TicketTailorServiceTests" -v n`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add tests/Humans.Application.Tests/Services/TicketTailorServiceTests.cs
git commit -m "test(tickets): add TicketTailorService tests — parsing, pagination, errors"
```

---

### Task 26: Add attendee upsert idempotency test and scale test

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/TicketSyncServiceTests.cs`

- [ ] **Step 1: Add attendee upsert idempotency test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_AttendeeUpsertDoesNotCreateDuplicates()
{
    // Arrange — order + attendee, synced twice
    var orders = new List<VendorOrderDto>
    {
        new("ord_010", "Jane", "jane@example.com", 150m, "EUR",
            null, "completed", null, Instant.FromUtc(2026, 5, 15, 10, 0), [])
    };
    var tickets = new List<VendorTicketDto>
    {
        new("tkt_010", "ord_010", "Jane Doe", "jane@example.com",
            "Full Week", 150m, "valid")
    };

    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(orders);
    _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(tickets);

    // Act — sync twice
    await _service.SyncOrdersAndAttendeesAsync();
    await _service.SyncOrdersAndAttendeesAsync();

    // Assert
    var attendeeCount = await _dbContext.TicketAttendees.CountAsync();
    attendeeCount.Should().Be(1);
}
```

- [ ] **Step 2: Add realistic scale test**

```csharp
[Fact]
public async Task SyncOrdersAndAttendeesAsync_HandlesRealisticScale()
{
    // Arrange — 500 orders with 700 attendees (representative subset of 1500+)
    var orders = Enumerable.Range(1, 500).Select(i =>
        new VendorOrderDto(
            $"ord_{i:D5}", $"Buyer {i}", $"buyer{i}@example.com",
            150m + (i % 3) * 50m, "EUR",
            i % 5 == 0 ? $"CODE{i}" : null, "completed", null,
            Instant.FromUtc(2026, 5, 1, 0, 0).Plus(Duration.FromHours(i)),
            [])
    ).ToList();

    var tickets = Enumerable.Range(1, 700).Select(i =>
        new VendorTicketDto(
            $"tkt_{i:D5}", $"ord_{(i % 500) + 1:D5}",
            $"Attendee {i}", $"attendee{i}@example.com",
            "Full Week", 150m, "valid")
    ).ToList();

    _vendorService.GetOrdersAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(orders);
    _vendorService.GetIssuedTicketsAsync(Arg.Any<Instant?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(tickets);

    // Act
    var result = await _service.SyncOrdersAndAttendeesAsync();

    // Assert
    result.OrdersSynced.Should().Be(500);
    result.AttendeesSynced.Should().Be(700);
    (await _dbContext.TicketOrders.CountAsync()).Should().Be(500);
    (await _dbContext.TicketAttendees.CountAsync()).Should().Be(700);
}
```

- [ ] **Step 3: Run all sync tests**

Run: `dotnet test tests/Humans.Application.Tests --filter "TicketSyncServiceTests" -v n`
Expected: All tests pass

- [ ] **Step 4: Commit**

```bash
git add tests/Humans.Application.Tests/Services/TicketSyncServiceTests.cs
git commit -m "test(tickets): add attendee idempotency and realistic scale tests"
```

---

### Task 27: Final build, full test suite, push to QA

- [ ] **Step 1: Build entire solution**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded with no errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Humans.slnx -v n`
Expected: All tests pass

- [ ] **Step 3: Push to origin for QA**

Run: `git push origin main`
