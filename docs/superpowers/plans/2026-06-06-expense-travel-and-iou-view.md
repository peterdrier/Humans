# Expense Travel Lines + Personal IOU View — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add mileage & per-diem expense lines (no receipt required, computed server-side via a wizard at 2026 Spanish tax-exempt rates), and surface each member's Holded IOU balance + a reports/payments ledger on `/Expenses`.

**Architecture:** All work stays in the Expenses section (DbContext → Repository → Service → Controller), reusing the existing line-insert path and the existing `GetHoldedTimelineAsync` cross-section read. One small read-only DTO extension in Finance surfaces individual payment rows. No new `*ServiceRead` interface method (preserves `[SurfaceBudget(7)]`).

**Tech Stack:** .NET (C# 12 primary constructors), EF Core + NodaTime, ASP.NET MVC + Razor, xUnit (`[HumansFact]`) + NSubstitute + AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-06-06-expense-travel-and-iou-view-design.md`

**Working directory:** `H:\source\Humans\.worktrees\expense-travel-iou` (branch `feat/expense-travel-iou`). `cd` here once; run all commands from here. Build with `dotnet build Humans.slnx -v quiet`; the `-v quiet` flag is required.

---

## File Map

**Feature 1 — travel lines**
- Create `src/Humans.Domain/Enums/ExpenseLineType.cs` — line discriminator.
- Create `src/Humans.Domain/Enums/PerDiemKind.cs` — non-persisted wizard param.
- Modify `src/Humans.Domain/Entities/ExpenseLine.cs` — add `LineType`.
- Modify `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseLineConfiguration.cs` — map `LineType`.
- Migration `AddExpenseLineType` (generated; never hand-edited).
- Modify `src/Humans.Application/Services/Expenses/Dtos/ExpenseLineDto.cs` — add `LineType`.
- Modify `src/Humans.Infrastructure/Repositories/Expenses/ExpenseReportMapper.cs` — map `LineType`.
- Create `src/Humans.Application/Services/Expenses/Dtos/TravelReimbursementConfig.cs` — rate config.
- Modify `src/Humans.Web/Extensions/Sections/ExpensesSectionExtensions.cs` — bind config.
- Modify `src/Humans.Web/appsettings.json` — `TravelReimbursement` section.
- Modify `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs` — two wizard methods.
- Modify `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` — inject config, extend `AddLineAsync`, add wizard methods, change submit rule.
- Modify the 4 test files that construct `ExpenseReportService` (add config arg).
- Modify `src/Humans.Web/Controllers/ExpensesController.cs` — two endpoints + Index summary.
- Modify `src/Humans.Web/Models/ExpensesViewModels.cs` — input models + IOU view models.
- Modify `src/Humans.Web/Views/Expenses/Edit.cshtml` — wizards + line badges.
- Modify `src/Humans.Web/Views/Expenses/Detail.cshtml` — line-type badge / no-receipt-warning suppression.

**Feature 2 — IOU view**
- Create `src/Humans.Application/Services/Finance/Dtos/HoldedPaymentInfo.cs` — payment row.
- Modify `src/Humans.Application/Services/Finance/Dtos/HoldedCreditorStatus.cs` — add `Payments`.
- Modify `src/Humans.Application/Services/Finance/HoldedFinanceService.cs` — map payment rows.
- Modify `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs` — extend `ExpenseHoldedTimeline` record.
- Modify `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` — map payments into timeline.
- Modify `src/Humans.Web/Views/Expenses/Index.cshtml` — IOU card + ledger.

**Docs**
- Modify `docs/sections/Expenses.md`, `docs/sections/Finance.md`.

---

## Task 1: Domain enums + `ExpenseLine.LineType`

**Files:**
- Create: `src/Humans.Domain/Enums/ExpenseLineType.cs`
- Create: `src/Humans.Domain/Enums/PerDiemKind.cs`
- Modify: `src/Humans.Domain/Entities/ExpenseLine.cs`

- [ ] **Step 1: Create `ExpenseLineType`**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Kind of expense line. <see cref="Receipt"/> lines require an attachment at submit time;
/// travel lines (<see cref="Mileage"/> / <see cref="PerDiem"/>) are justified by the trip, not a receipt.
/// </summary>
public enum ExpenseLineType
{
    Receipt = 0,
    Mileage,
    PerDiem
}
```

- [ ] **Step 2: Create `PerDiemKind`**

```csharp
namespace Humans.Domain.Enums;

/// <summary>Spanish per-diem (dieta) kind, selecting the tax-exempt daily rate. Not persisted —
/// a parameter to the per-diem wizard only.</summary>
public enum PerDiemKind
{
    DayTrip = 0,
    Overnight
}
```

- [ ] **Step 3: Add `LineType` to `ExpenseLine`**

In `src/Humans.Domain/Entities/ExpenseLine.cs`, add the `using` and the property:

```csharp
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class ExpenseLine
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public ExpenseLineType LineType { get; set; }
    public Guid? AttachmentId { get; set; }
    public int SortOrder { get; set; }

    public ExpenseAttachment? Attachment { get; set; }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (0 errors).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain
git commit -m "feat(expenses): add ExpenseLineType + PerDiemKind enums and ExpenseLine.LineType"
```

---

## Task 2: EF mapping + migration

**Files:**
- Modify: `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseLineConfiguration.cs`
- Migration: generated under `src/Humans.Infrastructure/Migrations/`

- [ ] **Step 1: Map `LineType` as a string column defaulting to `Receipt`**

In `ExpenseLineConfiguration.Configure`, add the `using` and the property mapping:

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class ExpenseLineConfiguration : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> b)
    {
        b.ToTable("expense_lines");
        b.HasKey(x => x.Id);

        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.Amount).HasColumnType("decimal(12,2)");

        b.Property(x => x.LineType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(ExpenseLineType.Receipt);

        b.HasOne(x => x.Attachment)
            .WithMany()
            .HasForeignKey(x => x.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ExpenseReportId);
    }
}
```

- [ ] **Step 2: Generate the migration (never hand-edit it)**

Run: `dotnet ef migrations add AddExpenseLineType --project src/Humans.Infrastructure --startup-project src/Humans.Web`
Expected: a new `*_AddExpenseLineType.cs` + `.Designer.cs` + updated `HumansDbContextModelSnapshot.cs`.

- [ ] **Step 3: Verify the generated `Up` adds the column with a string default**

Open the new migration. The `Up` must contain an `AddColumn<string>` for `LineType` on `expense_lines` with `defaultValue: "Receipt"` (and `maxLength: 20`, `nullable: false`). If the default is wrong (e.g. `0` or empty), fix the **model** (Step 1's `HasDefaultValue`) and regenerate — do not hand-edit the migration (`memory/architecture/migration-regen-after-rebase.md`, `feedback_never_hand_edit_migrations`).

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure
git commit -m "feat(expenses): map ExpenseLine.LineType (string, default Receipt) + migration"
```

---

## Task 3: DTO + mapper + GDPR slice

**Files:**
- Modify: `src/Humans.Application/Services/Expenses/Dtos/ExpenseLineDto.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Expenses/ExpenseReportMapper.cs`
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` (GDPR slice)

- [ ] **Step 1: Add `LineType` to `ExpenseLineDto`**

```csharp
using Humans.Domain.Enums;

namespace Humans.Application.Services.Expenses.Dtos;

public sealed record ExpenseLineDto
{
    public required Guid Id { get; init; }
    public required Guid ExpenseReportId { get; init; }
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public required ExpenseLineType LineType { get; init; }
    public Guid? AttachmentId { get; init; }
    public ExpenseAttachmentDto? Attachment { get; init; }
    public required int SortOrder { get; init; }
}
```

- [ ] **Step 2: Map it in `ExpenseReportMapper.ToLineDto`**

Add `LineType = l.LineType,` to the `ToLineDto` initializer:

```csharp
    internal static ExpenseLineDto ToLineDto(ExpenseLine l) => new()
    {
        Id = l.Id,
        ExpenseReportId = l.ExpenseReportId,
        Description = l.Description,
        Amount = l.Amount,
        LineType = l.LineType,
        AttachmentId = l.AttachmentId,
        Attachment = l.Attachment is null ? null : ToAttachmentDto(l.Attachment),
        SortOrder = l.SortOrder
    };
```

- [ ] **Step 3: Include `LineType` in the GDPR export line slice**

In `ExpenseReportService.cs`, the GDPR `shapedReports` projection builds an anonymous line object. Add `l.LineType` to it:

```csharp
                Lines = r.Lines.Select(l => new
                {
                    l.Id,
                    l.Description,
                    l.Amount,
                    l.LineType,
                    l.SortOrder,
                    Attachment = l.Attachment is null
                        ? null
                        : new
                        {
                            l.Attachment.OriginalFileName,
                            l.Attachment.ContentType,
                            l.Attachment.SizeBytes,
                        }
                }).ToList()
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds. (If any other `new ExpenseLineDto`/`ExpenseLineDto {` site exists it will now fail to compile on the required field — there are none beyond the mapper; if the compiler reports one, add `LineType` there.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application src/Humans.Infrastructure
git commit -m "feat(expenses): carry LineType through ExpenseLineDto, mapper, and GDPR export"
```

---

## Task 4: `TravelReimbursementConfig` + DI + appsettings

**Files:**
- Create: `src/Humans.Application/Services/Expenses/Dtos/TravelReimbursementConfig.cs`
- Modify: `src/Humans.Web/Extensions/Sections/ExpensesSectionExtensions.cs`
- Modify: `src/Humans.Web/appsettings.json`

- [ ] **Step 1: Create the config DTO (defaults = 2026 Spanish tax-exempt rates)**

```csharp
namespace Humans.Application.Services.Expenses.Dtos;

/// <summary>
/// Travel-reimbursement rates — 2026 Spanish IRPF tax-exempt limits.
/// Bound from the appsettings "TravelReimbursement" section. Defaults are the live
/// 2026 values so the section works without explicit configuration.
/// </summary>
public sealed class TravelReimbursementConfig
{
    /// <summary>€/km. Orden HFP/792/2023 raised this from 0.19 to 0.26 in Jul-2023; unchanged for 2026.</summary>
    public decimal MileageRatePerKm { get; set; } = 0.26m;

    /// <summary>€/day — manutención sin pernocta (day trip, within Spain).</summary>
    public decimal PerDiemDayTripRate { get; set; } = 26.67m;

    /// <summary>€/day — manutención con pernocta (overnight, within Spain).</summary>
    public decimal PerDiemOvernightRate { get; set; } = 53.34m;
}
```

- [ ] **Step 2: Bind it in `AddExpensesSection`**

Add the `Configure` call alongside the existing SEPA one (the `using Humans.Application.Services.Expenses.Dtos;` is already present):

```csharp
        // Travel-reimbursement rates — bind from appsettings "TravelReimbursement"; defaults are the 2026 values.
        services.Configure<TravelReimbursementConfig>(config.GetSection("TravelReimbursement"));
```

- [ ] **Step 3: Add the appsettings section**

In `src/Humans.Web/appsettings.json`, add a top-level section (sibling of `"Sepa"`):

```json
  "TravelReimbursement": {
    "MileageRatePerKm": 0.26,
    "PerDiemDayTripRate": 26.67,
    "PerDiemOvernightRate": 53.34
  },
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application src/Humans.Web
git commit -m "feat(expenses): add TravelReimbursementConfig (2026 Spanish rates) + DI binding"
```

---

## Task 5: Service wizard methods (mileage + per diem)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs`
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs`
- Modify (constructor arg): `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`, `ExpenseReportServiceHoldedOutboxTests.cs`, `ExpenseReportServiceHoldedPollingTests.cs`, `ExpenseReportServiceGdprTests.cs`
- Test: `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

- [ ] **Step 1: Inject the config into the service**

In `ExpenseReportService.cs`, add `using Microsoft.Extensions.Options;` (and ensure `using Humans.Application.Services.Expenses.Dtos;` and `using System.Globalization;` are present). Append the config to the primary constructor (after `logger`) and store its value:

```csharp
public sealed class ExpenseReportService(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IBudgetService budgetService,
    ITeamService teamService,
    IUserService userService,
    IAuditLogService auditLogService,
    IHoldedClient holdedClient,
    IHoldedFinanceService holdedFinance,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    IOptions<TravelReimbursementConfig> travelConfig) : IExpenseReportService,
        IExpenseReportBackgroundProcessor, IUserDataContributor
{
    private readonly IHoldedFinanceService _holdedFinance = holdedFinance;
    private readonly TravelReimbursementConfig _travel = travelConfig.Value;
```

- [ ] **Step 2: Extend internal `AddLineAsync` with a line type**

Change the internal `AddLineAsync` signature and body (insert `lineType` before `ct`, set it on the entity):

```csharp
    internal async Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount,
            LineType = lineType
        };
        var ok = await repo.AddLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to add line.");
        return line.Id;
    }
```

Then fix the existing caller inside `AddLineWithResultAsync` (its positional `ct` now lands on `lineType`). Pass `ExpenseLineType.Receipt` explicitly:

```csharp
            await AddLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.Receipt, ct);
```

(Add `using Humans.Domain.Enums;` if not already present.)

- [ ] **Step 3: Add the two wizard methods to the implementation**

Place these public methods near `AddLineWithResultAsync`:

```csharp
    public async Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default)
    {
        try
        {
            var rate = _travel.MileageRatePerKm;
            var amount = Math.Round(km * rate, 2, MidpointRounding.AwayFromZero);
            var description =
                $"{origin.Trim()} to {destination.Trim()}, " +
                $"{km.ToString("0.#", CultureInfo.InvariantCulture)} km @ " +
                $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
                $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
            await AddLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.Mileage, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding mileage line to report {ReportId}", reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }

    public async Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default)
    {
        try
        {
            var rate = kind == PerDiemKind.Overnight ? _travel.PerDiemOvernightRate : _travel.PerDiemDayTripRate;
            var amount = Math.Round(days * rate, 2, MidpointRounding.AwayFromZero);
            var kindLabel = kind == PerDiemKind.Overnight ? "overnight" : "day-trip";
            var dayWord = days == 1 ? "day" : "days";
            var description =
                $"Per diem: {days} {dayWord} {kindLabel} @ " +
                $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
                $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(note))
                description += $" — {note.Trim()}";
            await AddLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.PerDiem, ct);
            return ExpenseMutationResult.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding per-diem line to report {ReportId}", reportId);
            return ExpenseMutationResult.Failure(ex.Message);
        }
    }
```

- [ ] **Step 4: Declare them on the interface**

In `IExpenseReportService.cs`, add (with `using Humans.Domain.Enums;`):

```csharp
    Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default);
```

- [ ] **Step 5: Fix the 4 test constructors (compile blocker first)**

Each test file that does `new ExpenseReportService(...)` must pass the new last argument. Add `using Microsoft.Extensions.Options;` and `using Humans.Application.Services.Expenses.Dtos;` to each, and append the arg after the logger:

```csharp
            NullLogger<ExpenseReportService>.Instance,
            Options.Create(new TravelReimbursementConfig()));
```

Apply in: `ExpenseReportServiceTests.cs`, `ExpenseReportServiceHoldedOutboxTests.cs`, `ExpenseReportServiceHoldedPollingTests.cs`, `ExpenseReportServiceGdprTests.cs`. (`new TravelReimbursementConfig()` uses the 2026 defaults: 0.26 / 26.67 / 53.34.)

- [ ] **Step 6: Write the failing tests**

Add to `ExpenseReportServiceTests.cs`:

```csharp
    [HumansFact]
    public async Task AddMileageLineWithResultAsync_ComputesAmount_FormatsDescription_SetsType()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(userId, category.Id, null);

        var result = await _sut.AddMileageLineWithResultAsync(id, userId, "Berlin", "Barcelona", 1281m);

        result.Succeeded.Should().BeTrue();
        var line = (await _sut.GetAsync(id))!.Lines.Single();
        line.LineType.Should().Be(ExpenseLineType.Mileage);
        line.Amount.Should().Be(333.06m); // 1281 * 0.26
        line.Description.Should().Be("Berlin to Barcelona, 1281 km @ €0.26 = €333.06");
        line.AttachmentId.Should().BeNull();
    }

    [HumansFact]
    public async Task AddPerDiemLineWithResultAsync_Overnight_ComputesAmount_FormatsDescription_SetsType()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(userId, category.Id, null);

        var result = await _sut.AddPerDiemLineWithResultAsync(id, userId, PerDiemKind.Overnight, 3, "Assembly Madrid");

        result.Succeeded.Should().BeTrue();
        var line = (await _sut.GetAsync(id))!.Lines.Single();
        line.LineType.Should().Be(ExpenseLineType.PerDiem);
        line.Amount.Should().Be(160.02m); // 3 * 53.34
        line.Description.Should().Be("Per diem: 3 days overnight @ €53.34 = €160.02 — Assembly Madrid");
        line.AttachmentId.Should().BeNull();
    }

    [HumansFact]
    public async Task AddPerDiemLineWithResultAsync_DayTrip_SingleDay_UsesSingularAndDayTripRate()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(userId, category.Id, null);

        await _sut.AddPerDiemLineWithResultAsync(id, userId, PerDiemKind.DayTrip, 1, null);

        var line = (await _sut.GetAsync(id))!.Lines.Single();
        line.Amount.Should().Be(26.67m);
        line.Description.Should().Be("Per diem: 1 day day-trip @ €26.67 = €26.67");
    }
```

Add `using Humans.Domain.Enums;` to the test file if not present.

- [ ] **Step 7: Run the new tests — verify they pass**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~ExpenseReportServiceTests" -v quiet`
Expected: PASS (all, including the three new tests).

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application tests/Humans.Application.Tests
git commit -m "feat(expenses): mileage + per-diem wizard service methods with server-side computation"
```

---

## Task 6: Submit rule — receipts required only on Receipt lines

**Files:**
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` (`SubmitAsync`)
- Test: `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [HumansFact]
    public async Task SubmitWithResultAsync_Succeeds_WithOnlyMileageLine_NoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        await _sut.AddMileageLineWithResultAsync(id, submitter, "Berlin", "Barcelona", 100m);
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var result = await _sut.SubmitWithResultAsync(id, submitter);

        result.Succeeded.Should().BeTrue();
        (await _sut.GetAsync(id))!.Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    [HumansFact]
    public async Task SubmitWithResultAsync_Fails_WhenReceiptLineHasNoAttachment()
    {
        var (_, category) = SetupActiveYear();
        var submitter = Guid.NewGuid();
        var id = await _sut.CreateDraftAsync(submitter, category.Id, null);
        await _sut.AddLineWithResultAsync(id, submitter, "Tent", 50m); // Receipt line, no attachment
        SetupUserAndProfile(submitter, "Alice Tester", "ES9121000418450200051332");

        var result = await _sut.SubmitWithResultAsync(id, submitter);

        result.Succeeded.Should().BeFalse();
    }
```

- [ ] **Step 2: Run to verify the first test FAILS**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~SubmitWithResultAsync_Succeeds_WithOnlyMileageLine" -v quiet`
Expected: FAIL — current rule rejects any line with a null `AttachmentId`.

- [ ] **Step 3: Change the submit rule**

In `SubmitAsync`, replace the blanket attachment check:

```csharp
        if (report.Lines.Any(l => l.LineType == ExpenseLineType.Receipt && l.AttachmentId is null))
            throw new InvalidOperationException("Receipt lines must have an attachment before submitting.");
```

- [ ] **Step 4: Update the generic failure message**

In `SubmitWithResultAsync`, update the fallback message text:

```csharp
                : ExpenseMutationResult.Failure("Could not submit the report. Receipt lines need an attachment and your payment IBAN must be set.");
```

- [ ] **Step 5: Run both tests — verify PASS**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~SubmitWithResultAsync" -v quiet`
Expected: PASS (including the pre-existing submit tests, which use receipt lines with attachments).

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application tests/Humans.Application.Tests
git commit -m "feat(expenses): require attachments only on Receipt lines at submit"
```

---

## Task 7: Controller endpoints + input models

**Files:**
- Modify: `src/Humans.Web/Models/ExpensesViewModels.cs`
- Modify: `src/Humans.Web/Controllers/ExpensesController.cs`

- [ ] **Step 1: Add input models**

In `ExpensesViewModels.cs`, add `using Humans.Domain.Enums;` and these classes (next to `AddLineInputModel`):

```csharp
public sealed class AddMileageInputModel
{
    [Required, StringLength(200)]
    public string Origin { get; set; } = "";

    [Required, StringLength(200)]
    public string Destination { get; set; } = "";

    [Required, Range(0.1, 100_000)]
    public decimal Km { get; set; }
}

public sealed class AddPerDiemInputModel
{
    [Required]
    public PerDiemKind Kind { get; set; }

    [Required, Range(1, 366)]
    public int Days { get; set; }

    [StringLength(200)]
    public string? Note { get; set; }
}
```

- [ ] **Step 2: Add the two controller actions**

In `ExpensesController.cs`, add after `AddLine` (mirrors its owner-check + redirect pattern exactly):

```csharp
    [HttpPost("{id:guid}/Lines/AddMileage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMileage(Guid id, AddMileageInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid mileage data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await service.AddMileageLineWithResultAsync(
            id, user.Id, input.Origin, input.Destination, input.Km);
        if (!result.Succeeded)
            SetError($"Failed to add mileage line: {result.ErrorMessage}");
        else
            SetSuccess("Mileage line added.");

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/Lines/AddPerDiem")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPerDiem(Guid id, AddPerDiemInputModel input)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var report = await expenseReadService.GetAsync(id);
        if (report is null) return NotFound();
        if (report.SubmitterUserId != user.Id) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Invalid per-diem data.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await service.AddPerDiemLineWithResultAsync(
            id, user.Id, input.Kind, input.Days, input.Note);
        if (!result.Succeeded)
            SetError($"Failed to add per-diem line: {result.ErrorMessage}");
        else
            SetSuccess("Per-diem line added.");

        return RedirectToAction(nameof(Edit), new { id });
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web
git commit -m "feat(expenses): controller endpoints + input models for mileage/per-diem wizards"
```

---

## Task 8: Edit/Detail views — wizards + line-type badges

**Files:**
- Modify: `src/Humans.Web/Views/Expenses/Edit.cshtml`
- Modify: `src/Humans.Web/Views/Expenses/Detail.cshtml`

- [ ] **Step 1: Suppress the receipt UI for travel lines in Edit**

In `Edit.cshtml`, the per-line block currently always renders the attachment area + "No attachment — required before submit" warning. Wrap the attachment area so it only renders for `Receipt` lines, and show a badge for travel lines. Replace the attachment `@if (line.Attachment is not null) { … } else if (Model.CanEditLines) { … } else { … }` block with a leading branch:

```cshtml
                @if (line.LineType != ExpenseLineType.Receipt)
                {
                    <span class="badge bg-info text-dark">
                        <i class="fa-solid fa-route me-1"></i>@(line.LineType == ExpenseLineType.Mileage ? "Mileage" : "Per diem") — no receipt needed
                    </span>
                }
                else if (line.Attachment is not null)
                {
                    <div class="d-flex align-items-center gap-2 flex-wrap">
                        <a asp-action="Attachment" asp-route-attachmentId="@line.Attachment.Id"
                           class="text-decoration-none small" target="_blank">
                            <i class="fa-solid fa-paperclip me-1"></i>@line.Attachment.OriginalFileName
                            <span class="text-muted">(@(line.Attachment.SizeBytes / 1024) KB)</span>
                        </a>
                        @if (Model.CanEditLines)
                        {
                            <form asp-action="RemoveAttachment" asp-route-id="@r.Id" asp-route-lineId="@line.Id"
                                  method="post" class="d-inline">
                                @Html.AntiForgeryToken()
                                <button type="submit" class="btn btn-sm btn-outline-secondary">Remove attachment</button>
                            </form>
                        }
                    </div>
                }
                else if (Model.CanEditLines)
                {
                    <form asp-action="AttachFile" asp-route-id="@r.Id" asp-route-lineId="@line.Id"
                          method="post" enctype="multipart/form-data" class="d-flex gap-2 align-items-center flex-wrap">
                        @Html.AntiForgeryToken()
                        <input type="file" name="file" class="form-control form-control-sm" style="max-width:260px;"
                               accept=".pdf,.jpg,.jpeg,.png,.heic" />
                        <button type="submit" class="btn btn-sm btn-outline-primary">Upload receipt</button>
                    </form>
                    <div class="form-text text-warning mt-1">
                        <i class="fa-solid fa-triangle-exclamation me-1"></i>No attachment — required before submit.
                    </div>
                }
                else
                {
                    <span class="text-warning small"><i class="fa-solid fa-triangle-exclamation me-1"></i>No attachment</span>
                }
```

- [ ] **Step 2: Add the two wizards to the Edit card footer**

In the `@if (Model.CanEditLines)` card-footer, after the existing "Add line" form, add two collapsible mini-forms:

```cshtml
            <details class="mt-3">
                <summary class="text-muted small" style="cursor:pointer;"><i class="fa-solid fa-car me-1"></i>Add mileage</summary>
                <form asp-action="AddMileage" asp-route-id="@r.Id" method="post" class="mt-2">
                    @Html.AntiForgeryToken()
                    <div class="row g-2 align-items-end">
                        <div class="col-md-4">
                            <label class="form-label form-label-sm">From</label>
                            <input type="text" name="Origin" class="form-control form-control-sm" maxlength="200" placeholder="Berlin" required />
                        </div>
                        <div class="col-md-4">
                            <label class="form-label form-label-sm">To</label>
                            <input type="text" name="Destination" class="form-control form-control-sm" maxlength="200" placeholder="Barcelona" required />
                        </div>
                        <div class="col-md-2">
                            <label class="form-label form-label-sm">Km</label>
                            <input type="number" name="Km" class="form-control form-control-sm" step="0.1" min="0.1" placeholder="0" required />
                        </div>
                        <div class="col-md-2">
                            <button type="submit" class="btn btn-sm btn-outline-primary w-100">Add</button>
                        </div>
                    </div>
                    <div class="form-text">Amount is computed at the current per-km rate. No receipt needed.</div>
                </form>
            </details>

            <details class="mt-2">
                <summary class="text-muted small" style="cursor:pointer;"><i class="fa-solid fa-utensils me-1"></i>Add per diem</summary>
                <form asp-action="AddPerDiem" asp-route-id="@r.Id" method="post" class="mt-2">
                    @Html.AntiForgeryToken()
                    <div class="row g-2 align-items-end">
                        <div class="col-md-4">
                            <label class="form-label form-label-sm">Type</label>
                            <select name="Kind" class="form-select form-select-sm" required>
                                <option value="@((int)PerDiemKind.DayTrip)">Day trip</option>
                                <option value="@((int)PerDiemKind.Overnight)">Overnight</option>
                            </select>
                        </div>
                        <div class="col-md-2">
                            <label class="form-label form-label-sm">Days</label>
                            <input type="number" name="Days" class="form-control form-control-sm" step="1" min="1" value="1" required />
                        </div>
                        <div class="col-md-4">
                            <label class="form-label form-label-sm">Note <small class="text-muted">(optional)</small></label>
                            <input type="text" name="Note" class="form-control form-control-sm" maxlength="200" placeholder="Assembly in Madrid" />
                        </div>
                        <div class="col-md-2">
                            <button type="submit" class="btn btn-sm btn-outline-primary w-100">Add</button>
                        </div>
                    </div>
                    <div class="form-text">Amount is computed at the Spanish tax-exempt daily rate. No receipt needed.</div>
                </form>
            </details>
```

Add `@using Humans.Domain.Enums` at the top of `Edit.cshtml` (after the `@model` line) so `PerDiemKind` / `ExpenseLineType` resolve.

- [ ] **Step 3: Detail view — don't warn "No attachment" for travel lines**

In `Detail.cshtml`, add `@using Humans.Domain.Enums` after the `@model` line, and change the attachment cell so travel lines show a badge instead of the warning:

```cshtml
                                    <td>
                                        @if (line.LineType != ExpenseLineType.Receipt)
                                        {
                                            <span class="badge bg-info text-dark">@(line.LineType == ExpenseLineType.Mileage ? "Mileage" : "Per diem")</span>
                                        }
                                        else if (line.Attachment is not null)
                                        {
                                            <a asp-action="Attachment" asp-route-attachmentId="@line.Attachment.Id"
                                               class="text-decoration-none" target="_blank">
                                                <i class="fa-solid fa-paperclip me-1"></i>@line.Attachment.OriginalFileName
                                            </a>
                                        }
                                        else
                                        {
                                            <span class="text-warning small"><i class="fa-solid fa-triangle-exclamation me-1"></i>No attachment</span>
                                        }
                                    </td>
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (Razor compiles).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web
git commit -m "feat(expenses): mileage/per-diem wizard UI + travel-line badges (Edit/Detail)"
```

---

## Task 9: Finance — surface individual payment rows

**Files:**
- Create: `src/Humans.Application/Services/Finance/Dtos/HoldedPaymentInfo.cs`
- Modify: `src/Humans.Application/Services/Finance/Dtos/HoldedCreditorStatus.cs`
- Modify: `src/Humans.Application/Services/Finance/HoldedFinanceService.cs`
- Test: `tests/Humans.Application.Tests/Finance/HoldedFinanceServiceTests.cs`

- [ ] **Step 1: Create `HoldedPaymentInfo`**

```csharp
using NodaTime;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>One Holded payment row exposed to read consumers (per-member ledger).</summary>
public sealed record HoldedPaymentInfo(LocalDate Date, decimal Amount, string? DocumentType);
```

- [ ] **Step 2: Add `Payments` to `HoldedCreditorStatus` (default null = non-breaking)**

```csharp
using System.Collections.Generic;
using NodaTime;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>Cached creditor status for one member, sourced from Holded.</summary>
public sealed record HoldedCreditorStatus(
    int? SupplierAccountNum,
    decimal? Balance,           // signed; negative = org owes the member. NULL = no cached balance row (unknown — NOT settled).
    decimal OwedToMember,       // = max(0, -Balance), or 0 when Balance is unknown
    LocalDate? LastPaymentDate,
    decimal TotalPaid,
    IReadOnlyList<HoldedPaymentInfo>? Payments = null);  // individual rows for the per-member ledger
```

(The `= null` default keeps the existing positional/named constructions in tests compiling unchanged.)

- [ ] **Step 3: Write the failing test**

Add to `HoldedFinanceServiceTests.cs`:

```csharp
    [HumansFact]
    public async Task GetCreditorStatus_surfaces_individual_payment_rows()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(40000001, default).ReturnsForAnyArgs(
            new HoldedCreditorBalance { SupplierAccountNum = 40000001, Balance = -100m });
        _repo.GetPaymentsByContactAsync("c1", default).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 100m, Date = new LocalDate(2026, 4, 1), DocumentType = "purchase" },
            new() { HoldedPaymentId = "p2", HoldedContactId = "c1", Amount = 50m,  Date = new LocalDate(2026, 4, 20) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000001, "c1");

        status!.Payments.Should().NotBeNull();
        status.Payments!.Should().HaveCount(2);
        status.Payments!.Should().ContainEquivalentOf(new HoldedPaymentInfo(new LocalDate(2026, 4, 1), 100m, "purchase"));
    }
```

(Add `using Humans.Application.Services.Finance.Dtos;` if not present.)

- [ ] **Step 4: Run to verify it FAILS**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~GetCreditorStatus_surfaces_individual_payment_rows" -v quiet`
Expected: FAIL — `Payments` is null.

- [ ] **Step 5: Map the rows in `GetCreditorStatusAsync`**

In `HoldedFinanceService.cs`, extend the returned record (the `payments` array is already fetched above the return):

```csharp
        return new HoldedCreditorStatus(
            SupplierAccountNum: balanceRow?.SupplierAccountNum ?? supplierAccountNum,
            Balance: balance,
            OwedToMember: balance is { } b ? Math.Max(0m, -b) : 0m,
            LastPaymentDate: lastPaymentDate,
            TotalPaid: payments.Sum(p => p.Amount),
            Payments: payments.Select(p => new HoldedPaymentInfo(p.Date, p.Amount, p.DocumentType)).ToList());
```

- [ ] **Step 6: Run — verify PASS (and existing Finance tests still pass)**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~HoldedFinanceServiceTests" -v quiet`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application tests/Humans.Application.Tests
git commit -m "feat(finance): expose individual Holded payment rows on HoldedCreditorStatus"
```

---

## Task 10: Carry payments through the expense timeline

**Files:**
- Modify: `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs` (`ExpenseHoldedTimeline` record)
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` (`GetHoldedTimelineAsync`)
- Test: `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

- [ ] **Step 1: Add `Payments` to `ExpenseHoldedTimeline`**

In `IExpenseReportService.cs`, extend the record (add `using Humans.Application.Services.Finance.Dtos;` to the file):

```csharp
public sealed record ExpenseHoldedTimeline(
    bool RegisteredInHolded,
    decimal OwedToMember,
    decimal MemberRegisteredTotal,
    decimal OtherAmount,
    bool Paid,
    NodaTime.LocalDate? PaidOn,
    decimal TotalPaid,
    IReadOnlyList<HoldedPaymentInfo> Payments);
```

- [ ] **Step 2: Map payments in `GetHoldedTimelineAsync` (both return sites)**

In `ExpenseReportService.cs`, the early "no contact" return and the main return must both supply `Payments`. Early return:

```csharp
        if (string.IsNullOrEmpty(report.HoldedContactId))
            return new ExpenseHoldedTimeline(
                RegisteredInHolded: false, OwedToMember: 0m, MemberRegisteredTotal: 0m,
                OtherAmount: 0m, Paid: false, PaidOn: null, TotalPaid: 0m, Payments: []);
```

Main return (append `Payments`, normalizing the nullable source to empty):

```csharp
        return new ExpenseHoldedTimeline(
            RegisteredInHolded: report.HoldedDocId is not null,
            OwedToMember: owed,
            MemberRegisteredTotal: memberRegisteredTotal,
            OtherAmount: Math.Max(0m, owed - memberRegisteredTotal),
            Paid: paid,
            PaidOn: status?.LastPaymentDate,
            TotalPaid: totalPaid,
            Payments: status?.Payments ?? []);
```

- [ ] **Step 3: Write the failing test**

Add to `ExpenseReportServiceTests.cs` (mirror the existing timeline tests around line 1205 — seed an approved report with a contact, stub the finance service). Concretely:

```csharp
    [HumansFact]
    public async Task GetHoldedTimelineAsync_CarriesPaymentRows()
    {
        var (_, category) = SetupActiveYear();
        var userId = Guid.NewGuid();
        SetupUserAndProfile(userId, "Alice Tester", "ES9121000418450200051332");
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id);

        // Give the report a Holded contact so the timeline resolves a status.
        var report = await _sut.GetAsync(reportId);
        report = report! with { HoldedContactId = "c1", HoldedSupplierAccountNum = 40000007 };

        _holdedFinance.GetCreditorStatusAsync(40000007, "c1", Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: -50m, OwedToMember: 50m,
                LastPaymentDate: new LocalDate(2026, 4, 20), TotalPaid: 50m,
                Payments: new List<HoldedPaymentInfo> { new(new LocalDate(2026, 4, 20), 50m, "purchase") }));

        var timeline = await _sut.GetHoldedTimelineAsync(report);

        timeline!.Payments.Should().ContainSingle()
            .Which.Amount.Should().Be(50m);
    }
