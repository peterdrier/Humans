# Holded API v2 Migration + Holded Section Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the v1 Holded client with v2, carve a new `Humans.Holded` vertical section owning a full all-accounts ledger mirror with balance reconciliation, and ship the `/Holded` admin screen.

**Architecture:** The connector (`IHoldedClient` in Humans.Application, `HoldedClient` in Humans.Infrastructure) is rewritten endpoint-for-endpoint against `api/v2` and stays in Base. A new G5 section `src/Sections/Humans.Holded` (+ `Humans.Holded.Contracts`) takes over the mirror tables (`holded_ledger_lines` moves out of Finance; `holded_accounts`, `holded_api_calls`, kind-keyed `holded_sync_states` are new), the sync + reconciliation, and the admin screen. Finance keeps business meaning (category map, expense-doc matching, creditor bindings) and reads ledger data via `IHoldedService`. v1 code is deleted in the same PR.

**Tech Stack:** ASP.NET Core 10 MVC, EF Core (Postgres; EF-InMemory in tests), NodaTime, Hangfire, xunit + AwesomeAssertions (`HumansFact`, `StubHandler`).

**Spec:** `src/Sections/Humans.Finance/Docs/2026-08-10-holded-v2-migration-design.md` (moves into the new section in Task 9). API shapes there are live-probed.

## Global Constraints

- Branch/worktree: `.worktrees/holded-v2` (exists, branch `holded-v2`, rebased on origin/main @ 1d334ce77). Never commit in the main checkout. Push after every task.
- Build/test: `dotnet build Humans.slnx -v quiet` / `dotnet test Humans.slnx -v quiet` (`-v quiet` mandatory), from the worktree root.
- Layering (peters-hard-rules.md): DbContext → Repository → Service → Controller; only a section's own Repository touches its DbContext; cross-section calls at the service layer via the Contracts interface. `HoldedClient` never touches the DB.
- Post-#1240 convention: Contracts leaf = the public surface, referencing `Humans.Interfaces` **only** (never Base); everything without an external consumer stays `internal`. No `I<Section>ServiceRead` interfaces.
- EF: never hand-edit migrations (the sanctioned exception per #1240: the `namespace`/`using` line when moving existing migration files between projects); schema-only, **no data migrations** (moved tables are mirrors — drop, recreate, resync refills). One migration per context for the whole PR.
- Preserve #1241 semantics in the new sync: non-blocking `SemaphoreSlim` sweep gate (skip + report, single-server), trailing-window nightly anchored on *now* (accounting-date filter ⇒ never anchor on the newest cached line), full sweep on cold cache or explicit request. Creditor block is `40000000–40000999`.
- v2 API facts (live-probed): base `https://api.holded.com/api/v2`, `Authorization: Bearer <key>`, snake_case JSON, decimals as **strings** (`"121.00"`), cursor pagination `{items:[], cursor, has_more}` with `limit` max 200, **ledger-entries dates are `DD/MM/YYYY`**, purchases dates are ISO `YYYY-MM-DD`, 429 carries `Retry-After` seconds + `X-RateLimit-Remaining`/`X-RateLimit-Window`.
- Full OpenAPI spec: `https://api.holded.com/openapi/api2.json` — the authority for any field not covered here.
- Read-only live probing allowed via the token in `C:\Users\PeterDrier\.holded\dev-token` (a handful of calls); write endpoints are fixture-tested only.
- Config unchanged: `HOLDED_API_KEY` env var; jobs/pages no-op cleanly when unset.

---

### Task 1: v2 transport — Bearer auth, 429 handling, call metering

**Files:**
- Create: `src/Humans.Application/Interfaces/Holded/IHoldedCallLog.cs`
- Create: `src/Humans.Infrastructure/Services/Holded/HoldedCallLog.cs`
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs` (`AttachAuth`, `SendAsync`, constructor)
- Modify: `src/Humans.Web/Extensions/Sections/HoldedConnectorExtensions.cs` (register singleton)
- Test: `tests/Humans.Application.Tests/Services/Holded/HoldedClientTransportTests.cs` (new); update `Make(...)` in the three existing `HoldedClient*Tests.cs`

**Interfaces (Produces):**

```csharp
// Humans.Application.Interfaces.Holded
public sealed record HoldedApiCallRecord(
    Instant CalledAt, string Endpoint, string Method, int StatusCode,
    int? RateLimitRemaining, string? RateLimitWindow);

/// <summary>In-memory buffer of Holded API calls. The client appends; the Holded section's service
/// drains to holded_api_calls. Singleton; loses at most the unflushed tail on crash (GET /usage is
/// the authoritative counter).</summary>
public interface IHoldedCallLog
{
    void Record(HoldedApiCallRecord record);
    IReadOnlyList<HoldedApiCallRecord> DrainAll();
}
```

`HoldedCallLog` wraps a `ConcurrentQueue<HoldedApiCallRecord>`; `DrainAll` dequeues until empty.

- [ ] **Step 1: failing tests** — `HoldedClientTransportTests.cs`, using the `StubHandler`/`Make` pattern from `HoldedClientReadTests.cs` (`Make` gains `HoldedCallLog` + `NodaTime.Testing.FakeClock` args):
  - `Sends_bearer_authorization_header`: `req.Headers.Authorization` scheme `Bearer`, parameter `test-key`; legacy `key` header absent.
  - `Retries_once_on_429_honoring_retry_after`: 429 with `Retry-After: 0`, then 200 → success, 2 requests observed.
  - `Throws_transient_when_429_persists`: two 429s → `HoldedTransientException`.
  - `Records_call_in_log`: after one call, `log.DrainAll()` yields one record: `Endpoint` == calling method name, `StatusCode` 200, `RateLimitRemaining`/`RateLimitWindow` parsed from stubbed `X-RateLimit-Remaining: 42` / `X-RateLimit-Window: minute`.
- [ ] **Step 2:** run new tests, verify FAIL.
- [ ] **Step 3: implement.** `AttachAuth` → `req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);`. `SendAsync`: record every response into the log (`caller` is already a `[CallerMemberName]` param — that string is the `Endpoint`; parse rate-limit headers defensively → null when absent). 429 handling: for content-free requests (GETs), read `Retry-After` (cap wait 60 s, default 5 s), delay, retry once; content-bearing requests and a second 429 → `HoldedTransientException` naming the Retry-After. Constructor gains `IHoldedCallLog callLog, IClock clock`. Register `services.AddSingleton<IHoldedCallLog, HoldedCallLog>();` in `HoldedConnectorExtensions`.
- [ ] **Step 4:** build; Holded client test files green (existing tests updated for the new ctor).
- [ ] **Step 5:** commit `feat(holded): v2 bearer transport with 429 handling and call metering`, push.

### Task 2: v2 read endpoints — ledger-entries, accounting-accounts, usage

**Files:**
- Modify: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`, `HoldedReadDtos.cs`
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
- Modify (call-site shim only): `src/Sections/Humans.Finance/Services/Service.cs`
- Test: `tests/Humans.Application.Tests/Services/Holded/HoldedClientReadTests.cs`

