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
    private readonly PleoMirrorSync mirrorSync = new(repo, client, clock, audit, logger);
    private readonly PleoMirrorReader mirrorReader = new(repo, client, logger);

    private const string AuditEntity = "PleoCategory";

    public Task<PleoSyncOutcome> SyncAsync(CancellationToken ct = default) =>
        mirrorSync.SyncAsync(ct);

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

    // ── Category mapping by hand (the sweep fills in what nobody linked) ────

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
