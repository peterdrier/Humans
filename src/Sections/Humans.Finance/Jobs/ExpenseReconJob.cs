using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using NodaTime;

namespace Humans.Finance.Jobs;

/// <summary>After the ledger sweep, so the mirror is fresh. No "holded-api" lock: its one live call, the contact list,
/// is a single GET the transport retries on 429; if it still fails the report is unavailable and this logs a warning.
/// No API-key gate: a preview without Holded reports an unavailable mirror.</summary>
internal sealed class ExpenseReconJob(
    IFinanceService finance,
    IClock clock,
    ILogger<ExpenseReconJob> logger) : IRecurringJob
{
    /// <summary>The 03:00 ledger sweep is long done by 03:30 unless it failed or is still running.</summary>
    internal static readonly Duration StaleAfter = Duration.FromHours(2);

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var report = await finance.GetExpenseReconAsync(null, cancellationToken);
        var t = report.Totals;
        if (!report.IsAvailable)
            logger.LogWarning("Expense recon: not computed — {Reason}", report.UnavailableReason);
        else if (report.MirrorSyncedAt is { } syncedAt && clock.GetCurrentInstant() - syncedAt > StaleAfter)
            // Never "0 problems" off an old mirror: that reads as a clean night when the sweep did not finish.
            logger.LogWarning(
                "Expense recon: tonight's ledger sweep has not finished — computed on the mirror as of {SyncedAt}: "
                + "{Problems} problem(s), {Warnings} member balance warning(s). See /Finance/ExpenseRecon.",
                syncedAt, t.Problems, t.Warnings);
        else if (t.Problems == 0 && t.Warnings == 0)
            logger.LogInformation(
                "Expense recon: 0 problems — {Reports} reports, {Lines} lines, {Purchases} ledger purchases, {Members} members balanced.",
                t.Reports, t.Lines, t.LedgerPurchases, report.Members.Count);
        else
            logger.LogWarning(
                "Expense recon: {Problems} problem(s), {Warnings} member balance warning(s) — {ProblemLines} of {Lines} lines, "
                + "{Duplicates} duplicate purchase(s) ({Excess} excess), {Orphans} orphan purchase(s). See /Finance/ExpenseRecon.",
                t.Problems, t.Warnings, t.ProblemLines, t.Lines, t.DuplicateLines, t.ExcessAmount, t.Orphans);
    }
}
