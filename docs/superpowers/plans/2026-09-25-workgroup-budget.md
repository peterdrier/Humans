# Workgroup Budget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Board/Admin give a workgroup a budget amount and a dedicated Holded expense account, created or linked through Finance without ever producing a duplicate account.

**Architecture:** Workgroups owns the amount and the account reference (three nullable columns). Finance owns account creation, naming, numbering and a registry of the accounts it created outside the budget map, so its doc sync keeps those docs off the Unmatched queue. No section learns what a workgroup is. Phase 2 (Expenses) is a later PR and not in this plan.

**Tech Stack:** ASP.NET Core 10 MVC, EF Core (Postgres, per-section DbContext), NodaTime, xUnit + AwesomeAssertions + NSubstitute, resx localization (en, es, de, it, fr, ca).

**Spec:** `docs/features/global/workgroup-budget.md`

**Worktree:** `H:\source\humans\.claude\worktrees\workgroup-budget`, branch `feat/workgroup-budget`. Every command below runs from that directory. Never `cd` elsewhere.

## Global Constraints

- Account name for a created account is exactly `Workgroups / {workgroup.Name}`.
- Number block for created accounts starts at `62900100`, shared with budget categories; collisions are avoided against the local category map, the local managed registry and the live Holded chart.
- Name matching is normalized: trim, collapse internal whitespace to one space, case-insensitive, accents folded (é == e).
- Budget setting is `BoardOrAdmin` only; a member or coordinator cannot reach it.
- The budget block on the group page is visible to Board/Admin and to viewers whose `MembershipTier` is `Colaborador` or `Asociado`; hidden from `Volunteer`.
- Every user-visible string on the member-facing group page lives in `WorkgroupsResource.*.resx` for all six cultures. Admin-only form text under `/Workgroups/Admin/*` (and the admin form embedded in the group page for Board/Admin) is localization-exempt.
- Migrations are generated with `dotnet ef migrations add … --context <C> --output-dir Data/Migrations --project <section> --startup-project src/Humans.Web`, committed verbatim, never hand-edited.
- `dotnet build`/`dotnet test` always with `-v quiet`.
- Never `cd <dir> && cmd` in one Bash call. Never `git -C`.
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

1. **A name that already exists in Holded but with different casing or an accent** ("workgroups / ALM 2027" vs "Workgroups / ALM 2027") must link, not create. Pinned in Task 2.
2. **Two admins clicking "create" for the same group within seconds.** The second call finds the first's account by name and links it; no twin. Pinned in Task 2 (name match runs before allocation).
3. **Clearing the budget must not drop the account link**, otherwise a later re-budget creates a second account for the same group. Pinned in Task 7.
4. **A Holded outage during Close/Reactivate must not block the lifecycle step.** Pinned in Task 8.
5. **A doc booked to a managed account must not appear on `/Finance/HoldedUnmatched`** and must not appear in a budget year's actuals. Pinned in Task 4.

---

### Task 1: Finance — managed-account registry (entity, table, repository)

**Files:**
- Create: `src/Sections/Humans.Finance/Domain/HoldedManagedAccount.cs`
- Create: `src/Sections/Humans.Finance/Data/Configurations/HoldedManagedAccountConfiguration.cs`
- Modify: `src/Sections/Humans.Finance/Data/FinanceDbContext.cs`
- Modify: `src/Sections/Humans.Finance/Data/IHoldedRepository.cs`
- Modify: `src/Sections/Humans.Finance/Data/Repository.cs`
- Create (generated): `src/Sections/Humans.Finance/Data/Migrations/<timestamp>_HoldedManagedAccounts.cs` + `.Designer.cs`, snapshot updated
- Test: `tests/Humans.Finance.Tests/RepositoryTests.cs`

**Interfaces:**
- Produces: `internal sealed class HoldedManagedAccount { Guid Id; int HoldedAccountNumber; string HoldedAccountId; string Label; bool IsActive; Instant CreatedAt; Instant UpdatedAt; }`
- Produces on `IHoldedRepository`:
  - `Task<IReadOnlyList<HoldedManagedAccount>> GetManagedAccountsAsync(CancellationToken ct = default)`
  - `Task UpsertManagedAccountAsync(HoldedManagedAccount row, CancellationToken ct = default)` — keyed on `HoldedAccountNumber`; an existing row keeps its `Id` and `CreatedAt`, takes `Label`, `HoldedAccountId`, `IsActive`, `UpdatedAt`.

- [x] **Step 1: Write the failing repository test**

Append to `tests/Humans.Finance.Tests/RepositoryTests.cs` (inside the class, uses the existing `Make()` helper):

```csharp
    [HumansFact]
    public async Task ManagedAccounts_UpsertByNumber_KeepsIdAndCreatedAt_ReplacesTheRest()
    {
        var (repo, _) = Make();
        var t0 = Instant.FromUtc(2026, 9, 1, 0, 0);
        var first = new HoldedManagedAccount
        {
            Id = Guid.NewGuid(), HoldedAccountNumber = 62900150, HoldedAccountId = "acc-1",
            Label = "Workgroups / ALM 2027", IsActive = true, CreatedAt = t0, UpdatedAt = t0,
        };
        await repo.UpsertManagedAccountAsync(first, TestContext.Current.CancellationToken);

        var t1 = t0.Plus(Duration.FromDays(1));
        await repo.UpsertManagedAccountAsync(new HoldedManagedAccount
        {
            Id = Guid.NewGuid(), HoldedAccountNumber = 62900150, HoldedAccountId = "acc-1",
            Label = "Workgroups / ALM 2027", IsActive = false, CreatedAt = t1, UpdatedAt = t1,
        }, TestContext.Current.CancellationToken);

        var rows = await repo.GetManagedAccountsAsync(TestContext.Current.CancellationToken);
        var row = rows.Should().ContainSingle().Which;
        row.Id.Should().Be(first.Id);
        row.CreatedAt.Should().Be(t0);
        row.UpdatedAt.Should().Be(t1);
        row.IsActive.Should().BeFalse();
    }
```

Add `using NodaTime;` and `using Humans.Finance.Domain;` at the top if missing.

- [x] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~ManagedAccounts_UpsertByNumber"
```
Expected: build error, `HoldedManagedAccount` not defined.

- [x] **Step 3: Add the entity**

`src/Sections/Humans.Finance/Domain/HoldedManagedAccount.cs`:

```csharp
using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// An expense account Finance created or linked outside the budget category map — for a
/// caller that needs "an account to book to" without a BudgetCategory behind it. Finance
/// records only the account: which caller wanted it is that caller's business. The registry
/// is what keeps these accounts' purchase docs off the Unmatched queue, and what lets a
/// retired account drop out of the pickers.
/// </summary>
internal sealed class HoldedManagedAccount
{
    public Guid Id { get; init; }
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
```

- [x] **Step 4: Add the configuration and register it**

`src/Sections/Humans.Finance/Data/Configurations/HoldedManagedAccountConfiguration.cs`:

```csharp
using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Finance.Data.Configurations;

internal sealed class HoldedManagedAccountConfiguration : IEntityTypeConfiguration<HoldedManagedAccount>
{
    public void Configure(EntityTypeBuilder<HoldedManagedAccount> b)
    {
        b.ToTable("holded_managed_accounts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedAccountNumber).IsUnique();
        b.HasIndex(x => x.HoldedAccountId).IsUnique();
        b.Property(x => x.HoldedAccountId).HasMaxLength(64);
        b.Property(x => x.Label).HasMaxLength(200);
    }
}
```

In `FinanceDbContext.cs` add the DbSet and the configuration:

```csharp
    public DbSet<HoldedManagedAccount> HoldedManagedAccounts => Set<HoldedManagedAccount>();
```
```csharp
        builder.ApplyConfiguration(new HoldedManagedAccountConfiguration());
```
Update the class summary's table count ("the four holded_* tables" → "the five holded_* tables").

- [x] **Step 5: Repository interface and implementation**

In `IHoldedRepository.cs`, after the category-map block:

```csharp
    // Managed accounts (expense accounts created outside the budget map)
    Task<IReadOnlyList<HoldedManagedAccount>> GetManagedAccountsAsync(CancellationToken ct = default);

    /// <summary>Insert or, when a row already carries this account number, update its label, id and
    /// active flag. Keyed on the number because that is what Holded and every caller identify the
    /// account by.</summary>
    Task UpsertManagedAccountAsync(HoldedManagedAccount row, CancellationToken ct = default);
```

In `Repository.cs`, after `AddCategoryMapAsync`:

```csharp
    // ── Managed accounts ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedManagedAccount>> GetManagedAccountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedManagedAccounts.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertManagedAccountAsync(HoldedManagedAccount row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedManagedAccounts
            .SingleOrDefaultAsync(a => a.HoldedAccountNumber == row.HoldedAccountNumber, ct);
        if (existing is null)
        {
            ctx.HoldedManagedAccounts.Add(row);
        }
        else
        {
            existing.HoldedAccountId = row.HoldedAccountId;
            existing.Label = row.Label;
            existing.IsActive = row.IsActive;
            existing.UpdatedAt = row.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }
```

- [x] **Step 6: Generate the migration**

```bash
dotnet ef migrations add HoldedManagedAccounts --context FinanceDbContext --output-dir Data/Migrations --project src/Sections/Humans.Finance --startup-project src/Humans.Web
```
Then `git diff --stat src/Sections/Humans.Finance/Data/Migrations` — expect one new migration pair plus a snapshot diff that only adds `holded_managed_accounts`. If the snapshot diff touches anything else, stop and report.

- [x] **Step 7: Run the test**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~ManagedAccounts_UpsertByNumber"
```
Expected: PASS.

- [x] **Step 8: Commit**

```bash
git add src/Sections/Humans.Finance tests/Humans.Finance.Tests/RepositoryTests.cs
git commit -m "feat(Finance): managed-account registry table and repository

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Finance — `CreateOrLinkExpenseAccountAsync`

**Files:**
- Modify: `src/Sections/Humans.Finance.Contracts/HoldedDtos.cs`
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceService.cs`
- Modify: `src/Sections/Humans.AuditLog.Contracts/AuditAction.cs`
- Modify: `src/Sections/Humans.Finance/Services/HoldedMatcher.cs` (add `NormalizeAccountName`)
- Modify: `src/Sections/Humans.Finance/Services/Service.cs`
- Test: `tests/Humans.Finance.Tests/HoldedMatcherTests.cs`, `tests/Humans.Finance.Tests/ServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's repository methods; `IHoldedClient.ListExpenseAccountsAsync`, `IHoldedClient.CreateExpenseAccountAsync(int, string)`.
- Produces:
  - `public sealed record HoldedExpenseAccountRef(int AccountNum, string AccountId, string Name, bool Created);`
  - `IHoldedFinanceService.CreateOrLinkExpenseAccountAsync(string name, int? existingAccountNum, CancellationToken ct = default)` → `Task<HoldedExpenseAccountRef>`
  - `HoldedMatcher.NormalizeAccountName(string?)` → `string`
  - `Service.ExpenseAccountBlockStart = 62900100` (internal const)
  - `AuditAction.HoldedExpenseAccountCreated`, `AuditAction.HoldedExpenseAccountLinked`

- [x] **Step 1: Failing test for name normalization**

Append to `tests/Humans.Finance.Tests/HoldedMatcherTests.cs`:

```csharp
    [HumansTheory]
    [InlineData("Workgroups / ALM 2027", "workgroups / alm 2027")]
    [InlineData("  Workgroups  /  ALM   2027 ", "workgroups / alm 2027")]
    [InlineData("Workgroups / Asamblea Éxito", "workgroups / asamblea exito")]
    [InlineData(null, "")]
    public void NormalizeAccountName_TrimsCollapsesLowersAndFoldsAccents(string? raw, string expected)
    {
        HoldedMatcher.NormalizeAccountName(raw).Should().Be(expected);
    }
```

- [x] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~NormalizeAccountName"
```
Expected: build error, method missing.

- [x] **Step 3: Implement `NormalizeAccountName`**

In `HoldedMatcher.cs`, after `NormalizeTag`:

```csharp
    /// <summary>Trim, collapse whitespace runs to one space, lowercase, fold accents. Two account
    /// names that normalize equal are the same account as far as dedup is concerned.</summary>
    public static string NormalizeAccountName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
```
Add `using System.Globalization;` to the file.

- [x] **Step 4: Run the normalization test**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~NormalizeAccountName"
```
Expected: PASS.

- [x] **Step 5: Failing service tests**

Append to `tests/Humans.Finance.Tests/ServiceTests.cs` (class `HoldedFinanceServiceTests`):

```csharp
    // ─── CreateOrLinkExpenseAccount ──────────────────────────────────────────────

