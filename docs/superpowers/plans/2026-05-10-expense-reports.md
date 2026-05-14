# Expense Reports Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a new `/Expenses` section that lets members file expense reports against budget categories, route them through optional coordinator endorsement and FinanceAdmin approval, push approved reports to Holded, and let the treasurer pay out via downloaded SEPA pain.001 XML — with `Paid` confirmed automatically by polling Holded.

**Architecture:** Two new sections shipped together. (1) **Holded** — a thin sibling section owning the typed `HttpClient` to the Holded API (`IHoldedClient` with 4 methods). (2) **Expenses** — full section with entities, repository, services, controllers, views, authorization, two Hangfire jobs (outbox push + paid polling), SEPA file generation, and `Profile.Iban` lazy-surfacing. Outbox pattern decouples approval from Holded availability; polling job decouples paid confirmation from any future Finance/Holded sync.

**Tech Stack:** .NET 10 (`Humans.slnx`), EF Core + PostgreSQL (`Npgsql`), NodaTime, ASP.NET Core MVC + Razor, Hangfire (PostgreSQL storage), xUnit v3 with `[HumansFact]`/`[HumansTheory]`, `AwesomeAssertions`, `NSubstitute`, `NodaTime.Testing`. Resource-based authorization via `IAuthorizationHandler` + custom requirement classes.

**Spec:** [`docs/superpowers/specs/2026-05-10-expense-reports-design.md`](../specs/2026-05-10-expense-reports-design.md)

---

## File Map

### Files created (new)

**Domain (`src/Humans.Domain/`):**
- `Entities/ExpenseReport.cs`
- `Entities/ExpenseLine.cs`
- `Entities/ExpenseAttachment.cs`
- `Entities/HoldedExpenseOutboxEvent.cs`
- `Enums/ExpenseReportStatus.cs`
- `Enums/HoldedExpenseOutboxEventType.cs`
- `Helpers/IbanFormatter.cs`
- `Helpers/IbanValidator.cs`

**Application (`src/Humans.Application/`):**
- `Interfaces/Holded/IHoldedClient.cs`
- `Interfaces/Holded/HoldedApiException.cs`
- `Interfaces/Holded/HoldedPurchaseDocumentDto.cs`
- `Interfaces/Expenses/IExpenseReportService.cs`
- `Interfaces/Expenses/IExpenseAttachmentStorageService.cs`
- `Interfaces/Expenses/ISepaPaymentFileBuilder.cs`
- `Interfaces/Repositories/IExpenseRepository.cs`
- `Services/Expenses/ExpenseReportService.cs`
- `Services/Expenses/SepaPaymentFileBuilder.cs`
- `Services/Expenses/Dtos/ExpenseReportDto.cs`
- `Services/Expenses/Dtos/ExpenseLineDto.cs`
- `Services/Expenses/Dtos/ExpenseAttachmentDto.cs`
- `Services/Expenses/Dtos/SepaConfig.cs`

**Infrastructure (`src/Humans.Infrastructure/`):**
- `Services/Holded/HoldedClient.cs`
- `Services/Holded/HoldedClientOptions.cs`
- `Services/Expenses/ExpenseAttachmentFilesystemStorage.cs`
- `Services/Expenses/ExpenseAttachmentFilesystemStorageOptions.cs`
- `Repositories/Expenses/ExpenseRepository.cs`
- `Jobs/HoldedExpenseOutboxJob.cs`
- `Jobs/ExpensePaidPollingJob.cs`
- `Data/Configurations/Expenses/ExpenseReportConfiguration.cs`
- `Data/Configurations/Expenses/ExpenseLineConfiguration.cs`
- `Data/Configurations/Expenses/ExpenseAttachmentConfiguration.cs`
- `Data/Configurations/Expenses/HoldedExpenseOutboxEventConfiguration.cs`
- `Migrations/<timestamp>_AddProfileIban.cs` + `.Designer.cs`
- `Migrations/<timestamp>_AddExpensesSection.cs` + `.Designer.cs`

**Web (`src/Humans.Web/`):**
- `Controllers/ExpensesController.cs`
- `Models/ExpensesViewModels.cs`
- `Views/Expenses/Index.cshtml`
- `Views/Expenses/New.cshtml`
- `Views/Expenses/Detail.cshtml`
- `Views/Expenses/Edit.cshtml`
- `Views/Expenses/Iban.cshtml`
- `Views/Expenses/Coordinator.cshtml`
- `Views/Expenses/Review.cshtml`
- `Authorization/Requirements/ExpenseReportOperationRequirement.cs`
- `Authorization/Requirements/ExpenseReportAuthorizationHandler.cs`
- `Authorization/Requirements/IbanAccessRequirement.cs`
- `Authorization/Requirements/IbanAccessHandler.cs`
- `Extensions/Sections/ExpensesSectionExtensions.cs`
- `Extensions/Sections/HoldedSectionExtensions.cs`

**Tests:**
- `tests/Humans.Domain.Tests/Helpers/IbanFormatterTests.cs`
- `tests/Humans.Domain.Tests/Helpers/IbanValidatorTests.cs`
- `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`
- `tests/Humans.Application.Tests/Services/Expenses/SepaPaymentFileBuilderTests.cs`
- `tests/Humans.Application.Tests/Architecture/ExpensesArchitectureTests.cs`
- `tests/Humans.Application.Tests/Architecture/HoldedArchitectureTests.cs`
- `tests/Humans.Infrastructure.Tests/Services/Holded/HoldedClientTests.cs`
- `tests/Humans.Infrastructure.Tests/Services/Expenses/ExpenseAttachmentFilesystemStorageTests.cs`
- `tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs`

**Docs:**
- `docs/sections/Expenses.md`
- `docs/sections/Holded.md`

### Files modified

- `src/Humans.Domain/Entities/Profile.cs` — add `Iban` property
- `src/Humans.Infrastructure/Data/Configurations/Profiles/ProfileConfiguration.cs` — add `Iban` config
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add `DbSet<>` properties for the 4 Expenses entities
- `src/Humans.Web/Authorization/PolicyNames.cs` — verify existing `FinanceAdminOrAdmin` policy is present (no change expected)
- `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs` — register `ExpenseReportAuthorizationHandler` and `IbanAccessHandler`
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — call `services.AddHoldedSection()` and `services.AddExpensesSection()`
- `src/Humans.Web/Extensions/RecurringJobExtensions.cs` — register `HoldedExpenseOutboxJob` and `ExpensePaidPollingJob`
- `src/Humans.Domain/Enums/AuditAction.cs` — add new actions if needed (`ExpenseSubmit`, `ExpenseEndorse`, `ExpenseReject`, `ExpenseApprove`, `ExpenseWithdraw`, `ExpenseSepaSent`, `ExpensePaid`, `IbanReveal`)

---

## Cross-cutting conventions

Every task in this plan follows these conventions. Don't restate them per-task.

- **Always quiet:** every `dotnet build` / `dotnet test` runs with `-v quiet`. Required by `memory/process/dotnet-verbosity-quiet.md`.
- **No `cd` chaining:** never combine `cd <dir> && <cmd>` in a single Bash call. The agent harness blocks it.
- **Build then test:** after any code change, run `dotnet build Humans.slnx -v quiet` first; if it succeeds, run scoped tests. Don't run the full test suite per task — too slow. Run the specific test class with `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseReportServiceTests"`.
- **NodaTime types:** all timestamps are `Instant`; never `DateTime` or `DateTimeOffset` in domain/application code.
- **No EF in Application:** services live in `Humans.Application` and never `using Microsoft.EntityFrameworkCore`. The architecture test (Phase 4 / Task 4.10) enforces this.
- **Audit log AFTER save:** call `IAuditLogService.LogAsync` only after the business `SaveChangesAsync` succeeds. A rollback must never leave a ghost audit row.
- **Repository = Singleton + `IDbContextFactory`:** every repository public method opens its own short-lived `await using var ctx = await _factory.CreateDbContextAsync(ct);`. No shared DbContext across calls.
- **String enums:** all new enums get `HasConversion<string>()` in their EF config (matches `ProfileConfiguration.MembershipTier`).
- **Tests use the `[HumansFact]` / `[HumansTheory]` attributes** — defined in `tests/Humans.Tests.Common`. Never raw `[Fact]` / `[Theory]` for new tests.
- **Assertions use `AwesomeAssertions`** — `result.Should().Be(...)`. Not FluentAssertions, not native xUnit `Assert.*`.
- **Mocks use NSubstitute** — `Substitute.For<ITeamService>()`. Not Moq.
- **Migration discipline:** `memory/process/ef-migration-review-gate.md` — run the EF migration reviewer agent on every commit that touches `Migrations/`. Never hand-edit a migration or `HumansDbContextModelSnapshot.cs`.
- **Frequent commits:** each task ends with a commit. If a task has multiple sub-steps, commit at the end. Never amend; always create new commits.
- **Branch:** all work happens on `expense-reports-design` (current branch). Each task's commit has a `feat:`, `test:`, `chore:`, or `docs:` subject line.
- **`-Peter` sign-off** is for in-app feedback replies, not for git commits. Git commits use the `Co-Authored-By: Claude Opus 4.7 (1M context)` footer.

---

# Phase 1 — IBAN foundation

Adds `Iban` to `Profile`, the `IbanFormatter` mask helper, and the `IbanValidator` structural-checksum validator. This is the smallest independent foundation; everything else depends on these existing.

**PR boundary:** end of Phase 1 is a clean PR target.

### Task 1.1: Add `IbanFormatter` with masking

**Files:**
- Create: `src/Humans.Domain/Helpers/IbanFormatter.cs`
- Test: `tests/Humans.Domain.Tests/Helpers/IbanFormatterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Humans.Domain.Tests/Helpers/IbanFormatterTests.cs
using AwesomeAssertions;
using Humans.Domain.Helpers;
using Humans.Tests.Common;

namespace Humans.Domain.Tests.Helpers;

public class IbanFormatterTests
{
    [HumansFact]
    public void Mask_ReturnsFirst4PlusStarsPlusLast3()
    {
        IbanFormatter.Mask("NL75ABNA0123456789").Should().Be("NL75****789");
    }

    [HumansFact]
    public void Mask_HandlesShortIban()
    {
        IbanFormatter.Mask("ES1234567890").Should().Be("ES12****890");
    }

    [HumansFact]
    public void Mask_StripsSpacesBeforeMasking()
    {
        IbanFormatter.Mask("NL75 ABNA 0123 4567 89").Should().Be("NL75****789");
    }

    [HumansFact]
    public void Mask_NullReturnsEmpty()
    {
        IbanFormatter.Mask(null).Should().Be("");
    }

    [HumansFact]
    public void Mask_EmptyReturnsEmpty()
    {
        IbanFormatter.Mask("").Should().Be("");
    }

    [HumansFact]
    public void Mask_TooShortToMaskReturnsAllStars()
    {
        // 6 chars or fewer can't show 4+3 without overlap; mask entirely.
        IbanFormatter.Mask("NL75AB").Should().Be("****");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IbanFormatterTests"
```

Expected: FAIL with "type or namespace 'IbanFormatter' could not be found".

- [ ] **Step 3: Implement `IbanFormatter`**

```csharp
// src/Humans.Domain/Helpers/IbanFormatter.cs
namespace Humans.Domain.Helpers;

/// <summary>
/// Centralized IBAN masking. All log/audit/error output that
/// references an IBAN MUST go through Mask. Raw IBANs only appear
/// in pain.001 SEPA XML and in the Holded API request body.
/// </summary>
public static class IbanFormatter
{
    public static string Mask(string? iban)
    {
        if (string.IsNullOrEmpty(iban)) return "";
        var compact = iban.Replace(" ", "").Replace(" ", "");
        if (compact.Length <= 7) return "****";
        return $"{compact[..4]}****{compact[^3..]}";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IbanFormatterTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```
git add src/Humans.Domain/Helpers/IbanFormatter.cs tests/Humans.Domain.Tests/Helpers/IbanFormatterTests.cs
git commit -m "feat(domain): add IbanFormatter.Mask for safe IBAN logging

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 1.2: Add `IbanValidator` with structural + mod-97 checks

