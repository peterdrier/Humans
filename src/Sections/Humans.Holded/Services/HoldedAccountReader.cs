using Humans.Base.Interfaces;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Models;

namespace Humans.Holded.Services;

internal sealed class HoldedAccountReader(IHoldedMirrorRepository repo) : IApplicationService
{
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

}
