# Holded Creditor-Balance Exposure (Feature 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show each expense-report submitter what the org owes them and the full payment round-trip, sourced from the Holded creditor balance — and fix the paid-detection bug where treasury pays the **creditor account** (aggregate) rather than per-document.

**Architecture:** Builds on Feature 1's `IHoldedClient` / `HoldedFinanceService` / `HoldedSyncJob` (PR #783). The **Expenses** section enriches the Holded contact on push (legal `name`, burner `tradeName`, `customId`, `iban`, `type=creditor`), stores the returned `contactId` and the resolved `400000xx` supplier-account number on the `ExpenseReport`, and reads creditor status from **Finance**. The **Finance** section's nightly job additionally caches every `400000xx` creditor balance (`chartofaccounts`) and all `payments` rows; it exposes a read method `GetCreditorStatusAsync(accountNum, contactId)`. The submitter timeline and the paid-signal both consume that cached status — **no per-view live Holded calls**. Humans only ever **reads** Holded balances; it never writes reassignment journal entries (spec §4).

**Tech Stack:** .NET 10, EF Core (Npgsql), NodaTime, Hangfire (`IRecurringJob`), ASP.NET MVC, xUnit + AwesomeAssertions (`[HumansFact]`), NSubstitute.

**Spec:** `docs/superpowers/specs/2026-05-25-holded-finance-integration-design.md` — §3 (Feature 2), §1 (API findings), §4 (org-accounting boundary).

