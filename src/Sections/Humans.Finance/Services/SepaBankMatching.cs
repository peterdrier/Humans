using System.Globalization;
using System.Text.RegularExpressions;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using NodaTime;
using static Humans.Finance.Services.SepaText;

namespace Humans.Finance.Services;

internal sealed class SepaBankMatching(
    IClock clock,
    DateTimeZone zone,
    int feedWindowDays,
    int generationSlackDays) : IApplicationService
{
    private const string ReconciledStatus = "reconciled";

    private const string PendingStatus = "pending";

    /// <summary>Matched anywhere in the line, not anchored: bank feeds prepend their own wording.</summary>
    private static readonly Regex RemittanceAccount = new(
        @"(?<acct>\d{8})\s*-\s*NCA\s*-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary><c>partial</c> counts as started: part of that line is settled by hand, so posting the whole transfer
    /// would pay those documents twice.</summary>
    internal static bool NeedsAMatch(HoldedBankMovementDto m) => m.Amount < 0m && IsUntouched(m);

    /// <summary>Holded reports <c>pending</c>, <c>partial</c> or <c>reconciled</c>; only the first has nothing tied
    /// to it.</summary>
    internal static bool IsUntouched(HoldedBankMovementDto m) =>
        string.Equals(m.Status, PendingStatus, StringComparison.Ordinal);

    internal static bool IsReconciled(HoldedBankMovementDto m) =>
        string.Equals(m.Status, ReconciledStatus, StringComparison.Ordinal);

    /// <summary>Null when the line is not one of ours.</summary>
    internal static int? RemittanceAccountNum(string? description)
    {
        var match = RemittanceAccount.Match(description ?? "");
        return match.Success
            && int.TryParse(match.Groups["acct"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
            ? num
            : null;
    }

    /// <summary>A withdrawn transfer was cancelled at the bank, so no line pays it and no file total includes it. Every
    /// pairing path reads through this.</summary>
    internal static List<SepaPayoutTransferRow> Live(IEnumerable<SepaPayoutTransferRow> rows) =>
        rows.Where(r => !r.IsWithdrawn).ToList();

    /// <summary>Any account when the line names none (Sabadell drops the remittance text). More than one is ambiguous.
    /// Only files inside the feed window count, or an old row would make every later same-amount transfer ambiguous
    /// forever; a line dated before its file is some older payment.</summary>
    internal List<SepaPayoutTransferRow> UnbookedMatches(
        IReadOnlyList<SepaPayoutTransferRow> rows, int? account, decimal amount, LocalDate lineDate,
        int tolerance) =>
        rows.Where(r => r.BookedAt is null && !r.IsWithdrawn && (account is null || r.SupplierAccountNum == account)
                        && r.Amount == amount && !IsStale(r)
                        && InWindow(r, lineDate, tolerance, bounded: account is null))
            .ToList();

    /// <summary>No more than <paramref name="tolerance"/> days before the file date, and for a line only the date ties
    /// (<paramref name="bounded"/>, textless) no more than that after. A line naming the account may be later.</summary>
    private bool InWindow(SepaPayoutTransferRow row, LocalDate lineDate, int tolerance, bool bounded)
    {
        var fileDate = FileDate(row);
        return lineDate >= fileDate.PlusDays(-tolerance)
               && (!bounded || lineDate <= fileDate.PlusDays(tolerance));
    }

    /// <summary>A textless line that is also a multi-transfer file's total inside its window: one transfer's debit
    /// cannot be told from a whole file's, so neither path takes it.</summary>
    internal bool TextlessLineFitsAFile(
        IReadOnlyList<SepaPayoutTransferRow> rows, HoldedBankMovementDto m, int tolerance) =>
        rows.Where(r => !r.IsWithdrawn).GroupBy(r => r.FileId).Any(f =>
            f.Count() >= 2 && f.Sum(r => r.Amount) == Math.Abs(m.Amount)
            && f.Any(r => !r.IsBooked) && !IsStale(f.First())
            // A row booked against some other line means the bank did not debit this file as
            // one — the same rule BatchLineRefusal applies.
            && !f.Any(r => r.IsBooked && !string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal))
            && InWindow(f.First(), m.Date, tolerance, bounded: true));

    /// <summary>
    /// Pairs a textless set (two or more same-amount transfers of one file) one to one with exactly as many textless
    /// lines, when no other line could pay any of them. Which line paid which is unknowable and accounting-neutral, so
    /// lines go by id against transfers by account. Empty when the set does not pair.
    /// </summary>
    internal List<(HoldedBankMovementDto Line, SepaPayoutTransferRow Row)> TextlessSetPairs(
        IReadOnlyList<SepaPayoutTransferRow> rows, IReadOnlyList<HoldedBankMovementDto> feed,
        HoldedBankMovementDto m, int tolerance)
    {
        var set = UnbookedMatches(rows, null, Math.Abs(m.Amount), m.Date, tolerance);
        if (set.Count < 2 || set.Select(r => r.FileId).Distinct().Count() > 1) return [];
        var ids = set.Select(r => r.TransferId).ToHashSet();
        var claimants = feed
            .Where(x => NeedsAMatch(x)
                        && !rows.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal)))
            .Select(x => (Line: x, Account: RemittanceAccountNum(x.Description)))
            .Select(x => (x.Line, x.Account, Matches:
                UnbookedMatches(rows, x.Account, Math.Abs(x.Line.Amount), x.Line.Date, tolerance)
                    .Select(r => r.TransferId).ToHashSet()))
            .Where(x => x.Matches.Overlaps(ids))
            .ToList();
        if (claimants.Count != set.Count
            || claimants.Any(x => x.Account is not null || !x.Matches.SetEquals(ids)
                                  || TextlessLineFitsAFile(rows, x.Line, tolerance)))
            return [];
        return claimants
            .OrderBy(x => x.Line.Id, StringComparer.Ordinal)
            .Zip(set.OrderBy(r => r.SupplierAccountNum), (x, r) => (x.Line, r))
            .ToList();
    }

    /// <summary>More than one is ambiguous and waits for a human.</summary>
    internal List<Guid> BatchFiles(
        IReadOnlyList<SepaPayoutTransferRow> rows, HoldedBankMovementDto m,
        IReadOnlyList<HoldedBankMovementDto> feed, int tolerance) =>
        rows.Where(r => !r.IsWithdrawn).GroupBy(r => r.FileId)
            .Where(g => BatchLineRefusal(g.ToList(), m, rows, feed, tolerance) is null)
            .Select(g => g.Key)
            .ToList();

    /// <summary>Judged on this line and file alone; other files and lines are the caller's ambiguity check. The amount
    /// must be the file's total, exactly: nothing is split by guesswork.</summary>
    internal string? BatchLineRefusal(
        IReadOnlyList<SepaPayoutTransferRow> file, HoldedBankMovementDto m,
        IReadOnlyList<SepaPayoutTransferRow> rows, IReadOnlyList<HoldedBankMovementDto> feed,
        int tolerance)
    {
        var total = file.Sum(r => r.Amount);
        if (file.Count < 2)
            return "That file holds one transfer — book it on its own row. Nothing was posted.";
        if (m.Amount >= 0m || Math.Abs(m.Amount) != total)
            return $"That Sabadell line is {Euros(m.Amount)}, not the {Euros(total)} this file totals — "
                   + "nothing was posted.";
        if (!IsUntouched(m))
            return $"That Sabadell line is already {m.Status} in Holded — settle it there by hand. "
                   + "Nothing was posted.";
        if (IsStale(file[0]))
            return $"That file was generated more than {feedWindowDays} days ago — settle it in Holded "
                   + "by hand. Nothing was posted.";
        if (!InWindow(file[0], m.Date, tolerance, bounded: true))
            return $"That Sabadell line is dated more than {tolerance} day(s) from when this file was "
                   + "generated — widen the tolerance if it is this file's. Nothing was posted.";
        if (file.All(r => r.IsBooked))
            return "Every transfer in that file is already booked — nothing was posted.";
        // A transfer booked against a line of its own means the bank did not debit this file as one.
        if (file.Any(r => r.IsBooked
                          && !string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            return "Part of that file was already booked against its own Sabadell lines — settle the "
                   + "rest in Holded by hand. Nothing was posted.";
        if (rows.Any(r => r.FileId != file[0].FileId
                          && string.Equals(r.HoldedBankMovementId, m.Id, StringComparison.Ordinal)))
            return "That Sabadell line already booked a transfer from another file — nothing was posted.";
        // A line whose text names one transfer's account and amount is that transfer's own line.
        if (RemittanceAccountNum(m.Description) is { } account)
        {
            if (UnbookedMatches(rows, account, Math.Abs(m.Amount), m.Date, tolerance).Count > 0)
                return "That Sabadell line names a single transfer — book it on that transfer's row. "
                       + "Nothing was posted.";
        }
        // A textless one that is also some single transfer's amount could be either.
        else if (UnbookedMatches(rows, null, Math.Abs(m.Amount), m.Date, tolerance).Count > 0)
            return "That Sabadell line names no creditor account and could be a single transfer's "
                   + "debit as well as this file's — settle it in Holded by hand. Nothing was posted.";
        // And the mirror: a transfer of this file with a line of its own was not debited as part of
        // one total, so a total-sized line is some other payment.
        if (feed.Any(x => NeedsAMatch(x)
                          && !rows.Any(r => string.Equals(r.HoldedBankMovementId, x.Id, StringComparison.Ordinal))
                          && RemittanceAccountNum(x.Description) is { } own
                          && UnbookedMatches(file, own, Math.Abs(x.Amount), x.Date, tolerance).Count > 0))
            return "A transfer in that file has a Sabadell line of its own — book the transfers on their "
                   + "rows. Nothing was posted.";
        return null;
    }

    /// <summary>The file's generation day, less the generation tolerance.</summary>
    internal LocalDate FirstPayableDate(SepaPayoutTransferRow row) =>
        FileDate(row).PlusDays(-generationSlackDays);

    /// <summary>In the organisation's zone: what a bank line's date is measured against.</summary>
    internal LocalDate FileDate(SepaPayoutTransferRow row) => row.GeneratedAt.InZone(zone).Date;

    /// <summary>Generated before the feed's window, so no line can book it: a human's job in Holded.</summary>
    internal bool IsStale(SepaPayoutTransferRow row) =>
        row.GeneratedAt.InZone(zone).Date < clock.GetCurrentInstant().InZone(zone).Date.PlusDays(-feedWindowDays);
}
