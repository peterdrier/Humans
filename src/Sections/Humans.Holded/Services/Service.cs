using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Models;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Holded.Services;

/// <summary>
/// The ledger mirror: sweeps Holded's daybook into <c>holded_ledger_lines</c> with replace
/// semantics, refreshes the chart-of-accounts cache, reconciles per-account totals, and drains
/// the connector's call log. All cross-section reads are served from the cache — zero Holded
/// calls per page view.
/// </summary>
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

    // 45 days, not a year: the API's real free budget is the plan tier (~2,000 calls/month —
    // GET /usage's 2M "limit" is a billable-overage ceiling). A year-wide window costs ~20
    // pages/night; 45 days costs 1–2. Backdating or deletion OLDER than the window is caught by
    // the balance reconciliation below at one call/night, so correctness no longer depends on
    // window width. Anchored on *now*, never on the newest cached line — the date filter is on
    // the accounting date, so an entry posted today but dated to a closed month would sit behind
    // a cache-derived anchor forever (#1241).
    private static readonly Duration TrailingWindow = Duration.FromDays(45);

    /// <summary>Reconciliation re-pulls are cheap but not free; a run that finds more drifted
    /// accounts than this reports the rest as mismatches instead (no silent caps — logged).</summary>
    private const int MaxTargetedRepullsPerRun = 10;

    // One sweep at a time. Hangfire's DisableConcurrentExecution guards the nightly job, but the
    // admin buttons call the service directly, so two sweeps can interleave and race the replace
    // window. Non-blocking: a web request must not queue behind a running full sweep — the caller
    // reports the skip instead. Single-server deployment, so an in-process gate is the whole
    // requirement (#1241).
    private static readonly SemaphoreSlim LedgerSyncGate = new(1, 1);

    public async Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(
        int accountNum, CancellationToken ct = default)
    {
        var lines = await repo.GetLedgerLinesByAccountNumAsync(accountNum, ct);
        return lines.Select(ToInfo).ToList();
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(
        int? calendarYear = null, CancellationToken ct = default)
    {
        var lines = await repo.GetAllLedgerLinesAsync(ct);
        return lines
            .Where(l => calendarYear is null || l.Date.InZone(MadridZone).Year == calendarYear)
            .GroupBy(l => l.AccountNum)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit) - g.Sum(l => l.Credit));
    }

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
                // A standing mismatch is reportable state, not a failure: live example — entry
                // #2412 (352,234.31) is excluded from Holded's own chart totals, so 57200001
                // legitimately reads as drifted until Holded confirms the entry.
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

    public async Task<HoldedAdminOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        // Drain first: the usage call below lands in the log and shows up on the next load,
        // which is what makes the meter self-reporting rather than a call that hides itself.
        await DrainCallLogAsync(ct);

        HoldedUsageDto? usage = null;
        try
        {
            usage = await client.GetUsageAsync(ct);
        }
        catch (HoldedApiException ex)
        {
            // No key, Holded down, key revoked — the mirror still renders in full. The page says
            // "unreachable" rather than 500ing on the one live call it makes.
            logger.LogWarning(ex, "Holded usage lookup failed; rendering /Holded from the mirror only");
        }

        var apiCalls = await repo.GetApiCallsAsync(ct);
        var callsByMonth = apiCalls
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

        var syncStates = (await repo.GetSyncStatesAsync(ct))
            .Select(s => new HoldedSyncStateRow(
                s.Kind.ToString(), s.SyncStatus.ToString(), s.LastSyncAt, s.LastError, s.LastCount))
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();

        var lines = await repo.GetAllLedgerLinesAsync(ct);
        var local = lines
            .GroupBy(l => l.AccountNum)
            .ToDictionary(
                g => g.Key,
                g => (Balance: g.Sum(l => l.Debit) - g.Sum(l => l.Credit), Count: g.Count()));

        var accounts = (await repo.GetAccountsAsync(ct))
            .Where(a => !a.Archived)
            .OrderBy(a => a.Number)
            .Select(a =>
            {
                var hasLines = local.TryGetValue(a.Number, out var cached);
                decimal? localBalance = hasLines ? cached.Balance : null;
                // Reconciled compares the raw (Debit − Credit) convention — the same one Holded's
                // own chart balance is in — never the POV-flipped display value below.
                var reconciled = a.Balance == (localBalance ?? 0m);
                return new HoldedAccountRow(
                    a.Number, a.Name, GroupName(a.Number),
                    ToAssociationPov(a.Number, a.Balance),
                    localBalance is null ? null : ToAssociationPov(a.Number, localBalance.Value),
                    hasLines ? cached.Count : 0, reconciled,
                    a.Debit != 0m || a.Credit != 0m);
            })
            .ToList();

        return new HoldedAdminOverview(
            ApiReachable: usage is not null,
            Usage: usage,
            MonthlyCallBudget: options.Value.MonthlyCallBudget,
            CallsByMonth: callsByMonth,
            SyncStates: syncStates,
            Accounts: accounts,
            LedgerLineCount: lines.Count);
    }

    public async Task<HoldedAccountStatement?> GetAccountStatementAsync(
        int number, CancellationToken ct = default)
    {
        var lines = await repo.GetLedgerLinesByAccountNumAsync(number, ct);
        var accounts = await repo.GetAccountsAsync(ct);
        var account = accounts.FirstOrDefault(a => a.Number == number);
        if (account is null && lines.Count == 0)
            return null;

        // Archived accounts are not filtered out here the way the overview filters them: a direct
        // link to one is a deliberate lookup, and its history is still the answer.
        decimal? localBalanceRaw = lines.Count == 0 ? null : lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
        var holdedBalanceRaw = account?.Balance ?? 0m;
        // Reconciled compares the raw convention, never the POV-flipped display value below.
        var reconciled = holdedBalanceRaw == (localBalanceRaw ?? 0m);

        // Counterparties are resolved from the entry's OTHER legs, which can post to any account —
        // not just this one — so the lookup needs the whole mirror, not this account's slice of it.
        var byEntry = (await repo.GetAllLedgerLinesAsync(ct))
            .GroupBy(l => l.EntryNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
        var accountsByNumber = accounts.ToDictionary(a => a.Number);

        return new HoldedAccountStatement(
            new HoldedAccountRow(
                number, account?.Name ?? "", GroupName(number),
                ToAssociationPov(number, holdedBalanceRaw),
                localBalanceRaw is null ? null : ToAssociationPov(number, localBalanceRaw.Value),
                lines.Count, reconciled,
                account is { } a && (a.Debit != 0m || a.Credit != 0m)),
            lines
                .OrderBy(l => l.Date)
                .ThenBy(l => l.EntryNumber)
                .ThenBy(l => l.Line)
                .Select(l => ToStatementLine(l, number, byEntry, accountsByNumber))
                .ToList());
    }

    public async Task<HoldedEntry?> GetEntryAsync(int entryNumber, CancellationToken ct = default)
    {
        var lines = (await repo.GetAllLedgerLinesAsync(ct))
            .Where(l => l.EntryNumber == entryNumber)
            .OrderBy(l => l.Line)
            .ToList();
        if (lines.Count == 0)
            return null;

        var accountsByNumber = (await repo.GetAccountsAsync(ct)).ToDictionary(a => a.Number);
        return new HoldedEntry(entryNumber, lines.Select(l =>
        {
            accountsByNumber.TryGetValue(l.AccountNum, out var account);
            return new HoldedEntryLine(
                l.AccountNum, account?.Name, l.Type, l.Description,
                ToAssociationPov(l.AccountNum, l.Debit - l.Credit));
        }).ToList());
    }

    /// <summary>Refreshes the chart cache, then compares every non-archived account's Holded
    /// balance against the local ledger sum. A drifted account gets one targeted full-history
    /// re-pull (replace semantics); accounts still off afterwards are returned.</summary>
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

        // Rotate the starting point by day so a standing block of more-than-cap drifted accounts
        // (e.g. entries Holded itself excludes from chart totals) cannot pin the cap to the same
        // first ten forever and starve the rest of their targeted re-pull.
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
        }).ToList(), ct);
    }

    /// <summary>The replace window's lower bound as an Instant: local midnight of the sweep's
    /// start date. Rows dated before this are outside the sweep and must survive it.</summary>
    /// <summary>LastError is varchar(2000); an unbounded mismatch list or exception message
    /// would fail the save and report a completed refresh as an error.</summary>
    private static string TruncateForState(string message) =>
        message.Length <= 2000 ? message : message[..1997] + "…";

    /// <summary>
    /// The Spanish PGC group an account belongs to, in English. Read from the account number's
    /// leading digit rather than Holded's own <c>group</c> field, which returns Spanish
    /// ("Financiación básica", "Acreedores y deudores operaciones de la actividad") and is
    /// free text we would be translating by string match. The digit is the definition.
    /// </summary>
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

    /// <summary>The account number's leading digit — the Spanish PGC group.</summary>
    private static int LeadingDigit(int number)
    {
        var leading = Math.Abs(number);
        while (leading >= 10) leading /= 10;
        return leading;
    }

    /// <summary>
    /// The association's own point of view: + means its money went up, − means it went down.
    /// Groups 1–5 (equity, assets, banks, debtors, creditors) keep the raw Debit − Credit sign;
    /// groups 6–9 (expenses, income, and their equity-charged counterparts) carry the opposite
    /// bookkeeping sign, so the display flips it. Display-only: reconciliation always compares
    /// the raw (Debit − Credit) convention, before this flip is applied.
    /// </summary>
    private static decimal ToAssociationPov(int accountNum, decimal debitMinusCredit) =>
        LeadingDigit(accountNum) is 1 or 2 or 3 or 4 or 5 ? debitMinusCredit : -debitMinusCredit;

    private static HoldedStatementLine ToStatementLine(
        HoldedLedgerLine line, int accountNum,
        IReadOnlyDictionary<int, List<HoldedLedgerLine>> byEntry,
        IReadOnlyDictionary<int, HoldedAccount> accountsByNumber) => new(
        line.EntryNumber, line.Line, line.Date, line.Type, line.Description,
        ToAssociationPov(accountNum, line.Debit - line.Credit),
        ResolveCounterparty(line, byEntry, accountsByNumber));

    /// <summary>The entry's opposing-side legs: a debit line's counterparties are the entry's
    /// credit lines (and vice versa) — Holded's API does not return a contra account, so this is
    /// derived by grouping on <see cref="HoldedLedgerLine.EntryNumber"/>. Null when the entry
    /// carries no opposing leg (an unbalanced or partially-mirrored entry).</summary>
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
        CreatedAt = now,
        LastSyncedAt = now,
    };

    private static HoldedLedgerLineInfo ToInfo(HoldedLedgerLine l) => new(
        l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit);
}
