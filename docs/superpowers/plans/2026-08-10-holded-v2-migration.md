# Holded API v2 Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the v1 Holded client with v2, widen the ledger cache to a full all-accounts mirror with balance reconciliation, and add the `/Finance/Holded` admin screen.

**Architecture:** The connector (`IHoldedClient` in Humans.Application, `HoldedClient` in Humans.Infrastructure) is rewritten endpoint-for-endpoint against `api/v2`; the Finance section (`src/Sections/Humans.Finance`) gains two tables (`holded_accounts`, `holded_api_calls`), a re-keyed `holded_sync_states`, a rewritten ledger sync with reconciliation, and the admin screen. v1 code is deleted in the same PR — no dual path.

**Tech Stack:** ASP.NET Core 10 MVC, EF Core (`FinanceDbContext`, Postgres; EF-InMemory in tests), NodaTime, Hangfire, xunit + AwesomeAssertions (`HumansFact`, `StubHandler`).

**Spec:** `src/Sections/Humans.Finance/Docs/2026-08-10-holded-v2-migration-design.md` — read it first; API shapes there are live-probed.

## Global Constraints

- Branch/worktree: `.worktrees/holded-v2` (exists, branch `holded-v2`, pushed). Never commit in the main checkout. Push after every task.
- Build/test: `dotnet build Humans.slnx -v quiet` / `dotnet test Humans.slnx -v quiet` (`-v quiet` mandatory), run from the worktree root with the worktree's absolute solution path if cwd is doubtful.
- Layering (peters-hard-rules.md): DbContext → Repository → Service → Controller; only `Repository` touches `FinanceDbContext`; `HoldedClient` never touches the DB — metering goes through an in-memory queue drained by the service.
- EF: never hand-edit migrations; schema-only migration, **no data migrations** (the sync refills everything). One migration for the whole PR.
- v2 API facts (live-probed — trust these over doc pages): base `https://api.holded.com/api/v2`, `Authorization: Bearer <key>`, snake_case JSON, decimals as **strings** (`"121.00"`), cursor pagination `{items:[], cursor, has_more}` with `limit` max 200, **ledger-entries dates are `DD/MM/YYYY`**, purchases dates are ISO `YYYY-MM-DD`, 429 carries `Retry-After` seconds + `X-RateLimit-Remaining`/`X-RateLimit-Window` headers.
- Full OpenAPI spec: `https://api.holded.com/openapi/api2.json` — the authority for any field not covered here.
- Read-only live probing allowed via the token in `C:\Users\PeterDrier\.holded\dev-token` (keep to a handful of calls); write endpoints are fixture-tested only.
- Config unchanged: `HOLDED_API_KEY` env var; jobs/pages no-op cleanly when unset.

---

### Task 1: v2 transport — Bearer auth, 429 handling, call metering

**Files:**
- Create: `src/Humans.Application/Interfaces/Holded/IHoldedCallLog.cs`
- Create: `src/Humans.Infrastructure/Services/Holded/HoldedCallLog.cs`
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs` (`AttachAuth`, `SendAsync`, constructor)
- Modify: `src/Humans.Web/Extensions/Sections/HoldedConnectorExtensions.cs` (register singleton)
- Test: `tests/Humans.Application.Tests/Services/Holded/HoldedClientTransportTests.cs`
- Modify: the three existing `HoldedClient*Tests.cs` `Make(...)` helpers (constructor gains the log param)

**Interfaces (Produces):**

```csharp
// Humans.Application.Interfaces.Holded
public sealed record HoldedApiCallRecord(
    Instant CalledAt, string Endpoint, string Method, int StatusCode,
    int? RateLimitRemaining, string? RateLimitWindow);

