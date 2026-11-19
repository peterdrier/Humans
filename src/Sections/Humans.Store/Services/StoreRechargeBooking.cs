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

internal enum RechargeState
{
    Ready,
    NoRechargeAccount,
    NoDepartmentAccount,
}

internal enum RechargeOutcome
{
    Posted,
    Unconfirmed,
    Failed,
}

/// <summary>One month's journal entry for one team and product. <paramref name="Amount"/> is signed: positive is
/// Dr department / Cr recharge, negative reverses an earlier month after lines were removed or repriced.</summary>
internal sealed record RechargeRow(
    int Year, YearMonth Month, Guid TeamId, string TeamName, Guid ProductId, string ProductName,
    int? DepartmentAccount, int? RechargeAccount, decimal Amount, RechargeState State)
{
    public LocalDate Date => Month.OnDayOfMonth(1).PlusMonths(1).PlusDays(-1);

    public (int Debit, int Credit) Sides => Amount > 0m
        ? (DepartmentAccount!.Value, RechargeAccount!.Value)
        : (RechargeAccount!.Value, DepartmentAccount!.Value);

    public string Description => StoreRechargeBooking.Description(Year, Month, TeamId, ProductId, TeamName, ProductName);
}

internal sealed record RechargeBookedLine(RechargeRow Row, RechargeOutcome Outcome, string? Error);

internal sealed record RechargePlan(
    int Year, YearMonth? LastClosedMonth, IReadOnlyList<RechargeRow> Rows, string? DepartmentAccountsUnavailable);