**Files:**
- Create: `src/Humans.Domain/Helpers/IbanValidator.cs`
- Test: `tests/Humans.Domain.Tests/Helpers/IbanValidatorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Humans.Domain.Tests/Helpers/IbanValidatorTests.cs
using AwesomeAssertions;
using Humans.Domain.Helpers;
using Humans.Tests.Common;

namespace Humans.Domain.Tests.Helpers;

public class IbanValidatorTests
{
    [HumansTheory]
    [InlineData("ES9121000418450200051332")] // ES, 24 chars, valid
    [InlineData("DE89370400440532013000")]   // DE, 22 chars, valid
    [InlineData("NL91ABNA0417164300")]       // NL, 18 chars, valid
    [InlineData("FR1420041010050500013M02606")] // FR, 27 chars, valid
    public void IsValid_AcceptsRealIbans(string iban)
    {
        IbanValidator.IsValid(iban).Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("ES9121000418450200051333")] // bad checksum
    [InlineData("ES912100041845")]           // too short for ES
    [InlineData("XX9121000418450200051332")] // unknown country
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ES91 2100 0418 45")]        // valid country, but truncated
    public void IsValid_RejectsBadInputs(string? iban)
    {
        IbanValidator.IsValid(iban).Should().BeFalse();
    }

    [HumansFact]
    public void IsValid_StripsSpacesBeforeChecking()
    {
        IbanValidator.IsValid("ES91 2100 0418 4502 0005 1332").Should().BeTrue();
    }

    [HumansFact]
    public void Normalize_ReturnsUppercaseNoSpaces()
    {
        IbanValidator.Normalize("es91 2100 0418 4502 0005 1332")
            .Should().Be("ES9121000418450200051332");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IbanValidatorTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement `IbanValidator`**

```csharp
// src/Humans.Domain/Helpers/IbanValidator.cs
using System.Globalization;
using System.Numerics;

namespace Humans.Domain.Helpers;

public static class IbanValidator
{
    // ISO-13616 official lengths per country code.
    // Source: SWIFT IBAN Registry. Add countries as they come up; keep narrow.
    private static readonly Dictionary<string, int> Lengths = new()
    {
        ["AD"] = 24, ["AE"] = 23, ["AT"] = 20, ["BE"] = 16, ["BG"] = 22,
        ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18,
        ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22,
        ["GR"] = 27, ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IS"] = 26,
        ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21,
        ["MC"] = 27, ["MT"] = 31, ["NL"] = 18, ["NO"] = 15, ["PL"] = 28,
        ["PT"] = 25, ["RO"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24,
        ["SM"] = 27,
    };

    public static string Normalize(string? iban) =>
        (iban ?? "").Replace(" ", "").Replace(" ", "").ToUpperInvariant();

    public static bool IsValid(string? iban)
    {
        var v = Normalize(iban);
        if (v.Length < 4) return false;
        var country = v[..2];
        if (!Lengths.TryGetValue(country, out var expectedLen)) return false;
        if (v.Length != expectedLen) return false;
        if (!v.All(c => char.IsLetterOrDigit(c))) return false;

        // Move first 4 chars to end, expand letters to digits, mod 97 == 1.
        var rearranged = v[4..] + v[..4];
        var sb = new System.Text.StringBuilder(rearranged.Length * 2);
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c)) sb.Append(c);
            else sb.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
        }
        var big = BigInteger.Parse(sb.ToString(), CultureInfo.InvariantCulture);
        return big % 97 == 1;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IbanValidatorTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```
git add src/Humans.Domain/Helpers/IbanValidator.cs tests/Humans.Domain.Tests/Helpers/IbanValidatorTests.cs
git commit -m "feat(domain): add IbanValidator with mod-97 checksum

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 1.3: Add `Profile.Iban` field + EF config

**Files:**
- Modify: `src/Humans.Domain/Entities/Profile.cs` (add property)
- Modify: `src/Humans.Infrastructure/Data/Configurations/Profiles/ProfileConfiguration.cs` (add config)

- [ ] **Step 1: Read the current `Profile.cs` to find the right insertion point**

```
Read src/Humans.Domain/Entities/Profile.cs
```

- [ ] **Step 2: Add `Iban` property to `Profile`**

After the existing PII fields (e.g., near `LegalName`, `PhoneNumber`, etc.), add:

```csharp
[PersonalData]
public string? Iban { get; set; }
```

`[PersonalData]` is critical — it marks the field for GDPR export per the existing pattern on other PII fields. Don't omit it.

- [ ] **Step 3: Add `Iban` to `ProfileConfiguration`**

In `src/Humans.Infrastructure/Data/Configurations/Profiles/ProfileConfiguration.cs`, add inside `Configure(EntityTypeBuilder<Profile> builder)`:

```csharp
builder.Property(p => p.Iban)
    .HasMaxLength(34); // SWIFT IBAN max length per ISO-13616
```

No `IsRequired()` — it's nullable.

- [ ] **Step 4: Build to verify compilation**

```
dotnet build Humans.slnx -v quiet
```

Expected: success.

- [ ] **Step 5: Commit (no migration yet — generated in next task)**

```
git add src/Humans.Domain/Entities/Profile.cs src/Humans.Infrastructure/Data/Configurations/Profiles/ProfileConfiguration.cs
git commit -m "feat(profile): add nullable Iban field

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 1.4: Generate migration `AddProfileIban`

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddProfileIban.cs` (+ `.Designer.cs`)
- Modify: `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs` (auto-updated by EF)

- [ ] **Step 1: Generate the migration**

```
dotnet ef migrations add AddProfileIban --project src/Humans.Infrastructure --startup-project src/Humans.Web --output-dir Migrations -- -v quiet
```

If that errors with `dotnet ef` not installed: `dotnet tool install --global dotnet-ef --version 10.0.*` then retry.

- [ ] **Step 2: Open the generated migration and verify it ONLY adds the `iban` column**

Read the new file. The `Up()` should be one `AddColumn<string>` on `profiles`. The `Down()` should be one `DropColumn`. No other DDL, no DML. If anything else appears, stop and investigate before continuing — `memory/feedback_migration_checklist.md` rules say no mixed migrations.

- [ ] **Step 3: Run the EF migration reviewer agent**

Per `memory/process/ef-migration-review-gate.md`, dispatch `.claude/agents/ef-migration-reviewer.md` against the new migration and act on any CRITICAL output. (If a tooling agent runner isn't available, manually inspect against the rules in `memory/architecture/no-hand-edited-migrations.md` and the `ef-migration-review-gate.md` checklist.)

- [ ] **Step 4: Apply the migration to a local DB and verify**

```
dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web -- -v quiet
```

Expected: applies cleanly. Verify with `\d profiles` in psql or equivalent: column `iban character varying(34) NULL` is present.

- [ ] **Step 5: Commit**

```
git add src/Humans.Infrastructure/Migrations/
git commit -m "chore(db): migration adds profiles.iban column

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 1.5: Push branch and tag end-of-Phase-1

- [ ] **Step 1: Verify build + full test suite is green**

```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Expected: all green.

- [ ] **Step 2: Format check**

```
dotnet format Humans.slnx --verify-no-changes
```

Expected: clean. If it reports changes needed, run `dotnet format Humans.slnx`, then re-stage and amend the most recent commit.

- [ ] **Step 3: Push**

```
git push
```

**Phase 1 complete.** This is a clean PR target if you want to ship the IBAN field independently before the rest of the Expenses section ships.

---

# Phase 2 — Holded section (sibling)

Builds the narrow `IHoldedClient` and its impl. New section directory; no UI; no entities. Pure HTTP client + config + tests.

**PR boundary:** end of Phase 2 is a clean PR target.

### Task 2.1: Define `IHoldedClient`, DTOs, and exception types

**Files:**
- Create: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`
- Create: `src/Humans.Application/Interfaces/Holded/HoldedApiException.cs`
- Create: `src/Humans.Application/Interfaces/Holded/HoldedPurchaseDocumentDto.cs`

- [ ] **Step 1: Create the DTOs**

```csharp
// src/Humans.Application/Interfaces/Holded/HoldedPurchaseDocumentDto.cs
using NodaTime;

namespace Humans.Application.Interfaces.Holded;

public sealed record HoldedPurchaseDocumentDto
{
    public required string Id { get; init; }
    public required string DocNumber { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public required decimal PaymentsTotal { get; init; }
    public required decimal PaymentsPending { get; init; }
    public Instant? ApprovedAt { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record HoldedPurchaseDocumentLineInput
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record HoldedPurchaseDocumentInput
{
    public required string ContactName { get; init; }
    public required Instant Date { get; init; }
    public required IReadOnlyList<HoldedPurchaseDocumentLineInput> Lines { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Description { get; init; }
}

public sealed record HoldedAttachmentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required Stream Content { get; init; }
}
```

- [ ] **Step 2: Create the exception hierarchy**

```csharp
// src/Humans.Application/Interfaces/Holded/HoldedApiException.cs
namespace Humans.Application.Interfaces.Holded;

public abstract class HoldedApiException : Exception
{
    protected HoldedApiException(string message) : base(message) { }
    protected HoldedApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Transient — eligible for retry (5xx, network, timeout).
/// </summary>
public sealed class HoldedTransientException : HoldedApiException
{
    public HoldedTransientException(string message) : base(message) { }
    public HoldedTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Permanent — 4xx; do not retry.
/// </summary>
public sealed class HoldedPermanentException : HoldedApiException
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public HoldedPermanentException(int statusCode, string? body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }
}
```

- [ ] **Step 3: Create the `IHoldedClient` interface**

```csharp
// src/Humans.Application/Interfaces/Holded/IHoldedClient.cs
namespace Humans.Application.Interfaces.Holded;

public interface IHoldedClient
{
    /// <summary>Creates a purchase document and returns the new doc id.</summary>
    Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input,
        CancellationToken ct = default);

    /// <summary>Replaces the tags on an existing purchase document.</summary>
    Task UpdatePurchaseDocumentTagsAsync(
        string documentId,
        IReadOnlyList<string> tags,
        CancellationToken ct = default);

    /// <summary>Uploads a single attachment to a purchase document.</summary>
    Task UploadAttachmentAsync(
        string documentId,
        HoldedAttachmentInput attachment,
        CancellationToken ct = default);

    /// <summary>Reads a purchase document by id.</summary>
    Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId,
        CancellationToken ct = default);
}
```

- [ ] **Step 4: Build to verify compilation**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 5: Commit**

```
git add src/Humans.Application/Interfaces/Holded/
git commit -m "feat(holded): IHoldedClient surface and DTOs

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2.2: Implement `HoldedClient`

**Files:**
- Create: `src/Humans.Infrastructure/Services/Holded/HoldedClientOptions.cs`
- Create: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
- Test: `tests/Humans.Infrastructure.Tests/Services/Holded/HoldedClientTests.cs`

- [ ] **Step 1: Create `HoldedClientOptions`**

```csharp
// src/Humans.Infrastructure/Services/Holded/HoldedClientOptions.cs
namespace Humans.Infrastructure.Services.Holded;

public sealed class HoldedClientOptions
{
    public const string Section = "Holded";

    /// <summary>Bound from the HOLDED_API_KEY env var only — never appsettings.</summary>
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.holded.com";
}
```

- [ ] **Step 2: Write tests using a stubbed `HttpMessageHandler`**

```csharp
// tests/Humans.Infrastructure.Tests/Services/Holded/HoldedClientTests.cs
using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;
using Humans.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Tests.Services.Holded;

public class HoldedClientTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance);

    [HumansFact]
    public async Task CreatePurchaseDocumentAsync_PostsExpectedJson_AndReturnsId()
    {
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("POST");
            req.RequestUri!.PathAndQuery.Should().Be("/api/invoicing/v1/documents/purchase");
            req.Headers.GetValues("key").Single().Should().Be("test-key");
            return Respond(HttpStatusCode.OK, """{"status":1,"id":"doc-123"}""");
        });

        var client = Make(handler);
        var id = await client.CreatePurchaseDocumentAsync(new HoldedPurchaseDocumentInput
        {
            ContactName = "Alice",
            Date = Instant.FromUtc(2026, 5, 10, 0, 0),
            Lines = [new() { Description = "Train", Amount = 19.52m }]
        });

        id.Should().Be("doc-123");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_ParsesResponse()
    {
        var json = """
        {
          "id":"doc-123","docNumber":"F260009",
          "subtotal":19.52,"tax":0,"total":19.52,
          "paymentsTotal":19.52,"paymentsPending":0,
          "approvedAt":1746835200,
          "tags":["camp-build-camp"]
        }
        """;
        var handler = new StubHandler(req =>
        {
            req.Method.Method.Should().Be("GET");
            req.RequestUri!.PathAndQuery
                .Should().Be("/api/invoicing/v1/documents/purchase/doc-123");
            return Respond(HttpStatusCode.OK, json);
        });

        var client = Make(handler);
        var doc = await client.GetPurchaseDocumentAsync("doc-123");

        doc.Id.Should().Be("doc-123");
        doc.PaymentsPending.Should().Be(0);
        doc.ApprovedAt.Should().NotBeNull();
        doc.Tags.Should().ContainSingle("camp-build-camp");
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_404Throws_HoldedPermanent()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.NotFound, "{}"));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("missing");

        var ex = await act.Should().ThrowAsync<HoldedPermanentException>();
        ex.Which.StatusCode.Should().Be(404);
    }

    [HumansFact]
    public async Task GetPurchaseDocumentAsync_500Throws_HoldedTransient()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable, ""));
        var client = Make(handler);

        var act = async () => await client.GetPurchaseDocumentAsync("doc-123");

        await act.Should().ThrowAsync<HoldedTransientException>();
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
```

- [ ] **Step 3: Run tests, verify failures**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedClientTests"
```

