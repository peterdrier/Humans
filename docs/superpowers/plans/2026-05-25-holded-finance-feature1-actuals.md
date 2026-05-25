# Holded Expense Actuals (Feature 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pull Holded expense docs and attribute each to a `BudgetCategory` (dedicated account → tag → unmatched bucket), surfacing actual-vs-allocated per category plus an unmatched-cleanup queue, with an in-app page that provisions the per-category Holded accounts.

**Architecture:** New **Finance**-section Application/Infrastructure code (the section is currently a Budget-backed UI shell — design-rules §15h(1), starts at (A)). A nightly job pulls paginated purchase docs via an extended `IHoldedClient`, matches each line to a category, and upserts into Finance-owned tables (`holded_expense_docs`, `holded_category_map`, `holded_sync_states`). Those tables *are* the cache — no §15 caching decorator is needed. Cross-section reads go through `IBudgetService`; the Finance service holds no EF types and no cross-section repositories (pinned by architecture tests).

**Tech Stack:** .NET, EF Core (Npgsql), NodaTime, Hangfire (`IRecurringJob`), ASP.NET MVC, xUnit + AwesomeAssertions (`[HumansFact]`).

**Spec:** `docs/superpowers/specs/2026-05-25-holded-finance-integration-design.md` (Feature 1 + provisioning + §1 API findings + §6 probes).

**Worktree:** already created at `.worktrees/holded-finance` on branch `feat/holded-finance-integration`. All work happens there.

---

## Conventions for this plan

- **"Mirror `<file>`"** means: copy that file's structure/style exactly and adapt the named bits. Used for mechanical boilerplate (EF configs, DI, views, HTTP-mock test scaffolding) where reproducing canonical code verbatim here would drift from the source of truth. Read the named file first.
- **Build/test commands** (from the worktree root, per `memory/process/dotnet-verbosity-quiet.md`):
  - Build: `dotnet build Humans.slnx -v quiet`
  - Test one class: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~<ClassName>"`
- **Commit** after each task (frequent commits). Do **not** push until a review checkpoint; when pushing, `git push origin feat/holded-finance-integration`.
- **EF migrations:** never hand-edit. Generate via the CLI (Task 5). See `memory/architecture/` migration rules.

---

## File Structure

**Create:**
- `src/Humans.Application/Interfaces/Holded/HoldedReadDtos.cs` — read-response DTOs (expense account, chart row, purchase doc + line).
- `src/Humans.Domain/Entities/HoldedExpenseDoc.cs`, `HoldedCategoryMap.cs`, `HoldedSyncState.cs`
- `src/Humans.Domain/Enums/HoldedMatchStatus.cs`, `HoldedMatchSource.cs`, `HoldedSyncStatus.cs`
- `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedExpenseDocConfiguration.cs`, `HoldedCategoryMapConfiguration.cs`, `HoldedSyncStateConfiguration.cs`
- `src/Humans.Application/Interfaces/Finance/IHoldedRepository.cs`, `IHoldedFinanceService.cs`
- `src/Humans.Application/Services/Finance/Dtos/` — view/result DTOs (provisioning plan, actuals, unmatched row).
- `src/Humans.Application/Services/Finance/HoldedMatcher.cs` — pure tag-normalize + match logic.
- `src/Humans.Application/Services/Finance/HoldedFinanceService.cs`
- `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs`
- `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`
- `src/Humans.Web/Views/Finance/HoldedAccounts.cshtml`, `HoldedUnmatched.cshtml`
- `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`
- `tests/Humans.Application.Tests/Finance/HoldedMatcherTests.cs`, `HoldedFinanceServiceTests.cs`
- `tests/Humans.Infrastructure.Tests/Holded/HoldedClientReadTests.cs`

**Modify:**
- `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs` — add read + create-account methods.
- `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs` — implement them.
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add three `DbSet`s.
- `src/Humans.Web/Controllers/FinanceController.cs` — add Holded routes + actuals on the budget view.
- `src/Humans.Web/Extensions/Sections/HoldedSectionExtensions.cs` (or the Finance section extension) — register repo, service, job.
- `docs/sections/Finance.md`, `docs/superpowers/specs/2026-04-26-holded-read-integration-design.md` — doc updates.

---

## Task 1: Holded read DTOs

**Files:**
- Create: `src/Humans.Application/Interfaces/Holded/HoldedReadDtos.cs`

- [ ] **Step 1: Create the DTO file**

```csharp
using NodaTime;

namespace Humans.Application.Interfaces.Holded;

/// <summary>A P&L expense account from Holded (`expensesaccounts` / chart).</summary>
public sealed record HoldedExpenseAccountDto
{
    public required string Id { get; init; }
    public required int AccountNum { get; init; }
    public required string Name { get; init; }
}

/// <summary>One purchase-document line: carries its booked account id and tags.</summary>
public sealed record HoldedPurchaseLineDto
{
    public required decimal Amount { get; init; }      // line `price`
    public string? AccountId { get; init; }            // line `account` (Holded account id)
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A purchase document as returned by the list endpoint.</summary>
public sealed record HoldedPurchaseDocListItemDto
{
    public required string Id { get; init; }
    public required string DocNumber { get; init; }
    public required string ContactName { get; init; }
    public required Instant Date { get; init; }        // doc `date` (epoch s)
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public Instant? ApprovedAt { get; init; }
    public string Currency { get; init; } = "eur";
    public IReadOnlyList<string> Tags { get; init; } = []; // doc-level tags
    public IReadOnlyList<HoldedPurchaseLineDto> Lines { get; init; } = [];
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Holded/HoldedReadDtos.cs
git commit -m "feat(finance): add Holded read DTOs for expense accounts and purchase docs"
```

