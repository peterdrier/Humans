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
    private readonly HoldedAccountReader accountReader = new(repo);

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
    private const string PurchaseRefundType = "purchaserefund";

    /// <summary>The connector's ledger-entries page size, for the full-resync cost estimate.</summary>
    private const int LedgerPageSize = 200;

    /// <summary>More drifted accounts than this are reported as mismatches instead (and logged).</summary>
    private const int MaxTargetedRepullsPerRun = 10;

    // One sweep at a time: the admin buttons bypass Hangfire's DisableConcurrentExecution, and two sweeps would
    // race the replace window. Non-blocking so a request never queues behind a full sweep; single server, so an
    // in-process gate is enough.
    private static readonly SemaphoreSlim LedgerSyncGate = new(1, 1);

    // Same reasoning, its own gate: the two syncs write different tables.
    private static readonly SemaphoreSlim PurchaseDocSyncGate = new(1, 1);
    private static readonly SemaphoreSlim ContactSyncGate = new(1, 1);
    private static readonly SemaphoreSlim BankMovementSyncGate = new(1, 1);

    // The plan allows ~2,000 Holded calls a month: 50 a night is at most ~1,500, leaving the rest for the ledger sweep,
    // the list call and the pushes. A backlog drains over several nights, newest documents first.
    private const int MaxNotesFetchesPerRun = 50;

    public bool IsConfigured => client.IsConfigured;

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

    public async Task<IReadOnlyDictionary<int, decimal>> GetOpenDocumentAmountsAsync(
        int fromAccount, int toAccount, CancellationToken ct = default) =>
        (await repo.GetAccountTypeTotalsAsync(fromAccount, toAccount, ct))
            .Where(t => t.Type is PurchaseType or PurchaseRefundType or PaymentType)
            .GroupBy(t => t.AccountNum)
            // A purchase credits the creditor; a refund and a payment debit it.
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Credit - t.Debit));

    public async Task<IReadOnlyDictionary<int, decimal>> GetSumsByAccountAsync(
        IReadOnlySet<int> accounts, LocalDate from, LocalDate to, CancellationToken ct = default)
    {
        var sums = await repo.GetSumsByAccountAsync(
            accounts.ToList(), ToWindowStart(from), ToWindowStart(to.PlusDays(1)), ct);
        return accounts.ToDictionary(a => a, a => sums.GetValueOrDefault(a, 0m));
    }

    public async Task<IReadOnlyList<HoldedChartAccountInfo>> GetChartAsync(CancellationToken ct = default) =>
        (await repo.GetAccountsAsync(ct))
            .Select(a => new HoldedChartAccountInfo(
                a.Number, a.Name, PgcAccountNames.EnglishName(a.Number, a.Name), a.Group, a.Archived, a.Balance))
            .ToList();

    public async Task<Instant?> GetChartSyncedAtAsync(CancellationToken ct = default) =>
        (await repo.GetSyncStatesAsync(ct)).FirstOrDefault(s => s.Kind == HoldedSyncKind.Accounts)?.LastSyncAt;

    public async Task SyncChartAsync(CancellationToken ct = default)
    {
        await RefreshChartAsync(clock.GetCurrentInstant(), ct);
        await DrainCallLogAsync(ct);
    }

    /// <summary>One list call. The upsert never drops an account, so one Holded stops listing stays mirrored.</summary>
    private async Task<IReadOnlyList<HoldedAccountDto>> RefreshChartAsync(Instant now, CancellationToken ct)
    {
        var accounts = await client.ListAccountingAccountsAsync(ct);
        await repo.UpsertAccountsAsync(accounts.Select(a => ToAccountEntity(a, now)).ToList(), now, ct);

        var accountsState = await repo.GetOrCreateSyncStateAsync(HoldedSyncKind.Accounts, ct);
        accountsState.SyncStatus = HoldedSyncStatus.Idle;
        accountsState.LastSyncAt = now;
        accountsState.StatusChangedAt = now;
        accountsState.LastCount = accounts.Count;
        accountsState.LastError = null;
        await repo.SaveSyncStateAsync(accountsState, ct);
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
            await repo.ReplaceLedgerWindowAsync(
                ToWindowStart(LedgerInception), now, accountNum, fetched.Select(l => ToEntity(l, now)).ToList(), now, ct);
            await DrainCallLogAsync(ct);
            return true;
        }
        finally
        {
            LedgerSyncGate.Release();
        }
    }

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
            await repo.SaveSyncStateAsync(state, ct);

            try
            {
                var docs = (await client.ListPurchaseDocumentsAsync(ct)).Select(d => ToEntity(d, now)).ToList();
                await FetchNotesAsync(docs, ct);
                await repo.ReplacePurchaseDocsAsync(docs, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = docs.Count;
                state.LastError = null;
                await repo.SaveSyncStateAsync(state, ct);

                await DrainCallLogAsync(ct);
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
            await DrainCallLogAsync(ct);
        }
        await repo.SetPurchaseDocNotesAsync(notes, ct);
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
                Amount = m.Amount,
                Description = m.Description,
                Status = m.Status,
            })
            .ToList());
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
                        Amount = m.Amount,
                        Description = m.Description,
                        Status = m.Status,
                    })
                    .ToList();
                await repo.ReplaceBankMovementsAsync(treasuryAccountId, from, to, movements, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = movements.Count;
                state.LastError = null;
                await repo.SaveSyncStateAsync(state, ct);
                await DrainCallLogAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                // The caller gets the exception and decides; a stuck-date line is routine for the SEPA sweep.
                logger.LogWarning(ex, "Holded bank-feed sync failed");
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
            await repo.SaveSyncStateAsync(state, ct);

            try
            {
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
                await repo.ReplaceContactsAsync(contacts, now, ct);

                state.SyncStatus = HoldedSyncStatus.Idle;
                state.LastSyncAt = now;
                state.StatusChangedAt = now;
                state.LastCount = contacts.Count;
                state.LastError = null;
                await repo.SaveSyncStateAsync(state, ct);
                await DrainCallLogAsync(ct);
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
            try
            {
                doc.Notes = (await client.GetPurchaseDocumentAsync(doc.Id, ct)).Notes?.Trim() ?? "";
            }
            catch (HoldedTransientException ex)
            {
                failed++;
                logger.LogDebug(ex, "Holded purchase document {DocId} could not be read for its notes", doc.Id);
                break;
            }
            catch (Exception ex) when (ex is HoldedPermanentException or System.Text.Json.JsonException
                                           or InvalidOperationException)
            {
                failed++;
                logger.LogDebug(ex, "Holded purchase document {DocId} could not be read for its notes", doc.Id);
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

    // Holded has no stable per-document page; its purchase list opens one from this fragment.
    private static string PurchaseDocUrl(string id) => $"https://app.holded.com/expenses/list#open:purchase-{id}";

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
            .ToList();

        var (lineCount, entryCount) = await repo.GetMirrorCountsAsync(ct);
        var rows = await accountReader.GetAccountRowsAsync(ct);

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
            .ToList();
    }

    public Task<HoldedAccountsPage> GetAccountsPageAsync(
        HoldedAccountsFilter filter, CancellationToken ct = default) =>
        accountReader.GetAccountsPageAsync(filter, ct);

    public Task<HoldedAccountStatement?> GetAccountStatementAsync(
        int number, CancellationToken ct = default) =>
        accountReader.GetAccountStatementAsync(number, ct);

    public Task<HoldedEntry?> GetEntryViewAsync(int entryNumber, CancellationToken ct = default) =>
        accountReader.GetEntryViewAsync(entryNumber, ct);

    /// <summary>A drifted account gets one targeted full-history re-pull; accounts still off afterwards are
    /// returned.</summary>
    private async Task<List<(int AccountNum, decimal HoldedBalance, decimal LocalBalance)>>
        RefreshAccountsAndReconcileAsync(LocalDate today, Instant now, CancellationToken ct)
    {
        var accounts = await RefreshChartAsync(now, ct);

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

    private static HoldedLedgerLineInfo ToInfo(HoldedLedgerLine l) => new(
        l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit,
        l.EntryId, l.Checked);
}
