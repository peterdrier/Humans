# Budget Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add budget data model and admin management pages so the treasurer can build and manage annual budgets at `/Finance`.

**Architecture:** Four new domain entities (BudgetYear, BudgetGroup, BudgetCategory, BudgetLineItem) plus a BudgetAuditLog entity. New FinanceAdmin role. Dedicated FinanceController with five pages. Service layer handles budget CRUD with audit logging. All work in worktree `.worktrees/budget-phase1` on branch `feat/budget-phase1`.

**Tech Stack:** .NET 9, EF Core + PostgreSQL, NodaTime, ASP.NET Core MVC with Razor views, Bootstrap 5.3, Font Awesome 6.

---

### Task 1: Domain Enums

**Files:**
- Create: `src/Humans.Domain/Enums/BudgetYearStatus.cs`
- Create: `src/Humans.Domain/Enums/ExpenditureType.cs`

- [ ] **Step 1: Create BudgetYearStatus enum**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Status of a budget year.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum BudgetYearStatus
{
    /// <summary>
    /// Budget is being built, not visible outside admin.
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Current operational budget.
    /// </summary>
    Active = 1,

    /// <summary>
    /// Year complete, read-only.
    /// </summary>
    Closed = 2
}
```

- [ ] **Step 2: Create ExpenditureType enum**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Whether a budget category represents capital or operational expenditure.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum ExpenditureType
{
    /// <summary>
    /// Capital expenditure — investments, equipment purchases.
    /// </summary>
    CapEx = 0,

    /// <summary>
    /// Operational expenditure — recurring costs.
    /// </summary>
    OpEx = 1
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Humans.Domain`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Enums/BudgetYearStatus.cs src/Humans.Domain/Enums/ExpenditureType.cs
git commit -m "feat(budget): add BudgetYearStatus and ExpenditureType enums"
```

---

### Task 2: Domain Entities

**Files:**
- Create: `src/Humans.Domain/Entities/BudgetYear.cs`
- Create: `src/Humans.Domain/Entities/BudgetGroup.cs`
- Create: `src/Humans.Domain/Entities/BudgetCategory.cs`
- Create: `src/Humans.Domain/Entities/BudgetLineItem.cs`
- Create: `src/Humans.Domain/Entities/BudgetAuditLog.cs`
- Modify: `src/Humans.Domain/Entities/Team.cs`

- [ ] **Step 1: Create BudgetYear entity**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Top-level budget container for a fiscal year.
/// </summary>
public class BudgetYear
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Short identifier, e.g., "2026", "2027-A".
    /// </summary>
    public string Year { get; set; } = string.Empty;

    /// <summary>
    /// Display name, e.g., "2026 — Elsewhere".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Lifecycle status: Draft, Active, or Closed.
    /// </summary>
    public BudgetYearStatus Status { get; set; } = BudgetYearStatus.Draft;

    /// <summary>
    /// When this budget year was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this budget year was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to budget groups.
    /// </summary>
    public ICollection<BudgetGroup> Groups { get; } = new List<BudgetGroup>();

    /// <summary>
    /// Navigation property to audit log entries for this year.
    /// </summary>
    public ICollection<BudgetAuditLog> AuditLogs { get; } = new List<BudgetAuditLog>();
}
```

- [ ] **Step 2: Create BudgetGroup entity**

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Second-level budget container within a year (e.g., "Departments", "Site Infrastructure").
/// </summary>
public class BudgetGroup
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the parent budget year.
    /// </summary>
    public Guid BudgetYearId { get; init; }

    /// <summary>
    /// Navigation property to the parent budget year.
    /// </summary>
    public BudgetYear? BudgetYear { get; set; }

    /// <summary>
    /// Group name, e.g., "Departments", "Site Infrastructure", "Admin".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Manual display ordering.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// If true, this group is hidden from coordinators and public (e.g., Admin group with staff costs).
    /// </summary>
    public bool IsRestricted { get; set; }

    /// <summary>
    /// If true, categories were auto-generated from teams with HasBudget on year creation.
    /// </summary>
    public bool IsDepartmentGroup { get; set; }

    /// <summary>
    /// When this group was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this group was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to budget categories.
    /// </summary>
    public ICollection<BudgetCategory> Categories { get; } = new List<BudgetCategory>();
}
```

- [ ] **Step 3: Create BudgetCategory entity**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Third-level budget container within a group (e.g., "Cantina", "Sound").
/// Holds the allocated budget amount.
/// </summary>
public class BudgetCategory
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the parent budget group.
    /// </summary>
    public Guid BudgetGroupId { get; init; }

    /// <summary>
    /// Navigation property to the parent budget group.
    /// </summary>
    public BudgetGroup? BudgetGroup { get; set; }

    /// <summary>
    /// Category name, e.g., "Cantina", "Sound".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Budget allocation for this category.
    /// </summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>
    /// Whether this is capital or operational expenditure.
    /// </summary>
    public ExpenditureType ExpenditureType { get; set; } = ExpenditureType.OpEx;

    /// <summary>
    /// Optional FK to a Team. Set for department categories (auto-mapped from teams with HasBudget).
    /// </summary>
    public Guid? TeamId { get; set; }

    /// <summary>
    /// Navigation property to the linked team.
    /// </summary>
    public Team? Team { get; set; }

    /// <summary>
    /// Manual display ordering.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// When this category was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this category was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to line items.
    /// </summary>
    public ICollection<BudgetLineItem> LineItems { get; } = new List<BudgetLineItem>();
}
```

- [ ] **Step 4: Create BudgetLineItem entity**

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Detail row within a budget category (e.g., "Food", "PA System Rental").
/// </summary>
public class BudgetLineItem
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the parent budget category.
    /// </summary>
    public Guid BudgetCategoryId { get; init; }

    /// <summary>
    /// Navigation property to the parent budget category.
    /// </summary>
    public BudgetCategory? BudgetCategory { get; set; }

    /// <summary>
    /// Free-text description, e.g., "PA System Rental".
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Line item amount.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Optional FK to the team responsible for this line item.
    /// </summary>
    public Guid? ResponsibleTeamId { get; set; }

    /// <summary>
    /// Navigation property to the responsible team.
    /// </summary>
    public Team? ResponsibleTeam { get; set; }

    /// <summary>
    /// Optional notes.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Manual display ordering.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// When this line item was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this line item was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }
}
```

- [ ] **Step 5: Create BudgetAuditLog entity**

```csharp
using NodaTime;
using Humans.Domain.Entities;