```

> If `SeedApprovedReportWithAttachmentAsync` does not persist `HoldedContactId`, instead build an `ExpenseReportDto` for the timeline argument with `HoldedContactId = "c1"`, `HoldedSupplierAccountNum = 40000007`, `HoldedDocId = "doc-1"`, and `Lines = []` — `GetHoldedTimelineAsync` takes the DTO as an argument and only reads those fields plus `SubmitterUserId`. Use whichever the existing nearby timeline tests already do.

(Ensure `using Humans.Application.Services.Finance.Dtos;` is present in the test file.)

- [ ] **Step 4: Run — verify PASS**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~GetHoldedTimelineAsync" -v quiet`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application tests/Humans.Application.Tests
git commit -m "feat(expenses): carry Holded payment rows through ExpenseHoldedTimeline"
```

---

## Task 11: Index view model + controller summary/ledger

**Files:**
- Modify: `src/Humans.Web/Models/ExpensesViewModels.cs`
- Modify: `src/Humans.Web/Controllers/ExpensesController.cs` (`Index`)

- [ ] **Step 1: Add the IOU view models**

In `ExpensesViewModels.cs`, add `using NodaTime;` and `using Humans.Application.Services.Finance.Dtos;`, then:

```csharp
public sealed class ExpenseIouSummary
{
    public required decimal OwedToMember { get; init; }
    public required decimal TotalPaid { get; init; }
    public required decimal OtherAmount { get; init; }
    public LocalDate? LastPaymentDate { get; init; }
}