Expected: FAIL — `HoldedClient` doesn't exist yet.

- [ ] **Step 4: Implement `HoldedClient`**

```csharp
// src/Humans.Infrastructure/Services/Holded/HoldedClient.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Application.Interfaces.Holded;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services.Holded;

public sealed class HoldedClient : IHoldedClient
{
    private readonly HttpClient _http;
    private readonly HoldedClientOptions _options;
    private readonly ILogger<HoldedClient> _logger;

    public HoldedClient(
        HttpClient http,
        IOptions<HoldedClientOptions> options,
        ILogger<HoldedClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (_http.BaseAddress is null && !string.IsNullOrEmpty(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl);
    }

    public async Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default)
    {
        var payload = new
        {
            contactName = input.ContactName,
            date = input.Date.ToUnixTimeSeconds(),
            desc = input.Description,
            tags = input.Tags,
            items = input.Lines.Select(l => new
            {
                name = l.Description,
                units = 1,
                subtotal = l.Amount,
                tags = l.Tags
            })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "/api/invoicing/v1/documents/purchase")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);

        var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(body)
            ?? throw new HoldedTransientException("Holded returned empty body");
        var id = node["id"]?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded response missing id");
        return id;
    }

    public async Task UpdatePurchaseDocumentTagsAsync(
        string documentId, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/api/invoicing/v1/documents/purchase/{documentId}")
        { Content = JsonContent.Create(new { tags }) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task UploadAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(attachment.Content);
        streamContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(attachment.ContentType);
        content.Add(streamContent, "file", attachment.FileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoicing/v1/documents/purchase/{documentId}/attach")
        { Content = content };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/invoicing/v1/documents/purchase/{documentId}");
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new HoldedTransientException("Holded returned empty body");

        return new HoldedPurchaseDocumentDto
        {
            Id = node["id"]?.GetValue<string>() ?? "",
            DocNumber = node["docNumber"]?.GetValue<string>() ?? "",
            Subtotal = ReadDecimal(node["subtotal"]),
            Tax = ReadDecimal(node["tax"]),
            Total = ReadDecimal(node["total"]),
            PaymentsTotal = ReadDecimal(node["paymentsTotal"]),
            PaymentsPending = ReadDecimal(node["paymentsPending"]),
            ApprovedAt = ReadInstant(node["approvedAt"]),
            Tags = node["tags"]?.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToList() ?? []
        };
    }

    private void AttachAuth(HttpRequestMessage req) =>
        req.Headers.Add("key", _options.ApiKey);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HoldedTransientException("Holded HTTP send failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HoldedTransientException("Holded HTTP send timed out", ex);
        }

        if (resp.IsSuccessStatusCode) return resp;

        var body = await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode >= 500)
            throw new HoldedTransientException(
                $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}");
        throw new HoldedPermanentException((int)resp.StatusCode, body,
            $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }

    private static decimal ReadDecimal(JsonNode? node) =>
        node?.GetValue<decimal>() ?? 0m;

    private static Instant? ReadInstant(JsonNode? node)
    {
        if (node is null) return null;
        var seconds = node.GetValue<long>();
        return seconds == 0 ? null : Instant.FromUnixTimeSeconds(seconds);
    }
}
```

- [ ] **Step 5: Run tests, verify pass**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedClientTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```
git add src/Humans.Infrastructure/Services/Holded/ tests/Humans.Infrastructure.Tests/Services/Holded/
git commit -m "feat(holded): typed HttpClient impl with transient/permanent error split

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2.3: Wire `HoldedClient` into DI and bind options from env

**Files:**
- Create: `src/Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs`
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

- [ ] **Step 1: Create `HoldedSectionExtensions`**

```csharp
// src/Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;

namespace Humans.Web.Extensions.Sections;

public static class HoldedSectionExtensions
{
    public static IServiceCollection AddHoldedSection(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HoldedClientOptions>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("HOLDED_API_KEY") ?? "";
            opts.BaseUrl = config["Holded:BaseUrl"] ?? "https://api.holded.com";
        });

        services.AddHttpClient<IHoldedClient, HoldedClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<HoldedClientOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
```

- [ ] **Step 2: Call it from `InfrastructureServiceCollectionExtensions`**

In `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`, find where other section extensions are called (look for `AddBudgetSection`, `AddStoreSection`, etc.) and add:

```csharp
services.AddHoldedSection(configuration);
```

- [ ] **Step 3: Build to verify**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```
git add src/Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs
git commit -m "feat(holded): DI registration with env-bound API key

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2.4: Add `Holded` architecture test

**Files:**
- Test: `tests/Humans.Application.Tests/Architecture/HoldedArchitectureTests.cs`

- [ ] **Step 1: Write the architecture test**

```csharp
// tests/Humans.Application.Tests/Architecture/HoldedArchitectureTests.cs
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Tests.Common;

namespace Humans.Application.Tests.Architecture;

public class HoldedArchitectureTests
{
    [HumansFact]
    public void IHoldedClient_LivesIn_HoldedNamespace()
    {
        typeof(IHoldedClient).Namespace
            .Should().Be("Humans.Application.Interfaces.Holded");
    }

    [HumansFact]
    public void HoldedClient_HasNoEFCoreReference()
    {
        var asm = typeof(IHoldedClient).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore",
                "Holded section is HTTP-only — must not depend on EF Core");
    }

    [HumansFact]
    public void HoldedExceptions_AreClassified_TransientOrPermanent()
    {
        typeof(HoldedTransientException).Should().BeAssignableTo<HoldedApiException>();
        typeof(HoldedPermanentException).Should().BeAssignableTo<HoldedApiException>();
    }
}
```

- [ ] **Step 2: Run, verify pass**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedArchitectureTests"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```
git add tests/Humans.Application.Tests/Architecture/HoldedArchitectureTests.cs
git commit -m "test(holded): architecture tests pin namespace and EF-Core exclusion

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2.5: Write `docs/sections/Holded.md`

**Files:**
- Create: `docs/sections/Holded.md`

- [ ] **Step 1: Read `docs/sections/SECTION-TEMPLATE.md` for shape**

```
Read docs/sections/SECTION-TEMPLATE.md
```

- [ ] **Step 2: Write the Holded section invariant doc**

```markdown
<!-- freshness:triggers
  src/Humans.Application/Interfaces/Holded/**
  src/Humans.Infrastructure/Services/Holded/**
  src/Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs
-->

# Holded — Section Invariants

Thin typed-`HttpClient` surface to the Holded accounting API. Owned narrowly: v1 ships only the four methods the Expenses section needs. The broader Finance/Holded reconciliation described in `Finance.md` is forward-looking and will extend this same surface without breaking consumers.

## Concepts

- A **Purchase Document** in Holded is the org's incoming invoice/expense record. Expenses creates one per approved expense report.
- The **API key** is bound from the `HOLDED_API_KEY` environment variable only — never `appsettings.json`. Never logged.
- Errors are classified at the client boundary: `HoldedTransientException` (5xx, network, timeout) is retry-eligible; `HoldedPermanentException` (4xx) is not.

## Data Model

None. Holded owns no Humans tables in v1.

## Routing

None. Holded has no UI in v1.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Other sections (Expenses) | Call `IHoldedClient` via DI. |
| Any human | None directly. |

## Invariants

- API key is read from `HOLDED_API_KEY` env var only and is never written to logs, audit entries, or error messages.
- All HTTP calls go through one typed `HttpClient` (`HoldedClient`). No raw `HttpClient.Send` elsewhere.
- Currency is EUR-only. Multi-currency is out of scope.
- 5xx and network failures throw `HoldedTransientException`. 4xx failures throw `HoldedPermanentException`. Consumers choose retry policy.

## Negative Access Rules

- The Holded section **does not** read or write any Humans table.
- The Holded section **does not** maintain its own background sync / pull job in v1. (`HoldedSyncJob`, `holded_transactions`, etc. described in `Finance.md` are future work.)

## Triggers

None. The client is pure on-demand.

## Cross-Section Dependencies

None outbound. Inbound: Expenses calls `IHoldedClient`. Future Finance work will extend.

## Architecture

**Owning section:** `Holded`
**Owning services:** `IHoldedClient` (impl `HoldedClient`)
**Owned tables:** none
**Status:** (A) New section.

- `IHoldedClient` lives in `Humans.Application/Interfaces/Holded/`.
- `HoldedClient` lives in `Humans.Infrastructure/Services/Holded/` and is the single typed `HttpClient` to Holded.
- Registered via `services.AddHoldedSection(config)` in `Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs`.
- `HoldedClientOptions.ApiKey` is bound from the `HOLDED_API_KEY` env var at startup.
- **GDPR** — no `IUserDataContributor`. Holded owns no per-user data.

### Future evolution

When the broader Finance/Holded sync described in `docs/sections/Finance.md` ships, it adds:
- a recurring pull job (`HoldedSyncJob`) that imports purchase docs into a `holded_transactions` table,
- additional client methods (list / search / update payments) on the same `IHoldedClient` surface,
- the unmatched-queue UI under `/Finance`.

The current four-method surface stays stable; new methods get added alongside.
```

- [ ] **Step 3: Commit**

```
git add docs/sections/Holded.md
git commit -m "docs(holded): section invariant doc

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2.6: Push, end-of-Phase-2

- [ ] **Step 1: Build + test + format check**

```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet format Humans.slnx --verify-no-changes
```

- [ ] **Step 2: Push**

```
git push
```

**Phase 2 complete.** PR target: ship Holded section as its own PR before Expenses lands.

---

# Phase 3 — Expenses domain + repository + migration

Adds the four entities, their EF configs, the migration, the `IExpenseRepository` interface and impl, and repository tests. No service or UI yet.

**PR boundary:** end of Phase 3 is a clean PR target.

### Task 3.1: Create domain enums

**Files:**
- Create: `src/Humans.Domain/Enums/ExpenseReportStatus.cs`
- Create: `src/Humans.Domain/Enums/HoldedExpenseOutboxEventType.cs`
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Create `ExpenseReportStatus`**

```csharp
// src/Humans.Domain/Enums/ExpenseReportStatus.cs
namespace Humans.Domain.Enums;

public enum ExpenseReportStatus
{
    Draft,
    Submitted,
    CoordinatorEndorsed,
    Approved,
    SepaSent,
    Paid,
    Withdrawn
}
```

- [ ] **Step 2: Create `HoldedExpenseOutboxEventType`**

```csharp
// src/Humans.Domain/Enums/HoldedExpenseOutboxEventType.cs
namespace Humans.Domain.Enums;

public enum HoldedExpenseOutboxEventType
{
    CreateIncomingDoc,
    UpdateIncomingDocTag
}
```

- [ ] **Step 3: Add new `AuditAction` values**

In `src/Humans.Domain/Enums/AuditAction.cs`, add at the end of the enum:

```csharp
ExpenseSubmit,
ExpenseEndorse,
ExpenseCoordinatorReject,
ExpenseApprove,
ExpenseReject,
ExpenseWithdraw,
ExpenseCategoryOverride,
ExpenseSepaSent,
ExpensePaid,
IbanSet,
IbanRemove,
IbanReveal,
```

Don't reorder existing values — they're persisted as strings via `HasConversion<string>()`, so the order is meaningful only if you change to int storage. Verify the existing `AuditActionConfiguration` (or wherever `AuditAction` is configured) uses string conversion before continuing.

- [ ] **Step 4: Build to verify**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 5: Commit**

```
git add src/Humans.Domain/Enums/
git commit -m "feat(expenses): domain enums for status, outbox event type, audit actions

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.2: Create domain entities

**Files:**
- Create: `src/Humans.Domain/Entities/ExpenseReport.cs`
- Create: `src/Humans.Domain/Entities/ExpenseLine.cs`
- Create: `src/Humans.Domain/Entities/ExpenseAttachment.cs`
- Create: `src/Humans.Domain/Entities/HoldedExpenseOutboxEvent.cs`

- [ ] **Step 1: Create `ExpenseAttachment`**

```csharp
// src/Humans.Domain/Entities/ExpenseAttachment.cs
using NodaTime;

namespace Humans.Domain.Entities;

public class ExpenseAttachment
{
    public Guid Id { get; init; }
    public string OriginalFileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public Guid UploadedByUserId { get; set; }
    public Instant UploadedAt { get; init; }
}
```

- [ ] **Step 2: Create `ExpenseLine`**

```csharp
// src/Humans.Domain/Entities/ExpenseLine.cs
namespace Humans.Domain.Entities;

public class ExpenseLine
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public Guid? AttachmentId { get; set; }
    public int SortOrder { get; set; }

    public ExpenseAttachment? Attachment { get; set; }
}
```

- [ ] **Step 3: Create `ExpenseReport`**

