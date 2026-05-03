# Holded read-side integration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pull Holded purchase invoices into Humans daily, match them to budget categories via `{group-slug}-{category-slug}` tags, surface a planned-vs-actual roll-up for the treasurer, and provide a one-click queue to fix unmatched docs (which also pushes the corrected tag back to Holded).

**Architecture:** Promotes Finance to its own section (status A from day one per design-rules §15h(1)). New `HoldedTransaction` entity stores invoices verbatim alongside a persisted `MatchStatus`. New `Slug` fields on `BudgetGroup` and `BudgetCategory` are the single source of truth for tag mapping — no separate mapping table. Daily Hangfire job does a full-pull (Holded's date filter is unusable because real docs have null `accountingDate`).

**Tech Stack:** C# .NET, EF Core, Hangfire, ASP.NET Core MVC + Razor, NodaTime, xUnit, AwesomeAssertions, Moq.

**Spec:** [`docs/superpowers/specs/2026-04-26-holded-read-integration-design.md`](../specs/2026-04-26-holded-read-integration-design.md)

**Issue:** [nobodies-collective/Humans#463](https://github.com/nobodies-collective/Humans/issues/463)

---

## Pre-flight

This plan assumes:
- The spec PR ([peterdrier/Humans#347](https://github.com/peterdrier/Humans/pull/347)) is merged to `peterdrier/main`. If not, branch the implementation worktree off the spec branch and rebase later.
- A read-only `HOLDED_API_KEY` env var is set on the dev machine (User scope; readable from registry without shell restart).
- Implementer has read the spec.

**Set up the implementation worktree (off main, after spec PR is merged):**

```bash
git fetch origin
git worktree add .worktrees/holded-impl -b holded-impl origin/main
```

All commands below run from `.worktrees/holded-impl/`.

**Conventions:**
- All `dotnet` commands take `-v quiet` per project rules.
- Tests live under `tests/Humans.Application.Tests/`.
- Cross-section reads use `IBudgetService` (never `BudgetRepository` or `DbContext` directly).
- Repositories follow design-rules §15b: Singleton + `IDbContextFactory<HumansDbContext>` + `await using var ctx = await _factory.CreateDbContextAsync(ct);` per method.
- Issue/PR references always qualified: `peterdrier#N` (fork) or `nobodies-collective#N` (upstream).

---

## File Structure

### New files

| Path | Purpose |
|---|---|
| `src/Humans.Domain/Entities/HoldedTransaction.cs` | Holded purchase invoice stored verbatim |
| `src/Humans.Domain/Entities/HoldedSyncState.cs` | Singleton sync-state row |
| `src/Humans.Domain/Enums/HoldedMatchStatus.cs` | 6-value enum |
| `src/Humans.Domain/Enums/HoldedSyncStatus.cs` | Idle / Running / Error |
| `src/Humans.Application/Configuration/HoldedSettings.cs` | Bound settings (env + appsettings) |
| `src/Humans.Application/Interfaces/Finance/IHoldedClient.cs` | Vendor connector interface |
| `src/Humans.Application/Interfaces/Finance/IHoldedSyncService.cs` | Sync orchestration interface |
| `src/Humans.Application/Interfaces/Finance/IHoldedTransactionService.cs` | Read queries for views |
| `src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs` | Repository interface |
| `src/Humans.Application/DTOs/Finance/HoldedDocDto.cs` | Wire DTO from Holded |
| `src/Humans.Application/DTOs/Finance/HoldedTransactionDto.cs` | View DTO |
| `src/Humans.Application/DTOs/Finance/HoldedSyncResult.cs` | Result of one sync run |
| `src/Humans.Application/DTOs/Finance/HoldedTagInventoryRow.cs` | Tag inventory page row |
| `src/Humans.Application/Services/Finance/HoldedSyncService.cs` | Sync + reassignment orchestration |
| `src/Humans.Application/Services/Finance/HoldedTransactionService.cs` | Read queries |
| `src/Humans.Application/Services/Finance/SlugNormalizer.cs` | Pure slugify utility |
| `src/Humans.Infrastructure/Services/HoldedClient.cs` | Typed `HttpClient` over `api.holded.com` |
| `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs` | EF-backed repository |
| `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs` | Hangfire recurring (daily 04:30 UTC) |
| `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedTransactionConfiguration.cs` | EF config |
| `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedSyncStateConfiguration.cs` | EF config |
| `src/Humans.Web/Extensions/Sections/FinanceSectionExtensions.cs` | DI wiring for Finance |
| `src/Humans.Web/Views/Finance/HoldedUnmatched.cshtml` | Unmatched queue UI |
| `src/Humans.Web/Views/Finance/HoldedTags.cshtml` | Read-only tag inventory |
| `src/Humans.Web/Views/Shared/_HoldedSyncCard.cshtml` | Sync state card partial |
| `tests/Humans.Application.Tests/Services/Finance/SlugNormalizerTests.cs` | |
| `tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceMatchTests.cs` | |
| `tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceUpsertTests.cs` | |
| `tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceReassignTests.cs` | |
| `tests/Humans.Application.Tests/Services/Finance/HoldedClientTests.cs` | |
| `tests/Humans.Application.Tests/Services/Finance/HoldedTransactionServiceTests.cs` | |
| `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs` | |

### Modified files

| Path | Why |
|---|---|
| `src/Humans.Domain/Entities/BudgetGroup.cs` | Add `Slug` |
| `src/Humans.Domain/Entities/BudgetCategory.cs` | Add `Slug` |
| `src/Humans.Infrastructure/Data/Configurations/Budget/BudgetGroupConfiguration.cs` | Slug column + unique index |
| `src/Humans.Infrastructure/Data/Configurations/Budget/BudgetCategoryConfiguration.cs` | Slug column + unique index |
| `src/Humans.Infrastructure/Data/HumansDbContext.cs` | `DbSet<HoldedTransaction>`, `DbSet<HoldedSyncState>` |
| `src/Humans.Application/Interfaces/Budget/IBudgetService.cs` | New methods Finance needs |
| `src/Humans.Application/Services/Budget/BudgetService.cs` | Implement new methods |
| `src/Humans.Application/Interfaces/Repositories/IBudgetRepository.cs` | Slug-aware lookups, tag inventory |
| `src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs` | Implement slug-aware lookups |
| `src/Humans.Web/Controllers/FinanceController.cs` | Holded actions + Actual column data |
| `src/Humans.Web/Views/Finance/YearDetail.cshtml` | Actual column + drill-down |
| `src/Humans.Web/Views/Finance/Index.cshtml` | Embed `_HoldedSyncCard` partial |
| `src/Humans.Web/Views/Finance/_BudgetGroupForm.cshtml` (or equivalent) | Slug input |
| `src/Humans.Web/Views/Finance/_BudgetCategoryForm.cshtml` (or equivalent) | Slug input |
| `src/Humans.Web/Program.cs` | Call `AddFinanceSection()` + `IRecurringJob` registration |
| `src/Humans.Web/appsettings.json` | `Holded:Enabled`, `Holded:SyncIntervalCron` |

---

## Task 1: Slug normalizer (pure utility, TDD)

**Files:**
- Create: `src/Humans.Application/Services/Finance/SlugNormalizer.cs`
- Test: `tests/Humans.Application.Tests/Services/Finance/SlugNormalizerTests.cs`

The slugifier strips Spanish accents, lowercases, replaces non-alphanumeric runs with single dashes, and trims. Idempotent.

- [ ] **Step 1: Write failing tests**

Create `tests/Humans.Application.Tests/Services/Finance/SlugNormalizerTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Services.Finance;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class SlugNormalizerTests
{
    [Theory]
    [InlineData("Sound", "sound")]
    [InlineData("Sonido y Música", "sonido-y-musica")]
    [InlineData("  Departments  ", "departments")]
    [InlineData("Site Power & Lighting", "site-power-lighting")]
    [InlineData("Año 2026", "ano-2026")]
    [InlineData("Niño", "nino")]
    [InlineData("Café/Bar", "cafe-bar")]
    [InlineData("multi   spaces", "multi-spaces")]
    [InlineData("--leading-and-trailing--", "leading-and-trailing")]
    [InlineData("ÑOÑO", "nono")]
    public void Normalize_ProducesHoldedSafeSlug(string input, string expected)
    {
        SlugNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = SlugNormalizer.Normalize("Sonido y Música");
        var twice = SlugNormalizer.Normalize(once);
        twice.Should().Be(once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    public void Normalize_OnEmptyOrSymbolOnly_ReturnsEmptyString(string input)
    {
        SlugNormalizer.Normalize(input).Should().Be(string.Empty);
    }

    [Fact]
    public void Normalize_OnNull_ReturnsEmptyString()
    {
        SlugNormalizer.Normalize(null!).Should().Be(string.Empty);
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~SlugNormalizerTests" -v quiet
```

Expected: compile error (`SlugNormalizer` does not exist).

- [ ] **Step 3: Write minimal implementation**

Create `src/Humans.Application/Services/Finance/SlugNormalizer.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Humans.Application.Services.Finance;

/// <summary>
/// Produces Holded-tag-safe slugs: lowercase, ASCII-only, dash-separated,
/// no leading/trailing dashes, no consecutive dashes. Idempotent.
/// </summary>
public static class SlugNormalizer
{
    private static readonly Regex NonAlphaNumeric = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex DashCollapse = new(@"-+", RegexOptions.Compiled);

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // Decompose Unicode to separate base chars from combining diacritics, then strip diacritics.
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        // Replace ñ→n explicitly: NFD doesn't fully strip the tilde for some inputs.
        var stripped = sb.ToString()
            .Replace('ñ', 'n').Replace('Ñ', 'N')
            .ToLowerInvariant();

        var dashed = NonAlphaNumeric.Replace(stripped, "-");
        var collapsed = DashCollapse.Replace(dashed, "-");
        return collapsed.Trim('-');
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~SlugNormalizerTests" -v quiet
```

Expected: all 14+ test cases PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Finance/SlugNormalizer.cs tests/Humans.Application.Tests/Services/Finance/SlugNormalizerTests.cs
git commit -m "feat(finance): add SlugNormalizer for Holded-tag-safe slugs"
```

---

## Task 2: Add Slug field to BudgetGroup and BudgetCategory

**Files:**
- Modify: `src/Humans.Domain/Entities/BudgetGroup.cs`
- Modify: `src/Humans.Domain/Entities/BudgetCategory.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Budget/BudgetGroupConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Budget/BudgetCategoryConfiguration.cs`

- [ ] **Step 1: Add Slug to BudgetGroup**

Open `src/Humans.Domain/Entities/BudgetGroup.cs`. Add `Slug` property next to `Name`:

```csharp
public string Slug { get; set; } = string.Empty;
```

- [ ] **Step 2: Add Slug to BudgetCategory**

Open `src/Humans.Domain/Entities/BudgetCategory.cs`. Add `Slug` property next to `Name`:

```csharp
public string Slug { get; set; } = string.Empty;
```

- [ ] **Step 3: Add EF column + unique index for BudgetGroup**

Edit `src/Humans.Infrastructure/Data/Configurations/Budget/BudgetGroupConfiguration.cs`. After the existing `Name` configuration, add:

```csharp
builder.Property(g => g.Slug).HasMaxLength(64).IsRequired();
builder.HasIndex(g => new { g.BudgetYearId, g.Slug }).IsUnique();
```

- [ ] **Step 4: Add EF column + unique index for BudgetCategory**

Edit `src/Humans.Infrastructure/Data/Configurations/Budget/BudgetCategoryConfiguration.cs`. After the existing `Name` configuration, add:

```csharp
builder.Property(c => c.Slug).HasMaxLength(64).IsRequired();
builder.HasIndex(c => new { c.BudgetGroupId, c.Slug }).IsUnique();
```

- [ ] **Step 5: Build to confirm no compile errors yet**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED. (Migration will be generated in Task 5; this step just confirms entities + configurations compile.)

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Domain/Entities/BudgetGroup.cs src/Humans.Domain/Entities/BudgetCategory.cs src/Humans.Infrastructure/Data/Configurations/Budget/BudgetGroupConfiguration.cs src/Humans.Infrastructure/Data/Configurations/Budget/BudgetCategoryConfiguration.cs
git commit -m "feat(budget): add Slug field on BudgetGroup and BudgetCategory"
```

---

## Task 3: HoldedTransaction + HoldedSyncState entities + enums

**Files:**
- Create: `src/Humans.Domain/Enums/HoldedMatchStatus.cs`
- Create: `src/Humans.Domain/Enums/HoldedSyncStatus.cs`
- Create: `src/Humans.Domain/Entities/HoldedTransaction.cs`
- Create: `src/Humans.Domain/Entities/HoldedSyncState.cs`

- [ ] **Step 1: Create HoldedMatchStatus enum**

```csharp
namespace Humans.Domain.Enums;

public enum HoldedMatchStatus
{
    Matched = 0,
    NoTags = 1,
    UnknownTag = 2,
    MultiMatchConflict = 3,
    NoBudgetYearForDate = 4,
    UnsupportedCurrency = 5,
}
```

- [ ] **Step 2: Create HoldedSyncStatus enum**

```csharp
namespace Humans.Domain.Enums;

public enum HoldedSyncStatus
{
    Idle = 0,
    Running = 1,
    Error = 2,
}
```

- [ ] **Step 3: Create HoldedTransaction entity**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A Holded purchase invoice synced into Humans for budget reconciliation.
/// Stored verbatim alongside a persisted MatchStatus so the unmatched-queue
/// page is a simple WHERE.
/// </summary>
public class HoldedTransaction
{
    public Guid Id { get; init; }

    /// <summary>Holded's id (24-char hex). Natural key for upsert.</summary>
    public string HoldedDocId { get; set; } = string.Empty;

    public string HoldedDocNumber { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;

    public LocalDate Date { get; set; }
    public LocalDate? AccountingDate { get; set; }
    public LocalDate? DueDate { get; set; }

    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }

    public decimal PaymentsTotal { get; set; }
    public decimal PaymentsPending { get; set; }
    public decimal PaymentsRefunds { get; set; }

    public string Currency { get; set; } = "eur";
    public Instant? ApprovedAt { get; set; }

    /// <summary>Raw tags array from Holded, JSON-serialized.</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>Full Holded JSON for debugging + future field needs.</summary>
    public string RawPayload { get; set; } = "{}";

    /// <summary>from.id when from.docType = "incomingdocument"; deep-link to original receipt.</summary>
    public string? SourceIncomingDocId { get; set; }

    /// <summary>Matched BudgetCategory (FK only, no nav). Null when unmatched.</summary>
    public Guid? BudgetCategoryId { get; set; }

    public HoldedMatchStatus MatchStatus { get; set; }

    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Create HoldedSyncState entity (singleton)**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Singleton row (Id = 1) tracking the operational state of HoldedSyncJob.
/// Mirrors TicketSyncState.
/// </summary>
public class HoldedSyncState
{
    public int Id { get; init; } = 1;
    public Instant? LastSyncAt { get; set; }
    public HoldedSyncStatus SyncStatus { get; set; } = HoldedSyncStatus.Idle;
    public string? LastError { get; set; }
    public Instant StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
```

- [ ] **Step 5: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Domain/Enums/HoldedMatchStatus.cs src/Humans.Domain/Enums/HoldedSyncStatus.cs src/Humans.Domain/Entities/HoldedTransaction.cs src/Humans.Domain/Entities/HoldedSyncState.cs
git commit -m "feat(finance): add HoldedTransaction + HoldedSyncState entities"
```

---

## Task 4: EF configurations for Holded entities + DbSets

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedTransactionConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedSyncStateConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Create HoldedTransactionConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedTransactionConfiguration : IEntityTypeConfiguration<HoldedTransaction>
{
    public void Configure(EntityTypeBuilder<HoldedTransaction> builder)
    {
        builder.ToTable("holded_transactions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.HoldedDocId).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.HoldedDocId).IsUnique();

        builder.Property(t => t.HoldedDocNumber).HasMaxLength(64).IsRequired();
        builder.Property(t => t.ContactName).HasMaxLength(512).IsRequired();

        builder.Property(t => t.Date).IsRequired();
        builder.Property(t => t.AccountingDate);
        builder.Property(t => t.DueDate);

        builder.Property(t => t.Subtotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.Tax).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.Total).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.PaymentsTotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.PaymentsPending).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(t => t.PaymentsRefunds).HasColumnType("numeric(18,2)").IsRequired();

        builder.Property(t => t.Currency).HasMaxLength(3).IsRequired();
        builder.Property(t => t.ApprovedAt);

        // Tags: store as JSON string, expose as IReadOnlyList<string>.
        var tagsConverter = new ValueConverter<IReadOnlyList<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        builder.Property(t => t.Tags)
            .HasConversion(tagsConverter)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(t => t.RawPayload).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.SourceIncomingDocId).HasMaxLength(64);

        builder.Property(t => t.BudgetCategoryId);
        builder.HasIndex(t => t.BudgetCategoryId);

        builder.Property(t => t.MatchStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.HasIndex(t => t.MatchStatus);

        builder.Property(t => t.LastSyncedAt).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        // BudgetCategoryId is FK-only, no navigation property.
        // OnDelete restrict to refuse deleting a category that has matched transactions.
        builder.HasOne<BudgetCategory>()
            .WithMany()
            .HasForeignKey(t => t.BudgetCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

- [ ] **Step 2: Create HoldedSyncStateConfiguration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedSyncStateConfiguration : IEntityTypeConfiguration<HoldedSyncState>
{
    public void Configure(EntityTypeBuilder<HoldedSyncState> builder)
    {
        builder.ToTable("holded_sync_states");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).ValueGeneratedNever(); // singleton, fixed Id = 1

        builder.Property(s => s.LastSyncAt);
        builder.Property(s => s.SyncStatus)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(s => s.LastError).HasMaxLength(2048);
        builder.Property(s => s.StatusChangedAt).IsRequired();
        builder.Property(s => s.LastSyncedDocCount).IsRequired();
    }
}
```

- [ ] **Step 3: Add DbSets to HumansDbContext**

Open `src/Humans.Infrastructure/Data/HumansDbContext.cs` and add (next to other DbSets, e.g. budget ones):

```csharp
public DbSet<HoldedTransaction> HoldedTransactions => Set<HoldedTransaction>();
public DbSet<HoldedSyncState> HoldedSyncStates => Set<HoldedSyncState>();
```

- [ ] **Step 4: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/Finance/HoldedTransactionConfiguration.cs src/Humans.Infrastructure/Data/Configurations/Finance/HoldedSyncStateConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(finance): EF configurations + DbSets for Holded entities"
```

---

## Task 5: EF migration with slug backfill, then mandatory review

**Files:**
- Generated: `src/Humans.Infrastructure/Migrations/YYYYMMDDHHMMSS_AddHoldedAndBudgetSlugs.cs` (and `.Designer.cs`, snapshot update)

- [ ] **Step 1: Generate the migration**

```bash
dotnet ef migrations add AddHoldedAndBudgetSlugs --project src/Humans.Infrastructure --startup-project src/Humans.Web -v quiet
```

Expected: three files added under `src/Humans.Infrastructure/Migrations/`.

- [ ] **Step 2: Edit the migration to backfill slugs from names BEFORE the unique-index creation**

Open the generated `*.cs` file. Inside `Up`, AFTER the `AddColumn` calls that add `Slug` to `budget_groups` and `budget_categories` and BEFORE the `CreateIndex` calls that add the unique slug indexes, insert raw-SQL backfill blocks:

```csharp
// Backfill BudgetGroup slugs from Name (lower, accent-strip, dash-collapse, trim).
// Spanish-accent-aware via translate(); ñ handled explicitly.
migrationBuilder.Sql(@"
    UPDATE budget_groups
    SET ""Slug"" = trim(both '-' from regexp_replace(
        regexp_replace(
            translate(lower(""Name""),
                'áéíóúüñàèìòùâêîôûäëïöÿç',
                'aeiouunaeiouaeiouaeioyc'),
            '[^a-z0-9]+', '-', 'g'),
        '-+', '-', 'g'))
    WHERE ""Slug"" IS NULL OR ""Slug"" = '';
");

// Same for BudgetCategory.
migrationBuilder.Sql(@"
    UPDATE budget_categories
    SET ""Slug"" = trim(both '-' from regexp_replace(
        regexp_replace(
            translate(lower(""Name""),
                'áéíóúüñàèìòùâêîôûäëïöÿç',
                'aeiouunaeiouaeiouaeioyc'),
            '[^a-z0-9]+', '-', 'g'),
        '-+', '-', 'g'))
    WHERE ""Slug"" IS NULL OR ""Slug"" = '';
");
```

Note: column names in PostgreSQL are case-sensitive when EF generates them quoted. Verify the actual quoted-name style used by other migrations in this repo (look at any recent migration's `Up` for `budget_groups` references) and match it.

- [ ] **Step 3: Seed the singleton HoldedSyncState row**

In the same `Up`, AFTER the `holded_sync_states` table creation, insert the singleton:

```csharp
migrationBuilder.Sql(@"
    INSERT INTO holded_sync_states
        (""Id"", ""SyncStatus"", ""StatusChangedAt"", ""LastSyncedDocCount"")
    VALUES
        (1, 'Idle', '2026-04-26 00:00:00 +00:00', 0)
    ON CONFLICT (""Id"") DO NOTHING;
");
```

(The exact timestamp format must match how the codebase persists `Instant`. If a `_LastModified`-style migration in this repo seeds rows with timestamps, copy that exact format.)

- [ ] **Step 4: Run the EF migration reviewer agent (mandatory per CLAUDE.md)**

This is a hard rule. Dispatch the agent at `.claude/agents/ef-migration-reviewer.md` with the migration file path. Address every CRITICAL finding before commit. Do not skip.

- [ ] **Step 5: Apply migration locally to verify it runs**

```bash
dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web -v quiet
```

Expected: migration applies cleanly. Verify in psql that `budget_groups.Slug` is non-empty for all existing rows and the singleton row exists in `holded_sync_states`.

- [ ] **Step 6: Run the full test suite to confirm no breakage**

```bash
dotnet test Humans.slnx -v quiet
```

Expected: existing tests still PASS (slug fields are additive).

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(finance): EF migration adding Holded tables and Budget slug backfill"
```

---

## Task 6: IHoldedRepository interface

**Files:**
- Create: `src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs`

- [ ] **Step 1: Define the interface**

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// EF-backed access to Finance section tables (holded_transactions,
/// holded_sync_states). Singleton + IDbContextFactory per design-rules §15b.
/// </summary>
public interface IHoldedRepository
{
    // Sync state
    Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default);
    Task SetSyncStateAsync(HoldedSyncStatus status, Instant changedAt, string? lastError, CancellationToken ct = default);
    Task RecordSyncCompletedAsync(Instant completedAt, int docCount, CancellationToken ct = default);

    // Upsert + reads
    Task UpsertAsync(HoldedTransaction transaction, CancellationToken ct = default);
    Task UpsertManyAsync(IReadOnlyList<HoldedTransaction> transactions, CancellationToken ct = default);
    Task<HoldedTransaction?> GetByHoldedDocIdAsync(string holdedDocId, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedTransaction>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedTransaction>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default);
    Task<int> CountUnmatchedAsync(CancellationToken ct = default);

    // Manual reassignment (writes BudgetCategoryId + MatchStatus)
    Task AssignCategoryAsync(string holdedDocId, Guid budgetCategoryId, Instant updatedAt, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs
git commit -m "feat(finance): IHoldedRepository interface"
```

---

## Task 7: HoldedRepository implementation

**Files:**
- Create: `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs`

- [ ] **Step 1: Implement the repository**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Finance;

/// <summary>
/// EF-backed implementation of <see cref="IHoldedRepository"/>. Per design-rules §15b:
/// Singleton, IDbContextFactory-based, fresh DbContext per method.
/// </summary>
public sealed class HoldedRepository : IHoldedRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly ILogger<HoldedRepository> _logger;

    public HoldedRepository(IDbContextFactory<HumansDbContext> factory, ILogger<HoldedRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return state ?? throw new InvalidOperationException("HoldedSyncState singleton (Id=1) is missing — migration seed failed.");
    }

    public async Task SetSyncStateAsync(HoldedSyncStatus status, Instant changedAt, string? lastError, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.FirstAsync(s => s.Id == 1, ct);
        state.SyncStatus = status;
        state.StatusChangedAt = changedAt;
        state.LastError = lastError;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RecordSyncCompletedAsync(Instant completedAt, int docCount, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.FirstAsync(s => s.Id == 1, ct);
        state.SyncStatus = HoldedSyncStatus.Idle;
        state.LastError = null;
        state.LastSyncAt = completedAt;
        state.LastSyncedDocCount = docCount;
        state.StatusChangedAt = completedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpsertAsync(HoldedTransaction tx, CancellationToken ct = default)
    {
        await UpsertManyAsync(new[] { tx }, ct);
    }

    public async Task UpsertManyAsync(IReadOnlyList<HoldedTransaction> transactions, CancellationToken ct = default)
    {
        if (transactions.Count == 0) return;
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var ids = transactions.Select(t => t.HoldedDocId).ToList();
        var existing = await ctx.HoldedTransactions
            .Where(t => ids.Contains(t.HoldedDocId))
            .ToDictionaryAsync(t => t.HoldedDocId, ct);

        foreach (var incoming in transactions)
        {
            if (existing.TryGetValue(incoming.HoldedDocId, out var current))
            {
                current.HoldedDocNumber = incoming.HoldedDocNumber;
                current.ContactName = incoming.ContactName;
                current.Date = incoming.Date;
                current.AccountingDate = incoming.AccountingDate;
                current.DueDate = incoming.DueDate;
                current.Subtotal = incoming.Subtotal;
                current.Tax = incoming.Tax;
                current.Total = incoming.Total;
                current.PaymentsTotal = incoming.PaymentsTotal;
                current.PaymentsPending = incoming.PaymentsPending;
                current.PaymentsRefunds = incoming.PaymentsRefunds;
                current.Currency = incoming.Currency;
                current.ApprovedAt = incoming.ApprovedAt;
                current.Tags = incoming.Tags;
                current.RawPayload = incoming.RawPayload;
                current.SourceIncomingDocId = incoming.SourceIncomingDocId;
                current.BudgetCategoryId = incoming.BudgetCategoryId;
                current.MatchStatus = incoming.MatchStatus;
                current.LastSyncedAt = incoming.LastSyncedAt;
                current.UpdatedAt = incoming.LastSyncedAt;
                // CreatedAt preserved.
            }
            else
            {
                ctx.HoldedTransactions.Add(incoming);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<HoldedTransaction?> GetByHoldedDocIdAsync(string holdedDocId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.HoldedDocId == holdedDocId, ct);
    }

    public async Task<IReadOnlyList<HoldedTransaction>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions.AsNoTracking()
            .Where(t => t.MatchStatus != HoldedMatchStatus.Matched)
            .OrderByDescending(t => t.AccountingDate ?? t.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedTransaction>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions.AsNoTracking()
            .Where(t => t.BudgetCategoryId == budgetCategoryId)
            .OrderByDescending(t => t.AccountingDate ?? t.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Sum Total per BudgetCategory across approved transactions whose date falls in the year.
        // Year-membership is enforced by BudgetService.GetYearForDateAsync at sync-time;
        // here we filter through the BudgetCategory → BudgetGroup → BudgetYearId chain.
        var sums = await ctx.HoldedTransactions.AsNoTracking()
            .Where(t => t.ApprovedAt != null && t.BudgetCategoryId != null)
            .Join(
                ctx.BudgetCategories.AsNoTracking(),
                t => t.BudgetCategoryId,
                c => c.Id,
                (t, c) => new { c.Id, c.BudgetGroupId, t.Total })
            .Join(
                ctx.BudgetGroups.AsNoTracking().Where(g => g.BudgetYearId == budgetYearId),
                tc => tc.BudgetGroupId,
                g => g.Id,
                (tc, _) => new { tc.Id, tc.Total })
            .GroupBy(x => x.Id)
            .Select(g => new { CategoryId = g.Key, Sum = g.Sum(x => x.Total) })
            .ToListAsync(ct);

        return sums.ToDictionary(s => s.CategoryId, s => s.Sum);
    }

    public async Task<int> CountUnmatchedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.HoldedTransactions
            .CountAsync(t => t.MatchStatus != HoldedMatchStatus.Matched, ct);
    }

    public async Task AssignCategoryAsync(string holdedDocId, Guid budgetCategoryId, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var tx = await ctx.HoldedTransactions.FirstOrDefaultAsync(t => t.HoldedDocId == holdedDocId, ct)
            ?? throw new InvalidOperationException($"HoldedTransaction not found: {holdedDocId}");
        tx.BudgetCategoryId = budgetCategoryId;
        tx.MatchStatus = HoldedMatchStatus.Matched;
        tx.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs
git commit -m "feat(finance): HoldedRepository implementation"
```

---

## Task 8: HoldedSettings + IHoldedClient interface + wire DTO

**Files:**
- Create: `src/Humans.Application/Configuration/HoldedSettings.cs`
- Create: `src/Humans.Application/Interfaces/Finance/IHoldedClient.cs`
- Create: `src/Humans.Application/DTOs/Finance/HoldedDocDto.cs`

- [ ] **Step 1: Settings**

```csharp
namespace Humans.Application.Configuration;

/// <summary>
/// Holded API settings. ApiKey comes from env var HOLDED_API_KEY only; never appsettings.
/// Other knobs come from the Holded section in appsettings.json.
/// </summary>
public class HoldedSettings
{
    public const string SectionName = "Holded";

    /// <summary>From env var HOLDED_API_KEY at startup, never logged.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Default daily 04:30 UTC.</summary>
    public string SyncIntervalCron { get; set; } = "0 30 4 * * *";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
```

- [ ] **Step 2: Wire DTO (matches what Holded actually returns)**

```csharp
using System.Text.Json.Serialization;

namespace Humans.Application.DTOs.Finance;

/// <summary>
/// Wire DTO mirroring the Holded purchase-doc JSON. See spec section
/// "Holded API findings" for field semantics.
/// </summary>
public sealed class HoldedDocDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("docNumber")] public string DocNumber { get; set; } = string.Empty;
    [JsonPropertyName("contact")] public string? Contact { get; set; }
    [JsonPropertyName("contactName")] public string? ContactName { get; set; }
    [JsonPropertyName("date")] public long Date { get; set; }
    [JsonPropertyName("dueDate")] public long? DueDate { get; set; }
    [JsonPropertyName("accountingDate")] public long? AccountingDate { get; set; }
    [JsonPropertyName("approvedAt")] public long? ApprovedAt { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("subtotal")] public decimal Subtotal { get; set; }
    [JsonPropertyName("tax")] public decimal Tax { get; set; }
    [JsonPropertyName("total")] public decimal Total { get; set; }
    [JsonPropertyName("paymentsTotal")] public decimal PaymentsTotal { get; set; }
    [JsonPropertyName("paymentsPending")] public decimal PaymentsPending { get; set; }
    [JsonPropertyName("paymentsRefunds")] public decimal PaymentsRefunds { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("from")] public HoldedFromDto? From { get; set; }
}

public sealed class HoldedFromDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("docType")] public string? DocType { get; set; }
}
```

- [ ] **Step 3: Client interface**

```csharp
using Humans.Application.DTOs.Finance;

namespace Humans.Application.Interfaces.Finance;

/// <summary>
/// Vendor connector for the Holded API. Lives in the Application layer as
/// an interface; implementation in Humans.Infrastructure.
/// </summary>
public interface IHoldedClient
{
    /// <summary>
    /// Fetch all purchase documents (full pull, paginated under the hood). Returns
    /// the raw wire DTOs alongside the original JSON string for each doc so the
    /// sync service can persist RawPayload verbatim.
    /// </summary>
    Task<IReadOnlyList<(HoldedDocDto Dto, string RawJson)>> GetAllPurchaseDocsAsync(CancellationToken ct = default);

    /// <summary>
    /// Push a tag onto a Holded purchase doc. Adds <paramref name="tag"/> to the
    /// existing tags array and PUTs the doc back. Returns true on success;
    /// returns false if the API rejects the request (e.g. tag-update unsupported).
    /// </summary>
    Task<bool> TryAddTagAsync(string holdedDocId, string tag, CancellationToken ct = default);
}
```

- [ ] **Step 4: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Configuration/HoldedSettings.cs src/Humans.Application/Interfaces/Finance/IHoldedClient.cs src/Humans.Application/DTOs/Finance/HoldedDocDto.cs
git commit -m "feat(finance): HoldedSettings, IHoldedClient interface, wire DTO"
```

---

## Task 9: HoldedClient implementation (typed HttpClient, paginated GET, TryAddTag)

**Files:**
- Create: `src/Humans.Infrastructure/Services/HoldedClient.cs`
- Create: `tests/Humans.Application.Tests/Services/Finance/HoldedClientTests.cs`

Mirror the typed-`HttpClient` pattern in `src/Humans.Infrastructure/Services/TicketTailorService.cs:38-56`.

- [ ] **Step 1: Write failing client test (pagination terminates on empty page)**

```csharp
using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class HoldedClientTests
{
    private static HoldedClient MakeClient(Mock<HttpMessageHandler> handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.holded.com/") };
        var settings = Options.Create(new HoldedSettings { ApiKey = apiKey, Enabled = true });
        return new HoldedClient(http, settings, NullLogger<HoldedClient>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetAllPurchaseDocs_PaginatesUntilEmpty()
    {
        var handler = new Mock<HttpMessageHandler>();
        var calls = 0;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                calls++;
                return calls switch
                {
                    1 => Json("""[{"id":"a","docNumber":"F1","date":1700000000,"currency":"eur","subtotal":100,"tax":0,"total":100,"paymentsTotal":0,"paymentsPending":100,"paymentsRefunds":0,"tags":[]}]"""),
                    2 => Json("""[{"id":"b","docNumber":"F2","date":1700000001,"currency":"eur","subtotal":50,"tax":0,"total":50,"paymentsTotal":0,"paymentsPending":50,"paymentsRefunds":0,"tags":[]}]"""),
                    _ => Json("[]"),
                };
            });

        var client = MakeClient(handler);
        var docs = await client.GetAllPurchaseDocsAsync();

        docs.Should().HaveCount(2);
        docs[0].Dto.Id.Should().Be("a");
        docs[1].Dto.Id.Should().Be("b");
        calls.Should().Be(3); // 2 with data + 1 empty
    }

    [Fact]
    public async Task GetAllPurchaseDocs_SendsKeyHeader()
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? captured = null;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Json("[]"));

        var client = MakeClient(handler, apiKey: "secret-token");
        await client.GetAllPurchaseDocsAsync();

        captured.Should().NotBeNull();
        captured!.Headers.GetValues("key").Should().ContainSingle().Which.Should().Be("secret-token");
    }

    [Fact]
    public async Task TryAddTag_OnHttpError_ReturnsFalseAndDoesNotThrow()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            // GET to read existing tags
            .ReturnsAsync(Json("""{"id":"a","tags":["existing"],"docNumber":"F1","date":1,"currency":"eur","subtotal":0,"tax":0,"total":0,"paymentsTotal":0,"paymentsPending":0,"paymentsRefunds":0}"""))
            // PUT fails
            .ReturnsAsync(Json("""{"error":"not allowed"}""", HttpStatusCode.BadRequest));

        var client = MakeClient(handler);
        var result = await client.TryAddTagAsync("a", "departments-sound");

        result.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedClientTests" -v quiet
```

Expected: compile error (HoldedClient doesn't exist).

- [ ] **Step 3: Implement HoldedClient**

```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Humans.Application.Configuration;
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces.Finance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Typed HttpClient wrapper for Holded's purchase-document endpoints.
/// API key from env var HOLDED_API_KEY (bound into HoldedSettings at startup);
/// never logged.
/// </summary>
public sealed class HoldedClient : IHoldedClient
{
    private const int PageSize = 100;
    private readonly HttpClient _http;
    private readonly ILogger<HoldedClient> _logger;
    private readonly HoldedSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HoldedClient(HttpClient http, IOptions<HoldedSettings> settings, ILogger<HoldedClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.holded.com/");

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            // Holded auth: 'key' header.
            _http.DefaultRequestHeaders.Remove("key");
            _http.DefaultRequestHeaders.Add("key", _settings.ApiKey);
        }
    }

    public async Task<IReadOnlyList<(HoldedDocDto Dto, string RawJson)>> GetAllPurchaseDocsAsync(CancellationToken ct = default)
    {
        var results = new List<(HoldedDocDto, string)>();
        var page = 1;

        while (true)
        {
            var url = $"api/invoicing/v1/documents/purchase?page={page}&limit={PageSize}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                break;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var dto = element.Deserialize<HoldedDocDto>(JsonOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize Holded purchase doc");
                var rawElementJson = element.GetRawText();
                results.Add((dto, rawElementJson));
            }

            page++;
        }

        _logger.LogInformation("Fetched {Count} Holded purchase docs across {Pages} page(s)", results.Count, page);
        return results;
    }

    public async Task<bool> TryAddTagAsync(string holdedDocId, string tag, CancellationToken ct = default)
    {
        try
        {
            // GET current doc to read existing tags
            using var getResp = await _http.GetAsync($"api/invoicing/v1/documents/purchase/{holdedDocId}", ct);
            if (!getResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Holded GET {DocId} returned {Status}; cannot add tag", holdedDocId, getResp.StatusCode);
                return false;
            }
            var current = await getResp.Content.ReadFromJsonAsync<HoldedDocDto>(JsonOptions, ct);
            if (current is null) return false;

            var newTags = current.Tags.Append(tag).Distinct(StringComparer.Ordinal).ToList();
            var payload = new { tags = newTags };

            using var putResp = await _http.PutAsJsonAsync($"api/invoicing/v1/documents/purchase/{holdedDocId}", payload, ct);
            if (!putResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Holded PUT {DocId} tag update returned {Status}", holdedDocId, putResp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Always log problems even when expected (per project rule).
            _logger.LogWarning(ex, "Holded tag push for {DocId} failed", holdedDocId);
            return false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedClientTests" -v quiet
```

Expected: all 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/HoldedClient.cs tests/Humans.Application.Tests/Services/Finance/HoldedClientTests.cs
git commit -m "feat(finance): HoldedClient with paginated GET and TryAddTag fallback"
```

---

## Task 10: Extend IBudgetService with Finance-needed methods

**Files:**
- Modify: `src/Humans.Application/Interfaces/Budget/IBudgetService.cs`
- Modify: `src/Humans.Application/Services/Budget/BudgetService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/IBudgetRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs`
- Create: `src/Humans.Application/DTOs/Finance/HoldedTagInventoryRow.cs`

Finance reads through `IBudgetService` only (per design-rules §9). These four methods are the entire surface.

- [ ] **Step 1: Add the inventory DTO**

```csharp
namespace Humans.Application.DTOs.Finance;

public sealed record HoldedTagInventoryRow(
    Guid BudgetYearId,
    string Year,
    Guid BudgetGroupId,
    string GroupName,
    string GroupSlug,
    Guid BudgetCategoryId,
    string CategoryName,
    string CategorySlug,
    string Tag);
```

- [ ] **Step 2: Add interface methods to IBudgetService**

In `src/Humans.Application/Interfaces/Budget/IBudgetService.cs` (open and locate the existing interface), add:

```csharp
// Finance section consumers — read-only Slug-aware lookups
Task<BudgetCategory?> GetCategoryBySlugAsync(Guid budgetYearId, string groupSlug, string categorySlug, CancellationToken ct = default);
Task<BudgetYear?> GetYearForDateAsync(LocalDate date, CancellationToken ct = default);
Task<IReadOnlyList<BudgetCategory>> GetCategoriesByYearAsync(Guid budgetYearId, CancellationToken ct = default);
Task<IReadOnlyList<HoldedTagInventoryRow>> GetTagInventoryAsync(Guid budgetYearId, CancellationToken ct = default);
```

Add `using NodaTime;` and `using Humans.Application.DTOs.Finance;` if not already present.

- [ ] **Step 3: Add corresponding repository methods to IBudgetRepository**

```csharp
Task<BudgetCategory?> GetCategoryBySlugAsync(Guid budgetYearId, string groupSlug, string categorySlug, CancellationToken ct = default);
Task<BudgetYear?> GetYearForDateAsync(LocalDate date, CancellationToken ct = default);
Task<IReadOnlyList<BudgetCategory>> GetCategoriesByYearAsync(Guid budgetYearId, CancellationToken ct = default);
Task<IReadOnlyList<(BudgetYear Year, BudgetGroup Group, BudgetCategory Category)>> GetTagInventoryRowsAsync(Guid budgetYearId, CancellationToken ct = default);
```

- [ ] **Step 4: Implement in BudgetRepository**

Append to `src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs`:

```csharp
public async Task<BudgetCategory?> GetCategoryBySlugAsync(
    Guid budgetYearId, string groupSlug, string categorySlug, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.BudgetCategories.AsNoTracking()
        .Where(c => c.Slug == categorySlug
                    && c.BudgetGroup!.Slug == groupSlug
                    && c.BudgetGroup.BudgetYearId == budgetYearId)
        .FirstOrDefaultAsync(ct);
}

public async Task<BudgetYear?> GetYearForDateAsync(LocalDate date, CancellationToken ct = default)
{
    // BudgetYear.Year is a string per the existing model (e.g., "2026"). Match by
    // year-of-date when Year parses as int; otherwise no match. (More elaborate
    // year selection — e.g., "2027-A" sub-years — is not needed for Holded matching v1.)
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var yearStr = date.Year.ToString();
    return await ctx.BudgetYears.AsNoTracking()
        .Where(y => !y.IsDeleted && y.Year == yearStr)
        .FirstOrDefaultAsync(ct);
}

public async Task<IReadOnlyList<BudgetCategory>> GetCategoriesByYearAsync(
    Guid budgetYearId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.BudgetCategories.AsNoTracking()
        .Where(c => c.BudgetGroup!.BudgetYearId == budgetYearId)
        .Include(c => c.BudgetGroup)
        .OrderBy(c => c.BudgetGroup!.SortOrder).ThenBy(c => c.SortOrder)
        .ToListAsync(ct);
}

public async Task<IReadOnlyList<(BudgetYear Year, BudgetGroup Group, BudgetCategory Category)>> GetTagInventoryRowsAsync(
    Guid budgetYearId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var rows = await ctx.BudgetCategories.AsNoTracking()
        .Where(c => c.BudgetGroup!.BudgetYearId == budgetYearId)
        .Select(c => new
        {
            Year = c.BudgetGroup!.BudgetYear,
            Group = c.BudgetGroup,
            Category = c
        })
        .ToListAsync(ct);
    return rows.Select(r => (r.Year!, r.Group, r.Category)).ToList();
}
```

(Adjust the `BudgetYear` navigation if it's named differently in `BudgetGroup` — verify against the entity.)

- [ ] **Step 5: Implement in BudgetService**

Locate `BudgetService` (probably `src/Humans.Application/Services/Budget/BudgetService.cs`) and append:

```csharp
public Task<BudgetCategory?> GetCategoryBySlugAsync(Guid budgetYearId, string groupSlug, string categorySlug, CancellationToken ct = default)
    => _repository.GetCategoryBySlugAsync(budgetYearId, groupSlug, categorySlug, ct);

public Task<BudgetYear?> GetYearForDateAsync(LocalDate date, CancellationToken ct = default)
    => _repository.GetYearForDateAsync(date, ct);

public Task<IReadOnlyList<BudgetCategory>> GetCategoriesByYearAsync(Guid budgetYearId, CancellationToken ct = default)
    => _repository.GetCategoriesByYearAsync(budgetYearId, ct);

public async Task<IReadOnlyList<HoldedTagInventoryRow>> GetTagInventoryAsync(Guid budgetYearId, CancellationToken ct = default)
{
    var rows = await _repository.GetTagInventoryRowsAsync(budgetYearId, ct);
    return rows.Select(r => new HoldedTagInventoryRow(
        BudgetYearId: r.Year.Id,
        Year: r.Year.Year,
        BudgetGroupId: r.Group.Id,
        GroupName: r.Group.Name,
        GroupSlug: r.Group.Slug,
        BudgetCategoryId: r.Category.Id,
        CategoryName: r.Category.Name,
        CategorySlug: r.Category.Slug,
        Tag: $"{r.Group.Slug}-{r.Category.Slug}"
    )).ToList();
}
```

- [ ] **Step 6: Build + run existing budget tests to confirm no regression**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx --filter "FullyQualifiedName~Budget" -v quiet
```

Expected: BUILD SUCCEEDED. Tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/DTOs/Finance/HoldedTagInventoryRow.cs src/Humans.Application/Interfaces/IBudgetService.cs src/Humans.Application/Services/Budget/BudgetService.cs src/Humans.Application/Interfaces/Repositories/IBudgetRepository.cs src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs
git commit -m "feat(budget): expose slug-aware lookups + tag inventory for Finance"
```

---

## Task 11: HoldedSyncService — match-resolution rules (TDD)

**Files:**
- Create: `src/Humans.Application/Interfaces/Finance/IHoldedSyncService.cs`
- Create: `src/Humans.Application/DTOs/Finance/HoldedSyncResult.cs`
- Create: `src/Humans.Application/Services/Finance/HoldedSyncService.cs`
- Create: `tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceMatchTests.cs`

This task focuses on the pure match-resolution method `ResolveMatch` extracted as `internal` for direct testing. Sync orchestration comes in Task 12.

- [ ] **Step 1: Sync result DTO + service interface**

```csharp
namespace Humans.Application.DTOs.Finance;

public sealed record HoldedSyncResult(int DocsFetched, int Matched, int Unmatched, IReadOnlyDictionary<string, int> ByStatus);
```

```csharp
using Humans.Application.DTOs.Finance;

namespace Humans.Application.Interfaces.Finance;

public interface IHoldedSyncService
{
    /// <summary>Pulls all purchase docs, matches, upserts. Updates HoldedSyncState.</summary>
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>Manually assigns a doc to a category and pushes the corrected tag back to Holded (best-effort).</summary>
    Task<ReassignOutcome> ReassignAsync(string holdedDocId, Guid budgetCategoryId, Guid actorUserId, CancellationToken ct = default);
}

public sealed record ReassignOutcome(bool LocalMatchSaved, bool TagPushedToHolded, string? Warning);
```

- [ ] **Step 2: Write failing match-resolution tests**

```csharp
using AwesomeAssertions;
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class HoldedSyncServiceMatchTests
{
    private readonly Mock<IBudgetService> _budget = new();
    private readonly Mock<IHoldedRepository> _repo = new();
    private readonly Mock<IHoldedClient> _client = new();
    private readonly Mock<Humans.Application.Interfaces.IAuditLogService> _audit = new();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 26, 12, 0));

    private HoldedSyncService Make() => new(_client.Object, _repo.Object, _budget.Object, _audit.Object, _clock, NullLogger<HoldedSyncService>.Instance);

    private static HoldedDocDto Doc(string id, string currency = "eur", long? date = 1774994400, params string[] tags) => new()
    {
        Id = id,
        DocNumber = "F" + id,
        ContactName = "Vendor",
        Currency = currency,
        Date = date ?? 1774994400,
        Tags = tags.ToList(),
        Subtotal = 100, Tax = 0, Total = 100,
    };

    [Fact]
    public async Task ResolveMatch_NonEurCurrency_ReturnsUnsupportedCurrency()
    {
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a", currency: "usd"));
        status.Status.Should().Be(HoldedMatchStatus.UnsupportedCurrency);
        status.BudgetCategoryId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveMatch_NoYearForDate_ReturnsNoBudgetYearForDate()
    {
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BudgetYear?)null);
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a"));
        status.Status.Should().Be(HoldedMatchStatus.NoBudgetYearForDate);
    }

    [Fact]
    public async Task ResolveMatch_EmptyTags_ReturnsNoTags()
    {
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BudgetYear { Id = Guid.NewGuid(), Year = "2026" });
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a"));
        status.Status.Should().Be(HoldedMatchStatus.NoTags);
    }

    [Fact]
    public async Task ResolveMatch_TagWithoutDash_ReturnsUnknownTag()
    {
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BudgetYear { Id = Guid.NewGuid(), Year = "2026" });
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a", 1774994400, "sound"));
        status.Status.Should().Be(HoldedMatchStatus.UnknownTag);
    }

    [Fact]
    public async Task ResolveMatch_UnknownGroupOrCategory_ReturnsUnknownTag()
    {
        var year = new BudgetYear { Id = Guid.NewGuid(), Year = "2026" };
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>())).ReturnsAsync(year);
        _budget.Setup(b => b.GetCategoryBySlugAsync(year.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BudgetCategory?)null);
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a", 1774994400, "departments-sound"));
        status.Status.Should().Be(HoldedMatchStatus.UnknownTag);
    }

    [Fact]
    public async Task ResolveMatch_OneTagResolves_ReturnsMatched()
    {
        var year = new BudgetYear { Id = Guid.NewGuid(), Year = "2026" };
        var cat = new BudgetCategory { Id = Guid.NewGuid() };
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>())).ReturnsAsync(year);
        _budget.Setup(b => b.GetCategoryBySlugAsync(year.Id, "departments", "sound", It.IsAny<CancellationToken>())).ReturnsAsync(cat);
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a", 1774994400, "departments-sound"));
        status.Status.Should().Be(HoldedMatchStatus.Matched);
        status.BudgetCategoryId.Should().Be(cat.Id);
    }

    [Fact]
    public async Task ResolveMatch_MultipleTagsSameCategory_ReturnsMatched()
    {
        var year = new BudgetYear { Id = Guid.NewGuid(), Year = "2026" };
        var cat = new BudgetCategory { Id = Guid.NewGuid() };
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>())).ReturnsAsync(year);
        _budget.Setup(b => b.GetCategoryBySlugAsync(year.Id, "departments", "sound", It.IsAny<CancellationToken>())).ReturnsAsync(cat);
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a", 1774994400, "departments-sound", "departments-sound"));
        status.Status.Should().Be(HoldedMatchStatus.Matched);
    }

    [Fact]
    public async Task ResolveMatch_MultipleTagsDifferentCategories_ReturnsConflict()
    {
        var year = new BudgetYear { Id = Guid.NewGuid(), Year = "2026" };
        var catA = new BudgetCategory { Id = Guid.NewGuid() };
        var catB = new BudgetCategory { Id = Guid.NewGuid() };
        _budget.Setup(b => b.GetYearForDateAsync(It.IsAny<LocalDate>(), It.IsAny<CancellationToken>())).ReturnsAsync(year);
        _budget.Setup(b => b.GetCategoryBySlugAsync(year.Id, "departments", "sound", It.IsAny<CancellationToken>())).ReturnsAsync(catA);
        _budget.Setup(b => b.GetCategoryBySlugAsync(year.Id, "site", "power", It.IsAny<CancellationToken>())).ReturnsAsync(catB);
        var sut = Make();
        var status = await sut.ResolveMatchAsync(Doc("a", 1774994400, "departments-sound", "site-power"));
        status.Status.Should().Be(HoldedMatchStatus.MultiMatchConflict);
        status.BudgetCategoryId.Should().BeNull();
    }
}