---

## Task 2: Extend IHoldedClient with read + create-account methods

**Files:**
- Modify: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
- Test: `tests/Humans.Infrastructure.Tests/Holded/HoldedClientReadTests.cs`

> Endpoints (all v1, `key` header — confirmed live 2026-05-25):
> `GET /api/invoicing/v1/expensesaccounts`, `GET /api/accounting/v1/chartofaccounts`,
> `GET /api/invoicing/v1/documents/purchase?page=N`, `POST /api/invoicing/v1/expensesaccounts`.

- [ ] **Step 1: Add methods to the interface**

```csharp
    /// <summary>Lists all P&L expense accounts (id + number + name).</summary>
    Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default);

    /// <summary>Creates a P&L expense account; returns the new account id.</summary>
    Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default);

    /// <summary>Reads one page of purchase documents (1-based). Empty list = past the end.</summary>
    Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsPageAsync(
        int page, int limit, CancellationToken ct = default);
```

- [ ] **Step 2: Write failing tests**

Mirror the mocked-`HttpMessageHandler` scaffolding already used for the Holded client in `tests/Humans.Infrastructure.Tests/` (search: `class HoldedClient` tests, or the `TicketTailor` HTTP tests). Each test wires a handler returning a canned body and asserts the parsed DTO + the request shape (path, `key` header).

```csharp
[HumansFact]
public async Task ListPurchaseDocumentsPage_parses_lines_account_and_tags()
{
    const string body = """
    [{"id":"d1","docNumber":"V1","contactName":"ACME","date":1779141600,
      "subtotal":100.0,"tax":21.0,"total":121.0,"approvedAt":1779228000,"currency":"eur",
      "tags":["adminstaff"],
      "products":[{"price":100.0,"account":"acc-629","tags":["adminstaff"]}]}]
    """;
    var client = BuildClient(body); // handler returns `body` with 200
    var page = await client.ListPurchaseDocumentsPageAsync(1, 100);

    page.Should().HaveCount(1);
    page[0].Lines.Should().ContainSingle();
    page[0].Lines[0].AccountId.Should().Be("acc-629");
    page[0].Lines[0].Tags.Should().ContainSingle().Which.Should().Be("adminstaff");
    page[0].Date.Should().Be(Instant.FromUnixTimeSeconds(1779141600));
}

[HumansFact]
public async Task ListExpenseAccounts_parses_num_and_name()
{
    const string body = """[{"id":"a1","name":"Otros servicios","accountNum":62900000}]""";
    var client = BuildClient(body);
    var accts = await client.ListExpenseAccountsAsync();
    accts.Should().ContainSingle();
    accts[0].AccountNum.Should().Be(62900000);
}
```

- [ ] **Step 3: Run tests — expect FAIL** (methods not implemented)

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedClientReadTests"`
Expected: FAIL (compile error / NotImplemented).

- [ ] **Step 4: Implement in HoldedClient** (reuse existing `AttachAuth`, `SendAsync`, `ReadDecimal`, `ReadInstant`)

```csharp
public async Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
    CancellationToken ct = default)
{
    using var req = new HttpRequestMessage(HttpMethod.Get,
        "/api/invoicing/v1/expensesaccounts");
    AttachAuth(req);
    using var resp = await SendAsync(req, ct);
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
    return arr.Select(n => new HoldedExpenseAccountDto
    {
        Id = n!["id"]?.GetValue<string>() ?? "",
        AccountNum = (int)(n["accountNum"]?.GetValue<long>() ?? 0),
        Name = n["name"]?.GetValue<string>() ?? "",
    }).ToList();
}

public async Task<string> CreateExpenseAccountAsync(
    int accountNum, string name, CancellationToken ct = default)
{
    var payload = new { name, accountNum };          // NOTE: confirm field names against probe #1
    using var req = new HttpRequestMessage(HttpMethod.Post,
        "/api/invoicing/v1/expensesaccounts") { Content = JsonContent.Create(payload) };
    AttachAuth(req);
    using var resp = await SendAsync(req, ct);
    var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
        ?? throw new HoldedTransientException("Holded returned empty body");
    return node["id"]?.GetValue<string>()
        ?? throw new HoldedTransientException("Holded create-account response missing id");
}

public async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsPageAsync(
    int page, int limit, CancellationToken ct = default)
{
    using var req = new HttpRequestMessage(HttpMethod.Get,
        $"/api/invoicing/v1/documents/purchase?page={page}&limit={limit}");
    AttachAuth(req);
    using var resp = await SendAsync(req, ct);
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
    return arr.Select(ParsePurchaseDoc).ToList();
}

private static HoldedPurchaseDocListItemDto ParsePurchaseDoc(JsonNode? n) => new()
{
    Id = n!["id"]?.GetValue<string>() ?? "",
    DocNumber = n["docNumber"]?.GetValue<string>() ?? "",
    ContactName = n["contactName"]?.GetValue<string>() ?? "",
    Date = ReadInstant(n["date"]) ?? Instant.FromUnixTimeSeconds(0),
    Subtotal = ReadDecimal(n["subtotal"]),
    Tax = ReadDecimal(n["tax"]),
    Total = ReadDecimal(n["total"]),
    ApprovedAt = ReadInstant(n["approvedAt"]),
    Currency = n["currency"]?.GetValue<string>() ?? "eur",
    Tags = ReadTags(n["tags"]),
    Lines = (n["products"]?.AsArray() ?? []).Select(p => new HoldedPurchaseLineDto
    {
        Amount = ReadDecimal(p!["price"]),
        AccountId = p["account"]?.GetValue<string>(),
        Tags = ReadTags(p["tags"]),
    }).ToList(),
};

private static IReadOnlyList<string> ReadTags(JsonNode? node) =>
    node?.AsArray().Where(t => t is not null).Select(t => t!.GetValue<string>()).ToList() ?? [];
```