**Interfaces (Produces):**

```csharp
// IHoldedClient — replaces ListDailyLedgerAsync:
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
- `GET /api/v2/ledger-entries?start_date=YYYY-MM-DD&end_date=YYYY-MM-DD[&account=N][&limit=200][&cursor=…]` → `{items:[{entry_number:2064, line:2, date:"09/02/2026", type:"payment", description:"", doc_description:"", account:40000004, debit:"0.00", credit:"1200.00", tags:[], checked:false}], cursor, has_more}`. Parse `date` with `LocalDatePattern.CreateWithInvariantCulture("dd/MM/yyyy")` → `AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant()`; `debit`/`credit` via `decimal.Parse(s, CultureInfo.InvariantCulture)`.
- `GET /api/v2/accounting-accounts` → `{items:[{id, color, number:10000000, name, description, group, debit:"0.00", credit:"0.00", balance:"0.00", archived:false, non_deductible:false}]}` (267 accounts, unpaginated today; still tolerate `cursor`/`has_more`).
- `GET /api/v2/usage` → `{type:"automation_token", period:"2026-08", usage:36, limit:2000000, count:1, secondary_usages:{"api_v1_legacy_1":35}, user_usages:[], next_plan:null, next_limit:null}`.

Add one private cursor-pager reused by every v2 list endpoint:

```csharp
/// <summary>Walks a v2 cursor-paginated collection: follows `cursor` while `has_more`, collecting
/// `items` elements. THROWS HoldedTransientException when the pageSafetyCap is hit — a truncated
/// list must never be returned: ledger results feed replace-semantics reconciliation, where a
/// short fetch would delete rows that still exist in Holded (destructive, not just lossy).</summary>
private async Task<List<JsonNode>> GetPagedAsync(string pathAndQuery, int pageSafetyCap, CancellationToken ct)
```

- [ ] **Step 1: failing tests** (canned v2 JSON exactly as above):
  - `ListLedgerEntries_parses_ddMMyyyy_dates_and_string_decimals` (`"09/02/2026"` → Feb 9 Madrid midnight Instant; `Credit == 1200.00m`; `AccountNum == 40000004`).
  - `ListLedgerEntries_follows_cursor_until_has_more_false` (2 pages; second request query contains `cursor=c1`).
  - `ListLedgerEntries_throws_when_page_cap_hit` (stub always answers `has_more:true` → `HoldedTransientException`, nothing returned).
  - `ListLedgerEntries_passes_account_filter` (`account=40000004` in query).
  - `ListAccountingAccounts_parses_totals`.
  - `GetUsage_parses_period_usage_limit_and_secondary`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement; delete `ListDailyLedgerAsync`. Shim the two Finance call sites (`BackfillLedgerAsync` window loop, `SyncCreditorLedgerAsync` trailing window) to `ListLedgerEntriesAsync(fromInstant.InZone(MadridZone).Date, toInstant.InZone(MadridZone).Date)` — behavior-preserving; the real sync rewrite is Task 6.
- [ ] **Step 4:** build + **full suite** green.
- [ ] **Step 5:** commit `feat(holded): v2 read endpoints — ledger-entries, accounting-accounts, usage`, push.

### Task 3: v2 purchases, contacts, expenses-accounts + approval flag

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Holded/HoldedClient.cs`
- Modify: `src/Humans.Application/Interfaces/Holded/IHoldedClient.cs`, `HoldedReadDtos.cs`, `HoldedPurchaseDocumentDto.cs`
- Modify: `src/Sections/Humans.Finance/Domain/HoldedExpenseDoc.cs`, `Data/Configurations/HoldedExpenseDocConfiguration.cs` (`ApprovedAt` → `IsApproved`; the migration lands in Task 5)
- Modify: `src/Sections/Humans.Finance/Services/Service.cs` (`SyncAsync` paging, `MapDoc`, `GetActualsForYearAsync`)
- Modify: `src/Sections/Humans.Expenses/Services/ExpenseReportService.cs` call sites (compiler-driven; `ContactId` becomes required)
- Test: `HoldedClientTests.cs`, `HoldedClientContactTests.cs`, `HoldedClientReadTests.cs`

**Interfaces (Produces / changes):**

```csharp
// IHoldedClient — replaces ListPurchaseDocumentsPageAsync(page, limit):
Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(CancellationToken ct = default);
/// <summary>Ids of purchases still in draft — GET /purchases?approval_status=draft.</summary>
Task<IReadOnlySet<string>> ListDraftPurchaseIdsAsync(CancellationToken ct = default);
// HoldedPurchaseDocListItemDto: ApprovedAt removed (v2 list has no approval timestamp).
// HoldedPurchaseDocumentDto (single GET): ApprovedAt stays (v2 single GET has approved_at).
// HoldedPurchaseDocumentInput.ContactId: `required string` (v2 POST requires contact_id).
// HoldedExpenseDoc: `Instant? ApprovedAt` → `bool IsApproved`.
```

