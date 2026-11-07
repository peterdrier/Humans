using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

internal sealed class HoldedCategoryAccounts(
    IHoldedRepository repo,
    IHoldedClient client,
    IBudgetService budget,
    IClock clock,
    IAuditLogService audit,
    ILogger logger) : IApplicationService
{
    internal const string HoldedExpenseAccount = "HoldedExpenseAccount";

    public async Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(
        int blockStart, CancellationToken ct = default)
    {
        var categories = (await ActiveYearCategoriesAsync(ct))
            .Select(c => (Id: c.Key, c.Value.Name, c.Value.Group))
            .ToArray();

        var map = await repo.GetCategoryMapAsync(ct);
        var activeByCat = map.ToDictionary(m => m.BudgetCategoryId);

        // Avoid numbers in BOTH the local map and the live chart, so one taken remotely but missing locally (a failed
        // map write, or an account made directly in Holded) is never re-proposed.
        var remoteAccounts = await client.ListExpenseAccountsAsync(ct);
        var usedNumbers = map.Select(m => m.HoldedAccountNumber)
            .Concat(remoteAccounts.Select(a => a.AccountNum))
            .ToHashSet();

        var rows = new List<HoldedProvisioningRow>();
        // Seeded up front, not as the walk meets them: otherwise a ToAdd category sorting before a mapped one could be
        // handed its tag, and two rows sharing a tag make attribution arbitrary.
        var usedTags = map.Select(m => m.Tag).ToHashSet(StringComparer.Ordinal);
        var currentActiveCatIds = categories.Select(c => c.Id).ToHashSet();

        int nextFree = blockStart;

        foreach (var (catId, catName, groupName) in categories
            .OrderBy(c => c.Group, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            if (activeByCat.TryGetValue(catId, out var existing))
            {
                rows.Add(new HoldedProvisioningRow(
                    BudgetCategoryId: catId,
                    CategoryName: catName,
                    GroupName: groupName,
                    ExistingAccountNum: existing.HoldedAccountNumber,
                    ProposedAccountNum: null,
                    Tag: existing.Tag,
                    State: "Mapped"));
            }
            else
            {
                var tag = UniqueTag(groupName, catName, catId, usedTags);
                usedTags.Add(tag);

                while (usedNumbers.Contains(nextFree))
                    nextFree++;
                var proposed = nextFree;
                usedNumbers.Add(proposed);
                nextFree++;

                rows.Add(new HoldedProvisioningRow(
                    BudgetCategoryId: catId,
                    CategoryName: catName,
                    GroupName: groupName,
                    ExistingAccountNum: null,
                    ProposedAccountNum: proposed,
                    Tag: tag,
                    State: "ToAdd"));
            }
        }

        foreach (var m in activeByCat.Values.Where(m => !currentActiveCatIds.Contains(m.BudgetCategoryId)))
        {
            rows.Add(new HoldedProvisioningRow(
                BudgetCategoryId: m.BudgetCategoryId,
                CategoryName: "(deleted)",
                GroupName: "(deleted)",
                ExistingAccountNum: m.HoldedAccountNumber,
                ProposedAccountNum: null,
                Tag: m.Tag,
                State: "Orphan"));
        }

        while (usedNumbers.Contains(nextFree))
            nextFree++;

        return new HoldedProvisioningPlan(rows, nextFree);
    }

    public async Task<int> ProvisionAsync(
        int blockStart, bool addAll, Guid actorUserId, CancellationToken ct = default)
    {
        var plan = await GetProvisioningPlanAsync(blockStart, ct);
        var toAdd = plan.Rows.Where(r => string.Equals(r.State, "ToAdd", StringComparison.Ordinal)).ToList();
        if (!addAll)
            toAdd = toAdd.Take(1).ToList();

        var now = clock.GetCurrentInstant();
        var created = 0;

        foreach (var row in toAdd)
        {
            try
            {
                var accountName = $"{row.GroupName} / {row.CategoryName}";
                var id = await client.CreateExpenseAccountAsync(row.ProposedAccountNum!.Value, accountName, ct);
                // Audited as soon as Holded holds the account: a failed map save below must not hide it.
                await audit.LogAsync(FinanceAuditActions.HoldedCategoryAccountProvisioned, HoldedExpenseAccount,
                    row.BudgetCategoryId,
                    $"Provisioned Holded expense account {row.ProposedAccountNum.Value} '{accountName}'",
                    actorUserId);
                await repo.AddCategoryMapAsync(new HoldedCategoryMap
                {
                    Id = Guid.NewGuid(),
                    BudgetCategoryId = row.BudgetCategoryId,
                    HoldedAccountNumber = row.ProposedAccountNum.Value,
                    HoldedAccountId = id,
                    Tag = row.Tag,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct);
                created++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to provision Holded account for category {CategoryId} ({Name})",
                    row.BudgetCategoryId, row.CategoryName);
                // Partial success: already-created rows are persisted; let this one propagate.
                throw;
            }
        }

        return created;
    }

    public async Task<string?> GetHoldedAccountIdForCategoryAsync(
        Guid budgetCategoryId, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        return map.FirstOrDefault(m => m.BudgetCategoryId == budgetCategoryId)?.HoldedAccountId;
    }

    /// <summary>Category and group names resolved from the active budget year.</summary>
    private async Task<IReadOnlyList<CategoryAccountRow>> BuildCategoryMapAsync(CancellationToken ct)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var categories = await ActiveYearCategoriesAsync(ct);

        return map.Select(m => new CategoryAccountRow(
            m.BudgetCategoryId,
            categories.TryGetValue(m.BudgetCategoryId, out var c) ? c.Name : null,
            categories.TryGetValue(m.BudgetCategoryId, out var g) ? g.Group : null,
            m.HoldedAccountNumber,
            m.HoldedAccountId,
            m.Tag,
            m.UpdatedAt)).ToList();
    }

    public Task<IReadOnlyList<CategoryAccountRow>> GetCategoryMapAsync(CancellationToken ct = default) =>
        BuildCategoryMapAsync(ct);

    /// <summary>Empty when no year is active.</summary>
    internal async Task<Dictionary<Guid, (string Name, string Group)>> ActiveYearCategoriesAsync(CancellationToken ct)
    {
        var year = await budget.GetActiveYearAsync(ct);
        return year is null
            ? new Dictionary<Guid, (string Name, string Group)>()
            : year.Groups
                .SelectMany(g => g.Categories.Select(c => (c.Id, Name: c.Name, Group: g.Name)))
                .ToDictionary(c => c.Id, c => (c.Name, c.Group));
    }

    /// <summary>Bounded, dash-free key; existing mapped tags always win collisions.</summary>
    private static string UniqueTag(
        string groupName, string categoryName, Guid categoryId,
        HashSet<string> usedTags)
    {
        var baseTag = HoldedMatcher.NormalizeTag(groupName + categoryName);
        if (baseTag.Length == 0) baseTag = categoryId.ToString("N");
        baseTag = baseTag[..Math.Min(baseTag.Length, HoldedCategoryMap.TagMaxLength)];
        if (!usedTags.Contains(baseTag)) return baseTag;

        var categorySuffix = categoryId.ToString("N")[..4];
        var suffix = categorySuffix;
        for (var attempt = 1; ; attempt++)
        {
            var candidate = baseTag[..Math.Min(baseTag.Length, HoldedCategoryMap.TagMaxLength - suffix.Length)] + suffix;
            if (!usedTags.Contains(candidate)) return candidate;
            suffix = categorySuffix + attempt.ToString(CultureInfo.InvariantCulture);
        }
    }
}
