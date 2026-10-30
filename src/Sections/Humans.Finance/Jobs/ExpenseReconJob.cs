using Humans.Base.Interfaces;
using Humans.Finance.Contracts;

namespace Humans.Finance.Jobs;

/// <summary>After the document sync and the ledger sweep, so both mirrors are fresh. Local reads only, so no
/// "holded-api" lock and no API-key gate: a preview without Holded reports an unavailable mirror.</summary>
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
                "Expense recon: 0 problems — {Reports} reports, {Lines} lines, {Docs} Holded docs, {Members} members balanced.",
                t.Reports, t.Lines, t.HoldedDocs, report.Members.Count);
        else
            logger.LogWarning(
                "Expense recon: {Problems} problem(s), {Warnings} member balance warning(s) — {ProblemLines} of {Lines} lines, "
                + "{Duplicates} duplicate doc(s) ({Excess} excess), {Orphans} orphan doc(s). See /Finance/ExpenseRecon.",
                t.Problems, t.Warnings, t.ProblemLines, t.Lines, t.DuplicateDocs, t.ExcessAmount, t.Orphans);
    }
}