/// <summary>
/// Books team (department) store orders into Holded as internal recharges: one journal entry per team, product and
/// closed month, Dr the team's department account / Cr the product's recharge account, at the effective store price
/// excluding VAT and deposits. No invoice, no VAT, no 7xx. Owns no tables and stores no Holded ids: what is booked is
/// read back from the recharge accounts' ledger lines, whose description carries the key
/// (<see cref="Description"/>). Each month books the difference between what the team's lines added up to by that
/// month's end and what is already booked, so a removed line or a price change is corrected in the next entry.
/// </summary>
/// <remarks>
/// The department account is the Finance category-map account of the team's category in the active budget year's
/// department group. Previews read the mirror only; a booking run first re-pulls every recharge account from Holded.
/// One run at a time (one server). A post Holded took but does not list yet could be posted again by an immediate
/// second run; the run re-pulls the accounts after posting to keep that window short.
/// </remarks>
internal sealed partial class StoreRechargeBooking(
    IStoreAccountingRead orders,
    IStoreRepository repo,
    IBudgetService budget,
    IFinanceService finance,
    IHoldedService holded,
    IHoldedWrite write,
    IAuditLogService audit,
    IClock clock,
    ILogger<StoreRechargeBooking> logger) : IApplicationService
{
    private const string Prefix = "Store recharge ";
    private const string AuditEntityType = "StoreRecharge";
    private const int RechargeBlockStart = 62990000;
    private const int RechargeBlockEnd = 62999999;
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"^Store recharge (?<y>\d{4}) (?<my>\d{4})-(?<mm>\d{2}) T(?<t>[0-9a-f]{32}) P(?<p>[0-9a-f]{32}):",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex KeyPattern();

    internal static string Description(
        int year, YearMonth month, Guid teamId, Guid productId, string teamName, string productName) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{year} {month.Year:D4}-{month.Month:D2} T{teamId:N} P{productId:N}: {teamName} / {productName}");

    internal sealed record BookedKey(int Year, YearMonth Month, Guid TeamId, Guid ProductId);

    /// <summary>One keyed line on a recharge account: <paramref name="Amount"/> is credit − debit,
    /// <paramref name="Label"/> the "{team} / {product}" text after the key.</summary>
    internal sealed record BookedLine(BookedKey Key, int Account, decimal Amount, string Label);

    /// <summary>The key a recharge-account line names, or null for a line booked any other way.</summary>
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
    /// Pure. <paramref name="lines"/> are the year's team lines, <paramref name="rechargeAccounts"/> the year's
    /// catalog (product → recharge account today), <paramref name="booked"/> the keyed lines already on any recharge
    /// account. A team and product with entries resumes after its latest booked month, so nothing is ever back-dated
    /// before what Holded already holds. Whatever sits on an account that is no longer the product's (its recharge
    /// account changed) is reversed there, and the product's current account books the full amount.
    /// </summary>
    internal static IReadOnlyList<RechargeRow> Plan(
        int year,
        IReadOnlyList<AccountingOrderLineDto> lines,
        IReadOnlyDictionary<Guid, int?> rechargeAccounts,
        IReadOnlyList<BookedLine> booked,
        IReadOnlyDictionary<Guid, int> departmentAccounts,
        YearMonth lastClosedMonth)
    {
        var teamLines = lines.Where(l => l.Year == year && l.TeamId is not null).ToList();
        var bookedThisYear = booked.Where(b => b.Key.Year == year).ToList();

        var pairs = teamLines.Select(l => (Team: l.TeamId!.Value, l.ProductId))
            .Concat(bookedThisYear.Select(b => (Team: b.Key.TeamId, b.Key.ProductId)))
            .Distinct()
            .OrderBy(p => p.Team).ThenBy(p => p.ProductId);

        var rows = new List<RechargeRow>();
        foreach (var (team, product) in pairs)
        {
            var mine = teamLines.Where(l => l.TeamId == team && l.ProductId == product).ToList();
            var mineBooked = bookedThisYear.Where(b => b.Key.TeamId == team && b.Key.ProductId == product).ToList();

            var first = mine.Count > 0 ? mine.Min(l => MonthOf(l.AddedAt)) : lastClosedMonth;
            if (mineBooked.Count > 0)
            {
                var resume = mineBooked.Max(b => b.Key.Month).PlusMonths(1);
                if (resume.CompareTo(first) > 0) first = resume;
            }

            var (teamName, productName) = mine.Count > 0
                ? (mine[0].CounterpartyLabel, mine[0].ProductName)
                : SplitLabel(mineBooked[^1].Label);
            var current = rechargeAccounts.GetValueOrDefault(product)
                ?? mine.FirstOrDefault()?.HoldedRechargeAccountNum;
            int? departmentAccount = departmentAccounts.TryGetValue(team, out var d) ? d : null;

            RechargeRow Row(YearMonth month, int? account, decimal amount) =>
                new(year, month, team, teamName, product, productName, departmentAccount, account, amount,
                    account is null ? RechargeState.NoRechargeAccount
                    : departmentAccount is null ? RechargeState.NoDepartmentAccount
                    : RechargeState.Ready);

            if (first.CompareTo(lastClosedMonth) > 0) continue;

            foreach (var stale in mineBooked.Where(b => b.Account != current).GroupBy(b => b.Account))
                if (stale.Sum(b => b.Amount) is var amount and not 0m)
                    rows.Add(Row(first, stale.Key, -amount));

            var bookedTotal = mineBooked.Where(b => b.Account == current).Sum(b => b.Amount);
            for (var month = first; month.CompareTo(lastClosedMonth) <= 0; month = month.PlusMonths(1))
            {
                var target = mine.Where(l => MonthOf(l.AddedAt).CompareTo(month) <= 0).Sum(l => l.LineNet);
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

    private static YearMonth MonthOf(Instant at)
    {
        var date = at.InZone(Madrid).Date;
        return new YearMonth(date.Year, date.Month);
    }

    private YearMonth LastClosedMonth()
    {
        var today = clock.GetCurrentInstant().InZone(Madrid).Date;
        return new YearMonth(today.Year, today.Month).PlusMonths(-1);
    }

    /// <summary>From the mirror; no Holded call.</summary>
    public async Task<RechargePlan> GetPlanAsync(int year, CancellationToken ct = default)
    {
        var lines = await orders.GetOrderLinesAsync(year, ct);
        var catalog = await CatalogAccountsAsync(year, lines, ct);
        var (departments, unavailable) = await DepartmentAccountsAsync(year, ct);
        var booked = await BookedAsync(await AccountsToReadAsync(catalog, ct), ct);
        var lastClosed = LastClosedMonth();
        return new RechargePlan(year, lastClosed, Plan(year, lines, catalog, booked, departments, lastClosed), unavailable);
    }

    /// <summary>Posts every ready row of <paramref name="year"/>. Refused, nothing posted, while another run holds
    /// the gate, Holded is not configured, or a ledger sweep is running.</summary>
    public async Task<(IReadOnlyList<RechargeBookedLine> Lines, string? Refused)> BookAsync(
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
            var booked = await BookedAsync(accounts, ct);
            var ready = Plan(year, lines, catalog, booked, departments, LastClosedMonth())
                .Where(r => r.State == RechargeState.Ready).ToList();

            var results = new List<RechargeBookedLine>();
            foreach (var row in ready)
            {
                var (debit, credit) = row.Sides;
                var amount = Math.Abs(row.Amount);
                RechargeBookedLine result;
                var stop = false;
                try
                {
                    // Never the request's token: once Holded has the post, abandoning the call only hides it.
                    var outcome = await write.PostJournalEntryAsync(
                        row.Date, debit, credit, amount, row.Description, CancellationToken.None);
                    result = new RechargeBookedLine(row,
                        outcome.Confirmed ? RechargeOutcome.Posted : RechargeOutcome.Unconfirmed,
                        outcome.Confirmed ? null : "Holded took it without an id: check in Holded");
                }
                catch (HoldedPermanentException ex)
                {
                    logger.LogWarning("Holded refused store recharge {Description}: {Reason}", row.Description, ex.Message);
                    result = new RechargeBookedLine(row, RechargeOutcome.Failed, "Holded refused it: " + ex.Message);
                }
                catch (HoldedTransientException ex)
                {
                    logger.LogWarning("Holded did not answer store recharge {Description}; it may have posted: {Reason}",
                        row.Description, ex.Message);
                    result = new RechargeBookedLine(row, RechargeOutcome.Failed,
                        $"Holded did not answer ({ex.Message}): it may have posted, check in Holded");
                    stop = true;
                }

                results.Add(result);
                await audit.LogAsync(StoreAuditActions.StoreRechargeBooked, AuditEntityType, row.TeamId,
                    $"{row.TeamName} / {row.ProductName} {row.Month.Year:D4}-{row.Month.Month:D2} ({row.Date.ToInvariantDate()}): "
                    + $"Dr {debit} / Cr {credit} {amount.ToEuro()} — {result.Outcome}"
                    + (result.Error is null ? "" : ": " + result.Error),
                    actorUserId);
                if (stop) return (results, "Stopped: Holded did not answer.");
            }

            if (results.Count > 0)
                foreach (var account in results.Select(r => r.Row.RechargeAccount!.Value).Distinct())
                    await holded.SyncAccountLedgerAsync(account, CancellationToken.None);
            return (results, null);
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogError(ex, "Store recharge booking could not read Holded");
            return ([], "Holded could not be read: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Every product of the year's catalog plus any product a line names from elsewhere, with its recharge
    /// account today.</summary>
    private async Task<IReadOnlyDictionary<Guid, int?>> CatalogAccountsAsync(
        int year, IReadOnlyList<AccountingOrderLineDto> lines, CancellationToken ct)
    {
        var accounts = (await repo.GetAllProductsForYearAsync(year, ct))
            .ToDictionary(p => p.Id, p => p.HoldedRechargeAccountNum);
        foreach (var l in lines.Where(l => l.TeamId is not null))
            accounts.TryAdd(l.ProductId, l.HoldedRechargeAccountNum);
        return accounts;
    }

    /// <summary>The recharge accounts the year's products name, plus every chart account in the 6299xxxx block, so
    /// an entry booked on an account a product has since moved off is still seen.</summary>
    private async Task<IReadOnlyList<int>> AccountsToReadAsync(
        IReadOnlyDictionary<Guid, int?> catalog, CancellationToken ct) =>
        catalog.Values.OfType<int>()
            .Concat((await holded.GetChartAsync(ct)).Select(a => a.Number)
                .Where(n => n is >= RechargeBlockStart and <= RechargeBlockEnd))
            .Distinct().Order().ToList();

    private async Task<IReadOnlyList<BookedLine>> BookedAsync(IReadOnlyList<int> accounts, CancellationToken ct)
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
        return booked;
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