- [ ] **Step 5: Run tests — expect PASS**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedClientReadTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Holded/IHoldedClient.cs src/Humans.Infrastructure/Services/Holded/HoldedClient.cs tests/Humans.Infrastructure.Tests/Holded/HoldedClientReadTests.cs
git commit -m "feat(finance): extend IHoldedClient with expense-account + paginated purchase-doc reads"
```

---

## Task 3: Domain entities + enums

**Files:**
- Create: `src/Humans.Domain/Enums/HoldedMatchStatus.cs`, `HoldedMatchSource.cs`, `HoldedSyncStatus.cs`
- Create: `src/Humans.Domain/Entities/HoldedExpenseDoc.cs`, `HoldedCategoryMap.cs`, `HoldedSyncState.cs`

- [ ] **Step 1: Enums**

```csharp
namespace Humans.Domain.Enums;

public enum HoldedMatchStatus { Matched, Unmatched }

// Why a line/doc resolved (or didn't) — drives the unmatched-bucket reason text.
public enum HoldedMatchSource { None, Account, Tag }

public enum HoldedSyncStatus { Idle, Running, Error }
```

- [ ] **Step 2: Entities** (no cross-domain navigation properties — FK-only per design-rules)

```csharp
// HoldedCategoryMap.cs
using NodaTime;
namespace Humans.Domain.Entities;