internal sealed class FakeClock : IClock
{
    private Instant _now;
    public FakeClock(Instant now) => _now = now;
    public Instant GetCurrentInstant() => _now;
    public void Advance(Duration d) => _now += d;
}
```

(`IAuditLogService` namespace may differ — adapt the using to whatever the project uses, e.g. `Humans.Application.Interfaces.AuditLog`.)

- [ ] **Step 3: Run tests to verify failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedSyncServiceMatchTests" -v quiet
```

Expected: compile error (HoldedSyncService doesn't exist).

- [ ] **Step 4: Implement HoldedSyncService.ResolveMatchAsync**

```csharp
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Finance;

public sealed class HoldedSyncService : IHoldedSyncService
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private readonly IHoldedClient _client;
    private readonly IHoldedRepository _repository;
    private readonly IBudgetService _budget;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly ILogger<HoldedSyncService> _logger;

    public HoldedSyncService(
        IHoldedClient client,
        IHoldedRepository repository,
        IBudgetService budget,
        IAuditLogService audit,
        IClock clock,
        ILogger<HoldedSyncService> logger)
    {
        _client = client;
        _repository = repository;
        _budget = budget;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    internal sealed record MatchOutcome(HoldedMatchStatus Status, Guid? BudgetCategoryId);

    internal async Task<MatchOutcome> ResolveMatchAsync(HoldedDocDto doc, CancellationToken ct = default)
    {
        // Rule 1: currency
        if (!string.Equals(doc.Currency, "eur", StringComparison.OrdinalIgnoreCase))
            return new MatchOutcome(HoldedMatchStatus.UnsupportedCurrency, null);

        // Rule 2: budget year
        var date = ResolveDate(doc);
        var year = await _budget.GetYearForDateAsync(date, ct);
        if (year is null)
            return new MatchOutcome(HoldedMatchStatus.NoBudgetYearForDate, null);

        // Rule 3: any tags?
        if (doc.Tags is null || doc.Tags.Count == 0)
            return new MatchOutcome(HoldedMatchStatus.NoTags, null);

        // Rule 4: resolve each tag
        var resolved = new HashSet<Guid>();
        foreach (var tag in doc.Tags)
        {
            var dash = tag.IndexOf('-');
            if (dash <= 0 || dash >= tag.Length - 1) continue; // malformed
            var groupSlug = tag[..dash];
            var categorySlug = tag[(dash + 1)..];
            var category = await _budget.GetCategoryBySlugAsync(year.Id, groupSlug, categorySlug, ct);
            if (category is not null) resolved.Add(category.Id);
        }

        return resolved.Count switch
        {
            0 => new MatchOutcome(HoldedMatchStatus.UnknownTag, null),
            1 => new MatchOutcome(HoldedMatchStatus.Matched, resolved.Single()),
            _ => new MatchOutcome(HoldedMatchStatus.MultiMatchConflict, null),
        };
    }

    private static LocalDate ResolveDate(HoldedDocDto doc)
    {
        var epoch = doc.AccountingDate ?? doc.Date;
        return Instant.FromUnixTimeSeconds(epoch).InZone(Madrid).Date;
    }

    public Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 12");

    public Task<ReassignOutcome> ReassignAsync(string holdedDocId, Guid budgetCategoryId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
}
```

(If `IAuditLogService` uses a different namespace or method shape, adapt the using line. Verify against an existing call site.)

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedSyncServiceMatchTests" -v quiet
```

Expected: all 8 match-resolution tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/DTOs/Finance/HoldedSyncResult.cs src/Humans.Application/Interfaces/Finance/IHoldedSyncService.cs src/Humans.Application/Services/Finance/HoldedSyncService.cs tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceMatchTests.cs
git commit -m "feat(finance): HoldedSyncService match-resolution rules"
```

---

## Task 12: HoldedSyncService.SyncAsync — full pull, match, upsert, state

**Files:**
- Modify: `src/Humans.Application/Services/Finance/HoldedSyncService.cs`
- Create: `tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceUpsertTests.cs`

- [ ] **Step 1: Write failing upsert tests**

```csharp
using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class HoldedSyncServiceUpsertTests
{
    private readonly Mock<IHoldedClient> _client = new();
    private readonly Mock<IHoldedRepository> _repo = new();
    private readonly Mock<IBudgetService> _budget = new();
    private readonly Mock<IAuditLogService> _audit = new();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 26, 12, 0));
    private HoldedSyncService Make() => new(_client.Object, _repo.Object, _budget.Object, _audit.Object, _clock, NullLogger<HoldedSyncService>.Instance);

    [Fact]
    public async Task SyncAsync_OnSuccess_MarksRunningThenIdleAndUpsertsAll()
    {
        _client.Setup(c => c.GetAllPurchaseDocsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                (new HoldedDocDto { Id = "a", DocNumber = "F1", Currency = "eur", Date = 1774994400, Total = 100, Tags = new() }, """{"id":"a"}"""),
                (new HoldedDocDto { Id = "b", DocNumber = "F2", Currency = "eur", Date = 1774994400, Total = 200, Tags = new() }, """{"id":"b"}"""),
            });
        _repo.Setup(r => r.GetSyncStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HoldedSyncState { Id = 1, SyncStatus = HoldedSyncStatus.Idle });

        var sut = Make();
        var result = await sut.SyncAsync();

        result.DocsFetched.Should().Be(2);
        _repo.Verify(r => r.SetSyncStateAsync(HoldedSyncStatus.Running, It.IsAny<Instant>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.UpsertManyAsync(It.Is<IReadOnlyList<HoldedTransaction>>(list => list.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.RecordSyncCompletedAsync(It.IsAny<Instant>(), 2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenAlreadyRunning_SkipsAndDoesNotCallClient()
    {
        _repo.Setup(r => r.GetSyncStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HoldedSyncState { Id = 1, SyncStatus = HoldedSyncStatus.Running });

        var sut = Make();
        var result = await sut.SyncAsync();

        result.DocsFetched.Should().Be(0);
        _client.Verify(c => c.GetAllPurchaseDocsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_OnException_MarksErrorWithLastError()
    {
        _repo.Setup(r => r.GetSyncStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HoldedSyncState { Id = 1, SyncStatus = HoldedSyncStatus.Idle });
        _client.Setup(c => c.GetAllPurchaseDocsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var sut = Make();
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.SyncAsync());

        _repo.Verify(r => r.SetSyncStateAsync(HoldedSyncStatus.Error, It.IsAny<Instant>(), It.Is<string>(s => s.Contains("boom")), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedSyncServiceUpsertTests" -v quiet
```

Expected: tests FAIL (`SyncAsync` throws `NotImplementedException`).

- [ ] **Step 3: Implement SyncAsync**

In `HoldedSyncService.cs`, replace the `SyncAsync` body:

```csharp
public async Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default)
{
    var state = await _repository.GetSyncStateAsync(ct);
    if (state.SyncStatus == HoldedSyncStatus.Running)
    {
        _logger.LogInformation("HoldedSyncJob already Running; skipping this cycle");
        return new HoldedSyncResult(0, 0, 0, new Dictionary<string, int>());
    }

    var startedAt = _clock.GetCurrentInstant();
    await _repository.SetSyncStateAsync(HoldedSyncStatus.Running, startedAt, lastError: null, ct);

    try
    {
        var docs = await _client.GetAllPurchaseDocsAsync(ct);
        var transactions = new List<HoldedTransaction>(docs.Count);
        var byStatus = new Dictionary<string, int>();

        foreach (var (dto, raw) in docs)
        {
            var match = await ResolveMatchAsync(dto, ct);
            var date = ResolveDate(dto);
            var tx = new HoldedTransaction
            {
                Id = Guid.NewGuid(),
                HoldedDocId = dto.Id,
                HoldedDocNumber = dto.DocNumber,
                ContactName = dto.ContactName ?? string.Empty,
                Date = date,
                AccountingDate = dto.AccountingDate is { } ad ? Instant.FromUnixTimeSeconds(ad).InZone(Madrid).Date : null,
                DueDate = dto.DueDate is { } dd ? Instant.FromUnixTimeSeconds(dd).InZone(Madrid).Date : null,
                Subtotal = dto.Subtotal,
                Tax = dto.Tax,
                Total = dto.Total,
                PaymentsTotal = dto.PaymentsTotal,
                PaymentsPending = dto.PaymentsPending,
                PaymentsRefunds = dto.PaymentsRefunds,
                Currency = (dto.Currency ?? "eur").ToLowerInvariant(),
                ApprovedAt = dto.ApprovedAt is { } ap ? Instant.FromUnixTimeSeconds(ap) : null,
                Tags = dto.Tags,
                RawPayload = raw,
                SourceIncomingDocId = dto.From?.DocType == "incomingdocument" ? dto.From.Id : null,
                BudgetCategoryId = match.BudgetCategoryId,
                MatchStatus = match.Status,
                LastSyncedAt = startedAt,
                CreatedAt = startedAt,
                UpdatedAt = startedAt,
            };
            transactions.Add(tx);

            var key = match.Status.ToString();
            byStatus[key] = byStatus.GetValueOrDefault(key) + 1;
        }

        await _repository.UpsertManyAsync(transactions, ct);
        await _repository.RecordSyncCompletedAsync(_clock.GetCurrentInstant(), transactions.Count, ct);

        var matched = byStatus.GetValueOrDefault(HoldedMatchStatus.Matched.ToString());
        return new HoldedSyncResult(transactions.Count, matched, transactions.Count - matched, byStatus);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Holded sync failed");
        await _repository.SetSyncStateAsync(HoldedSyncStatus.Error, _clock.GetCurrentInstant(), ex.Message, ct);
        throw;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedSyncServiceUpsertTests" -v quiet
```

Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Finance/HoldedSyncService.cs tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceUpsertTests.cs
git commit -m "feat(finance): HoldedSyncService.SyncAsync end-to-end with state machine"
```

---

## Task 13: HoldedSyncService.ReassignAsync (manual + tag push fallback)

**Files:**
- Modify: `src/Humans.Application/Services/Finance/HoldedSyncService.cs`
- Create: `tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceReassignTests.cs`

- [ ] **Step 1: Write failing reassign tests**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class HoldedSyncServiceReassignTests
{
    private readonly Mock<IHoldedClient> _client = new();
    private readonly Mock<IHoldedRepository> _repo = new();
    private readonly Mock<IBudgetService> _budget = new();
    private readonly Mock<IAuditLogService> _audit = new();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 26, 12, 0));
    private HoldedSyncService Make() => new(_client.Object, _repo.Object, _budget.Object, _audit.Object, _clock, NullLogger<HoldedSyncService>.Instance);

    [Fact]
    public async Task ReassignAsync_OnPutSuccess_SavesLocalAndReturnsTagPushedTrue()
    {
        var actor = Guid.NewGuid();
        var docId = "doc-1";
        var groupId = Guid.NewGuid(); var catId = Guid.NewGuid();
        var category = new BudgetCategory { Id = catId, Slug = "sound", BudgetGroupId = groupId };
        _budget.Setup(b => b.GetCategoriesByYearAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { category });
        // Use a single-category lookup helper or hand the service the BudgetGroup.Slug another way.
        _client.Setup(c => c.TryAddTagAsync(docId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // For this test, assume the service can resolve the tag from category id
        // by calling IBudgetService for the category + its group.
        var sut = Make();
        var outcome = await sut.ReassignAsync(docId, catId, actor);

        outcome.LocalMatchSaved.Should().BeTrue();
        outcome.TagPushedToHolded.Should().BeTrue();
        outcome.Warning.Should().BeNull();
        _repo.Verify(r => r.AssignCategoryAsync(docId, catId, It.IsAny<Instant>(), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.LogAsync(It.IsAny<string>(), It.IsAny<string>(), actor, It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReassignAsync_OnPutFailure_StillSavesLocalAndReturnsWarning()
    {
        var actor = Guid.NewGuid();
        var docId = "doc-1";
        var catId = Guid.NewGuid();
        var category = new BudgetCategory { Id = catId, Slug = "sound", BudgetGroupId = Guid.NewGuid() };
        _budget.Setup(b => b.GetCategoriesByYearAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { category });
        _client.Setup(c => c.TryAddTagAsync(docId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = Make();
        var outcome = await sut.ReassignAsync(docId, catId, actor);

        outcome.LocalMatchSaved.Should().BeTrue();
        outcome.TagPushedToHolded.Should().BeFalse();
        outcome.Warning.Should().NotBeNull();
        _repo.Verify(r => r.AssignCategoryAsync(docId, catId, It.IsAny<Instant>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

(The `IAuditLogService.LogAsync` signature shape is illustrative — adapt to whatever the project's actual audit interface looks like. Likely something like `LogAsync(action, entityType, entityId, actorUserId, payload)`.)

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedSyncServiceReassignTests" -v quiet
```

Expected: tests FAIL (ReassignAsync throws `NotImplementedException`).

- [ ] **Step 3: Implement ReassignAsync — needs a way to resolve the tag from a category id**

First, add a helper to `IBudgetService` to resolve a category + its group + year in one call, OR look up a single category by id (which we likely already have). Use whichever exists. For the plan, assume we add a `GetCategoryByIdAsync(Guid id)` if missing — verify against the existing interface and skip if already present.

Then implement `ReassignAsync`:

```csharp
public async Task<ReassignOutcome> ReassignAsync(string holdedDocId, Guid budgetCategoryId, Guid actorUserId, CancellationToken ct = default)
{
    // Resolve the category + its group to compose the Holded tag
    var category = await _budget.GetCategoryByIdAsync(budgetCategoryId, ct)
        ?? throw new InvalidOperationException($"BudgetCategory not found: {budgetCategoryId}");
    var groupSlug = category.BudgetGroup?.Slug
        ?? throw new InvalidOperationException("BudgetCategory.BudgetGroup not loaded; verify GetCategoryByIdAsync includes the group");

    var tag = $"{groupSlug}-{category.Slug}";
    var now = _clock.GetCurrentInstant();

    // Save local match first
    await _repository.AssignCategoryAsync(holdedDocId, budgetCategoryId, now, ct);

    // Audit
    await _audit.LogAsync(
        action: "HoldedReassign",
        entityType: nameof(HoldedTransaction),
        actorUserId: actorUserId,
        payload: new { holdedDocId, budgetCategoryId, tag },
        ct: ct);

    // Push tag to Holded (best-effort)
    var pushed = await _client.TryAddTagAsync(holdedDocId, tag, ct);
    var warning = pushed ? null : "Tag could not be pushed to Holded — please add it manually.";
    return new ReassignOutcome(LocalMatchSaved: true, TagPushedToHolded: pushed, Warning: warning);
}
```

(Adapt the `IAuditLogService` call to match the project's actual signature; the conceptual call — action, entity, actor, payload — is what matters.)

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedSyncServiceReassignTests" -v quiet
```

Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Finance/HoldedSyncService.cs src/Humans.Application/Interfaces/IBudgetService.cs src/Humans.Application/Services/Budget/BudgetService.cs src/Humans.Application/Interfaces/Repositories/IBudgetRepository.cs src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs tests/Humans.Application.Tests/Services/Finance/HoldedSyncServiceReassignTests.cs
git commit -m "feat(finance): manual reassignment with audit log and tag push fallback"
```

---

## Task 14: HoldedSyncJob (Hangfire recurring)

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`

Mirror `src/Humans.Infrastructure/Jobs/TicketSyncJob.cs`.

- [ ] **Step 1: Implement the job**

```csharp
using Hangfire;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 600)]
public class HoldedSyncJob : IRecurringJob
{
    private readonly IHoldedSyncService _syncService;
    private readonly HoldedSettings _settings;
    private readonly ILogger<HoldedSyncJob> _logger;