namespace Humans.Domain.Entities;

/// <summary>
/// Append-only audit log for budget changes. No UPDATE or DELETE allowed.
/// </summary>
public class BudgetAuditLog
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the budget year this change belongs to.
    /// </summary>
    public Guid BudgetYearId { get; init; }

    /// <summary>
    /// Navigation property to the budget year.
    /// </summary>
    public BudgetYear? BudgetYear { get; set; }

    /// <summary>
    /// Type of entity changed: "BudgetYear", "BudgetGroup", "BudgetCategory", "BudgetLineItem".
    /// </summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>
    /// ID of the changed entity.
    /// </summary>
    public Guid EntityId { get; init; }

    /// <summary>
    /// Which field changed (null for create/delete operations).
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Previous value (null for create operations).
    /// </summary>
    public string? OldValue { get; init; }

    /// <summary>
    /// New value (null for delete operations).
    /// </summary>
    public string? NewValue { get; init; }

    /// <summary>
    /// Human-readable description of the change.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// FK to the user who made the change.
    /// </summary>
    public Guid ActorUserId { get; init; }

    /// <summary>
    /// Navigation property to the actor.
    /// </summary>
    public User? ActorUser { get; set; }

    /// <summary>
    /// When the change occurred.
    /// </summary>
    public Instant OccurredAt { get; init; }
}
```

- [ ] **Step 6: Add HasBudget to Team entity**

In `src/Humans.Domain/Entities/Team.cs`, add before the `ParentTeamId` property (around line 113):

```csharp
/// <summary>
/// Whether this team participates in budget planning.
/// When true, a BudgetCategory is auto-created under the Departments group on budget year creation.
/// </summary>
public bool HasBudget { get; set; }
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/Humans.Domain`
Expected: Build succeeded

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Domain/Entities/BudgetYear.cs src/Humans.Domain/Entities/BudgetGroup.cs src/Humans.Domain/Entities/BudgetCategory.cs src/Humans.Domain/Entities/BudgetLineItem.cs src/Humans.Domain/Entities/BudgetAuditLog.cs src/Humans.Domain/Entities/Team.cs
git commit -m "feat(budget): add budget domain entities and HasBudget on Team"
```

---

### Task 3: FinanceAdmin Role

**Files:**
- Modify: `src/Humans.Domain/Constants/RoleNames.cs`
- Modify: `src/Humans.Domain/Constants/RoleGroups.cs`
- Modify: `src/Humans.Web/Authorization/RoleChecks.cs`

- [ ] **Step 1: Add FinanceAdmin to RoleNames**

In `src/Humans.Domain/Constants/RoleNames.cs`, add after the `HumanAdmin` constant (after line 64):

```csharp
/// <summary>
/// Finance Administrator — can manage budgets, budget years, groups, categories,
/// and line items. Full access to the Finance section.
/// </summary>
public const string FinanceAdmin = "FinanceAdmin";
```

- [ ] **Step 2: Add FinanceAdminOrAdmin to RoleGroups**

In `src/Humans.Domain/Constants/RoleGroups.cs`, add after the `HumanAdminOrAdmin` constant (after line 31):

```csharp
public const string FinanceAdminOrAdmin = RoleNames.FinanceAdmin + "," + RoleNames.Admin;
```

- [ ] **Step 3: Add FinanceAdmin to RoleChecks**

In `src/Humans.Web/Authorization/RoleChecks.cs`:

Add `RoleNames.FinanceAdmin` to both `AdminAssignableRoles` and `BoardAssignableRoles` arrays (after `RoleNames.FeedbackAdmin` in each):

```csharp
private static readonly string[] AdminAssignableRoles =
[
    RoleNames.Admin,
    RoleNames.Board,
    RoleNames.HumanAdmin,
    RoleNames.TeamsAdmin,
    RoleNames.CampAdmin,
    RoleNames.TicketAdmin,
    RoleNames.NoInfoAdmin,
    RoleNames.FeedbackAdmin,
    RoleNames.FinanceAdmin,
    RoleNames.ConsentCoordinator,
    RoleNames.VolunteerCoordinator
];

private static readonly string[] BoardAssignableRoles =
[
    RoleNames.Board,
    RoleNames.HumanAdmin,
    RoleNames.TeamsAdmin,
    RoleNames.CampAdmin,
    RoleNames.TicketAdmin,
    RoleNames.NoInfoAdmin,
    RoleNames.FeedbackAdmin,
    RoleNames.FinanceAdmin,
    RoleNames.ConsentCoordinator,
    RoleNames.VolunteerCoordinator
];
```

Add the `CanAccessFinance` and `IsFinanceAdmin` methods after the `IsFeedbackAdmin` method (after line 115):

```csharp
public static bool IsFinanceAdmin(ClaimsPrincipal user)
{
    return IsAdmin(user) || user.IsInRole(RoleNames.FinanceAdmin);
}

public static bool CanAccessFinance(ClaimsPrincipal user)
{
    return IsFinanceAdmin(user);
}
```

Also add `RoleNames.FinanceAdmin` to `BypassesMembershipRequirement` (after the FeedbackAdmin line):

```csharp
user.IsInRole(RoleNames.FeedbackAdmin) ||
user.IsInRole(RoleNames.FinanceAdmin) ||
```

And add to `CanManageRole` in the Board/HumanAdmin branch:

```csharp
string.Equals(roleName, RoleNames.FinanceAdmin, StringComparison.Ordinal) ||
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Constants/RoleNames.cs src/Humans.Domain/Constants/RoleGroups.cs src/Humans.Web/Authorization/RoleChecks.cs
git commit -m "feat(budget): add FinanceAdmin role"
```

---