/// <summary>One row in the combined reports-and-payments ledger, sorted by <see cref="Date"/> desc.</summary>
public sealed record ExpenseLedgerRow(
    LocalDate Date,
    bool IsPayment,
    string Label,
    decimal Amount,
    Guid? ReportId,
    ExpenseReportStatus? Status);
```

Extend `ExpensesIndexViewModel` with two nullable/empty members:

```csharp
public sealed class ExpensesIndexViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public bool HasActiveYear { get; init; }
    public bool HasIban { get; init; }
    public IReadOnlyDictionary<Guid, string> CategoryNames { get; init; } =
        new Dictionary<Guid, string>();

    /// <summary>Non-null when the member has a Holded creditor account with activity.</summary>
    public ExpenseIouSummary? Iou { get; init; }
    public IReadOnlyList<ExpenseLedgerRow> Ledger { get; init; } = [];
}
```

- [ ] **Step 2: Build the summary + ledger in `Index`**

In `ExpensesController.Index`, **after the `categoryNames` dictionary is built** (it's referenced below) and before constructing `var model = new ExpensesIndexViewModel`, add the IOU/ledger composition. Add `using NodaTime;` to the controller if not present.

```csharp
            ExpenseIouSummary? iou = null;
            var ledger = new List<ExpenseLedgerRow>();

            var pushedReport = reports
                .Where(rep => !string.IsNullOrEmpty(rep.HoldedContactId))
                .OrderByDescending(rep => rep.CreatedAt)
                .FirstOrDefault();

            if (pushedReport is not null)
            {
                var tl = await expenseReadService.GetHoldedTimelineAsync(pushedReport);
                if (tl is not null && (tl.OwedToMember > 0 || tl.TotalPaid > 0 || tl.Payments.Count > 0))
                {
                    iou = new ExpenseIouSummary
                    {
                        OwedToMember = tl.OwedToMember,
                        TotalPaid = tl.TotalPaid,
                        OtherAmount = tl.OtherAmount,
                        LastPaymentDate = tl.PaidOn
                    };

                    // Org's reporting zone (Spain). Reports are claims once submitted; drafts/withdrawn are excluded.
                    var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
                    ledger.AddRange(reports
                        .Where(rep => rep.Status is not ExpenseReportStatus.Draft and not ExpenseReportStatus.Withdrawn)
                        .Select(rep => new ExpenseLedgerRow(
                            Date: (rep.SubmittedAt ?? rep.CreatedAt).InZone(zone).Date,
                            IsPayment: false,
                            Label: categoryNames.TryGetValue(rep.BudgetCategoryId, out var cat) ? cat : "Expense report",
                            Amount: rep.Total,
                            ReportId: rep.Id,
                            Status: rep.Status)));
                    ledger.AddRange(tl.Payments
                        .Select(p => new ExpenseLedgerRow(
                            Date: p.Date,
                            IsPayment: true,
                            Label: "Payment received",
                            Amount: p.Amount,
                            ReportId: null,
                            Status: null)));
                    ledger = ledger.OrderByDescending(row => row.Date).ThenByDescending(row => row.IsPayment).ToList();
                }
            }