    public HoldedSyncJob(
        IHoldedSyncService syncService,
        IOptions<HoldedSettings> settings,
        ILogger<HoldedSyncJob> logger)
    {
        _syncService = syncService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            _logger.LogDebug("Holded not configured (Enabled=false or no API key); skipping scheduled sync");
            return;
        }

        _logger.LogInformation("Starting Holded sync job");
        var result = await _syncService.SyncAsync(cancellationToken);
        _logger.LogInformation(
            "Holded sync completed: {Fetched} docs ({Matched} matched, {Unmatched} unmatched)",
            result.DocsFetched, result.Matched, result.Unmatched);
    }
}
```

(`IRecurringJob` is the interface used by `TicketSyncJob` — verify exact namespace; copy the using.)

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs
git commit -m "feat(finance): HoldedSyncJob Hangfire recurring (daily 04:30 UTC)"
```

---

## Task 15: HoldedTransactionService (read queries for views)

**Files:**
- Create: `src/Humans.Application/Interfaces/Finance/IHoldedTransactionService.cs`
- Create: `src/Humans.Application/DTOs/Finance/HoldedTransactionDto.cs`
- Create: `src/Humans.Application/Services/Finance/HoldedTransactionService.cs`
- Create: `tests/Humans.Application.Tests/Services/Finance/HoldedTransactionServiceTests.cs`

