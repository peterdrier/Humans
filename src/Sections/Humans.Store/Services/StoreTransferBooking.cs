using System.Globalization;
using System.Text.RegularExpressions;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Store.Contracts;
using Humans.Store.Data;
using NodaTime;

namespace Humans.Store.Services;

internal enum TransferState
{
    Ready,
    NoTransferAccount,
    NoDepartmentAccount,
}

internal enum TransferOutcome
{
    Posted,
    Unconfirmed,
    Failed,
}

/// <summary>One month's journal entry for one team and product. <paramref name="Amount"/> is signed: positive is
/// Dr department / Cr transfer, negative reverses an earlier month after lines were removed or repriced.</summary>
internal sealed record TransferRow(
    int Year, YearMonth Month, Guid TeamId, string TeamName, Guid ProductId, string ProductName,
    int? DepartmentAccount, int? TransferAccount, decimal Amount, TransferState State)
{
    public bool IsReady => State == TransferState.Ready;

    public LocalDate Date => Month.OnDayOfMonth(1).PlusMonths(1).PlusDays(-1);

    public (int Debit, int Credit) Sides => Amount > 0m
        ? (DepartmentAccount!.Value, TransferAccount!.Value)
        : (TransferAccount!.Value, DepartmentAccount!.Value);

    public string Description => StoreTransferBooking.Description(Year, Month, TeamId, ProductId, TeamName, ProductName);
}

internal sealed record TransferBookedLine(TransferRow Row, TransferOutcome Outcome, string? Error);

internal sealed record TransferPlan(
    int Year, YearMonth? LastClosedMonth, IReadOnlyList<TransferRow> Rows, string? Unavailable)
{
    public IReadOnlyList<TransferRow> Ready { get; } = Rows.Where(r => r.IsReady).ToList();
    public decimal ReadyAmount => Ready.Sum(r => r.Amount);
    public int BlockedCount => Rows.Count - Ready.Count;
    public decimal BlockedAmount => Rows.Where(r => !r.IsReady).Sum(r => r.Amount);
}