### Task 4: EF Core Configurations

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/BudgetYearConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/BudgetGroupConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/BudgetCategoryConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/BudgetLineItemConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/BudgetAuditLogConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Create BudgetYearConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetYearConfiguration : IEntityTypeConfiguration<BudgetYear>
{
    public void Configure(EntityTypeBuilder<BudgetYear> builder)
    {
        builder.ToTable("budget_years");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Year)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(b => b.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(b => b.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(b => b.CreatedAt)
            .IsRequired();

        builder.Property(b => b.UpdatedAt)
            .IsRequired();

        builder.HasMany(b => b.Groups)
            .WithOne(g => g.BudgetYear)
            .HasForeignKey(g => g.BudgetYearId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(b => b.AuditLogs)
            .WithOne(a => a.BudgetYear)
            .HasForeignKey(a => a.BudgetYearId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(b => b.Year)
            .IsUnique();

        builder.HasIndex(b => b.Status);
    }
}
```

- [ ] **Step 2: Create BudgetGroupConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetGroupConfiguration : IEntityTypeConfiguration<BudgetGroup>
{
    public void Configure(EntityTypeBuilder<BudgetGroup> builder)
    {
        builder.ToTable("budget_groups");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(g => g.SortOrder)
            .IsRequired();

        builder.Property(g => g.IsRestricted)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(g => g.IsDepartmentGroup)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(g => g.CreatedAt)
            .IsRequired();

        builder.Property(g => g.UpdatedAt)
            .IsRequired();

        builder.HasMany(g => g.Categories)
            .WithOne(c => c.BudgetGroup)
            .HasForeignKey(c => c.BudgetGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => new { g.BudgetYearId, g.SortOrder });
    }
}
```

- [ ] **Step 3: Create BudgetCategoryConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetCategoryConfiguration : IEntityTypeConfiguration<BudgetCategory>
{
    public void Configure(EntityTypeBuilder<BudgetCategory> builder)
    {
        builder.ToTable("budget_categories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(c => c.AllocatedAmount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(c => c.ExpenditureType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(c => c.SortOrder)
            .IsRequired();

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.Property(c => c.UpdatedAt)
            .IsRequired();

        builder.HasOne(c => c.Team)
            .WithMany()
            .HasForeignKey(c => c.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(c => c.LineItems)
            .WithOne(l => l.BudgetCategory)
            .HasForeignKey(l => l.BudgetCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.BudgetGroupId, c.SortOrder });

        builder.HasIndex(c => c.TeamId)
            .HasFilter("\"TeamId\" IS NOT NULL");
    }
}
```

- [ ] **Step 4: Create BudgetLineItemConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetLineItemConfiguration : IEntityTypeConfiguration<BudgetLineItem>
{
    public void Configure(EntityTypeBuilder<BudgetLineItem> builder)
    {
        builder.ToTable("budget_line_items");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Description)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(l => l.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(l => l.Notes)
            .HasMaxLength(2000);

        builder.Property(l => l.SortOrder)
            .IsRequired();

        builder.Property(l => l.CreatedAt)
            .IsRequired();

        builder.Property(l => l.UpdatedAt)
            .IsRequired();

        builder.HasOne(l => l.ResponsibleTeam)
            .WithMany()
            .HasForeignKey(l => l.ResponsibleTeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(l => new { l.BudgetCategoryId, l.SortOrder });

        builder.HasIndex(l => l.ResponsibleTeamId)
            .HasFilter("\"ResponsibleTeamId\" IS NOT NULL");
    }
}
```

- [ ] **Step 5: Create BudgetAuditLogConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class BudgetAuditLogConfiguration : IEntityTypeConfiguration<BudgetAuditLog>
{
    public void Configure(EntityTypeBuilder<BudgetAuditLog> builder)
    {
        builder.ToTable("budget_audit_logs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.FieldName)
            .HasMaxLength(100);

        builder.Property(a => a.OldValue)
            .HasMaxLength(1000);

        builder.Property(a => a.NewValue)
            .HasMaxLength(1000);

        builder.Property(a => a.Description)
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(a => a.OccurredAt)
            .IsRequired();

        builder.HasOne(a => a.ActorUser)
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.BudgetYearId);
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
```

- [ ] **Step 6: Add HasBudget to TeamConfiguration**

In `src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs`, add after the `builder.Property(t => t.ShowCoordinatorsOnPublicPage)` block (after line 67):

```csharp
builder.Property(t => t.HasBudget)
    .IsRequired()
    .HasDefaultValue(false);
```

Also add `HasBudget = false` to each of the 6 seed data anonymous objects (after `ShowCoordinatorsOnPublicPage = true` in each).

- [ ] **Step 7: Add DbSets to HumansDbContext**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, add after the `CommunicationPreferences` DbSet (after line 66):

```csharp
public DbSet<BudgetYear> BudgetYears => Set<BudgetYear>();
public DbSet<BudgetGroup> BudgetGroups => Set<BudgetGroup>();
public DbSet<BudgetCategory> BudgetCategories => Set<BudgetCategory>();
public DbSet<BudgetLineItem> BudgetLineItems => Set<BudgetLineItem>();
public DbSet<BudgetAuditLog> BudgetAuditLogs => Set<BudgetAuditLog>();
```

- [ ] **Step 8: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/BudgetYearConfiguration.cs src/Humans.Infrastructure/Data/Configurations/BudgetGroupConfiguration.cs src/Humans.Infrastructure/Data/Configurations/BudgetCategoryConfiguration.cs src/Humans.Infrastructure/Data/Configurations/BudgetLineItemConfiguration.cs src/Humans.Infrastructure/Data/Configurations/BudgetAuditLogConfiguration.cs src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(budget): EF Core configurations and DbSets"
```

---

### Task 5: EF Core Migration

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/{timestamp}_AddBudgetEntities.cs` (generated)

- [ ] **Step 1: Generate the migration**

Run from the repo root:

```bash
dotnet ef migrations add AddBudgetEntities --project src/Humans.Infrastructure --startup-project src/Humans.Web --output-dir Migrations
```

Expected: Migration files created in `src/Humans.Infrastructure/Migrations/`

- [ ] **Step 2: Review the generated migration**

Read the generated `*_AddBudgetEntities.cs` file. Verify it creates:
- `budget_years` table with all columns
- `budget_groups` table with FK to budget_years
- `budget_categories` table with FKs to budget_groups and teams
- `budget_line_items` table with FKs to budget_categories and teams
- `budget_audit_logs` table with FKs to budget_years and users
- `HasBudget` column on `teams` table
- All expected indexes

Do NOT edit the migration file.

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(budget): add EF Core migration for budget entities"
```

---

### Task 6: Budget Service Interface and Implementation

**Files:**
- Create: `src/Humans.Application/Interfaces/IBudgetService.cs`
- Create: `src/Humans.Infrastructure/Services/BudgetService.cs`
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

- [ ] **Step 1: Create IBudgetService interface**

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for managing budget years, groups, categories, and line items.
/// </summary>
public interface IBudgetService
{
    // Budget Years
    Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync();
    Task<BudgetYear?> GetYearByIdAsync(Guid id);
    Task<BudgetYear?> GetActiveYearAsync();
    Task<BudgetYear> CreateYearAsync(string year, string name, Guid actorUserId);
    Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId);
    Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId);
    Task DeleteYearAsync(Guid yearId, Guid actorUserId);

    // Budget Groups
    Task<BudgetGroup> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId);
    Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId);
    Task DeleteGroupAsync(Guid groupId, Guid actorUserId);

    // Budget Categories
    Task<BudgetCategory?> GetCategoryByIdAsync(Guid id);
    Task<BudgetCategory> CreateCategoryAsync(Guid budgetGroupId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid? teamId, Guid actorUserId);
    Task UpdateCategoryAsync(Guid categoryId, string name, decimal allocatedAmount, ExpenditureType expenditureType, Guid actorUserId);
    Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId);

    // Budget Line Items
    Task<BudgetLineItem> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount, Guid? responsibleTeamId, string? notes, Guid actorUserId);
    Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount, Guid? responsibleTeamId, string? notes, Guid actorUserId);
    Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId);

    // Audit Log
    Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId);
}
```

- [ ] **Step 2: Create BudgetService implementation**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class BudgetService : IBudgetService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<BudgetService> _logger;

    public BudgetService(HumansDbContext dbContext, IClock clock, ILogger<BudgetService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    // --- Budget Years ---

    public async Task<IReadOnlyList<BudgetYear>> GetAllYearsAsync()
    {
        return await _dbContext.BudgetYears
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
            .OrderByDescending(y => y.Year)
            .ToListAsync();
    }

    public async Task<BudgetYear?> GetYearByIdAsync(Guid id)
    {
        return await _dbContext.BudgetYears
            .Include(y => y.Groups.OrderBy(g => g.SortOrder))
                .ThenInclude(g => g.Categories.OrderBy(c => c.SortOrder))
                    .ThenInclude(c => c.LineItems)
            .Include(y => y.Groups)
                .ThenInclude(g => g.Categories)
                    .ThenInclude(c => c.Team)
            .FirstOrDefaultAsync(y => y.Id == id);
    }

    public async Task<BudgetYear?> GetActiveYearAsync()
    {
        var activeYear = await _dbContext.BudgetYears
            .FirstOrDefaultAsync(y => y.Status == BudgetYearStatus.Active);

        if (activeYear is null)
            return null;

        return await GetYearByIdAsync(activeYear.Id);
    }

    public async Task<BudgetYear> CreateYearAsync(string year, string name, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();

        var budgetYear = new BudgetYear
        {
            Id = Guid.NewGuid(),
            Year = year,
            Name = name,
            Status = BudgetYearStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetYears.Add(budgetYear);

        // Auto-create Departments group with budget-enabled teams
        var departmentGroup = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYear.Id,
            Name = "Departments",
            SortOrder = 0,
            IsRestricted = false,
            IsDepartmentGroup = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetGroups.Add(departmentGroup);

        var budgetTeams = await _dbContext.Teams
            .Where(t => t.HasBudget && t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync();

        var sortOrder = 0;
        foreach (var team in budgetTeams)
        {
            var category = new BudgetCategory
            {
                Id = Guid.NewGuid(),
                BudgetGroupId = departmentGroup.Id,
                Name = team.Name,
                AllocatedAmount = 0,
                ExpenditureType = ExpenditureType.OpEx,
                TeamId = team.Id,
                SortOrder = sortOrder++,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.BudgetCategories.Add(category);
        }

        LogAudit(budgetYear.Id, "BudgetYear", budgetYear.Id, null, null, null,
            $"Created budget year '{name}' ({year})", actorUserId, now);

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Budget year {Year} ({Name}) created by {ActorUserId} with {TeamCount} department categories",
            year, name, actorUserId, budgetTeams.Count);

        return budgetYear;
    }

    public async Task UpdateYearStatusAsync(Guid yearId, BudgetYearStatus status, Guid actorUserId)
    {
        var year = await _dbContext.BudgetYears.FindAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        var oldStatus = year.Status;
        var now = _clock.GetCurrentInstant();

        // If activating, close any currently active year
        if (status == BudgetYearStatus.Active)
        {
            var currentActive = await _dbContext.BudgetYears
                .FirstOrDefaultAsync(y => y.Status == BudgetYearStatus.Active && y.Id != yearId);

            if (currentActive is not null)
            {
                var prevStatus = currentActive.Status;
                currentActive.Status = BudgetYearStatus.Closed;
                currentActive.UpdatedAt = now;

                LogAudit(currentActive.Id, "BudgetYear", currentActive.Id, nameof(BudgetYear.Status),
                    prevStatus.ToString(), BudgetYearStatus.Closed.ToString(),
                    $"Auto-closed budget year '{currentActive.Name}' (replaced by '{year.Name}')", actorUserId, now);
            }
        }

        year.Status = status;
        year.UpdatedAt = now;

        LogAudit(year.Id, "BudgetYear", year.Id, nameof(BudgetYear.Status),
            oldStatus.ToString(), status.ToString(),
            $"Changed budget year '{year.Name}' status from {oldStatus} to {status}", actorUserId, now);

        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateYearAsync(Guid yearId, string year, string name, Guid actorUserId)
    {
        var entity = await _dbContext.BudgetYears.FindAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        var now = _clock.GetCurrentInstant();

        if (!string.Equals(entity.Year, year, StringComparison.Ordinal))
        {
            LogAudit(entity.Id, "BudgetYear", entity.Id, nameof(BudgetYear.Year),
                entity.Year, year, $"Changed year identifier from '{entity.Year}' to '{year}'", actorUserId, now);
            entity.Year = year;
        }

        if (!string.Equals(entity.Name, name, StringComparison.Ordinal))
        {
            LogAudit(entity.Id, "BudgetYear", entity.Id, nameof(BudgetYear.Name),
                entity.Name, name, $"Changed year name from '{entity.Name}' to '{name}'", actorUserId, now);
            entity.Name = name;
        }

        entity.UpdatedAt = now;
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteYearAsync(Guid yearId, Guid actorUserId)
    {
        var year = await _dbContext.BudgetYears.FindAsync(yearId)
            ?? throw new InvalidOperationException($"Budget year {yearId} not found");

        if (year.Status == BudgetYearStatus.Active)
            throw new InvalidOperationException("Cannot delete an active budget year. Close it first.");

        var now = _clock.GetCurrentInstant();

        LogAudit(year.Id, "BudgetYear", year.Id, null, null, null,
            $"Deleted budget year '{year.Name}' ({year.Year})", actorUserId, now);

        _dbContext.BudgetYears.Remove(year);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Budget year {YearId} ({Year}) deleted by {ActorUserId}", yearId, year.Year, actorUserId);
    }

    // --- Budget Groups ---

    public async Task<BudgetGroup> CreateGroupAsync(Guid budgetYearId, string name, bool isRestricted, Guid actorUserId)
    {
        var now = _clock.GetCurrentInstant();
        var maxSortOrder = await _dbContext.BudgetGroups
            .Where(g => g.BudgetYearId == budgetYearId)
            .MaxAsync(g => (int?)g.SortOrder) ?? -1;

        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            Name = name,
            SortOrder = maxSortOrder + 1,
            IsRestricted = isRestricted,
            IsDepartmentGroup = false,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetGroups.Add(group);

        LogAudit(budgetYearId, "BudgetGroup", group.Id, null, null, null,
            $"Created budget group '{name}'", actorUserId, now);

        await _dbContext.SaveChangesAsync();
        return group;
    }

    public async Task UpdateGroupAsync(Guid groupId, string name, int sortOrder, bool isRestricted, Guid actorUserId)
    {
        var group = await _dbContext.BudgetGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"Budget group {groupId} not found");

        var now = _clock.GetCurrentInstant();

        if (!string.Equals(group.Name, name, StringComparison.Ordinal))
        {
            LogAudit(group.BudgetYearId, "BudgetGroup", group.Id, nameof(BudgetGroup.Name),
                group.Name, name, $"Renamed group from '{group.Name}' to '{name}'", actorUserId, now);
            group.Name = name;
        }

        if (group.SortOrder != sortOrder)
        {
            group.SortOrder = sortOrder;
        }

        if (group.IsRestricted != isRestricted)
        {
            LogAudit(group.BudgetYearId, "BudgetGroup", group.Id, nameof(BudgetGroup.IsRestricted),
                group.IsRestricted.ToString(), isRestricted.ToString(),
                $"Changed group '{group.Name}' restricted flag to {isRestricted}", actorUserId, now);
            group.IsRestricted = isRestricted;
        }

        group.UpdatedAt = now;
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteGroupAsync(Guid groupId, Guid actorUserId)
    {
        var group = await _dbContext.BudgetGroups.FindAsync(groupId)
            ?? throw new InvalidOperationException($"Budget group {groupId} not found");

        var now = _clock.GetCurrentInstant();

        LogAudit(group.BudgetYearId, "BudgetGroup", group.Id, null, null, null,
            $"Deleted budget group '{group.Name}'", actorUserId, now);

        _dbContext.BudgetGroups.Remove(group);
        await _dbContext.SaveChangesAsync();
    }

    // --- Budget Categories ---

    public async Task<BudgetCategory?> GetCategoryByIdAsync(Guid id)
    {
        return await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
                .ThenInclude(g => g!.BudgetYear)
            .Include(c => c.LineItems.OrderBy(l => l.SortOrder))
                .ThenInclude(l => l.ResponsibleTeam)
            .Include(c => c.Team)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<BudgetCategory> CreateCategoryAsync(Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid actorUserId)
    {
        var group = await _dbContext.BudgetGroups.FindAsync(budgetGroupId)
            ?? throw new InvalidOperationException($"Budget group {budgetGroupId} not found");

        var now = _clock.GetCurrentInstant();
        var maxSortOrder = await _dbContext.BudgetCategories
            .Where(c => c.BudgetGroupId == budgetGroupId)
            .MaxAsync(c => (int?)c.SortOrder) ?? -1;

        var category = new BudgetCategory
        {
            Id = Guid.NewGuid(),
            BudgetGroupId = budgetGroupId,
            Name = name,
            AllocatedAmount = allocatedAmount,
            ExpenditureType = expenditureType,
            TeamId = teamId,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetCategories.Add(category);

        LogAudit(group.BudgetYearId, "BudgetCategory", category.Id, null, null, null,
            $"Created category '{name}' in group '{group.Name}' with allocation {allocatedAmount:C}", actorUserId, now);

        await _dbContext.SaveChangesAsync();
        return category;
    }

    public async Task UpdateCategoryAsync(Guid categoryId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid actorUserId)
    {
        var category = await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId)
            ?? throw new InvalidOperationException($"Budget category {categoryId} not found");

        var now = _clock.GetCurrentInstant();

        if (!string.Equals(category.Name, name, StringComparison.Ordinal))
        {
            LogAudit(category.BudgetGroup!.BudgetYearId, "BudgetCategory", category.Id, nameof(BudgetCategory.Name),
                category.Name, name, $"Renamed category from '{category.Name}' to '{name}'", actorUserId, now);
            category.Name = name;
        }

        if (category.AllocatedAmount != allocatedAmount)
        {
            LogAudit(category.BudgetGroup!.BudgetYearId, "BudgetCategory", category.Id, nameof(BudgetCategory.AllocatedAmount),
                category.AllocatedAmount.ToString("F2"), allocatedAmount.ToString("F2"),
                $"Changed '{category.Name}' allocation from {category.AllocatedAmount:C} to {allocatedAmount:C}", actorUserId, now);
            category.AllocatedAmount = allocatedAmount;
        }

        if (category.ExpenditureType != expenditureType)
        {
            LogAudit(category.BudgetGroup!.BudgetYearId, "BudgetCategory", category.Id, nameof(BudgetCategory.ExpenditureType),
                category.ExpenditureType.ToString(), expenditureType.ToString(),
                $"Changed '{category.Name}' expenditure type from {category.ExpenditureType} to {expenditureType}", actorUserId, now);
            category.ExpenditureType = expenditureType;
        }

        category.UpdatedAt = now;
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteCategoryAsync(Guid categoryId, Guid actorUserId)
    {
        var category = await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == categoryId)
            ?? throw new InvalidOperationException($"Budget category {categoryId} not found");

        var now = _clock.GetCurrentInstant();

        LogAudit(category.BudgetGroup!.BudgetYearId, "BudgetCategory", category.Id, null, null, null,
            $"Deleted category '{category.Name}'", actorUserId, now);

        _dbContext.BudgetCategories.Remove(category);
        await _dbContext.SaveChangesAsync();
    }

    // --- Budget Line Items ---

    public async Task<BudgetLineItem> CreateLineItemAsync(Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, Guid actorUserId)
    {
        var category = await _dbContext.BudgetCategories
            .Include(c => c.BudgetGroup)
            .FirstOrDefaultAsync(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException($"Budget category {budgetCategoryId} not found");

        var now = _clock.GetCurrentInstant();
        var maxSortOrder = await _dbContext.BudgetLineItems
            .Where(l => l.BudgetCategoryId == budgetCategoryId)
            .MaxAsync(l => (int?)l.SortOrder) ?? -1;

        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = budgetCategoryId,
            Description = description,
            Amount = amount,
            ResponsibleTeamId = responsibleTeamId,
            Notes = notes,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetLineItems.Add(lineItem);

        LogAudit(category.BudgetGroup!.BudgetYearId, "BudgetLineItem", lineItem.Id, null, null, null,
            $"Created line item '{description}' ({amount:C}) in category '{category.Name}'", actorUserId, now);

        await _dbContext.SaveChangesAsync();
        return lineItem;
    }

    public async Task UpdateLineItemAsync(Guid lineItemId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, Guid actorUserId)
    {
        var lineItem = await _dbContext.BudgetLineItems
            .Include(l => l.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(l => l.Id == lineItemId)
            ?? throw new InvalidOperationException($"Budget line item {lineItemId} not found");

        var yearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        var now = _clock.GetCurrentInstant();

        if (!string.Equals(lineItem.Description, description, StringComparison.Ordinal))
        {
            LogAudit(yearId, "BudgetLineItem", lineItem.Id, nameof(BudgetLineItem.Description),
                lineItem.Description, description,
                $"Renamed line item from '{lineItem.Description}' to '{description}'", actorUserId, now);
            lineItem.Description = description;
        }

        if (lineItem.Amount != amount)
        {
            LogAudit(yearId, "BudgetLineItem", lineItem.Id, nameof(BudgetLineItem.Amount),
                lineItem.Amount.ToString("F2"), amount.ToString("F2"),
                $"Changed '{lineItem.Description}' amount from {lineItem.Amount:C} to {amount:C}", actorUserId, now);
            lineItem.Amount = amount;
        }

        if (lineItem.ResponsibleTeamId != responsibleTeamId)
        {
            LogAudit(yearId, "BudgetLineItem", lineItem.Id, nameof(BudgetLineItem.ResponsibleTeamId),
                lineItem.ResponsibleTeamId?.ToString(), responsibleTeamId?.ToString(),
                $"Changed '{lineItem.Description}' responsible team", actorUserId, now);
            lineItem.ResponsibleTeamId = responsibleTeamId;
        }

        lineItem.Notes = notes;
        lineItem.UpdatedAt = now;
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteLineItemAsync(Guid lineItemId, Guid actorUserId)
    {
        var lineItem = await _dbContext.BudgetLineItems
            .Include(l => l.BudgetCategory)
                .ThenInclude(c => c!.BudgetGroup)
            .FirstOrDefaultAsync(l => l.Id == lineItemId)
            ?? throw new InvalidOperationException($"Budget line item {lineItemId} not found");

        var yearId = lineItem.BudgetCategory!.BudgetGroup!.BudgetYearId;
        var now = _clock.GetCurrentInstant();

        LogAudit(yearId, "BudgetLineItem", lineItem.Id, null, null, null,
            $"Deleted line item '{lineItem.Description}' ({lineItem.Amount:C})", actorUserId, now);

        _dbContext.BudgetLineItems.Remove(lineItem);
        await _dbContext.SaveChangesAsync();
    }

    // --- Audit Log ---

    public async Task<IReadOnlyList<BudgetAuditLog>> GetAuditLogAsync(Guid? budgetYearId)
    {
        var query = _dbContext.BudgetAuditLogs
            .Include(a => a.ActorUser)
            .AsQueryable();

        if (budgetYearId.HasValue)
        {
            query = query.Where(a => a.BudgetYearId == budgetYearId.Value);
        }

        return await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(500)
            .ToListAsync();
    }

    // --- Private Helpers ---

    private void LogAudit(Guid budgetYearId, string entityType, Guid entityId,
        string? fieldName, string? oldValue, string? newValue,
        string description, Guid actorUserId, Instant occurredAt)
    {
        _dbContext.BudgetAuditLogs.Add(new BudgetAuditLog
        {
            Id = Guid.NewGuid(),
            BudgetYearId = budgetYearId,
            EntityType = entityType,
            EntityId = entityId,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Description = description,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt
        });
    }
}
```