- [ ] **Step 1: View DTO**

```csharp
using NodaTime;

namespace Humans.Application.DTOs.Finance;

public sealed record HoldedTransactionDto(
    string HoldedDocId,
    string HoldedDocNumber,
    string ContactName,
    LocalDate Date,
    decimal Total,
    decimal PaymentsTotal,
    decimal PaymentsPending,
    string Currency,
    bool Approved,
    IReadOnlyList<string> Tags,
    string MatchStatusReason,
    string? HoldedDeepLinkUrl,
    Guid? BudgetCategoryId);

public sealed record CategoryActualDto(Guid BudgetCategoryId, decimal ActualTotal);
```

- [ ] **Step 2: Service interface**

```csharp
using Humans.Application.DTOs.Finance;

namespace Humans.Application.Interfaces.Finance;

public interface IHoldedTransactionService
{
    Task<IReadOnlyList<HoldedTransactionDto>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedTransactionDto>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default);
    Task<int> CountUnmatchedAsync(CancellationToken ct = default);
}
```

- [ ] **Step 3: Service implementation**

```csharp
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Finance;

public sealed class HoldedTransactionService : IHoldedTransactionService
{
    private readonly IHoldedRepository _repository;

    public HoldedTransactionService(IHoldedRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<HoldedTransactionDto>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var rows = await _repository.GetUnmatchedAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<HoldedTransactionDto>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default)
    {
        var rows = await _repository.GetByCategoryAsync(budgetCategoryId, ct);
        return rows.Select(ToDto).ToList();
    }

    public Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default)
        => _repository.GetActualSumsByCategoryAsync(budgetYearId, ct);

    public Task<int> CountUnmatchedAsync(CancellationToken ct = default) => _repository.CountUnmatchedAsync(ct);

    private static HoldedTransactionDto ToDto(HoldedTransaction t) => new(
        HoldedDocId: t.HoldedDocId,
        HoldedDocNumber: t.HoldedDocNumber,
        ContactName: t.ContactName,
        Date: t.AccountingDate ?? t.Date,
        Total: t.Total,
        PaymentsTotal: t.PaymentsTotal,
        PaymentsPending: t.PaymentsPending,
        Currency: t.Currency,
        Approved: t.ApprovedAt is not null,
        Tags: t.Tags,
        MatchStatusReason: PlainLanguage(t.MatchStatus, t.Tags),
        HoldedDeepLinkUrl: $"https://app.holded.com/invoicing/purchases/{t.HoldedDocId}",
        BudgetCategoryId: t.BudgetCategoryId);

    private static string PlainLanguage(HoldedMatchStatus status, IReadOnlyList<string> tags) => status switch
    {
        HoldedMatchStatus.Matched => "Matched",
        HoldedMatchStatus.NoTags => "No tags",
        HoldedMatchStatus.UnknownTag => $"Tag(s) not found: {string.Join(", ", tags)}",
        HoldedMatchStatus.MultiMatchConflict => $"Multiple tags resolve to different categories: {string.Join(", ", tags)}",
        HoldedMatchStatus.NoBudgetYearForDate => "No budget year covers this document's date",
        HoldedMatchStatus.UnsupportedCurrency => "Currency is not EUR",
        _ => status.ToString(),
    };
}
```