    private void NoManagedAccounts() =>
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>());

    private void NoCategoryMap() =>
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCategoryMap>());

    private void LiveChart(params (int Num, string Id, string Name)[] accounts) =>
        _client.ListExpenseAccountsAsync(Arg.Any<CancellationToken>()).Returns(
            accounts.Select(a => new HoldedExpenseAccountDto { AccountNum = a.Num, Id = a.Id, Name = a.Name }).ToList());

    [HumansFact]
    public async Task CreateOrLink_NoMatch_CreatesAtNextFreeNumberAndRegisters()
    {
        NoManagedAccounts();
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCategoryMap>
        {
            new() { Id = Guid.NewGuid(), BudgetCategoryId = Guid.NewGuid(), HoldedAccountNumber = 62900100, HoldedAccountId = "cat-0", Tag = "x" },
        });
        LiveChart((62900100, "cat-0", "Departments / Geeks"), (62900101, "manual-1", "Something by hand"));
        _client.CreateExpenseAccountAsync(62900102, "Workgroups / ALM 2027", Arg.Any<CancellationToken>()).Returns("new-1");

        var result = await MakeService().CreateOrLinkExpenseAccountAsync("Workgroups / ALM 2027", null, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(new HoldedExpenseAccountRef(62900102, "new-1", "Workgroups / ALM 2027", Created: true));
        await _repo.Received(1).UpsertManagedAccountAsync(
            Arg.Is<HoldedManagedAccount>(a => a.HoldedAccountNumber == 62900102 && a.HoldedAccountId == "new-1"
                && a.Label == "Workgroups / ALM 2027" && a.IsActive && a.CreatedAt == FixedNow),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(AuditAction.HoldedExpenseAccountCreated, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("62900102")), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CreateOrLink_NameMatchesIgnoringCaseSpaceAndAccents_LinksInsteadOfCreating()
    {
        NoManagedAccounts();
        NoCategoryMap();
        LiveChart((62900150, "acc-150", "workgroups /  alm 2027"));

        var result = await MakeService().CreateOrLinkExpenseAccountAsync("Workgroups / ALM 2027", null, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(new HoldedExpenseAccountRef(62900150, "acc-150", "workgroups /  alm 2027", Created: false));
        await _client.DidNotReceive().CreateExpenseAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpsertManagedAccountAsync(
            Arg.Is<HoldedManagedAccount>(a => a.HoldedAccountNumber == 62900150 && a.Label == "workgroups /  alm 2027"),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(AuditAction.HoldedExpenseAccountLinked, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CreateOrLink_ExplicitNumber_LinksThatAccount()
    {
        NoManagedAccounts();
        NoCategoryMap();
        LiveChart((62900150, "acc-150", "Anything"), (62900151, "acc-151", "Other"));

        var result = await MakeService().CreateOrLinkExpenseAccountAsync("Workgroups / ALM 2027", 62900151, Xunit.TestContext.Current.CancellationToken);

        result.Should().Be(new HoldedExpenseAccountRef(62900151, "acc-151", "Other", Created: false));
        await _client.DidNotReceive().CreateExpenseAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateOrLink_ExplicitNumberUnknownInHolded_Throws()
    {
        NoManagedAccounts();
        NoCategoryMap();
        LiveChart((62900150, "acc-150", "Anything"));

        var act = () => MakeService().CreateOrLinkExpenseAccountAsync("X", 62900199, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*62900199*");
        await _repo.DidNotReceive().UpsertManagedAccountAsync(Arg.Any<HoldedManagedAccount>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateOrLink_LinkingABudgetCategoryAccount_DoesNotRegisterIt()
    {
        NoManagedAccounts();
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCategoryMap>
        {
            new() { Id = Guid.NewGuid(), BudgetCategoryId = Guid.NewGuid(), HoldedAccountNumber = 62900100, HoldedAccountId = "cat-0", Tag = "x" },
        });
        LiveChart((62900100, "cat-0", "Departments / Geeks"));

        var result = await MakeService().CreateOrLinkExpenseAccountAsync("Workgroups / Geeks", 62900100, Xunit.TestContext.Current.CancellationToken);

        result.AccountId.Should().Be("cat-0");
        await _repo.DidNotReceive().UpsertManagedAccountAsync(Arg.Any<HoldedManagedAccount>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateOrLink_SkipsNumbersHeldByTheManagedRegistry()
    {
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>
        {
            new() { Id = Guid.NewGuid(), HoldedAccountNumber = 62900100, HoldedAccountId = "m-0", Label = "Old", IsActive = false },
        });
        NoCategoryMap();
        LiveChart();
        _client.CreateExpenseAccountAsync(62900101, "Workgroups / New", Arg.Any<CancellationToken>()).Returns("new-2");

        var result = await MakeService().CreateOrLinkExpenseAccountAsync("Workgroups / New", null, Xunit.TestContext.Current.CancellationToken);

        result.AccountNum.Should().Be(62900101);
    }

    [HumansFact]
    public async Task CreateOrLink_BlankName_Throws()
    {
        var act = () => MakeService().CreateOrLinkExpenseAccountAsync("   ", null, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentException>();
    }
```

The audit `LogAsync` signature used above must match `IAuditLogService.LogAsync` — check `src/Sections/Humans.AuditLog.Contracts/IAuditLogService.cs` and adjust the `Received` argument list to the overload the service calls (the existing creditor-binding audit at `Service.cs:1012` shows the overload in use).

- [x] **Step 6: Run to verify they fail**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~CreateOrLink"
```
Expected: build errors (record, method, audit actions missing).

- [x] **Step 7: Contracts**

`HoldedDtos.cs`, append:

```csharp
/// <summary>The outcome of <see cref="IHoldedFinanceService.CreateOrLinkExpenseAccountAsync"/>:
/// the account the caller should book to, and whether this call created it.</summary>
public sealed record HoldedExpenseAccountRef(int AccountNum, string AccountId, string Name, bool Created);
```

`IHoldedFinanceService.cs`, add before `SetCreditorContactAsync`:

```csharp
    /// <summary>Resolves an expense account for a caller that has no budget category: an explicit
    /// <paramref name="existingAccountNum"/> is verified against the live chart and returned; else an
    /// account whose name normalizes equal to <paramref name="name"/> is returned; else one is created
    /// at the next free number of the <c>62900100</c> block. Accounts outside the budget map are
    /// recorded in Finance's managed registry so the doc sync attributes their docs.
    /// Throws <see cref="InvalidOperationException"/> when the explicit number is not in Holded.</summary>
    Task<HoldedExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default);
```

`AuditAction.cs`, after `ExpenseHoldedRequeued` add:

```csharp
    // Finance created or linked a Holded expense account outside the budget map on a caller's behalf.
    HoldedExpenseAccountCreated,
    HoldedExpenseAccountLinked,
```

- [x] **Step 8: Service implementation**

In `Service.cs`, add the constant near the other `internal const` lines:

```csharp
    /// <summary>First number of the P&amp;L expense block both provisioning paths allocate from.</summary>
    internal const int ExpenseAccountBlockStart = 62900100;
    internal const string HoldedExpenseAccount = "HoldedExpenseAccount";
```

After `ProvisionAsync`, add:

```csharp
    // ─── Managed expense accounts ───────────────────────────────────────────────

    public async Task<HoldedExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();

        var chart = await client.ListExpenseAccountsAsync(ct);
        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);

        HoldedExpenseAccountDto? found;
        if (existingAccountNum is { } num)
        {
            found = chart.FirstOrDefault(a => a.AccountNum == num)
                ?? throw new InvalidOperationException($"Holded has no expense account {num}.");
        }
        else
        {
            var wanted = HoldedMatcher.NormalizeAccountName(name);
            found = chart.FirstOrDefault(a => HoldedMatcher.NormalizeAccountName(a.Name) == wanted);
        }

        var now = clock.GetCurrentInstant();
        if (found is not null)
        {
            var isCategoryAccount = map.Any(m => m.HoldedAccountNumber == found.AccountNum);
            if (!isCategoryAccount)
                await RegisterManagedAsync(found.AccountNum, found.Id, found.Name, managed, now, ct);
            await audit.LogAsync(AuditAction.HoldedExpenseAccountLinked, HoldedExpenseAccount, Guid.Empty,
                $"Linked Holded expense account {found.AccountNum} '{found.Name}' for '{name}'");
            return new HoldedExpenseAccountRef(found.AccountNum, found.Id, found.Name, Created: false);
        }

        var used = map.Select(m => m.HoldedAccountNumber)
            .Concat(managed.Select(m => m.HoldedAccountNumber))
            .Concat(chart.Select(a => a.AccountNum))
            .ToHashSet();
        var next = ExpenseAccountBlockStart;
        while (used.Contains(next)) next++;

        var id = await client.CreateExpenseAccountAsync(next, name, ct);
        await RegisterManagedAsync(next, id, name, managed, now, ct);
        await audit.LogAsync(AuditAction.HoldedExpenseAccountCreated, HoldedExpenseAccount, Guid.Empty,
            $"Created Holded expense account {next} '{name}'");
        return new HoldedExpenseAccountRef(next, id, name, Created: true);
    }

    private Task RegisterManagedAsync(
        int accountNum, string accountId, string label,
        IReadOnlyList<HoldedManagedAccount> managed, Instant now, CancellationToken ct)
    {
        var existing = managed.FirstOrDefault(m => m.HoldedAccountNumber == accountNum);
        return repo.UpsertManagedAccountAsync(new HoldedManagedAccount
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            HoldedAccountNumber = accountNum,
            HoldedAccountId = accountId,
            Label = label,
            IsActive = true,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        }, ct);
    }
```

Use the same `audit.LogAsync` overload the existing creditor audit uses (the one that takes an actor name string rather than a user id, since there is no acting user at this layer — pass `"Finance"` or the overload's job-name form). Adjust the test's `Received` call to match.

- [x] **Step 9: Run the tests**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~CreateOrLink|FullyQualifiedName~NormalizeAccountName"
```
Expected: all PASS.

- [x] **Step 10: Commit**

```bash
git add src/Sections/Humans.Finance src/Sections/Humans.Finance.Contracts src/Sections/Humans.AuditLog.Contracts tests/Humans.Finance.Tests
git commit -m "feat(Finance): CreateOrLinkExpenseAccountAsync with name dedup and block allocation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Finance — retire flag and account listing

**Files:**
- Modify: `src/Sections/Humans.Finance.Contracts/HoldedDtos.cs`
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceService.cs`
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceServiceRead.cs`
- Modify: `src/Sections/Humans.Finance/Services/Service.cs`
- Test: `tests/Humans.Finance.Tests/ServiceTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record HoldedExpenseAccountOption(int AccountNum, string AccountId, string Label, bool IsBudgetCategory, bool IsActive);`
  - `IHoldedFinanceService.SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default)` → `Task`
  - `IHoldedFinanceServiceRead.ListExpenseAccountsAsync(bool activeOnly, CancellationToken ct = default)` → `Task<IReadOnlyList<HoldedExpenseAccountOption>>`

- [x] **Step 1: Failing tests**

Append to `HoldedFinanceServiceTests`:

```csharp
    // ─── SetExpenseAccountActive / ListExpenseAccounts ───────────────────────────

    [HumansFact]
    public async Task SetExpenseAccountActive_FlipsAManagedRow()
    {
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>
        {
            new() { Id = Guid.NewGuid(), HoldedAccountNumber = 62900150, HoldedAccountId = "m", Label = "Workgroups / ALM 2027", IsActive = true, CreatedAt = FixedNow },
        });

        await MakeService().SetExpenseAccountActiveAsync(62900150, false, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).UpsertManagedAccountAsync(
            Arg.Is<HoldedManagedAccount>(a => a.HoldedAccountNumber == 62900150 && !a.IsActive && a.UpdatedAt == FixedNow),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetExpenseAccountActive_UnknownNumber_IsANoOp()
    {
        NoManagedAccounts();

        await MakeService().SetExpenseAccountActiveAsync(1, false, Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().UpsertManagedAccountAsync(Arg.Any<HoldedManagedAccount>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ListExpenseAccounts_UnionsCategoryMapAndRegistry_FiltersInactive()
    {
        var catId = Guid.NewGuid();
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCategoryMap>
        {
            new() { Id = Guid.NewGuid(), BudgetCategoryId = catId, HoldedAccountNumber = 62900100, HoldedAccountId = "cat-0", Tag = "x" },
        });
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>
        {
            new() { Id = Guid.NewGuid(), HoldedAccountNumber = 62900150, HoldedAccountId = "m-1", Label = "Workgroups / ALM 2027", IsActive = true },
            new() { Id = Guid.NewGuid(), HoldedAccountNumber = 62900151, HoldedAccountId = "m-2", Label = "Workgroups / ALM 2026", IsActive = false },
        });
        _budget.GetActiveYearAsync().Returns(new BudgetYearDetail(Guid.NewGuid(), "2026", "2026", BudgetYearStatus.Active, false,
        [
            new BudgetGroupDetail(Guid.NewGuid(), Guid.NewGuid(), "Departments", 0, false, true, false, null,
            [
                new BudgetCategoryDetail(catId, Guid.NewGuid(), "Geeks", -100m, ExpenditureType.OpEx, null, 0, []),
            ]),
        ]));

        var all = await MakeService().ListExpenseAccountsAsync(activeOnly: false, Xunit.TestContext.Current.CancellationToken);
        var active = await MakeService().ListExpenseAccountsAsync(activeOnly: true, Xunit.TestContext.Current.CancellationToken);

        all.Select(o => (o.AccountNum, o.Label, o.IsBudgetCategory, o.IsActive)).Should().BeEquivalentTo(
        [
            (62900100, "Departments / Geeks", true, true),
            (62900150, "Workgroups / ALM 2027", false, true),
            (62900151, "Workgroups / ALM 2026", false, false),
        ]);
        active.Select(o => o.AccountNum).Should().BeEquivalentTo([62900100, 62900150]);
        await _client.DidNotReceive().ListExpenseAccountsAsync(Arg.Any<CancellationToken>());
    }
```

- [x] **Step 2: Run to verify failure**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~SetExpenseAccountActive|FullyQualifiedName~ListExpenseAccounts_Unions"
```
Expected: build errors.

- [x] **Step 3: Contracts**

`HoldedDtos.cs`, append:

```csharp
/// <summary>One expense account a caller may book to: a budget category's account (labelled
/// "Group / Category" from the active budget year) or a Finance-managed account (its label).
/// Cache reads only — never a Holded call.</summary>
public sealed record HoldedExpenseAccountOption(
    int AccountNum, string AccountId, string Label, bool IsBudgetCategory, bool IsActive);
```

`IHoldedFinanceService.cs`, after `CreateOrLinkExpenseAccountAsync`:

```csharp
    /// <summary>Retires or restores a managed account so it drops out of, or returns to, the active
    /// pickers. No-op for a budget-category account or a number Finance does not manage — the
    /// account itself is never touched in Holded.</summary>
    Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default);
```

`IHoldedFinanceServiceRead.cs`, after `GetHoldedAccountIdForCategoryAsync`:

```csharp
    /// <summary>Every expense account a report or a workgroup may book to — the category map's
    /// accounts plus Finance's managed registry. <paramref name="activeOnly"/> drops retired managed
    /// accounts; category accounts are always active. Cache reads only.</summary>
    Task<IReadOnlyList<HoldedExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default);
```

- [x] **Step 4: Service implementation**

In `Service.cs`, after `RegisterManagedAsync`:

```csharp
    public async Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default)
    {
        var managed = await repo.GetManagedAccountsAsync(ct);
        var row = managed.FirstOrDefault(m => m.HoldedAccountNumber == accountNum);
        if (row is null || row.IsActive == isActive) return;

        row.IsActive = isActive;
        row.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpsertManagedAccountAsync(row, ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var year = await budget.GetActiveYearAsync();
        var labels = year?.Groups
            .SelectMany(g => g.Categories.Select(c => (c.Id, Label: $"{g.Name} / {c.Name}")))
            .ToDictionary(x => x.Id, x => x.Label)
            ?? new Dictionary<Guid, string>();

        var options = map
            .Where(m => m.IsActive)
            .Select(m => new HoldedExpenseAccountOption(
                m.HoldedAccountNumber, m.HoldedAccountId,
                labels.GetValueOrDefault(m.BudgetCategoryId, $"Category {m.HoldedAccountNumber}"),
                IsBudgetCategory: true, IsActive: true))
            .Concat(managed
                .Where(m => !activeOnly || m.IsActive)
                .Select(m => new HoldedExpenseAccountOption(
                    m.HoldedAccountNumber, m.HoldedAccountId, m.Label,
                    IsBudgetCategory: false, IsActive: m.IsActive)))
            .OrderBy(o => o.AccountNum)
            .ToList();
        return options;
    }
```

- [x] **Step 5: Run the tests**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~SetExpenseAccountActive|FullyQualifiedName~ListExpenseAccounts_Unions"
```
Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add src/Sections/Humans.Finance src/Sections/Humans.Finance.Contracts tests/Humans.Finance.Tests
git commit -m "feat(Finance): managed-account retire flag and expense account listing

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Finance — doc sync attributes managed accounts

**Files:**
- Modify: `src/Sections/Humans.Finance/Services/HoldedMatcher.cs`
- Modify: `src/Sections/Humans.Finance/Services/Service.cs` (`SyncAsync`, `MapDoc`)
- Test: `tests/Humans.Finance.Tests/HoldedMatcherTests.cs`, `tests/Humans.Finance.Tests/ServiceTests.cs`

**Interfaces:**
- Produces: `HoldedMatcher.Match(string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map, IReadOnlySet<string> managedAccountIds)` — new overload; the three-argument form stays and delegates with an empty set.
- `HoldedMatchResult` gains `bool IsManaged` (default false). Managed hit → `(CategoryId: null, Source: Account, IsManaged: true)`.

- [x] **Step 1: Failing matcher test**

Append to `HoldedMatcherTests.cs`:

```csharp
    [HumansFact]
    public void Match_BookedToAManagedAccount_IsAnAccountMatchWithNoCategory()
    {
        var map = new[] { new HoldedMatchEntry(Guid.NewGuid(), "cat-0", "geeks") };
        var managed = new HashSet<string>(StringComparer.Ordinal) { "m-1" };

        var result = HoldedMatcher.Match("m-1", ["geeks"], map, managed);

        result.CategoryId.Should().BeNull();
        result.Source.Should().Be(HoldedMatchSource.Account);
        result.IsManaged.Should().BeTrue();
    }

    [HumansFact]
    public void Match_CategoryAccountWinsOverManagedSet()
    {
        var cat = Guid.NewGuid();
        var map = new[] { new HoldedMatchEntry(cat, "cat-0", "geeks") };
        var managed = new HashSet<string>(StringComparer.Ordinal) { "cat-0" };

        var result = HoldedMatcher.Match("cat-0", [], map, managed);

        result.CategoryId.Should().Be(cat);
        result.IsManaged.Should().BeFalse();
    }
```

- [x] **Step 2: Failing sync test**

Append to `HoldedFinanceServiceTests`:

```csharp
    [HumansFact]
    public async Task Sync_DocBookedToManagedAccount_IsMatchedWithNoCategory()
    {
        NoCategoryMap();
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>
        {
            new() { Id = Guid.NewGuid(), HoldedAccountNumber = 62900150, HoldedAccountId = "m-1", Label = "Workgroups / ALM 2027", IsActive = true },
        });
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new HoldedDocSyncState { Id = 1, Status = "Idle" });
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedPurchaseDocListItemDto>
        {
            new()
            {
                Id = "doc-1", DocNumber = "F1", ContactName = "Vendor", Date = FixedNow, Subtotal = 10m, Tax = 2.1m, Total = 12.1m,
                IsDraft = false, Lines = [new HoldedPurchaseLineDto { Amount = 10m, AccountId = "m-1" }],
            },
        });
        List<HoldedExpenseDoc>? saved = null;
        await _repo.UpsertDocsAsync(Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => saved = d.ToList()), Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        var result = await MakeService().SyncAsync(Xunit.TestContext.Current.CancellationToken);

        result.Matched.Should().Be(1);
        result.Unmatched.Should().Be(0);
        var doc = saved.Should().ContainSingle().Which;
        doc.MatchStatus.Should().Be(HoldedMatchStatus.Matched);
        doc.MatchSource.Should().Be(HoldedMatchSource.Account);
        doc.BudgetCategoryId.Should().BeNull();
    }
```

Check `HoldedDocSyncState`'s required members against the domain class and adjust the object initializer to compile.

- [x] **Step 3: Run to verify failure**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet --filter "FullyQualifiedName~Match_BookedToAManaged|FullyQualifiedName~Match_CategoryAccountWins|FullyQualifiedName~Sync_DocBookedToManaged"
```
Expected: build errors (overload / `IsManaged` missing).

- [x] **Step 4: Matcher change**

In `HoldedMatcher.cs` replace the `HoldedMatchResult` record and `Match`:

```csharp
[StructLayout(LayoutKind.Auto)]
internal readonly record struct HoldedMatchResult(Guid? CategoryId, HoldedMatchSource Source, bool IsManaged = false);
```
```csharp
    /// <summary>Account (A) wins over tag (B); else None.</summary>
    public static HoldedMatchResult Match(
        string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map) =>
        Match(bookedAccountId, tags, map, ImmutableHashSet<string>.Empty);

    /// <summary>As above, then a booked account in <paramref name="managedAccountIds"/> (Finance's
    /// registry of accounts outside the budget map) is an Account match with no category — attributed,
    /// so it stays off the Unmatched queue, but outside every budget year's actuals.</summary>
    public static HoldedMatchResult Match(
        string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map,
        IReadOnlySet<string> managedAccountIds)
    {
        if (!string.IsNullOrEmpty(bookedAccountId))
        {
            foreach (var e in map)
                if (string.Equals(e.AccountId, bookedAccountId, StringComparison.Ordinal))
                    return new(e.CategoryId, HoldedMatchSource.Account);
            if (managedAccountIds.Contains(bookedAccountId))
                return new(null, HoldedMatchSource.Account, IsManaged: true);
        }
        var normTags = tags.Select(NormalizeTag).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (normTags.Count > 0)
        {
            foreach (var e in map)
                if (normTags.Contains(NormalizeTag(e.Tag)))
                    return new(e.CategoryId, HoldedMatchSource.Tag);
        }
        return new(null, HoldedMatchSource.None);
    }
```
Add `using System.Collections.Immutable;`.

- [x] **Step 5: Sync change**

In `Service.SyncAsync`, after building `entries`:

```csharp
            var managedIds = (await repo.GetManagedAccountsAsync(ct))
                .Select(m => m.HoldedAccountId)
                .ToHashSet(StringComparer.Ordinal);
```
Change the doc mapping line to `MapDoc(doc, entries, managedIds, now)`. Change `MapDoc`'s signature to add `IReadOnlySet<string> managedIds` after `entries`, call `HoldedMatcher.Match(bookedAccount, tags, entries, managedIds)`, and set:

```csharp
            MatchStatus = matchResult.CategoryId is null && !matchResult.IsManaged
                ? HoldedMatchStatus.Unmatched
                : HoldedMatchStatus.Matched,
```

Grep for other `MapDoc(` callers and the existing `SyncAsync` tests (`grep -n "MapDoc\|SyncAsync" tests/Humans.Finance.Tests/ServiceTests.cs`) — existing sync tests must now stub `GetManagedAccountsAsync`; an unstubbed NSubstitute `Task<IReadOnlyList<T>>` returns an empty list, so they should pass unchanged. Verify.

- [x] **Step 6: Run the Finance test project**

```bash
dotnet test tests/Humans.Finance.Tests -v quiet
```
Expected: all PASS.

- [x] **Step 7: Commit**

```bash
git add src/Sections/Humans.Finance tests/Humans.Finance.Tests
git commit -m "feat(Finance): doc sync attributes managed accounts, keeps them off Unmatched

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Finance — connector index shows the registry; Finance.md

**Files:**
- Modify: `src/Sections/Humans.Finance/Models/HoldedConnectorVm.cs`
- Modify: `src/Sections/Humans.Finance/Services/Service.cs` (`GetConnectorOverviewAsync`)
- Modify: `src/Sections/Humans.Finance/Views/Finance/Holded.cshtml`
- Modify: `src/Sections/Humans.Finance/Docs/Finance.md`
- Modify: `src/Sections/Humans.Finance/Docs/data-access.md` (table list)

- [ ] **Step 1: View model**

In `HoldedConnectorVm.cs` add a positional parameter after `CategoryMap`:

```csharp
    IReadOnlyList<HoldedManagedAccountVm> ManagedAccounts,
```
and the record:

```csharp
/// <summary>One Finance-managed expense account (created outside the budget map) as the connector
/// index lists it.</summary>
internal sealed record HoldedManagedAccountVm(int AccountNumber, string AccountId, string Label, bool IsActive, Instant CreatedAt);
```

- [ ] **Step 2: Service**

In `GetConnectorOverviewAsync` (around `Service.cs:347`), read `var managed = await repo.GetManagedAccountsAsync(ct);` and pass

```csharp
            managed.OrderBy(m => m.HoldedAccountNumber)
                .Select(m => new HoldedManagedAccountVm(m.HoldedAccountNumber, m.HoldedAccountId, m.Label, m.IsActive, m.CreatedAt))
                .ToList(),
```
in the new positional slot. Fix any test or other caller constructing `HoldedConnectorVm` (`grep -rn "new HoldedConnectorVm(" src tests`).

- [ ] **Step 3: View**

In `Holded.cshtml`, directly after the "Category map" card (ends after the `@foreach (var m in Model.CategoryMap …)` table), add:

```cshtml
<div class="card mb-4">
    <div class="card-header d-flex justify-content-between align-items-center">
        <span><i class="fa-solid fa-folder-tree me-1"></i>Managed accounts</span>
        <span class="badge bg-secondary">@Model.ManagedAccounts.Count rows</span>
    </div>
    @if (Model.ManagedAccounts.Count == 0)
    {
        <div class="card-body text-muted small">No expense accounts created outside the budget map yet.</div>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-sm mb-0">
                <thead><tr><th>Account</th><th>Label</th><th>Holded id</th><th>Active</th><th>Since</th></tr></thead>
                <tbody>
                @foreach (var m in Model.ManagedAccounts)
                {
                    <tr>
                        <td><code>@m.AccountNumber</code></td>
                        <td>@m.Label</td>
                        <td class="text-muted small">@m.AccountId</td>
                        <td>@(m.IsActive ? "Yes" : "Retired")</td>
                        <td>@m.CreatedAt.InUtc().Date</td>
                    </tr>
                }
                </tbody>
            </table>
        </div>
    }
</div>
```

- [ ] **Step 4: Docs**

`Finance.md`:
- Concepts: add a bullet — *A **Managed Account** is an expense account Finance created or linked outside the budget map on another caller's request (`CreateOrLinkExpenseAccountAsync`). Finance records number, id, label and an active flag — never who asked. Docs booked to one are Matched with no category: off the Unmatched queue, outside every year's actuals.*
- Data Model: add a `### HoldedManagedAccount` section (table `holded_managed_accounts`, the six columns, unique on number and id).
- Attribution chain: after step 1 add *1b. **Managed (A′):** the booked account id is in `holded_managed_accounts` → `Matched`, `MatchSource = Account`, `BudgetCategoryId = null`.*
- Invariants: add *Created accounts are named exactly as the caller asked and numbered from `62900100` past every number in the category map, the managed registry and the live chart. A name that normalizes equal to an existing chart account (trim, whitespace, case, accents) links instead of creating.*
- Routing: `/Finance/Holded` row — mention the managed registry.
- Architecture / cross-section read interface table: list the three new members.
- Update the freshness `flag-on-change` comment to mention the managed registry.

`data-access.md`: add `holded_managed_accounts` to the owned-table list.

- [ ] **Step 5: Build and test**

```bash
dotnet build src/Sections/Humans.Finance -v quiet
dotnet test tests/Humans.Finance.Tests -v quiet
```
Expected: clean, all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Sections/Humans.Finance tests/Humans.Finance.Tests
git commit -m "feat(Finance): list managed accounts on /Finance/Holded; document the registry

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Workgroups — budget columns, DTO, migration

**Files:**
- Modify: `src/Sections/Humans.Workgroups/Domain/Workgroup.cs`
- Modify: `src/Sections/Humans.Workgroups/Data/Configurations/WorkgroupConfiguration.cs`
- Modify: `src/Sections/Humans.Workgroups/Services/Dtos/WorkgroupDtos.cs` (`WorkgroupInfo`)
- Modify: `src/Sections/Humans.Workgroups/Services/WorkgroupService.Helpers.cs` (`ToInfo`)
- Create (generated): `src/Sections/Humans.Workgroups/Data/Migrations/<timestamp>_WorkgroupBudget.cs` + `.Designer.cs`, snapshot updated

**Interfaces:**
- Produces on `Workgroup`: `decimal? BudgetAmount`, `int? HoldedAccountNumber`, `string? HoldedAccountId`.
- Produces on `WorkgroupInfo` (appended positional params): `decimal? BudgetAmount, int? HoldedAccountNumber, string? HoldedAccountId`. `WorkgroupInfo.HasBudget => BudgetAmount is not null`.

- [ ] **Step 1: Entity**

In `Workgroup.cs`, after `DiscordChannelUrl`:

```csharp
    /// <summary>Allocated budget in EUR; null means the group has no budget. Shown on the register
    /// to association members. Finance and Expenses never read it.</summary>
    public decimal? BudgetAmount { get; set; }

    /// <summary>The Holded expense account the group's spending books to — created or linked
    /// through Finance. Same posture as <see cref="DriveFolderId"/>: an opaque external reference
    /// with no FK. Kept when the budget is cleared; Holded accounts are never deleted.</summary>
    public int? HoldedAccountNumber { get; set; }

    public string? HoldedAccountId { get; set; }
```

- [ ] **Step 2: Configuration**

In `WorkgroupConfiguration.cs`, next to the `DriveFolderId` max-length line:

```csharp
        b.Property(w => w.BudgetAmount).HasPrecision(18, 2);
        b.Property(w => w.HoldedAccountId).HasMaxLength(64);
```

- [ ] **Step 3: DTO**

In `WorkgroupDtos.cs`, `WorkgroupInfo`: add three positional parameters at the end, after `Documents`:

```csharp
    IReadOnlyList<WorkgroupDocumentInfo> Documents,
    decimal? BudgetAmount,
    int? HoldedAccountNumber,
    string? HoldedAccountId)
```
and inside the record body (create one if the record has none):

```csharp
{
    public bool HasBudget => BudgetAmount is not null;
}
```
In `ToInfo` (`WorkgroupService.Helpers.cs:21`) append `w.BudgetAmount, w.HoldedAccountNumber, w.HoldedAccountId` as the last arguments. Grep `new WorkgroupInfo(` across `src` and `tests` — there were zero other constructions at plan time; fix any that appeared.

- [ ] **Step 4: Migration**

```bash
dotnet ef migrations add WorkgroupBudget --context WorkgroupsDbContext --output-dir Data/Migrations --project src/Sections/Humans.Workgroups --startup-project src/Humans.Web
```
`git diff --stat src/Sections/Humans.Workgroups/Data/Migrations` — expect one new pair plus a snapshot diff adding exactly the three columns. Anything else: stop and report.

- [ ] **Step 5: Build and run the section tests**

```bash
dotnet build src/Sections/Humans.Workgroups -v quiet
dotnet test tests/Humans.Workgroups.Tests -v quiet
```
Expected: clean, all PASS (no behavior change yet).

- [ ] **Step 6: Commit**

```bash
git add src/Sections/Humans.Workgroups
git commit -m "feat(Workgroups): budget amount and Holded account columns

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Workgroups — `SetBudgetAsync`

**Files:**
- Modify: `src/Sections/Humans.Workgroups/Humans.Workgroups.csproj` (add `Humans.Finance.Contracts`, `Humans.Holded.Contracts` project references)
- Modify: `src/Sections/Humans.Workgroups/Services/Dtos/WorkgroupDtos.cs` (`WorkgroupBudgetSave`)
- Modify: `src/Sections/Humans.Workgroups/Services/IWorkgroupService.cs`
- Modify: `src/Sections/Humans.Workgroups/Services/WorkgroupService.cs` (ctor + method)
- Modify: `src/Sections/Humans.Workgroups/Services/CachingWorkgroupService.cs`
- Modify: `src/Sections/Humans.Workgroups/Services/WorkgroupErrorKeys.cs`
- Modify: `src/Sections/Humans.Workgroups/Domain/Enums.cs` (`WorkgroupLogKind.BudgetSet`)
- Modify: `src/Sections/Humans.AuditLog.Contracts/AuditAction.cs` (`WorkgroupBudgetSet`)
- Modify: `tests/Humans.Workgroups.Tests/Infrastructure/WorkgroupsTestHarness.cs`
- Modify: `tests/Humans.Workgroups.Tests/Humans.Workgroups.Tests.csproj` (reference `Humans.Finance.Contracts` if not transitively available)
- Create: `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceBudgetTests.cs`

**Interfaces:**
- Consumes: `IHoldedFinanceService.CreateOrLinkExpenseAccountAsync(string, int?, CancellationToken)` → `HoldedExpenseAccountRef(AccountNum, AccountId, Name, Created)`.
- Produces:
  - `internal sealed record WorkgroupBudgetSave(decimal? Amount, int? ExistingAccountNum);` — `ExistingAccountNum` set means "link that account"; null means "create `Workgroups / {Name}` if none is bound yet".
  - `IWorkgroupService.SetBudgetAsync(Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)` → `Task<HoldedExpenseAccountRef?>` — the account touched this call, null when no Finance call was made.
  - `WorkgroupErrorKeys.BudgetNegative = "Workgroups_Error_BudgetNegative"`, `WorkgroupErrorKeys.BudgetAccountFailed = "Workgroups_Error_BudgetAccountFailed"`.
  - `WorkgroupService` ctor gains `IHoldedFinanceService finance` after `IGoogleSyncService googleSync`.

- [ ] **Step 1: Project references**

In `Humans.Workgroups.csproj`, beside the other `.Contracts` references:

```xml
    <ProjectReference Include="..\Humans.Finance.Contracts\Humans.Finance.Contracts.csproj" />
    <ProjectReference Include="..\Humans.Holded.Contracts\Humans.Holded.Contracts.csproj" />
```

- [ ] **Step 2: Harness**

In `WorkgroupsTestHarness.cs`: add `using Humans.Finance.Contracts;`; in the constructor after `GoogleSync`:

```csharp
        Finance = Substitute.For<IHoldedFinanceService>();
        Finance.CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => new HoldedExpenseAccountRef(
                call.Arg<int?>() ?? 62900150, $"acc-{call.Arg<int?>() ?? 62900150}", call.Arg<string>(),
                Created: call.Arg<int?>() is null));
```
property `private protected IHoldedFinanceService Finance { get; }`, and in `NewService` pass `Finance` right after `GoogleSync`.

- [ ] **Step 3: Failing tests**

`tests/Humans.Workgroups.Tests/Services/WorkgroupServiceBudgetTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Finance.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Humans.Workgroups.Tests.Services;

/// <summary>The budget allocation: amount on the register, account through Finance, never a twin.</summary>
public sealed class WorkgroupServiceBudgetTests : WorkgroupsTestHarness
{
    [HumansFact]
    public async Task SetBudget_FirstTime_CreatesTheAccountNamedAfterTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync(name: "ALM 2027");
        var secretary = SeedUser("Secretary");

        var result = await NewService().SetBudgetAsync(workgroup.Id, secretary, new WorkgroupBudgetSave(1500m, null), Ct);

        await Finance.Received(1).CreateOrLinkExpenseAccountAsync("Workgroups / ALM 2027", null, Arg.Any<CancellationToken>());
        result!.Created.Should().BeTrue();
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.Include(w => w.LogEntries).SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().Be(1500m);
        reloaded.HoldedAccountNumber.Should().Be(62900150);
        reloaded.HoldedAccountId.Should().Be("acc-62900150");
        reloaded.LogEntries.Should().ContainSingle(e => e.Kind == WorkgroupLogKind.BudgetSet);
        await AuditLog.Received(1).LogAsync(AuditAction.WorkgroupBudgetSet, Arg.Any<string>(), workgroup.Id,
            Arg.Is<string>(d => d.Contains("1500") && d.Contains("62900150")), secretary,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetBudget_AccountAlreadyBound_ChangesAmountOnly()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(250m, null), Ct);

        await Finance.DidNotReceive().CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().Be(250m);
        reloaded.HoldedAccountNumber.Should().Be(62900160);
    }

    [HumansFact]
    public async Task SetBudget_ClearingKeepsTheAccountBinding()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(null, null), Ct);

        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().BeNull();
        reloaded.HoldedAccountNumber.Should().Be(62900160);
        reloaded.HoldedAccountId.Should().Be("acc-160");
    }

    [HumansFact]
    public async Task SetBudget_LinkExisting_RebindsToThatAccount()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(100m, 62900170), Ct);

        await Finance.Received(1).CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), 62900170, Arg.Any<CancellationToken>());
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.HoldedAccountNumber.Should().Be(62900170);
        reloaded.HoldedAccountId.Should().Be("acc-62900170");
    }

    [HumansFact]
    public async Task SetBudget_NegativeAmount_Throws()
    {
        var workgroup = await SeedWorkgroupAsync();

        var act = () => NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(-1m, null), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key.Should().Be(WorkgroupErrorKeys.BudgetNegative);
    }

    [HumansTheory]
    [InlineData(nameof(WorkgroupStatus.Refused))]
    [InlineData(nameof(WorkgroupStatus.Withdrawn))]
    public async Task SetBudget_OnRefusedOrWithdrawn_Throws(string statusName)
    {
        var workgroup = await SeedWorkgroupAsync(status: Enum.Parse<WorkgroupStatus>(statusName));

        var act = () => NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(10m, null), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key.Should().Be(WorkgroupErrorKeys.WrongStatus);
    }

    [HumansFact]
    public async Task SetBudget_FinanceFails_WritesNothing()
    {
        var workgroup = await SeedWorkgroupAsync();
        Finance.CreateOrLinkExpenseAccountAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<HoldedExpenseAccountRef>(_ => throw new InvalidOperationException("Holded has no expense account 1."));

        var act = () => NewService().SetBudgetAsync(workgroup.Id, SeedUser(), new WorkgroupBudgetSave(10m, 1), Ct);

        (await act.Should().ThrowAsync<WorkgroupRuleException>()).Which.Key.Should().Be(WorkgroupErrorKeys.BudgetAccountFailed);
        await using var ctx = OpenContext();
        var reloaded = await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct);
        reloaded.BudgetAmount.Should().BeNull();
        reloaded.HoldedAccountNumber.Should().BeNull();
    }

    private async Task BindAsync(Guid workgroupId, int accountNum, string accountId, decimal amount)
    {
        var w = await Db.Workgroups.SingleAsync(x => x.Id == workgroupId, Ct);
        w.BudgetAmount = amount;
        w.HoldedAccountNumber = accountNum;
        w.HoldedAccountId = accountId;
        await Db.SaveChangesAsync(Ct);
    }
}
```

Match the `AuditLog.Received(...).LogAsync(...)` argument list to the overload `AuditAsync` in `WorkgroupService.Helpers.cs:373` calls (`action, entityType, id, description, actorUserId, relatedEntityId:, relatedEntityType:`).

- [ ] **Step 4: Run to verify failure**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet --filter "FullyQualifiedName~WorkgroupServiceBudgetTests"
```
Expected: build errors.

- [ ] **Step 5: Enum, audit action, error keys, DTO, interface**

`Enums.cs`, `WorkgroupLogKind`, after `SurveySent`:

```csharp
    BudgetSet,
```

`AuditAction.cs`, after `WorkgroupRegisteredExisting`:

```csharp
    // Board/Admin set, changed or cleared a group's budget allocation and Holded account.
    WorkgroupBudgetSet,
```

`WorkgroupErrorKeys.cs`:

```csharp
    public const string BudgetNegative = "Workgroups_Error_BudgetNegative";
    public const string BudgetAccountFailed = "Workgroups_Error_BudgetAccountFailed";
```

`WorkgroupDtos.cs`, after `WorkgroupBootstrap`:

```csharp
/// <summary>The budget form. <paramref name="Amount"/> null clears the budget (the account binding
/// stays). <paramref name="ExistingAccountNum"/> set links that Holded account; null creates
/// "Workgroups / {Name}" when nothing is bound yet, and is an amount-only change otherwise.</summary>
internal sealed record WorkgroupBudgetSave(decimal? Amount, int? ExistingAccountNum);
```

`IWorkgroupService.cs`, in the admin block after `RegisterExistingAsync`:

```csharp
    /// <summary>Board/Admin: set, change or clear the group's budget and bind its Holded account
    /// through Finance. Returns the account Finance resolved when one was created or linked this
    /// call, else null. Refused and Withdrawn groups are rejected.</summary>
    Task<HoldedExpenseAccountRef?> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default);
```
Add `using Humans.Finance.Contracts;`.

- [ ] **Step 6: Service**

`WorkgroupService.cs`: add ctor parameter `IHoldedFinanceService finance,` after `IGoogleSyncService googleSync,` and `using Humans.Finance.Contracts;`. Extend the `[CrossSectionWrite(...)]` text: `"…; SetBudgetAsync creates or links the group's Holded expense account through IHoldedFinanceService, and the lifecycle steps retire or restore it."`

After `MarkDoneAsync`, add:

```csharp
    // ── Budget ────────────────────────────────────────────────────────────

    public async Task<HoldedExpenseAccountRef?> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Amount is < 0)
            throw new WorkgroupRuleException(WorkgroupErrorKeys.BudgetNegative);

        var workgroup = await RequireAsync(workgroupId, ct);
        RequireStatus(workgroup, WorkgroupStatus.Applied, WorkgroupStatus.Referred,
            WorkgroupStatus.Active, WorkgroupStatus.Dormant);

        // Finance is asked first, before anything is written: a failed create or an unknown
        // account number leaves the row exactly as it was.
        HoldedExpenseAccountRef? account = null;
        var needsAccount = save.Amount is not null
            && (workgroup.HoldedAccountNumber is null
                || (save.ExistingAccountNum is { } wanted && wanted != workgroup.HoldedAccountNumber));
        if (needsAccount)
        {
            try
            {
                account = await finance.CreateOrLinkExpenseAccountAsync(
                    $"Workgroups / {workgroup.Name}", save.ExistingAccountNum, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Finance could not resolve a Holded account for workgroup {WorkgroupId}", workgroup.Id);
                throw new WorkgroupRuleException(WorkgroupErrorKeys.BudgetAccountFailed);
            }
        }

        var now = clock.GetCurrentInstant();
        workgroup.BudgetAmount = save.Amount;
        if (account is not null)
        {
            workgroup.HoldedAccountNumber = account.AccountNum;
            workgroup.HoldedAccountId = account.AccountId;
        }
        workgroup.UpdatedAt = now;
        await repository.UpdateWorkgroupAsync(workgroup, ct);

        var summary = save.Amount is { } amount
            ? $"Budget set to {amount:0.00} EUR, account {workgroup.HoldedAccountNumber}"
            : "Budget cleared";
        await AddSystemEntryAsync(workgroup, WorkgroupLogKind.BudgetSet, now, summary, ct, authorUserId: actorUserId);
        await AuditAsync(AuditAction.WorkgroupBudgetSet, workgroup, summary, actorUserId);
        return account;
    }
```

Check `WorkgroupRuleException` has a `(string key)` constructor (it does — `WorkgroupErrorKeys.WrongStatus` is thrown that way).

`CachingWorkgroupService.cs`, next to `RegisterExistingAsync`:

```csharp
    public Task<HoldedExpenseAccountRef?> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.SetBudgetAsync(workgroupId, actorUserId, save, ct));
```
If `MutateAsync` has only a `Func<IWorkgroupService, Task>` overload, use the generic one the `Task<Guid>` members use (`RegisterExistingAsync` returns `Task<Guid>` through `MutateAsync`, so a generic overload exists).

- [ ] **Step 7: Run the tests**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet --filter "FullyQualifiedName~WorkgroupServiceBudgetTests"
```
Expected: all PASS. Then the whole section project:

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet
```
Any test constructing `WorkgroupService` directly (outside the harness) needs the new parameter — fix them.

- [ ] **Step 8: Commit**

```bash
git add src/Sections/Humans.Workgroups src/Sections/Humans.AuditLog.Contracts tests/Humans.Workgroups.Tests
git commit -m "feat(Workgroups): SetBudgetAsync — amount on the register, account through Finance

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Workgroups — lifecycle retires and restores the account

**Files:**
- Modify: `src/Sections/Humans.Workgroups/Services/WorkgroupService.Helpers.cs` (`EndAsync`, new `SetAccountActiveAsync`)
- Modify: `src/Sections/Humans.Workgroups/Services/WorkgroupService.Lifecycle.cs` (`WithdrawAsync`, `ReactivateAsync`)
- Test: `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceBudgetTests.cs`

**Interfaces:**
- Consumes: `IHoldedFinanceService.SetExpenseAccountActiveAsync(int, bool, CancellationToken)`.

- [ ] **Step 1: Failing tests**

Append to `WorkgroupServiceBudgetTests`:

```csharp
    [HumansFact]
    public async Task Close_RetiresTheAccount()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet for months", Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MarkDone_RetiresTheAccount()
    {
        var coordinator = SeedUser("Coordinator");
        var workgroup = await SeedWorkgroupAsync(coordinatorUserId: coordinator);
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().MarkDoneAsync(workgroup.Id, coordinator, WorkgroupDormantReason.Delivered, Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Withdraw_RetiresTheAccount()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().WithdrawAsync(workgroup.Id, SeedUser("Secretary"), "Wrong register", Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Reactivate_RestoresTheAccount()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);

        await NewService().ReactivateAsync(workgroup.Id, SeedUser("Secretary"), Ct);

        await Finance.Received(1).SetExpenseAccountActiveAsync(62900160, true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Close_NoAccountBound_DoesNotCallFinance()
    {
        var workgroup = await SeedWorkgroupAsync();

        await NewService().CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet", Ct);

        await Finance.DidNotReceive().SetExpenseAccountActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Close_FinanceThrows_StillEndsTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync();
        await BindAsync(workgroup.Id, 62900160, "acc-160", 100m);
        Finance.SetExpenseAccountActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("Holded down"));

        await NewService().CloseAsync(workgroup.Id, SeedUser("Secretary"), "Quiet", Ct);

        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).Status.Should().Be(WorkgroupStatus.Dormant);
        Logger.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }
```
`CapturingLogger<T>.Entries` is `List<LogEntry(LogLevel Level, string Message, Exception? Exception)>` (`tests/Humans.Testing/CapturingLogger.cs`).

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet --filter "FullyQualifiedName~WorkgroupServiceBudgetTests"
```
Expected: the six new tests FAIL (no Finance call received).

- [ ] **Step 3: Implement**

`WorkgroupService.Helpers.cs`, next to `RequestDriveSyncAsync`:

```csharp
    /// <summary>
    /// Retires or restores the group's Holded account in Finance's registry so the pickers stop
    /// (or start) offering it. Failures are logged, never surfaced: the lifecycle step is the
    /// Board's decision and does not wait on Holded. A group with no account has nothing to flip.
    /// </summary>
    private async Task SetAccountActiveAsync(Workgroup w, bool isActive, CancellationToken ct)
    {
        if (w.HoldedAccountNumber is not { } accountNum) return;
        try
        {
            await finance.SetExpenseAccountActiveAsync(accountNum, isActive, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not set Holded account {AccountNum} active={IsActive} for workgroup {WorkgroupId}",
                accountNum, isActive, w.Id);
        }
    }
```

In `EndAsync`, after `await RequestDriveSyncAsync(w, ct);`:
```csharp
        await SetAccountActiveAsync(w, isActive: false, ct);
```
In `WithdrawAsync`, after its `RequestDriveSyncAsync` call:
```csharp
        await SetAccountActiveAsync(workgroup, isActive: false, ct);
```
In `ReactivateAsync`, after its `RequestDriveSyncAsync` call:
```csharp
        await SetAccountActiveAsync(workgroup, isActive: true, ct);
```

- [ ] **Step 4: Run the section tests**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet
```
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sections/Humans.Workgroups tests/Humans.Workgroups.Tests
git commit -m "feat(Workgroups): lifecycle retires and restores the group's Holded account

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Workgroups — admin route, forms, budget block

**Files:**
- Modify: `src/Sections/Humans.Workgroups/Models/WorkgroupViewModels.cs`
- Modify: `src/Sections/Humans.Workgroups/Controllers/WorkgroupsAdminController.cs`
- Modify: `src/Sections/Humans.Workgroups/Controllers/WorkgroupsController.cs` (`Details`)
- Modify: `src/Sections/Humans.Workgroups/Views/Workgroups/Details.cshtml`
- Modify: `src/Sections/Humans.Workgroups/Views/WorkgroupsAdmin/RegisterExisting.cshtml`
- Modify: `src/Sections/Humans.Workgroups/Services/WorkgroupService.Lifecycle.cs` (`RegisterExistingAsync`)
- Modify: `src/Sections/Humans.Workgroups/Services/Dtos/WorkgroupDtos.cs` (`WorkgroupBootstrap`)
- Test: `tests/Humans.Workgroups.Tests/Controllers/WorkgroupsAdminControllerTests.cs`, `tests/Humans.Workgroups.Tests/Controllers/WorkgroupsControllerAuthorizationTests.cs`, `tests/Humans.Workgroups.Tests/Services/WorkgroupServiceRegistrationTests.cs`

**Interfaces:**
- Consumes: `IWorkgroupService.SetBudgetAsync`, `IHoldedClient.ListExpenseAccountsAsync` (live chart for the picker), `IUserServiceRead.GetUserInfoAsync` (viewer tier).
- Produces:
  - `WorkgroupBudgetFormViewModel { bool HasBudget; decimal? Amount; string AccountMode ("create" | "link"); int? ExistingAccountNum; }` with `ToSave()` → `WorkgroupBudgetSave(HasBudget ? Amount : null, AccountMode == "link" ? ExistingAccountNum : null)`.
  - `WorkgroupPageViewModel` gains `bool CanSeeBudget`, `IReadOnlyList<HoldedExpenseAccountDto> ExpenseAccounts` (empty unless `CanAdminister`).
  - `RegisterExistingViewModel` gains `WorkgroupBudgetFormViewModel Budget`.
  - `WorkgroupBootstrap` gains `WorkgroupBudgetSave? Budget` (positional, last).
  - `POST /Workgroups/Admin/{id:guid}/Budget` (`WorkgroupsAdminController.Budget(Guid id, WorkgroupBudgetFormViewModel model, string slug, CancellationToken ct)`).

- [ ] **Step 1: Failing controller tests**

Append to `WorkgroupsAdminControllerTests` (reuse the file's controller-building pattern; the existing test around line 36 shows how `http`, `localizer` and `sut` are made — copy that setup into a private helper if one does not exist):

```csharp
    [HumansFact]
    public async Task Budget_ValidForm_SetsBudgetAndRedirectsToTheGroup()
    {
        var workgroup = await SeedWorkgroupAsync(name: "ALM 2027");
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Budget));

        var result = await sut.Budget(workgroup.Id,
            new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = 900m, AccountMode = "create" },
            workgroup.Slug, Ct);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ControllerName.Should().Be("Workgroups");
        redirect.ActionName.Should().Be("Details");
        redirect.RouteValues!["slug"].Should().Be(workgroup.Slug);
        await using var ctx = OpenContext();
        (await ctx.Workgroups.SingleAsync(w => w.Id == workgroup.Id, Ct)).BudgetAmount.Should().Be(900m);
    }

    [HumansFact]
    public async Task Budget_RuleFailure_RedirectsWithError()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Withdrawn);
        var sut = MakeAdminController(nameof(WorkgroupsAdminController.Budget));

        var result = await sut.Budget(workgroup.Id,
            new WorkgroupBudgetFormViewModel { HasBudget = true, Amount = 10m, AccountMode = "create" },
            workgroup.Slug, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        sut.TempData.Should().ContainKey(TempDataKeys.ErrorMessage);
    }
