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
    private readonly PleoMirrorReader mirrorReader = new(repo, client, logger);

    /// <summary>One sweep at a time across the nightly job and Sync now; a second caller skips instead of waiting.</summary>
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    private const string AuditEntity = "PleoCategory";

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
                try
                {
                    state.SyncStatus = PleoSyncStatus.Error;
                    state.StatusChangedAt = clock.GetCurrentInstant();
                    state.LastError = PleoTextBounds.Bound(ex.Message, 2000);
                    await repo.SaveSyncStateAsync(state, CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    logger.LogError(saveEx, "Failed to persist Pleo sync error state");
                }
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
            Note = PleoTextBounds.Bound(w.Note, 2000),
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

    public Task<IReadOnlyList<PleoEntryInfo>> GetSettledEntriesAsync(
        LocalDate from, LocalDate to, CancellationToken ct = default) =>
        mirrorReader.GetSettledEntriesAsync(from, to, ct);

    public Task<PleoWalletBalance?> GetWalletBalanceAsync(CancellationToken ct = default) =>
        mirrorReader.GetWalletBalanceAsync(ct);

    public Task<PleoSummary> GetSummaryAsync(CancellationToken ct = default) =>
        mirrorReader.GetSummaryAsync(ct);

    public Task<PleoEntriesPage> GetEntriesPageAsync(PleoEntriesFilter filter, CancellationToken ct = default) =>
        mirrorReader.GetEntriesPageAsync(filter, ct);

    public Task<PleoEntryView?> GetEntryViewAsync(string id, CancellationToken ct = default) =>
        mirrorReader.GetEntryViewAsync(id, ct);

    public Task<PleoReceiptFile?> GetReceiptFileAsync(
        string entryId, string receiptId, CancellationToken ct = default) =>
        mirrorReader.GetReceiptFileAsync(entryId, receiptId, ct);

    public Task<PleoCategoriesPage> GetCategoriesPageAsync(CancellationToken ct = default) =>
        mirrorReader.GetCategoriesPageAsync(ct);

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