- [ ] **Step 4: Smoke test (one happy-path test)**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Moq;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

public class HoldedTransactionServiceTests
{
    [Fact]
    public async Task GetUnmatched_MapsMatchStatusToPlainLanguage()
    {
        var repo = new Mock<IHoldedRepository>();
        repo.Setup(r => r.GetUnmatchedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new HoldedTransaction
            {
                HoldedDocId = "a", HoldedDocNumber = "F1", ContactName = "V",
                Date = new LocalDate(2026, 4, 1), Total = 100, Currency = "eur",
                Tags = new[] { "sound" }, MatchStatus = HoldedMatchStatus.UnknownTag,
            }
        });
        var sut = new HoldedTransactionService(repo.Object);
        var result = await sut.GetUnmatchedAsync();
        result.Should().HaveCount(1);
        result[0].MatchStatusReason.Should().Contain("not found");
        result[0].HoldedDeepLinkUrl.Should().StartWith("https://app.holded.com/");
    }
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedTransactionServiceTests" -v quiet
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/DTOs/Finance/HoldedTransactionDto.cs src/Humans.Application/Interfaces/Finance/IHoldedTransactionService.cs src/Humans.Application/Services/Finance/HoldedTransactionService.cs tests/Humans.Application.Tests/Services/Finance/HoldedTransactionServiceTests.cs
git commit -m "feat(finance): HoldedTransactionService read queries"
```

---

## Task 16: FinanceController — Holded actions

**Files:**
- Modify: `src/Humans.Web/Controllers/FinanceController.cs`

- [ ] **Step 1: Inject `IHoldedSyncService`, `IHoldedTransactionService`, `IBudgetService` (latter likely already injected)**

In the `FinanceController` constructor, add the new dependencies. Confirm `[Authorize(Roles = "FinanceAdmin,Admin")]` is at controller level (or matches existing convention).

- [ ] **Step 2: Add HoldedUnmatched action**

```csharp
[HttpGet]
public async Task<IActionResult> HoldedUnmatched(CancellationToken ct)
{
    var unmatched = await _holdedTransactions.GetUnmatchedAsync(ct);
    var activeYear = await _budgetService.GetActiveYearAsync(ct); // or whichever method returns the current year
    var categories = activeYear is null
        ? Array.Empty<BudgetCategory>()
        : await _budgetService.GetCategoriesByYearAsync(activeYear.Id, ct);
    var vm = new HoldedUnmatchedViewModel(unmatched, categories);
    return View(vm);
}
```

- [ ] **Step 3: Add HoldedTags action**

```csharp
[HttpGet]
public async Task<IActionResult> HoldedTags(CancellationToken ct)
{
    var activeYear = await _budgetService.GetActiveYearAsync(ct);
    var inventory = activeYear is null
        ? Array.Empty<HoldedTagInventoryRow>()
        : await _budgetService.GetTagInventoryAsync(activeYear.Id, ct);
    return View(inventory);
}
```

- [ ] **Step 4: Add HoldedSyncRun POST action**

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> HoldedSyncRun(CancellationToken ct)
{
    var result = await _holdedSyncService.SyncAsync(ct);
    TempData["HoldedSyncMessage"] =
        $"Synced {result.DocsFetched} docs ({result.Matched} matched, {result.Unmatched} unmatched).";
    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 5: Add HoldedReassign POST action**

```csharp
[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> HoldedReassign(string holdedDocId, Guid budgetCategoryId, CancellationToken ct)
{
    var actorId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value); // or whatever the project uses
    var outcome = await _holdedSyncService.ReassignAsync(holdedDocId, budgetCategoryId, actorId, ct);
    if (outcome.Warning is not null)
        TempData["HoldedReassignWarning"] = outcome.Warning;
    return RedirectToAction(nameof(HoldedUnmatched));
}
```

- [ ] **Step 6: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/FinanceController.cs
git commit -m "feat(finance): controller actions for Holded sync, unmatched queue, tags"
```