```
`TempDataKeys` lives in `Humans.Base` (the key `HumansControllerBase.SetError` writes).

Append to `WorkgroupsControllerAuthorizationTests` a case proving the budget route is not reachable by a member: the file already asserts `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` on `WorkgroupsAdminController` class-wide, so add:

```csharp
    [HumansFact]
    public void Budget_IsOnTheAdminController_SoBoardOrAdminGatesIt()
    {
        var method = typeof(WorkgroupsAdminController).GetMethod(nameof(WorkgroupsAdminController.Budget));
        method.Should().NotBeNull();
        typeof(WorkgroupsAdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Should().Contain(a => a.Policy == PolicyNames.BoardOrAdmin);
    }
```

Also in `WorkgroupsControllerAuthorizationTests`, a visibility test for the budget block. The file builds `WorkgroupsController` around line 229 with `NewService(), Users, Teams, localizer, Clock, IAuthorizationService, logger`; the controller gains an `IHoldedClient` parameter in Step 6, so add a harness property `private protected IHoldedClient Holded { get; } = Substitute.For<IHoldedClient>();` (returns an empty chart unstubbed) and pass it in that constructor call and in this test:

```csharp
    [HumansTheory]
    [InlineData(nameof(MembershipTier.Volunteer), false)]
    [InlineData(nameof(MembershipTier.Colaborador), true)]
    [InlineData(nameof(MembershipTier.Asociado), true)]
    public async Task Details_BudgetBlockVisibility_FollowsMembershipTier(string tierName, bool expected)
    {
        var tier = Enum.Parse<MembershipTier>(tierName);
        var workgroup = await SeedWorkgroupAsync();
        var viewer = SeedUser("Viewer", profile: null);
        // SeedUser builds a UserInfo; set the tier the way the harness's UserInfoFor allows (add a
        // `tier` parameter to SeedUser defaulting to Volunteer if it has none).
        var controller = BuildController(viewer, isBoard: false);

        var result = await controller.Details(workgroup.Slug, Ct);

        var vm = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<WorkgroupPageViewModel>().Which;
        vm.CanSeeBudget.Should().Be(expected);
    }
```
`BuildController(actorId, isBoard)` is the existing construction extracted into a helper if the file does not already have one; `SeedUser` needs a `MembershipTier tier = MembershipTier.Volunteer` parameter threaded into `UserInfoFor` — check `UserInfoFor` in the harness and add it.

Append to `WorkgroupServiceRegistrationTests`:

```csharp
    [HumansFact]
    public async Task RegisterExisting_WithBudget_BindsTheAccount()
    {
        var coordinator = SeedUser("Coordinator");
        var bootstrap = new WorkgroupBootstrap(
            new WorkgroupApplication("ALM 2027", "Purpose", "A report", WorkgroupDeliverableKind.Report,
                WorkgroupAudience.Board, null, null, null),
            coordinator, Clock.GetCurrentInstant(),
            new WorkgroupBudgetSave(1200m, null));

        var id = await NewService().RegisterExistingAsync(SeedUser("Secretary"), bootstrap, Ct);

        await using var ctx = OpenContext();
        var w = await ctx.Workgroups.SingleAsync(x => x.Id == id, Ct);
        w.BudgetAmount.Should().Be(1200m);
        w.HoldedAccountNumber.Should().Be(62900150);
    }
```
Existing `WorkgroupBootstrap` constructions in tests get a trailing `null` (or use a named default — make the new parameter `WorkgroupBudgetSave? Budget = null`).

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet --filter "FullyQualifiedName~Budget_|FullyQualifiedName~RegisterExisting_WithBudget"
```
Expected: build errors.

- [ ] **Step 3: View models**

`WorkgroupViewModels.cs`:

```csharp
/// <summary>The Board/Admin budget form on the group page and on the bootstrap form.</summary>
internal sealed class WorkgroupBudgetFormViewModel
{
    public bool HasBudget { get; set; }

    [Range(0, 99_999_999)]
    public decimal? Amount { get; set; }

    /// <summary>"create" (default) or "link".</summary>
    public string AccountMode { get; set; } = "create";

    public int? ExistingAccountNum { get; set; }

    public WorkgroupBudgetSave ToSave() => new(
        HasBudget ? Amount : null,
        string.Equals(AccountMode, "link", StringComparison.Ordinal) ? ExistingAccountNum : null);
}
```
`WorkgroupPageViewModel`: add

```csharp
    /// <summary>Board/Admin, Colaborador and Asociado see the allocation; Volunteers do not.</summary>
    public required bool CanSeeBudget { get; init; }

    /// <summary>The live Holded expense chart for the "link existing" picker; empty unless the viewer can administer.</summary>
    public IReadOnlyList<HoldedExpenseAccountDto> ExpenseAccounts { get; init; } = [];
```
`RegisterExistingViewModel`: add `public WorkgroupBudgetFormViewModel Budget { get; set; } = new();`.
Add `using Humans.Holded.Contracts;` to the file.

`WorkgroupDtos.cs`: `WorkgroupBootstrap` becomes

```csharp
internal sealed record WorkgroupBootstrap(
    WorkgroupApplication Application,
    Guid CoordinatorUserId,
    Instant RegisteredAt,
    WorkgroupBudgetSave? Budget = null);
```

- [ ] **Step 4: Service — bootstrap applies the budget**

In `RegisterExistingAsync`, after `await RequestDriveSyncAsync(workgroup, ct);` and before `return workgroup.Id;`:

```csharp
        if (bootstrap.Budget is { Amount: not null } budget)
            await SetBudgetAsync(workgroup.Id, actorUserId, budget, ct);
```

- [ ] **Step 5: Admin controller**

In `WorkgroupsAdminController`, after `RegisterExisting` POST:

```csharp
    // ── Budget ────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/Budget")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Budget(Guid id, WorkgroupBudgetFormViewModel model, string slug, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (!ModelState.IsValid)
        {
            SetError("Check the budget amount.");
            return RedirectToAction("Details", "Workgroups", new { slug });
        }

        try
        {
            var account = await workgroups.SetBudgetAsync(id, user.Id, model.ToSave(), ct);
            SetSuccess(account switch
            {
                { Created: true } => $"Budget saved; created Holded account {account.AccountNum} '{account.Name}'.",
                { Created: false } => $"Budget saved; linked to existing Holded account {account.AccountNum} '{account.Name}'.",
                null => "Budget saved.",
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (WorkgroupRuleException ex)
        {
            logger.LogInformation(ex, "Workgroups admin Budget: rule {Rule}", ex.Key);
            SetError(localizer[ex.Key, ex.Args]);
        }
        return RedirectToAction("Details", "Workgroups", new { slug });
    }
```
In `RegisterExisting` POST, pass `model.Budget.ToSave()` as the bootstrap's fourth argument.

- [ ] **Step 6: Member controller — Details**

`WorkgroupsController.Details`: add `IHoldedClient holded` to the controller's primary constructor right after `IClock clock` (add `using Humans.Holded.Contracts;` and `using Humans.Users.Contracts;`), then:

```csharp
        var canAdminister = await MayAdministerAsync(workgroup);
        var viewer = await users.GetUserInfoAsync(user.Id, ct);
        var canSeeBudget = canAdminister
            || viewer?.MembershipTier is MembershipTier.Colaborador or MembershipTier.Asociado;
        IReadOnlyList<HoldedExpenseAccountDto> accounts = [];
        if (canAdminister)
        {
            try { accounts = await holded.ListExpenseAccountsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Holded chart unavailable; the link-existing picker is empty");
            }
        }
```
and set `CanAdminister = canAdminister, CanSeeBudget = canSeeBudget, ExpenseAccounts = accounts` on the view model. Use whatever the controller's `IUserServiceRead` field is called (`users` per `HumansControllerBase(users)`) and its logger. Confirm the `MembershipTier` enum member names in `src/Sections/Humans.Users.Contracts/MembershipTier.cs`.

Every other place that constructs `WorkgroupPageViewModel` (the `Edit` GET around line 278 and any tests) gets `CanSeeBudget = false` (or the same computation where cheap).

- [ ] **Step 7: Details view**

In `Details.cshtml`, after the register-facts card (the `</div>` closing `<div class="card mb-3">` that holds Purpose/Deliverable/Discord/Drive) add:

```cshtml
@if (Model.CanSeeBudget && w.HasBudget)
{
    <div class="card mb-3">
        <div class="card-body d-flex flex-wrap gap-4 align-items-baseline">
            <div>
                <div class="text-uppercase text-muted small">@Localizer["Workgroups_Budget"]</div>
                <div class="fs-5 fw-semibold">@w.BudgetAmount!.Value.ToString("N2") €</div>
            </div>
            @if (w.HoldedAccountNumber is { } acct)
            {
                <div>
                    <div class="text-uppercase text-muted small">@Localizer["Workgroups_BudgetAccount"]</div>
                    <code>@acct</code>
                </div>
            }
        </div>
    </div>
}

@if (Model.CanAdminister && w.Status is not (WorkgroupStatus.Refused or WorkgroupStatus.Withdrawn))
{
    <details class="card mb-3">
        <summary class="card-header">Budget (Board/Admin)</summary>
        <div class="card-body">
            <form method="post" asp-controller="WorkgroupsAdmin" asp-action="Budget" asp-route-id="@w.Id" asp-route-slug="@w.Slug">
                <div class="form-check mb-2">
                    <input class="form-check-input" type="checkbox" id="budget-has" name="HasBudget" value="true" @(w.HasBudget ? "checked" : "") />
                    <label class="form-check-label" for="budget-has">This workgroup has a budget</label>
                </div>
                <div class="mb-3" style="max-width: 240px;">
                    <label class="form-label" for="budget-amount">Amount (EUR)</label>
                    <input class="form-control" type="number" step="0.01" min="0" id="budget-amount" name="Amount" value="@w.BudgetAmount" />
                </div>
                @if (w.HoldedAccountNumber is { } bound)
                {
                    <p class="small text-muted mb-2">Booked to Holded account <code>@bound</code>. Leave "Keep" unless you mean to rebind.</p>
                    <div class="form-check"><input class="form-check-input" type="radio" name="AccountMode" id="mode-keep" value="create" checked /><label class="form-check-label" for="mode-keep">Keep account @bound</label></div>
                }
                else
                {
                    <div class="form-check"><input class="form-check-input" type="radio" name="AccountMode" id="mode-create" value="create" checked /><label class="form-check-label" for="mode-create">Create <code>Workgroups / @w.Name</code> in Holded</label></div>
                }
                <div class="form-check mb-2"><input class="form-check-input" type="radio" name="AccountMode" id="mode-link" value="link" /><label class="form-check-label" for="mode-link">Link an existing account</label></div>
                <select class="form-select mb-3" name="ExistingAccountNum" style="max-width: 480px;">
                    <option value="">— choose —</option>
                    @foreach (var a in Model.ExpenseAccounts.OrderBy(a => a.AccountNum))
                    {
                        <option value="@a.AccountNum">@a.AccountNum — @a.Name</option>
                    }
                </select>
                <button type="submit" class="btn btn-sm btn-primary">Save budget</button>
            </form>
        </div>
    </details>
}
```
The "Keep account" radio posts `AccountMode=create` with an account already bound, which `SetBudgetAsync` treats as amount-only — that is the spec's "defaults to keeping it".

- [ ] **Step 8: RegisterExisting view**

In `RegisterExisting.cshtml`, before the submit button, add:

```cshtml
            <fieldset class="border rounded p-3 mb-3">
                <legend class="fs-6">Budget</legend>
                <div class="form-check mb-2">
                    <input asp-for="Budget.HasBudget" class="form-check-input" />
                    <label asp-for="Budget.HasBudget" class="form-check-label">This workgroup has a budget</label>
                </div>
                <div class="mb-2" style="max-width: 240px;">
                    <label asp-for="Budget.Amount" class="form-label">Amount (EUR)</label>
                    <input asp-for="Budget.Amount" type="number" step="0.01" min="0" class="form-control" />
                    <span asp-validation-for="Budget.Amount" class="text-danger"></span>
                </div>
                <p class="small text-muted mb-0">The Holded account <code>Workgroups / {name}</code> is created on save, or linked when one of that name already exists. Use the group page to link a differently named account.</p>
            </fieldset>
```
The bootstrap form is create-only (`AccountMode` stays "create"); the group page carries the full picker.

- [ ] **Step 9: Run the section tests**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet
```
Expected: all PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Sections/Humans.Workgroups tests/Humans.Workgroups.Tests
git commit -m "feat(Workgroups): budget form for Board/Admin, budget block for association members

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Workgroups — localization, docs, dependency graph

**Files:**
- Modify: `src/Sections/Humans.Workgroups/WorkgroupsResource.resx` and the `.es`, `.de`, `.it`, `.fr`, `.ca` files
- Modify: `src/Sections/Humans.Workgroups/Docs/Workgroups.md`, `Docs/authorization.md`, `Docs/data-access.md`
- Modify: `docs/architecture/dependency-graph.md`

- [ ] **Step 1: Resx keys, all six files**

Add to each resx (values per culture):

| Key | en | es | de | it | fr | ca |
|-----|----|----|----|----|----|----|
| `Workgroups_Budget` | Budget | Presupuesto | Budget | Budget | Budget | Pressupost |
| `Workgroups_BudgetAccount` | Holded account | Cuenta de Holded | Holded-Konto | Conto Holded | Compte Holded | Compte de Holded |
| `Enum_WorkgroupLogKind_BudgetSet` | Budget set | Presupuesto asignado | Budget festgelegt | Budget assegnato | Budget défini | Pressupost assignat |
| `Workgroups_Error_BudgetNegative` | The budget amount cannot be negative. | El importe del presupuesto no puede ser negativo. | Der Budgetbetrag darf nicht negativ sein. | L'importo del budget non può essere negativo. | Le montant du budget ne peut pas être négatif. | L'import del pressupost no pot ser negatiu. |
| `Workgroups_Error_BudgetAccountFailed` | Finance could not create or link the Holded account. Nothing was saved. | Finanzas no pudo crear ni vincular la cuenta de Holded. No se guardó nada. | Finanzen konnte das Holded-Konto weder anlegen noch verknüpfen. Nichts wurde gespeichert. | Finanza non ha potuto creare o collegare il conto Holded. Nulla è stato salvato. | Finance n'a pas pu créer ni lier le compte Holded. Rien n'a été enregistré. | Finances no ha pogut crear ni vincular el compte de Holded. No s'ha desat res. |

Match the existing `<data name="…" xml:space="preserve"><value>…</value></data>` one-line style.

- [ ] **Step 2: Run the parity test**

```bash
dotnet test tests/Humans.Workgroups.Tests -v quiet
```
Find the resource-parity test in the solution if the section project does not carry one (`grep -rln "parity" tests --include=*.cs`) and run that project too. Expected: PASS.

- [ ] **Step 3: Workgroups.md**

- Concepts: add *A **Budget** is an optional EUR allocation on the register plus the Holded expense account the group's spending books to. Finance owns the account's creation and naming ("Workgroups / {Name}"); Workgroups owns the amount and the reference. Visible to Board/Admin, Colaborador and Asociado.*
- Data Model / Workgroup table: three rows — `BudgetAmount | decimal(18,2)? | Null = no budget`, `HoldedAccountNumber | int? | Bare external reference, no FK; kept when the budget is cleared`, `HoldedAccountId | string(64)? |`.
- `WorkgroupLogKind` list: add `BudgetSet` to the system kinds.
- Routing: `/Workgroups/Admin/{id}/Budget | Set, change or clear the budget; BoardOrAdmin`.
- Actors: Board/Admin row gains "set the budget and its Holded account".
- Invariants: *Budget: amount ≥ 0 or null; Refused/Withdrawn rejected; Finance is called before anything is written, so a failed create or an unknown account number changes nothing; the first account bound is created as "Workgroups / {Name}" or linked to an existing account of that normalized name; clearing keeps the binding; rebinding never touches the old account.*
- Triggers: *SetBudget: system log entry (`BudgetSet`, attributed to the actor), audit entry. Close/Done/Withdraw: retire the account in Finance's registry; Reactivate restores it; a Finance failure there is logged, never blocks the transition.*
- Cross-Section Dependencies: *Finance: `IHoldedFinanceService.CreateOrLinkExpenseAccountAsync` / `SetExpenseAccountActiveAsync` (outbound). Holded: `IHoldedClient.ListExpenseAccountsAsync` for the admin picker only.*
- Architecture → Cross-section calls: add `IHoldedFinanceService`, `IHoldedClient`.
- Update the freshness `flag-on-change` comment to mention the budget rules.

- [ ] **Step 4: authorization.md and data-access.md**

`authorization.md`: add to the admin paragraph "…, bootstrapping, settings and the budget." and a bullet *A member or coordinator cannot set the budget; the route lives on the admin controller.*

`data-access.md`: in the cross-section reference sentence add `HoldedAccountNumber`/`HoldedAccountId` as bare external references (no FK).

- [ ] **Step 5: Dependency graph**

In `docs/architecture/dependency-graph.md`, add edges `Workgroups --> HoldedFinance` (or whatever node name the Finance service carries there) and `Workgroups --> HoldedClient`, following the file's node naming. Read the node list first.

- [ ] **Step 6: Commit**

```bash
git add src/Sections/Humans.Workgroups docs/architecture/dependency-graph.md
git commit -m "docs(Workgroups): budget strings in six cultures, invariants, auth, dependency graph

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Full gate, push, PR

- [ ] **Step 1: Build and test the whole solution**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```
Expected: 0 warnings-as-errors from analyzers, all tests PASS (integration tests self-skip locally under CI-like conditions; skipped is fine).

If an architecture test or analyzer flags the new Workgroups → Finance.Contracts / Holded.Contracts references, read the failure: a baseline that lists allowed section references needs the two new edges added in the same commit, with the reason in the commit body. Never suppress.

- [ ] **Step 2: Migration verification**

```bash
dotnet ef migrations list --context FinanceDbContext --project src/Sections/Humans.Finance --startup-project src/Humans.Web
dotnet ef migrations list --context WorkgroupsDbContext --project src/Sections/Humans.Workgroups --startup-project src/Humans.Web
```
Expected: each chain ends with this branch's single new migration.

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin feat/workgroup-budget
```
```bash
gh pr create --repo peterdrier/Humans --base main --title "Workgroup budgets: amount on the register, Holded account through Finance" --body "$(cat <<'EOF'
Phase 1 of `docs/features/global/workgroup-budget.md`.

- Workgroups: `BudgetAmount`, `HoldedAccountNumber`, `HoldedAccountId`; Board/Admin budget form on the group page and the bootstrap form; budget block visible to Board/Admin, Colaborador, Asociado. Close/Done/Withdraw retire the account, Reactivate restores it.
- Finance: `CreateOrLinkExpenseAccountAsync` (name-normalized dedup, block allocation past map + registry + live chart), `SetExpenseAccountActiveAsync`, `ListExpenseAccountsAsync`; `holded_managed_accounts` registry; doc sync attributes managed accounts so they stay off Unmatched and out of budget-year actuals; `/Finance/Holded` lists the registry.
- Phase 2 (Expenses retargets onto Finance's account list, no endorsement for unmapped accounts) is a separate PR.

Surfaces walked: six cultures for member-visible strings (admin form exempt), BoardOrAdmin on the route with a negative test, audit + system log on every budget change, no new personal data, Workgroups.md/Finance.md/authorization.md/data-access.md updated, one migration per section, navigation is the group page itself.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 4: Verify on the preview**

Once the preview deploy is up at `https://<pr>.n.burn.camp`: as Admin, open a workgroup, set a budget with "create", confirm the success message names the account and `/Finance/Holded` lists it under Managed accounts; save again with the same amount and confirm no second account; log in as a Volunteer and confirm the budget block is absent.