/// <summary>Maps a BudgetCategory to its dedicated Holded account + fallback tag.</summary>
public class HoldedCategoryMap
{
    public Guid Id { get; init; }
    public Guid BudgetCategoryId { get; set; }     // FK-only, no nav (cross-section)
    public int HoldedAccountNumber { get; set; }
    public string HoldedAccountId { get; set; } = "";
    public string Tag { get; set; } = "";          // dash-free normalized fallback key
    public bool IsActive { get; set; } = true;
    public Instant? ArchivedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
```

```csharp
// HoldedExpenseDoc.cs
using Humans.Domain.Enums;
using NodaTime;
namespace Humans.Domain.Entities;

/// <summary>A Holded purchase doc pulled + attributed to a budget category.</summary>
public class HoldedExpenseDoc
{
    public Guid Id { get; init; }
    public string HoldedDocId { get; set; } = "";  // unique upsert key
    public string DocNumber { get; set; } = "";
    public string ContactName { get; set; } = "";
    public LocalDate Date { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "eur";
    public Instant? ApprovedAt { get; set; }
    public string TagsJson { get; set; } = "[]";    // raw tags, jsonb
    public string? BookedAccountId { get; set; }    // first line's account id
    public Guid? BudgetCategoryId { get; set; }     // FK-only, null = unmatched
    public HoldedMatchStatus MatchStatus { get; set; }
    public HoldedMatchSource MatchSource { get; set; }
    public string RawPayload { get; set; } = "{}";  // jsonb, debugging
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
```

```csharp
// HoldedSyncState.cs  (singleton, Id = 1) — mirror TicketSyncState
using Humans.Domain.Enums;
using NodaTime;
namespace Humans.Domain.Entities;

public class HoldedSyncState
{
    public int Id { get; init; } = 1;
    public Instant? LastSyncAt { get; set; }
    public HoldedSyncStatus SyncStatus { get; set; } = HoldedSyncStatus.Idle;
    public string? LastError { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Domain/Entities/Holded*.cs src/Humans.Domain/Enums/Holded*.cs
git commit -m "feat(finance): add Holded actuals domain entities + enums"
```

---

## Task 4: EF configurations + DbSets

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedExpenseDocConfiguration.cs`, `HoldedCategoryMapConfiguration.cs`, `HoldedSyncStateConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Configurations** — mirror `Configurations/Expenses/HoldedExpenseOutboxEventConfiguration.cs`. Enums via `HasConversion<string>().HasMaxLength(32)`; `LocalDate`/`Instant` already have project-wide value converters (confirm in an existing config that uses `LocalDate`, e.g. a Budget config). jsonb columns: `b.Property(x => x.TagsJson).HasColumnType("jsonb")`.

```csharp
// HoldedExpenseDocConfiguration.cs
public class HoldedExpenseDocConfiguration : IEntityTypeConfiguration<HoldedExpenseDoc>
{
    public void Configure(EntityTypeBuilder<HoldedExpenseDoc> b)
    {
        b.ToTable("holded_expense_docs");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedDocId).IsUnique();
        b.HasIndex(x => x.BudgetCategoryId);
        b.HasIndex(x => x.MatchStatus);
        b.HasIndex(x => x.Date);
        b.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.MatchSource).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.HoldedDocId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.TagsJson).HasColumnType("jsonb");
        b.Property(x => x.RawPayload).HasColumnType("jsonb");
    }
}
```

```csharp
// HoldedCategoryMapConfiguration.cs
public class HoldedCategoryMapConfiguration : IEntityTypeConfiguration<HoldedCategoryMap>
{
    public void Configure(EntityTypeBuilder<HoldedCategoryMap> b)
    {
        b.ToTable("holded_category_map");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.BudgetCategoryId).IsUnique();        // one account per category
        b.HasIndex(x => x.HoldedAccountNumber).IsUnique();
        b.Property(x => x.HoldedAccountId).HasMaxLength(64);
        b.Property(x => x.Tag).HasMaxLength(128);
    }
}
```

```csharp
// HoldedSyncStateConfiguration.cs
public class HoldedSyncStateConfiguration : IEntityTypeConfiguration<HoldedSyncState>
{
    public void Configure(EntityTypeBuilder<HoldedSyncState> b)
    {
        b.ToTable("holded_sync_states");
        b.HasKey(x => x.Id);
        b.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(2000);
    }
}
```

- [ ] **Step 2: Add DbSets** to `HumansDbContext` (find the existing `HoldedExpenseOutboxEvents` DbSet and add nearby):

```csharp
public DbSet<HoldedExpenseDoc> HoldedExpenseDocs => Set<HoldedExpenseDoc>();
public DbSet<HoldedCategoryMap> HoldedCategoryMap => Set<HoldedCategoryMap>();
public DbSet<HoldedSyncState> HoldedSyncStates => Set<HoldedSyncState>();
```

(Configurations are applied via `ApplyConfigurationsFromAssembly` — confirm the context already does this; if it registers each explicitly, add the three.)

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet` → success.

- [ ] **Step 4: Generate the migration** (never hand-edit)

```bash
dotnet ef migrations add HoldedActuals --project src/Humans.Infrastructure --startup-project src/Humans.Web -- -v quiet
```
Then `dotnet build Humans.slnx -v quiet`. Inspect the generated migration for the three tables + indexes; if it includes unrelated drift, stop and reconcile model state (see `memory/architecture/` migration rules) — do not edit the migration by hand.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/Finance/ src/Humans.Infrastructure/Data/HumansDbContext.cs src/Humans.Infrastructure/Migrations/
git commit -m "feat(finance): EF config + migration for Holded actuals tables"
```

---

## Task 5: IHoldedRepository + HoldedRepository

**Files:**
- Create: `src/Humans.Application/Interfaces/Finance/IHoldedRepository.cs`
- Create: `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs`

> Repository returns DTOs/entities owned by the section only; no cross-section reads. Mirror `Repositories/Expenses/ExpenseRepository.cs` (factory + `AsNoTracking` + DTO projection).

- [ ] **Step 1: Interface**

```csharp
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Finance;

public interface IHoldedRepository : IRepository
{
    // Category map
    Task<IReadOnlyList<HoldedCategoryMap>> GetCategoryMapAsync(CancellationToken ct = default);
    Task AddCategoryMapAsync(HoldedCategoryMap row, CancellationToken ct = default);
    Task ArchiveCategoryMapAsync(Guid id, CancellationToken ct = default);

    // Docs
    Task UpsertDocsAsync(IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default);

    // Sync state
    Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
}
```

- [ ] **Step 2: Implementation** — mirror `ExpenseRepository`. Key points:
  - Constructor: `internal sealed class HoldedRepository(IDbContextFactory<HumansDbContext> factory, ILogger<HoldedRepository> logger) : IHoldedRepository`.
  - `UpsertDocsAsync`: load existing by `HoldedDocId IN (...)`, update in place (preserve `CreatedAt`), insert the rest. Set `UpdatedAt`/`LastSyncedAt` from an injected source — pass `Instant now` as a parameter from the service (the repo has no `IClock`); add `Instant now` to the method signature.
  - `GetSyncStateAsync`: `FirstOrDefault(Id==1)`; if null, create+save the singleton then return it.
  - `GetMatchedForYearAsync`: `Where(d => d.MatchStatus == Matched && d.Date.Year == calendarYear)`. Add `// arch:db-sort-ok` to any ordered query.

```csharp
public async Task UpsertDocsAsync(
    IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default)
{
    if (docs.Count == 0) return;
    await using var ctx = await factory.CreateDbContextAsync(ct);
    var ids = docs.Select(d => d.HoldedDocId).ToList();
    var existing = await ctx.HoldedExpenseDocs
        .Where(d => ids.Contains(d.HoldedDocId)).ToDictionaryAsync(d => d.HoldedDocId, ct);
    foreach (var d in docs)
    {
        if (existing.TryGetValue(d.HoldedDocId, out var cur))
        {
            cur.DocNumber = d.DocNumber; cur.ContactName = d.ContactName; cur.Date = d.Date;
            cur.Subtotal = d.Subtotal; cur.Tax = d.Tax; cur.Total = d.Total;
            cur.Currency = d.Currency; cur.ApprovedAt = d.ApprovedAt; cur.TagsJson = d.TagsJson;
            cur.BookedAccountId = d.BookedAccountId; cur.BudgetCategoryId = d.BudgetCategoryId;
            cur.MatchStatus = d.MatchStatus; cur.MatchSource = d.MatchSource;
            cur.RawPayload = d.RawPayload; cur.LastSyncedAt = now; cur.UpdatedAt = now;
        }
        else { d.LastSyncedAt = now; ctx.HoldedExpenseDocs.Add(d); }
    }
    await ctx.SaveChangesAsync(ct);
}
```