---

## Task 17: /Finance/HoldedUnmatched view

**Files:**
- Create: `src/Humans.Web/Views/Finance/HoldedUnmatched.cshtml`
- Create the corresponding view-model class (or use an inline tuple) — keep it next to the controller or under `Humans.Web/ViewModels/Finance/`

- [ ] **Step 1: Add the view-model**

```csharp
using Humans.Application.DTOs.Finance;
using Humans.Domain.Entities;

namespace Humans.Web.ViewModels.Finance;

public sealed record HoldedUnmatchedViewModel(
    IReadOnlyList<HoldedTransactionDto> Unmatched,
    IReadOnlyList<BudgetCategory> AvailableCategories);
```

- [ ] **Step 2: Create the view**

```razor
@model Humans.Web.ViewModels.Finance.HoldedUnmatchedViewModel
@{
    ViewData["Title"] = "Holded — Unmatched";
}

<h2>Unmatched Holded documents</h2>

@if (TempData["HoldedReassignWarning"] is string warning)
{
    <div class="alert alert-warning">@warning</div>
}

@if (Model.Unmatched.Count == 0)
{
    <p>No unmatched documents — every Holded purchase is mapped to a budget category.</p>
}
else
{
    <table class="table table-sm">
        <thead>
            <tr>
                <th>Doc #</th><th>Date</th><th>Vendor</th><th>Total</th>
                <th>Tags</th><th>Reason</th><th>→ Holded</th><th>Action</th>
            </tr>
        </thead>
        <tbody>
        @foreach (var row in Model.Unmatched)
        {
            <tr>
                <td>@row.HoldedDocNumber</td>
                <td>@row.Date</td>
                <td>@row.ContactName</td>
                <td>@row.Total.ToString("N2") @row.Currency.ToUpperInvariant()</td>
                <td>@(row.Tags.Count == 0 ? "—" : string.Join(", ", row.Tags))</td>
                <td>@row.MatchStatusReason</td>
                <td>
                    @if (row.HoldedDeepLinkUrl is not null)
                    {
                        <a href="@row.HoldedDeepLinkUrl" target="_blank" rel="noopener">Open</a>
                    }
                </td>
                <td>
                    <form asp-action="HoldedReassign" method="post" class="d-flex gap-2">
                        @Html.AntiForgeryToken()
                        <input type="hidden" name="holdedDocId" value="@row.HoldedDocId" />
                        <select name="budgetCategoryId" class="form-select form-select-sm">
                            <option value="">— pick category —</option>
                            @foreach (var cat in Model.AvailableCategories)
                            {
                                <option value="@cat.Id">@cat.BudgetGroup?.Name › @cat.Name</option>
                            }
                        </select>
                        <button type="submit" class="btn btn-sm btn-primary">Assign</button>
                    </form>
                </td>
            </tr>
        }
        </tbody>
    </table>
}
```

