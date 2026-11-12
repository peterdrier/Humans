using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Pleo.Contracts;
using Humans.Pleo.Data;
using Humans.Pleo.Domain;
using Humans.Pleo.Models;
using NodaTime;

namespace Humans.Pleo.Services;

internal sealed class PleoMirrorSync(
    IPleoRepository repo,
    IPleoClient client,
    IClock clock,
    IAuditLogService audit,
    ILogger<Service> logger) : IApplicationService
{
    /// <summary>One sweep at a time across the nightly job and Sync now; a second caller skips instead of waiting.</summary>
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

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
                var (pleoCategories, categoryError) = await TrySearchCategoriesAsync(ct);
                var employees = await client.ListEmployeesAsync(ct);
                var balance = await client.GetBalanceAsync(ct);

                var (entries, skipped) = ToEntries(rawEntries);
                var now = clock.GetCurrentInstant();
                var categoryChanges = PlanCategories(await repo.GetCategoriesAsync(ct), pleoCategories, accounts, now);
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
                    },
                    categoryChanges.Select(c => c.Category).ToList()), now, ct);
                foreach (var change in categoryChanges)
                    await audit.LogAsync(change.Action, "PleoCategory", change.Category.Id, change.Description,
                        nameof(Jobs.PleoSyncJob));

                state.SyncStatus = PleoSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = entries.Count;
                state.SkippedCount = skipped;
                state.LastError = categoryError;
                await repo.SaveSyncStateAsync(state, ct);
                logger.LogInformation("Pleo sweep mirrored {Entries} entries ({Skipped} skipped), {Accounts} accounts, {Employees} cardholders",
                    entries.Count, skipped, accounts.Count, employees.Count);
                return categoryError is null ? PleoSyncOutcome.Synced : PleoSyncOutcome.SyncedWithoutCategories;
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

    /// <summary>
    /// Categories only feed Pleo's own mapping, so a failed fetch (e.g. a key without <c>accountCategory:read</c>) must
    /// not stop the sweep: <c>pleo_categories</c> stays as it is and the error is kept for the sync card.
    /// </summary>
    private async Task<(IReadOnlyList<PleoCategoryWire> Categories, string? Error)> TrySearchCategoriesAsync(CancellationToken ct)
    {
        try
        {
            return (await client.SearchCategoriesAsync(ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Pleo category fetch failed; sweeping without refreshing categories");
            return ([], PleoTextBounds.Bound($"Categories not refreshed: {ex.Message}", 2000));
        }
    }

    /// <summary>
    /// Pleo's own category → account mapping, laid over <c>pleo_categories</c>. Whoever linked an id owns the row: one a
    /// person linked is left as it is; an id nobody linked is linked to the unlinked row of the same name, or added; a
    /// row this sweep linked follows Pleo's name and account. A category with no account in the chart is left unmapped.
    /// Returns only the rows that change; nothing is ever deleted or unlinked.
    /// </summary>
    internal static List<PleoCategoryChange> PlanCategories(
        IReadOnlyList<PleoCategory> existing, IReadOnlyList<PleoCategoryWire> fetched,
        IReadOnlyList<PleoAccountWire> accounts, Instant now)
    {
        var codeByAccount = accounts.Where(a => a.Code is { Length: > 0 and <= 16 })
            .ToDictionary(a => a.Id, a => a.Code!, StringComparer.Ordinal);
        var linked = existing.Where(c => c.PleoCategoryId is not null)
            .ToDictionary(c => c.PleoCategoryId!, StringComparer.Ordinal);
        var unlinked = existing.Where(c => c.PleoCategoryId is null).ToList();
        var changes = new List<PleoCategoryChange>();

        // Live categories first: where an archived one shares a name, the live one takes the existing row.
        foreach (var w in fetched.DistinctBy(w => w.Id, StringComparer.Ordinal).OrderBy(w => w.Archived))
        {
            var name = PleoTextBounds.Bound(w.Name?.Trim(), 128);
            if (string.IsNullOrEmpty(name) || w.Id.Length is 0 or > 64
                || w.AccountId is null || !codeByAccount.TryGetValue(w.AccountId, out var code))
                continue;

            if (linked.TryGetValue(w.Id, out var row))
            {
                if (row.SetByUserId is not null
                    || (string.Equals(row.Name, name, StringComparison.Ordinal) && string.Equals(row.HoldedCode, code, StringComparison.Ordinal)))
                    continue;
                changes.Add(new(row, PleoAuditActions.PleoCategoryRemapped,
                    $"Pleo category id {w.Id}: '{row.Name}' (Holded {row.HoldedCode}) is now '{name}' (Holded {code}) in Pleo"));
            }
            else if (unlinked.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                unlinked.Remove(match);
                row = match;
                var was = string.Equals(row.HoldedCode, code, StringComparison.Ordinal) ? "" : $", was Holded {row.HoldedCode}";
                changes.Add(new(row, PleoAuditActions.PleoCategoryLinked,
                    $"Linked Pleo category id {w.Id} to '{name}' (Holded {code}{was}) from Pleo's own mapping"));
            }
            else
            {
                row = new PleoCategory { Id = Guid.NewGuid() };
                changes.Add(new(row, PleoAuditActions.PleoCategoryAdded,
                    $"Added Pleo category '{name}' → Holded {code} (id {w.Id}) from Pleo's own mapping"));
            }

            row.Name = name;
            row.HoldedCode = code;
            row.PleoCategoryId = w.Id;
            row.SetByUserId = null;
            row.SetAt = now;
        }
        return changes;
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
        RequireCompleteMoney(w.TransactionValue, "transactionValue", w.Id!);
        RequireCompleteMoney(w.TotalBillValue, "totalBillValue", w.Id!);
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

    private static void RequireCompleteMoney(PleoMoneyWire? money, string field, string entryId)
    {
        if (money is not null && (string.IsNullOrWhiteSpace(money.Currency) || money.Minors is null))
            throw new PleoPermanentException($"Pleo entry {entryId}: {field} requires currency and minors.");
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
}

internal sealed record PleoCategoryChange(PleoCategory Category, AuditActionDefinition Action, string Description);
