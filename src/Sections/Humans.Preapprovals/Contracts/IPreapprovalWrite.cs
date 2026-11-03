using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Contracts;

/// <summary>
/// Each runs inside the ambient <c>TransactionScope</c> Expenses opens around its own status change, so the report and
/// the grant commit together or not at all; a view component can't sit inside that transaction.
/// Ambient-transaction callers collect completion through <c>afterCommit</c> and invoke it only after commit, with
/// <see cref="CancellationToken.None"/>. On rollback or refusal they discard it. Completion is best-effort.
/// </summary>
public interface IPreapprovalWrite : IApplicationService
{
    /// <summary>
    /// Open → Claimed. Idempotent for the report already holding the grant (a rejected report resubmitting), which
    /// also skips the deadline: it was filed in time. A refusal leaves the grant unchanged.
    /// </summary>
    Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit = null);

    /// <summary>
    /// Claimed → Open when the holding report is withdrawn before approval or drops the grant. The report id stays on
    /// the grant for the trail.
    /// </summary>
    Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit = null);

    /// <summary>Claimed → Consumed when the holding report is approved. Terminal; the amount and report id are kept.</summary>
    Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit = null);
}

/// <summary>A refusal leaves the grant unchanged. A successful claim includes its booking stamp.</summary>
public sealed record PreapprovalWriteResult(GrantStamp? Stamp = null, string? Refusal = null);