- [ ] **Step 3: Build + run smoke**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Finance/HoldedUnmatched.cshtml src/Humans.Web/ViewModels/Finance/HoldedUnmatchedViewModel.cs
git commit -m "feat(finance): /Finance/HoldedUnmatched view"
```

---

## Task 18: /Finance/HoldedTags view (read-only inventory)

**Files:**
- Create: `src/Humans.Web/Views/Finance/HoldedTags.cshtml`

- [ ] **Step 1: Create the view**

```razor
@model IReadOnlyList<Humans.Application.DTOs.Finance.HoldedTagInventoryRow>
@{
    ViewData["Title"] = "Holded — Tag inventory";
}

<h2>Holded tag inventory</h2>
<p class="text-muted">Tag each Holded purchase with one of these tags so it lands in the right budget category.</p>

@if (Model.Count == 0)
{
    <p>No active budget year, or no categories defined.</p>
}
else
{
    <table class="table table-sm">
        <thead><tr><th>Year</th><th>Group</th><th>Category</th><th>Holded Tag</th><th></th></tr></thead>
        <tbody>
        @foreach (var row in Model)
        {
            <tr>
                <td>@row.Year</td>
                <td>@row.GroupName</td>
                <td>@row.CategoryName</td>
                <td><code>@row.Tag</code></td>
                <td>
                    <button type="button" class="btn btn-sm btn-outline-secondary"
                            onclick="navigator.clipboard.writeText('@row.Tag')">Copy</button>
                </td>
            </tr>
        }
        </tbody>
    </table>
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Finance/HoldedTags.cshtml
git commit -m "feat(finance): /Finance/HoldedTags read-only tag inventory"
```

---

## Task 19: Sync state card on /Finance index + nav links

**Files:**
- Create: `src/Humans.Web/Views/Shared/_HoldedSyncCard.cshtml`
- Modify: `src/Humans.Web/Views/Finance/Index.cshtml` (or `YearDetail.cshtml` — wherever the dashboard top is)
- Modify: `FinanceController` to pass sync-state data to `Index` (or add a child action / view component)

- [ ] **Step 1: Add a tiny method on `IHoldedTransactionService` if needed, or reuse `IHoldedRepository` directly via service**

The card needs `LastSyncAt`, `SyncStatus`, `LastError`, `LastSyncedDocCount`, `CountUnmatched`. Add a single read method to `IHoldedSyncService`:

```csharp
Task<HoldedSyncDashboardDto> GetSyncDashboardAsync(CancellationToken ct = default);
```

```csharp
namespace Humans.Application.DTOs.Finance;

public sealed record HoldedSyncDashboardDto(
    NodaTime.Instant? LastSyncAt,
    Humans.Domain.Enums.HoldedSyncStatus SyncStatus,
    string? LastError,
    int LastSyncedDocCount,
    int UnmatchedCount);
```

Implement in `HoldedSyncService`:

```csharp
public async Task<HoldedSyncDashboardDto> GetSyncDashboardAsync(CancellationToken ct = default)
{
    var state = await _repository.GetSyncStateAsync(ct);
    var unmatched = await _repository.CountUnmatchedAsync(ct);
    return new HoldedSyncDashboardDto(state.LastSyncAt, state.SyncStatus, state.LastError, state.LastSyncedDocCount, unmatched);
}
```

- [ ] **Step 2: Build the partial view**

```razor
@model Humans.Application.DTOs.Finance.HoldedSyncDashboardDto

<div class="card mb-3">
    <div class="card-body d-flex align-items-center justify-content-between flex-wrap gap-2">
        <div>
            <h5 class="card-title mb-1">Holded sync</h5>
            <div class="text-muted small">
                Status: <strong>@Model.SyncStatus</strong>
                @if (Model.LastSyncAt is { } t)
                {
                    <text> · Last sync @t.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)</text>
                }
                · @Model.LastSyncedDocCount docs last cycle
                @if (Model.LastError is not null)
                {
                    <text> · </text>
                    <span class="text-danger" title="@Model.LastError">Error</span>
                }
            </div>
        </div>
        <div class="d-flex align-items-center gap-2">
            <a asp-action="HoldedUnmatched" class="btn btn-outline-secondary btn-sm">
                Unmatched: <span class="badge bg-warning text-dark">@Model.UnmatchedCount</span>
            </a>
            <a asp-action="HoldedTags" class="btn btn-link btn-sm">Tag reference</a>
            <form asp-action="HoldedSyncRun" method="post" class="m-0">
                @Html.AntiForgeryToken()
                <button type="submit" class="btn btn-primary btn-sm">Sync now</button>
            </form>
        </div>
    </div>
</div>
```

- [ ] **Step 3: Embed the partial in `Index.cshtml` (or `YearDetail.cshtml`) above the year selector**

```razor
@{
    var holdedDashboard = await Component.InvokeAsync("HoldedSyncCard"); // or via ViewData / direct render
}
```

Simpler approach: have `FinanceController.Index` populate `ViewData["HoldedSync"]` with the DTO and render via `<partial name="_HoldedSyncCard" model="@(...)"/>`.

- [ ] **Step 4: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/DTOs/Finance/HoldedSyncDashboardDto.cs src/Humans.Application/Interfaces/Finance/IHoldedSyncService.cs src/Humans.Application/Services/Finance/HoldedSyncService.cs src/Humans.Web/Views/Shared/_HoldedSyncCard.cshtml src/Humans.Web/Views/Finance/Index.cshtml src/Humans.Web/Controllers/FinanceController.cs
git commit -m "feat(finance): sync state card on /Finance dashboard"
```

---

## Task 20: Actual column on /Finance YearDetail + per-category drill-down

**Files:**
- Modify: `src/Humans.Web/Controllers/FinanceController.cs` (year-detail action)
- Modify: `src/Humans.Web/Views/Finance/YearDetail.cshtml`

- [ ] **Step 1: Fetch per-category actuals in the year-detail controller action**

In the action that renders `YearDetail`, add:

```csharp
var actualsByCategory = await _holdedTransactions.GetActualSumsByCategoryAsync(year.Id, ct);
ViewData["HoldedActuals"] = actualsByCategory;
```

