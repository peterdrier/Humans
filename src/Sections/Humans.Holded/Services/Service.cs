using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Models;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Holded.Services;

/// <summary>All cross-section reads come from the cache: zero Holded calls per page view. Nothing loads the whole
/// ledger table: balances and sums are grouped in the database, and statements load only their own entries.</summary>
internal sealed class Service(
    IHoldedMirrorRepository repo,
    IHoldedClient client,
    IHoldedCallLog callLog,
    IClock clock,
    IOptions<HoldedSectionOptions> options,
    ILogger<Service> logger) : IHoldedService, IHoldedAdminService
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    /// <summary>Before the org existed; the full sweep's fixed floor.</summary>
    private static readonly LocalDate LedgerInception = new(2020, 1, 1);

    // 45 days, not a year: the real budget is the plan tier (~2,000 calls/month), and 45 days costs 1-2 pages a night.
    // Older backdating or deletion is caught by the balance reconciliation. Anchored on now, never the newest
    // cached line: the filter is on the accounting date, so a cache-derived anchor would miss an entry backdated
    // to a closed month forever.
    private static readonly Duration TrailingWindow = Duration.FromDays(45);

    private const string PurchaseType = "purchase";
    private const string PaymentType = "payment";

    /// <summary>The connector's ledger-entries page size, for the full-resync cost estimate.</summary>
    private const int LedgerPageSize = 200;

    /// <summary>More drifted accounts than this are reported as mismatches instead (and logged).</summary>
    private const int MaxTargetedRepullsPerRun = 10;

    // One sweep at a time: the admin buttons bypass Hangfire's DisableConcurrentExecution, and two sweeps would
    // race the replace window. Non-blocking so a request never queues behind a full sweep; single server, so an
    // in-process gate is enough.
    private static readonly SemaphoreSlim LedgerSyncGate = new(1, 1);

    public bool IsConfigured => client.IsConfigured;

    public async Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(
        int accountNum, CancellationToken ct = default)
    {
        var lines = await repo.GetLedgerLinesByAccountNumAsync(accountNum, ct: ct);
        return lines.Select(ToInfo).ToList();
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(
        CancellationToken ct = default) =>
        (await repo.GetAccountTotalsAsync(ct)).ToDictionary(t => t.AccountNum, t => t.Balance);

    public async Task<IReadOnlyList<HoldedLedgerLineInfo>> GetEntryAsync(
        int entryNumber, CancellationToken ct = default) =>
        (await repo.GetLedgerLinesByEntryNumbersAsync([entryNumber], ct))
            .OrderBy(l => l.Line)
            .Select(ToInfo)
            .ToList();

    public async Task<IReadOnlyList<HoldedLedgerLineInfo>> GetPurchaseLinesAsync(
        int accountNum, string? descriptionPrefix = null, CancellationToken ct = default) =>
        (await repo.GetLedgerLinesByAccountNumAsync(accountNum, type: PurchaseType, ct: ct))
            .Where(l => descriptionPrefix is null
                        || (l.Description?.StartsWith(descriptionPrefix, StringComparison.Ordinal) ?? false))
            .OrderBy(l => l.Date).ThenBy(l => l.EntryNumber).ThenBy(l => l.Line)
            .Select(ToInfo)
            .ToList();

    public async Task<IReadOnlyDictionary<int, decimal>> GetOpenAmountsAsync(
        int fromAccount, int toAccount, CancellationToken ct = default) =>
        (await repo.GetAccountTypeTotalsAsync(fromAccount, toAccount, ct))
            .Where(t => t.Type is PurchaseType or PaymentType)
            .GroupBy(t => t.AccountNum)
            .ToDictionary(
                g => g.Key,
                // A purchase credits the creditor, a payment debits it.
                g => g.Where(t => string.Equals(t.Type, PurchaseType, StringComparison.Ordinal)).Sum(t => t.Credit - t.Debit)
                     - g.Where(t => string.Equals(t.Type, PaymentType, StringComparison.Ordinal)).Sum(t => t.Debit - t.Credit));

    public async Task<IReadOnlyDictionary<int, decimal>> GetSumsByAccountAsync(
        IReadOnlySet<int> accounts, LocalDate from, LocalDate to, CancellationToken ct = default)
    {
        var sums = await repo.GetSumsByAccountAsync(
            accounts.ToList(), ToWindowStart(from), ToWindowStart(to.PlusDays(1)), ct);
        return accounts.ToDictionary(a => a, a => sums.GetValueOrDefault(a, 0m));
    }

    public async Task<IReadOnlyList<HoldedChartAccountInfo>> GetChartAsync(CancellationToken ct = default) =>
        (await repo.GetAccountsAsync(ct))
            .OrderBy(a => a.Number)
            .Select(a => new HoldedChartAccountInfo(
                a.Number, a.Name, PgcAccountNames.EnglishName(a.Number, a.Name), a.Group, a.Archived, a.Balance))
            .ToList();

    public async Task<bool> SyncLedgerAsync(bool full = false, CancellationToken ct = default)
    {
        if (!await LedgerSyncGate.WaitAsync(0, ct))
        {
            logger.LogInformation("Holded ledger sync already in progress — skipping this request.");
            return false;
        }

        try
        {
            var now = clock.GetCurrentInstant();
            var today = now.InZone(MadridZone).Date;

            var sweepAll = full || !await repo.HasAnyLedgerLinesAsync(ct);
            var state = await repo.GetOrCreateSyncStateAsync(
                sweepAll ? HoldedSyncKind.FullSync : HoldedSyncKind.Ledger, ct);
            state.SyncStatus = HoldedSyncStatus.Running;
            state.StatusChangedAt = now;
            await repo.SaveSyncStateAsync(state, ct);

            try
            {
                var from = sweepAll ? LedgerInception : now.Minus(TrailingWindow).InZone(MadridZone).Date;
                var fetched = await client.ListLedgerEntriesAsync(from, today, ct: ct);
                await repo.ReplaceLedgerWindowAsync(
                    ToWindowStart(from), now, accountNum: null, fetched.Select(l => ToEntity(l, now)).ToList(), now, ct);

                var mismatches = await RefreshAccountsAndReconcileAsync(today, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = fetched.Count;
                // A standing mismatch is state, not a failure: Holded excludes an unconfirmed entry from its own
                // chart totals until it is confirmed.
                state.LastError = mismatches.Count == 0
                    ? null
                    : TruncateForState("Unreconciled after re-pull: " + string.Join("; ",
                        mismatches.Select(m => $"{m.AccountNum}: holded {m.HoldedBalance:0.00}, local {m.LocalBalance:0.00}")));
                await repo.SaveSyncStateAsync(state, ct);

                await DrainCallLogAsync(ct);
                logger.LogInformation(
                    "Holded ledger sync ({Mode}) cached {Count} lines, {Mismatches} account(s) unreconciled",
                    sweepAll ? "full" : "trailing window", fetched.Count, mismatches.Count);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    logger.LogWarning("Holded ledger sync cancelled by the caller");
                else
                    logger.LogError(ex, "Holded ledger sync failed");
                try
                {
                    state.SyncStatus = HoldedSyncStatus.Error;
                    state.LastError = TruncateForState(ex.Message);
                    state.StatusChangedAt = clock.GetCurrentInstant();
                    await repo.SaveSyncStateAsync(state, CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    logger.LogError(saveEx, "Failed to persist ledger-sync error state");
                }
                throw;
            }
        }
        finally
        {
            LedgerSyncGate.Release();
        }
    }

    public async Task<HoldedSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        // Drain first: the usage call below then shows up on the next load, so the meter reports itself.
        await DrainCallLogAsync(ct);

        HoldedUsageDto? usage = null;
        if (client.IsConfigured)
        {
            try
            {
                usage = await client.GetUsageAsync(ct);
            }
            catch (HoldedApiException ex)
            {
                // Holded down, key revoked: the page says "unreachable" and still renders the mirror.
                logger.LogWarning(ex, "Holded usage lookup failed; rendering /Holded from the mirror only");
            }
        }

        var syncStates = (await repo.GetSyncStatesAsync(ct))
            .Select(s => new HoldedSyncStateRow(
                s.Kind.ToString(), s.SyncStatus.ToString(), s.LastSyncAt, s.LastError, s.LastCount))
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();

        var (lineCount, entryCount) = await repo.GetMirrorCountsAsync(ct);
        var rows = await GetAccountRowsAsync(ct);

        return new HoldedSummary(
            IsConfigured: client.IsConfigured,
            ApiReachable: usage is not null,
            Usage: usage,
            MonthlyCallBudget: options.Value.MonthlyCallBudget,
            SyncStates: syncStates,
            LedgerLineCount: lineCount,
            EntryCount: entryCount,
            AccountCount: rows.Count(r => !r.Archived),
            MismatchCount: rows.Count(r => !r.Archived && !r.Reconciled),
            FullSyncCalls: (lineCount + LedgerPageSize - 1) / LedgerPageSize + 1);
    }

    public async Task<IReadOnlyList<HoldedMonthlyCalls>> GetCallsByMonthAsync(CancellationToken ct = default)
    {
        await DrainCallLogAsync(ct);
        return (await repo.GetApiCallsAsync(ct))
            .GroupBy(c =>
            {
                var d = c.CalledAt.InZone(MadridZone).Date;
                return (d.Year, d.Month);
            })
            .Select(m => new HoldedMonthlyCalls(
                m.Key.Year,
                m.Key.Month,
                m.Count(),
                m.GroupBy(c => c.Endpoint, StringComparer.Ordinal)
                    .ToDictionary(e => e.Key, e => e.Count(), StringComparer.Ordinal)))
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month)
            .ToList();
    }

    public async Task<HoldedAccountsPage> GetAccountsPageAsync(
        HoldedAccountsFilter filter, CancellationToken ct = default)
    {
        var rows = await GetAccountRowsAsync(ct);
        if (filter.Mismatched)
            rows = rows.Where(r => !r.Archived && !r.Reconciled).ToList();

        var hiddenArchived = filter.ShowArchived ? 0 : rows.Count(r => r.Archived);
        var visible = rows.Where(r => filter.ShowArchived || !r.Archived).ToList();
        var hiddenEmpty = filter.ShowEmpty ? 0 : visible.Count(r => r.IsEmpty);
        visible = visible.Where(r => filter.ShowEmpty || !r.IsEmpty).ToList();

        var groups = visible
            .GroupBy(r => LeadingDigit(r.Number))
            .OrderBy(g => g.Key)
            .Select(g => new HoldedAccountGroup(
                g.Key, GroupName(g.First().Number), g.ToList(),
                g.Sum(r => r.HoldedBalance), g.Sum(r => r.LocalBalance ?? 0m),
                g.Sum(r => r.LocalLineCount), g.Count(r => !r.Reconciled)))
            .ToList();

        return new HoldedAccountsPage(filter, groups, hiddenArchived, hiddenEmpty);
    }

    public async Task<HoldedAccountStatement?> GetAccountStatementAsync(
        int number, CancellationToken ct = default)
    {
        var lines = await repo.GetLedgerLinesByAccountNumAsync(number, ct: ct);
        var accounts = await repo.GetAccountsAsync(ct);
        var account = accounts.FirstOrDefault(a => a.Number == number);
        if (account is null && lines.Count == 0)
            return null;

        // Archived accounts are kept here, unlike the chart's default: a direct link is a deliberate lookup.
        decimal? localBalanceRaw = lines.Count == 0 ? null : lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        var holdedBalanceRaw = account?.Balance ?? 0m;

        // Counterparties are the entry's other legs, on any account: only this account's entries are loaded.
        var byEntry = (await repo.GetLedgerLinesByEntryNumbersAsync(
                lines.Select(l => l.EntryNumber).Distinct().ToList(), ct))
            .GroupBy(l => l.EntryNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
        var accountsByNumber = accounts.ToDictionary(a => a.Number);

        return new HoldedAccountStatement(
            MakeAccountRow(
                number, account?.Name ?? "", holdedBalanceRaw, localBalanceRaw, lines.Count,
                account is { } a && (a.Debit != 0m || a.Credit != 0m), account?.Archived ?? false),
            lines
                .OrderBy(l => l.Date)
                .ThenBy(l => l.EntryNumber)
                .ThenBy(l => l.Line)
                .Select(l => ToStatementLine(l, number, byEntry, accountsByNumber))
                .ToList());
    }

    public async Task<HoldedEntry?> GetEntryViewAsync(int entryNumber, CancellationToken ct = default)
    {
        var lines = (await repo.GetLedgerLinesByEntryNumbersAsync([entryNumber], ct))
            .OrderBy(l => l.Line)
            .ToList();
        if (lines.Count == 0)
            return null;

        var accountsByNumber = (await repo.GetAccountsAsync(ct)).ToDictionary(a => a.Number);
        return new HoldedEntry(
            entryNumber,
            lines.Select(l => l.EntryId).FirstOrDefault(id => !string.IsNullOrEmpty(id)),
            lines.Select(l =>
            {
                accountsByNumber.TryGetValue(l.AccountNum, out var account);
                return new HoldedEntryLine(
                    l.AccountNum, account?.Name, l.Type, l.Description,
                    ToAssociationPov(l.AccountNum, l.Debit - l.Credit), l.Checked);
            }).ToList());
    }

    /// <summary>Every chart account plus any number that only appears on a cached line, archived included.</summary>
    private async Task<List<HoldedAccountRow>> GetAccountRowsAsync(CancellationToken ct)
    {
        var totals = (await repo.GetAccountTotalsAsync(ct)).ToDictionary(t => t.AccountNum);
        var accounts = await repo.GetAccountsAsync(ct);
        var chartNumbers = accounts.Select(a => a.Number).ToHashSet();

        return accounts
            .Select(a =>
            {
                var hasLines = totals.TryGetValue(a.Number, out var t);
                return MakeAccountRow(
                    a.Number, a.Name, a.Balance,
                    hasLines ? t!.Balance : null,
                    hasLines ? t!.LineCount : 0,
                    a.Debit != 0m || a.Credit != 0m,
                    a.Archived);
            })
            .Concat(totals.Values
                .Where(t => !chartNumbers.Contains(t.AccountNum))
                .Select(t => MakeAccountRow(t.AccountNum, "", 0m, t.Balance, t.LineCount, false, false)))
            .OrderBy(r => r.Number)
            .ToList();
    }

    /// <summary>A drifted account gets one targeted full-history re-pull; accounts still off afterwards are
    /// returned.</summary>
    private async Task<List<(int AccountNum, decimal HoldedBalance, decimal LocalBalance)>>
        RefreshAccountsAndReconcileAsync(LocalDate today, Instant now, CancellationToken ct)
    {
        var accounts = await client.ListAccountingAccountsAsync(ct);
        await repo.UpsertAccountsAsync(accounts.Select(a => new HoldedAccount
        {
            Number = a.Number,
            HoldedId = a.Id,
            Name = a.Name,
            Group = a.Group,
            Debit = a.Debit,
            Credit = a.Credit,
            Balance = a.Balance,
            Archived = a.Archived,
            SyncedAt = now,
        }).ToList(), now, ct);

        var accountsState = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, ct);
        accountsState.SyncStatus = HoldedSyncStatus.Idle;
        accountsState.LastSyncAt = now;
        accountsState.StatusChangedAt = now;
        accountsState.LastCount = accounts.Count;
        accountsState.LastError = null;
        await repo.SaveSyncStateAsync(accountsState, ct);

        var localBalances = await GetAccountBalancesAsync(ct: ct);
        var drifted = accounts
            .Where(a => !a.Archived && a.Balance != localBalances.GetValueOrDefault(a.Number, 0m))
            .ToList();

        // Rotate the start by day so a standing block of drifted accounts can't pin the cap to the same ten forever.
        if (drifted.Count > MaxTargetedRepullsPerRun)
        {
            var offset = (int)(now.ToUnixTimeSeconds() / 86_400 % drifted.Count);
            drifted = [.. drifted.Skip(offset), .. drifted.Take(offset)];
        }

        var mismatches = new List<(int, decimal, decimal)>();
        var repulled = 0;
        foreach (var account in drifted)
        {
            if (repulled >= MaxTargetedRepullsPerRun)
            {
                logger.LogWarning(
                    "Reconciliation hit the {Cap} targeted re-pull cap; {Remaining} drifted account(s) wait for the next run.",
                    MaxTargetedRepullsPerRun, drifted.Count - repulled);
                mismatches.AddRange(drifted.Skip(repulled)
                    .Select(a => (a.Number, a.Balance, localBalances.GetValueOrDefault(a.Number, 0m))));
                break;
            }

            repulled++;
            var fetched = await client.ListLedgerEntriesAsync(LedgerInception, today, account.Number, ct);
            await repo.ReplaceLedgerWindowAsync(
                ToWindowStart(LedgerInception), now, account.Number,
                fetched.Select(l => ToEntity(l, now)).ToList(), now, ct);

            var local = fetched.Sum(l => l.Debit) - fetched.Sum(l => l.Credit);
            if (account.Balance != local)
                mismatches.Add((account.Number, account.Balance, local));
        }

        return mismatches;
    }

    private async Task DrainCallLogAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var records = callLog.DrainAll();
        if (records.Count == 0) return;
        await repo.AddApiCallsAsync(records.Select(r => new HoldedApiCall
        {
            Id = Guid.NewGuid(),
            CalledAt = r.CalledAt,
            Endpoint = r.Endpoint,
            Method = r.Method,
            StatusCode = r.StatusCode,
            RateLimitRemaining = r.RateLimitRemaining,
            RateLimitWindow = r.RateLimitWindow,
        }).ToList(), CancellationToken.None); // Drained records must reach the mirror despite request cancellation.
    }

    /// <summary>LastError is varchar(2000): an unbounded message would fail the save and report a finished refresh
    /// as an error.</summary>
    private static string TruncateForState(string message)
    {
        if (message.Length <= 2000) return message;
        var length = 1997;
        if (char.IsHighSurrogate(message[length - 1]) && char.IsLowSurrogate(message[length]))
            length--;
        return message[..length] + "…";
    }

    /// <summary>From the leading digit, not Holded's <c>group</c> field, which is Spanish free text we would have to
    /// translate by string match.</summary>
    private static string GroupName(int number) =>
        LeadingDigit(number) switch
        {
            1 => "Equity and long-term financing",
            2 => "Non-current assets",
            3 => "Inventory",
            4 => "Receivables and payables",
            5 => "Financial accounts",
            6 => "Purchases and expenses",
            7 => "Sales and income",
            8 => "Expenses charged to equity",
            9 => "Income charged to equity",
            _ => "Unclassified",
        };

    private static int LeadingDigit(int number)
    {
        var leading = Math.Abs(number);
        while (leading >= 10) leading /= 10;
        return leading;
    }

    /// <summary>
    /// + means the association's money went up. Groups 1–5 keep the raw Debit − Credit sign; groups 6–9 carry the
    /// opposite bookkeeping sign, so the display flips it. Display only: reconciliation compares the raw sign.
    /// </summary>
    private static decimal ToAssociationPov(int accountNum, decimal debitMinusCredit) =>
        LeadingDigit(accountNum) is 1 or 2 or 3 or 4 or 5 ? debitMinusCredit : -debitMinusCredit;

    /// <summary>Takes the two raw balances. The POV flip and the reconciliation verdict live in one expression so a
    /// later edit can't flip one without the other: balances flip, the ✓/✗ never does.</summary>
    private static HoldedAccountRow MakeAccountRow(
        int number, string name, decimal holdedBalanceRaw, decimal? localBalanceRaw,
        int lineCount, bool hasHoldedTotals, bool archived) =>
        new(number, name, GroupName(number),
            ToAssociationPov(number, holdedBalanceRaw),
            localBalanceRaw is null ? null : ToAssociationPov(number, localBalanceRaw.Value),
            lineCount, holdedBalanceRaw == (localBalanceRaw ?? 0m), hasHoldedTotals,
            PgcAccountNames.EnglishName(number, name), archived);

    private static HoldedStatementLine ToStatementLine(
        HoldedLedgerLine line, int accountNum,
        IReadOnlyDictionary<int, List<HoldedLedgerLine>> byEntry,
        IReadOnlyDictionary<int, HoldedAccount> accountsByNumber) => new(
        line.EntryNumber, line.Line, line.Date, line.Type, line.Description,
        ToAssociationPov(accountNum, line.Debit - line.Credit),
        ResolveCounterparty(line, byEntry, accountsByNumber), line.EntryId, line.Checked);

    /// <summary>A debit line's counterparties are the entry's credit lines and vice versa: the API returns no contra
    /// account. Null when the entry has no opposing leg (unbalanced or partly mirrored).</summary>
    private static HoldedCounterparty? ResolveCounterparty(
        HoldedLedgerLine line,
        IReadOnlyDictionary<int, List<HoldedLedgerLine>> byEntry,
        IReadOnlyDictionary<int, HoldedAccount> accountsByNumber)
    {
        if (!byEntry.TryGetValue(line.EntryNumber, out var siblings))
            return null;

        var opposing = line.Debit > 0
            ? siblings.Where(s => s.Line != line.Line && s.Credit > 0).ToList()
            : siblings.Where(s => s.Line != line.Line && s.Debit > 0).ToList();
        if (opposing.Count == 0)
            return null;

        var largest = opposing.OrderByDescending(s => Math.Max(s.Debit, s.Credit)).First();
        accountsByNumber.TryGetValue(largest.AccountNum, out var account);
        return new HoldedCounterparty(largest.AccountNum, account?.Name, opposing.Count);
    }

    /// <summary>Local midnight of the start date. Rows dated before it are outside the sweep and must survive.</summary>
    private static Instant ToWindowStart(LocalDate from) =>
        from.AtStartOfDayInZone(MadridZone).ToInstant();

    private static HoldedLedgerLine ToEntity(HoldedLedgerLineDto l, Instant now) => new()
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
        EntryId = l.EntryId,
        Checked = l.Checked,
        CreatedAt = now,
        LastSyncedAt = now,
    };

    private static HoldedLedgerLineInfo ToInfo(HoldedLedgerLine l) => new(
        l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit,
        l.EntryId, l.Checked);
}