  (Update the interface signature to include `Instant now` on `UpsertDocsAsync`.)

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Finance/IHoldedRepository.cs src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs
git commit -m "feat(finance): IHoldedRepository + HoldedRepository (docs, map, sync state)"
```

---

## Task 6: HoldedMatcher (pure logic, TDD)

**Files:**
- Create: `src/Humans.Application/Services/Finance/HoldedMatcher.cs`
- Test: `tests/Humans.Application.Tests/Finance/HoldedMatcherTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using AwesomeAssertions;
using Humans.Application.Services.Finance;

namespace Humans.Application.Tests.Finance;

public class HoldedMatcherTests
{
    [HumansFact]
    public void NormalizeTag_strips_separators_and_lowercases()
    {
        HoldedMatcher.NormalizeTag("Admin-Staff").Should().Be("adminstaff");
        HoldedMatcher.NormalizeTag("Operations / Toilets").Should().Be("operationstoilets");
        HoldedMatcher.NormalizeTag("  Comms_2  ").Should().Be("comms2");
    }

    [HumansFact]
    public void Match_prefers_account_over_tag()
    {
        var map = new[]
        {
            new HoldedMatchEntry(CatA, AccountId: "acc-1", AccountNum: 6290001, Tag: "comms"),
            new HoldedMatchEntry(CatB, AccountId: "acc-2", AccountNum: 6290002, Tag: "staff"),
        };
        // line booked to acc-1 but tagged "staff" → account wins → CatA
        var r = HoldedMatcher.Match(bookedAccountId: "acc-1", tags: ["staff"], map);
        r.CategoryId.Should().Be(CatA);
        r.Source.Should().Be(HoldedMatchSource.Account);
    }

    [HumansFact]
    public void Match_falls_back_to_tag_when_account_unmapped()
    {
        var map = new[] { new HoldedMatchEntry(CatB, "acc-2", 6290002, "staff") };
        var r = HoldedMatcher.Match("acc-629-generic", ["Admin-Staff"], map); // tag normalizes, but no "staff"? 
        // adjust: tag "staff"
        var r2 = HoldedMatcher.Match("acc-629-generic", ["staff"], map);
        r2.CategoryId.Should().Be(CatB);
        r2.Source.Should().Be(HoldedMatchSource.Tag);
    }

    [HumansFact]
    public void Match_returns_none_when_nothing_resolves()
    {
        var map = new[] { new HoldedMatchEntry(CatB, "acc-2", 6290002, "staff") };
        var r = HoldedMatcher.Match("acc-generic", ["unknown"], map);
        r.CategoryId.Should().BeNull();
        r.Source.Should().Be(HoldedMatchSource.None);
    }

