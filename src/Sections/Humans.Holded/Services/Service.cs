using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Models;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Interfaces;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

/// <summary>All cross-section reads come from the cache: zero Holded calls per page view. Nothing loads the whole
/// ledger table: balances are grouped in the database, and statements load only their own entries.</summary>
internal sealed class Service(
    IHoldedMirrorRepository repo,
    IHoldedClient client,
    IHoldedCallLog callLog,
    IClock clock,
    IOptions<HoldedSectionOptions> options,
    ILogger<Service> logger) : IHoldedService, IHoldedAdminService
{
    /// <summary>The connector's ledger-entries page size, for the full-resync cost estimate.</summary>
    private const int LedgerPageSize = 200;

    public bool IsConfigured => client.IsConfigured;

    public async Task<HoldedSalesDocumentDto> GetSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default)
    {
        var document = await client.GetSalesDocumentAsync(kind, documentId, ct);
        if (document.ContactId is null) return document;
        var contact = await client.GetContactAsync(document.ContactId, ct);
        return document with { ContactAccountNum = contact.ClientAccountNum };
    }

    public Task<byte[]> GetSalesDocumentPdfAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default) =>
        client.GetSalesDocumentPdfAsync(kind, documentId, ct);

    public async Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(
        int accountNum, CancellationToken ct = default)
    {
        var lines = await repo.GetLedgerLinesByAccountNumAsync(accountNum, ct: ct);
        return lines.Select(ToInfo).ToList();
    }

    public async Task<Instant?> GetLedgerSyncedAtAsync(CancellationToken ct = default) =>
        (await repo.GetSyncStatesAsync(ct))
            .Where(s => s.Kind is HoldedSyncKind.Ledger or HoldedSyncKind.FullSync)
            .Max(s => s.LastSyncAt);

    public async Task<IReadOnlyList<HoldedChartAccountInfo>> GetChartAsync(CancellationToken ct = default) =>
        (await repo.GetAccountsAsync(ct))
            .Select(a => new HoldedChartAccountInfo(
                a.Number, a.Name, PgcAccountNames.EnglishName(a.Number, a.Name), a.Group, a.Archived, a.Balance))
            .ToList();

    public async Task<HoldedPurchaseDocMirror> GetPurchaseDocumentsAsync(CancellationToken ct = default)
    {
        var state = (await repo.GetSyncStatesAsync(ct)).FirstOrDefault(s => s.Kind == HoldedSyncKind.PurchaseDocs);
        var docs = await repo.GetPurchaseDocsAsync(ct);
        return new HoldedPurchaseDocMirror(
            state?.LastSyncAt,
            (state?.SyncStatus ?? HoldedSyncStatus.Idle).ToString(),
            state?.LastError,
            state?.LastCount ?? 0,
            state?.StatusChangedAt,
            docs.Select(d => new HoldedPurchaseDocInfo(
                    d.Id, d.DocNumber, d.ContactName, d.ContactId, d.Description, d.Date, d.Subtotal, d.Tax, d.Total,
                    d.Currency, d.IsApproved, d.Notes, d.Tags, d.BookedAccountId, PurchaseDocUrl(d.Id),
                    d.PaymentsPending))
                .ToList());
    }

    public async Task<HoldedContactMirror> GetContactsAsync(CancellationToken ct = default)
    {
        var state = (await repo.GetSyncStatesAsync(ct)).FirstOrDefault(s => s.Kind == HoldedSyncKind.Contacts);
        return new HoldedContactMirror(state?.LastSyncAt, (await repo.GetContactsAsync(ct))
            .Select(c => new HoldedContactDto
            {
                Id = c.Id,
                Name = c.Name,
                SupplierAccountNum = c.SupplierAccountNum,
                TradeName = c.TradeName,
                Email = c.Email,
                Phone = c.Phone,
                Mobile = c.Mobile,
                Iban = c.Iban,
                TaxCode = c.TaxCode,
                Address = c.Address,
            })
            .ToList());
    }

    public async Task<HoldedBankMovementMirror> GetBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default)
    {
        var state = (await repo.GetSyncStatesAsync(ct)).FirstOrDefault(s => s.Kind == HoldedSyncKind.BankMovements);
        var rows = await repo.GetBankMovementsAsync(treasuryAccountId, from, to, ct);
        return new HoldedBankMovementMirror(state?.LastSyncAt, rows
            .Select(m => new HoldedBankMovementDto
            {
                Id = m.Id,
                Date = m.Date,
                BookedAt = m.BookedAt,
                Amount = m.Amount,
                Description = m.Description,
                Status = m.Status,
            })
            .ToList());
    }

    // Holded has no stable per-document page; its purchase list opens one from this fragment.
    private static string PurchaseDocUrl(string id) => $"https://app.holded.com/expenses/list#open:purchase-{id}";

    public async Task<HoldedSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await DrainCallLogAsync(ct);

        var states = await repo.GetSyncStatesAsync(ct);
        var usage = states.FirstOrDefault(s => s.Kind == HoldedSyncKind.Usage);
        var syncStates = states
            .Where(s => s.Kind != HoldedSyncKind.Usage)
            .Select(s => new HoldedSyncStateRow(
                s.Kind.ToString(), s.SyncStatus.ToString(), s.LastSyncAt, s.LastError, s.LastCount))
            .ToList();

        var (lineCount, entryCount) = await repo.GetMirrorCountsAsync(ct);
        var rows = await GetAccountRowsAsync(ct);

        return new HoldedSummary(
            IsConfigured: client.IsConfigured,
            Usage: usage is null ? null : ToReading(usage),
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
                var d = c.CalledAt.InZone(HoldedJson.MadridZone).Date;
                return (d.Year, d.Month);
            })
            .Select(m => new HoldedMonthlyCalls(
                m.Key.Year,
                m.Key.Month,
                m.Count(),
                m.GroupBy(c => c.Endpoint, StringComparer.Ordinal)
                    .ToDictionary(e => e.Key, e => e.Count(), StringComparer.Ordinal)))
            .ToList();
    }

    // The period is the Madrid month of the read: Holded reports the current month.
    private static HoldedUsageReading ToReading(HoldedSyncState s)
    {
        var month = s.LastSyncAt?.InZone(HoldedJson.MadridZone).Date;
        return new HoldedUsageReading(
            s.LastCount, month is { } d ? $"{d.Year:D4}-{d.Month:D2}" : null, s.LastSyncAt,
            s.SyncStatus == HoldedSyncStatus.Error ? s.LastError : null);
    }

    public async Task<HoldedSyncNowResult> SyncNowAsync(CancellationToken ct = default)
    {
        var docs = await TrySyncAsync("purchase documents", () => SyncPurchaseDocumentsAsync(ct), ct);
        var contacts = await TrySyncAsync("contacts", () => SyncContactsAsync(ct), ct);
        var ledger = await TrySyncAsync("ledger", () => SyncLedgerAsync(full: false, ct), ct);
        var outcomes = new[] { docs, contacts, ledger };
        return new HoldedSyncNowResult(
            outcomes.Where(o => o.Ran is null).Select(o => o.Name).ToList(),
            outcomes.All(o => o.Ran == true));
    }

    /// <summary>Ran is null when the sync threw.</summary>
    private async Task<(string Name, bool? Ran)> TrySyncAsync(string name, Func<Task<bool>> sync, CancellationToken ct)
    {
        try
        {
            return (name, await sync());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Manual Holded {Sync} sync cancelled by the caller", name);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual Holded {Sync} sync failed", name);
            return (name, null);
        }
    }

    private static HoldedLedgerLineInfo ToInfo(HoldedLedgerLine l) => new(
        l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit,
        l.EntryId, l.Checked);

    public async Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(
        CancellationToken ct = default) =>
        (await repo.GetAccountTotalsAsync(ct)).ToDictionary(t => t.AccountNum, t => t.Balance);

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
            .Select(g => new HoldedAccountGroup(
                g.Key, GroupName(g.First().Number), g.ToList(),
                g.Sum(r => r.HoldedBalance), g.Sum(r => r.LocalBalance ?? 0m),
                g.Sum(r => r.LocalLineCount), g.Count(r => !r.Reconciled),
                g.GroupBy(r => PgcAccountNames.Subgroup(r.Number))
                    .Select(sg => new HoldedAccountSubgroup(
                        sg.Key, PgcAccountNames.SubgroupName(sg.Key), sg.ToList(),
                        sg.Sum(r => r.HoldedBalance), sg.Sum(r => r.LocalBalance ?? 0m),
                        sg.Sum(r => r.LocalLineCount), sg.Count(r => !r.Reconciled)))
                    .ToList()))
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
    internal async Task<List<HoldedAccountRow>> GetAccountRowsAsync(CancellationToken ct)
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
            .ToList();
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

    /// <summary>Before the org existed; the full sweep's fixed floor.</summary>
    private static readonly LocalDate LedgerInception = new(2020, 1, 1);

    // 45 days, not a year: the real budget is the plan tier (~2,000 calls/month), and 45 days costs 1-2 pages a night.
    // Older backdating or deletion is caught by the balance reconciliation. Anchored on now, never the newest
    // cached line: the filter is on the accounting date, so a cache-derived anchor would miss an entry backdated
    // to a closed month forever.
    private static readonly Duration TrailingWindow = Duration.FromDays(45);

    /// <summary>More drifted accounts than this are reported as mismatches instead (and logged).</summary>
    private const int MaxTargetedRepullsPerRun = 10;

    // One sweep at a time: the admin buttons bypass Hangfire's DisableConcurrentExecution, and two sweeps would
    // race the replace window. Non-blocking so a request never queues behind a full sweep; single server, so an
    // in-process gate is enough.
    private static readonly SemaphoreSlim LedgerSyncGate = new(1, 1);

    // Same reasoning, one gate per sync: each writes its own table.
    private static readonly SemaphoreSlim PurchaseDocSyncGate = new(1, 1);
    private static readonly SemaphoreSlim ContactSyncGate = new(1, 1);
    private static readonly SemaphoreSlim BankMovementSyncGate = new(1, 1);

    // The plan allows ~2,000 Holded calls a month: 50 a night is at most ~1,500, leaving the rest for the ledger sweep,
    // the list call and the pushes. A backlog drains over several nights, newest documents first.
    private const int MaxNotesFetchesPerRun = 50;

    public async Task SyncChartAsync(CancellationToken ct = default)
    {
        await RefreshChartAsync(clock.GetCurrentInstant(), ct);
        await DrainCallLogAsync(CancellationToken.None);
    }

    /// <summary>One list call. The upsert never drops an account, so one Holded stops listing stays mirrored.</summary>
    private async Task<IReadOnlyList<HoldedAccountDto>> RefreshChartAsync(Instant now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var accounts = await client.ListAccountingAccountsAsync(ct);
        ct.ThrowIfCancellationRequested();
        await repo.UpsertAccountsAsync(accounts.Select(a => ToAccountEntity(a, now)).ToList(), now, ct);

        try
        {
            var accountsState = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, CancellationToken.None);
            accountsState.SyncStatus = HoldedSyncStatus.Idle;
            accountsState.LastSyncAt = now;
            accountsState.StatusChangedAt = now;
            accountsState.LastCount = accounts.Count;
            accountsState.LastError = null;
            await SaveCompletedSyncStateAsync(accountsState);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("Holded chart completion failed after account upsert.", ex);
        }
        return accounts;
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
            try
            {
                await repo.SaveSyncStateAsync(state, ct);
                var from = sweepAll ? LedgerInception : now.Minus(TrailingWindow).InZone(MadridZone).Date;
                var fetched = await client.ListLedgerEntriesAsync(from, today, ct: ct);
                ct.ThrowIfCancellationRequested();
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
                await SaveCompletedSyncStateAsync(state);

                await DrainCallLogAsync(CancellationToken.None);
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
                await PersistSyncFailureAsync(state, ex);
                throw;
            }
        }
        finally
        {
            LedgerSyncGate.Release();
        }
    }

    public async Task<bool> SyncAccountLedgerAsync(int accountNum, CancellationToken ct = default)
    {
        if (!await LedgerSyncGate.WaitAsync(0, ct))
        {
            logger.LogInformation("Holded ledger sync already in progress — skipping the {Account} re-pull.", accountNum);
            return false;
        }

        try
        {
            var now = clock.GetCurrentInstant();
            var fetched = await client.ListLedgerEntriesAsync(
                LedgerInception, now.InZone(MadridZone).Date, accountNum, ct);
            ct.ThrowIfCancellationRequested();
            await repo.ReplaceLedgerWindowAsync(
                ToWindowStart(LedgerInception), now, accountNum, fetched.Select(l => ToEntity(l, now)).ToList(), now, ct);
            await DrainCallLogAsync(CancellationToken.None);
            return true;
        }
        finally
        {
            LedgerSyncGate.Release();
        }
    }

    public async Task<bool> SyncPurchaseDocumentsAsync(CancellationToken ct = default)
    {
        if (!await PurchaseDocSyncGate.WaitAsync(0, ct))
        {
            logger.LogInformation("Holded purchase-document sync already in progress — skipping this request.");
            return false;
        }

        try
        {
            var now = clock.GetCurrentInstant();
            var state = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.PurchaseDocs, ct);
            state.SyncStatus = HoldedSyncStatus.Running;
            state.StatusChangedAt = now;
            try
            {
                await repo.SaveSyncStateAsync(state, ct);
                var docs = (await client.ListPurchaseDocumentsAsync(ct)).Select(d => ToEntity(d, now)).ToList();
                await FetchNotesAsync(docs, ct);
                ct.ThrowIfCancellationRequested();
                await repo.ReplacePurchaseDocsAsync(docs, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = docs.Count;
                state.LastError = null;
                await SaveCompletedSyncStateAsync(state);

                await DrainCallLogAsync(CancellationToken.None);
                logger.LogInformation("Holded purchase-document sync cached {Count} documents", docs.Count);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    logger.LogWarning("Holded purchase-document sync cancelled by the caller");
                else
                    logger.LogError(ex, "Holded purchase-document sync failed");
                await PersistSyncFailureAsync(state, ex);
                throw;
            }
        }
        finally
        {
            PurchaseDocSyncGate.Release();
        }
    }

    public async Task SyncPurchaseDocumentNotesAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var id in documentIds.Distinct(StringComparer.Ordinal))
                notes[id] = (await client.GetPurchaseDocumentAsync(id, ct)).Notes?.Trim() ?? "";
        }
        finally
        {
            await DrainCallLogAsync(CancellationToken.None);
        }
        await repo.SetPurchaseDocNotesAsync(notes, ct);
    }

    public async Task<bool> SyncBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, IReadOnlySet<string>? skipUnreadableDateIds = null,
        CancellationToken ct = default)
    {
        if (!await BankMovementSyncGate.WaitAsync(0, ct))
        {
            logger.LogInformation("Holded bank-feed sync already in progress — skipping this request.");
            return false;
        }

        try
        {
            var now = clock.GetCurrentInstant();
            var state = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.BankMovements, ct);
            try
            {
                var movements = (await client.ListBankMovementsAsync(
                        treasuryAccountId, from, to, ct, skipUnreadableDateIds))
                    .Select(m => new HoldedBankMovement
                    {
                        TreasuryAccountId = treasuryAccountId,
                        Id = m.Id,
                        Date = m.Date,
                        BookedAt = m.BookedAt,
                        Amount = m.Amount,
                        Description = m.Description,
                        Status = m.Status,
                    })
                    .ToList();
                ct.ThrowIfCancellationRequested();
                await repo.ReplaceBankMovementsAsync(treasuryAccountId, from, to, movements, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = movements.Count;
                state.LastError = null;
                await SaveCompletedSyncStateAsync(state);
                await DrainCallLogAsync(CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    logger.LogWarning("Holded bank-feed sync cancelled by the caller");
                else if (ex is HoldedApiException)
                    logger.LogWarning("Holded bank-feed sync failed: {Reason}", ex.Message);
                else
                    logger.LogError(ex, "Holded bank-feed sync failed");
                await PersistSyncFailureAsync(state, ex);
                throw;
            }
        }
        finally
        {
            BankMovementSyncGate.Release();
        }
    }

    public async Task<bool> SyncContactsAsync(CancellationToken ct = default)
    {
        if (!await ContactSyncGate.WaitAsync(0, ct))
        {
            logger.LogInformation("Holded contact sync already in progress — skipping this request.");
            return false;
        }

        try
        {
            var now = clock.GetCurrentInstant();
            var state = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Contacts, ct);
            state.SyncStatus = HoldedSyncStatus.Running;
            state.StatusChangedAt = now;
            try
            {
                await repo.SaveSyncStateAsync(state, ct);
                var contacts = (await client.ListContactsAsync(ct))
                    .Select(c => new HoldedContact
                    {
                        Id = c.Id,
                        Name = c.Name,
                        SupplierAccountNum = c.SupplierAccountNum,
                        TradeName = c.TradeName,
                        Email = c.Email,
                        Phone = c.Phone,
                        Mobile = c.Mobile,
                        Iban = c.Iban,
                        TaxCode = c.TaxCode,
                        Address = c.Address,
                    })
                    .ToList();
                ct.ThrowIfCancellationRequested();
                await repo.ReplaceContactsAsync(contacts, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = contacts.Count;
                state.LastError = null;
                await SaveCompletedSyncStateAsync(state);
                await DrainCallLogAsync(CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    logger.LogWarning("Holded contact sync cancelled by the caller");
                else
                    logger.LogError(ex, "Holded contact sync failed");
                await PersistSyncFailureAsync(state, ex);
                throw;
            }
        }
        finally
        {
            ContactSyncGate.Release();
        }
    }

    /// <summary>
    /// One GET per document whose notes the mirror lacks, newest first, at most <see cref="MaxNotesFetchesPerRun"/> a
    /// run; the rest keep theirs through the upsert. A failed GET leaves the notes unknown for the next sync to retry
    /// and never fails this one; a transient failure ends the run's fetching.
    /// </summary>
    private async Task FetchNotesAsync(IReadOnlyList<HoldedPurchaseDoc> docs, CancellationToken ct)
    {
        var known = (await repo.GetPurchaseDocsAsync(ct))
            .Where(d => d.Notes is not null)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = docs
            .Where(d => !known.Contains(d.Id))
            .OrderByDescending(d => d.Date)
            .Take(MaxNotesFetchesPerRun);

        var failed = 0;
        foreach (var doc in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                doc.Notes = (await client.GetPurchaseDocumentAsync(doc.Id, ct)).Notes?.Trim() ?? "";
            }
            catch (HoldedTransientException ex)
            {
                failed++;
                logger.LogWarning("Holded purchase document {DocId} could not be read for its notes: {Reason}", doc.Id, ex.Message);
                break;
            }
            catch (Exception ex) when (ex is HoldedPermanentException or System.Text.Json.JsonException
                                           or InvalidOperationException)
            {
                failed++;
                logger.LogError(ex, "Holded purchase document {DocId} could not be read for its notes", doc.Id);
            }
        }

        if (failed > 0)
            logger.LogWarning(
                "Holded purchase-document sync: notes unreadable for {Failed} document(s); the next sync retries them.",
                failed);
    }

    private static HoldedPurchaseDoc ToEntity(HoldedPurchaseDocListItemDto doc, Instant now) => new()
    {
        Id = doc.Id,
        DocNumber = doc.DocNumber,
        ContactName = doc.ContactName,
        ContactId = string.IsNullOrEmpty(doc.ContactId) ? null : doc.ContactId,
        Description = string.IsNullOrWhiteSpace(doc.Description) ? null : doc.Description.Trim(),
        Date = doc.Date.InZone(MadridZone).Date,
        Subtotal = doc.Subtotal,
        Tax = doc.Tax,
        Total = doc.Total,
        Currency = doc.Currency,
        // Strict: an absent `draft` is not approved, so a doc never leaks into budget actuals on a guess.
        IsApproved = doc.IsDraft == false,
        PaymentsPending = doc.PaymentsPending,
        Tags = [.. doc.Tags, .. doc.Lines.SelectMany(l => l.Tags)],
        BookedAccountId = doc.Lines.Count > 0 ? doc.Lines[0].AccountId : null,
        LastSyncedAt = now,
    };

    /// <summary>A drifted account gets one targeted full-history re-pull; accounts still off afterwards are
    /// returned.</summary>
    private async Task<List<(int AccountNum, decimal HoldedBalance, decimal LocalBalance)>>
        RefreshAccountsAndReconcileAsync(LocalDate today, Instant now, CancellationToken ct)
    {
        var accounts = await RefreshChartAsync(now, ct);

        ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
            await repo.ReplaceLedgerWindowAsync(
                ToWindowStart(LedgerInception), now, account.Number,
                fetched.Select(l => ToEntity(l, now)).ToList(), now, ct);

            var local = fetched.Sum(l => l.Debit) - fetched.Sum(l => l.Credit);
            if (account.Balance != local)
                mismatches.Add((account.Number, account.Balance, local));
        }

        return mismatches;
    }

    public async Task DrainCallLogAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var records = callLog.DrainAll();
        if (records.Count == 0) return;
        try
        {
            // Drained records must reach the mirror despite request cancellation.
            await repo.AddApiCallsAsync(records.Select(r => new HoldedApiCall
            {
                Id = Guid.NewGuid(),
                CalledAt = r.CalledAt,
                Endpoint = r.Endpoint,
                Method = r.Method,
                StatusCode = r.StatusCode,
                RateLimitRemaining = r.RateLimitRemaining,
                RateLimitWindow = r.RateLimitWindow,
            }).ToList(), CancellationToken.None);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("Holded call-log persistence failed after draining records.", ex);
        }
    }

    private async Task SaveCompletedSyncStateAsync(HoldedSyncState state)
    {
        try
        {
            // The mirror phase committed; its outcome must finish despite caller cancellation.
            await repo.SaveSyncStateAsync(state, CancellationToken.None);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("Holded sync outcome save failed after mirror completion.", ex);
        }
    }

    private async Task PersistSyncFailureAsync(HoldedSyncState state, Exception failure)
    {
        try
        {
            state.SyncStatus = HoldedSyncStatus.Error;
            state.LastError = TruncateForState(failure.Message);
            state.StatusChangedAt = clock.GetCurrentInstant();
            await repo.SaveSyncStateAsync(state, CancellationToken.None);
        }
        catch (Exception saveEx)
        {
            logger.LogError(saveEx, "Failed to persist {SyncKind} sync error state", state.Kind);
        }
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

    /// <summary>Local midnight of the start date. Rows dated before it are outside the sweep and must survive.</summary>
    internal static Instant ToWindowStart(LocalDate from) =>
        from.AtStartOfDayInZone(MadridZone).ToInstant();

    /// <summary>Reads the month's call count into the Usage state row. A Holded failure is recorded on the row and
    /// answers false; the last good reading and its stamp stay.</summary>
    public async Task<bool> RefreshUsageAsync(CancellationToken ct = default)
    {
        var state = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Usage, ct);
        try
        {
            var usage = await client.GetUsageAsync(ct);
            var now = clock.GetCurrentInstant();
            state.SyncStatus = HoldedSyncStatus.Idle;
            state.LastSyncAt = now;
            state.StatusChangedAt = now;
            state.LastCount = (int)Math.Min(usage.Usage, int.MaxValue);
            state.LastError = null;
            await repo.SaveSyncStateAsync(state, ct);
            return true;
        }
        catch (HoldedApiException ex)
        {
            logger.LogWarning("Holded usage refresh failed: {Reason}", ex.Message);
            await PersistSyncFailureAsync(state, ex);
            return false;
        }
        finally
        {
            await DrainCallLogAsync(CancellationToken.None);
        }
    }

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

    internal static HoldedAccount ToAccountEntity(HoldedAccountDto a, Instant now) => new()
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
    };
}
