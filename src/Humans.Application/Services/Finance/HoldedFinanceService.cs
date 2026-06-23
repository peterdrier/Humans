using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Gdpr;
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
    ILogger<HoldedFinanceService> logger) : IHoldedFinanceService, IUserDataContributor
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

    // Holded caps a dailyledger window at one year; 364 days stays safely under.
    private static readonly Duration LedgerWindow = Duration.FromDays(364);
    // Backstop on the first-run backward sweep (~25 years); logged if hit so a cap is never silent.
    private const int BackfillWindowCap = 25;

    public async Task SyncCreditorLedgerAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        try
        {
            var latest = await repo.GetLatestLedgerLineDateAsync(ct);

            // First run (empty cache) → full-history backfill in ≤1-year backward windows.
            // Steady state → incremental append from the latest cached line forward, also in ≤1-year
            // windows so a long gap (sync disabled or no creditor activity for >1 year) still catches up
            // instead of failing on an over-wide request. Idempotent upsert on (EntryNumber, Line) makes
            // re-fetching the boundary safe.
            var fetched = latest is null
                ? await BackfillLedgerAsync(now, ct)
                : await IncrementalLedgerAsync(latest.Value, now, ct);

            // Persist only creditor-account (400000xx) lines — the only accounts the read paths derive
            // from. The dailyledger has no server-side account filter, so the fetch sweeps the whole
            // daybook regardless; we keep the subset we use. (Budget actuals run on a separate path.)
            var lines = fetched
                .Where(l => l.AccountNum >= CreditorAccountMin && l.AccountNum <= CreditorAccountMax)
                .Select(l => new HoldedLedgerLine
                {
                    Id = Guid.NewGuid(),
                    EntryNumber = l.EntryNumber,
                    Line = l.Line,
                    AccountNum = l.AccountNum,
                    Date = l.Date,
                    Type = l.Type,
                    Description = l.Description,
                    Debit = l.Debit,
                    Credit = l.Credit,
                    CreatedAt = now,
                    LastSyncedAt = now,
                })
                .ToList();

            await repo.UpsertLedgerLinesAsync(lines, now, ct);

            logger.LogInformation(
                "Holded ledger sync ({Mode}) cached {Count} creditor journal lines",
                latest is null ? "backfill" : "incremental", lines.Count);
        }
        catch (Exception ex)
        {
            // Surface the failure in the same sync-state widget the actuals pull uses, then rethrow
            // so Hangfire records the job failure too.
            logger.LogError(ex, "HoldedFinanceService.SyncCreditorLedgerAsync failed");
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

    /// <summary>Sweeps inception→now in ≤1-year backward windows, stopping at the first empty window
    /// (the org's books are contiguous back to inception). Logs if the window cap is hit (no silent caps).</summary>
    private async Task<IReadOnlyList<HoldedLedgerLineDto>> BackfillLedgerAsync(Instant now, CancellationToken ct)
    {
        var all = new List<HoldedLedgerLineDto>();
        var to = now;
        for (var window = 0; window < BackfillWindowCap; window++)
        {
            var from = to.Minus(LedgerWindow);
            var page = await client.ListDailyLedgerAsync(from, to, ct);
            if (page.Count == 0)
                return all;
            all.AddRange(page);
            to = from.Minus(Duration.FromSeconds(1));
        }

        logger.LogWarning(
            "Holded ledger backfill hit the {Cap}-window cap; journal history older than {To} was not swept.",
            BackfillWindowCap, to);
        return all;
    }

    /// <summary>Sweeps forward from the latest cached line in ≤1-year windows (the API rejects wider
    /// ranges). In steady state this is a single window; after a long dormancy it chunks so the sync
    /// catches up rather than failing on an over-wide request.</summary>
    private async Task<IReadOnlyList<HoldedLedgerLineDto>> IncrementalLedgerAsync(Instant from, Instant now, CancellationToken ct)
    {
        var all = new List<HoldedLedgerLineDto>();
        var windowStart = from;
        while (windowStart < now)
        {
            var windowEnd = windowStart.Plus(LedgerWindow);
            if (windowEnd > now) windowEnd = now;
            all.AddRange(await client.ListDailyLedgerAsync(windowStart, windowEnd, ct));
            windowStart = windowEnd.Plus(Duration.FromSeconds(1));
        }
        return all;
    }

    public async Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default)
    {
        if (supplierAccountNum is not { } num)
            return null;

        var lines = await repo.GetLedgerLinesByAccountNumAsync(num, ct);
        if (lines.Count == 0)
            return null;

        var balance = LedgerBalance(lines);
        var payments = LedgerPayments(lines);

        return new HoldedCreditorStatus(
            SupplierAccountNum: num,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            LastPaymentDate: payments.Count == 0 ? null : payments.Max(p => p.Date),
            TotalPaid: payments.Sum(p => p.Amount),
            Payments: payments);
    }

    // ── Ledger derivations (sign confirmed against live data: Daniela 40000001
    //    credit 12720 − debit 9540 = 3180 owed; chart showed −3180) ──────────────
    //    balance = Σdebit − Σcredit (negative = org owes); owed = max(0, −balance); payments = debit lines.

    private static decimal LedgerBalance(IReadOnlyCollection<HoldedLedgerLine> lines) =>
        lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

    private static List<HoldedPaymentInfo> LedgerPayments(IEnumerable<HoldedLedgerLine> lines)
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        return lines
            .Where(l => l.Debit > 0m)
            .Select(l => new HoldedPaymentInfo(l.Date.InZone(zone).Date, l.Debit, l.Type))
            .ToList();
    }

    // ─── Creditor bindings + statement ──────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedCreditorAccountRow>> ListCreditorAccountsAsync(
        CancellationToken ct = default)
    {
        var byAccount = (await repo.GetAllLedgerLinesAsync(ct))
            .GroupBy(l => l.AccountNum)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<HoldedLedgerLine>)g.ToList());

        // Group-by-first, not ToDictionary: only UserId is unique in the DB, so two members
        // could (mis)bind to the same account number — that must not throw the whole list.
        var bindings = (await repo.GetCreditorContactsAsync(ct))
            .Where(b => b.SupplierAccountNum is not null)
            .GroupBy(b => b.SupplierAccountNum!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Every creditor account with ledger activity, plus bound accounts that have no lines yet.
        // Name (the Holded chart-account label) is no longer cached; the bound member's name is
        // resolved by the controller for display.
        return byAccount.Keys.Union(bindings.Keys)
            .Select(num =>
            {
                decimal? balance = byAccount.TryGetValue(num, out var lines) ? LedgerBalance(lines) : null;
                bindings.TryGetValue(num, out var binding);
                return new HoldedCreditorAccountRow(
                    SupplierAccountNum: num,
                    Name: "",
                    Balance: balance,
                    OwedToMember: balance is { } b ? Math.Max(0m, -b) : 0m,
                    BoundUserId: binding?.UserId,
                    BindingSource: binding?.Source);
            }).ToList();
    }

    public async Task<CreditorContactBinding?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var b = await repo.GetCreditorContactByUserAsync(userId, ct);
        return b is null
            ? null
            : new CreditorContactBinding(b.UserId, b.HoldedContactId, b.SupplierAccountNum, b.Source);
    }

    public async Task<bool> SetCreditorContactAsync(
        Guid userId, int supplierAccountNum, CancellationToken ct = default)
    {
        var contact = (await client.ListContactsAsync(ct))
            .FirstOrDefault(c => c.SupplierAccountNum == supplierAccountNum);
        if (contact is null) return false;

        var now = clock.GetCurrentInstant();
        await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contact.Id,
            SupplierAccountNum = supplierAccountNum,
            Source = CreditorContactSource.Manual,
            CreatedAt = now,
            UpdatedAt = now,
        }, now, ct);
        return true;
    }

    public async Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(
        int supplierAccountNum, CancellationToken ct = default)
    {
        // Reads cached daybook lines only — zero Holded calls per view.
        var lines = await repo.GetLedgerLinesByAccountNumAsync(supplierAccountNum, ct);
        if (lines.Count == 0)
            return null;

        var balance = LedgerBalance(lines);
        return new HoldedCreditorLedger(
            SupplierAccountNum: supplierAccountNum,
            Name: null,
            Balance: balance,
            OwedToMember: Math.Max(0m, -balance),
            Lines: lines.Select(l => new HoldedLedgerLineDto
            {
                EntryNumber = l.EntryNumber,
                Line = l.Line,
                Date = l.Date,
                AccountNum = l.AccountNum,
                Debit = l.Debit,
                Credit = l.Credit,
                Type = l.Type,
                Description = l.Description,
            }).ToList());
    }

    public async Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        // Reuse the bound contact, else lazy-seed from the report's previously-cached contact id.
        var existingContactId = !string.IsNullOrEmpty(binding?.HoldedContactId)
            ? binding.HoldedContactId
            : (string.IsNullOrEmpty(seedContactId) ? null : seedContactId);

        // Burner goes in tradeName only — and only when it differs from the official legal name.
        var tradeName = !string.IsNullOrWhiteSpace(burnerName)
                        && !string.Equals(burnerName, legalName, StringComparison.Ordinal)
            ? burnerName
            : null;

        var contactId = await client.UpsertContactAsync(new HoldedContactInput
        {
            Name = legalName,
            TradeName = tradeName,
            CustomId = userId.ToString(),
            Type = "creditor",
            Iban = string.IsNullOrWhiteSpace(iban) ? null : iban,
            ExistingContactId = existingContactId,
        }, ct);

        var now = clock.GetCurrentInstant();
        await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),                                   // ignored on update (keyed by UserId)
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = binding?.SupplierAccountNum ?? seedAccountNum,
            Source = binding?.Source ?? CreditorContactSource.Auto, // preserve a Manual binding
            CreatedAt = now,
            UpdatedAt = now,
        }, now, ct);

        return contactId;
    }

    public async Task SetCreditorAccountNumAsync(
        Guid userId, int supplierAccountNum, CancellationToken ct = default)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        if (binding is null) return;

        var now = clock.GetCurrentInstant();
        await repo.UpsertCreditorContactAsync(new HoldedCreditorContact
        {
            Id = binding.Id,
            UserId = userId,
            HoldedContactId = binding.HoldedContactId,
            SupplierAccountNum = supplierAccountNum,
            Source = binding.Source,
            CreatedAt = binding.CreatedAt,
            UpdatedAt = now,
        }, now, ct);
    }

    // ─── GDPR (Article 15 export) ───────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var binding = await repo.GetCreditorContactByUserAsync(userId, ct);
        return
        [
            new UserDataSlice(GdprExportSections.HoldedCreditorAccount,
                binding is null
                    ? null
                    : new
                    {
                        binding.SupplierAccountNum,
                        binding.HoldedContactId,
                        Source = binding.Source.ToString(),
                    }),
        ];
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