```csharp
// src/Humans.Domain/Entities/ExpenseReport.cs
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class ExpenseReport
{
    public Guid Id { get; init; }
    public Guid SubmitterUserId { get; set; }
    public Guid BudgetCategoryId { get; set; }
    public Guid BudgetYearId { get; set; }
    public ExpenseReportStatus Status { get; set; }
    public string? Note { get; set; }
    public string PayeeName { get; set; } = "";
    public string PayeeIban { get; set; } = "";
    public decimal Total { get; set; }
    public Instant? SubmittedAt { get; set; }
    public Guid? CoordinatorEndorsedByUserId { get; set; }
    public Instant? CoordinatorEndorsedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Instant? ApprovedAt { get; set; }
    public Instant? SepaSentAt { get; set; }
    public Instant? PaidAt { get; set; }
    public string? LastRejectionReason { get; set; }
    public Guid? LastRejectedByUserId { get; set; }
    public Instant? LastRejectedAt { get; set; }
    public string? HoldedDocId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public ICollection<ExpenseLine> Lines { get; set; } = new List<ExpenseLine>();
}
```

- [ ] **Step 4: Create `HoldedExpenseOutboxEvent`**

```csharp
// src/Humans.Domain/Entities/HoldedExpenseOutboxEvent.cs
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class HoldedExpenseOutboxEvent
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public HoldedExpenseOutboxEventType EventType { get; set; }
    public Instant OccurredAt { get; init; }
    public Instant? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public bool FailedPermanently { get; set; }
}
```

- [ ] **Step 5: Build**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 6: Commit**

```
git add src/Humans.Domain/Entities/ExpenseReport.cs src/Humans.Domain/Entities/ExpenseLine.cs src/Humans.Domain/Entities/ExpenseAttachment.cs src/Humans.Domain/Entities/HoldedExpenseOutboxEvent.cs
git commit -m "feat(expenses): domain entities

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.3: Create EF configurations

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseReportConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseLineConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseAttachmentConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Expenses/HoldedExpenseOutboxEventConfiguration.cs`

- [ ] **Step 1: Create `ExpenseReportConfiguration`**

```csharp
// src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseReportConfiguration.cs
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class ExpenseReportConfiguration : IEntityTypeConfiguration<ExpenseReport>
{
    public void Configure(EntityTypeBuilder<ExpenseReport> b)
    {
        b.ToTable("expense_reports");
        b.HasKey(x => x.Id);

        b.Property(x => x.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.PayeeName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PayeeIban).HasMaxLength(34).IsRequired();
        b.Property(x => x.Total).HasColumnType("decimal(12,2)");
        b.Property(x => x.LastRejectionReason).HasMaxLength(1000);
        b.Property(x => x.HoldedDocId).HasMaxLength(64);

        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.ExpenseReportId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK-only refs (no nav)
        b.HasIndex(x => new { x.SubmitterUserId, x.Status });
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.BudgetCategoryId);
        b.HasIndex(x => x.HoldedDocId);
    }
}
```

- [ ] **Step 2: Create `ExpenseLineConfiguration`**

```csharp
// src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseLineConfiguration.cs
using Humans.Domain.Entities;
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

        b.HasOne(x => x.Attachment)
            .WithMany()
            .HasForeignKey(x => x.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ExpenseReportId);
    }
}
```

- [ ] **Step 3: Create `ExpenseAttachmentConfiguration`**

```csharp
// src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseAttachmentConfiguration.cs
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class ExpenseAttachmentConfiguration : IEntityTypeConfiguration<ExpenseAttachment>
{
    public void Configure(EntityTypeBuilder<ExpenseAttachment> b)
    {
        b.ToTable("expense_attachments");
        b.HasKey(x => x.Id);

        b.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.Extension).HasMaxLength(8).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
    }
}
```

- [ ] **Step 4: Create `HoldedExpenseOutboxEventConfiguration`**

```csharp
// src/Humans.Infrastructure/Data/Configurations/Expenses/HoldedExpenseOutboxEventConfiguration.cs
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Expenses;

public class HoldedExpenseOutboxEventConfiguration
    : IEntityTypeConfiguration<HoldedExpenseOutboxEvent>
{
    public void Configure(EntityTypeBuilder<HoldedExpenseOutboxEvent> b)
    {
        b.ToTable("holded_expense_outbox_events");
        b.HasKey(x => x.Id);

        b.Property(x => x.EventType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        b.Property(x => x.LastError).HasMaxLength(2000);

        b.HasIndex(x => x.ExpenseReportId);
        b.HasIndex(x => new { x.ProcessedAt, x.FailedPermanently });
    }
}
```

- [ ] **Step 5: Add `DbSet` properties to `HumansDbContext`**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, near the other `DbSet<>` declarations, add:

```csharp
public DbSet<ExpenseReport> ExpenseReports => Set<ExpenseReport>();
public DbSet<ExpenseLine> ExpenseLines => Set<ExpenseLine>();
public DbSet<ExpenseAttachment> ExpenseAttachments => Set<ExpenseAttachment>();
public DbSet<HoldedExpenseOutboxEvent> HoldedExpenseOutboxEvents
    => Set<HoldedExpenseOutboxEvent>();
```

- [ ] **Step 6: Build**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 7: Commit**

```
git add src/Humans.Infrastructure/Data/Configurations/Expenses/ src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(expenses): EF configurations and DbSets

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.4: Generate `AddExpensesSection` migration

- [ ] **Step 1: Generate migration**

```
dotnet ef migrations add AddExpensesSection --project src/Humans.Infrastructure --startup-project src/Humans.Web --output-dir Migrations -- -v quiet
```

- [ ] **Step 2: Read the generated migration end-to-end**

```
Read src/Humans.Infrastructure/Migrations/<timestamp>_AddExpensesSection.cs
```

Verify:
- 4 `CreateTable` calls (one per entity)
- All FK constraints are intra-table (just the lines→reports cascade)
- No DML
- Indexes match what EF configs declared
- `Down()` drops in reverse order

- [ ] **Step 3: Run the EF migration reviewer agent**

Per `memory/process/ef-migration-review-gate.md`. Address any CRITICAL findings before continuing.

- [ ] **Step 4: Apply locally**

```
dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web -- -v quiet
```

Verify the four tables exist. Indexes exist. Column types match.

- [ ] **Step 5: Commit**

```
git add src/Humans.Infrastructure/Migrations/
git commit -m "chore(db): migration adds expenses section tables

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.5: Define `IExpenseRepository`

**Files:**
- Create: `src/Humans.Application/Interfaces/Repositories/IExpenseRepository.cs`

- [ ] **Step 1: Create the interface**

```csharp
// src/Humans.Application/Interfaces/Repositories/IExpenseRepository.cs
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

public interface IExpenseRepository
{
    // Reads
    Task<ExpenseReport?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ExpenseReport?> GetByIdWithLinesAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetByStatusAsync(
        ExpenseReportStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReport>> GetForReviewQueueAsync(CancellationToken ct = default);
    Task<ExpenseAttachment?> GetAttachmentByIdAsync(Guid id, CancellationToken ct = default);

    // Writes — atomic per-method, all inside one short-lived DbContext.
    Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task<bool> AddLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    Task<bool> UpdateLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    Task<bool> RemoveLineAsync(
        Guid reportId, Guid lineId, CancellationToken ct = default);
    Task<Guid> AddAttachmentAsync(
        ExpenseAttachment attachment, CancellationToken ct = default);
    Task RemoveAttachmentAsync(Guid id, CancellationToken ct = default);
    Task SetLineAttachmentAsync(
        Guid lineId, Guid? attachmentId, CancellationToken ct = default);

    Task<bool> SubmitAsync(
        Guid reportId,
        string payeeName, string payeeIban,
        NodaTime.Instant submittedAt,
        CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, NodaTime.Instant updatedAt, CancellationToken ct = default);

    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid actorUserId,
        NodaTime.Instant endorsedAt, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId,
        Guid? overrideCategoryId,
        NodaTime.Instant approvedAt,
        Guid outboxEventId,
        CancellationToken ct = default);

    Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    Task<bool> CategoryOverrideAsync(
        Guid reportId, Guid actorUserId,
        Guid newCategoryId,
        NodaTime.Instant overriddenAt,
        Guid outboxEventId,
        CancellationToken ct = default);

    Task<int> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds,
        NodaTime.Instant sepaSentAt,
        CancellationToken ct = default);

    Task<bool> MarkPaidAsync(
        Guid reportId, NodaTime.Instant paidAt, CancellationToken ct = default);

    // Outbox
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        int limit, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetFailedPermanentlyAsync(
        CancellationToken ct = default);
    Task SetHoldedDocIdAsync(
        Guid reportId, string holdedDocId,
        Guid outboxEventId, NodaTime.Instant processedAt,
        CancellationToken ct = default);
    Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, CancellationToken ct = default);
    Task MarkOutboxFailedPermanentlyAsync(
        Guid outboxEventId, string error,
        NodaTime.Instant processedAt, CancellationToken ct = default);
    Task MarkOutboxProcessedAsync(
        Guid outboxEventId, NodaTime.Instant processedAt, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 3: Commit**

```
git add src/Humans.Application/Interfaces/Repositories/IExpenseRepository.cs
git commit -m "feat(expenses): IExpenseRepository surface

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.6: Implement `ExpenseRepository` (read methods)

**Files:**
- Create: `src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs`
- Test: `tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs`

This task creates the file and implements only the **read** methods. Subsequent tasks add write methods.

- [ ] **Step 1: Write tests for read methods**

```csharp
// tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Expenses;
using Humans.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Humans.Infrastructure.Tests.Repositories.Expenses;

public class ExpenseRepositoryTests : IAsyncLifetime
{
    private ServiceProvider _sp = null!;
    private IDbContextFactory<HumansDbContext> _factory = null!;
    private IExpenseRepository _sut = null!;

    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<HumansDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        _sp = services.BuildServiceProvider();
        _factory = _sp.GetRequiredService<IDbContextFactory<HumansDbContext>>();
        _sut = new ExpenseRepository(_factory, NullLogger<ExpenseRepository>.Instance);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
    }

    [HumansFact]
    public async Task GetByIdAsync_ReturnsRecord_WhenExists()
    {
        var id = Guid.NewGuid();
        await Seed(new ExpenseReport
        {
            Id = id,
            SubmitterUserId = Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = ExpenseReportStatus.Draft,
            CreatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
        });

        var got = await _sut.GetByIdAsync(id);
        got.Should().NotBeNull();
        got!.Id.Should().Be(id);
    }

    [HumansFact]
    public async Task GetForSubmitterAsync_ScopesByUser()
    {
        var meId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await Seed(MakeReport(submitter: meId), MakeReport(submitter: otherId));

        var mine = await _sut.GetForSubmitterAsync(meId);
        mine.Should().HaveCount(1);
        mine[0].SubmitterUserId.Should().Be(meId);
    }

    [HumansFact]
    public async Task GetByStatusAsync_FiltersExactly()
    {
        await Seed(
            MakeReport(status: ExpenseReportStatus.Draft),
            MakeReport(status: ExpenseReportStatus.Submitted),
            MakeReport(status: ExpenseReportStatus.Approved));

        var submitted = await _sut.GetByStatusAsync(ExpenseReportStatus.Submitted);
        submitted.Should().HaveCount(1);
        submitted[0].Status.Should().Be(ExpenseReportStatus.Submitted);
    }

    private async Task Seed(params ExpenseReport[] reports)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.ExpenseReports.AddRange(reports);
        await ctx.SaveChangesAsync();
    }

    private static ExpenseReport MakeReport(
        Guid? submitter = null,
        ExpenseReportStatus status = ExpenseReportStatus.Draft)
    {
        var now = Instant.FromUtc(2026, 5, 1, 0, 0);
        return new ExpenseReport
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitter ?? Guid.NewGuid(),
            BudgetCategoryId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
```

- [ ] **Step 2: Implement read methods only**

