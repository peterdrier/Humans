using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Contracts;

/// <summary>
/// Each runs inside the ambient <c>TransactionScope</c> Expenses opens around its own status change, so the report and
/// the grant commit together or not at all; a view component can't sit inside that transaction.
/// Audit and notices go into <c>afterCommit</c>; the caller runs them only after its commit, with
/// <see cref="CancellationToken.None"/>, and discards them on rollback or refusal. Completion is best-effort.
/// </summary>
public interface IPreapprovalWrite : IApplicationService
{
    /// <summary>
    /// Open → Claimed. Idempotent for the report already holding the grant (a rejected report resubmitting), which
    /// also skips the deadline: it was filed in time. A refusal leaves the grant unchanged.
    /// </summary>
    Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit);

    /// <summary>
    /// Claimed → Open when the holding report is withdrawn before approval or drops the grant. The report id stays on
    /// the grant for the trail.
    /// </summary>
    Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit);

    /// <summary>Claimed → Consumed when the holding report is approved. Terminal; the amount and report id are kept.</summary>
    Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit);
}

/// <summary>A refusal leaves the grant unchanged. A successful claim carries the grant as booked: its cap, account and
/// deadline at submit time.</summary>
public sealed record PreapprovalWriteResult(GrantOption? Claimed = null, string? Refusal = null);