- [ ] **Step 2: Render the Actual column in `YearDetail.cshtml`**

For each category row, look up:

```razor
@{
    var actuals = ViewData["HoldedActuals"] as IReadOnlyDictionary<Guid, decimal>;
    var actual = actuals is not null && actuals.TryGetValue(category.Id, out var a) ? a : 0m;
    var allocated = category.AllocatedAmount;
    var variance = allocated - actual;
    // direction-aware: negative AllocatedAmount = income, positive = expense
    var isOver = allocated >= 0 ? actual > allocated : actual < allocated;
}
<td class="text-end">@allocated.ToString("N2")</td>
<td class="text-end">@actual.ToString("N2")</td>
<td class="text-end @(isOver ? "text-danger" : "text-success")">@variance.ToString("N2")</td>
```

- [ ] **Step 3: Add per-category drill-down — collapsible row showing transactions**

When the user expands a category, fetch via a child controller action `HoldedByCategory(Guid id)` that returns a partial view, OR pre-render and hide. For the simple implementation, expose the action and lazy-load:

```csharp
[HttpGet]
public async Task<IActionResult> HoldedByCategory(Guid id, CancellationToken ct)
{
    var rows = await _holdedTransactions.GetByCategoryAsync(id, ct);
    return PartialView("_HoldedTransactionList", rows);
}
```

Create `src/Humans.Web/Views/Finance/_HoldedTransactionList.cshtml`:

```razor
@model IReadOnlyList<Humans.Application.DTOs.Finance.HoldedTransactionDto>

@if (Model.Count == 0)
{
    <p class="text-muted small">No actuals for this category yet.</p>
}
else
{
    <table class="table table-sm">
        <thead><tr><th>Doc #</th><th>Date</th><th>Vendor</th><th>Total</th><th></th></tr></thead>
        <tbody>
        @foreach (var t in Model)
        {
            <tr>
                <td>@t.HoldedDocNumber</td>
                <td>@t.Date</td>
                <td>@t.ContactName</td>
                <td class="text-end">@t.Total.ToString("N2")</td>
                <td><a href="@t.HoldedDeepLinkUrl" target="_blank" rel="noopener">Open</a></td>
            </tr>
        }
        </tbody>
    </table>
}
```

Wire the existing accordion in `YearDetail.cshtml` to call `HoldedByCategory` via `htmx`/`fetch` on expand and inject the result.

- [ ] **Step 4: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/FinanceController.cs src/Humans.Web/Views/Finance/YearDetail.cshtml src/Humans.Web/Views/Finance/_HoldedTransactionList.cshtml
git commit -m "feat(finance): planned vs actual column + Holded drill-down on year detail"
```

---

## Task 21: Slug input on BudgetGroup and BudgetCategory edit forms

**Files:**
- Modify: the existing edit/create views/partials for `BudgetGroup` and `BudgetCategory` under `src/Humans.Web/Views/Finance/`

- [ ] **Step 1: Add Slug input field below Name on both forms**

```razor
<div class="mb-3">
    <label asp-for="Slug" class="form-label">Holded tag slug</label>
    <input asp-for="Slug" class="form-control" />
    <small class="form-text text-muted">
        Lowercase, no spaces / accents / symbols. Used as part of the Holded tag.
        @if (Model.Id != Guid.Empty)
        {
            <strong class="text-warning d-block">
                Existing Holded tags using the old slug will become unmatched on next sync — fix tags in Holded after saving.
            </strong>
        }
    </small>
</div>
```

- [ ] **Step 2: Auto-populate Slug from Name on create** — if there's already a `[ValidateNever]` Slug or it's currently empty, default it server-side via `SlugNormalizer.Normalize(name)` in the controller create action before saving.

In the controller create actions for `BudgetGroup` and `BudgetCategory`:

```csharp
if (string.IsNullOrWhiteSpace(model.Slug))
    model.Slug = SlugNormalizer.Normalize(model.Name);
```

- [ ] **Step 3: Build + run all tests to confirm no regression**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Finance/ src/Humans.Web/Controllers/FinanceController.cs
git commit -m "feat(finance): Slug field on BudgetGroup/Category edit forms with auto-populate"
```

---

## Task 22: FinanceSectionExtensions — DI wiring + Hangfire registration

**Files:**
- Create: `src/Humans.Web/Extensions/Sections/FinanceSectionExtensions.cs`
- Modify: `src/Humans.Web/Program.cs` to call `AddFinanceSection()` and register the recurring job

Mirror `src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs`.

- [ ] **Step 1: Create the extension**

```csharp
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Finance;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Web.Extensions.Sections;

internal static class FinanceSectionExtensions
{
    internal static IServiceCollection AddFinanceSection(this IServiceCollection services, IConfiguration config)
    {
        // Bind HoldedSettings; ApiKey is overlaid from env var HOLDED_API_KEY.
        services.Configure<HoldedSettings>(s =>
        {
            config.GetSection(HoldedSettings.SectionName).Bind(s);
            var envKey = Environment.GetEnvironmentVariable("HOLDED_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey)) s.ApiKey = envKey;
        });

        // Repository — Singleton + IDbContextFactory per design-rules §15b
        services.AddSingleton<IHoldedRepository, HoldedRepository>();

        // Services — scoped
        services.AddScoped<IHoldedSyncService, HoldedSyncService>();
        services.AddScoped<IHoldedTransactionService, HoldedTransactionService>();

        // Vendor connector — typed HttpClient
        services.AddHttpClient<IHoldedClient, HoldedClient>();

        // Hangfire job (registered as scoped so the recurring registration in Program.cs can resolve it)
        services.AddScoped<HoldedSyncJob>();

        return services;
    }
}
```

- [ ] **Step 2: Wire it into Program.cs**

In `src/Humans.Web/Program.cs`, where other section extensions are called, add:

```csharp
builder.Services.AddFinanceSection(builder.Configuration);
```

In the Hangfire setup block (near where `TicketSyncJob` is registered as recurring), add:

```csharp
RecurringJob.AddOrUpdate<HoldedSyncJob>(
    "holded-sync",
    j => j.ExecuteAsync(CancellationToken.None),
    builder.Configuration["Holded:SyncIntervalCron"] ?? "0 30 4 * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
```

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Extensions/Sections/FinanceSectionExtensions.cs src/Humans.Web/Program.cs
git commit -m "feat(finance): DI wiring + Hangfire recurring registration"
```

---

## Task 23: appsettings configuration

**Files:**
- Modify: `src/Humans.Web/appsettings.json`

- [ ] **Step 1: Add the Holded section**

```jsonc
{
  // ...existing settings...
  "Holded": {
    "Enabled": true,
    "SyncIntervalCron": "0 30 4 * * *"
  }
}
```

(Do NOT add `ApiKey` here — it comes from the env var.)

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/appsettings.json
git commit -m "feat(finance): appsettings entries for Holded sync"
```

---

## Task 24: FinanceArchitectureTests

**Files:**
- Create: `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`

Mirror `TicketSyncArchitectureTests.cs`. Pin: namespace, no `DbContext` parameter, takes `IHoldedRepository`, no cross-section repository injected, no direct `BudgetCategory` table access from the service, repository is sealed + implements interface.

- [ ] **Step 1: Write tests**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Finance;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HoldedSyncService = Humans.Application.Services.Finance.HoldedSyncService;
using HoldedTransactionService = Humans.Application.Services.Finance.HoldedTransactionService;

namespace Humans.Application.Tests.Architecture;

public class FinanceArchitectureTests
{
    [HumansFact]
    public void HoldedSyncService_LivesInHumansApplicationServicesFinanceNamespace()
    {
        typeof(HoldedSyncService).Namespace
            .Should().Be("Humans.Application.Services.Finance");
    }

    [HumansFact]
    public void HoldedSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(p => typeof(DbContext).IsAssignableFrom(p.ParameterType));
    }

    [HumansFact]
    public void HoldedSyncService_TakesHoldedRepository()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(IHoldedRepository));
    }

    [HumansFact]
    public void HoldedSyncService_TakesBudgetServiceForCrossSectionReads()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(IBudgetService));
    }

    [HumansFact]
    public void HoldedSyncService_DoesNotTakeBudgetRepository()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(IBudgetRepository),
                because: "Finance must not reach into Budget's repository — read through IBudgetService only");
    }

    [HumansFact]
    public void HoldedSyncService_TakesVendorConnectorInterface()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(IHoldedClient));
    }

    [HumansFact]
    public void IHoldedRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IHoldedRepository).Namespace.Should().Be("Humans.Application.Interfaces.Repositories");
    }

    [HumansFact]
    public void HoldedRepository_IsSealed()
    {
        typeof(HoldedRepository).IsSealed.Should().BeTrue();
    }

    [HumansFact]
    public void HoldedRepository_ImplementsIHoldedRepository()
    {
        typeof(IHoldedRepository).IsAssignableFrom(typeof(HoldedRepository)).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~FinanceArchitectureTests" -v quiet
```

Expected: 9 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs
git commit -m "test(finance): architecture tests pin §15 shape"
```

---

## Task 25: Live API smoke + nav-link audit + final verification

- [ ] **Step 1: Confirm `HOLDED_API_KEY` is in env, run app locally**

```bash
dotnet run --project src/Humans.Web -v quiet
```

Navigate to `https://nuc.home:<port>/Finance`. The sync card should appear with `Idle` status and 0 docs synced.

- [ ] **Step 2: Trigger a manual sync via the "Sync now" button**

After completion, the card should show `Idle`, the doc count, and the unmatched count. Clicking "Unmatched" should land on the queue with the 2 production docs (which currently have no tags).

- [ ] **Step 3: Verify the inventory page**

Navigate to `/Finance/HoldedTags`. Confirm every category in the active year shows with a `{group-slug}-{category-slug}` tag.

- [ ] **Step 4: Tag one Holded doc in Holded's UI with a known tag**

Pick a real category (e.g. `departments-cantina`), tag one of the production docs in Holded directly, hit "Sync now". The doc should drop off the unmatched queue and appear under the Cantina category drill-down on `/Finance` year-detail with the correct Total in the Actual column.

- [ ] **Step 5: Test the manual reassignment flow with PUT verification**

On a still-untagged doc, pick a category from the unmatched-queue dropdown and click Assign. Verify:
- Doc disappears from the queue.
- Local `HoldedTransaction.BudgetCategoryId` is set (check via category drill-down).
- Tag was pushed to Holded (refresh Holded's UI to confirm).
- If PUT failed (e.g. PUT-tag unsupported): warning banner appears, local match is still saved.

- [ ] **Step 6: Nav-link audit (CLAUDE.md "no orphan pages")**

Confirm every new page is reachable:
- `/Finance/HoldedUnmatched` — sync card on `/Finance`
- `/Finance/HoldedTags` — secondary "Tag reference" link in sync card
- Per-category drill-down — inline expand on year-detail

- [ ] **Step 7: Run the full suite**

```bash
dotnet test Humans.slnx -v quiet
```

Expected: full suite PASS.

- [ ] **Step 8: Commit any UI tweaks discovered during smoke**

```bash
git add -- <touched files>
git commit -m "fix(finance): polish from live smoke test"
```

- [ ] **Step 9: Push the branch**

```bash
git push -u origin holded-impl
```

- [ ] **Step 10: Open PR to peterdrier/main**

```bash
gh pr create --repo peterdrier/Humans --base main --head holded-impl \
  --title "Holded read-side integration (nobodies-collective#463)" \
  --body "Implements the spec at docs/superpowers/specs/2026-04-26-holded-read-integration-design.md. Closes nobodies-collective#463 once production-merged."
```

---

## Summary of safety nets

- Every entity change runs through the EF migration reviewer agent (Task 5).
- Every service change is pinned by `FinanceArchitectureTests` (Task 24).
- Every cross-section read goes through `IBudgetService` (no `IBudgetRepository` in `HoldedSyncService`'s constructor — pinned by Task 24).
- Every PUT call has a documented fallback (Task 9, Task 13).
- Every new page has a nav link (Task 19, Task 25).
- Every doc-touching change is followed by `dotnet build -v quiet` and the relevant test filter.