```csharp
// src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Expenses;

public sealed class ExpenseRepository : IExpenseRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;
    private readonly ILogger<ExpenseRepository> _logger;

    public ExpenseRepository(
        IDbContextFactory<HumansDbContext> factory,
        ILogger<ExpenseRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<ExpenseReport?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<ExpenseReport?> GetByIdWithLinesAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseReports.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Attachment)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<ExpenseReport>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseReports.AsNoTracking()
            .Where(r => r.SubmitterUserId == submitterUserId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ExpenseReport>> GetByStatusAsync(
        ExpenseReportStatus status, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseReports.AsNoTracking()
            .Where(r => r.Status == status)
            .OrderBy(r => r.SubmittedAt ?? r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ExpenseReport>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default)
    {
        if (categoryIds.Count == 0) return [];
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseReports.AsNoTracking()
            .Where(r => r.Status == status && categoryIds.Contains(r.BudgetCategoryId))
            .OrderBy(r => r.SubmittedAt ?? r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ExpenseReport>> GetForReviewQueueAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseReports.AsNoTracking()
            .Where(r => r.Status != ExpenseReportStatus.Draft
                     && r.Status != ExpenseReportStatus.Withdrawn)
            .OrderByDescending(r => r.SubmittedAt ?? r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ExpenseAttachment?> GetAttachmentByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ExpenseAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    // Write methods follow in next tasks — throw NotImplementedException
    // for now to keep the file compiling.

    public Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> AddLineAsync(Guid reportId, ExpenseLine line, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> UpdateLineAsync(Guid reportId, ExpenseLine line, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Guid> AddAttachmentAsync(ExpenseAttachment attachment, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task RemoveAttachmentAsync(Guid id, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task SetLineAttachmentAsync(Guid lineId, Guid? attachmentId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> SubmitAsync(Guid reportId, string payeeName, string payeeIban, Instant submittedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> WithdrawAsync(Guid reportId, Instant updatedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> CoordinatorEndorseAsync(Guid reportId, Guid actorUserId, Instant endorsedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> CoordinatorRejectAsync(Guid reportId, Guid actorUserId, string reason, Instant rejectedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> ApproveAsync(Guid reportId, Guid actorUserId, Guid? overrideCategoryId, Instant approvedAt, Guid outboxEventId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> FinanceRejectAsync(Guid reportId, Guid actorUserId, string reason, Instant rejectedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> CategoryOverrideAsync(Guid reportId, Guid actorUserId, Guid newCategoryId, Instant overriddenAt, Guid outboxEventId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<int> MarkSepaSentAsync(IReadOnlyCollection<Guid> reportIds, Instant sepaSentAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> MarkPaidAsync(Guid reportId, Instant paidAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(int limit, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetFailedPermanentlyAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task SetHoldedDocIdAsync(Guid reportId, string holdedDocId, Guid outboxEventId, Instant processedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task IncrementOutboxRetryAsync(Guid outboxEventId, string error, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task MarkOutboxFailedPermanentlyAsync(Guid outboxEventId, string error, Instant processedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task MarkOutboxProcessedAsync(Guid outboxEventId, Instant processedAt, CancellationToken ct = default)
        => throw new NotImplementedException();
}
```

- [ ] **Step 3: Run read-method tests, verify pass**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseRepositoryTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 4: Commit**

```
git add src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs
git commit -m "feat(expenses): repository read methods + tests

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.7: Implement `ExpenseRepository` write methods (drafts + attachments)

This task expands the repository with the write methods used during draft authoring: `AddDraftAsync`, `UpdateDraftAsync`, `AddLineAsync`, `UpdateLineAsync`, `RemoveLineAsync`, `AddAttachmentAsync`, `RemoveAttachmentAsync`, `SetLineAttachmentAsync`.

- [ ] **Step 1: Add tests covering each method (round-trip seed → mutate → re-read)**

Append to `ExpenseRepositoryTests`:

```csharp
[HumansFact]
public async Task AddDraftAsync_PersistsReport()
{
    var report = MakeReport();
    await _sut.AddDraftAsync(report);

    var loaded = await _sut.GetByIdAsync(report.Id);
    loaded.Should().NotBeNull();
    loaded!.Status.Should().Be(ExpenseReportStatus.Draft);
}

[HumansFact]
public async Task AddLineAsync_AppendsLine_AndUpdatesTotal()
{
    var report = MakeReport();
    await _sut.AddDraftAsync(report);

    var ok = await _sut.AddLineAsync(report.Id,
        new ExpenseLine { Id = Guid.NewGuid(), Description = "x", Amount = 12.50m });
    ok.Should().BeTrue();

    var loaded = await _sut.GetByIdWithLinesAsync(report.Id);
    loaded!.Lines.Should().HaveCount(1);
    loaded.Total.Should().Be(12.50m);
}

[HumansFact]
public async Task RemoveLineAsync_RemovesAndRecomputesTotal()
{
    var report = MakeReport();
    await _sut.AddDraftAsync(report);
    var lineId = Guid.NewGuid();
    await _sut.AddLineAsync(report.Id,
        new ExpenseLine { Id = lineId, Description = "a", Amount = 10m });
    await _sut.AddLineAsync(report.Id,
        new ExpenseLine { Id = Guid.NewGuid(), Description = "b", Amount = 20m });

    var ok = await _sut.RemoveLineAsync(report.Id, lineId);
    ok.Should().BeTrue();

    var loaded = await _sut.GetByIdWithLinesAsync(report.Id);
    loaded!.Lines.Should().HaveCount(1);
    loaded.Total.Should().Be(20m);
}

[HumansFact]
public async Task SetLineAttachmentAsync_LinksAttachment()
{
    var report = MakeReport();
    await _sut.AddDraftAsync(report);
    var lineId = Guid.NewGuid();
    await _sut.AddLineAsync(report.Id,
        new ExpenseLine { Id = lineId, Description = "x", Amount = 1m });

    var attachId = await _sut.AddAttachmentAsync(new ExpenseAttachment
    {
        Id = Guid.NewGuid(),
        OriginalFileName = "r.pdf",
        Extension = ".pdf",
        ContentType = "application/pdf",
        SizeBytes = 100,
        UploadedByUserId = Guid.NewGuid(),
        UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
    });

    await _sut.SetLineAttachmentAsync(lineId, attachId);

    var loaded = await _sut.GetByIdWithLinesAsync(report.Id);
    loaded!.Lines[0].AttachmentId.Should().Be(attachId);
    loaded.Lines[0].Attachment.Should().NotBeNull();
}
```

- [ ] **Step 2: Replace the relevant `NotImplementedException` stubs**

In `ExpenseRepository.cs`:

```csharp
public async Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    ctx.ExpenseReports.Add(report);
    await ctx.SaveChangesAsync(ct);
}

public async Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var tracked = await ctx.ExpenseReports
        .FirstOrDefaultAsync(r => r.Id == report.Id, ct);
    if (tracked is null || tracked.Status != ExpenseReportStatus.Draft) return;
    tracked.BudgetCategoryId = report.BudgetCategoryId;
    tracked.BudgetYearId = report.BudgetYearId;
    tracked.Note = report.Note;
    tracked.UpdatedAt = report.UpdatedAt;
    await ctx.SaveChangesAsync(ct);
}