```

Then include them in the returned model:

```csharp
            var model = new ExpensesIndexViewModel
            {
                Reports = reports,
                HasActiveYear = activeYear is not null,
                HasIban = !string.IsNullOrEmpty(info?.Profile?.Iban),
                CategoryNames = categoryNames,
                Iou = iou,
                Ledger = ledger,
            };
            return View(model);
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web
git commit -m "feat(expenses): build IOU summary + reports/payments ledger on the dashboard"
```

---

## Task 12: Index view — IOU card + ledger

**Files:**
- Modify: `src/Humans.Web/Views/Expenses/Index.cshtml`

- [ ] **Step 1: Render the IOU card + ledger above the existing reports table**

In `Index.cshtml`, add `@using Humans.Domain.Enums` after the `@model` line, and insert this block right after the `<vc:temp-data-alerts />` line:

```cshtml
@if (Model.Iou is { } iou)
{
    <div class="card mb-4 border-info">
        <div class="card-header bg-info-subtle"><strong><i class="fa-solid fa-building-columns me-1"></i>What the collective owes you</strong></div>
        <div class="card-body">
            <div class="row text-center">
                <div class="col-md-4">
                    <div class="text-muted small">Owed to you (Holded balance)</div>
                    <div class="fs-4 fw-semibold">&euro;@iou.OwedToMember.ToString("N2")</div>
                </div>
                <div class="col-md-4">
                    <div class="text-muted small">Paid to date</div>
                    <div class="fs-4">&euro;@iou.TotalPaid.ToString("N2")</div>
                </div>
                <div class="col-md-4">
                    <div class="text-muted small">Last payment</div>
                    <div class="fs-4">@(iou.LastPaymentDate is { } d ? d.ToInvariantDate() : "—")</div>
                </div>
            </div>
            @if (iou.OtherAmount > 0)
            {
                <p class="text-muted small mb-0 mt-3">
                    <i class="fa-solid fa-circle-info me-1"></i>
                    Your Holded balance includes &euro;@iou.OtherAmount.ToString("N2") unrelated to these reports
                    (items fronted on the collective's behalf, adjustments, etc.), so the rows below won't necessarily sum to it.
                </p>
            }
        </div>
        @if (Model.Ledger.Count > 0)
        {
            <div class="table-responsive">
                <table class="table table-sm mb-0">
                    <thead>
                        <tr>
                            <th>Date</th>
                            <th>Item</th>
                            <th class="text-end">Amount</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        @foreach (var row in Model.Ledger)
                        {
                            <tr class="@(row.IsPayment ? "table-success" : "")">
                                <td>@row.Date.ToInvariantDate()</td>
                                <td>
                                    @if (row.IsPayment)
                                    {
                                        <i class="fa-solid fa-money-bill-wave text-success me-1"></i>
                                    }
                                    @row.Label
                                    @if (row.Status is { } st)
                                    {
                                        <span class="badge bg-secondary ms-1">@st</span>
                                    }
                                </td>
                                <td class="text-end">@(row.IsPayment ? "−" : "")&euro;@row.Amount.ToString("N2")</td>
                                <td class="text-end">
                                    @if (row.ReportId is { } rid)
                                    {
                                        <a asp-action="Detail" asp-route-id="@rid" class="btn btn-sm btn-outline-secondary">View</a>
                                    }
                                </td>
                            </tr>
                        }
                    </tbody>
                </table>
            </div>
        }
    </div>
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Manual verification (run the app)**

Run: `dotnet run --project src/Humans.Web` and sign in (dev login). Then:
1. Create a draft report, use **Add mileage** (Berlin → Barcelona, 1281) → a line `Berlin to Barcelona, 1281 km @ €0.26 = €333.06` appears with a "Mileage — no receipt needed" badge and no upload prompt.
2. Use **Add per diem** (Overnight, 3 days, note) → `Per diem: 3 days overnight @ €53.34 = €160.02 — …`.
3. Set IBAN, **Submit** with no attachments → succeeds (pure travel report).
4. The `/Expenses` dashboard shows the IOU card + ledger only for a member with Holded creditor activity (otherwise it's absent — expected for a fresh dev DB; verify it's absent, not erroring).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web
git commit -m "feat(expenses): IOU summary card + reports/payments ledger on /Expenses index"
```

---

## Task 13: Docs + full verification + push

**Files:**
- Modify: `docs/sections/Expenses.md`, `docs/sections/Finance.md`

- [ ] **Step 1: Update `docs/sections/Expenses.md`**

- Add `LineType` to the `ExpenseLine` data-model table (values: Receipt / Mileage / PerDiem; default Receipt).
- Add a **Concepts** bullet for travel lines (mileage = km × configured rate; per-diem = days × Spanish day-trip/overnight rate; computed server-side; no attachment).
- Update the submit **Invariant**: "Every **Receipt** line must have an attachment at submit time; Mileage/PerDiem lines never require one. A report still needs ≥1 line."
- Add a routing row for `POST /Expenses/{id}/Lines/AddMileage` and `/Lines/AddPerDiem`.
- Note the `/Expenses` dashboard now shows the member's Holded IOU summary + reports/payments ledger (reuses `GetHoldedTimelineAsync`).
- Add the new trigger paths to the top `freshness:triggers` block: `src/Humans.Domain/Enums/ExpenseLineType.cs`, `src/Humans.Application/Services/Expenses/Dtos/TravelReimbursementConfig.cs`.

- [ ] **Step 2: Update `docs/sections/Finance.md`**

In the Feature 2 section, note that `HoldedCreditorStatus` now also exposes individual `Payments` rows (read-only) consumed by the Expenses dashboard ledger; no new interface method.

- [ ] **Step 3: Full build + test**

Run: `dotnet build Humans.slnx -v quiet`
Run: `dotnet test Humans.slnx -v quiet`
Expected: build + all tests PASS.

- [ ] **Step 4: Commit + push**

```bash
git add docs/sections
git commit -m "docs(expenses,finance): travel lines, receipt rule, IOU dashboard, payment rows"
git push
```

---

## Notes for the implementer

- **Reuse stance:** no new repository method, no new `*ServiceRead` interface method, no new cross-section dependency. `IExpenseReportServiceRead`'s `[SurfaceBudget(7)]` must stay 7 — do not add methods to it. The mutation interface `IExpenseReportService` has no budget; the two wizard methods go there.
- **Rates** live only in `TravelReimbursementConfig`; never hard-code 0.26/26.67/53.34 outside it (the description string captures the rate at creation time, which is the historical record).
- **Never hand-edit the migration** — regenerate from the model if the default is wrong.
- **No data backfill** — the `LineType` column default (`Receipt`) is a schema default; existing lines were all receipts.
- IBAN masking, auth handlers, and the Holded outbox/polling jobs are untouched.
```
