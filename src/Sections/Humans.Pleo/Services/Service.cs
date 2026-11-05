using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Pleo.Contracts;
using Humans.Pleo.Data;
using Humans.Pleo.Domain;
using Humans.Pleo.Models;
using NodaTime;

namespace Humans.Pleo.Services;

internal sealed class Service(
    IPleoRepository repo,
    IPleoClient client,
    IClock clock,
    IAuditLogService audit,
    ILogger<Service> logger) : IPleoService, IPleoAdminService
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>One sweep at a time across the nightly job and Sync now; a second caller skips instead of waiting.</summary>
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    private const string AuditEntity = "PleoCategory";

    private static readonly PleoEntryKind[] SpendKinds =
        [PleoEntryKind.CardPurchase, PleoEntryKind.PleoInvoice, PleoEntryKind.OutOfPocket];

    public async Task<PleoSyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        if (!client.IsConfigured) return PleoSyncOutcome.NotConfigured;
        if (!await SyncGate.WaitAsync(0, ct)) return PleoSyncOutcome.Busy;
        try
        {
            var state = await repo.GetSyncStateAsync(ct) ?? new PleoSyncState();
            state.SyncStatus = PleoSyncStatus.Running;
            state.StatusChangedAt = clock.GetCurrentInstant();
            await repo.SaveSyncStateAsync(state, ct);
            try
            {
                // Everything is fetched before anything is written, so a failed call leaves the mirror as it was.
                var rawEntries = await client.SearchEntriesAsync(ct);
                var accounts = await client.SearchAccountsAsync(ct);
                var employees = await client.ListEmployeesAsync(ct);
                var balance = await client.GetBalanceAsync(ct);

                var (entries, skipped) = ToEntries(rawEntries);
                var now = clock.GetCurrentInstant();
                await repo.ReplaceMirrorAsync(new PleoMirrorSnapshot(
                    entries,
                    employees.Select(ToEmployee).ToList(),
                    accounts.Select(ToAccount).ToList(),
                    new PleoBalance
                    {
                        Currency = balance.Currency,
                        CurrentMinors = balance.Current,
                        AvailableMinors = balance.Available,
                        PendingDebitsMinors = balance.PendingDebits,
                    }), now, ct);

                state.SyncStatus = PleoSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = entries.Count;
                state.SkippedCount = skipped;
                state.LastError = null;
                await repo.SaveSyncStateAsync(state, ct);
                logger.LogInformation("Pleo sweep mirrored {Entries} entries ({Skipped} skipped), {Accounts} accounts, {Employees} cardholders",
                    entries.Count, skipped, accounts.Count, employees.Count);
                return PleoSyncOutcome.Synced;
            }
            catch (Exception ex)
            {
                state.SyncStatus = PleoSyncStatus.Error;
                state.StatusChangedAt = clock.GetCurrentInstant();
                state.LastError = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
                await repo.SaveSyncStateAsync(state, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            SyncGate.Release();
        }
    }

    /// <summary>Rows without an id or a family are skipped and counted, never mirrored half-empty.</summary>
    internal static (List<PleoEntry> Entries, int Skipped) ToEntries(IReadOnlyList<JsonElement> raw)
    {
        var entries = new List<PleoEntry>(raw.Count);
        var skipped = 0;
        foreach (var item in raw)
        {
            var wire = item.ValueKind == JsonValueKind.Object
                ? item.Deserialize<PleoEntryWire>(JsonSerializerOptions.Web)
                : null;
            if (wire is null || string.IsNullOrEmpty(wire.Id) || wire.Family is null)
            {
                skipped++;
                continue;
            }
            entries.Add(ToEntry(wire, item.GetRawText()));
        }
        // A page boundary that shifts mid-sweep can return a row twice; the next sweep catches anything it skipped.
        return (entries.DistinctBy(e => e.Id, StringComparer.Ordinal).ToList(), skipped);
    }

    private static PleoEntry ToEntry(PleoEntryWire w, string raw)
    {
        var currency = w.TransactionValue?.Currency ?? "";
        var minors = w.TransactionValue?.Minors ?? 0;
        return new PleoEntry
        {
            Id = w.Id!,
            EmployeeId = w.EmployeeId,
            TeamId = w.TeamId,
            CategoryId = w.AccountId,
            Family = w.Family,
            SubFamily = w.SubFamily,
            Status = w.Status,
            ExportStatus = w.ExportStatus,
            ReviewStatus = w.ReviewStatus,
            PerformedAt = ToInstant(w.PerformedAt),
            SettledAt = ToInstant(w.SettledAt),
            BookkeepingDate = ToInstant(w.BookkeepingDate),
            CreatedAt = ToInstant(w.CreatedAt),
            UpdatedAt = ToInstant(w.UpdatedAt),
            Currency = currency,
            Minors = minors,
            AmountEur = PleoEntryRules.SignedEur(w.Family, w.SubFamily, currency, minors,
                w.TotalBillValue?.Currency, w.TotalBillValue?.Minors),
            MerchantName = w.Merchant?.Name,
            MerchantCategoryCode = w.Merchant?.MerchantCategoryCode,
            MerchantCountry = w.Merchant?.Address?.Country,
            Note = w.Note is { Length: > 2000 } note ? note[..2000] : w.Note,
            ReceiptCount = w.ReceiptIds?.Count ?? 0,
            Raw = raw,
        };
    }

    private static Instant? ToInstant(DateTimeOffset? at) => at is { } v ? Instant.FromDateTimeOffset(v) : null;

    private static PleoEmployee ToEmployee(PleoEmployeeWire e) => new()
    {
        Id = e.Id,
        FirstName = e.FirstName ?? "",
        LastName = e.LastName ?? "",
        Code = e.Code,
        IsDeleted = e.IsDeleted,
    };

    private static PleoAccount ToAccount(PleoAccountWire a) => new()
    {
        Id = a.Id,
        Code = a.Code ?? "",
        Name = a.Name ?? "",
        ExternalId = a.ExternalId,
        Archived = a.Archived,
    };

    // ── Reads ────────────────────────────────────────────────────────────────

    private sealed record Mirror(
        IReadOnlyList<PleoEntry> Entries,
        IReadOnlyDictionary<string, PleoEmployee> Employees,
        IReadOnlyDictionary<string, PleoAccount> AccountsByCode,
        IReadOnlyList<PleoCategory> Categories,
        IReadOnlyDictionary<string, PleoCategory> CategoryByPleoId)
    {
        public string? HoldedCodeOf(PleoEntry e) =>
            e.CategoryId is { } id && CategoryByPleoId.TryGetValue(id, out var c) ? c.HoldedCode : null;

        public string CardholderOf(PleoEntry e) =>
            e.EmployeeId is null ? "—"
            : Employees.TryGetValue(e.EmployeeId, out var emp) ? emp.DisplayName
            : e.EmployeeId;

        /// <summary>Missing from the chart, or archived there: the category points at an account Holded won't take.</summary>
        public bool CodeProblem(string code) => !AccountsByCode.TryGetValue(code, out var a) || a.Archived;
    }

    private async Task<Mirror> LoadAsync(CancellationToken ct)
    {
        var entries = await repo.GetEntriesAsync(ct);
        var employees = await repo.GetEmployeesAsync(ct);
        var accounts = await repo.GetAccountsAsync(ct);
        var categories = await repo.GetCategoriesAsync(ct);
        return new Mirror(
            entries,
            employees.ToDictionary(e => e.Id, StringComparer.Ordinal),
            // A code can sit on an archived and a live account at once; the live one names it.
            accounts.GroupBy(a => a.Code, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.FirstOrDefault(a => !a.Archived) ?? g.First(), StringComparer.Ordinal),
            categories,
            categories.Where(c => c.PleoCategoryId is not null)
                .ToDictionary(c => c.PleoCategoryId!, StringComparer.Ordinal));
    }

    private static bool IsSpend(PleoEntry e) =>
        PleoEntryRules.IsCompleted(e.Status) && e.AmountEur is not null
        && SpendKinds.Contains(PleoEntryRules.KindOf(e.Family, e.SubFamily));

    private static decimal SpendOf(IEnumerable<PleoEntry> entries) => -entries.Sum(e => e.AmountEur ?? 0m);

    public async Task<IReadOnlyList<PleoEntryInfo>> GetSettledEntriesAsync(
        LocalDate from, LocalDate to, CancellationToken ct = default)
    {
        var start = from.AtStartOfDayInZone(MadridZone).ToInstant();
        var end = to.PlusDays(1).AtStartOfDayInZone(MadridZone).ToInstant();
        var m = await LoadAsync(ct);
        return m.Entries
            .Where(e => e.SettledAt is { } s && s >= start && s < end)
            .Select(e => new PleoEntryInfo(e.Id, e.Family, e.SubFamily, e.Status, e.SettledAt!.Value, e.Currency,
                e.Minors, e.AmountEur, e.MerchantName, e.EmployeeId, e.CategoryId, m.HoldedCodeOf(e), e.ReceiptCount > 0))
            .ToList();
    }

    public async Task<PleoWalletBalance?> GetWalletBalanceAsync(CancellationToken ct = default) =>
        await repo.GetBalanceAsync(ct) is { } b && string.Equals(b.Currency, PleoEntryRules.Eur, StringComparison.Ordinal)
            ? new PleoWalletBalance(b.CurrentMinors / 100m, b.SyncedAt)
            : null;

    public async Task<PleoSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var m = await LoadAsync(ct);
        var state = await repo.GetSyncStateAsync(ct);
        var balance = await repo.GetBalanceAsync(ct);
        var spend = m.Entries.Where(IsSpend).ToList();

        var byAccount = spend.Where(e => m.HoldedCodeOf(e) is not null)
            .GroupBy(e => m.HoldedCodeOf(e)!, StringComparer.Ordinal)
            .Select(g => new PleoSpendTotal(g.Key,
                m.AccountsByCode.TryGetValue(g.Key, out var a) ? $"{g.Key} · {a.Name}" : g.Key,
                g.Count(), SpendOf(g), m.CodeProblem(g.Key)))
            .ToList();
        var unmappedRows = spend.Where(e => m.HoldedCodeOf(e) is null).ToList();
        var noReceiptRows = spend.Where(e => e.ReceiptCount == 0).ToList();
        var byCardholder = spend.GroupBy(e => e.EmployeeId ?? "", StringComparer.Ordinal)
            .Select(g => new PleoSpendTotal(g.Key, m.CardholderOf(g.First()), g.Count(), SpendOf(g)))
            .ToList();

        return new PleoSummary(
            client.IsConfigured,
            state is null ? null
                : new PleoSyncInfo(state.SyncStatus.ToString(), state.LastSyncAt, state.LastCount, state.SkippedCount,
                    state.LastError),
            m.Entries.Count,
            Reconcile(m.Entries, balance),
            byAccount,
            new PleoSpendTotal("", "Unmapped", unmappedRows.Count, SpendOf(unmappedRows)),
            new PleoSpendTotal("", "Missing receipt", noReceiptRows.Count, SpendOf(noReceiptRows)),
            byCardholder,
            UnlinkedCategoryIds(m).Count);
    }

    internal static PleoReconciliation Reconcile(IReadOnlyList<PleoEntry> entries, PleoBalance? balance)
    {
        var completed = entries.Where(e => PleoEntryRules.IsCompleted(e.Status)).ToList();
        var counted = completed.Where(e => e.AmountEur is not null).ToList();

        var kinds = new[]
        {
            (PleoEntryKind.Load, "Loads in"),
            (PleoEntryKind.CardPurchase, "Card purchases"),
            (PleoEntryKind.PleoInvoice, "Pleo invoices"),
            (PleoEntryKind.OutOfPocket, "Out of pocket"),
        }.Select(k =>
        {
            var rows = counted.Where(e => PleoEntryRules.KindOf(e.Family, e.SubFamily) == k.Item1).ToList();
            // A refund runs against its family's direction: money back on a card, money out of a load.
            var refunds = rows.Where(e => k.Item1 == PleoEntryKind.Load ? e.AmountEur < 0 : e.AmountEur > 0).ToList();
            return new PleoKindTotal(k.Item2, rows.Count, rows.Sum(e => e.AmountEur!.Value),
                refunds.Count, refunds.Sum(e => e.AmountEur!.Value));
        }).ToList();

        var unclassified = completed
            .Where(e => PleoEntryRules.KindOf(e.Family, e.SubFamily) == PleoEntryKind.Unclassified).ToList();
        var euro = balance is not null && string.Equals(balance.Currency, PleoEntryRules.Eur, StringComparison.Ordinal);

        return new PleoReconciliation(
            kinds,
            kinds.Sum(k => k.Total),
            euro ? balance!.CurrentMinors / 100m : null,
            euro ? balance!.PendingDebitsMinors / 100m : null,
            ConvertedCount: counted.Count(e => !string.Equals(e.Currency, PleoEntryRules.Eur, StringComparison.Ordinal)),
            NoEurCount: completed.Count(e => e.AmountEur is null),
            PendingCount: entries.Count - completed.Count,
            UnclassifiedCount: unclassified.Count,
            UnclassifiedTotal: unclassified.Sum(e => e.AmountEur ?? 0m));
    }

    public async Task<PleoEntriesPage> GetEntriesPageAsync(PleoEntriesFilter filter, CancellationToken ct = default)
    {
        var m = await LoadAsync(ct);
        IEnumerable<PleoEntry> rows = m.Entries;
        if (!string.IsNullOrEmpty(filter.Account)) rows = rows.Where(e => string.Equals(m.HoldedCodeOf(e), filter.Account, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(filter.Cardholder)) rows = rows.Where(e => string.Equals(e.EmployeeId, filter.Cardholder, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(filter.Family)) rows = rows.Where(e => string.Equals(e.Family, filter.Family, StringComparison.Ordinal));
        // Both blocking filters leave loads out: a load is neither categorised nor receipted, and is booked another way.
        if (filter.NoReceipt || filter.Unmapped)
            rows = rows.Where(e => PleoEntryRules.KindOf(e.Family, e.SubFamily) != PleoEntryKind.Load);
        if (filter.NoReceipt) rows = rows.Where(e => e.ReceiptCount == 0);
        if (filter.Unmapped) rows = rows.Where(e => m.HoldedCodeOf(e) is null);

        return new PleoEntriesPage(
            filter,
            rows.Select(e => ToRow(e, m)).ToList(),
            m.Categories.Select(c => c.HoldedCode).Distinct(StringComparer.Ordinal)
                .Select(code => new PleoOption(code,
                    m.AccountsByCode.TryGetValue(code, out var a) ? $"{code} · {a.Name}" : code))
                .ToList(),
            m.Employees.Values.Select(e => new PleoOption(e.Id, e.DisplayName)).ToList(),
            m.Entries.Select(e => e.Family!).Distinct(StringComparer.Ordinal).ToList());
    }

    private static PleoEntryRow ToRow(PleoEntry e, Mirror m)
    {
        var code = m.HoldedCodeOf(e);
        return new PleoEntryRow(e.Id, e.PerformedAt, e.SettledAt, m.CardholderOf(e), e.MerchantName, e.Family,
            e.SubFamily, e.Status, e.AmountEur, e.Currency, e.Minors, code,
            code is not null && m.AccountsByCode.TryGetValue(code, out var a) ? a.Name : null,
            e.ReceiptCount > 0);
    }

    public async Task<PleoEntryView?> GetEntryViewAsync(string id, CancellationToken ct = default)
    {
        var m = await LoadAsync(ct);
        var entry = m.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (entry is null) return null;

        IReadOnlyList<PleoReceiptView> receipts = [];
        string? receiptError = null;
        if (entry.ReceiptCount > 0)
        {
            if (!client.IsConfigured)
            {
                receiptError = "Pleo is not configured, so the receipts can't be fetched.";
            }
            else
            {
                try
                {
                    receipts = (await client.ListReceiptsAsync(id, ct))
                        .Select(r => new PleoReceiptView(r.Id, r.MimeType)).ToList();
                }
                catch (PleoApiException ex)
                {
                    logger.LogWarning(ex, "Pleo receipt lookup failed for entry {EntryId}", id);
                    receiptError = $"The receipt lookup failed: {ex.Message}";
                }
            }
        }

        using var raw = JsonDocument.Parse(entry.Raw);
        return new PleoEntryView(
            ToRow(entry, m),
            entry.CategoryId,
            entry.CategoryId is { } cid && m.CategoryByPleoId.TryGetValue(cid, out var cat) ? cat.Name : null,
            entry.Note,
            JsonSerializer.Serialize(raw.RootElement, new JsonSerializerOptions { WriteIndented = true }),
            receipts,
            receiptError);
    }

    public async Task<PleoReceiptFile?> GetReceiptFileAsync(
        string entryId, string receiptId, CancellationToken ct = default)
    {
        // Only entries in the mirror: this is not a general proxy onto Pleo.
        if (!client.IsConfigured || await repo.GetEntryAsync(entryId, ct) is null) return null;
        try
        {
            return await client.GetReceiptFileAsync(entryId, receiptId, ct);
        }
        catch (PleoApiException ex)
        {
            logger.LogWarning(ex, "Pleo receipt {ReceiptId} fetch failed for entry {EntryId}", receiptId, entryId);
            return null;
        }
    }

    private static List<string> UnlinkedCategoryIds(Mirror m) =>
        m.Entries.Where(e => e.CategoryId is not null && !m.CategoryByPleoId.ContainsKey(e.CategoryId))
            .Select(e => e.CategoryId!).Distinct(StringComparer.Ordinal).ToList();

    public async Task<PleoCategoriesPage> GetCategoriesPageAsync(CancellationToken ct = default)
    {
        var m = await LoadAsync(ct);
        var byCategory = m.Entries.Where(e => e.CategoryId is not null)
            .ToLookup(e => e.CategoryId!, StringComparer.Ordinal);

        var unlinked = UnlinkedCategoryIds(m).Select(id =>
        {
            var rows = byCategory[id].ToList();
            return new PleoUnlinkedCategory(id, rows.Count, SpendOf(rows.Where(IsSpend)),
                rows.Where(e => e.MerchantName is not null).GroupBy(e => e.MerchantName!, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList(),
                rows.Select(m.CardholderOf).Distinct(StringComparer.Ordinal).ToList());
        }).ToList();

        var categories = m.Categories.Select(c =>
        {
            var rows = c.PleoCategoryId is null ? [] : byCategory[c.PleoCategoryId].ToList();
            return new PleoCategoryRow(c.Id, c.Name, c.HoldedCode,
                m.AccountsByCode.TryGetValue(c.HoldedCode, out var a) ? a.Name : null,
                m.CodeProblem(c.HoldedCode), c.PleoCategoryId, rows.Count, SpendOf(rows.Where(IsSpend)), c.SetAt);
        }).ToList();

        return new PleoCategoriesPage(
            unlinked,
            categories,
            m.Categories.Where(c => c.PleoCategoryId is null).Select(c => new PleoOption(c.Id.ToString(), c.Name))
                .ToList(),
            m.AccountsByCode.Values.Where(a => !a.Archived).Select(a => new PleoOption(a.Code, $"{a.Code} · {a.Name}"))
                .ToList());
    }

    // ── Category mapping (the one table the sweep never touches) ────────────

    public async Task<string?> LinkCategoryAsync(
        string pleoCategoryId, Guid categoryId, Guid userId, CancellationToken ct = default)
    {
        pleoCategoryId = pleoCategoryId.Trim();
        if (pleoCategoryId.Length is 0 or > 64) return "That is not a Pleo category id.";
        var categories = await repo.GetCategoriesAsync(ct);
        var category = categories.FirstOrDefault(c => c.Id == categoryId);
        if (category is null) return "No such category.";
        if (category.PleoCategoryId is not null) return $"{category.Name} is already linked.";
        if (categories.FirstOrDefault(c => string.Equals(c.PleoCategoryId, pleoCategoryId, StringComparison.Ordinal)) is { } other)
            return $"That id is already linked to {other.Name}.";

        category.PleoCategoryId = pleoCategoryId;
        Stamp(category, userId);
        await repo.SaveCategoryAsync(category, ct);
        await audit.LogAsync(PleoAuditActions.PleoCategoryLinked, AuditEntity, category.Id,
            $"Linked Pleo category id {pleoCategoryId} to '{category.Name}' (Holded {category.HoldedCode})", userId);
        return null;
    }

    public async Task<string?> UnlinkCategoryAsync(Guid categoryId, Guid userId, CancellationToken ct = default)
    {
        var category = (await repo.GetCategoriesAsync(ct)).FirstOrDefault(c => c.Id == categoryId);
        if (category?.PleoCategoryId is null) return "That category is not linked.";

        var pleoCategoryId = category.PleoCategoryId;
        category.PleoCategoryId = null;
        Stamp(category, userId);
        await repo.SaveCategoryAsync(category, ct);
        await audit.LogAsync(PleoAuditActions.PleoCategoryUnlinked, AuditEntity, category.Id,
            $"Unlinked Pleo category id {pleoCategoryId} from '{category.Name}' (Holded {category.HoldedCode})", userId);
        return null;
    }

    public async Task<string?> AddCategoryAsync(string name, string holdedCode, Guid userId, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is 0 or > 128) return "A category needs a name of up to 128 characters.";
        var categories = await repo.GetCategoriesAsync(ct);
        if (categories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a category named {name}.";
        var accounts = await repo.GetAccountsAsync(ct);
        if (!accounts.Any(a => string.Equals(a.Code, holdedCode, StringComparison.Ordinal) && !a.Archived))
            return "Pick a live account from Pleo's chart.";

        var category = new PleoCategory { Id = Guid.NewGuid(), Name = name, HoldedCode = holdedCode };
        Stamp(category, userId);
        await repo.SaveCategoryAsync(category, ct);
        await audit.LogAsync(PleoAuditActions.PleoCategoryAdded, AuditEntity, category.Id,
            $"Added Pleo category '{name}' → Holded {holdedCode}", userId);
        return null;
    }

    private void Stamp(PleoCategory category, Guid userId)
    {
        category.SetByUserId = userId;
        category.SetAt = clock.GetCurrentInstant();
    }
}