**Branch / worktree:** `feat/holded-finance-creditor` at `.worktrees/holded-finance-f2`. Feature 1 (PR #783) is now **merged to `main`**, its migration regenerated as `20260525215323_HoldedActuals`; this branch is rebased onto `origin/main`, so Feature 2 is a **normal PR to `main`** (no longer stacked). All work happens in this worktree.

---

## Load-bearing design decisions (confirmed with Peter before execution)

1. **New cross-section surface (needs approval):** `IHoldedFinanceService.GetCreditorStatusAsync(int? supplierAccountNum, string holdedContactId, CancellationToken)` is consumed by `ExpenseReportService` (Expenses → Finance). It uses the **full** `IHoldedFinanceService` (no `IHoldedFinanceServiceRead` yet — read-split is deferred tech debt, exactly like `HoldedFinanceService`'s use of the full `IBudgetService`). One new interface method.
2. **Extra `ExpenseReport` field (beyond the spec-named `HoldedContactId`):** `HoldedSupplierAccountNum` (`int?`) caches the `contactId → 400000xx` link resolved at push time. This keeps the read path pure-cache (no per-view `GetContact`) and lets Finance cache balances keyed by account number from one `chartofaccounts` pull — instead of enumerating every creditor contact nightly.
3. **Paid trigger:** a member's **creditor balance ≥ 0** (account settled) marks **all** their `SepaSent` reports `Paid`, with `PaidOn` = the latest cached payment date. This replaces the per-doc `PaymentsPending == 0` poll, which misses aggregate account-level payments.

## Build-time probes (verify against the live account; key in `HOLDED_API_KEY`)

- **Contact create/update payload field names:** `name`, `tradeName`, `customId`, `type` (value `creditor` vs `supplier`), `iban`. Endpoints: `POST /api/invoicing/v1/contacts`, `PUT /api/invoicing/v1/contacts/{id}`. Mark with `// TODO(probe)` like Feature 1's `CreateExpenseAccountAsync`.
- **`supplierRecord.num`** path on `GET /api/invoicing/v1/contacts/{id}` (the `400000xx`).
- **Linking a purchase doc to a contact:** the field name on `POST documents/purchase` (`contactId` vs `contact`).
- **`chartofaccounts` shape** (`{id, num, name, balance}`) and the creditor number range (`40000000..40000099`).
- **`payments` shape** (`{id, contactId, amount, date, documentType}`) and whether one call returns all rows.

---

## Database changes vs. in-memory-only (read this first)

This feature touches the **Postgres schema in exactly one migration** (Task 5). Everything else is in-memory C# types (API DTOs, result records, view models) that never hit the DB.

**Schema changes (persisted — `HoldedCreditor` migration, Task 5):**
| Change | Table | Source |
|---|---|---|
| **New column** `holded_contact_id` (varchar 64, null) | `expense_reports` | `ExpenseReport.HoldedContactId` (Task 3) |
| **New column** `holded_supplier_account_num` (int, null) | `expense_reports` | `ExpenseReport.HoldedSupplierAccountNum` (Task 3) |
| **New index** on `holded_contact_id` | `expense_reports` | Task 3 config |
| **New table** `holded_creditor_balances` (+ unique idx on `supplier_account_num`) | — | `HoldedCreditorBalance` entity (Task 4) |
| **New table** `holded_payments` (+ unique idx `holded_payment_id`, idx `holded_contact_id`) | — | `HoldedPayment` entity (Task 4) |

No other tables/columns change. No seed data (the `holded_sync_states` singleton was seeded in Feature 1; nothing new to seed). The two new tables are **caches** — populated nightly from Holded, never user-edited.

**In-memory-only types (NO migration, NO DB):**
- API transfer DTOs (Task 1): `HoldedContactInput`, `HoldedContactDto`, `HoldedChartAccountDto`, `HoldedPaymentDto`, and the new `ContactId` field on `HoldedPurchaseDocumentInput`.
- Result record (Task 8): `HoldedCreditorStatus`.
- DTO projection (Task 7): `ExpenseReportDto.HoldedContactId` / `.HoldedSupplierAccountNum` — these just **read** the Task 3 columns through the mapper; they add no schema.
- View types (Tasks 12–13): `ExpenseHoldedTimeline`, the extended `ExpenseDetailViewData`, `ExpenseDetailViewModel.HoldedTimeline`.

**Rule for the executor:** only Tasks 3, 4, and 5 alter EF entities/configs/`DbContext`/migrations. If any other task tempts you to add a `DbSet`, an `IEntityTypeConfiguration`, or an entity property, **stop** — it belongs in the in-memory layer.

---

## Conventions for this plan

- **"Mirror `<file>`"** = copy that file's structure/style and adapt the named bits; read the named file first. Used for mechanical boilerplate.
- **Build:** `dotnet build Humans.slnx -v quiet` · **Test one class:** `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~<ClassName>"` (from the worktree root; `-v quiet` is required per `memory/process/dotnet-verbosity-quiet.md`).
- **Commit** after each task. **Do not push** until the review checkpoint; when pushing: `git push origin feat/holded-finance-creditor`.
- **EF migrations:** never hand-edit. Generate via the CLI (Task 5). The `holded_sync_states` singleton already seeds via `HasData` (Feature 1) — the new tables need no seed.
- **Pre-existing failures:** the 2 Stripe `Pay_*` integration tests fail on the base too — **not yours, do not chase them.**

---

## File Structure

**Create:**
- `src/Humans.Application/Interfaces/Holded/HoldedContactDtos.cs` — contact + chart + payment read/write DTOs.
- `src/Humans.Domain/Entities/HoldedCreditorBalance.cs`, `HoldedPayment.cs`
- `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedCreditorBalanceConfiguration.cs`, `HoldedPaymentConfiguration.cs`
- `src/Humans.Application/Services/Finance/Dtos/HoldedCreditorStatus.cs`
- `tests/Humans.Application.Tests/Services/Holded/HoldedClientContactTests.cs`
- (tests added inline to existing `HoldedFinanceServiceTests.cs` and `ExpenseReportServiceTests.cs`)

**Modify:**
- `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs` + `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs` — contact/chart/payment methods; send `contactId` on purchase-doc create.
- `src/Humans.Application/Interfaces/Holded/HoldedPurchaseDocumentDto.cs` — add `ContactId` to `HoldedPurchaseDocumentInput`.
- `src/Humans.Domain/Entities/ExpenseReport.cs` — add `HoldedContactId`, `HoldedSupplierAccountNum`.
- `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseReportConfiguration.cs` — configure the two new columns.
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add two Finance `DbSet`s.
- `src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs` + `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs` — creditor-balance + payment upsert/read.
- `src/Humans.Application/Interfaces/Repositories/IExpenseRepository.cs` + `src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs` — `SetHoldedContactLinkAsync`.
- `src/Humans.Infrastructure/Repositories/Expenses/ExpenseReportMapper.cs` + `src/Humans.Application/Services/Expenses/Dtos/ExpenseReportDto.cs` — surface the two new fields.
- `src/Humans.Application/Interfaces/Finance/IHoldedFinanceService.cs` + `src/Humans.Application/Services/Finance/HoldedFinanceService.cs` — `SyncCreditorDataAsync`, `GetCreditorStatusAsync`.
- `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs` — also call `SyncCreditorDataAsync`.
- `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` — inject `IHoldedFinanceService`; contact enrichment; paid-fix; timeline.
- `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs` — extend `ExpenseDetailViewData` with the timeline.
- `src/Humans.Web/Models/ExpenseDetailViewModel.cs` — carry the timeline.
- `src/Humans.Web/Views/Expenses/Detail.cshtml` — render the timeline.
- `docs/sections/Finance.md`, `docs/sections/Expenses.md` (if present) — document Feature 2.

---

## Task 1: Holded contact / chart / payment DTOs — **[in-memory only, NO DB]**

**Files:**
- Create: `src/Humans.Application/Interfaces/Holded/HoldedContactDtos.cs`
- Modify: `src/Humans.Application/Interfaces/Holded/HoldedPurchaseDocumentDto.cs`

- [ ] **Step 1: Create `HoldedContactDtos.cs`**

```csharp
using NodaTime;

namespace Humans.Application.Interfaces.Holded;

/// <summary>Create/update payload for a Holded contact (creditor/supplier).</summary>
public sealed record HoldedContactInput
{
    /// <summary>Legal name — the official identity (accountant / SEPA / tax). Never the burner.</summary>
    public required string Name { get; init; }
    /// <summary>Burner/display name. Only ever set alongside a legal <see cref="Name"/>.</summary>
    public string? TradeName { get; init; }
    /// <summary>Our stable handle — the Humans UserId.</summary>
    public string? CustomId { get; init; }
    /// <summary>Holded contact type. Creditors/suppliers get a 400000xx account.</summary>
    public string Type { get; init; } = "creditor";
    public string? Iban { get; init; }
    /// <summary>When set, update this existing contact rather than create a new one.</summary>
    public string? ExistingContactId { get; init; }
}

/// <summary>A Holded contact as returned by GET contacts/{id}.</summary>
public sealed record HoldedContactDto
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    /// <summary>supplierRecord.num — the 400000xx supplier account number, or null if not yet assigned.</summary>
    public int? SupplierAccountNum { get; init; }
}

/// <summary>One row from GET accounting/v1/chartofaccounts.</summary>
public sealed record HoldedChartAccountDto
{
    public required int Num { get; init; }
    public required string Name { get; init; }
    /// <summary>Account balance. Negative on a 400000xx creditor account = money owed.</summary>
    public required decimal Balance { get; init; }
}

/// <summary>One row from GET invoicing/v1/payments.</summary>
public sealed record HoldedPaymentDto
{
    public required string Id { get; init; }
    public required string ContactId { get; init; }
    public required decimal Amount { get; init; }
    public required Instant Date { get; init; }
    public string? DocumentType { get; init; }
}
```

- [ ] **Step 2: Add `ContactId` to `HoldedPurchaseDocumentInput`** in `HoldedPurchaseDocumentDto.cs` (so a created purchase doc links to the enriched contact):

```csharp
public sealed record HoldedPurchaseDocumentInput
{
    public required string ContactName { get; init; }
    /// <summary>When set, link the purchase doc to this Holded contact id.</summary>
    public string? ContactId { get; init; }
    public required Instant Date { get; init; }
    public required IReadOnlyList<HoldedPurchaseDocumentLineInput> Lines { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Description { get; init; }
}
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Holded/HoldedContactDtos.cs src/Humans.Application/Interfaces/Holded/HoldedPurchaseDocumentDto.cs
git commit -m "feat(holded): contact/chart/payment DTOs + ContactId on purchase-doc input"
```

---

## Task 2: Extend IHoldedClient with contact / chart / payment methods — **[in-memory only, NO DB]**

**Files:**
- Modify: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`, `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
- Test: `tests/Humans.Application.Tests/Services/Holded/HoldedClientContactTests.cs`

> All v1, `key` header. Endpoints (confirmed live 2026-05-25): `POST/PUT /api/invoicing/v1/contacts`, `GET /api/invoicing/v1/contacts/{id}`, `GET /api/accounting/v1/chartofaccounts`, `GET /api/invoicing/v1/payments`.

- [ ] **Step 1: Add to `IHoldedClient`**

```csharp
    /// <summary>Creates or updates a contact; returns the contact id.</summary>
    Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default);

    /// <summary>Reads one contact; exposes supplierRecord.num (the 400000xx account).</summary>
    Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default);

    /// <summary>Reads the full chart of accounts (trial balance) in one call.</summary>
    Task<IReadOnlyList<HoldedChartAccountDto>> ListChartOfAccountsAsync(CancellationToken ct = default);

    /// <summary>Reads all payment rows (contactId, amount, date) in one call.</summary>
    Task<IReadOnlyList<HoldedPaymentDto>> ListPaymentsAsync(CancellationToken ct = default);
```

- [ ] **Step 2: Write failing tests** (mirror `HoldedClientReadTests` — same `StubHandler`/`Make`/`Respond` helpers; copy them into the new class)

```csharp
using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Services.Holded;
using Humans.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Tests.Services.Holded;

public class HoldedClientContactTests
{
    private static HoldedClient Make(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.holded.com") },
            Options.Create(new HoldedClientOptions { ApiKey = "test-key" }),
            NullLogger<HoldedClient>.Instance);

    [HumansFact]
    public async Task GetContact_parses_supplierRecord_num()
    {
        var json = """{"id":"c1","name":"Daniela Real","supplierRecord":{"num":40000001}}""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var contact = await client.GetContactAsync("c1");

        contact.Id.Should().Be("c1");
        contact.SupplierAccountNum.Should().Be(40000001);
    }

    [HumansFact]
    public async Task GetContact_supplierAccountNum_null_when_absent()
    {
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"id":"c1","name":"X"}""")));
        var contact = await client.GetContactAsync("c1");
        contact.SupplierAccountNum.Should().BeNull();
    }

    [HumansFact]
    public async Task UpsertContact_posts_when_no_existing_id_and_returns_id()
    {
        string? method = null;
        var client = Make(new StubHandler(req =>
        {
            method = req.Method.Method;
            return Respond(HttpStatusCode.OK, """{"id":"new-c"}""");
        }));

        var id = await client.UpsertContactAsync(new HoldedContactInput { Name = "Legal", CustomId = "u1" });

        method.Should().Be("POST");
        id.Should().Be("new-c");
    }

    [HumansFact]
    public async Task UpsertContact_puts_when_existing_id_present()
    {
        string? method = null;
        string? path = null;
        var client = Make(new StubHandler(req =>
        {
            method = req.Method.Method;
            path = req.RequestUri!.AbsolutePath;
            return Respond(HttpStatusCode.OK, """{"id":"c-exist"}""");
        }));

        var id = await client.UpsertContactAsync(new HoldedContactInput
        {
            Name = "Legal", TradeName = "Burner", ExistingContactId = "c-exist",
        });

        method.Should().Be("PUT");
        path.Should().EndWith("/contacts/c-exist");
        id.Should().Be("c-exist");
    }

    [HumansFact]
    public async Task ListChartOfAccounts_parses_num_name_balance()
    {
        var json = """[{"id":"a","num":40000001,"name":"Daniela","balance":-3180.0},
                       {"id":"b","num":62900000,"name":"Otros","balance":12.0}]""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var rows = await client.ListChartOfAccountsAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.Num == 40000001).Balance.Should().Be(-3180.0m);
    }

    [HumansFact]
    public async Task ListPayments_parses_contact_amount_date()
    {
        var json = """[{"id":"p1","contactId":"c1","amount":50.0,"date":1779141600,"documentType":"purchase"}]""";
        var client = Make(new StubHandler(_ => Respond(HttpStatusCode.OK, json)));

        var rows = await client.ListPaymentsAsync();

        rows.Should().ContainSingle();
        rows[0].ContactId.Should().Be("c1");
        rows[0].Amount.Should().Be(50.0m);
        rows[0].Date.Should().Be(Instant.FromUnixTimeSeconds(1779141600));
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

- [ ] **Step 3: Run — expect FAIL** (methods not implemented)

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedClientContactTests"`
Expected: FAIL (compile error / NotImplemented).

- [ ] **Step 4: Implement in `HoldedClient`** (reuse `AttachAuth`, `SendAsync`, `ReadDecimal`, `ReadInstant`; add a small `ReadInt` helper)

```csharp
public async Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default)
{
    // TODO(probe): confirm contact payload field names (name/tradeName/customId/type/iban) against live API.
    var payload = new
    {
        name = input.Name,
        tradeName = input.TradeName,
        customId = input.CustomId,
        type = input.Type,
        iban = input.Iban,
    };

    var isUpdate = !string.IsNullOrEmpty(input.ExistingContactId);
    using var req = new HttpRequestMessage(
        isUpdate ? HttpMethod.Put : HttpMethod.Post,
        isUpdate
            ? $"/api/invoicing/v1/contacts/{input.ExistingContactId}"
            : "/api/invoicing/v1/contacts")
    { Content = JsonContent.Create(payload) };
    AttachAuth(req);

    using var resp = await SendAsync(req, ct);
    var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
        ?? throw new HoldedTransientException("Holded returned empty body");
    // On update Holded may echo the id, or just a status — fall back to the known id.
    return node["id"]?.GetValue<string>()
        ?? input.ExistingContactId
        ?? throw new HoldedTransientException("Holded contact upsert response missing id");
}

public async Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/invoicing/v1/contacts/{contactId}");
    AttachAuth(req);
    using var resp = await SendAsync(req, ct);
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
        ?? throw new HoldedTransientException("Holded returned empty body");

    return new HoldedContactDto
    {
        Id = node["id"]?.GetValue<string>() ?? contactId,
        Name = node["name"]?.GetValue<string>(),
        SupplierAccountNum = ReadInt(node["supplierRecord"]?["num"]),
    };
}

public async Task<IReadOnlyList<HoldedChartAccountDto>> ListChartOfAccountsAsync(CancellationToken ct = default)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, "/api/accounting/v1/chartofaccounts");
    AttachAuth(req);
    using var resp = await SendAsync(req, ct);
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
    return arr.Where(n => n is not null).Select(n => new HoldedChartAccountDto
    {
        Num = ReadInt(n!["num"]) ?? 0,
        Name = n["name"]?.GetValue<string>() ?? "",
        Balance = ReadDecimal(n["balance"]),
    }).ToList();
}

public async Task<IReadOnlyList<HoldedPaymentDto>> ListPaymentsAsync(CancellationToken ct = default)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, "/api/invoicing/v1/payments");
    AttachAuth(req);
    using var resp = await SendAsync(req, ct);
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
    return arr.Where(n => n is not null).Select(n => new HoldedPaymentDto
    {
        Id = n!["id"]?.GetValue<string>() ?? "",
        ContactId = n["contactId"]?.GetValue<string>() ?? "",
        Amount = ReadDecimal(n["amount"]),
        Date = ReadInstant(n["date"]) ?? Instant.FromUnixTimeSeconds(0),
        DocumentType = n["documentType"]?.GetValue<string>(),
    }).ToList();
}
```

Add the int helper near `ReadDecimal`:

```csharp
private static int? ReadInt(JsonNode? node) =>
    node is null ? null : (int?)node.GetValue<long>();
```

- [ ] **Step 5: Send `contactId` on purchase-doc create.** In `CreatePurchaseDocumentAsync`, add to the anonymous payload (only when present):

```csharp
        var payload = new
        {
            contactId = input.ContactId,   // TODO(probe): confirm field name (contactId vs contact)
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
```

- [ ] **Step 6: Run — expect PASS.** Build. Commit.

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedClientContactTests"` → PASS.

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Holded/IHoldedClient.cs src/Humans.Infrastructure/Services/Holded/HoldedClient.cs tests/Humans.Application.Tests/Services/Holded/HoldedClientContactTests.cs
git commit -m "feat(holded): contact upsert/get + chartofaccounts + payments client reads"
```

---

## Task 3: ExpenseReport entity fields — **[DB SCHEMA: 2 new columns + index on `expense_reports`]** (migration generated in Task 5)

**Files:**
- Modify: `src/Humans.Domain/Entities/ExpenseReport.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseReportConfiguration.cs`

- [ ] **Step 1: Add fields** to `ExpenseReport` (next to `HoldedDocId`):

```csharp
    public string? HoldedDocId { get; set; }
    /// <summary>Holded contact id for this submitter (set on first push). Links to creditor balance + payments.</summary>
    public string? HoldedContactId { get; set; }
    /// <summary>Resolved 400000xx supplier-account number (supplierRecord.num), cached at push time.</summary>
    public int? HoldedSupplierAccountNum { get; set; }
```

- [ ] **Step 2: Configure columns** in `ExpenseReportConfiguration` (after the `HoldedDocId` lines):

```csharp
        b.Property(x => x.HoldedDocId).HasMaxLength(64);
        b.Property(x => x.HoldedContactId).HasMaxLength(64);
        b.HasIndex(x => x.HoldedContactId);
```

(`HoldedSupplierAccountNum` is a plain nullable int — no extra config needed.)

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Domain/Entities/ExpenseReport.cs src/Humans.Infrastructure/Data/Configurations/Expenses/ExpenseReportConfiguration.cs
git commit -m "feat(expenses): HoldedContactId + HoldedSupplierAccountNum on ExpenseReport"
```

---

## Task 4: Finance creditor-balance + payment entities, EF configs, DbSets — **[DB SCHEMA: 2 new tables]** (migration generated in Task 5)

**Files:**
- Create: `src/Humans.Domain/Entities/HoldedCreditorBalance.cs`, `HoldedPayment.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Finance/HoldedCreditorBalanceConfiguration.cs`, `HoldedPaymentConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Entities** (no cross-domain navs — FK/handle only):

```csharp
// HoldedCreditorBalance.cs
using NodaTime;
namespace Humans.Domain.Entities;

/// <summary>Cached 400000xx creditor-account balance from Holded chartofaccounts. Negative balance = owed.</summary>
public class HoldedCreditorBalance
{
    public Guid Id { get; init; }
    public int SupplierAccountNum { get; set; }   // 400000xx, unique
    public string Name { get; set; } = "";
    public decimal Balance { get; set; }          // signed; negative = org owes
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
```

```csharp
// HoldedPayment.cs
using NodaTime;
namespace Humans.Domain.Entities;

/// <summary>Cached Holded payment row, keyed by contact for creditor settle detection.</summary>
public class HoldedPayment
{
    public Guid Id { get; init; }
    public string HoldedPaymentId { get; set; } = "";  // unique upsert key
    public string HoldedContactId { get; set; } = "";
    public decimal Amount { get; set; }
    public LocalDate Date { get; set; }
    public string? DocumentType { get; set; }
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
}
```

- [ ] **Step 2: EF configs** (mirror `HoldedExpenseDocConfiguration` — `LocalDate` already has a project-wide converter):

```csharp
// HoldedCreditorBalanceConfiguration.cs
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedCreditorBalanceConfiguration : IEntityTypeConfiguration<HoldedCreditorBalance>
{
    public void Configure(EntityTypeBuilder<HoldedCreditorBalance> b)
    {
        b.ToTable("holded_creditor_balances");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.SupplierAccountNum).IsUnique();
        b.Property(x => x.Name).HasMaxLength(256);
        b.Property(x => x.Balance).HasColumnType("decimal(12,2)");
    }
}
```

```csharp
// HoldedPaymentConfiguration.cs
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Finance;

public class HoldedPaymentConfiguration : IEntityTypeConfiguration<HoldedPayment>
{
    public void Configure(EntityTypeBuilder<HoldedPayment> b)
    {
        b.ToTable("holded_payments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.HoldedPaymentId).IsUnique();
        b.HasIndex(x => x.HoldedContactId);
        b.Property(x => x.HoldedPaymentId).HasMaxLength(64);
        b.Property(x => x.HoldedContactId).HasMaxLength(64);
        b.Property(x => x.DocumentType).HasMaxLength(32);
        b.Property(x => x.Amount).HasColumnType("decimal(12,2)");
    }
}
```

- [ ] **Step 3: Add DbSets** to `HumansDbContext` (next to the Feature-1 Holded sets at lines ~119-121):

```csharp
    public DbSet<HoldedCreditorBalance> HoldedCreditorBalances => Set<HoldedCreditorBalance>();
    public DbSet<HoldedPayment> HoldedPayments => Set<HoldedPayment>();
```

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Domain/Entities/HoldedCreditorBalance.cs src/Humans.Domain/Entities/HoldedPayment.cs src/Humans.Infrastructure/Data/Configurations/Finance/HoldedCreditorBalanceConfiguration.cs src/Humans.Infrastructure/Data/Configurations/Finance/HoldedPaymentConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(finance): HoldedCreditorBalance + HoldedPayment cache tables"
```

---

## Task 5: EF migration — **[DB SCHEMA: the single migration for Tasks 3 + 4]**

**Files:**
- Create (generated): `src/Humans.Infrastructure/Migrations/*_HoldedCreditor.cs` (+ Designer + snapshot update)

> **Generate only after the branch is rebased on the current `origin/main`** (chain tip `20260525215323_HoldedActuals`). This migration must land **end-of-chain** after it. Never hand-edit the migration or the snapshot (`memory/architecture/no-hand-edited-migrations.md`). If the tools won't produce a clean migration, **stop and ask** — do not improvise (`memory/architecture/migration-regen-after-rebase.md`).

- [ ] **Step 1: Generate** (CLI only):

```bash
dotnet ef migrations add HoldedCreditor --project src/Humans.Infrastructure --startup-project src/Humans.Web -- -v quiet
```

- [ ] **Step 2: Build.** `dotnet build Humans.slnx -v quiet` → success.

- [ ] **Step 3: Verify scope — body AND snapshot.** Confirm the `Up`/`Down` body creates `holded_creditor_balances` + `holded_payments` (with the unique indexes) and adds `holded_contact_id` + `holded_supplier_account_num` (+ index) to `expense_reports` — and **nothing else**. Then per `memory/process/diff-snapshot-after-ef-tool.md`:

```bash
git diff src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs
```

The snapshot diff must touch only the two new entities + the two new `ExpenseReport` properties. If it touches anything else, **STOP** — snapshot drift; do not commit, do not hand-fix; reconcile the model or stop and ask.

- [ ] **Step 4: MANDATORY migration-review gate** (`memory/process/ef-migration-review-gate.md`). Dispatch the EF migration reviewer agent at `.claude/agents/ef-migration-reviewer.md` against the generated migration. Do **not** commit until it passes with no CRITICAL issues. If CRITICAL, fix by **regenerating** (`migrations remove` → adjust model/config → `migrations add`), never by patching the file.

- [ ] **Step 5: Commit** (only after the reviewer is clean)

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(finance): migration for creditor/payment cache + ER contact link columns"
```

---

## Task 6: IHoldedRepository creditor-balance + payment methods — **[reads/writes existing tables, NO schema change]**

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs`, `src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs`

- [ ] **Step 1: Add to `IHoldedRepository`** (the `[Section("Finance")]` interface):

```csharp
    // Creditor balances (chartofaccounts cache)
    Task UpsertCreditorBalancesAsync(IReadOnlyList<HoldedCreditorBalance> rows, Instant now, CancellationToken ct = default);
    Task<HoldedCreditorBalance?> GetCreditorBalanceByAccountNumAsync(int accountNum, CancellationToken ct = default);

    // Payments cache
    Task UpsertPaymentsAsync(IReadOnlyList<HoldedPayment> rows, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedPayment>> GetPaymentsByContactAsync(string holdedContactId, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `HoldedRepository`** (mirror the existing `UpsertDocsAsync` upsert shape):

```csharp
    // ── Creditor balances ────────────────────────────────────────────────────

    public async Task UpsertCreditorBalancesAsync(
        IReadOnlyList<HoldedCreditorBalance> rows, Instant now, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var nums = rows.Select(r => r.SupplierAccountNum).ToList();
        var existing = await ctx.HoldedCreditorBalances
            .Where(b => nums.Contains(b.SupplierAccountNum))
            .ToDictionaryAsync(b => b.SupplierAccountNum, ct);
        foreach (var r in rows)
        {
            if (existing.TryGetValue(r.SupplierAccountNum, out var cur))
            {
                cur.Name = r.Name;
                cur.Balance = r.Balance;
                cur.LastSyncedAt = now;
                cur.UpdatedAt = now;
            }
            else
            {
                r.LastSyncedAt = now;
                ctx.HoldedCreditorBalances.Add(r);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<HoldedCreditorBalance?> GetCreditorBalanceByAccountNumAsync(
        int accountNum, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedCreditorBalances.AsNoTracking()
            .FirstOrDefaultAsync(b => b.SupplierAccountNum == accountNum, ct);
    }

    // ── Payments ──────────────────────────────────────────────────────────────

    public async Task UpsertPaymentsAsync(
        IReadOnlyList<HoldedPayment> rows, Instant now, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = rows.Select(r => r.HoldedPaymentId).ToList();
        var existing = await ctx.HoldedPayments
            .Where(p => ids.Contains(p.HoldedPaymentId))
            .ToDictionaryAsync(p => p.HoldedPaymentId, ct);
        foreach (var r in rows)
        {
            if (existing.TryGetValue(r.HoldedPaymentId, out var cur))
            {
                cur.HoldedContactId = r.HoldedContactId;
                cur.Amount = r.Amount;
                cur.Date = r.Date;
                cur.DocumentType = r.DocumentType;
                cur.LastSyncedAt = now;
            }
            else
            {
                r.LastSyncedAt = now;
                ctx.HoldedPayments.Add(r);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedPayment>> GetPaymentsByContactAsync(
        string holdedContactId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedPayments.AsNoTracking()
            .Where(p => p.HoldedContactId == holdedContactId)
            .ToListAsync(ct);
    }
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Repositories/IHoldedRepository.cs src/Humans.Infrastructure/Repositories/Finance/HoldedRepository.cs
git commit -m "feat(finance): repository upsert/read for creditor balances + payments"
```

---

## Task 7: ExpenseReport contact-link persistence + DTO surfacing — **[writes existing columns + in-memory DTO, NO schema change]**

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/IExpenseRepository.cs`, `src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs`
- Modify: `src/Humans.Application/Services/Expenses/Dtos/ExpenseReportDto.cs`, `src/Humans.Infrastructure/Repositories/Expenses/ExpenseReportMapper.cs`

- [ ] **Step 1: Add to `IExpenseRepository`** (near `SetHoldedDocIdAsync`):

```csharp
    /// <summary>
    /// Persists the Holded contact id and (optionally) the resolved 400000xx supplier-account
    /// number on the report. A null <paramref name="supplierAccountNum"/> leaves any existing
    /// number untouched (it is resolved post-doc-creation and may not exist on the first call).
    /// </summary>
    Task SetHoldedContactLinkAsync(
        Guid reportId, string holdedContactId, int? supplierAccountNum,
        NodaTime.Instant updatedAt, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `ExpenseRepository`** (mirror `SetHoldedDocIdAsync`):

```csharp
    public async Task SetHoldedContactLinkAsync(
        Guid reportId, string holdedContactId, int? supplierAccountNum,
        Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var r = await ctx.ExpenseReports.FirstOrDefaultAsync(x => x.Id == reportId, ct);
        if (r is null) return;
        r.HoldedContactId = holdedContactId;
        if (supplierAccountNum is not null) r.HoldedSupplierAccountNum = supplierAccountNum;
        r.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
    }
```

- [ ] **Step 3: Surface on the DTO.** Add to `ExpenseReportDto` (after `HoldedDocId`):

```csharp
    public string? HoldedDocId { get; init; }
    public string? HoldedContactId { get; init; }
    public int? HoldedSupplierAccountNum { get; init; }
```

And map them in `ExpenseReportMapper.ToDto` (after `HoldedDocId = r.HoldedDocId,`):

```csharp
        HoldedDocId = r.HoldedDocId,
        HoldedContactId = r.HoldedContactId,
        HoldedSupplierAccountNum = r.HoldedSupplierAccountNum,
```

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Repositories/IExpenseRepository.cs src/Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs src/Humans.Application/Services/Expenses/Dtos/ExpenseReportDto.cs src/Humans.Infrastructure/Repositories/Expenses/ExpenseReportMapper.cs
git commit -m "feat(expenses): persist + expose Holded contact link on ExpenseReport"
```

---

## Task 8: HoldedFinanceService — creditor sync + status read — **[in-memory logic + existing tables, NO schema change]**

**Files:**
- Create: `src/Humans.Application/Services/Finance/Dtos/HoldedCreditorStatus.cs`
- Modify: `src/Humans.Application/Interfaces/Finance/IHoldedFinanceService.cs`, `src/Humans.Application/Services/Finance/HoldedFinanceService.cs`
- Test: add to `tests/Humans.Application.Tests/Finance/HoldedFinanceServiceTests.cs`

- [ ] **Step 1: Result DTO** (`HoldedCreditorStatus.cs`):

```csharp
using NodaTime;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>Cached creditor status for one member, sourced from Holded.</summary>
public sealed record HoldedCreditorStatus(
    int? SupplierAccountNum,
    decimal Balance,            // signed; negative = org owes the member
    decimal OwedToMember,       // = max(0, -Balance)
    LocalDate? LastPaymentDate,
    decimal TotalPaid);
```

- [ ] **Step 2: Interface methods** on `IHoldedFinanceService`:

```csharp
    /// <summary>Nightly cache refresh: creditor balances (chartofaccounts) + payments rows.</summary>
    Task SyncCreditorDataAsync(CancellationToken ct = default);

    /// <summary>Reads cached creditor status for a member by their supplier-account number + contact id.
    /// Returns null when nothing is cached yet (not registered in Holded).</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, string holdedContactId, CancellationToken ct = default);
```

(Add `using Humans.Application.Services.Finance.Dtos;` — already present in the interface file from Feature 1.)

- [ ] **Step 3: Write failing tests** (append to `HoldedFinanceServiceTests`):

```csharp
    // ─── Creditor data ──────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task SyncCreditorData_caches_only_400000xx_balances_and_all_payments()
    {
        _client.ListChartOfAccountsAsync(default).ReturnsForAnyArgs(new List<HoldedChartAccountDto>
        {
            new() { Num = 40000001, Name = "Daniela", Balance = -3180m },
            new() { Num = 40000004, Name = "Peter",   Balance = -23m },
            new() { Num = 62900000, Name = "Otros",   Balance = 12m },  // not a creditor acct
        });
        _client.ListPaymentsAsync(default).ReturnsForAnyArgs(new List<HoldedPaymentDto>
        {
            new() { Id = "p1", ContactId = "c1", Amount = 50m, Date = FixedNow, DocumentType = "purchase" },
        });

        IReadOnlyList<HoldedCreditorBalance>? balances = null;
        await _repo.UpsertCreditorBalancesAsync(
            Arg.Do<IReadOnlyList<HoldedCreditorBalance>>(b => balances = b), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        IReadOnlyList<HoldedPayment>? payments = null;
        await _repo.UpsertPaymentsAsync(
            Arg.Do<IReadOnlyList<HoldedPayment>>(p => payments = p), Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        await MakeService().SyncCreditorDataAsync();

        balances.Should().NotBeNull();
        balances!.Select(b => b.SupplierAccountNum).Should().BeEquivalentTo(new[] { 40000001, 40000004 });
        payments.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetCreditorStatus_computes_owed_and_paid()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(40000001, default).ReturnsForAnyArgs(
            new HoldedCreditorBalance { SupplierAccountNum = 40000001, Balance = -3180m });
        _repo.GetPaymentsByContactAsync("c1", default).ReturnsForAnyArgs(new List<HoldedPayment>
        {
            new() { HoldedPaymentId = "p1", HoldedContactId = "c1", Amount = 100m, Date = new LocalDate(2026, 4, 1) },
            new() { HoldedPaymentId = "p2", HoldedContactId = "c1", Amount = 50m,  Date = new LocalDate(2026, 4, 20) },
        });

        var status = await MakeService().GetCreditorStatusAsync(40000001, "c1");

        status.Should().NotBeNull();
        status!.OwedToMember.Should().Be(3180m);
        status.TotalPaid.Should().Be(150m);
        status.LastPaymentDate.Should().Be(new LocalDate(2026, 4, 20));
    }

    [HumansFact]
    public async Task GetCreditorStatus_returns_null_when_nothing_cached()
    {
        _repo.GetCreditorBalanceByAccountNumAsync(default, default).ReturnsForAnyArgs((HoldedCreditorBalance?)null);
        _repo.GetPaymentsByContactAsync(default!, default).ReturnsForAnyArgs(new List<HoldedPayment>());

        var status = await MakeService().GetCreditorStatusAsync(40000099, "c-unknown");

        status.Should().BeNull();
    }
```

- [ ] **Step 4: Run — expect FAIL.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedFinanceServiceTests"` → FAIL.

- [ ] **Step 5: Implement** in `HoldedFinanceService` (add the creditor block; reuse `Europe/Madrid` zone as in `MapDoc`):

```csharp
    // ─── Creditor data (Feature 2) ──────────────────────────────────────────────

    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 40000099;

    public async Task SyncCreditorDataAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

        var chart = await client.ListChartOfAccountsAsync(ct);
        var balances = chart
            .Where(a => a.Num >= CreditorAccountMin && a.Num <= CreditorAccountMax)
            .Select(a => new HoldedCreditorBalance
            {
                Id = Guid.NewGuid(),
                SupplierAccountNum = a.Num,
                Name = a.Name,
                Balance = a.Balance,
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();
        await repo.UpsertCreditorBalancesAsync(balances, now, ct);

        var payments = (await client.ListPaymentsAsync(ct))
            .Where(p => !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.ContactId))
            .Select(p => new HoldedPayment
            {
                Id = Guid.NewGuid(),
                HoldedPaymentId = p.Id,
                HoldedContactId = p.ContactId,
                Amount = p.Amount,
                Date = p.Date.InZone(zone).Date,
                DocumentType = p.DocumentType,
                CreatedAt = now,
            })
            .ToList();
        await repo.UpsertPaymentsAsync(payments, now, ct);

        logger.LogInformation(
            "Holded creditor sync cached {BalanceCount} creditor balances and {PaymentCount} payments",
            balances.Count, payments.Count);
    }

    public async Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, string holdedContactId, CancellationToken ct = default)
    {
        var balanceRow = supplierAccountNum is { } num
            ? await repo.GetCreditorBalanceByAccountNumAsync(num, ct)
            : null;
        var payments = string.IsNullOrEmpty(holdedContactId)
            ? Array.Empty<HoldedPayment>()
            : (await repo.GetPaymentsByContactAsync(holdedContactId, ct)).ToArray();

        if (balanceRow is null && payments.Length == 0)
            return null;

        var balance = balanceRow?.Balance ?? 0m;
        var lastPaymentDate = payments.Length == 0
            ? (LocalDate?)null
            : payments.Max(p => p.Date);

        return new HoldedCreditorStatus(
            SupplierAccountNum: balanceRow?.SupplierAccountNum ?? supplierAccountNum,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            LastPaymentDate: lastPaymentDate,
            TotalPaid: payments.Sum(p => p.Amount));
    }
```

Add `using Humans.Application.Services.Finance.Dtos;` if not already imported (it is, from Feature 1).

- [ ] **Step 6: Run — expect PASS.** Build. Commit.

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~HoldedFinanceServiceTests"
git add src/Humans.Application/Services/Finance/Dtos/HoldedCreditorStatus.cs src/Humans.Application/Interfaces/Finance/IHoldedFinanceService.cs src/Humans.Application/Services/Finance/HoldedFinanceService.cs tests/Humans.Application.Tests/Finance/HoldedFinanceServiceTests.cs
git commit -m "feat(finance): creditor-balance + payment sync and GetCreditorStatus read"
```

---

## Task 9: Nightly job also syncs creditor data — **[no schema change]**

**Files:**
- Modify: `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`

- [ ] **Step 1: Call both** (actuals then creditor data — independent pulls, run sequentially):

```csharp
/// <summary>Nightly Holded pull: purchase docs → budget-category actuals, plus creditor balances + payments.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedFinanceService finance) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await finance.SyncAsync(cancellationToken);
        await finance.SyncCreditorDataAsync(cancellationToken);
    }
}
```

(The recurring schedule `holded-sync` already exists in `RecurringJobExtensions` — no registration change.)

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs
git commit -m "feat(finance): nightly job also caches creditor balances + payments"
```

---

## Task 10: Contact enrichment on push — **[no schema change]**

**Files:**
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs`
- Test: add to `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

> `ExpenseReportService` already injects `IUserService` (burner name) and `IHoldedClient`. This task adds the contact upsert + link persistence inside `ProcessHoldedCreateAsync`. The legal name is `report.PayeeName` (snapshot at submit); the burner is the user's `BurnerName`/`DisplayName`. **Guard: `tradeName` is only set when a legal name is present — the burner never becomes the official `name`.**

- [ ] **Step 1: Write failing tests** (the harness already builds `_sut` with a real `ExpenseRepository` + `Substitute.For<IHoldedClient>()`; capture that client via a field so the test can assert. Refactor the harness ctor to keep a `_holdedClient` field, and inject a `Substitute.For<IHoldedFinanceService>()` as `_holdedFinance` — see Task 12 Step 1 for the ctor change; do that ctor change here as part of Step 1.)

```csharp
    [HumansFact]
    public async Task ProcessHoldedCreate_upserts_contact_with_legal_name_and_burner_tradeName()
    {
        // Arrange an Approved report with one attached line, so DrainHoldedOutbox processes a Create event.
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id, payeeName: "Daniela Legal");

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(MakeProfile(userId, burner: "DaniBurner", iban: "ES760000")));
        _budgetService.GetCategoryByIdAsync(category.Id).Returns(MakeCategoryDetail(category));

        _holdedClient.UpsertContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>())
            .Returns("contact-123");
        _holdedClient.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");
        _holdedClient.GetContactAsync("contact-123", Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "contact-123", SupplierAccountNum = 40000007 });
        _fileStorage.TryReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });

        // Act
        await _sut.DrainHoldedOutboxAsync(batchSize: 10);

        // Assert: contact upserted with legal name in Name, burner in TradeName, UserId as CustomId.
        await _holdedClient.Received(1).UpsertContactAsync(
            Arg.Is<HoldedContactInput>(i =>
                i.Name == "Daniela Legal" && i.TradeName == "DaniBurner" &&
                i.CustomId == userId.ToString() && i.Type == "creditor"),
            Arg.Any<CancellationToken>());

        // Assert: contact id + resolved supplier account number persisted on the report.
        var loaded = await _sut.GetAsync(reportId);
        loaded!.HoldedContactId.Should().Be("contact-123");
        loaded.HoldedSupplierAccountNum.Should().Be(40000007);
    }
```

> Helper methods to add to the test class if not present: `SeedApprovedReportWithAttachmentAsync` (create draft → add line → attach → submit → approve via `_sut`, returns the report id), `MakeProfile(userId, burner, iban)`, `MakeCategoryDetail(category)`. Mirror existing helpers in `ExpenseReportServiceTests` for the submit/approve flow; for the attachment, `_fileStorage.SaveAsync` is a no-op substitute so attaching just needs the line + `AttachFileToLineAsync` with a small `MemoryStream`. Keep the seeding minimal — the assertion target is the contact upsert + link, not the doc/attachment mechanics.

- [ ] **Step 2: Run — expect FAIL.**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseReportServiceTests.ProcessHoldedCreate_upserts_contact"` → FAIL.

- [ ] **Step 3: Implement.** Rewrite `ProcessHoldedCreateAsync` to enrich the contact first, link the doc, then resolve + persist the supplier account number:

```csharp
    private async Task ProcessHoldedCreateAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string tag,
        string submitterName,
        Instant now,
        CancellationToken ct)
    {
        // 1. Enrich/upsert the Holded contact. Legal name -> name; burner -> tradeName (only with a legal name).
        var holdedContactId = await UpsertHoldedContactAsync(report, ct);

        var input = new HoldedPurchaseDocumentInput
        {
            ContactId = holdedContactId,
            ContactName = submitterName,
            Date = report.SubmittedAt ?? report.CreatedAt,
            Description = report.Note ?? "",
            Tags = [tag],
            Lines = report.Lines
                .OrderBy(l => l.SortOrder)
                .Select(l => new HoldedPurchaseDocumentLineInput
                {
                    Description = l.Description,
                    Amount = l.Amount,
                    Tags = [tag],
                })
                .ToList(),
        };

        // 2. Create the purchase doc (idempotent on HoldedDocId).
        string holdedDocId;
        if (string.IsNullOrEmpty(report.HoldedDocId))
        {
            holdedDocId = await holdedClient.CreatePurchaseDocumentAsync(input, ct);
            await repo.SetHoldedDocIdAsync(report.Id, holdedDocId, now, ct);
        }
        else
        {
            holdedDocId = report.HoldedDocId;
        }

        // 3. Upload attachments.
        foreach (var line in report.Lines.OrderBy(l => l.SortOrder))
        {
            if (line.AttachmentId is null || line.Attachment is null) continue;

            var bytes = await fileStorage.TryReadAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
            if (bytes is null)
                throw new InvalidOperationException(
                    $"Attachment file for {line.Attachment.Id}{line.Attachment.Extension} could not be read from storage.");
            using var stream = new MemoryStream(bytes, writable: false);
            await holdedClient.UploadAttachmentAsync(
                holdedDocId,
                new HoldedAttachmentInput
                {
                    FileName = line.Attachment.OriginalFileName,
                    ContentType = line.Attachment.ContentType,
                    Content = stream,
                },
                ct);
        }

        // 4. Resolve supplierRecord.num (now that a payable exists) and persist the contact link.
        int? supplierAccountNum = null;
        try
        {
            var contact = await holdedClient.GetContactAsync(holdedContactId, ct);
            supplierAccountNum = contact.SupplierAccountNum;
        }
        catch (HoldedTransientException ex)
        {
            logger.LogWarning(ex,
                "Could not resolve supplier account number for contact {ContactId} — will backfill on the paid poll",
                holdedContactId);
        }
        await repo.SetHoldedContactLinkAsync(report.Id, holdedContactId, supplierAccountNum, now, ct);

        await repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);
    }

    /// <summary>
    /// Upserts the submitter's Holded contact. Reuses an existing <c>HoldedContactId</c> when present
    /// (update), else creates. Legal name is the official identity; the burner is recognizability only
    /// and is never written to the official <c>name</c> slot.
    /// </summary>
    private async Task<string> UpsertHoldedContactAsync(ExpenseReportDto report, CancellationToken ct)
    {
        var legalName = report.PayeeName;
        string? burner = null;
        if (!string.IsNullOrWhiteSpace(legalName))
        {
            var info = await userService.GetUserInfoAsync(report.SubmitterUserId, ct);
            var display = info?.BurnerName;
            if (!string.IsNullOrWhiteSpace(display) &&
                !string.Equals(display, legalName, StringComparison.Ordinal))
            {
                burner = display;
            }
        }

        return await holdedClient.UpsertContactAsync(new HoldedContactInput
        {
            Name = legalName,
            TradeName = burner,                 // null when no legal name (guard) or burner == legal
            CustomId = report.SubmitterUserId.ToString(),
            Type = "creditor",
            Iban = string.IsNullOrWhiteSpace(report.PayeeIban) ? null : report.PayeeIban,
            ExistingContactId = string.IsNullOrEmpty(report.HoldedContactId) ? null : report.HoldedContactId,
        }, ct);
    }
```

> `userService` is the `IUserService` ctor parameter (already injected). Confirm `UserInfo.BurnerName` is the right display accessor (the controller uses `u.BurnerName`); adjust if the harness exposes it differently.

- [ ] **Step 4: Run — expect PASS.** Build. Commit.

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseReportServiceTests"
git add src/Humans.Application/Services/Expenses/ExpenseReportService.cs tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs
git commit -m "feat(expenses): enrich Holded contact (legal name/burner/customId/iban) on push"
```

---

## Task 11: Paid-signal fix (creditor balance, not per-doc) — **[no schema change]**

**Files:**
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs`
- Test: add to `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

> Replaces the `GetPurchaseDocumentAsync(...).PaymentsPending == 0` poll. New rule: for each `SepaSent` report, ask Finance for the member's creditor status; **balance ≥ 0 → account settled → mark Paid**, `PaidOn` = the latest payment date (or now). Backfill a missing supplier-account number via `GetContactAsync` so the balance becomes readable.

- [ ] **Step 1: Write failing tests** (uses `_holdedFinance` substitute from the Task 10/12 ctor change):

```csharp
    [HumansFact]
    public async Task PollHoldedPaidStatus_marks_paid_when_creditor_balance_settled()
    {
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        var reportId = await SeedSepaSentReportAsync(userId, category.Id, contactId: "c1", accountNum: 40000007);

        _holdedFinance.GetCreditorStatusAsync(40000007, "c1", Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: 0m, OwedToMember: 0m,
                LastPaymentDate: new LocalDate(2026, 5, 20), TotalPaid: 121m));

        await _sut.PollHoldedPaidStatusAsync(batchSize: 50);

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.Paid);
        loaded.PaidAt.Should().Be(new LocalDate(2026, 5, 20).AtStartOfDayInZone(
            NodaTime.DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant());
    }

    [HumansFact]
    public async Task PollHoldedPaidStatus_does_not_mark_paid_when_balance_still_negative()
    {
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        var reportId = await SeedSepaSentReportAsync(userId, category.Id, contactId: "c1", accountNum: 40000007);

        _holdedFinance.GetCreditorStatusAsync(40000007, "c1", Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: -50m, OwedToMember: 50m,
                LastPaymentDate: null, TotalPaid: 0m));

        await _sut.PollHoldedPaidStatusAsync(batchSize: 50);

        var loaded = await _sut.GetAsync(reportId);
        loaded!.Status.Should().Be(ExpenseReportStatus.SepaSent);
    }
```

> Helper `SeedSepaSentReportAsync(userId, categoryId, contactId, accountNum)`: drive a report to `SepaSent` via `_sut` (submit → approve → `MarkSepaSentAsync`) and set the contact link via `_expenseRepo.SetHoldedContactLinkAsync`. Reuse the real repo.

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement.** Replace the body of `PollHoldedPaidStatusAsync`:

```csharp
    public async Task PollHoldedPaidStatusAsync(int batchSize, CancellationToken ct = default)
    {
        var reports = await repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, ct);

        var batch = reports
            .OrderBy(r => r.SepaSentAt ?? r.CreatedAt)
            .Take(batchSize)
            .ToList();

        if (batch.Count == 0) return;

        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

        foreach (var report in batch)
        {
            if (string.IsNullOrEmpty(report.HoldedContactId))
            {
                logger.LogWarning(
                    "SepaSent report {ReportId} has no HoldedContactId — skipping paid poll", report.Id);
                continue;
            }

            try
            {
                // Backfill the supplier-account number if it wasn't resolved at push time.
                var accountNum = report.HoldedSupplierAccountNum;
                if (accountNum is null)
                {
                    var contact = await holdedClient.GetContactAsync(report.HoldedContactId, ct);
                    accountNum = contact.SupplierAccountNum;
                    if (accountNum is not null)
                        await repo.SetHoldedContactLinkAsync(
                            report.Id, report.HoldedContactId, accountNum, clock.GetCurrentInstant(), ct);
                }

                var status = await holdedFinance.GetCreditorStatusAsync(
                    accountNum, report.HoldedContactId, ct);
                if (status is null) continue;

                // Treasury pays the creditor account in aggregate: balance >= 0 means the member is settled.
                if (status.Balance >= 0m)
                {
                    var paidAt = status.LastPaymentDate is { } d
                        ? d.AtStartOfDayInZone(zone).ToInstant()
                        : clock.GetCurrentInstant();
                    await MarkPaidAsync(report.Id, paidAt, ct);
                    logger.LogInformation(
                        "Marked expense report {ReportId} Paid via creditor balance (contact {ContactId})",
                        report.Id, report.HoldedContactId);
                }
            }
            catch (HoldedPermanentException ex) when (ex.StatusCode == 404)
            {
                logger.LogWarning(
                    "Holded contact {ContactId} for report {ReportId} missing — skipping",
                    report.HoldedContactId, report.Id);
            }
            catch (HoldedTransientException ex)
            {
                logger.LogWarning(ex,
                    "Transient error polling Holded creditor status for report {ReportId} — retry next run",
                    report.Id);
            }
        }
    }
```

> This changes `MarkPaidAsync` to take a `paidAt` — update its signature and the service method that wraps it.

- [ ] **Step 4: Update `MarkPaidAsync` to accept the payment date.** Change the service method:

```csharp
    public async Task<bool> MarkPaidAsync(
        Guid reportId, Instant paidAt, CancellationToken ct = default)
    {
        var ok = await repo.MarkPaidAsync(reportId, paidAt, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpensePaid,
            "ExpenseReport", reportId,
            "Marked as paid.",
            "ExpensePaidJob");

        return true;
    }
```

Update the interface `IExpenseReportService.MarkPaidAsync` signature to `Task<bool> MarkPaidAsync(Guid reportId, NodaTime.Instant paidAt, CancellationToken ct = default);` and fix any other caller (search `MarkPaidAsync(`). The repo's `MarkPaidAsync(reportId, paidAt, ct)` already takes a date — no repo change.

- [ ] **Step 5: Remove the now-dead `GetPurchaseDocumentAsync` paid path.** `IHoldedClient.GetPurchaseDocumentAsync` and `HoldedPurchaseDocumentDto.PaymentsPending`/`PaymentsTotal` were used only by the old poll. Search for remaining references:

Run: `git grep -n "GetPurchaseDocumentAsync\|PaymentsPending\|PaymentsTotal"`

If the only remaining references are the interface method, the client implementation, the DTO fields, and their unit tests, **leave them in place** (they are still valid client surface) — do not remove client surface in this PR; just note in the commit that the production paid-poll no longer calls them. (Removing client surface is a separate reuse-review concern; keep this PR focused.)

- [ ] **Step 6: Run — expect PASS.** Build. Full expense test pass. Commit.

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseReportServiceTests"
git add src/Humans.Application/Services/Expenses/ExpenseReportService.cs src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs
git commit -m "fix(expenses): mark Paid from creditor balance, not per-doc PaymentsPending"
```

---

## Task 12: Submitter timeline (service + view model) — **[in-memory view types, no schema change]**

**Files:**
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` (inject `IHoldedFinanceService`; build timeline in `GetDetailViewDataAsync`)
- Modify: `src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs` (extend `ExpenseDetailViewData`)
- Modify: `src/Humans.Web/Models/ExpenseDetailViewModel.cs`
- Test: add to `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs`

> **Ctor change (do this first — Tasks 10/11 tests depend on it):** add `IHoldedFinanceService holdedFinance` to the `ExpenseReportService` primary constructor (place it after `IHoldedClient holdedClient`). In the test harness ctor, add `private readonly IHoldedFinanceService _holdedFinance = Substitute.For<IHoldedFinanceService>();` and a `private readonly IHoldedClient _holdedClient = Substitute.For<IHoldedClient>();`, and pass both into `new ExpenseReportService(...)`. `IHoldedFinanceService` is already DI-registered (`AddHoldedSection`), so production wiring needs no change.

- [ ] **Step 1: Add the timeline record + extend `ExpenseDetailViewData`** in `IExpenseReportService.cs`:

```csharp
public sealed record ExpenseDetailViewData(
    string CategoryDisplayName,
    bool CanEdit,
    bool CanSubmit,
    bool CanWithdraw,
    bool HasIban,
    string? MaskedIban,
    ExpenseHoldedTimeline? HoldedTimeline);

/// <summary>Round-trip timeline for the submitter, sourced from the Holded creditor balance.</summary>
public sealed record ExpenseHoldedTimeline(
    bool RegisteredInHolded,
    decimal OwedToMember,
    decimal MemberRegisteredTotal,   // sum of this member's registered-but-unpaid ER totals
    decimal OtherAmount,             // max(0, OwedToMember - MemberRegisteredTotal): fronted / adjustments
    bool Paid,
    NodaTime.LocalDate? PaidOn,
    decimal TotalPaid);
```

- [ ] **Step 2: Build the timeline in `GetDetailViewDataAsync`.** Replace the method so it also assembles the timeline (only for the submitter, and only when a Holded contact exists):

```csharp
    public async Task<ExpenseDetailViewData> GetDetailViewDataAsync(
        Guid viewerUserId, ExpenseReportDto report, CancellationToken ct = default)
    {
        var category = await budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
        var categoryName = category is not null
            ? $"{category.BudgetGroup?.Name} / {category.Name}"
            : "(unknown category)";

        var isSubmitter = report.SubmitterUserId == viewerUserId;
        var canWithdraw = report.Status is ExpenseReportStatus.Submitted
            or ExpenseReportStatus.CoordinatorEndorsed
            or ExpenseReportStatus.Approved;
        var iban = await GetSubmitterIbanViewAsync(viewerUserId, ct);

        var timeline = isSubmitter
            ? await BuildHoldedTimelineAsync(report, ct)
            : null;

        return new ExpenseDetailViewData(
            CategoryDisplayName: categoryName,
            CanEdit: isSubmitter && report.Status == ExpenseReportStatus.Draft,
            CanSubmit: isSubmitter && report.Status == ExpenseReportStatus.Draft,
            CanWithdraw: isSubmitter && canWithdraw,
            HasIban: iban.HasIban,
            MaskedIban: iban.MaskedIban,
            HoldedTimeline: timeline);
    }

    /// <summary>
    /// Aggregates the submitter's owed/paid round-trip from the cached Holded creditor balance.
    /// The balance already sums all of a member's outstanding docs; when it exceeds the member's
    /// own registered-unpaid ER totals, the remainder is shown as fronted/adjustments (spec §3).
    /// </summary>
    private async Task<ExpenseHoldedTimeline?> BuildHoldedTimelineAsync(
        ExpenseReportDto report, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(report.HoldedContactId))
            return new ExpenseHoldedTimeline(
                RegisteredInHolded: false, OwedToMember: 0m, MemberRegisteredTotal: 0m,
                OtherAmount: 0m, Paid: false, PaidOn: null, TotalPaid: 0m);

        var status = await holdedFinance.GetCreditorStatusAsync(
            report.HoldedSupplierAccountNum, report.HoldedContactId, ct);

        // The member's own registered, not-yet-paid ER totals (what Humans expects to be owed).
        var memberReports = await repo.GetForSubmitterAsync(report.SubmitterUserId, ct);
        var memberRegisteredTotal = memberReports
            .Where(r => r.HoldedDocId is not null
                     && r.Status is ExpenseReportStatus.Approved or ExpenseReportStatus.SepaSent)
            .Sum(r => r.Total);

        var owed = status?.OwedToMember ?? 0m;
        var totalPaid = status?.TotalPaid ?? 0m;
        var paid = status is not null && status.Balance >= 0m && totalPaid > 0m;

        return new ExpenseHoldedTimeline(
            RegisteredInHolded: report.HoldedDocId is not null,
            OwedToMember: owed,
            MemberRegisteredTotal: memberRegisteredTotal,
            OtherAmount: Math.Max(0m, owed - memberRegisteredTotal),
            Paid: paid,
            PaidOn: status?.LastPaymentDate,
            TotalPaid: totalPaid);
    }
```

- [ ] **Step 3: Carry the timeline on the view model.** Add to `ExpenseDetailViewModel`:

```csharp
    public ExpenseHoldedTimeline? HoldedTimeline { get; init; }
```

(add `using Humans.Application.Interfaces.Expenses;` if needed). In `ExpensesController.Detail`, map it:

```csharp
                MaskedIban = detail.MaskedIban,
                HoldedTimeline = detail.HoldedTimeline
```

- [ ] **Step 4: Write a failing test** for the timeline aggregation:

```csharp
    [HumansFact]
    public async Task GetDetailViewData_builds_timeline_with_owed_and_other()
    {
        var userId = Guid.NewGuid();
        var (_, category) = SetupActiveYear();
        var reportId = await SeedApprovedReportWithAttachmentAsync(userId, category.Id, payeeName: "Legal");
        await _expenseRepo.SetHoldedContactLinkAsync(reportId, "c1", 40000007, FakeNow);
        // pretend the doc was pushed:
        await _expenseRepo.SetHoldedDocIdAsync(reportId, "doc-1", FakeNow);

        _budgetService.GetCategoryByIdAsync(category.Id).Returns(MakeCategoryDetail(category));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(MakeProfile(userId, burner: "B", iban: "ES76")));
        // Holded says owed 200 though this member's ER is only 121 -> 79 is "other".
        _holdedFinance.GetCreditorStatusAsync(40000007, "c1", Arg.Any<CancellationToken>())
            .Returns(new HoldedCreditorStatus(40000007, Balance: -200m, OwedToMember: 200m,
                LastPaymentDate: null, TotalPaid: 0m));

        var report = await _sut.GetAsync(reportId);
        var detail = await _sut.GetDetailViewDataAsync(userId, report!);

        detail.HoldedTimeline.Should().NotBeNull();
        detail.HoldedTimeline!.RegisteredInHolded.Should().BeTrue();
        detail.HoldedTimeline.OwedToMember.Should().Be(200m);
        detail.HoldedTimeline.OtherAmount.Should().Be(200m - report!.Total);
    }
```

- [ ] **Step 5: Run — expect FAIL, then PASS after the implementation above.** Build.

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~ExpenseReportServiceTests"`

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Expenses/ExpenseReportService.cs src/Humans.Application/Interfaces/Expenses/IExpenseReportService.cs src/Humans.Web/Models/ExpenseDetailViewModel.cs src/Humans.Web/Controllers/ExpensesController.cs tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs
git commit -m "feat(expenses): submitter Holded round-trip timeline from creditor balance"
```

---

## Task 13: Detail.cshtml timeline UI — **[no schema change]**

**Files:**
- Modify: `src/Humans.Web/Views/Expenses/Detail.cshtml`

> Mirror the existing card/`dl` styling. Show the timeline only when `Model.HoldedTimeline` is non-null and `RegisteredInHolded` is true. Place a card in the right column (`col-md-4`) above or below the Payment IBAN card.

- [ ] **Step 1: Add the timeline card** (after the Payment IBAN card, inside `<div class="col-md-4">`):

```razor
        @if (Model.HoldedTimeline is { RegisteredInHolded: true } tl)
        {
            <div class="card mb-3">
                <div class="card-header"><strong>Payment status</strong></div>
                <div class="card-body">
                    <ul class="list-unstyled mb-0 small">
                        <li class="mb-2">
                            <i class="fa-solid fa-circle-check text-success me-1"></i>
                            Submitted &amp; approved
                        </li>
                        <li class="mb-2">
                            <i class="fa-solid fa-building-columns text-info me-1"></i>
                            Registered in Holded
                            @if (tl.OwedToMember > 0)
                            {
                                <span class="fw-semibold">&mdash; &euro;@tl.OwedToMember.ToString("N2") owed to you</span>
                            }
                        </li>
                        @if (tl.OtherAmount > 0)
                        {
                            <li class="mb-2 text-muted">
                                <i class="fa-solid fa-circle-info me-1"></i>
                                Includes &euro;@tl.OtherAmount.ToString("N2") other (fronted / adjustments in Holded)
                            </li>
                        }
                        @if (tl.Paid)
                        {
                            <li class="mb-0">
                                <i class="fa-solid fa-money-bill-wave text-success me-1"></i>
                                Paid&nbsp;&euro;@tl.TotalPaid.ToString("N2")@(tl.PaidOn is { } d ? $" on {d:yyyy-MM-dd}" : "") &mdash; balance &euro;0
                            </li>
                        }
                        else
                        {
                            <li class="mb-0 text-muted">
                                <i class="fa-regular fa-clock me-1"></i>Awaiting payment
                            </li>
                        }
                    </ul>
                </div>
            </div>
        }
```

- [ ] **Step 2: Build + commit** (Razor compiles with the Web build)

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Views/Expenses/Detail.cshtml
git commit -m "feat(expenses): payment-status timeline on the expense detail view"
```

---

## Task 14: Architecture tests + docs — **[no schema change]**

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs`
- Modify: `docs/sections/Finance.md` (+ `docs/sections/Expenses.md` if it exists)

- [ ] **Step 1: Pin the new repo methods stay EF-free at the service layer.** `FinanceArchitectureTests` already asserts `HoldedFinanceService` references no EF and no cross-section repos — those still hold (the new `GetCreditorStatusAsync`/`SyncCreditorDataAsync` use `IHoldedRepository` + `IHoldedClient` only). Add one assertion that the new cross-section read method lives on the Finance interface:

```csharp
    [HumansFact]
    public void IHoldedFinanceService_ExposesCreditorStatus_Read() =>
        typeof(IHoldedFinanceService).GetMethod("GetCreditorStatusAsync")
            .Should().NotBeNull("Expenses consumes creditor status cross-section via the Finance service interface");
```

- [ ] **Step 2: Confirm the full suite's architecture pins still pass** (no-EF-in-service, repository-ownership map, single-repo-per-table — the new tables are owned by `IHoldedRepository`, already registered in the ownership test from Feature 1; add `holded_creditor_balances` + `holded_payments` to that map if the test enumerates tables explicitly).

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Architecture"`
Expected: PASS. If the repository-ownership test lists tables, add the two new tables under `IHoldedRepository`.

- [ ] **Step 3: Docs.** In `docs/sections/Finance.md`, add a "Creditor exposure (Feature 2)" subsection: the `holded_creditor_balances` / `holded_payments` cache tables, the nightly `SyncCreditorDataAsync`, `GetCreditorStatusAsync` as the Expenses→Finance read, and the **read-only org-accounting boundary** (Humans never writes reassignment entries — spec §4). In the Expenses section doc, note the contact enrichment on push, the `HoldedContactId`/`HoldedSupplierAccountNum` fields, the balance-based paid signal, and the submitter timeline.

- [ ] **Step 4: Commit**

```bash
git add tests/Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs docs/sections/Finance.md docs/sections/Expenses.md
git commit -m "test+docs(finance): pin creditor read surface; document Feature 2"
```

---

## Full verification (before pushing for review)

- [ ] `dotnet build Humans.slnx -v quiet` → success
- [ ] `dotnet test Humans.slnx -v quiet` → all green **except** the 2 known-pre-existing Stripe `Pay_*` integration tests (verify they fail identically on the base before blaming this branch)
- [ ] `git grep -n "PaymentsPending"` → confirm no **production** code path still gates Paid on it (tests/DTO/client surface may remain)
- [ ] **Clear the build-time probes live** against the real account before relying on the feature: one ER push end-to-end → confirm (a) the contact is created with legal `name` + burner `tradeName`, (b) the purchase doc links to it, (c) `GET contact` returns `supplierRecord.num`, (d) the nightly `chartofaccounts`/`payments` pulls populate the cache, (e) a manual account-level payment in Holded flips the ER to Paid on the next poll. Fix the `// TODO(probe)` field names if any differ.
- [ ] `git push origin feat/holded-finance-creditor`
- [ ] Open the **stacked PR**: base = `feat/holded-finance-integration`, head = `feat/holded-finance-creditor`.

---

## Self-review notes (gaps to confirm during execution)

- **`UserInfo.BurnerName`** is the burner accessor used by `ExpensesController.ResolveSubmitterNamesAsync` — confirm the same on the `UserInfo` returned by `userService.GetUserInfoAsync` inside the service; if the display name lives elsewhere (`Profile.BurnerName`), adjust `UpsertHoldedContactAsync`.
- **Contact payload field names** (`name`/`tradeName`/`customId`/`type`/`iban`) and **purchase-doc `contactId`** field — `// TODO(probe)`; verify before "Add all" / first real push.
- **`supplierRecord.num` timing** — assumed assigned once a payable is booked; resolution happens post-doc-create with a paid-poll backfill. If Holded assigns it earlier, the backfill is simply a no-op.
- **Paid trigger semantics** — `Balance >= 0` settles **all** of a member's SepaSent reports; a member who submits a fresh ER after payment re-opens a negative balance, which correctly leaves the new (not-yet-SepaSent) report untouched.
- **`MarkPaidAsync` signature change** — confirm no other production caller relied on the old `MarkPaidAsync(Guid)` overload (search done in Task 11 Step 4).
- **Holded contact deep-link / timeline copy** — wording can be refined; the data is the contract.