public async Task<bool> AddLineAsync(
    Guid reportId, ExpenseLine line, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var report = await ctx.ExpenseReports.Include(r => r.Lines)
        .FirstOrDefaultAsync(r => r.Id == reportId, ct);
    if (report is null) return false;
    line.ExpenseReportId = reportId;
    line.SortOrder = report.Lines.Count;
    report.Lines.Add(line);
    report.Total = report.Lines.Sum(l => l.Amount);
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> UpdateLineAsync(
    Guid reportId, ExpenseLine line, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var report = await ctx.ExpenseReports.Include(r => r.Lines)
        .FirstOrDefaultAsync(r => r.Id == reportId, ct);
    var tracked = report?.Lines.FirstOrDefault(l => l.Id == line.Id);
    if (report is null || tracked is null) return false;
    tracked.Description = line.Description;
    tracked.Amount = line.Amount;
    report.Total = report.Lines.Sum(l => l.Amount);
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> RemoveLineAsync(
    Guid reportId, Guid lineId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var report = await ctx.ExpenseReports.Include(r => r.Lines)
        .FirstOrDefaultAsync(r => r.Id == reportId, ct);
    var tracked = report?.Lines.FirstOrDefault(l => l.Id == lineId);
    if (report is null || tracked is null) return false;
    report.Lines.Remove(tracked);
    ctx.ExpenseLines.Remove(tracked);
    report.Total = report.Lines.Sum(l => l.Amount);
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<Guid> AddAttachmentAsync(
    ExpenseAttachment attachment, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    ctx.ExpenseAttachments.Add(attachment);
    await ctx.SaveChangesAsync(ct);
    return attachment.Id;
}

public async Task RemoveAttachmentAsync(Guid id, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var att = await ctx.ExpenseAttachments.FirstOrDefaultAsync(a => a.Id == id, ct);
    if (att is null) return;
    ctx.ExpenseAttachments.Remove(att);
    await ctx.SaveChangesAsync(ct);
}

public async Task SetLineAttachmentAsync(
    Guid lineId, Guid? attachmentId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var line = await ctx.ExpenseLines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
    if (line is null) return;
    line.AttachmentId = attachmentId;
    await ctx.SaveChangesAsync(ct);
}
```

- [ ] **Step 3: Run tests**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseRepositoryTests"
```

Expected: PASS.

- [ ] **Step 4: Commit**

```
git add src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs
git commit -m "feat(expenses): repository draft + line + attachment writes

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.8: Implement `ExpenseRepository` workflow transitions

Adds: `SubmitAsync`, `WithdrawAsync`, `CoordinatorEndorseAsync`, `CoordinatorRejectAsync`, `ApproveAsync`, `FinanceRejectAsync`, `CategoryOverrideAsync`, `MarkSepaSentAsync`, `MarkPaidAsync`.

For brevity, this task lists the implementation patterns; mirror the structure of `AddLineAsync` (load tracked entity, mutate, save).

- [ ] **Step 1: Add tests for the state-machine transitions**

```csharp
[HumansFact]
public async Task SubmitAsync_FlipsStatus_AndStampsSubmittedAt()
{
    var r = MakeReport();
    await _sut.AddDraftAsync(r);
    await _sut.AddLineAsync(r.Id,
        new ExpenseLine { Id = Guid.NewGuid(), Description = "x", Amount = 5m });
    var attachId = await _sut.AddAttachmentAsync(NewAttachment());
    var line = (await _sut.GetByIdWithLinesAsync(r.Id))!.Lines[0];
    await _sut.SetLineAttachmentAsync(line.Id, attachId);

    var ok = await _sut.SubmitAsync(r.Id, "Alice", "ES9121000418450200051332",
        Instant.FromUtc(2026, 5, 2, 9, 0));
    ok.Should().BeTrue();

    var loaded = await _sut.GetByIdAsync(r.Id);
    loaded!.Status.Should().Be(ExpenseReportStatus.Submitted);
    loaded.PayeeName.Should().Be("Alice");
    loaded.PayeeIban.Should().Be("ES9121000418450200051332");
    loaded.SubmittedAt.Should().NotBeNull();
}

[HumansFact]
public async Task ApproveAsync_StampsApproval_AndInsertsOutboxRow()
{
    var r = MakeReport(status: ExpenseReportStatus.Submitted);
    await Seed(r);
    var outboxId = Guid.NewGuid();

    var ok = await _sut.ApproveAsync(r.Id, Guid.NewGuid(), null,
        Instant.FromUtc(2026, 5, 3, 12, 0), outboxId);
    ok.Should().BeTrue();

    var loaded = await _sut.GetByIdAsync(r.Id);
    loaded!.Status.Should().Be(ExpenseReportStatus.Approved);
    loaded.ApprovedAt.Should().NotBeNull();

    await using var ctx = await _factory.CreateDbContextAsync();
    var event_ = await ctx.HoldedExpenseOutboxEvents.FirstAsync(e => e.Id == outboxId);
    event_.ExpenseReportId.Should().Be(r.Id);
    event_.EventType.Should().Be(HoldedExpenseOutboxEventType.CreateIncomingDoc);
}

[HumansFact]
public async Task MarkSepaSentAsync_FlipsAllInBatch()
{
    var a = MakeReport(status: ExpenseReportStatus.Approved);
    var b = MakeReport(status: ExpenseReportStatus.Approved);
    var c = MakeReport(status: ExpenseReportStatus.Submitted); // not in batch
    await Seed(a, b, c);

    var n = await _sut.MarkSepaSentAsync(new[] { a.Id, b.Id },
        Instant.FromUtc(2026, 5, 4, 10, 0));
    n.Should().Be(2);

    (await _sut.GetByIdAsync(a.Id))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
    (await _sut.GetByIdAsync(b.Id))!.Status.Should().Be(ExpenseReportStatus.SepaSent);
    (await _sut.GetByIdAsync(c.Id))!.Status.Should().Be(ExpenseReportStatus.Submitted);
}

private static ExpenseAttachment NewAttachment() => new()
{
    Id = Guid.NewGuid(),
    OriginalFileName = "r.pdf",
    Extension = ".pdf",
    ContentType = "application/pdf",
    SizeBytes = 100,
    UploadedByUserId = Guid.NewGuid(),
    UploadedAt = Instant.FromUtc(2026, 5, 1, 0, 0)
};
```

- [ ] **Step 2: Implement the transition methods**

```csharp
public async Task<bool> SubmitAsync(
    Guid reportId, string payeeName, string payeeIban,
    Instant submittedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null || r.Status != ExpenseReportStatus.Draft) return false;
    r.Status = ExpenseReportStatus.Submitted;
    r.PayeeName = payeeName;
    r.PayeeIban = payeeIban;
    r.SubmittedAt = submittedAt;
    r.UpdatedAt = submittedAt;
    r.LastRejectionReason = null;
    r.LastRejectedByUserId = null;
    r.LastRejectedAt = null;
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> WithdrawAsync(
    Guid reportId, Instant updatedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null) return false;
    if (r.Status is ExpenseReportStatus.Approved
                 or ExpenseReportStatus.SepaSent
                 or ExpenseReportStatus.Paid
                 or ExpenseReportStatus.Withdrawn) return false;
    r.Status = ExpenseReportStatus.Withdrawn;
    r.UpdatedAt = updatedAt;
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> CoordinatorEndorseAsync(
    Guid reportId, Guid actorUserId, Instant endorsedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null || r.Status != ExpenseReportStatus.Submitted) return false;
    r.Status = ExpenseReportStatus.CoordinatorEndorsed;
    r.CoordinatorEndorsedByUserId = actorUserId;
    r.CoordinatorEndorsedAt = endorsedAt;
    r.UpdatedAt = endorsedAt;
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> CoordinatorRejectAsync(
    Guid reportId, Guid actorUserId, string reason,
    Instant rejectedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null || r.Status != ExpenseReportStatus.Submitted) return false;
    r.Status = ExpenseReportStatus.Draft;
    r.LastRejectionReason = reason;
    r.LastRejectedByUserId = actorUserId;
    r.LastRejectedAt = rejectedAt;
    r.UpdatedAt = rejectedAt;
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> ApproveAsync(
    Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
    Instant approvedAt, Guid outboxEventId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null) return false;
    if (r.Status is not (ExpenseReportStatus.Submitted
                         or ExpenseReportStatus.CoordinatorEndorsed)) return false;

    r.Status = ExpenseReportStatus.Approved;
    r.ApprovedByUserId = actorUserId;
    r.ApprovedAt = approvedAt;
    r.UpdatedAt = approvedAt;
    if (overrideCategoryId is { } cat) r.BudgetCategoryId = cat;

    ctx.HoldedExpenseOutboxEvents.Add(new HoldedExpenseOutboxEvent
    {
        Id = outboxEventId,
        ExpenseReportId = r.Id,
        EventType = HoldedExpenseOutboxEventType.CreateIncomingDoc,
        OccurredAt = approvedAt
    });

    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> FinanceRejectAsync(
    Guid reportId, Guid actorUserId, string reason,
    Instant rejectedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null) return false;
    if (r.Status is not (ExpenseReportStatus.Submitted
                         or ExpenseReportStatus.CoordinatorEndorsed)) return false;
    r.Status = ExpenseReportStatus.Draft;
    r.LastRejectionReason = reason;
    r.LastRejectedByUserId = actorUserId;
    r.LastRejectedAt = rejectedAt;
    r.CoordinatorEndorsedAt = null;
    r.CoordinatorEndorsedByUserId = null;
    r.UpdatedAt = rejectedAt;
    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<bool> CategoryOverrideAsync(
    Guid reportId, Guid actorUserId, Guid newCategoryId,
    Instant overriddenAt, Guid outboxEventId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null || r.Status != ExpenseReportStatus.Approved) return false;
    r.BudgetCategoryId = newCategoryId;
    r.UpdatedAt = overriddenAt;

    ctx.HoldedExpenseOutboxEvents.Add(new HoldedExpenseOutboxEvent
    {
        Id = outboxEventId,
        ExpenseReportId = r.Id,
        EventType = HoldedExpenseOutboxEventType.UpdateIncomingDocTag,
        OccurredAt = overriddenAt
    });

    await ctx.SaveChangesAsync(ct);
    return true;
}

public async Task<int> MarkSepaSentAsync(
    IReadOnlyCollection<Guid> reportIds, Instant sepaSentAt,
    CancellationToken ct = default)
{
    if (reportIds.Count == 0) return 0;
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var rows = await ctx.ExpenseReports
        .Where(r => reportIds.Contains(r.Id) && r.Status == ExpenseReportStatus.Approved)
        .ToListAsync(ct);
    foreach (var r in rows)
    {
        r.Status = ExpenseReportStatus.SepaSent;
        r.SepaSentAt = sepaSentAt;
        r.UpdatedAt = sepaSentAt;
    }
    await ctx.SaveChangesAsync(ct);
    return rows.Count;
}

public async Task<bool> MarkPaidAsync(
    Guid reportId, Instant paidAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports
        .FirstOrDefaultAsync(x => x.Id == reportId, ct);
    if (r is null || r.Status != ExpenseReportStatus.SepaSent) return false;
    r.Status = ExpenseReportStatus.Paid;
    r.PaidAt = paidAt;
    r.UpdatedAt = paidAt;
    await ctx.SaveChangesAsync(ct);
    return true;
}
```

- [ ] **Step 3: Run all repo tests**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseRepositoryTests"
```

Expected: PASS.

- [ ] **Step 4: Commit**

```
git add src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs
git commit -m "feat(expenses): repository workflow transitions

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3.9: Implement `ExpenseRepository` outbox methods

Adds: `GetUnprocessedOutboxAsync`, `GetFailedPermanentlyAsync`, `SetHoldedDocIdAsync`, `IncrementOutboxRetryAsync`, `MarkOutboxFailedPermanentlyAsync`, `MarkOutboxProcessedAsync`.

- [ ] **Step 1: Add tests**

```csharp
[HumansFact]
public async Task GetUnprocessedOutboxAsync_FiltersAndLimits()
{
    var ev1 = NewOutbox();
    var ev2 = NewOutbox(processedAt: Instant.FromUtc(2026, 5, 5, 0, 0));
    var ev3 = NewOutbox(failedPermanently: true);
    var ev4 = NewOutbox();
    await SeedOutbox(ev1, ev2, ev3, ev4);

    var got = await _sut.GetUnprocessedOutboxAsync(limit: 10);
    got.Should().HaveCount(2);
    got.Select(e => e.Id).Should().BeEquivalentTo(new[] { ev1.Id, ev4.Id });
}

[HumansFact]
public async Task SetHoldedDocIdAsync_StampsBoth_InOneTransaction()
{
    var report = MakeReport(status: ExpenseReportStatus.Approved);
    var outbox = NewOutbox(reportId: report.Id);
    await Seed(report);
    await SeedOutbox(outbox);

    await _sut.SetHoldedDocIdAsync(report.Id, "doc-123",
        outbox.Id, Instant.FromUtc(2026, 5, 5, 1, 0));

    var loaded = await _sut.GetByIdAsync(report.Id);
    loaded!.HoldedDocId.Should().Be("doc-123");

    await using var ctx = await _factory.CreateDbContextAsync();
    var loadedEvent = await ctx.HoldedExpenseOutboxEvents.FirstAsync(e => e.Id == outbox.Id);
    loadedEvent.ProcessedAt.Should().NotBeNull();
}

private static HoldedExpenseOutboxEvent NewOutbox(
    Guid? reportId = null,
    Instant? processedAt = null,
    bool failedPermanently = false) => new()
{
    Id = Guid.NewGuid(),
    ExpenseReportId = reportId ?? Guid.NewGuid(),
    EventType = HoldedExpenseOutboxEventType.CreateIncomingDoc,
    OccurredAt = Instant.FromUtc(2026, 5, 1, 0, 0),
    ProcessedAt = processedAt,
    FailedPermanently = failedPermanently
};

private async Task SeedOutbox(params HoldedExpenseOutboxEvent[] events)
{
    await using var ctx = await _factory.CreateDbContextAsync();
    ctx.HoldedExpenseOutboxEvents.AddRange(events);
    await ctx.SaveChangesAsync();
}
```

- [ ] **Step 2: Implement**

```csharp
public async Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
    int limit, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.HoldedExpenseOutboxEvents.AsNoTracking()
        .Where(e => e.ProcessedAt == null && !e.FailedPermanently)
        .OrderBy(e => e.OccurredAt)
        .Take(limit)
        .ToListAsync(ct);
}

public async Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetFailedPermanentlyAsync(
    CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.HoldedExpenseOutboxEvents.AsNoTracking()
        .Where(e => e.FailedPermanently)
        .OrderByDescending(e => e.OccurredAt)
        .ToListAsync(ct);
}

public async Task SetHoldedDocIdAsync(
    Guid reportId, string holdedDocId, Guid outboxEventId,
    Instant processedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var r = await ctx.ExpenseReports.FirstOrDefaultAsync(x => x.Id == reportId, ct);
    var ev = await ctx.HoldedExpenseOutboxEvents
        .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
    if (r is not null) r.HoldedDocId = holdedDocId;
    if (ev is not null)
    {
        ev.ProcessedAt = processedAt;
        ev.LastError = null;
    }
    await ctx.SaveChangesAsync(ct);
}

public async Task IncrementOutboxRetryAsync(
    Guid outboxEventId, string error, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var ev = await ctx.HoldedExpenseOutboxEvents
        .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
    if (ev is null) return;
    ev.RetryCount += 1;
    ev.LastError = error;
    await ctx.SaveChangesAsync(ct);
}

public async Task MarkOutboxFailedPermanentlyAsync(
    Guid outboxEventId, string error, Instant processedAt,
    CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var ev = await ctx.HoldedExpenseOutboxEvents
        .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
    if (ev is null) return;
    ev.FailedPermanently = true;
    ev.LastError = error;
    ev.ProcessedAt = processedAt;
    await ctx.SaveChangesAsync(ct);
}

public async Task MarkOutboxProcessedAsync(
    Guid outboxEventId, Instant processedAt, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var ev = await ctx.HoldedExpenseOutboxEvents
        .FirstOrDefaultAsync(e => e.Id == outboxEventId, ct);
    if (ev is null) return;
    ev.ProcessedAt = processedAt;
    await ctx.SaveChangesAsync(ct);
}
```

- [ ] **Step 3: Run tests**

```
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseRepositoryTests"
```

Expected: PASS.

- [ ] **Step 4: Push end-of-Phase-3**

```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet format Humans.slnx --verify-no-changes
git add -- src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs tests/Humans.Infrastructure.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs
git commit -m "feat(expenses): repository outbox methods

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
git push
```

**Phase 3 complete.** Clean PR target if you want to ship the data layer ahead of the UI.

---

# Phase 4 — Application services + DTOs + audit

Builds `IExpenseReportService` (the orchestrator that implements business rules + audit + outbox-event-id generation), DTOs, and the architecture test that pins the section's shape.

**PR boundary:** end of Phase 4 is a clean PR target.

> **Note on `IBudgetService` / `ITeamService`:** the service uses **only existing methods** on these interfaces — `IBudgetService.GetActiveYearAsync()`, `IBudgetService.GetCategoryByIdAsync(Guid)`, `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId)`, `ITeamService.IsUserCoordinatorOfTeamAsync(teamId, userId)`. Per `memory/architecture/interface-method-additions-are-debt.md`, do **not** add new interface methods unless explicitly approved. To check "does this user coordinate this category", load the category's `TeamId` and call the existing `IsUserCoordinatorOfTeamAsync`.

### Task 4.1: Define service interface + DTOs

**Files:**
- Create: `src/Humans.Application/Services/Expenses/Dtos/ExpenseReportDto.cs`
- Create: `src/Humans.Application/Services/Expenses/Dtos/ExpenseLineDto.cs`
- Create: `src/Humans.Application/Services/Expenses/Dtos/ExpenseAttachmentDto.cs`
- Create: `src/Humans.Application/Services/Expenses/Dtos/SepaConfig.cs`
- Create: `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs`

- [ ] **Step 1: Create the DTOs**

```csharp
// src/Humans.Application/Services/Expenses/Dtos/ExpenseReportDto.cs
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Expenses.Dtos;

public sealed record ExpenseReportDto
{
    public required Guid Id { get; init; }
    public required Guid SubmitterUserId { get; init; }
    public required Guid BudgetCategoryId { get; init; }
    public required Guid BudgetYearId { get; init; }
    public required ExpenseReportStatus Status { get; init; }
    public string? Note { get; init; }
    public required string PayeeName { get; init; }
    public required string PayeeIban { get; init; }
    public required decimal Total { get; init; }
    public Instant? SubmittedAt { get; init; }
    public Guid? CoordinatorEndorsedByUserId { get; init; }
    public Instant? CoordinatorEndorsedAt { get; init; }
    public Guid? ApprovedByUserId { get; init; }
    public Instant? ApprovedAt { get; init; }
    public Instant? SepaSentAt { get; init; }
    public Instant? PaidAt { get; init; }
    public string? LastRejectionReason { get; init; }
    public Guid? LastRejectedByUserId { get; init; }
    public Instant? LastRejectedAt { get; init; }
    public string? HoldedDocId { get; init; }
    public required Instant CreatedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
    public required IReadOnlyList<ExpenseLineDto> Lines { get; init; }
}

// src/Humans.Application/Services/Expenses/Dtos/ExpenseLineDto.cs
namespace Humans.Application.Services.Expenses.Dtos;

public sealed record ExpenseLineDto
{
    public required Guid Id { get; init; }
    public required Guid ExpenseReportId { get; init; }
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public Guid? AttachmentId { get; init; }
    public ExpenseAttachmentDto? Attachment { get; init; }
    public required int SortOrder { get; init; }
}

