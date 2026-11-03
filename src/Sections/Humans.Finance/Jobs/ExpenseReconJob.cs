using Humans.Base.Interfaces;
using Humans.Finance.Contracts;

namespace Humans.Finance.Jobs;

/// <summary>After the document sync and the ledger sweep, so both are fresh. Mirror reads plus the cached contact list,
/// so no "holded-api" lock and no API-key gate: a preview without Holded reports an unavailable mirror.</summary>
internal sealed class ExpenseReconJob(
    IHoldedFinanceService finance,
    ILogger<ExpenseReconJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var report = await finance.GetExpenseReconAsync(null, cancellationToken);
        var t = report.Totals;
        if (!report.IsAvailable)
            logger.LogWarning("Expense recon: not computed — {Reason}", report.UnavailableReason);
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
