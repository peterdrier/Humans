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
    private HoldedMirrorSync MirrorSync => new(repo, client, callLog, clock, logger, accountReader);

    private const string PurchaseType = "purchase";
    private const string PaymentType = "payment";
    private const string PurchaseRefundType = "purchaserefund";

    /// <summary>The connector's ledger-entries page size, for the full-resync cost estimate.</summary>
    private const int LedgerPageSize = 200;

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

    public Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(CancellationToken ct = default) =>
        accountReader.GetAccountBalancesAsync(ct);

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
            accounts.ToList(), HoldedMirrorSync.ToWindowStart(from), HoldedMirrorSync.ToWindowStart(to.PlusDays(1)), ct);
        return accounts.ToDictionary(a => a, a => sums.GetValueOrDefault(a, 0m));
    }

    public async Task<IReadOnlyList<HoldedChartAccountInfo>> GetChartAsync(CancellationToken ct = default) =>
        (await repo.GetAccountsAsync(ct))
            .Select(a => new HoldedChartAccountInfo(
                a.Number, a.Name, PgcAccountNames.EnglishName(a.Number, a.Name), a.Group, a.Archived, a.Balance))
            .ToList();

    public async Task<Instant?> GetChartSyncedAtAsync(CancellationToken ct = default) =>
        (await repo.GetSyncStatesAsync(ct)).FirstOrDefault(s => s.Kind == HoldedSyncKind.Accounts)?.LastSyncAt;

    public Task SyncChartAsync(CancellationToken ct = default) =>
        MirrorSync.SyncChartAsync(ct);

    public Task<bool> SyncLedgerAsync(bool full = false, CancellationToken ct = default) =>
        MirrorSync.SyncLedgerAsync(full, ct);

    public Task<bool> SyncAccountLedgerAsync(int accountNum, CancellationToken ct = default) =>
        MirrorSync.SyncAccountLedgerAsync(accountNum, ct);

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

    public Task<bool> SyncPurchaseDocumentsAsync(CancellationToken ct = default) =>
        MirrorSync.SyncPurchaseDocumentsAsync(ct);

    public Task SyncPurchaseDocumentNotesAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default) =>
        MirrorSync.SyncPurchaseDocumentNotesAsync(documentIds, ct);

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

    public Task<bool> SyncBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, IReadOnlySet<string>? skipUnreadableDateIds = null,
        CancellationToken ct = default) =>
        MirrorSync.SyncBankMovementsAsync(treasuryAccountId, from, to, skipUnreadableDateIds, ct);

    public Task<bool> SyncContactsAsync(CancellationToken ct = default) =>
        MirrorSync.SyncContactsAsync(ct);

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
                var d = c.CalledAt.InZone(HoldedMirrorSync.MadridZone).Date;
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

    private Task DrainCallLogAsync(CancellationToken ct) =>
        MirrorSync.DrainCallLogAsync(ct);

    private static HoldedLedgerLineInfo ToInfo(HoldedLedgerLine l) => new(
        l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit,
        l.EntryId, l.Checked);
}