// src/Humans.Application/Services/Expenses/Dtos/ExpenseAttachmentDto.cs
using NodaTime;

namespace Humans.Application.Services.Expenses.Dtos;

public sealed record ExpenseAttachmentDto
{
    public required Guid Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string Extension { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required Guid UploadedByUserId { get; init; }
    public required Instant UploadedAt { get; init; }
}

// src/Humans.Application/Services/Expenses/Dtos/SepaConfig.cs
namespace Humans.Application.Services.Expenses.Dtos;

public sealed record SepaConfig
{
    public required string CreditorName { get; init; }
    public required string CreditorIban { get; init; }
    public required string CreditorBic { get; init; }
    /// <summary>Spanish NIF or other org tax id, used as initiating-party identifier.</summary>
    public required string CreditorIdentifier { get; init; }
    /// <summary>"SLEV" / "SHAR" / "DEBT" — service level for charge bearer in pain.001.</summary>
    public string ChargeBearer { get; init; } = "SLEV";
}
```

- [ ] **Step 2: Create `IExpenseReportService`**

```csharp
// src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseReportService
{
    Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetApprovedUnpaidAsync(CancellationToken ct = default);

    Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default);

    Task UpdateLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default);

    Task AttachToLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, Guid attachmentId,
        CancellationToken ct = default);

    Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default);

    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default);

    Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    Task<bool> CategoryOverrideAsync(
        Guid reportId, Guid actorUserId, Guid newCategoryId,
        CancellationToken ct = default);

    Task<int> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default);

    Task<bool> MarkPaidAsync(
        Guid reportId, CancellationToken ct = default);

    /// <summary>True iff the category has at least one budget coordinator
    /// (so the Submitted -> CoordinatorEndorsed step is required).</summary>
    Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Build**

```
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```
git add src/Humans.Application/Services/Expenses/Dtos/ src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs
git commit -m "feat(expenses): service interface and DTOs

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 4.2 — Task 4.9: Implement `ExpenseReportService` (TDD per method group)

> Implementation note: this is one task per logical method group. Each task has its own test+impl+commit cycle. Skipping the long-form repetition for brevity — the pattern is constant: write tests using the seeded `IExpenseRepository` (real, not mocked) + mocked `ITeamService` / `IBudgetService` / `IAuditLogService`, then implement, then commit.

The full service test class lives at:
`tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

It uses an in-memory `HumansDbContext` factory (same pattern as `ExpenseRepositoryTests` from Task 3.6) and constructs the SUT as:

```csharp
_sut = new ExpenseReportService(
    _expenseRepo,
    Substitute.For<IBudgetService>(),
    Substitute.For<ITeamService>(),
    Substitute.For<IAuditLogService>(),
    new FakeClock(Instant.FromUtc(2026, 5, 10, 12, 0)),
    NullLogger<ExpenseReportService>.Instance);
```

For each task below, write the test first, run, fail, implement, run, pass, commit.

**Task 4.2 — `CreateDraftAsync` + `UpdateDraftAsync` + `GetAsync` + `GetForSubmitterAsync`**

Tests verify: the draft is created with the active budget year resolved via `IBudgetService.GetActiveYearAsync()`, status is `Draft`, `Total` is 0, audit-log writes `AuditAction.ExpenseSubmit` is **not** triggered yet (creation isn't a submit). `UpdateDraftAsync` rejects non-submitter actors. `GetAsync` returns null for missing.

Implementation pattern:

```csharp
public async Task<Guid> CreateDraftAsync(
    Guid submitterUserId, Guid budgetCategoryId, string? note,
    CancellationToken ct = default)
{
    var year = await _budgetService.GetActiveYearAsync(ct)
        ?? throw new InvalidOperationException("No active budget year.");
    var category = year.Groups.SelectMany(g => g.Categories)
        .FirstOrDefault(c => c.Id == budgetCategoryId)
        ?? throw new InvalidOperationException("Category not in active year.");

    var now = _clock.GetCurrentInstant();
    var report = new ExpenseReport
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = submitterUserId,
        BudgetCategoryId = category.Id,
        BudgetYearId = year.Id,
        Status = ExpenseReportStatus.Draft,
        Note = note,
        PayeeName = "",
        PayeeIban = "",
        Total = 0m,
        CreatedAt = now,
        UpdatedAt = now
    };
    await _repo.AddDraftAsync(report, ct);
    return report.Id;
}
```

`GetAsync` and `GetForSubmitterAsync` map repository entities to DTOs via a private static `ToDto(ExpenseReport)` helper.

Commit: `feat(expenses): service create + read methods`.

**Task 4.3 — Line and attachment methods**

`AddLineAsync`, `UpdateLineAsync`, `RemoveLineAsync`, `AttachToLineAsync`. All four enforce: only the submitter can mutate, and only when the report is in `{Draft, Submitted, CoordinatorEndorsed}`. Reverting `CoordinatorEndorsed` to `Submitted` happens in this layer — when a line/attachment edit lands while in `CoordinatorEndorsed`, the service first calls a new repository method or simply re-flips status via `UpdateDraftAsync`-shaped extension. **Implementation choice:** add the revert to `IExpenseRepository.RevertEndorsementAsync(Guid reportId, Instant updatedAt)` — confirm with Peter before adding (interface-method-additions-are-debt). For now, fold it into `AddLineAsync` / `RemoveLineAsync` etc. in the repository directly: they already have a tracked `ExpenseReport` in scope, so set `Status = Submitted` if it was `CoordinatorEndorsed`, alongside total recompute.

Updated repository code in `AddLineAsync` (and `UpdateLineAsync`/`RemoveLineAsync`):

```csharp
if (report.Status == ExpenseReportStatus.CoordinatorEndorsed)
{
    report.Status = ExpenseReportStatus.Submitted;
    report.CoordinatorEndorsedAt = null;
    report.CoordinatorEndorsedByUserId = null;
}
```

Commit: `feat(expenses): service line + attachment methods with endorsement revert`.

**Task 4.4 — `SubmitAsync` + `WithdrawAsync`**

`SubmitAsync` validates: actor == submitter, status == Draft, ≥ 1 line, every line has an attachment, submitter's `Profile.Iban` is set, snapshots `PayeeName` (from `IUserService` display name) + `PayeeIban` (from `Profile.Iban`). Calls `_repo.SubmitAsync`. Writes audit `AuditAction.ExpenseSubmit`.

`WithdrawAsync` requires actor == submitter and current status ∈ withdrawable set. Calls `_repo.WithdrawAsync`. Writes audit `AuditAction.ExpenseWithdraw`.

Commit: `feat(expenses): service submit + withdraw with validation`.

**Task 4.5 — `CategoryRequiresCoordinatorEndorsementAsync`**

Loads the category via `IBudgetService.GetCategoryByIdAsync(categoryId)`. Returns `false` if `category.TeamId is null`, otherwise returns whether the team has at least one coordinator.

**Resolution path (per Peter's direction):** by the time this task is reached, `ITeamService` will already have a `TeamInfo` object on its surface (the team-side equivalent of `FullProfile`). Add a `Coordinators` collection (returning coordinator user `Guid`s) to `TeamInfo` rather than introducing a new `ITeamService.HasBudgetCoordinatorsAsync` method.

Implementation:
```csharp
public async Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
    Guid categoryId, CancellationToken ct = default)
{
    var category = await _budgetService.GetCategoryByIdAsync(categoryId, ct);
    if (category?.TeamId is null) return false;
    var teamInfo = await _teamService.GetTeamInfoAsync(category.TeamId.Value, ct);
    return teamInfo?.Coordinators.Count > 0;
}
```

If `TeamInfo` is not yet on `ITeamService` when this task runs, **stop and confirm with Peter** before either adding it or extending it. The `TeamInfo.Coordinators` field is the agreed extension point.

Commit: `feat(expenses): coordinator-required determination via TeamInfo`.

**Task 4.6 — `CoordinatorEndorseAsync` + `CoordinatorRejectAsync`**

Authz check: `category.TeamId.HasValue && _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId.Value, coordinatorUserId)`. Both methods write audit (`ExpenseEndorse` / `ExpenseCoordinatorReject`).

Commit: `feat(expenses): coordinator endorse + reject`.

**Task 4.7 — `ApproveAsync` + `FinanceRejectAsync` + `CategoryOverrideAsync`**

`ApproveAsync` generates a fresh `outboxEventId = Guid.NewGuid()` and passes it to `_repo.ApproveAsync` so the repository writes both the status flip and outbox row in the same transaction. Audit `ExpenseApprove`. If `overrideCategoryId` differs from current, additionally audit `ExpenseCategoryOverride`.

`FinanceRejectAsync` audits `ExpenseReject`.

`CategoryOverrideAsync` (post-approval) audits `ExpenseCategoryOverride` and queues an `UpdateIncomingDocTag` outbox event.

Commit: `feat(expenses): finance approve / reject / category override`.

**Task 4.8 — `MarkSepaSentAsync` + `MarkPaidAsync` + `GetApprovedUnpaidAsync` + `GetReviewQueueAsync` + `GetCoordinatorQueueAsync`**

`MarkSepaSentAsync` is multi-id atomic. Calls repo, then writes one audit per report.

`MarkPaidAsync` (called from the polling job) writes `ExpensePaid` audit.

The three queue methods translate repo results to DTOs.

Commit: `feat(expenses): SEPA + paid + queues`.

**Task 4.9 — Architecture test for the section**

Create `tests/Humans.Application.Tests/Architecture/ExpensesArchitectureTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Services.Expenses;
using Humans.Tests.Common;

namespace Humans.Application.Tests.Architecture;

public class ExpensesArchitectureTests
{
    [HumansFact]
    public void IExpenseReportService_LivesIn_ExpensesNamespace()
    {
        typeof(IExpenseReportService).Namespace
            .Should().Be("Humans.Application.Interfaces.Expenses");
    }

    [HumansFact]
    public void ExpenseReportService_DoesNotReferenceEFCore()
    {
        var asm = typeof(ExpenseReportService).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore");
    }

    [HumansFact]
    public void ExpenseReportService_Constructor_HasNoCrossSectionRepositories()
    {
        var ctor = typeof(ExpenseReportService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        // Allowed: own repository, application services, IClock, ILogger.
        // Forbidden: any *Repository other than IExpenseRepository.
        var forbidden = paramTypes
            .Where(t => t.Name.EndsWith("Repository") && t.Name != "IExpenseRepository")
            .ToList();
        forbidden.Should().BeEmpty();
    }
}
```

Commit: `test(expenses): architecture pin`.

End of Phase 4 — push and stop.

---

# Phase 5 — Filesystem attachment storage

Implements `IExpenseAttachmentStorageService` against the local filesystem with the configured root.

### Task 5.1: Define interface

**Files:**
- Create: `src/Humans.Application/Interfaces/Expenses/IExpenseAttachmentStorageService.cs`

```csharp
namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseAttachmentStorageService
{
    /// <summary>Persists the stream and returns the new attachment id.</summary>
    Task<Guid> StoreAsync(
        Stream content, string extension, string contentType,
        CancellationToken ct = default);

    /// <summary>Opens a stream over the attachment bytes. Caller disposes.</summary>
    Task<Stream> OpenReadAsync(
        Guid id, string extension, CancellationToken ct = default);

    /// <summary>Deletes the on-disk file. Idempotent.</summary>
    Task DeleteAsync(Guid id, string extension, CancellationToken ct = default);
}
```

Commit: `feat(expenses): IExpenseAttachmentStorageService surface`.

### Task 5.2: Implement filesystem storage with tests

**Files:**
- Create: `src/Humans.Infrastructure/Services/Expenses/ExpenseAttachmentFilesystemStorageOptions.cs`
- Create: `src/Humans.Infrastructure/Services/Expenses/ExpenseAttachmentFilesystemStorage.cs`
- Test: `tests/Humans.Infrastructure.Tests/Services/Expenses/ExpenseAttachmentFilesystemStorageTests.cs`

Tests should cover:
- Round-trip: `StoreAsync` then `OpenReadAsync` yields the same bytes.
- `DeleteAsync` on non-existent id is a no-op (no exception).
- Path traversal is impossible: a malicious extension like `"../../etc/passwd"` is rejected with `ArgumentException`.
- `OpenReadAsync` for an unknown id throws `FileNotFoundException`.
- The configured root is created if it doesn't exist.

```csharp
// Options
public sealed class ExpenseAttachmentFilesystemStorageOptions
{
    public const string Section = "ExpenseAttachments";
    public string Root { get; set; } = "/var/lib/humans/expense-attachments";
    public long MaxBytes { get; set; } = 20 * 1024 * 1024; // 20 MB
}
```

Implementation: validate extension against an allowlist (`.pdf`, `.jpg`, `.jpeg`, `.png`, `.heic`); ensure root exists (`Directory.CreateDirectory`); compose path as `Path.Combine(root, $"{id}{extension}")`; reject paths whose canonical form escapes root (`Path.GetFullPath(path).StartsWith(root)`).

Tests run against a `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())` root that's created in `InitializeAsync` and deleted in `DisposeAsync`.

Commit: `feat(expenses): filesystem attachment storage with traversal guard`.

### Task 5.3: DI registration in `ExpensesSectionExtensions`

**Files:**
- Create: `src/Humans.Web/Extensions/Sections/ExpensesSectionExtensions.cs`
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

```csharp
public static IServiceCollection AddExpensesSection(
    this IServiceCollection services, IConfiguration config)
{
    services.Configure<ExpenseAttachmentFilesystemStorageOptions>(
        config.GetSection(ExpenseAttachmentFilesystemStorageOptions.Section));
    services.AddSingleton<IExpenseAttachmentStorageService,
        ExpenseAttachmentFilesystemStorage>();
    services.AddSingleton<IExpenseRepository, ExpenseRepository>();
    services.AddScoped<IExpenseReportService, ExpenseReportService>();
    return services;
}
```

Wire into `AddHumansInfrastructure` next to `AddHoldedSection`. Commit + push end of Phase 5.

---

# Phase 6 — Submitter UI

Builds the member-facing controller actions, view models, and views: `/Expenses`, `/Expenses/New`, `/Expenses/{id}`, `/Expenses/{id}/Edit`, `/Expenses/{id}/Submit`, `/Expenses/{id}/Withdraw`, `/Expenses/{id}/Iban`, `/Expenses/Attachment/{id}`.

This is a large phase but consists of mechanical Razor + controller work. Each route gets one task, structured as: write controller action, write view, build, manually exercise via `dotnet run --project src/Humans.Web`, commit.

**Tasks (one per route):** 6.1 Index, 6.2 New, 6.3 Detail, 6.4 Edit (incl. attachments POST), 6.5 Submit POST, 6.6 Withdraw POST, 6.7 Iban GET+POST, 6.8 Attachment GET.

Common controller scaffold:

```csharp
// src/Humans.Web/Controllers/ExpensesController.cs
[Authorize]
[Route("Expenses")]
public sealed class ExpensesController : HumansControllerBase
{
    private readonly IExpenseReportService _service;
    private readonly IExpenseAttachmentStorageService _storage;
    private readonly IBudgetService _budgetService;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IAuthorizationService _authService;
    private readonly ILogger<ExpensesController> _logger;