/// <summary>In-memory buffer of Holded API calls. The client appends; the Finance service drains
/// to holded_api_calls. Singleton; loses at most the unflushed tail on crash (GET /usage is the
/// authoritative counter).</summary>
public interface IHoldedCallLog
{
    void Record(HoldedApiCallRecord record);
    IReadOnlyList<HoldedApiCallRecord> DrainAll();
}
```

`HoldedCallLog` wraps a `ConcurrentQueue<HoldedApiCallRecord>`; `DrainAll` dequeues until empty.

- [ ] **Step 1: failing tests** — in `HoldedClientTransportTests.cs`, using the existing `StubHandler`/`Make` pattern from `HoldedClientReadTests.cs` (Make now also takes/creates a `HoldedCallLog` and an `IClock` — use `NodaTime.Testing.FakeClock`):
  - `Sends_bearer_authorization_header`: capture `req.Headers.Authorization`; expect scheme `Bearer`, parameter `test-key`; assert the legacy `key` header is absent.
  - `Retries_once_on_429_honoring_retry_after`: stub returns 429 with `Retry-After: 0` then 200; expect success and 2 requests.
  - `Throws_transient_when_429_persists`: two 429s → `HoldedTransientException`.
  - `Records_call_in_log`: after any call, `log.DrainAll()` has one record with `Endpoint` = calling method name (e.g. `"ListExpenseAccountsAsync"`), `StatusCode` 200, and `RateLimitRemaining`/`RateLimitWindow` parsed from stubbed `X-RateLimit-Remaining: 42` / `X-RateLimit-Window: minute` headers.
- [ ] **Step 2:** run new tests, verify FAIL (missing type / wrong header).
- [ ] **Step 3: implement.** `AttachAuth` → `req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);`. In `SendAsync`: after receiving the response, `_callLog.Record(new(_clock.GetCurrentInstant(), caller, req.Method.Method, (int)resp.StatusCode, remaining, window))` (parse headers defensively — absent → null). On 429: read `Retry-After` (delta seconds; cap the wait at 60 s, default 5 s when absent), `await Task.Delay(...)`, retry **once** (clone-safe: build the retry inside `SendAsync`'s caller loop is NOT possible with consumed content — instead do the retry only for requests without content, and for content-bearing requests throw `HoldedTransientException` immediately with the Retry-After noted in the message; GETs are the volume). Persisting 429 → `HoldedTransientException`. Constructor gains `IHoldedCallLog callLog, IClock clock` params. Register in `HoldedConnectorExtensions`: `services.AddSingleton<IHoldedCallLog, HoldedCallLog>();`.
- [ ] **Step 4:** `dotnet build Humans.slnx -v quiet` then run the Holded client test files; all pass (existing tests updated for the new ctor params — pass a fresh `HoldedCallLog` + `FakeClock`).
- [ ] **Step 5:** commit `feat(holded): v2 bearer transport with 429 handling and call metering`, push.

### Task 2: v2 read endpoints — ledger-entries, accounting-accounts, usage

**Files:**
- Modify: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`
- Modify: `src/Humans.Application/Interfaces/Holded/HoldedReadDtos.cs`
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
- Test: `tests/Humans.Application.Tests/Services/Holded/HoldedClientReadTests.cs` (replace ledger tests, add new)

**Interfaces (Produces):**

```csharp
// replaces ListDailyLedgerAsync on IHoldedClient:
Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
    LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default);
Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(CancellationToken ct = default);
Task<HoldedUsageDto> GetUsageAsync(CancellationToken ct = default);

// HoldedReadDtos.cs — HoldedLedgerLineDto unchanged (Date stays Instant). New:
public sealed record HoldedAccountDto
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public required string Name { get; init; }
    public string? Group { get; init; }
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public required decimal Balance { get; init; }
    public bool Archived { get; init; }
}
public sealed record HoldedUsageDto
{
    public required string Period { get; init; }         // "2026-08"
    public required long Usage { get; init; }
    public required long Limit { get; init; }
    public IReadOnlyDictionary<string, long> SecondaryUsages { get; init; }
        = new Dictionary<string, long>();
}
```

**v2 wire shapes (live-probed):**
- `GET /api/v2/ledger-entries?start_date=YYYY-MM-DD&end_date=YYYY-MM-DD[&account=N][&limit=200][&cursor=…]` → `{items:[{entry_number:2064, line:2, date:"09/02/2026", type:"payment", description:"", doc_description:"", account:40000004, debit:"0.00", credit:"1200.00", tags:[], checked:false}], cursor, has_more}`. **`date` is DD/MM/YYYY** — parse with `LocalDatePattern.CreateWithInvariantCulture("dd/MM/yyyy")`, then `date.AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant()` into `HoldedLedgerLineDto.Date`. `debit`/`credit` are strings → `decimal.Parse(s, CultureInfo.InvariantCulture)`.
- `GET /api/v2/accounting-accounts` → `{items:[{id, color, number:10000000, name, description, group, debit:"0.00", credit:"0.00", balance:"0.00", archived:false, non_deductible:false}]}` (no pagination observed for 267 accounts; still tolerate `cursor`/`has_more` if present).
- `GET /api/v2/usage` → `{type:"automation_token", period:"2026-08", usage:36, limit:2000000, count:1, secondary_usages:{"api_v1_legacy_1":35}, user_usages:[], next_plan:null, next_limit:null}`.