    private static readonly Guid CatA = Guid.NewGuid();
    private static readonly Guid CatB = Guid.NewGuid();
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedMatcherTests"` → FAIL.

- [ ] **Step 3: Implement**

```csharp
using System.Text;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Finance;

public readonly record struct HoldedMatchEntry(
    Guid CategoryId, string AccountId, int AccountNum, string Tag);

public readonly record struct HoldedMatchResult(Guid? CategoryId, HoldedMatchSource Source);

public static class HoldedMatcher
{
    /// <summary>Lowercase, drop every non-alphanumeric (Holded strips tag separators).</summary>
    public static string NormalizeTag(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Account (A) wins over tag (B); else None.</summary>
    public static HoldedMatchResult Match(
        string? bookedAccountId, IReadOnlyList<string> tags, IReadOnlyList<HoldedMatchEntry> map)
    {
        if (!string.IsNullOrEmpty(bookedAccountId))
        {
            foreach (var e in map)
                if (e.AccountId == bookedAccountId)
                    return new(e.CategoryId, HoldedMatchSource.Account);
        }
        var normTags = tags.Select(NormalizeTag).Where(t => t.Length > 0).ToHashSet();
        if (normTags.Count > 0)
        {
            foreach (var e in map)
                if (normTags.Contains(NormalizeTag(e.Tag)))
                    return new(e.CategoryId, HoldedMatchSource.Tag);
        }
        return new(null, HoldedMatchSource.None);
    }
}
```

- [ ] **Step 4: Run — expect PASS.** Fix the test draft (the `r` in step 1's fallback test is illustrative; keep only `r2`). Then commit.

```bash
git add src/Humans.Application/Services/Finance/HoldedMatcher.cs tests/Humans.Application.Tests/Finance/HoldedMatcherTests.cs
git commit -m "feat(finance): HoldedMatcher — tag normalize + account-then-tag attribution"
```

---

## Task 7: IHoldedFinanceService + HoldedFinanceService

**Files:**
- Create: `src/Humans.Application/Services/Finance/Dtos/HoldedDtos.cs` (result/view DTOs)
- Create: `src/Humans.Application/Interfaces/Finance/IHoldedFinanceService.cs`
- Create: `src/Humans.Application/Services/Finance/HoldedFinanceService.cs`
- Test: `tests/Humans.Application.Tests/Finance/HoldedFinanceServiceTests.cs`

> The service holds `IHoldedRepository`, `IHoldedClient`, `IBudgetService`, `IClock`, `ILogger`. **No EF types, no cross-section repository.** Cross-section category data comes from `IBudgetService` only.

- [ ] **Step 1: Result/view DTOs**

```csharp
namespace Humans.Application.Services.Finance.Dtos;

public sealed record HoldedProvisioningRow(
    Guid BudgetCategoryId, string CategoryName, string GroupName,
    int? ExistingAccountNum, int? ProposedAccountNum, string Tag, string State); // Mapped|ToAdd|Orphan

public sealed record HoldedProvisioningPlan(
    IReadOnlyList<HoldedProvisioningRow> Rows, int NextNumber);

public sealed record HoldedActualRow(Guid BudgetCategoryId, decimal Actual, int DocCount);

public sealed record HoldedUnmatchedRow(
    string HoldedDocId, string DocNumber, string ContactName, decimal Total,
    string Reason, string HoldedUrl);

public sealed record HoldedSyncResult(int DocCount, int Matched, int Unmatched);
```

- [ ] **Step 2: Interface**

```csharp
using Humans.Application.Interfaces;
using Humans.Application.Services.Finance.Dtos;

namespace Humans.Application.Interfaces.Finance;

public interface IHoldedFinanceService : IApplicationService
{
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default); // returns # created
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(int calendarYear, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default);
}
```

- [ ] **Step 3: Write failing tests** (mock `IHoldedRepository`, `IHoldedClient`, `IBudgetService`, `FakeClock`)

```csharp
[HumansFact]
public async Task GetProvisioningPlan_marks_categories_without_accounts_as_ToAdd()
{
    // budget has 2 categories; map has an account for only the first
    budget.SetCategories(("Comms","Departments",CatA), ("Staff","Admin",CatB));
    repo.SetMap(new HoldedCategoryMap{ BudgetCategoryId=CatA, HoldedAccountNumber=62900100, IsActive=true });

    var plan = await svc.GetProvisioningPlanAsync(blockStart: 62900100);

    plan.Rows.Single(r => r.BudgetCategoryId == CatB).State.Should().Be("ToAdd");
    plan.Rows.Single(r => r.BudgetCategoryId == CatA).State.Should().Be("Mapped");
    plan.Rows.Single(r => r.BudgetCategoryId == CatB).ProposedAccountNum.Should().Be(62900101);
}

[HumansFact]
public async Task Sync_attributes_by_account_then_tag_and_counts()
{
    repo.SetMap(new HoldedCategoryMap{ BudgetCategoryId=CatA, HoldedAccountId="acc-1", HoldedAccountNumber=62900100, Tag="comms", IsActive=true });
    client.SetPurchasePages(new[]{ Doc("d1", account:"acc-1", tags:[]),           // → account match
                                   Doc("d2", account:"generic", tags:["comms"]),  // → tag match
                                   Doc("d3", account:"generic", tags:["nope"]) });// → unmatched
    var r = await svc.SyncAsync();
    r.DocCount.Should().Be(3);
    r.Matched.Should().Be(2);
    r.Unmatched.Should().Be(1);
}
```

- [ ] **Step 4: Run — expect FAIL.**

- [ ] **Step 5: Implement.** Core methods:

  - **`GetProvisioningPlanAsync`**: pull categories via `IBudgetService` (active year → groups → categories), active map rows from repo. For each category: Mapped if it has an active map row; else ToAdd with the next free number ≥ `blockStart` (skip numbers already in the map). Orphan = active map rows whose category is no longer present. `Tag = HoldedMatcher.NormalizeTag(group + category)` (compute a stable, unique tag; if collision, append the category short id).
  - **`ProvisionAsync`**: compute plan; for each ToAdd (one if `!addAll`, else all): `client.CreateExpenseAccountAsync(num, name)` → on success `repo.AddCategoryMapAsync(...)`. Name = `$"{group} / {category}"`. Return count.
  - **`SyncAsync`**: set sync state Running; build `HoldedMatchEntry[]` from active map; page `client.ListPurchaseDocumentsPageAsync(page, 100)` until empty; for each doc attribute via first line's account (`BookedAccountId = lines[0].AccountId`) + union of doc+line tags through `HoldedMatcher.Match`; build `HoldedExpenseDoc` (Date = doc.Date in Europe/Madrid → `LocalDate`); `repo.UpsertDocsAsync(docs, now)`; set state Idle + counts. On exception → state Error + `LastError`, rethrow. Counts returned.
  - **`GetActualsForYearAsync`**: `repo.GetMatchedForYearAsync` → group by `BudgetCategoryId` → sum `Total` (only `ApprovedAt != null`, per spec invariant) → `HoldedActualRow`.
  - **`GetUnmatchedAsync`**: `repo.GetUnmatchedAsync` → map to rows; `Reason` from `MatchSource`/tags ("no account, no tag" / "tag not mapped" / "account not mapped"); `HoldedUrl = $"https://app.holded.com/purchases/{HoldedDocId}"` (confirm the deep-link shape at build).

  Multi-line docs: v1 attributes the whole doc by its **first line's** account/tags (spec §6 notes most docs are single-line; line-level split is a later refinement). Document this in a code comment.

- [ ] **Step 6: Run — expect PASS. Build. Commit.**

```bash
git add src/Humans.Application/Interfaces/Finance/IHoldedFinanceService.cs src/Humans.Application/Services/Finance/ tests/Humans.Application.Tests/Finance/HoldedFinanceServiceTests.cs
git commit -m "feat(finance): HoldedFinanceService — provisioning plan, sync, actuals, unmatched"
```

---

## Task 8: Sync job

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`

> Mirror `Jobs/HoldedExpenseOutboxJob.cs` exactly.

- [ ] **Step 1: Implement**

```csharp
using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;

namespace Humans.Infrastructure.Jobs;

/// <summary>Nightly pull of Holded purchase docs → budget-category actuals.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedFinanceService finance) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        finance.SyncAsync(cancellationToken);
}
```

- [ ] **Step 2: Register the recurring schedule.** Find where `HoldedExpenseOutboxJob`/other `IRecurringJob`s are registered with their cron (search `IRecurringJob` / `AddRecurringJob` / `RecurringJob.AddOrUpdate`) and add `HoldedSyncJob` on a nightly cron (e.g. `0 3 * * *`). Match the existing registration pattern.

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs <registration file>
git commit -m "feat(finance): nightly HoldedSyncJob"
```

---

## Task 9: DI wiring

**Files:**
- Modify: the section DI extension (mirror how `ExpenseRepository`/`ExpenseReportService` are registered — find with `AddScoped<IExpenseRepository`).

- [ ] **Step 1: Register**

```csharp
services.AddScoped<IHoldedRepository, HoldedRepository>();
services.AddScoped<IHoldedFinanceService, HoldedFinanceService>();
// HoldedSyncJob: register like the other IRecurringJob implementations
```

Place repository registration with Infrastructure registrations and the service with Application-service registrations, following the existing split. `IHoldedClient` is already registered in `AddHoldedSection`.

- [ ] **Step 2: Build + run a quick smoke**

Run: `dotnet build Humans.slnx -v quiet` → success. (Optional: `dotnet run --project src/Humans.Web` and hit `/Finance` to confirm DI resolves.)

- [ ] **Step 3: Commit**

```bash
git add <di file>
git commit -m "feat(finance): register Holded repo, finance service, sync job"
```

---

## Task 10: FinanceController routes

**Files:**
- Modify: `src/Humans.Web/Controllers/FinanceController.cs`

> Add `IHoldedFinanceService` to the primary constructor. Routes are already under `[Route("Finance")]` + `PolicyNames.FinanceAdminOrAdmin`. Use the existing try/catch + `SetError` pattern.

- [ ] **Step 1: Add actions**

```csharp
[HttpGet("HoldedAccounts")]
public async Task<IActionResult> HoldedAccounts(int blockStart = 62900100)
{
    var plan = await holdedFinance.GetProvisioningPlanAsync(blockStart);
    ViewBag.BlockStart = blockStart;
    return View(plan);
}

[HttpPost("HoldedAccounts/Provision")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ProvisionHoldedAccounts(int blockStart, bool addAll)
{
    try { var n = await holdedFinance.ProvisionAsync(blockStart, addAll);
          SetSuccess($"Created {n} Holded account(s)."); }
    catch (Exception ex) { logger.LogError(ex, "Holded provisioning failed"); SetError("Provisioning failed."); }
    return RedirectToAction(nameof(HoldedAccounts), new { blockStart });
}

[HttpGet("HoldedUnmatched")]
public async Task<IActionResult> HoldedUnmatched()
    => View(await holdedFinance.GetUnmatchedAsync());

[HttpPost("HoldedSync/Run")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> RunHoldedSync()
{
    try { var r = await holdedFinance.SyncAsync();
          SetSuccess($"Synced {r.DocCount} docs ({r.Matched} matched, {r.Unmatched} unmatched)."); }
    catch (Exception ex) { logger.LogError(ex, "Holded sync failed"); SetError("Sync failed."); }
    return RedirectToAction(nameof(HoldedUnmatched));
}
```

(Confirm `SetSuccess` exists on `HumansControllerBase`; if only `SetError` exists, use the existing success/toast mechanism.)

- [ ] **Step 2: Wire actuals onto the budget view.** In `BuildFinanceOverviewAsync`, call `holdedFinance.GetActualsForYearAsync(int.Parse(year.Year))` and attach a `CategoryId → Actual` lookup to the existing overview view model so each category row can show actual vs. allocated. Add the property to that view model; render it in the existing `YearDetail`/category partial.

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Controllers/FinanceController.cs <view model file>
git commit -m "feat(finance): Holded provisioning/unmatched/sync routes + actuals on budget view"
```

---

## Task 11: Views

**Files:**
- Create: `src/Humans.Web/Views/Finance/HoldedAccounts.cshtml`, `HoldedUnmatched.cshtml`

> Mirror an existing `Views/Finance/*.cshtml` for layout, toolbar links, table styling, antiforgery form pattern.

- [ ] **Step 1: `HoldedAccounts.cshtml`** — `@model HoldedProvisioningPlan`. Number-block input (GET form), a table of rows (Category, Group, State badge, existing/proposed number, tag), and two POST buttons: "Add one (test)" (`addAll=false`) and "Add all" (`addAll=true`), both posting to `HoldedAccounts/Provision` with antiforgery. Orphan rows show a "retire" note (archive only; no delete).

- [ ] **Step 2: `HoldedUnmatched.cshtml`** — `@model IReadOnlyList<HoldedUnmatchedRow>`. Table: Doc# | Contact | Total | Reason | → Holded (link to `HoldedUrl`). A "Sync now" POST button to `HoldedSync/Run`. Empty state: "No unmatched documents."

- [ ] **Step 3: Nav links** (no-orphan-pages rule) — add toolbar links to both pages from the existing `/Finance` view.

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Views/Finance/HoldedAccounts.cshtml src/Humans.Web/Views/Finance/HoldedUnmatched.cshtml <finance index view>
git commit -m "feat(finance): Holded provisioning + unmatched-bucket views"
```

---

## Task 12: Architecture tests

**Files:**
- Create: `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`

> Mirror `ExpensesArchitectureTests.cs`.

- [ ] **Step 1: Tests**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Services.Finance;

namespace Humans.Application.Tests.Architecture;

public class FinanceArchitectureTests
{
    [HumansFact]
    public void HoldedFinanceService_DoesNotReferenceEFCore()
    {
        typeof(HoldedFinanceService).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore");
    }

    [HumansFact]
    public void HoldedFinanceService_Constructor_HasNoCrossSectionRepositories()
    {
        var paramTypes = typeof(HoldedFinanceService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();
        var forbidden = paramTypes
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     && t.Name != "IHoldedRepository").ToList();
        forbidden.Should().BeEmpty();
    }

    [HumansFact]
    public void IHoldedFinanceService_LivesIn_FinanceNamespace() =>
        typeof(IHoldedFinanceService).Namespace.Should().Be("Humans.Application.Interfaces.Finance");
}
```

- [ ] **Step 2: Run — expect PASS. Commit.**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~FinanceArchitectureTests"
git add tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs
git commit -m "test(finance): architecture pins (no EF, no cross-section repos)"
```

---

## Task 13: Docs

**Files:**
- Modify: `docs/sections/Finance.md`, `docs/superpowers/specs/2026-04-26-holded-read-integration-design.md`

- [ ] **Step 1:** In `Finance.md`, replace the "Planned" Holded section (the dead `{group-slug}-{category-slug}` dash-split design + `HoldedTransaction`/`MatchStatus` 6-value model) with the shipped design: `holded_expense_docs` / `holded_category_map` / `holded_sync_states`, A-account/B-tag/bucket matching, the `/Finance/HoldedAccounts` + `/Finance/HoldedUnmatched` routes, and flip the Architecture status from (C) Pre-migration to (A). Add `FinanceArchitectureTests` to the "what exists" list.

- [ ] **Step 2:** Add a banner to the top of the 2026-04-26 spec: `> **SUPERSEDED** by docs/superpowers/specs/2026-05-25-holded-finance-integration-design.md — Holded strips tag separators, so the dash-split scheme here never worked.`

- [ ] **Step 3: Commit**

```bash
git add docs/sections/Finance.md docs/superpowers/specs/2026-04-26-holded-read-integration-design.md
git commit -m "docs(finance): update Finance.md to shipped Holded design; mark old spec superseded"
```

---

## Full verification (before pushing for review)

- [ ] `dotnet build Humans.slnx -v quiet` → success
- [ ] `dotnet test Humans.slnx -v quiet` → all green
- [ ] **Clear build-time probe #1 live** before relying on provisioning: run the page's "Add one (test)" against the real account once, confirm it appears in `chartofaccounts`. If `create-expenses-account`'s payload field names differ, fix `CreateExpenseAccountAsync` (Task 2, Step 4) accordingly.
- [ ] `git push origin feat/holded-finance-integration`

---

## Self-review notes (gaps to confirm during execution)

- **`GetYearForDate`**: this plan maps a doc's calendar year to `BudgetYear.Year` (string) to avoid adding cross-section surface. If budget years aren't calendar-aligned, revisit (may need a small `IBudgetService` read method — that's new cross-section surface and needs Peter's approval per the reuse rule).
- **`create-expenses-account` payload** (`{name, accountNum}`) is unverified — probe #1 confirms field names.
- **Holded deep-link URL** shape for unmatched rows — confirm at build.
- **`ApplyConfigurationsFromAssembly`** vs explicit config registration — confirm how `HumansDbContext` registers configs.
- **`SetSuccess`/toast** helper name on `HumansControllerBase` — confirm.
