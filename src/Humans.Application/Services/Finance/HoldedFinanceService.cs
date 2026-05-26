using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance.Dtos;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Finance;

/// <summary>
/// Application-layer service for the Holded finance integration.
/// Manages account provisioning, purchase-doc sync, actuals computation, and unmatched reporting.
/// </summary>
public sealed class HoldedFinanceService(
    IHoldedRepository repo,
    IHoldedClient client,
    // Cross-section read via full IBudgetService matches existing FinanceController usage.
    // Future: narrow to an IBudgetServiceRead via the section read/write split.
    IBudgetService budget,
    IClock clock,
    ILogger<HoldedFinanceService> logger) : IHoldedFinanceService
{
    private const int SyncPageSafetyCap = 200;

    // ─── Provisioning ───────────────────────────────────────────────────────────

    public async Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(
        int blockStart, CancellationToken ct = default)
    {
        var year = await budget.GetActiveYearAsync();
        var categories = year is null
            ? Array.Empty<(Guid Id, string Name, string Group)>()
            : year.Groups
                  .SelectMany(g => g.Categories.Select(c => (c.Id, c.Name, Group: g.Name)))
                  .ToArray();

        var map = await repo.GetCategoryMapAsync(ct);
        var activeByCat = map
            .Where(m => m.IsActive)
            .ToDictionary(m => m.BudgetCategoryId);

        // Seed collision avoidance from BOTH the local map and the live Holded chart of
        // accounts, so a number occupied remotely but missing locally — e.g. an account
        // created in Holded whose local map write later failed, or accounts created
        // directly in Holded — is never re-proposed.
        var remoteAccounts = await client.ListExpenseAccountsAsync(ct);
        var usedNumbers = map.Select(m => m.HoldedAccountNumber)
            .Concat(remoteAccounts.Select(a => a.AccountNum))
            .ToHashSet();

        var rows = new List<HoldedProvisioningRow>();
        var usedTags = new HashSet<string>(StringComparer.Ordinal);
        var currentActiveCatIds = categories.Select(c => c.Id).ToHashSet();

        // Track the rolling "next free" number across ToAdd assignments.
        int nextFree = blockStart;

        // Walk categories in stable order: group then category name.
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
                usedTags.Add(existing.Tag);
            }
            else
            {
                var tag = UniqueTag(groupName, catName, catId, usedTags);
                usedTags.Add(tag);

                // Advance nextFree past any already-used numbers.
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

        // Orphans: active map rows whose category no longer exists.
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

        // Final nextFree after all assignments.
        while (usedNumbers.Contains(nextFree))
            nextFree++;

        return new HoldedProvisioningPlan(rows, nextFree);
    }

    public async Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default)
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
                await repo.AddCategoryMapAsync(new HoldedCategoryMap
                {
                    Id = Guid.NewGuid(),
                    BudgetCategoryId = row.BudgetCategoryId,
                    HoldedAccountNumber = row.ProposedAccountNum.Value,
                    HoldedAccountId = id,
                    Tag = row.Tag,
                    IsActive = true,
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

    // ─── Sync ────────────────────────────────────────────────────────────────────

    public async Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var state = await repo.GetSyncStateAsync(ct);
        state.SyncStatus = HoldedSyncStatus.Running;
        state.StatusChangedAt = now;
        await repo.SaveSyncStateAsync(state, ct);

        try
        {
            var map = await repo.GetCategoryMapAsync(ct);
            var entries = map
                .Where(m => m.IsActive)
                .Select(m => new HoldedMatchEntry(m.BudgetCategoryId, m.HoldedAccountId, m.HoldedAccountNumber, m.Tag))
                .ToArray();

            // Page through all purchase documents.
            var allDocs = new List<HoldedPurchaseDocListItemDto>();
            for (var page = 1; page <= SyncPageSafetyCap; page++)
            {
                var pageDocs = await client.ListPurchaseDocumentsPageAsync(page, 100, ct);
                if (pageDocs.Count == 0)
                    break;

                allDocs.AddRange(pageDocs);

                if (page == SyncPageSafetyCap)
                {
                    logger.LogWarning(
                        "HoldedFinanceService.SyncAsync: safety cap of {Cap} pages reached — some docs may be missing",
                        SyncPageSafetyCap);
                }
            }

            var docs = allDocs.Select(doc => MapDoc(doc, entries, now)).ToList();

            await repo.UpsertDocsAsync(docs, now, ct);

            var matched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Matched);
            var unmatched = docs.Count(d => d.MatchStatus == HoldedMatchStatus.Unmatched);

            state.SyncStatus = HoldedSyncStatus.Idle;
            state.LastSyncAt = now;
            state.StatusChangedAt = now;
            state.LastError = null;
            state.LastSyncedDocCount = docs.Count;
            await repo.SaveSyncStateAsync(state, ct);

            return new HoldedSyncResult(docs.Count, matched, unmatched);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HoldedFinanceService.SyncAsync failed");
            state.SyncStatus = HoldedSyncStatus.Error;
            state.LastError = ex.Message;
            state.StatusChangedAt = now;
            try { await repo.SaveSyncStateAsync(state, CancellationToken.None); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist error sync state"); }
            throw;
        }
    }

    private static HoldedExpenseDoc MapDoc(
        HoldedPurchaseDocListItemDto doc,
        HoldedMatchEntry[] entries,
        Instant now)
    {
        // v1 attributes the whole doc by its FIRST line's account (+ union of doc/line tags)
        // and assigns the full doc.Total to that one category. Per spec §6, virtually all
        // purchase docs today are single-line, so this is correct in practice. Line-level
        // attribution (splitting a mixed-account doc's total across categories) is a
        // deliberate later refinement, not a v1 requirement.
        var bookedAccount = doc.Lines.Count > 0 ? doc.Lines[0].AccountId : null;
        var tags = doc.Tags
            .Concat(doc.Lines.SelectMany(l => l.Tags))
            .ToList();

        var matchResult = HoldedMatcher.Match(bookedAccount, tags, entries);

        var localDate = doc.Date
            .InZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"])
            .Date;

        return new HoldedExpenseDoc
        {
            Id = Guid.NewGuid(),
            HoldedDocId = doc.Id,
            DocNumber = doc.DocNumber,
            ContactName = doc.ContactName,
            Date = localDate,
            Subtotal = doc.Subtotal,
            Tax = doc.Tax,
            Total = doc.Total,
            Currency = doc.Currency,
            ApprovedAt = doc.ApprovedAt,
            TagsJson = System.Text.Json.JsonSerializer.Serialize(tags),
            BookedAccountId = bookedAccount,
            BudgetCategoryId = matchResult.CategoryId,
            MatchStatus = matchResult.CategoryId is null
                ? HoldedMatchStatus.Unmatched
                : HoldedMatchStatus.Matched,
            MatchSource = matchResult.Source,
            RawPayload = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ─── Actuals ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(
        int calendarYear, CancellationToken ct = default)
    {
        var docs = await repo.GetMatchedForYearAsync(calendarYear, ct);
        return docs
            .Where(d => d.ApprovedAt is not null && d.BudgetCategoryId is not null)
            .GroupBy(d => d.BudgetCategoryId!.Value)
            .Select(g => new HoldedActualRow(g.Key, g.Sum(x => x.Total), g.Count()))
            .ToList();
    }

    // ─── Unmatched ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        var docs = await repo.GetUnmatchedAsync(ct);
        return docs
            .Select(d => new HoldedUnmatchedRow(
                d.HoldedDocId,
                d.DocNumber,
                d.ContactName,
                d.Total,
                ReasonFor(d),
                // TODO(probe): confirm Holded deep-link URL format
                $"https://app.holded.com/purchases/{d.HoldedDocId}"))
            .ToList();
    }

    private static string ReasonFor(HoldedExpenseDoc d)
    {
        var hasAccount = !string.IsNullOrEmpty(d.BookedAccountId);
        // Tags are stored as JSON; a non-empty array means at least one tag existed.
        var hasTags = d.TagsJson is not null
            && !string.Equals(d.TagsJson, "[]", StringComparison.Ordinal)
            && !string.Equals(d.TagsJson, "null", StringComparison.Ordinal);

        if (!hasAccount && !hasTags)
            return "No account, no tag";
        if (hasAccount && hasTags)
            return "Account and tags not mapped";
        if (hasAccount)
            return "Account not mapped";
        return "Tags not matched";
    }

    // ─── Creditor data (Feature 2) ──────────────────────────────────────────────

    private const int CreditorAccountMin = 40000000;
    private const int CreditorAccountMax = 40000099;

    public async Task SyncCreditorDataAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var epoch = Instant.FromUnixTimeSeconds(0);

        try
        {
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
                // Skip malformed rows: no id/contact, or a missing/epoch-0 date (would store 1970-01-01).
                .Where(p => !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.ContactId)
                         && p.Date != epoch)
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
        catch (Exception ex)
        {
            // Surface the failure in the same sync-state widget the actuals pull uses, then rethrow
            // so Hangfire records the job failure too.
            logger.LogError(ex, "HoldedFinanceService.SyncCreditorDataAsync failed");
            try
            {
                var state = await repo.GetSyncStateAsync(CancellationToken.None);
                state.SyncStatus = HoldedSyncStatus.Error;
                state.LastError = ex.Message;
                state.StatusChangedAt = now;
                await repo.SaveSyncStateAsync(state, CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx, "Failed to persist creditor-sync error state");
            }
            throw;
        }
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

        // Leave Balance null when no balance row is cached — a missing balance is UNKNOWN, not settled.
        // Coercing to 0 would make downstream polling falsely mark reports Paid (Codex P1).
        var balance = balanceRow?.Balance;
        var lastPaymentDate = payments.Length == 0
            ? (LocalDate?)null
            : payments.Max(p => p.Date);

        return new HoldedCreditorStatus(
            SupplierAccountNum: balanceRow?.SupplierAccountNum ?? supplierAccountNum,
            Balance: balance,
            OwedToMember: balance is { } b ? Math.Max(0m, -b) : 0m,
            LastPaymentDate: lastPaymentDate,
            TotalPaid: payments.Sum(p => p.Amount));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a dash-free normalized tag for the given group+category.
    /// If the base tag collides with an already-used one, appends the first 4 hex chars of the category id.
    /// </summary>
    private static string UniqueTag(
        string groupName, string categoryName, Guid categoryId,
        HashSet<string> usedTags)
    {
        var baseTag = HoldedMatcher.NormalizeTag(groupName + categoryName);
        if (!usedTags.Contains(baseTag))
            return baseTag;

        // Disambiguate with first 4 hex chars of the category id.
        return baseTag + categoryId.ToString("N")[..4];
    }
}
