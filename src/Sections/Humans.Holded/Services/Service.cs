using Humans.Application.Interfaces.Holded;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Microsoft.Extensions.Logging;
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
    ILogger<Service> logger) : IHoldedService
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
                    : "Unreconciled after re-pull: " + string.Join("; ",
                        mismatches.Select(m => $"{m.AccountNum}: holded {m.HoldedBalance:0.00}, local {m.LocalBalance:0.00}"));
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
                    state.LastError = ex.Message;
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