/// <summary>
/// Books team (department) store orders into Holded as internal transfers: one journal entry per team, product and
/// closed month, Dr the team's department account / Cr the product's transfer account, at the effective store price
/// excluding VAT and deposits. No invoice, no VAT, no 7xx. Owns no tables and stores no Holded ids: what is booked is
/// read back from the transfer accounts' ledger lines, whose description carries the key
/// (<see cref="Description"/>). Each month books the difference between what the team's lines added up to by that
/// month's end and what is already booked, so a removed line or a price change is corrected in the next entry.
/// </summary>
/// <remarks>
/// The department account is the Finance category-map account of the team's category in the active budget year's
/// department group. Previews read the mirror only; a booking run first re-pulls every transfer account from Holded.
/// One run at a time (one server). A post Holded took but does not list yet could be posted again by an immediate
/// second run; the run re-pulls the accounts after posting to keep that window short.
/// </remarks>
internal sealed partial class StoreTransferBooking(
    IOrgTimeZone orgTimeZone,
    IStoreAccountingRead orders,
    IStoreRepository repo,
    IBudgetService budget,
    IFinanceService finance,
    IHoldedService holded,
    IHoldedWrite write,
    IAuditLogService audit,
    IClock clock,
    ILogger<StoreTransferBooking> logger) : IApplicationService
{
    private const string Prefix = "Store transfer ";
    private const string AuditEntityType = "StoreTransfer";
    private const int TransferBlockStart = 62990000;
    private const int TransferBlockEnd = 62999999;
    private DateTimeZone OrgZone => orgTimeZone.Zone;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"^Store transfer (?<y>[0-9]{4}) (?<my>[0-9]{4})-(?<mm>0[1-9]|1[0-2]) T(?<t>[0-9a-f]{32}) P(?<p>[0-9a-f]{32}):",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex KeyPattern();

    internal static string Description(
        int year, YearMonth month, Guid teamId, Guid productId, string teamName, string productName) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{year} {month.Year:D4}-{month.Month:D2} T{teamId:N} P{productId:N}: {teamName} / {productName}");

    internal sealed record BookedKey(int Year, YearMonth Month, Guid TeamId, Guid ProductId);

    /// <summary>One keyed line on a transfer account: <paramref name="Amount"/> is credit − debit,
    /// <paramref name="Label"/> the "{team} / {product}" text after the key.</summary>
    internal sealed record BookedLine(BookedKey Key, int Account, decimal Amount, string Label);

    /// <summary>The key a transfer-account line names, or null for a line booked any other way.</summary>
    internal static BookedKey? KeyOf(HoldedLedgerLineInfo line)
    {
        if (line.Description is not { } d || KeyPattern().Match(d) is not { Success: true } m) return null;
        return new BookedKey(
            int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            new YearMonth(int.Parse(m.Groups["my"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["mm"].Value, CultureInfo.InvariantCulture)),
            Guid.ParseExact(m.Groups["t"].Value, "N"),
            Guid.ParseExact(m.Groups["p"].Value, "N"));
    }

    /// <summary>
    /// Pure. <paramref name="lines"/> are the year's team lines, <paramref name="transferAccounts"/> the year's
    /// catalog (product → transfer account today), <paramref name="booked"/> the keyed lines already on any transfer
    /// account. A team and product with entries resumes after its latest booked month, so nothing is ever back-dated
    /// before what Holded already holds. Whatever sits on an account that is no longer the product's (its transfer
    /// account changed) is reversed there, and the product's current account books the full amount.
    /// </summary>
    internal static IReadOnlyList<TransferRow> Plan(
        int year,
        IReadOnlyList<AccountingOrderLineDto> lines,
        IReadOnlyDictionary<Guid, int?> transferAccounts,
        IReadOnlyList<BookedLine> booked,
        IReadOnlyDictionary<Guid, int> departmentAccounts,
        YearMonth lastClosedMonth,
        DateTimeZone zone)
    {
        var teamLines = lines.Where(l => l.Year == year && l.TeamId is not null).ToList();
        var bookedThisYear = booked.Where(b => b.Key.Year == year).ToList();

        var pairs = teamLines.Select(l => (Team: l.TeamId!.Value, l.ProductId))
            .Concat(bookedThisYear.Select(b => (Team: b.Key.TeamId, b.Key.ProductId)))
            .Distinct()
            .OrderBy(p => p.Team).ThenBy(p => p.ProductId);

        var rows = new List<TransferRow>();
        foreach (var (team, product) in pairs)
        {
            var mine = teamLines.Where(l => l.TeamId == team && l.ProductId == product).ToList();
            var mineBooked = bookedThisYear.Where(b => b.Key.TeamId == team && b.Key.ProductId == product).ToList();

            var first = mine.Count > 0 ? mine.Min(l => MonthOf(l.AddedAt, zone)) : lastClosedMonth;
            if (mineBooked.Count > 0)
            {
                var resume = mineBooked.Max(b => b.Key.Month).PlusMonths(1);
                if (resume.CompareTo(first) > 0) first = resume;
            }

            var (teamName, productName) = mine.Count > 0
                ? (mine[0].CounterpartyLabel, mine[0].ProductName)
                : SplitLabel(mineBooked[^1].Label);
            var current = transferAccounts.GetValueOrDefault(product)
                ?? mine.FirstOrDefault()?.HoldedTransferAccountNum;
            int? departmentAccount = departmentAccounts.TryGetValue(team, out var d) ? d : null;

            TransferRow Row(YearMonth month, int? account, decimal amount) =>
                new(year, month, team, teamName, product, productName, departmentAccount, account, amount,
                    account is null ? TransferState.NoTransferAccount
                    : departmentAccount is null ? TransferState.NoDepartmentAccount
                    : TransferState.Ready);

            if (first.CompareTo(lastClosedMonth) > 0) continue;

            foreach (var stale in mineBooked.Where(b => b.Account != current).GroupBy(b => b.Account))
                if (stale.Sum(b => b.Amount) is var amount and not 0m)
                    rows.Add(Row(first, stale.Key, -amount));

            var bookedTotal = mineBooked.Where(b => b.Account == current).Sum(b => b.Amount);
            for (var month = first; month.CompareTo(lastClosedMonth) <= 0; month = month.PlusMonths(1))
            {
                var target = mine.Where(l => MonthOf(l.AddedAt, zone).CompareTo(month) <= 0).Sum(l => l.LineNet);
                var delta = target - bookedTotal;
                if (delta == 0m) continue;
                rows.Add(Row(month, current, delta));
                bookedTotal = target;
            }
        }
        return rows;
    }

    private static (string Team, string Product) SplitLabel(string label)
    {
        var slash = label.LastIndexOf(" / ", StringComparison.Ordinal);
        return slash < 0 ? (label, "(unknown product)") : (label[..slash], label[(slash + 3)..]);
    }

    private static YearMonth MonthOf(Instant at, DateTimeZone zone)
    {
        var date = at.InZone(zone).Date;
        return new YearMonth(date.Year, date.Month);
    }

    private YearMonth LastClosedMonth()
    {
        var today = clock.GetCurrentInstant().InZone(OrgZone).Date;
        return new YearMonth(today.Year, today.Month).PlusMonths(-1);
    }

    /// <summary>From the mirror; no Holded call.</summary>
    public async Task<TransferPlan> GetPlanAsync(int year, CancellationToken ct = default)
    {
        var lines = await orders.GetOrderLinesAsync(year, ct);
        var catalog = await CatalogAccountsAsync(year, lines, ct);
        var (departments, unavailable) = await DepartmentAccountsAsync(year, ct);
        var (booked, ledgerUnavailable) = await BookedAsync(await AccountsToReadAsync(catalog, ct), ct);
        var lastClosed = LastClosedMonth();
        if (ledgerUnavailable is not null) return new TransferPlan(year, lastClosed, [], ledgerUnavailable);
        return new TransferPlan(year, lastClosed, Plan(year, lines, catalog, booked, departments, lastClosed, OrgZone), unavailable);
    }

    /// <summary>Posts every ready row of <paramref name="year"/>. Refused, nothing posted, while another run holds
    /// the gate, Holded is not configured, or a ledger sweep is running.</summary>
    public async Task<(IReadOnlyList<TransferBookedLine> Lines, string? Refused)> BookAsync(
        int year, Guid actorUserId, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct)) return ([], "Another booking run is going; nothing was booked.");
        try
        {
            if (!holded.IsConfigured) return ([], "Holded is not configured here; nothing was booked.");
            var lines = await orders.GetOrderLinesAsync(year, ct);
            var catalog = await CatalogAccountsAsync(year, lines, ct);
            var accounts = await AccountsToReadAsync(catalog, ct);
            foreach (var account in accounts)
                if (!await holded.SyncAccountLedgerAsync(account, ct))
                    return ([], "A Holded ledger sync is running; nothing was booked. Try again in a minute.");

            var (departments, _) = await DepartmentAccountsAsync(year, ct);
            var (booked, unavailable) = await BookedAsync(accounts, ct);
            if (unavailable is not null) return ([], unavailable);
            var ready = Plan(year, lines, catalog, booked, departments, LastClosedMonth(), OrgZone)
                .Where(r => r.IsReady).ToList();

            var results = new List<TransferBookedLine>();
            foreach (var row in ready)
            {
                var (debit, credit) = row.Sides;
                var amount = Math.Abs(row.Amount);
                TransferBookedLine result;
                var stop = false;
                try
                {
                    // Never the request's token: once Holded has the post, abandoning the call only hides it.
                    var outcome = await write.PostJournalEntryAsync(
                        row.Date, debit, credit, amount, row.Description, CancellationToken.None);
                    result = new TransferBookedLine(row,
                        outcome.Confirmed ? TransferOutcome.Posted : TransferOutcome.Unconfirmed,
                        outcome.Confirmed ? null : "Holded took it without an id: check in Holded");
                }
                catch (HoldedPermanentException ex)
                {
                    logger.LogWarning("Holded refused store transfer {Description}: {Reason}", row.Description, ex.Message);
                    result = new TransferBookedLine(row, TransferOutcome.Failed, "Holded refused it: " + ex.Message);
                }
                catch (HoldedTransientException ex)
                {
                    logger.LogWarning("Holded did not answer store transfer {Description}; it may have posted: {Reason}",
                        row.Description, ex.Message);
                    result = new TransferBookedLine(row, TransferOutcome.Failed,
                        $"Holded did not answer ({ex.Message}): it may have posted, check in Holded");
                    stop = true;
                }

                results.Add(result);
                await audit.LogAsync(StoreAuditActions.StoreTransferBooked, AuditEntityType, row.TeamId,
                    $"{row.TeamName} / {row.ProductName} {row.Month.Year:D4}-{row.Month.Month:D2} ({row.Date.ToInvariantDate()}): "
                    + $"Dr {debit} / Cr {credit} {amount.ToEuro()} — {result.Outcome}"
                    + (result.Error is null ? "" : ": " + result.Error),
                    actorUserId);
                if (stop) return (results, "Stopped: Holded did not answer.");
            }

            await ResyncAsync(results);
            return (results, null);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Store transfer booking could not read Holded");
            return ([], "Holded could not be read: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Best effort: a failed re-pull must not hide what was posted.</summary>
    private async Task ResyncAsync(IReadOnlyList<TransferBookedLine> results)
    {
        try
        {
            foreach (var account in results.Select(r => r.Row.TransferAccount!.Value).Distinct())
                await holded.SyncAccountLedgerAsync(account, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex, "Could not re-read the transfer accounts after a store transfer run");
        }
    }

    /// <summary>Every product of the year's catalog plus any product a line names from elsewhere, with its transfer
    /// account today.</summary>
    private async Task<IReadOnlyDictionary<Guid, int?>> CatalogAccountsAsync(
        int year, IReadOnlyList<AccountingOrderLineDto> lines, CancellationToken ct)
    {
        var accounts = (await repo.GetAllProductsForYearAsync(year, ct))
            .ToDictionary(p => p.Id, p => p.HoldedTransferAccountNum);
        foreach (var l in lines.Where(l => l.TeamId is not null))
            accounts.TryAdd(l.ProductId, l.HoldedTransferAccountNum);
        return accounts;
    }

    /// <summary>The transfer accounts the year's products name, plus every chart account in the 6299xxxx block, so
    /// an entry booked on an account a product has since moved off is still seen.</summary>
    private async Task<IReadOnlyList<int>> AccountsToReadAsync(
        IReadOnlyDictionary<Guid, int?> catalog, CancellationToken ct) =>
        catalog.Values.OfType<int>()
            .Concat((await holded.GetChartAsync(ct)).Select(a => a.Number)
                .Where(n => n is >= TransferBlockStart and <= TransferBlockEnd))
            .Distinct().Order().ToList();

    private async Task<(IReadOnlyList<BookedLine> Lines, string? Unavailable)> BookedAsync(
        IReadOnlyList<int> accounts, CancellationToken ct)
    {
        var booked = new List<BookedLine>();
        foreach (var account in accounts)
            foreach (var line in await holded.GetLedgerLinesAsync(account, ct))
                if (KeyOf(line) is { } key)
                {
                    var colon = line.Description!.IndexOf(": ", StringComparison.Ordinal);
                    booked.Add(new BookedLine(key, account, line.Credit - line.Debit,
                        colon < 0 ? "(unknown team)" : line.Description[(colon + 2)..]));
                }
                else if (line.Description?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                {
                    logger.LogWarning("Invalid store transfer key on Holded account {Account}, entry {Entry}, line {Line}",
                        account, line.EntryNumber, line.Line);
                    return ([], string.Create(CultureInfo.InvariantCulture,
                        $"Store transfer history is unreadable: account {account}, entry {line.EntryNumber}, line {line.Line} has an invalid transfer key. Nothing was booked."));
                }
        return (booked, null);
    }

    /// <summary>Team id → its department category's account. Unavailable, with a reason, when the active budget year
    /// is not <paramref name="year"/>: a team's account for another year cannot be read.</summary>
    private async Task<(IReadOnlyDictionary<Guid, int> Accounts, string? Unavailable)> DepartmentAccountsAsync(
        int year, CancellationToken ct)
    {
        var active = await budget.GetActiveYearAsync(ct);
        if (active is null) return (new Dictionary<Guid, int>(), "No budget year is active.");
        if (!string.Equals(active.Year, year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return (new Dictionary<Guid, int>(),
                $"The active budget year is {active.Year}, so {year}'s department accounts cannot be read.");

        var accountByCategory = (await finance.GetCategoryMapAsync(ct))
            .ToDictionary(m => m.BudgetCategoryId, m => m.HoldedAccountNumber);
        var accounts = new Dictionary<Guid, int>();
        foreach (var category in active.Groups.Where(g => g.IsDepartmentGroup).SelectMany(g => g.Categories))
            if (category.TeamId is { } team && accountByCategory.TryGetValue(category.Id, out var account))
                accounts.TryAdd(team, account);
        return (accounts, null);
    }
}