- [ ] **Step 3: Register in DI**

In `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`, add after the `IFeedbackService` registration (after line 88):

```csharp
services.AddScoped<IBudgetService, BudgetService>();
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/IBudgetService.cs src/Humans.Infrastructure/Services/BudgetService.cs src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs
git commit -m "feat(budget): add IBudgetService and BudgetService with audit logging"
```

---

### Task 7: FinanceController

**Files:**
- Create: `src/Humans.Web/Controllers/FinanceController.cs`

- [ ] **Step 1: Create FinanceController**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.FinanceAdminOrAdmin)]
[Route("Finance")]
public class FinanceController : HumansControllerBase
{
    private readonly IBudgetService _budgetService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<FinanceController> _logger;

    public FinanceController(
        IBudgetService budgetService,
        HumansDbContext dbContext,
        UserManager<User> userManager,
        ILogger<FinanceController> logger)
        : base(userManager)
    {
        _budgetService = budgetService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var activeYear = await _budgetService.GetActiveYearAsync();
            if (activeYear is null)
            {
                ViewBag.Years = await _budgetService.GetAllYearsAsync();
                return View("NoActiveYear");
            }

            ViewBag.AllYears = await _budgetService.GetAllYearsAsync();
            return View("YearDetail", activeYear);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading finance index");
            SetError("Failed to load budget data.");
            return View("NoActiveYear");
        }
    }

    [HttpGet("Years/{id:guid}")]
    public async Task<IActionResult> YearDetail(Guid id)
    {
        try
        {
            var year = await _budgetService.GetYearByIdAsync(id);
            if (year is null)
                return NotFound();

            ViewBag.AllYears = await _budgetService.GetAllYearsAsync();
            return View(year);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget year {YearId}", id);
            SetError("Failed to load budget year.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Categories/{id:guid}")]
    public async Task<IActionResult> CategoryDetail(Guid id)
    {
        try
        {
            var category = await _budgetService.GetCategoryByIdAsync(id);
            if (category is null)
                return NotFound();

            ViewBag.Teams = await _dbContext.Teams
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync();

            return View(category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget category {CategoryId}", id);
            SetError("Failed to load category.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("AuditLog/{yearId:guid?}")]
    public async Task<IActionResult> AuditLog(Guid? yearId)
    {
        try
        {
            if (!yearId.HasValue)
            {
                var active = await _budgetService.GetActiveYearAsync();
                yearId = active?.Id;
            }

            var entries = await _budgetService.GetAuditLogAsync(yearId);
            ViewBag.YearId = yearId;
            ViewBag.Years = await _budgetService.GetAllYearsAsync();
            return View(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading budget audit log");
            SetError("Failed to load audit log.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("Admin")]
    public async Task<IActionResult> Admin()
    {
        try
        {
            var years = await _budgetService.GetAllYearsAsync();
            return View(years);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading finance admin");
            SetError("Failed to load finance admin.");
            return RedirectToAction(nameof(Index));
        }
    }

    // --- POST Actions ---

    [HttpPost("Years/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateYear(string year, string name)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var budgetYear = await _budgetService.CreateYearAsync(year, name, user.Id);
            SetSuccess($"Budget year '{name}' created.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating budget year {Year}", year);
            SetError($"Failed to create budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/UpdateStatus")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateYearStatus(Guid id, BudgetYearStatus status)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateYearStatusAsync(id, status, user.Id);
            SetSuccess($"Budget year status updated to {status}.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget year {YearId} status to {Status}", id, status);
            SetError($"Failed to update status: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateYear(Guid id, string year, string name)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateYearAsync(id, year, name, user.Id);
            SetSuccess("Budget year updated.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget year {YearId}", id);
            SetError($"Failed to update budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Years/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteYear(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.DeleteYearAsync(id, user.Id);
            SetSuccess("Budget year deleted.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting budget year {YearId}", id);
            SetError($"Failed to delete budget year: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(Guid budgetYearId, string name, bool isRestricted)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.CreateGroupAsync(budgetYearId, name, isRestricted, user.Id);
            SetSuccess($"Group '{name}' created.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating budget group in year {YearId}", budgetYearId);
            SetError($"Failed to create group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGroup(Guid id, string name, int sortOrder, bool isRestricted, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateGroupAsync(id, name, sortOrder, isRestricted, user.Id);
            SetSuccess($"Group '{name}' updated.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget group {GroupId}", id);
            SetError($"Failed to update group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Groups/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGroup(Guid id)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.DeleteGroupAsync(id, user.Id);
            SetSuccess("Group deleted.");
            return RedirectToAction(nameof(Admin));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting budget group {GroupId}", id);
            SetError($"Failed to delete group: {ex.Message}");
            return RedirectToAction(nameof(Admin));
        }
    }

    [HttpPost("Categories/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(Guid budgetGroupId, string name, decimal allocatedAmount,
        ExpenditureType expenditureType, Guid? teamId, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.CreateCategoryAsync(budgetGroupId, name, allocatedAmount, expenditureType, teamId, user.Id);
            SetSuccess($"Category '{name}' created.");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating budget category in group {GroupId}", budgetGroupId);
            SetError($"Failed to create category: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
    }

    [HttpPost("Categories/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory(Guid id, string name, decimal allocatedAmount,
        ExpenditureType expenditureType)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateCategoryAsync(id, name, allocatedAmount, expenditureType, user.Id);
            SetSuccess($"Category '{name}' updated.");
            return RedirectToAction(nameof(CategoryDetail), new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating budget category {CategoryId}", id);
            SetError($"Failed to update category: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id });
        }
    }

    [HttpPost("Categories/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(Guid id, Guid budgetYearId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.DeleteCategoryAsync(id, user.Id);
            SetSuccess("Category deleted.");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting budget category {CategoryId}", id);
            SetError($"Failed to delete category: {ex.Message}");
            return RedirectToAction(nameof(YearDetail), new { id = budgetYearId });
        }
    }

    [HttpPost("LineItems/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateLineItem(Guid budgetCategoryId, string description, decimal amount,
        Guid? responsibleTeamId, string? notes)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.CreateLineItemAsync(budgetCategoryId, description, amount, responsibleTeamId, notes, user.Id);
            SetSuccess($"Line item '{description}' created.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating line item in category {CategoryId}", budgetCategoryId);
            SetError($"Failed to create line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }

    [HttpPost("LineItems/{id:guid}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLineItem(Guid id, string description, decimal amount,
        Guid? responsibleTeamId, string? notes, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.UpdateLineItemAsync(id, description, amount, responsibleTeamId, notes, user.Id);
            SetSuccess($"Line item '{description}' updated.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating line item {LineItemId}", id);
            SetError($"Failed to update line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }

    [HttpPost("LineItems/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLineItem(Guid id, Guid budgetCategoryId)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await _budgetService.DeleteLineItemAsync(id, user.Id);
            SetSuccess("Line item deleted.");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting line item {LineItemId}", id);
            SetError($"Failed to delete line item: {ex.Message}");
            return RedirectToAction(nameof(CategoryDetail), new { id = budgetCategoryId });
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/FinanceController.cs
git commit -m "feat(budget): add FinanceController with CRUD actions"
```

---

### Task 8: Razor Views

**Files:**
- Create: `src/Humans.Web/Views/Finance/NoActiveYear.cshtml`
- Create: `src/Humans.Web/Views/Finance/YearDetail.cshtml`
- Create: `src/Humans.Web/Views/Finance/CategoryDetail.cshtml`
- Create: `src/Humans.Web/Views/Finance/AuditLog.cshtml`
- Create: `src/Humans.Web/Views/Finance/Admin.cshtml`

This task creates all five views. Each view follows existing admin page conventions: Bootstrap 5.3 cards, Font Awesome 6 icons, `<vc:temp-data-alerts />` for flash messages, `@Html.AntiForgeryToken()` on forms.

**Note:** Because views are large HTML files, subagents implementing this task should read an existing view for style reference (e.g., `src/Humans.Web/Views/Admin/Index.cshtml` or `src/Humans.Web/Views/Admin/EmailOutbox.cshtml`) and build the Finance views to match.

- [ ] **Step 1: Create NoActiveYear.cshtml**

This is the fallback page when no budget year is active. Shows a list of existing years with links, and a prompt to go to Finance Admin.

Key elements:
- Title: "Finance"
- Message: "No active budget year."
- Link to `/Finance/Admin` to create or activate one
- If draft years exist, list them with links

- [ ] **Step 2: Create YearDetail.cshtml**

Model: `@model Humans.Domain.Entities.BudgetYear`

Key elements:
- Title: year name, status badge (Draft/Active/Closed)
- Year picker dropdown from `ViewBag.AllYears` to switch between years
- Link to Audit Log and Finance Admin
- For each group (ordered by SortOrder):
  - Collapsible card with group name, category count, total allocation
  - IsRestricted badge if applicable
  - Category table within the card: Name, Allocated Amount, Line Items Total, Unallocated, ExpenditureType badge, "Manage →" link
  - "Add Category" form at bottom of each group card
- Summary cards at top: total budget, total allocated, total in line items

- [ ] **Step 3: Create CategoryDetail.cshtml**

Model: `@model Humans.Domain.Entities.BudgetCategory`

Key elements:
- Breadcrumb: Finance > Year Name > Group Name > Category Name
- Summary cards: Allocated, In Line Items, Unallocated remainder
- Edit category form (name, allocated amount, expenditure type)
- Line items table: Description, Amount, Responsible Team, Notes, Edit/Delete actions
- Add line item form below table (description, amount, responsible team dropdown from `ViewBag.Teams`, notes)
- Each line item row has inline edit capability via form

- [ ] **Step 4: Create AuditLog.cshtml**

Model: `@model IReadOnlyList<Humans.Domain.Entities.BudgetAuditLog>`

Key elements:
- Title: "Budget Audit Log"
- Year filter dropdown from `ViewBag.Years`
- Table: Timestamp, Actor (display name from ActorUser navigation), Entity Type, Description, Field Name, Old Value → New Value
- Use `ToAuditTimestamp()` extension for date display

- [ ] **Step 5: Create Admin.cshtml**

Model: `@model IReadOnlyList<Humans.Domain.Entities.BudgetYear>`

Key elements:
- Title: "Finance Admin"
- Create New Year form (year identifier, name)
- For each year: card with name, status badge, actions:
  - Edit year (year identifier, name)
  - Status buttons: Activate (if Draft/Closed), Close (if Active), Delete (if Draft/Closed)
  - Group management: list groups with edit/delete, add group form
- Back link to Finance

- [ ] **Step 6: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Views/Finance/
git commit -m "feat(budget): add Finance Razor views"
```

---

### Task 9: Navigation Link

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`

- [ ] **Step 1: Add Finance nav link**

In `src/Humans.Web/Views/Shared/_Layout.cshtml`, add after the Feedback nav item block (after line 111, before the closing `</ul>`):

```html
@if (Humans.Web.Authorization.RoleChecks.CanAccessFinance(User))
{
    <li class="nav-item">
        <a class="nav-link" asp-area="" asp-controller="Finance" asp-action="Index">Finance</a>
    </li>
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_Layout.cshtml
git commit -m "feat(budget): add Finance nav link"
```

---

### Task 10: Enum Stability Tests

**Files:**
- Modify: `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`

- [ ] **Step 1: Add new enums to stability test data**

In `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`, add new entries to the `StringStoredEnumData` property (after the `DrivePermissionLevel` entry, before the closing `};`):

```csharp
{
    typeof(BudgetYearStatus),
    new[] { "Draft", "Active", "Closed" }
},
{
    typeof(ExpenditureType),
    new[] { "CapEx", "OpEx" }
}
```

Also add `using Humans.Domain.Enums;` if not already present (it is).

- [ ] **Step 2: Run tests to verify**

Run: `dotnet test tests/Humans.Domain.Tests`
Expected: All tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs
git commit -m "test(budget): add enum stability tests for BudgetYearStatus and ExpenditureType"
```

---

### Task 11: Full Build, Test, Format Verification

- [ ] **Step 1: Build the full solution**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Humans.slnx`
Expected: All tests pass

- [ ] **Step 3: Verify formatting**

Run: `dotnet format Humans.slnx --verify-no-changes`
Expected: No formatting changes needed. If it fails, run `dotnet format Humans.slnx` to fix, then commit the formatting changes.

- [ ] **Step 4: Fix any issues found and commit**

If any step above failed, fix the issue and commit. Then re-run all three verification steps.