    public ExpensesController(
        UserManager<User> userManager,
        IExpenseReportService service,
        IExpenseAttachmentStorageService storage,
        IBudgetService budgetService,
        IUserService userService,
        IProfileService profileService,
        IAuthorizationService authService,
        ILogger<ExpensesController> logger) : base(userManager)
    {
        _service = service; _storage = storage;
        _budgetService = budgetService; _userService = userService;
        _profileService = profileService;
        _authService = authService; _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index() { /* ... */ }
    // etc.
}
```

Commit each route. Push end of Phase 6.

> Per the **CLAUDE.md** "If UI changes, exercise in browser" rule, **after** each route lands, run `dotnet run --project src/Humans.Web` and exercise that route at `http://localhost:<port>/Expenses/...`. Verify the golden path and obvious edge cases (no IBAN → modal pops; no active year → friendly empty state; non-submitter visiting another's report → 403).

---

# Phase 7 — Coordinator + FinanceAdmin UI + authorization

Adds `/Expenses/Coordinator`, `/Expenses/Review`, the Endorse/CoordinatorReject/Approve/Reject POSTs, the SEPA generation route stub (real impl in Phase 9), and the resource-based `ExpenseReportAuthorizationHandler` + `IbanAccessHandler`.

**Tasks:** 7.1 `ExpenseReportOperationRequirement`, 7.2 `ExpenseReportAuthorizationHandler`, 7.3 `IbanAccessRequirement` + handler, 7.4 register in `AuthorizationPolicyExtensions`, 7.5 Coordinator queue + view, 7.6 Coord endorse/reject POSTs, 7.7 Review queue + view, 7.8 Approve/Reject POSTs.

Each authorization handler test in `tests/Humans.Application.Tests` (or a Web-test project if one exists — verify) covers the matrix: submitter / coordinator-of-this-category / coordinator-of-other-category / FinanceAdmin / Admin / random user × {View, Edit, Endorse, Approve}.

Push end of Phase 7.

---

# Phase 8 — Holded outbox job

Implements `HoldedExpenseOutboxJob` against `IHoldedClient` and the outbox repository methods. Every approved report becomes a Holded purchase doc.

### Task 8.1: Implement `HoldedExpenseOutboxJob`

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/HoldedExpenseOutboxJob.cs`
- Test: `tests/Humans.Infrastructure.Tests/Jobs/HoldedExpenseOutboxJobTests.cs`

Behavior:
1. Pull up to 100 unprocessed events.
2. For each: load report (with lines + attachments) via `IExpenseRepository.GetByIdWithLinesAsync`.
3. Resolve category tag via `IBudgetService.GetCategoryByIdAsync` → `{group.Slug}-{category.Slug}`.
4. Resolve submitter display name via `IUserService.GetByIdsAsync`.
5. For `CreateIncomingDoc`: call `IHoldedClient.CreatePurchaseDocumentAsync` → for each attachment, `IExpenseAttachmentStorageService.OpenReadAsync` then `IHoldedClient.UploadAttachmentAsync` → call `IExpenseRepository.SetHoldedDocIdAsync`.
6. For `UpdateIncomingDocTag`: call `IHoldedClient.UpdatePurchaseDocumentTagsAsync(report.HoldedDocId!, [tag])` → `MarkOutboxProcessedAsync`.
7. On `HoldedTransientException`: `IncrementOutboxRetryAsync(error)`.
8. On `HoldedPermanentException`: `MarkOutboxFailedPermanentlyAsync(error, now)`.

Tests use `Substitute.For<IHoldedClient>()` to assert the full call sequence and the per-error transition.

### Task 8.2: Recurring job registration

In `src/Humans.Web/Extensions/RecurringJobExtensions.cs`:

```csharp
RecurringJob.AddOrUpdate<HoldedExpenseOutboxJob>(
    "holded-expense-outbox",
    job => job.ExecuteAsync(CancellationToken.None),
    "*/1 * * * *");
```

DI registration: add `services.AddScoped<HoldedExpenseOutboxJob>()` to `ExpensesSectionExtensions`.

Push end of Phase 8.

---

# Phase 9 — SEPA generation

Builds `ISepaPaymentFileBuilder` and the `/Expenses/Sepa/Generate` POST.

### Task 9.1: `ISepaPaymentFileBuilder` + impl

**Files:**
- Create: `src/Humans.Application/Interfaces/Expenses/ISepaPaymentFileBuilder.cs`
- Create: `src/Humans.Application/Services/Expenses/SepaPaymentFileBuilder.cs`
- Test: `tests/Humans.Application.Tests/Services/Expenses/SepaPaymentFileBuilderTests.cs`

Interface:

```csharp
public interface ISepaPaymentFileBuilder
{
    string BuildPain001(
        SepaConfig config,
        Instant generatedAt,
        IReadOnlyList<ExpenseReportDto> reports);
}
```

Implementation: builds a pain.001.001.09 XML document (ISO 20022). Reference: any open-source pain.001 example. Per-report fields: report id as `EndToEndId`, payee name, payee IBAN, amount = `report.Total`, remittance info = `"Expense {shortId}"`. Group header with `MessageId = $"EXP-{generatedAt:yyyyMMddHHmmss}"`, control sum = sum of totals, payment-info-id same as message id, charge bearer from config.

Tests: parse the XML back via `XDocument`, assert root element, group header structure, one `CdtTrfTxInf` per report, IBANs/amounts present.

### Task 9.2: `SepaConfig` registration

`SepaConfig` bound from `appsettings.json` `Sepa:*` plus an env var override for the IBAN value (so production overrides without committing the IBAN). Wire in `ExpensesSectionExtensions`.

### Task 9.3: `/Expenses/Sepa/Generate` controller action

POST handler: receive a list of report ids in the form body, call `_service.MarkSepaSentAsync(ids, actorUserId)`, then `_sepaBuilder.BuildPain001(...)`, return as `File(bytes, "application/xml", $"sepa-{now:yyyy-MM-dd-HHmm}.xml")`. Resource auth: each id in the list must be `Approved` and the actor must be FinanceAdmin/Admin.

Push end of Phase 9.

---

# Phase 10 — Paid polling + Admin IBAN reveal + GDPR + section docs

### Task 10.1: `ExpensePaidPollingJob`

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/ExpensePaidPollingJob.cs`
- Test: `tests/Humans.Infrastructure.Tests/Jobs/ExpensePaidPollingJobTests.cs`

Behavior:
1. Pull `SepaSent` reports (cap 50 per run; oldest `SepaSentAt` first).
2. For each: `IHoldedClient.GetPurchaseDocumentAsync(report.HoldedDocId!)`.
3. If `doc.PaymentsPending == 0 && doc.ApprovedAt is not null`: call `IExpenseReportService.MarkPaidAsync`.
4. On `HoldedPermanentException` 404: log warning ("doc deleted out-of-band"), do not transition.
5. On `HoldedTransientException`: log + continue.

Register in `RecurringJobExtensions` at `*/15 * * * *`.

### Task 10.2: Admin IBAN reveal on `/Admin/Users/{id}`

Locate the existing user-admin view (look for `AdminUserController` or similar). Add a "Payment details" section showing `IbanFormatter.Mask(user.Profile.Iban)` by default. Add a `[HttpPost("Admin/Users/{id}/RevealIban")]` action that requires `Admin` role, returns the unmasked IBAN to the page, and writes one `AuditLogEntry` with `AuditAction.IbanReveal`. Mark the reveal as session-scoped — re-page-load returns to masked.

### Task 10.3: GDPR contributor

In `ExpenseReportService` (or a partial), implement `IUserDataContributor.ContributeForUserAsync`:
- Resolve merge-source ids via `IUserService.GetMergedSourceIdsAsync`.
- For each id, fetch reports + lines (no attachment bytes — reference URLs only).
- Include a snapshot of `Profile.Iban` (masked in the export, per the spec — verify Peter's preference at this point).
- Return `IReadOnlyList<UserDataSlice>`.

Update DI to dual-register: both `IExpenseReportService` and `IUserDataContributor` resolve to the concrete `ExpenseReportService`.

### Task 10.4: `docs/sections/Expenses.md`

Create the section invariant doc following `docs/sections/SECTION-TEMPLATE.md`. Concepts, data model (concise — point at the spec for full detail), actors/roles, invariants, negative access rules, triggers, cross-section deps, architecture status. Should be terser than the spec — about half its length.

### Task 10.5: Memory atom — IBAN logging rule

Create `memory/code/iban-mask-in-logs.md` capturing: "All IBAN output to logs / audit / errors goes through `IbanFormatter.Mask`. Reason: Spanish data protection + GDPR; raw IBAN is personal financial data. How to apply: search-and-destroy on any log statement that interpolates `Profile.Iban` or `ExpenseReport.PayeeIban`." Add a one-line entry to `memory/INDEX.md`.

### Task 10.6: Final push and PR

```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet format Humans.slnx --verify-no-changes
git push
gh pr create --title "feat: Expenses + Holded sections (full)" \
  --body "<summary linking to spec + plan>"
```

**End of plan.**

---

## Self-review notes

- **Spec coverage:** every concept, route, lifecycle transition, invariant, and negative access rule in `2026-05-10-expense-reports-design.md` maps to a numbered task above. The Holded back-flow is Phase 10 (`ExpensePaidPollingJob`); the outbox is Phase 8; the SEPA generator is Phase 9; the section docs (both Expenses and Holded) are Phase 2.5 and 10.4 respectively.
- **Holded scope deviation from spec text:** the spec was amended (see commit `83bccfed`) so that `IFinanceService` and the `holded_transactions` table dependency are out of v1 scope; Holded is its own sibling section. The plan reflects that amendment.
- **Pause-point flagged:** Task 4.5 (`CategoryRequiresCoordinatorEndorsementAsync`) needs a new `ITeamService` method or a near-equivalent. The plan instructs the executor to confirm with Peter before adding the method.
- **Test framework calibrated:** every test file uses `[HumansFact]`/`[HumansTheory]`, AwesomeAssertions, NSubstitute, NodaTime.Testing. Architecture tests follow `CampsArchitectureTests` shape.
- **Cron format:** all cron expressions in the plan are 5-field (`*/1 * * * *`, `*/15 * * * *`), matching the existing `RecurringJobExtensions.cs` pattern, not 6-field.
- **DI patterns:** repositories registered as `Singleton`, services as `Scoped`, jobs as `Scoped`. Dual-interface registration used for the GDPR contributor in Phase 10.3.
- **Migration discipline:** every migration task includes the EF migration reviewer agent step per `memory/process/ef-migration-review-gate.md`.