**v2 wire shapes:**
- `POST /api/v2/purchases` `{contact_id, contact_name, date:"2026-05-14", description, items:[{name, units:1, price, account}]}` → 201 `{id}`. **`items[].account` = the mapped 629 expense-account id** (Holded id string; `holded_category_map.HoldedAccountId`) — the doc is booked to the right department from birth. **No tags are written** (Peter, 2026-08-10: tags were a v1 workaround from before double-entry was understood; the account IS the category). `HoldedPurchaseDocumentLineInput` gains `AccountId`; tag-input properties with zero remaining writers are deleted.
- **There is no tag/doc update call in v2** (confirmed against the OpenAPI spec: PUT is full-replacement, no tags field; no assignment endpoint). `UpdatePurchaseDocumentTagsAsync` is DELETED; the `UpdateIncomingDocTag` outbox handler completes such events with an informational log (keep the enum member — prod may hold queued rows that must drain, not poison). Recategorize-after-push is now done inside Holded (reclassify the line); the ledger mirror + reconciliation pull the correction back automatically.
- `POST /api/v2/purchases/{id}/attachments` — multipart, part name `file`.
- `GET /api/v2/purchases` (cursor) items: `{id, document_number, contact_id, contact_name, date:"2024-01-15", subtotal:"100.00", tax:"21.00", total:"121.00", currency:"EUR", status, tags:[], lines:[{price:"100.00", units:1, account, …}], payments_total, payments_pending}`. `lines[].account`: **probe its runtime type once with the dev token** — v1 sent the account id string; `HoldedMatchEntry` carries both `HoldedAccountId` and `HoldedAccountNumber`, so map whichever arrives (integer → resolve to the mapped id by number before `HoldedMatcher.Match`).
- `GET /api/v2/purchases/{id}`: has `approved_at` (ISO), `draft`, `payments_total`, `payments_pending` → keep `HoldedPurchaseDocumentDto` shape, parse `approved_at` → Instant?.
- `GET/POST /api/v2/expenses-accounts`: `{items:[{id, name, account_num:6290001, archived}]}` / POST `{name, account_num}` → `{id}`.
- `GET /api/v2/contacts` (cursor): items carry `{id, custom_id, name, trade_name, type, iban, email, phone, mobile, code, bill_address, supplier_record:{num, name}}` → `HoldedContactDto.SupplierAccountNum = supplier_record.num`, and the DTO **gains contact-info fields** for the creditor-statement header (Task 8b): `TradeName`, `Email`, `Phone`, `Mobile`, `Iban`, `TaxCode` (`code`), `Address` (one display string assembled from `bill_address`'s parts — check the OpenAPI spec for its exact property names and join the non-empty ones). All nullable; parse defensively via `Prop`. `POST/PUT /api/v2/contacts` `{name, trade_name, custom_id, type, iban}` → `{id}`.

- [ ] **Step 1: failing tests:** rewrite affected cases to v2 fixtures — purchase create posts snake_case to `/api/v2/purchases` (assert `contact_id` + ISO date in captured body); list parses string decimals + ISO dates; draft-ids call sends `approval_status=draft`; contact parse reads `supplier_record.num`; expense-account create posts `{name, account_num}`; attachment posts multipart to `/attachments`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement client; `SyncAsync` → `ListPurchaseDocumentsAsync` + `ListDraftPurchaseIdsAsync`, `MapDoc(doc, entries, draftIds, now)` sets `IsApproved = !draftIds.Contains(doc.Id)`; `GetActualsForYearAsync` filters `d.IsApproved`; fix `ExpenseReportService` compile errors (it already passes the contact id from `EnsureCreditorContactAsync`).
- [ ] **Step 4:** build + full suite green (EF-InMemory picks the model change up without a migration).
- [ ] **Step 5:** commit `feat(holded): v2 purchases, contacts and expenses-accounts endpoints`, push.

### Task 4: scaffold the Humans.Holded section

**Files:**
- Create: `src/Sections/Humans.Holded.Contracts/Humans.Holded.Contracts.csproj`, `IHoldedService.cs`, `HoldedLedgerLineInfo.cs`
- Create: `src/Sections/Humans.Holded/Humans.Holded.csproj`, `Section.cs`, `Properties/AssemblyInfo.cs` if Finance has one
- Create: `src/Sections/Humans.Holded/Domain/`: `HoldedLedgerLine.cs` (moved from Finance, namespace `Humans.Holded.Domain`), `HoldedSyncStatus.cs` (moved), `HoldedSyncKind.cs`, `HoldedSyncState.cs`, `HoldedAccount.cs`, `HoldedApiCall.cs`
- Create: `src/Sections/Humans.Holded/Data/`: `HoldedDbContext.cs`, `HoldedDbContextFactory.cs` (copy Finance's factory shape), `IHoldedMirrorRepository.cs`, `Repository.cs`, `Configurations/` (4 files), `Data/Migrations/` (generated)
- Create: `tests/Humans.Holded.Tests/Humans.Holded.Tests.csproj` (copy `tests/Humans.Finance.Tests` csproj shape), `HoldedRepositoryTests.cs`
- Modify: `Humans.slnx` (three `<Project Path=…/>` entries beside Finance's)

**Interfaces (Produces):**

```csharp
// Humans.Holded.Contracts (references Humans.Interfaces ONLY):
public sealed record HoldedLedgerLineInfo(
    int EntryNumber, int Line, int AccountNum, Instant Date,
    string? Type, string? Description, decimal Debit, decimal Credit);

public interface IHoldedService : IApplicationService
{
    /// <summary>Cached journal lines for one account. Zero Holded calls.</summary>
    Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(int accountNum, CancellationToken ct = default);
    /// <summary>Per-account balance (Σdebit − Σcredit) for every account with cached lines,
    /// optionally restricted to one calendar year (Madrid zone) — the year form feeds Finance's
    /// ledger-derived actuals.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(int? calendarYear = null, CancellationToken ct = default);
    /// <summary>Ledger mirror sync; false when a sweep was already running and this one was skipped.
    /// full=false: trailing 364-day window anchored on now. full=true / cold cache: inception → today.
    /// Both refresh the accounts cache, reconcile per-account totals with targeted re-pulls, and
    /// drain the API call log.</summary>
    Task<bool> SyncLedgerAsync(bool full = false, CancellationToken ct = default);
}

// Humans.Holded.Data (internal):
internal enum HoldedSyncKind { Ledger, Accounts, FullSync }
internal sealed class HoldedSyncState
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
    public int Number { get; init; }              // PK — the literal chart number
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

[Section("Holded")]
internal interface IHoldedMirrorRepository : IRepository
{
    /// <summary>Upserts the fetched window on (EntryNumber, Line) and deletes local rows inside
    /// [from,to] (optionally one account) absent from rows — the fetch is the truth for its window.
    /// An EMPTY rows list is a valid sweep result and deletes everything cached in the window; do
    /// NOT early-return on rows.Count == 0 (the append-only early return is the bug being fixed:
    /// deleted/reclassified Holded lines lingered forever — e.g. a phantom €23 debit on 40000004).</summary>
    Task ReplaceLedgerWindowAsync(Instant from, Instant to, int? accountNum,
        IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByAccountNumAsync(int accountNum, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedLedgerLine>> GetAllLedgerLinesAsync(CancellationToken ct = default);
    Task<bool> HasAnyLedgerLinesAsync(CancellationToken ct = default);
    Task UpsertAccountsAsync(IReadOnlyList<HoldedAccount> rows, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedAccount>> GetAccountsAsync(CancellationToken ct = default);
    Task AddApiCallsAsync(IReadOnlyList<HoldedApiCall> rows, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedApiCall>> GetApiCallsAsync(CancellationToken ct = default);
    Task<HoldedSyncState> GetOrCreateSyncStateAsync(HoldedSyncKind kind, CancellationToken ct = default);
    Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedSyncState>> GetSyncStatesAsync(CancellationToken ct = default);
}
```

Section registration (`Section.cs`, copying Finance's): `services.AddSectionDbContext<HoldedDbContext>(sentinelTable: "holded_ledger_lines"); services.AddScoped<IHoldedMirrorRepository, Repository>();` (service registration lands in Task 6). No GDPR contributor — no user-scoped tables. `HoldedDbContext` mirrors Finance's shape: internal sealed, explicit `ApplyConfiguration` calls, history table `__EFMigrationsHistory_Holded` (whatever mechanism `AddSectionDbContext`/the factory uses for Finance — copy it exactly). Configs: `holded_ledger_lines` identical to Finance's current one (unique `(EntryNumber, Line)`, index `AccountNum`, `decimal(12,2)`); `holded_sync_states` keyed on `Kind` (`HasConversion<string>().HasMaxLength(16)` both enums, **no `HasData`**); `holded_accounts` keyed on `Number`, decimals `decimal(14,2)`; `holded_api_calls` keyed on `Id`, max lengths `Endpoint` 64 / `Method` 8 / `RateLimitWindow` 16, index on `CalledAt`.

**Table-name collision guard:** Finance still maps `holded_ledger_lines`/`holded_sync_states` until Task 5. Both migrations run in the deploy in solution order — Finance's drop must land with/before Holded's create in the same PR; that is Task 5's migration. Within this task, do **not** yet generate the Holded migration if `dotnet ef` refuses duplicate table mappings across contexts (it won't — contexts are independent); generate it here, and CI's `has-pending-model-changes` check runs per context.

- [ ] **Step 1: failing tests** — `HoldedRepositoryTests.cs` (EF-InMemory, fixture style copied from `tests/Humans.Finance.Tests/ServiceTests.cs`):
  - `GetOrCreateSyncState_creates_then_returns_same_row`.
  - `ReplaceLedgerWindow_upserts_and_deletes_missing`: seed A(entry 1/1, in window), B(2/1, in window), C(3/1, outside); replace with A′(changed credit) + D → A updated, B gone, C untouched, D inserted.
  - `ReplaceLedgerWindow_scoped_to_account_leaves_other_accounts`.
  - `ReplaceLedgerWindow_empty_fetch_deletes_everything_in_window` (rows: `[]` → in-window rows gone, out-of-window rows survive).
  - `UpsertAccounts_replaces_totals`.
- [ ] **Step 2:** run, verify FAIL (missing projects — wire csproj/slnx first so the failure is assertions, not compile).
- [ ] **Step 3:** implement (entities, configs, DbContext, factory, repository — `ReplaceLedgerWindowAsync` follows Finance's `UpsertLedgerLinesAsync` dictionary pattern plus the window delete).
- [ ] **Step 4:** generate the Holded migration:
  `dotnet ef migrations add InitialHoldedSection --project src/Sections/Humans.Holded --startup-project src/Humans.Web --context HoldedDbContext --output-dir Data/Migrations`
  Review: creates exactly the four tables + `__EFMigrationsHistory_Holded`. No hand edits.
- [ ] **Step 5:** build + full suite green.
- [ ] **Step 6:** commit `feat(holded): scaffold Humans.Holded section — mirror tables, repository, migration`, push.

### Task 5: Finance sheds the mirror — table moves + IsApproved migration

**Files:**
- Delete: `src/Sections/Humans.Finance/Domain/HoldedLedgerLine.cs`, `HoldedSyncState.cs`, `HoldedSyncStatus.cs` (moved in Task 4), `Data/Configurations/HoldedLedgerLineConfiguration.cs`, `HoldedSyncStateConfiguration.cs`
- Create: `src/Sections/Humans.Finance/Domain/HoldedDocSyncState.cs` + `Data/Configurations/HoldedDocSyncStateConfiguration.cs`
- Modify: `src/Sections/Humans.Finance/Data/FinanceDbContext.cs`, `IHoldedRepository.cs`, `Repository.cs`, `Services/Service.cs`
- Create (generated): `src/Sections/Humans.Finance/Data/Migrations/*_HoldedMirrorMovesToHoldedSection.cs`
- Test: `tests/Humans.Finance.Tests/ServiceTests.cs` (compile fixes)

**Interfaces (Produces):**

```csharp
// Finance Domain — replaces the moved HoldedSyncState for the docs sync only:
internal sealed class HoldedDocSyncState
{
    public int Id { get; init; } = 1;                    // singleton, lazy-created (no HasData)
    public Instant? LastSyncAt { get; set; }
    public string Status { get; set; } = "Idle";         // "Idle" | "Running" | "Error"
    public string? LastError { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
// IHoldedRepository: ledger + sync-state members removed
//   (UpsertLedgerLinesAsync, GetLedgerLinesByAccountNumAsync, GetAllLedgerLinesAsync,
//    HasAnyLedgerLinesAsync, GetSyncStateAsync, SaveSyncStateAsync); added:
Task<HoldedDocSyncState> GetOrCreateDocSyncStateAsync(CancellationToken ct = default);
Task SaveDocSyncStateAsync(HoldedDocSyncState state, CancellationToken ct = default);
```

Table: `holded_doc_sync_state`, `Status` max 16, `LastError` max 2000. `Service.SyncAsync` swaps to the new state pair (string status values as above — the enum moved out with the mirror). **Temporary compile state:** `SyncCreditorLedgerAsync`, `GetCreditorStatusAsync`, `GetCreditorLedgerAsync`, `ListCreditorAccountsAsync` lose their repo ledger reads in Task 6 — within this task, keep the build green by moving those methods onto `IHoldedService` stubs is premature; instead do Tasks 5 and 6 as **one commit train**: complete both, then run the suite and commit Task 5's migration + Task 6's rewiring separately only if green independently; otherwise a single commit `refactor(holded): Finance sheds the ledger mirror to the Holded section` is acceptable.

- [ ] **Step 1:** make the Finance-side code changes above (delete moved types, add doc-sync state, strip repo).
- [ ] **Step 2:** generate the Finance migration:
  `dotnet ef migrations add HoldedMirrorMovesToHoldedSection --project src/Sections/Humans.Finance --startup-project src/Humans.Web --context FinanceDbContext --output-dir Data/Migrations`
  Review: drops `holded_ledger_lines` + `holded_sync_states` (mirror data — refilled by first sync; acceptable by design), drops `ApprovedAt` + adds `IsApproved` on `holded_expense_docs`, creates `holded_doc_sync_state`. **No data SQL, no hand edits.**
- [ ] **Step 3:** proceed to Task 6 before expecting green (see note above).

### Task 6: the Holded section service — sync, reconcile; Finance reads via IHoldedService

**Files:**
- Create: `src/Sections/Humans.Holded/Services/Service.cs`
- Modify: `src/Sections/Humans.Holded/Section.cs` (register `Service` + `IHoldedService`)
- Modify: `src/Sections/Humans.Finance/Services/Service.cs` (creditor reads via `IHoldedService`; delete `SyncCreditorLedgerAsync`, `BackfillLedgerAsync`, `IncrementalLedgerAsync`, `LedgerSyncGate`)
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceService.cs` (remove `SyncCreditorLedgerAsync`)
- Modify: `src/Sections/Humans.Finance/Controllers/FinanceController.cs` (delete `ResyncCreditorLedger` — relocates to `/Holded` in Task 8)
- Modify: `src/Humans.Infrastructure/Jobs/HoldedSyncJob.cs` (call both services)
- Modify: `src/Sections/Humans.Finance/Views/Finance/Creditors.cshtml` (drop the Resync button/form)
- Modify: `src/Sections/Humans.Holded/Humans.Holded.csproj` + `Humans.Web` references if the job's DI needs them (job consumes `IHoldedService` from Contracts — Infrastructure references `Humans.Holded.Contracts` the same way it references `Humans.Finance.Contracts` for `IHoldedFinanceService`)
- Test: `tests/Humans.Holded.Tests/HoldedLedgerSyncTests.cs` (new), `tests/Humans.Finance.Tests/ServiceTests.cs` (rewire creditor-read tests onto a fake `IHoldedService`), `tests/Humans.Application.Tests/Jobs/HoldedSyncJobTests.cs`

**Interfaces (Consumes):** Task 4's `IHoldedMirrorRepository` + `IHoldedService`; Task 2's client methods.

Holded `Service` (internal sealed, `IHoldedService`; ctor `IHoldedMirrorRepository repo, IHoldedClient client, IHoldedCallLog callLog, IClock clock, ILogger<Service> logger`):

```csharp
private static readonly LocalDate LedgerInception = new(2020, 1, 1);
// 45 days, not #1241's 364: the API's real free budget is the plan tier (~2,000 calls/month on
// Basic; the 2M "limit" from GET /usage is a billable-overage ceiling). A year-wide window costs
// ~20 pages/night; 45 days costs 1–2. Backdating/deletion OLDER than the window is caught by the
// balance reconciliation below at one call/night, which triggers a targeted per-account re-pull
// only when an account actually drifted — correctness no longer depends on window width.
private static readonly Duration TrailingWindow = Duration.FromDays(45);
private const int MaxTargetedRepullsPerRun = 10;
private static readonly SemaphoreSlim LedgerSyncGate = new(1, 1);   // #1241 semantics: WaitAsync(0), skip + report
```

`SyncLedgerAsync(full, ct)` algorithm:
1. `LedgerSyncGate.WaitAsync(0)` — false → log + return false.
2. State row: `full` → `FullSync` kind, else `Ledger`; set Running (same try/catch persist-error shape as Finance's current `SyncAsync`).
3. Window: `full || !repo.HasAnyLedgerLinesAsync()` → `[LedgerInception, today]`, else `[today − 364d, today]` (today = clock in Madrid zone, LocalDates). Fetch `client.ListLedgerEntriesAsync(from, to)`; map to entity (all accounts — no filter); `repo.ReplaceLedgerWindowAsync(fromInstant, toInstant, null, lines, now)`.
4. Accounts refresh: `client.ListAccountingAccountsAsync()` → `repo.UpsertAccountsAsync`; update `Accounts` state.
5. Reconcile: local sums from `repo.GetAllLedgerLinesAsync()` grouped by account; for each non-archived Holded account where `Balance != Σdebit − Σcredit` (missing group = 0): targeted `client.ListLedgerEntriesAsync(LedgerInception, today, account.Number)` + `ReplaceLedgerWindowAsync(…, account.Number, …)`, recompute; still off → collect. Cap `MaxTargetedRepullsPerRun`, log when capped.
6. Residual mismatches → state `LastError` = `"57200001: holded 418840.54, local 771074.85; …"` (null when none — a standing known mismatch is reportable state, never a throw); `LastCount` = lines upserted; state Idle.
7. Drain `callLog.DrainAll()` → `repo.AddApiCallsAsync`. Release gate in `finally`. Return true.

Finance `Service` rewiring (ctor gains `IHoldedService holded`, drops nothing else):
- `GetCreditorStatusAsync`: `repo.GetLedgerLinesByAccountNumAsync(num)` → `holded.GetLedgerLinesAsync(num)`; derivations unchanged (post-#1241 shape — no `Payments` list).
- `GetCreditorLedgerAsync`: same swap; map `HoldedLedgerLineInfo` → `CreditorLedgerLine` (existing Finance.Contracts type).
- `ListCreditorAccountsAsync`: `repo.GetAllLedgerLinesAsync()` grouping → `holded.GetAccountBalancesAsync()` filtered to the creditor block (`CreditorAccountMin/Max` stay in Finance).
- **`GetActualsForYearAsync` becomes ledger-derived** (the tag-era doc-matching path was guesswork): for each active `holded_category_map` row, actual = `holded.GetAccountBalancesAsync(calendarYear)` value for its `HoldedAccountNumber` (missing → 0). `HoldedActualRow.DocCount` loses meaning — keep the record shape, pass 0, and note it for a later contracts cleanup (or drop the field now if all consumers are in-solution and trivial to fix — prefer dropping). Doc-matching (`MapDoc`/`HoldedMatcher`) survives ONLY to power the `/Finance/HoldedUnmatched` queue for legacy tag-era docs and catch-all bookings.
- `HoldedSyncJob`: `await finance.SyncAsync(ct); await holded.SyncLedgerAsync(full: false, ct);` (ctor gains `IHoldedService`).

- [ ] **Step 1: failing tests** (`HoldedLedgerSyncTests.cs`: fake `IHoldedClient` + real `Repository` over EF-InMemory + `FakeClock`):
  - `Cold_cache_sweeps_from_inception` (client sees `from == 2020-01-01`; all accounts cached — 40000004 and 57200001 both present).
  - `Warm_cache_sweeps_trailing_window` (`from == today − 45 days`).
  - `Full_flag_forces_inception_sweep_and_replaces_deleted_lines`.
  - `Reconcile_repulls_mismatched_account_and_reports_residual` (one account off even after re-pull → in `LastError`; matching account → no targeted call).
  - `Second_concurrent_sweep_skips` (`WaitAsync(0)` path → returns false).
  - `Drains_call_log_into_repo`.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement Holded service + Finance rewiring + job + view/controller deletions.
- [ ] **Step 4:** `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet` — full suite green; grep `SyncCreditorLedgerAsync` → zero hits.
- [ ] **Step 5:** commit (with Task 5's changes if they weren't independently green): `refactor(holded): Holded section owns the ledger mirror; sync with balance reconciliation`, push.

### Task 7: admin overview read model

**Files:**
- Create: `src/Sections/Humans.Holded/Services/IHoldedAdminService.cs` (internal), overview types in `src/Sections/Humans.Holded/Models/HoldedAdminModels.cs` (internal)
- Modify: `src/Sections/Humans.Holded/Services/Service.cs`, `Section.cs` (register)
- Modify: `src/Sections/Humans.Finance.Contracts/IHoldedFinanceService.cs` + `HoldedDtos.cs` (one addition, below)
- Modify: `src/Sections/Humans.Finance/Services/Service.cs` (implement it)
- Test: `tests/Humans.Holded.Tests/HoldedAdminOverviewTests.cs`

**Interfaces (Produces):**

```csharp
// Finance.Contracts — the one cross-section read the screen needs from Finance:
public sealed record HoldedDocSyncInfo(Instant? LastSyncAt, string Status, string? LastError, int LastSyncedDocCount);
// on IHoldedFinanceService:
Task<HoldedDocSyncInfo> GetDocSyncInfoAsync(CancellationToken ct = default);

// Humans.Holded internal:
internal interface IHoldedAdminService : IApplicationService
{
    Task<HoldedAdminOverview> GetOverviewAsync(CancellationToken ct = default);
}
internal sealed record HoldedAdminOverview(
    bool ApiReachable,
    HoldedUsageDto? Usage,                                   // null when the usage call fails / no key
    int MonthlyCallBudget,                                   // config Holded:MonthlyCallBudget, default 2000
    IReadOnlyList<HoldedMonthlyCalls> CallsByMonth,          // newest first
    IReadOnlyList<HoldedSyncStateRow> SyncStates,
    IReadOnlyList<HoldedAccountRow> Accounts,                // non-archived chart, by number
    IReadOnlyList<HoldedAccountRow> DepartmentActuals,       // the 629* slice, balance desc
    int LedgerLineCount);
internal sealed record HoldedMonthlyCalls(int Year, int Month, int Total, IReadOnlyDictionary<string, int> ByEndpoint);
internal sealed record HoldedSyncStateRow(string Kind, string Status, Instant? LastSyncAt, string? LastError, int LastCount);
internal sealed record HoldedAccountRow(int Number, string Name, string? Group,
    decimal HoldedBalance, decimal? LocalBalance, int LocalLineCount, bool Reconciled);
```

Assembly (on the Holded `Service`): drain call log to repo first (so this page-load's calls appear next load); `Usage` = `client.GetUsageAsync()` in try/catch (`HoldedTransientException`/`HoldedPermanentException` → null); `ApiReachable = Usage is not null`; `MonthlyCallBudget` from a section-owned options class bound in `Section.Register` (`configuration["Holded:MonthlyCallBudget"]`, default 2000 — the API's `limit` field is Holded's billable-overage ceiling, NOT the free allowance, so the screen budgets against this config value and merely displays the API number); `CallsByMonth` from `repo.GetApiCallsAsync()` grouped by Madrid-zone year/month with per-endpoint counts; `Accounts` = `repo.GetAccountsAsync()` (non-archived) joined with ledger sums (`LocalBalance` null when no lines; `Reconciled = HoldedBalance == (LocalBalance ?? 0m)`); `DepartmentActuals` = `Number is >= 62900000 and <= 62999999` ordered by `HoldedBalance` desc; `LedgerLineCount` from `GetAllLedgerLinesAsync().Count`. (Finance's doc-sync info + bindings count are fetched by the *controller* in Task 8 via `IHoldedFinanceService` — the Holded service never references Finance.)

- [ ] **Step 1: failing tests:** monthly grouping (two months → two buckets, newest first); 629 slice filter + ordering; `Reconciled` false on a mismatch; usage failure → `Usage` null, rest populated.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement (incl. the small Finance `GetDocSyncInfoAsync`).
- [ ] **Step 4:** build + suite green.
- [ ] **Step 5:** commit `feat(holded): admin overview read model`, push.

### Task 8: /Holded screen

**Files:**
- Create: `src/Sections/Humans.Holded/Controllers/HoldedController.cs`, `Views/Holded/Index.cshtml`, `Views/_ViewImports.cshtml` + `_ViewStart.cshtml` (copy Finance's)
- Modify: `src/Sections/Humans.Finance/Views/Finance/HoldedAccounts.cshtml`, `HoldedUnmatched.cshtml`, `Creditors.cshtml` (nav link to `/Holded`, matching their existing header-link markup)

```csharp
[Authorize(Policy = PolicyNames.FinanceAdminOrAdmin)]
[Route("Holded")]
internal sealed class HoldedController(
    IHoldedAdminService admin, IHoldedService holded,
    IHoldedFinanceService holdedFinance, ILogger<HoldedController> logger) : BaseController // whatever base Finance's controller uses
{
    [HttpGet("")]                                   // view model = (Overview, DocSync: await holdedFinance.GetDocSyncInfoAsync())
    [HttpPost("SyncNow")]  [ValidateAntiForgeryToken]  // holdedFinance.SyncAsync + holded.SyncLedgerAsync(false); report counts + skipped-sweep case
    [HttpPost("FullSync")] [ValidateAntiForgeryToken]  // holded.SyncLedgerAsync(true); report skip when gate busy
}
```

View sections in order (plain tables, Bootstrap classes as in `Finance/HoldedAccounts.cshtml`): connection card (usage meter, `ApiReachable` warning), sync states (incl. the Finance doc-sync row) + the two buttons, calls-by-month, department actuals (629*), full account list (number, name, group, Holded balance, local balance, line count, ✓/✗), ledger-line total footer.

- [ ] **Step 1:** implement controller + views + nav links; build green.
- [ ] **Step 2:** live check: `HOLDED_API_KEY=$(cat ~/.holded/dev-token) dotnet run --project src/Humans.Web`, load `/Holded` — usage card shows period `2026-08`; ~267 account rows; 629 table led by `62900000 Otros servicios 133,416.45`; `40000004` local balance `−53,203.00` ✓; Sabadell `57200001` shows the known mismatch (352,234.31 — entry #2412 excluded from chart totals). **Stale-row verification (the bug this PR fixes):** after the first full sync against a copy of the QA cache — or simply on the fresh mirror — account 40000004 must show exactly 6 rows (all credits); the cache's phantom €23 debit from 13 May and the two zero-debit-zero-credit "loan" rows dated 22 June must NOT survive (live v2 shows no such rows — they are cache-only artifacts that replace-semantics remove). **Read-only key: ledger sync buttons are safe; do not run the doc sync against live (SyncNow calls it — expect its write-free read path to succeed; if the purchase list read is all it does, it is safe).**
- [ ] **Step 3:** commit `feat(holded): /Holded admin screen`, push.

### Task 8b: GL account page + Creditors UX (Peter, 2026-08-10 evening)

**Files:**
- Modify: `src/Sections/Humans.Holded/Controllers/HoldedController.cs`, `Services/Service.cs`, `Services/IHoldedAdminService.cs`, `Models/HoldedAdminModels.cs`
- Create: `src/Sections/Humans.Holded/Views/Holded/Account.cshtml`
- Modify: `src/Sections/Humans.Finance/Views/Finance/Creditors.cshtml`, `CreditorStatement.cshtml`, `Controllers/FinanceController.cs`, `Services/Service.cs`, `src/Sections/Humans.Finance.Contracts/HoldedCreditorAdminDtos.cs`
- Test: `tests/Humans.Holded.Tests/HoldedAdminOverviewTests.cs` (statement cases), `tests/Humans.Finance.Tests/ServiceTests.cs`

**1. Generic GL account page — `/Holded/Accounts/{number}`** (any account: 629x department, 572x bank, 400x creditor):

```csharp
// IHoldedAdminService:
Task<HoldedAccountStatement?> GetAccountStatementAsync(int number, CancellationToken ct = default);
// Models:
internal sealed record HoldedAccountStatement(
    HoldedAccountRow Account,                       // reuses Task 7's row (Holded vs local balance, reconciled flag)
    IReadOnlyList<HoldedLedgerLineInfo> Lines);     // all cached lines, date then entry/line order
```

Null when the number is neither in `holded_accounts` nor has cached lines → controller 404s. View: header (number, name, group, Holded balance, local balance, reconciled ✓/✗ — **native Holded sign**, so the page always matches Holded's own UI) + lines table (date, entry/line, type, description, debit, credit). `HoldedController`: `[HttpGet("Accounts/{number:int}")]`.

**2. `/Finance/Creditors` list:** drop the Owed column; show one **inverted Balance** (`credit − debit`; +53,203 = owed to the person; negative = they owe us) — display-level inversion, derivations elsewhere unchanged. All columns sortable: first Grep the repo's views for an existing sortable-table pattern (`sortable`, `data-sort`, `th a[href*=sort]`) and copy it; if none exists, controller-side `?sort=col&dir=asc|desc` query params with toggling header links (hard rules: sorting is the controller's job). Default sort: account number.

**3. `/Finance/Creditors/{num}` statement:** balance shown inverted (same rule), plus a contact header rendered from the account's Holded contact — name, trade name, email, phone, mobile, IBAN, tax code, address; omit empty fields. Data path: Finance's cached contact list (`ListContactsOrEmptyAsync`) now carries these fields after Task 3's DTO extension; extend `HoldedCreditorLedger` (Finance.Contracts) with a nullable `HoldedContactInfo` record carrying them. Add a small link to the GL page (`/Holded/Accounts/{num}`) for the raw native-sign view.

- [ ] **Step 1: failing tests:** `GetAccountStatement_unknown_number_returns_null`; `GetAccountStatement_returns_header_and_ordered_lines`; Finance: statement carries contact info when the cached contact matches, null when absent.
- [ ] **Step 2:** run, verify FAIL.
- [ ] **Step 3:** implement all three surfaces.
- [ ] **Step 4:** build + full suite green; live check `/Holded/Accounts/62900128` (Toilets — 121,684.00 debit) and `/Finance/Creditors/40000004` (+53,203.00, contact header shows Peter D).
- [ ] **Step 5:** commit `feat(holded): GL account page; sortable Creditors with inverted balances and contact header`, push.

### Task 9: v1 sweep, docs, PR

- [ ] **Step 1:** `grep -rn "invoicing/v1\|accounting/v1\|dailyledger\|starttmp" src tests` → zero (fixtures included); `grep -rn 'Headers.Add("key"' src` → zero.
- [ ] **Step 2:** docs: move the spec to `src/Sections/Humans.Holded/Docs/2026-08-10-holded-v2-migration-design.md`; write `src/Sections/Humans.Holded/Docs/Holded.md` per `docs/sections/SECTION-TEMPLATE.md` (concepts: mirror/connector split; invariants: mirror is re-derivable, reconcile reports never fails; triggers; cross-section: Finance + job); update `docs/sections/_Index.md`, the old connector doc `docs/sections/Holded.md` (now points at both the connector and the section), `src/Sections/Humans.Finance/Docs/Finance.md` (ledger tables gone), add a superseded-by note to `2026-06-15-holded-ledger-single-source-design.md`, and `docs/architecture/data-model.md` if it lists Finance tables.
- [ ] **Step 3:** full `dotnet build` + `dotnet test` green, including `HoldedArchitectureTests` / `FinanceArchitectureTests` (new section may need the same per-section architecture-test wiring Finance has — copy `tests/Humans.Application.Tests/Architecture/` patterns if they enumerate sections).
- [ ] **Step 4:** commit `chore(holded): v1 removal sweep + section docs`, push. Open the PR to `origin/main`: title `feat: Holded API v2 + Humans.Holded section — full ledger mirror, reconciliation, /Holded admin screen`, body linking the spec + plan and summarizing per task, standard generated-with footer.

---

## Self-review notes (spec → task map)

- v2 transport/Bearer/429/metering → T1; reads → T2; writes + IsApproved → T3. DD/MM/YYYY → T2. Required `contact_id` → T3.
- Section carve: scaffold + tables + repo → T4; Finance sheds mirror + doc-sync state + migrations → T5; sync + reconcile + read rewiring + job + Resync relocation → T6.
- Admin: overview → T7; screen + buttons + doc-sync row → T8.
- v1 deletion → T2/T3 (methods) + T9 (sweep). Webhooks / bank-reconciliation panel / Pleo: excluded (spec: deferred/follow-up).
- #1241 preserved: sweep gate (T6), trailing-window-anchored-on-now (T6), widened creditor block constants stay in Finance (T6), `HasAnyLedgerLinesAsync` (T4 repo).
- Stale-line bug (Peter, 2026-08-10 evening) folded in: append-only cache + pre-upsert creditor filter left deleted/reclassified lines forever (confirmed: phantom €23 debit + two zero-amount rows on 40000004). Fixed structurally — full mirror has no pre-upsert filter (a reclassified line's account just updates on upsert), window-replace deletes what a sweep no longer returns including on an empty fetch (T4), truncated pagination hard-fails before it can drive deletions (T2), and balance reconciliation catches anything outside the swept window (T6). nobodies-collective/Humans#1019 (widened block's uncached history) is covered by the full-mirror backfill.
