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

internal sealed class CategoryAccounts(
    IFinanceRepository repo,
    IHoldedService holded,
    IHoldedWrite holdedWrite,
    IBudgetService budget,
    IClock clock,
    IAuditLogService audit,
    ILogger logger) : IApplicationService
{
    internal const string HoldedExpenseAccount = "HoldedExpenseAccount";

    /// <summary>First number of the P&amp;L expense block both provisioning paths allocate from.</summary>
    private const int ExpenseAccountBlockStart = 62900100;

    // PGC group 6, the accounts Holded's expenses-accounts list holds.
    private const int ExpenseAccountsFrom = 60000000;
    private const int ExpenseAccountsTo = 69999999;
    public async Task<ExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();

        IReadOnlyList<HoldedChartAccountInfo> chart;
        try
        {
            // Matching by name against a stale mirror would miss an account made in Holded's UI and mint its twin.
            await holded.SyncChartAsync(ct);
            chart = await holded.GetChartAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh the Holded chart while creating or linking account '{Name}'", name);
            throw;
        }

        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var expenseChart = chart.Where(a => a.Number is >= ExpenseAccountsFrom and <= ExpenseAccountsTo).ToList();

        HoldedChartAccountInfo? found;
        if (existingAccountNum is { } num)
        {
            found = expenseChart.FirstOrDefault(a => a.Number == num)
                ?? throw new InvalidOperationException($"Holded has no expense account {num}.");
        }
        else
        {
            var wanted = HoldedMatcher.NormalizeAccountName(name);
            found = expenseChart.FirstOrDefault(a =>
                !a.Archived
                && string.Equals(HoldedMatcher.NormalizeAccountName(a.Name), wanted, StringComparison.Ordinal));
        }

        var now = clock.GetCurrentInstant();
        if (found is not null)
        {
            // In the mirror, so this only reads its id.
            var linked = await holdedWrite.EnsureExpenseAccountAsync(found.Number, found.Name, ct);
            var isCategoryAccount = map.Any(m => m.HoldedAccountNumber == found.Number);
            if (!isCategoryAccount)
                await RegisterManagedAsync(found.Number, linked.HoldedId, found.Name, managed, now, ct);
            await audit.LogAsync(FinanceAuditActions.HoldedExpenseAccountLinked, HoldedExpenseAccount, Guid.Empty,
                $"Linked Holded expense account {found.Number} '{found.Name}' for '{name}'", "Finance");
            return new ExpenseAccountRef(found.Number, linked.HoldedId, found.Name, Created: false);
        }

        var used = map.Select(m => m.HoldedAccountNumber)
            .Concat(managed.Select(m => m.HoldedAccountNumber))
            .Concat(chart.Select(a => a.Number))
            .ToHashSet();
        var next = ExpenseAccountBlockStart;
        while (used.Contains(next)) next++;

        HoldedExpenseAccountOutcome created;
        try
        {
            created = await holdedWrite.EnsureExpenseAccountAsync(next, name, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Holded expense account {AccountNum} '{Name}'", next, name);
            throw;
        }

        await audit.LogAsync(FinanceAuditActions.HoldedExpenseAccountCreated, HoldedExpenseAccount, Guid.Empty,
            $"Created Holded expense account {next} '{name}'", "Finance");
        await RegisterManagedAsync(next, created.HoldedId, name, managed, now, ct);
        return new ExpenseAccountRef(next, created.HoldedId, name, Created: true);
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

    public async Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default)
    {
        var managed = await repo.GetManagedAccountsAsync(ct);
        var row = managed.FirstOrDefault(m => m.HoldedAccountNumber == accountNum);
        if (row is null || row.IsActive == isActive) return;

        row.IsActive = isActive;
        row.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpsertManagedAccountAsync(row, ct);
        await audit.LogAsync(FinanceAuditActions.HoldedExpenseAccountActiveChanged, HoldedExpenseAccount, Guid.Empty,
            $"{(isActive ? "Restored" : "Retired")} Holded expense account {accountNum} '{row.Label}'", "Finance");
    }

    public async Task<IReadOnlyList<ExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default)
    {
        var map = await repo.GetCategoryMapAsync(ct);
        var managed = await repo.GetManagedAccountsAsync(ct);
        var labels = (await ActiveYearCategoriesAsync(ct))
            .ToDictionary(c => c.Key, c => $"{c.Value.Group} / {c.Value.Name}");

        // A map row whose category is not in the active year still labels old reports but takes no new booking, so it
        // is inactive here and dropped with activeOnly.
        var options = map
            .Select(m => new ExpenseAccountOption(
                m.HoldedAccountNumber, m.HoldedAccountId,
                labels.GetValueOrDefault(m.BudgetCategoryId, $"Category {m.HoldedAccountNumber}"),
                IsBudgetCategory: true, IsActive: labels.ContainsKey(m.BudgetCategoryId)))
            .Where(o => !activeOnly || o.IsActive)
            .Concat(managed
                .Where(m => !activeOnly || m.IsActive)
                .Select(m => new ExpenseAccountOption(
                    m.HoldedAccountNumber, m.HoldedAccountId, m.Label,
                    IsBudgetCategory: false, IsActive: m.IsActive)))
            .ToList();
        return options;
    }

    public async Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(
        int blockStart, CancellationToken ct = default)
    {
        var categories = (await ActiveYearCategoriesAsync(ct))
            .Select(c => (Id: c.Key, c.Value.Name, c.Value.Group))
            .ToArray();

        var map = await repo.GetCategoryMapAsync(ct);
        var activeByCat = map.ToDictionary(m => m.BudgetCategoryId);

        // Avoid numbers in BOTH the local map and the chart mirror, so one taken remotely but missing locally (a failed
        // map write, or an account made directly in Holded) is never re-proposed.
        var remoteAccounts = await holded.GetChartAsync(ct);
        var usedNumbers = map.Select(m => m.HoldedAccountNumber)
            .Concat(remoteAccounts.Select(a => a.Number))
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
        // The plan proposes numbers the mirror lacks; a stale mirror would propose one made in Holded's UI since.
        await holded.SyncChartAsync(ct);
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
                var account = await holdedWrite.EnsureExpenseAccountAsync(
                    row.ProposedAccountNum!.Value, accountName, ct);
                var id = account.HoldedId;
                // Audited as soon as Holded holds the account: a failed map save below must not hide it.
                await audit.LogAsync(FinanceAuditActions.HoldedCategoryAccountProvisioned, HoldedExpenseAccount,
                    row.BudgetCategoryId,
                    account.Created
                        ? $"Provisioned Holded expense account {row.ProposedAccountNum.Value} '{accountName}'"
                        : $"Mapped existing Holded expense account {row.ProposedAccountNum.Value} for '{accountName}'",
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
    private async Task<Dictionary<Guid, (string Name, string Group)>> ActiveYearCategoriesAsync(CancellationToken ct)
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
    /// <summary>The purchase-document mirror with each document's category attribution: account match over tag
    /// match, a managed account matches with no category. Computed on read, so a mapping fixed in Holded or added
    /// here applies on the next page load.</summary>
    private async Task<(HoldedPurchaseDocMirror Mirror, IReadOnlyList<AttributedDoc> Docs)> AttributedDocsAsync(
        CancellationToken ct)
    {
        var mirror = await holded.GetPurchaseDocumentsAsync(ct);
        var entries = (await repo.GetCategoryMapAsync(ct))
            .Select(m => new HoldedMatchEntry(m.BudgetCategoryId, m.HoldedAccountId, m.Tag))
            .ToArray();
        var managedIds = (await repo.GetManagedAccountsAsync(ct))
            .Select(m => m.HoldedAccountId)
            .ToHashSet(StringComparer.Ordinal);
        return (mirror, mirror.Docs
            .Select(d => new AttributedDoc(d, HoldedMatcher.Match(d.BookedAccountId, d.Tags, entries, managedIds)))
            .ToList());
    }

    public async Task<IReadOnlyList<CategoryActualRow>> GetActualsForYearAsync(
        int calendarYear, CancellationToken ct = default)
    {
        // Doc-derived, not ledger balances: the budget pages are gross/IVA-inclusive while a 629
        // balance is net, and the ledger carries drafts Holded has not approved.
        var (_, docs) = await AttributedDocsAsync(ct);
        return docs
            .Where(d => d.Doc.IsApproved && d.Doc.Date.Year == calendarYear && d.Match.CategoryId is not null)
            .GroupBy(d => d.Match.CategoryId!.Value)
            .Select(g => new CategoryActualRow(
                g.Key,
                g.Sum(d => d.Doc.Total),
                g.Select(d => new CategoryActualDoc(
                        d.Doc.Id, d.Doc.DocNumber, d.Doc.ContactName, d.Doc.Description, d.Doc.Date, d.Doc.Total,
                        d.Doc.HoldedUrl))
                    .ToList()))
            .Where(r => r.Actual != 0m)
            .ToList();
    }

    public async Task<IReadOnlyList<UnmatchedDocRow>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var (_, docs) = await AttributedDocsAsync(ct);
        return docs
            .Where(d => d.IsUnmatched)
            .Select(d => new UnmatchedDocRow(
                d.Doc.Id,
                d.Doc.DocNumber,
                d.Doc.ContactName,
                d.Doc.Description,
                d.Doc.Total,
                ReasonFor(d.Doc),
                d.Doc.HoldedUrl)
            { Date = d.Doc.Date })
            .ToList();
    }

    public async Task<IReadOnlyList<HoldedManagedAccountVm>> GetManagedAccountsAsync(CancellationToken ct = default) =>
        (await repo.GetManagedAccountsAsync(ct))
            .Select(m => new HoldedManagedAccountVm(m.HoldedAccountNumber, m.HoldedAccountId, m.Label, m.IsActive, m.CreatedAt))
            .ToList();

    private static string ReasonFor(HoldedPurchaseDocInfo d)
    {
        var hasAccount = !string.IsNullOrEmpty(d.BookedAccountId);
        var hasTags = d.Tags.Count > 0;

        if (!hasAccount && !hasTags)
            return "No account, no tag";
        if (hasAccount && hasTags)
            return "Account and tags not mapped";
        if (hasAccount)
            return "Account not mapped";
        return "Tags not matched";
    }

}