Add one private cursor-pager helper and reuse it for every v2 list endpoint:

```csharp
/// <summary>Walks a v2 cursor-paginated collection: follows `cursor` while `has_more`, yielding
/// each `items` element. Caps at pageSafetyCap pages and logs if hit (no silent caps).</summary>
private async Task<List<JsonNode>> GetPagedAsync(
    string pathAndQuery, int pageSafetyCap, CancellationToken ct)
```
(append `cursor=` with `&`/`?` as appropriate; `limit=200` belongs in the caller's query).

- [ ] **Step 1: failing tests** (canned v2 JSON, exactly the shapes above):
  - `ListLedgerEntries_parses_ddMMyyyy_dates_and_string_decimals`: one item dated `"09/02/2026"` → `Date` = 2026-02-09 midnight Madrid as Instant (assert against `LocalDate(2026,2,9).AtStartOfDayInZone(...)`), `Credit == 1200.00m`, `AccountNum == 40000004`.
  - `ListLedgerEntries_follows_cursor_until_has_more_false`: page 1 `has_more:true, cursor:"c1"` + page 2 `has_more:false`; expect both items and second request query containing `cursor=c1`.
  - `ListLedgerEntries_passes_account_filter`: `accountNum: 40000004` → query contains `account=40000004`.
  - `ListAccountingAccounts_parses_totals`: number/name/group/debit/credit/balance from string decimals.
  - `GetUsage_parses_period_usage_limit_and_secondary`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement the three methods + pager; delete `ListDailyLedgerAsync` from interface and client (compiler errors in the service are expected — leave the Finance service temporarily calling a thin shim only if needed to keep the build green within this task; otherwise sequence Task 5's service change together with this build break in the same commit train but keep each test suite green at commit time by doing the minimal service call-site swap: `client.ListLedgerEntriesAsync(from.InZone(zone).Date, to.InZone(zone).Date)` inside the existing `BackfillLedgerAsync`/`IncrementalLedgerAsync`).
- [ ] **Step 4:** build + run Holded client tests; pass. Full `dotnet test Humans.slnx -v quiet` must be green before commit.
- [ ] **Step 5:** commit `feat(holded): v2 read endpoints — ledger-entries, accounting-accounts, usage`, push.

### Task 3: v2 purchases, contacts, expenses-accounts + approval flag

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs` (remaining endpoints)
- Modify: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`, `HoldedReadDtos.cs`, `HoldedPurchaseDocumentDto.cs`
- Modify: `src/Sections/Humans.Finance/Domain/HoldedExpenseDoc.cs` + `Data/Configurations/HoldedExpenseDocConfiguration.cs` (`ApprovedAt` → `IsApproved`; migration comes in Task 4)
- Modify: `src/Sections/Humans.Finance/Services/Service.cs` (`MapDoc`, `GetActualsForYearAsync`, `SyncAsync` paging)
- Modify: `src/Humans.Application/Services/Expenses/ExpenseReportService.cs` call sites (compiler-driven)
- Test: `HoldedClientTests.cs`, `HoldedClientContactTests.cs`, `HoldedClientReadTests.cs`

**Interfaces (Produces / changes):**

```csharp
// IHoldedClient — replaces ListPurchaseDocumentsPageAsync:
Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(CancellationToken ct = default);
/// <summary>Ids of purchases still in draft (unapproved) — GET /purchases?approval_status=draft.</summary>
Task<IReadOnlySet<string>> ListDraftPurchaseIdsAsync(CancellationToken ct = default);
// HoldedPurchaseDocListItemDto: ApprovedAt removed; the service derives approval from the draft-id set.
// HoldedPurchaseDocumentDto: ApprovedAt stays (single GET has approved_at).
// HoldedPurchaseDocumentInput.ContactId becomes `required string` (v2 POST requires contact_id).
// HoldedExpenseDoc: `Instant? ApprovedAt` → `bool IsApproved`.
```

**v2 wire shapes:**
- `POST /api/v2/purchases` body `{contact_id, contact_name, date:"2026-05-14", description, tags:[...], items:[{name, units:1, price}]}` → 201 `{id}`. Line-level tags ride on the item only if the OpenAPI item schema carries `tags` — it does not (fields: name/type/description/product_id/units/price/discount/taxes/sku/account/project_id/unit_type), so put all tags at doc level (today's `MapDoc` already unions doc+line tags on read).
- `PUT /api/v2/purchases/{id}` body `{tags:[...]}` for the tag update.
- `POST /api/v2/purchases/{id}/attachments` — multipart, part name `file`.
- `GET /api/v2/purchases` items: `{id, document_number, contact_id, contact_name, date:"2024-01-15", subtotal:"100.00", tax:"21.00", total:"121.00", currency:"EUR", status, tags:[], lines:[{price:"100.00", units:1, account, tags? (absent), …}], payments_total, payments_pending}` — dates ISO, decimals strings. `lines[].account`: **probe its runtime type once with the dev token** (v1 sent the account *id* string; the matcher's `HoldedMatchEntry` carries both `HoldedAccountId` and `HoldedAccountNumber`, so map whichever arrives: string → `BookedAccountId` as today; integer → resolve through `entries` by number to the mapped id before calling `HoldedMatcher.Match`).
- `GET /api/v2/purchases/{id}`: adds `approved_at`, `draft`, `payments_total`, `payments_pending` → keep `HoldedPurchaseDocumentDto` shape (parse `approved_at` ISO → Instant?).
- `GET/POST /api/v2/expenses-accounts`: `{items:[{id, name, account_num:6290001, archived, …}]}` / POST `{name, account_num}` → `{id}`.
- `GET /api/v2/contacts` (cursor-paged): items carry `{id, custom_id, name, trade_name, type, iban, supplier_record:{num, name}}` → `HoldedContactDto.SupplierAccountNum = supplier_record.num`. `POST/PUT /api/v2/contacts` body `{name, trade_name, custom_id, type, iban}` → `{id}`.

- [ ] **Step 1: failing tests** — rewrite the affected cases in the three client-test files to v2 fixtures: purchase create posts snake_case body to `/api/v2/purchases` (capture and assert `contact_id` + ISO date); list parses string decimals + ISO date; draft-ids call sends `approval_status=draft`; contact parse reads `supplier_record.num`; expense-account create posts `{name, account_num}`; attachment posts multipart to `/attachments`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement client changes; update `SyncAsync` in `Service.cs` to `var allDocs = await client.ListPurchaseDocumentsAsync(ct); var draftIds = await client.ListDraftPurchaseIdsAsync(ct);` and `MapDoc(doc, entries, draftIds, now)` setting `IsApproved = !draftIds.Contains(doc.Id)`; `GetActualsForYearAsync` filter becomes `d.IsApproved && d.BudgetCategoryId is not null`; fix `ExpenseReportService` call sites the compiler flags (`ContactId` now required — it already passes the id from `EnsureCreditorContactAsync`).
- [ ] **Step 4:** build + full test suite green (`HoldedExpenseDoc` model change breaks the EF model snapshot only at migration time — Task 4 adds the migration; EF-InMemory tests pick the new model up automatically).
- [ ] **Step 5:** commit `feat(holded): v2 purchases, contacts and expenses-accounts endpoints`, push.

### Task 4: Finance data model — re-keyed sync state, holded_accounts, holded_api_calls, migration

**Files:**
- Create: `src/Sections/Humans.Finance/Domain/HoldedSyncKind.cs`, `HoldedAccount.cs`, `HoldedApiCall.cs`
- Modify: `src/Sections/Humans.Finance/Domain/HoldedSyncState.cs`, `Domain/HoldedLedgerLine.cs` (doc comment only — no longer creditor-scoped)
- Create: `src/Sections/Humans.Finance/Data/Configurations/HoldedAccountConfiguration.cs`, `HoldedApiCallConfiguration.cs`
- Modify: `Data/Configurations/HoldedSyncStateConfiguration.cs`, `Data/FinanceDbContext.cs`, `Data/IHoldedRepository.cs`, `Data/Repository.cs`
- Create (generated): `src/Sections/Humans.Finance/Data/Migrations/*_HoldedV2.cs`
- Test: `tests/Humans.Finance.Tests/HoldedRepositoryTests.cs` (new, EF-InMemory)

**Interfaces (Produces):**

```csharp
// Domain
internal enum HoldedSyncKind { Ledger, Accounts, PurchaseDocs, FullSync }
internal sealed class HoldedSyncState        // re-keyed
{
    public HoldedSyncKind Kind { get; init; }
    public Instant? LastSyncAt { get; set; }
    public HoldedSyncStatus SyncStatus { get; set; } = HoldedSyncStatus.Idle;
    public string? LastError { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastCount { get; set; }
}
internal sealed class HoldedAccount
{
    public int Number { get; init; }         // PK — the literal chart number
    public string HoldedId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Group { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public bool Archived { get; set; }
    public Instant SyncedAt { get; set; }
}
internal sealed class HoldedApiCall
{
    public Guid Id { get; init; }
    public Instant CalledAt { get; init; }
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "";
    public int StatusCode { get; init; }
    public int? RateLimitRemaining { get; init; }
    public string? RateLimitWindow { get; init; }
}

// IHoldedRepository — GetSyncStateAsync/SaveSyncStateAsync replaced; additions:
Task<HoldedSyncState> GetOrCreateSyncStateAsync(HoldedSyncKind kind, CancellationToken ct = default);
Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
Task<IReadOnlyList<HoldedSyncState>> GetSyncStatesAsync(CancellationToken ct = default);
Task UpsertAccountsAsync(IReadOnlyList<HoldedAccount> rows, Instant now, CancellationToken ct = default);
Task<IReadOnlyList<HoldedAccount>> GetAccountsAsync(CancellationToken ct = default);
Task AddApiCallsAsync(IReadOnlyList<HoldedApiCall> rows, CancellationToken ct = default);
Task<IReadOnlyList<HoldedApiCall>> GetApiCallsAsync(CancellationToken ct = default);
/// <summary>Upserts the fetched window and deletes local rows inside [from,to] (optionally one
/// account) that the fetch no longer contains — the fetch is the truth for its window.</summary>
Task ReplaceLedgerWindowAsync(Instant from, Instant to, int? accountNum,
    IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default);
```

Config specifics: `holded_sync_states` keyed on `Kind` (`HasConversion<string>().HasMaxLength(16)` for both enums, **no `HasData` seed** — lazy-created); `holded_accounts` keyed on `Number`, decimals `decimal(14,2)`; `holded_api_calls` keyed on `Id`, `Endpoint`/`Method`/`RateLimitWindow` max lengths 64/8/16, index on `CalledAt`. `ReplaceLedgerWindowAsync` follows `UpsertLedgerLinesAsync`'s dictionary pattern, then `ctx.HoldedLedgerLines.Where(l => l.Date >= from && l.Date <= to && (accountNum == null || l.AccountNum == accountNum))`, removing those whose `(EntryNumber, Line)` is absent from `rows`. Keep `UpsertLedgerLinesAsync` only if still called; otherwise delete it.

- [ ] **Step 1: failing tests** (`HoldedRepositoryTests.cs`, `DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(...)` with a factory stub — mirror whatever fixture style `tests/Humans.Finance.Tests` already uses):
  - `GetOrCreateSyncState_creates_then_returns_same_row`.
  - `ReplaceLedgerWindow_upserts_and_deletes_missing`: seed lines A(entry 1/1, date in window), B(entry 2/1, in window), C(entry 3/1, outside window); replace window with only A′(changed credit) + new D → expect A updated, B deleted, C untouched, D inserted.
  - `ReplaceLedgerWindow_scoped_to_account_leaves_other_accounts`: same-window line on another account survives.
  - `UpsertAccounts_replaces_totals`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement entities/configs/DbContext/repo. Callers of the old singleton API (`Service.SyncAsync`, `SyncCreditorLedgerAsync` error paths) switch to `GetOrCreateSyncStateAsync(HoldedSyncKind.PurchaseDocs / .Ledger)` — full rewrite of those methods lands in Task 5; here only rename-level changes to keep green.
- [ ] **Step 4:** generate the migration (from the worktree root):
  `dotnet ef migrations add HoldedV2 --project src/Sections/Humans.Finance --startup-project src/Humans.Web --context FinanceDbContext --output-dir Data/Migrations`
  Review per `.claude/agents/ef-migration-reviewer.md` expectations: expect drop/recreate of `holded_sync_states` (old seeded singleton removed — statuses are ephemeral, acceptable), `ApprovedAt` column dropped + `IsApproved` added on `holded_expense_docs`, two new tables. **No hand edits; no data SQL.**
- [ ] **Step 5:** build + full suite green.
- [ ] **Step 6:** commit `feat(holded): finance data model for v2 — per-kind sync state, accounts cache, call log (one migration)`, push.

### Task 5: sync rewrite — full mirror, reconciliation, job

**Files:**
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceService.cs` + `HoldedDtos.cs`
- Modify: `src/Sections/Humans.Finance/Services/Service.cs`
- Modify: `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs`
- Modify: `src/Sections/Humans.Finance/Controllers/FinanceController.cs` (`RunHoldedSync` calls the new method)
- Test: `tests/Humans.Finance.Tests/HoldedLedgerSyncTests.cs` (new)

**Interfaces (Produces):**

```csharp
// IHoldedFinanceService — SyncCreditorLedgerAsync replaced by:
/// <summary>Ledger mirror sync. full=false: incremental window (local max date − 7 days → today).
/// full=true: inception → today, replace semantics. Both then refresh the accounts cache,
/// reconcile per-account totals, re-pull mismatched accounts once, and drain the API call log.</summary>
Task<HoldedLedgerSyncResult> SyncLedgerAsync(bool full, CancellationToken ct = default);

// HoldedDtos.cs:
public sealed record HoldedLedgerSyncResult(
    int LinesUpserted, int AccountsRefreshed,
    IReadOnlyList<HoldedReconcileMismatch> Mismatches);
public sealed record HoldedReconcileMismatch(
    int AccountNum, string AccountName, decimal HoldedBalance, decimal LocalBalance);
```

Service constants: `private static readonly LocalDate LedgerInception = new(2020, 1, 1);` `private static readonly Duration IncrementalOverlap = Duration.FromDays(7);` Delete `CreditorAccountMin/Max` **filtering from the sync only** — the constants stay for the creditor-binding paths (`SetCreditorContactAsync` range check, `ListCreditorAccountsAsync` contact filter), which are member-facing and still creditor-block-scoped.

Algorithm (one method, private helpers as needed):
1. State `Ledger` (or `FullSync` when `full`) → Running (reuse the existing try/catch state-write shape from today's `SyncAsync`).
2. Window: `full || cache empty` → `[LedgerInception, today]` (today = `clock` in Madrid zone); else `[local max Date − 7 days, today]` as LocalDates.
3. `client.ListLedgerEntriesAsync(from, to)` → map to `HoldedLedgerLine` (same mapping as today, **no account filter**) → `repo.ReplaceLedgerWindowAsync(fromInstant, toInstant, null, lines, now)`.
4. Accounts refresh: `client.ListAccountingAccountsAsync()` → map → `repo.UpsertAccountsAsync` → state `Accounts` updated. (This satisfies the spec's "Refresh accounts" as a by-product; the button in Task 7 calls the same private helper.)
5. Reconcile: local sums via `repo.GetAllLedgerLinesAsync()` grouped by `AccountNum` (`Σdebit`, `Σcredit`); for each **non-archived** Holded account where `holded.Balance != localDebit − localCredit` (treat missing local group as 0): one targeted `client.ListLedgerEntriesAsync(LedgerInception, today, account.Number)` + `repo.ReplaceLedgerWindowAsync(..., account.Number, ...)`, then recompute; still off → add to `Mismatches`. **Cap targeted re-pulls at 10 accounts per run** and log the count when capped (no silent caps); a standing mismatch is reportable state, not an error loop (live example in the spec: entry #2412 excluded from chart totals).
6. Mismatches → `Ledger` state `LastError` = `"57200001: holded 418840.54, local 771074.85; …"` (null when none); `LastCount` = lines upserted.
7. Drain: `callLog.DrainAll()` → map to `HoldedApiCall` → `repo.AddApiCallsAsync`. (Also drained by `GetHoldedAdminAsync` in Task 6.)

`HoldedSyncJob.ExecuteAsync` → `await finance.SyncAsync(ct); await finance.SyncLedgerAsync(full: false, ct);`. `FinanceController.RunHoldedSync` keeps running `SyncAsync` inline and now also `SyncLedgerAsync(false)` (success message extends with lines count).

- [ ] **Step 1: failing tests** (`HoldedLedgerSyncTests.cs`; fake `IHoldedClient` + real `Repository` over EF-InMemory + `FakeClock`, matching Task 4's fixture):
  - `First_run_backfills_all_accounts_from_inception`: fake client returns lines on accounts 40000004 and 57200001; both cached (creditor filter gone); client received `from == 2020-01-01`.
  - `Incremental_uses_seven_day_overlap`: seed cache with max date D; client asserts `from == D.Date − 7 days`.
  - `Reconcile_repulls_mismatched_account_and_reports_residual`: accounts fixture where one account's Holded balance ≠ local sum even after re-pull → result `Mismatches` has it, sync state `LastError` mentions the account number; matching account produces no re-pull call.
  - `Full_sync_replaces_deleted_lines`: cache holds a line the full fetch no longer returns → gone after `SyncLedgerAsync(true)`.
  - `Drains_call_log_into_repo`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement service + job + controller call-site.
- [ ] **Step 4:** build + full suite green. Grep for `SyncCreditorLedgerAsync` — zero references left.
- [ ] **Step 5:** commit `feat(holded): full-mirror ledger sync with balance reconciliation`, push.

### Task 6: admin overview service method

**Files:**
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceService.cs`, `HoldedDtos.cs`
- Modify: `src/Sections/Humans.Finance/Services/Service.cs`
- Test: `tests/Humans.Finance.Tests/HoldedAdminOverviewTests.cs` (new)

**Interfaces (Produces):**

```csharp
// IHoldedFinanceService:
/// <summary>Everything /Finance/Holded renders. One live Holded call (usage); the rest local.</summary>
Task<HoldedAdminOverview> GetHoldedAdminAsync(CancellationToken ct = default);

// HoldedDtos.cs:
public sealed record HoldedAdminOverview(
    bool ApiKeyConfigured,
    HoldedUsageInfo? Usage,                                  // null when the usage call fails/no key
    IReadOnlyList<HoldedMonthlyCalls> CallsByMonth,          // newest first
    IReadOnlyList<HoldedSyncStateInfo> SyncStates,
    IReadOnlyList<HoldedAccountRow> Accounts,                // full chart, non-archived
    IReadOnlyList<HoldedAccountRow> DepartmentActuals,       // the 629* slice, ordered by balance desc
    HoldedTotals Totals);
public sealed record HoldedUsageInfo(string Period, long Usage, long Limit,
    IReadOnlyDictionary<string, long> SecondaryUsages);
public sealed record HoldedMonthlyCalls(int Year, int Month, int Total,
    IReadOnlyDictionary<string, int> ByEndpoint);
public sealed record HoldedSyncStateInfo(string Kind, string Status,
    Instant? LastSyncAt, string? LastError, int LastCount);
public sealed record HoldedAccountRow(int Number, string Name, string? Group,
    decimal HoldedBalance, decimal? LocalBalance, int LocalLineCount, bool Reconciled);
public sealed record HoldedTotals(int LedgerLines, int PurchaseDocs, int CreditorBindings);
```

Assembly: `ApiKeyConfigured` from `IOptions<HoldedClientOptions>` (inject into `Service` — it already lives in Application-visible namespace? **No** — `HoldedClientOptions` is Infrastructure. Instead: `Usage is null && !apiKeyMissing` distinction is not worth a layering hole; derive `ApiKeyConfigured` by catching `HoldedPermanentException` from the usage call — simpler: call `client.GetUsageAsync()` in try/catch (`HoldedTransientException`/`HoldedPermanentException` → `Usage = null`), and set `ApiKeyConfigured = Usage is not null`). `CallsByMonth` from `repo.GetApiCallsAsync()` grouped in memory by `CalledAt` in Madrid zone (year, month, count, per-`Endpoint` counts) — drain the call log into the repo first so the current page-load's own calls appear next load. `Accounts` join `repo.GetAccountsAsync()` (non-archived) with ledger sums from `repo.GetAllLedgerLinesAsync()`; `LocalBalance = Σdebit − Σcredit` (null when no lines), `Reconciled = HoldedBalance == (LocalBalance ?? 0m)`. `DepartmentActuals` = accounts whose `Number` is in `[62900000, 62999999]`, ordered by `HoldedBalance` desc. Totals from existing repo reads (`GetAllLedgerLinesAsync().Count`, docs via a count over `GetUnmatchedAsync` is wrong — add nothing: reuse `repo.GetMatchedForYearAsync`? No — **add one small repo count method is not justified**; `HoldedTotals.PurchaseDocs` comes from `GetApiCallsAsync`? No. Use the sync state: `PurchaseDocs` = the `PurchaseDocs` sync state's `LastCount`; `CreditorBindings` = `repo.GetCreditorContactsAsync().Count`.)

- [ ] **Step 1: failing tests:** monthly grouping (two calls in different months → two buckets, newest first); 629-slice filter + ordering; `Reconciled` flag false on a mismatched account; usage failure → `Usage null`, rest populated.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** build + suite green.
- [ ] **Step 5:** commit `feat(holded): admin overview read model`, push.

### Task 7: /Finance/Holded screen

**Files:**
- Modify: `src/Sections/Humans.Finance/Controllers/FinanceController.cs`
- Create: `src/Sections/Humans.Finance/Views/Finance/Holded.cshtml`
- Modify: `src/Sections/Humans.Finance/Views/Finance/HoldedAccounts.cshtml` + `HoldedUnmatched.cshtml` + `Creditors.cshtml` (one nav link each to the new page, matching their existing header-link markup)
- Test: none new beyond compilation — controller is parse/call/format only; the view renders `HoldedAdminOverview` directly as its model.

Actions (all under the existing `[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]`):

```csharp
[HttpGet("Holded")]
public async Task<IActionResult> Holded()                    // View(await holdedFinance.GetHoldedAdminAsync())

[HttpPost("Holded/SyncNow")] [ValidateAntiForgeryToken]      // SyncAsync + SyncLedgerAsync(false); SetSuccess with counts + mismatch count; redirect to Holded
[HttpPost("Holded/FullSync")] [ValidateAntiForgeryToken]     // SyncLedgerAsync(true); same reporting; redirect to Holded
```

(No separate Refresh-accounts action: both sync paths refresh the accounts cache — a third button would duplicate `SyncNow` at this call volume. Deviation from spec §4 noted here deliberately; spec's intent was "refresh cheaply", which SyncNow already is at ~3 calls.)

View sections, in order (plain tables, same Bootstrap classes as `HoldedAccounts.cshtml`): connection card (usage meter + `ApiKeyConfigured` warning when false), sync states table with the two buttons, calls-by-month table, department actuals (629*) table, full account list table (number, name, group, Holded balance, local balance, line count, ✓/✗), totals footer. Every decimal via the culture the sibling views use.

- [ ] **Step 1:** implement controller actions + view + nav links.
- [ ] **Step 2:** `dotnet build Humans.slnx -v quiet`; run the site (`dotnet run --project src/Humans.Web`) and load `/Finance/Holded` with `HOLDED_API_KEY` set from the dev token — verify: usage card shows period `2026-08`, account list shows 267 rows, 629 table led by `62900000 Otros servicios 133,416.45`, Sabadell `57200001` shows the known reconcile mismatch, account `40000004` local balance `−53,203.00` reconciled ✓. **Read-only key: SyncNow/FullSync buttons will fail on any write path — do not exercise the purchase-doc sync against live; ledger sync is read-only and safe to click.**
- [ ] **Step 3:** commit `feat(holded): /Finance/Holded admin screen`, push.

### Task 8: v1 removal sweep, docs, final verification

**Files:**
- Verify-deleted/Modify: any remaining `invoicing/v1`/`accounting/v1`/`dailyledger`/`key` header references
- Modify: `docs/sections/Holded.md` (connector doc — endpoints, auth, metering), `src/Sections/Humans.Finance/Docs/2026-06-15-holded-ledger-single-source-design.md` (add a superseded-by note pointing at the v2 spec), `docs/architecture/data-model.md` (new tables) if it lists Finance tables
- Test: full suite

- [ ] **Step 1:** `grep -rn "invoicing/v1\|accounting/v1\|dailyledger\|starttmp" src tests` → zero hits (fixtures included). Grep `Headers.Add("key"` → zero.
- [ ] **Step 2:** docs updates above; spec's §"Decisions" stays authoritative — do not restate, link.
- [ ] **Step 3:** `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet` — all green, including `HoldedArchitectureTests` and `FinanceArchitectureTests`.
- [ ] **Step 4:** commit `chore(holded): remove v1 remnants, update docs`, push. Open the PR to `origin/main` titled `feat: Holded API v2 migration — full ledger mirror, reconciliation, /Finance/Holded admin screen`, body linking the spec file and summarizing per task; end with the standard generated-with footer.

---

## Self-review notes (spec → task map)

- v2 client / Bearer / 429 / metering → Tasks 1–3. Cursor pager → Task 2. DD/MM/YYYY → Task 2. Required `contact_id` → Task 3.
- Full mirror + backfill + 7-day incremental + delete-in-window → Tasks 4 (ReplaceLedgerWindow) + 5. Reconcile + targeted re-pull + report-don't-fail → Task 5 (cap 10/run).
- `holded_accounts` / `holded_api_calls` / per-kind sync state / single migration / no data migration → Task 4.
- Admin screen incl. 629 actuals, usage meter, calls-by-month, sync buttons → Tasks 6–7 (Refresh-accounts button folded into SyncNow — recorded deviation).
- v1 deleted same PR → Tasks 2–3 (methods) + 8 (sweep). Webhooks / bank-reconciliation panel / Pleo → explicitly not in this plan (spec: deferred/follow-up).
