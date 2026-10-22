using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Contracts;

/// <summary>
/// Each runs inside the ambient <c>TransactionScope</c> Expenses opens around its own status change, so the report and
/// the grant commit together or not at all; a view component can't sit inside that transaction.
/// </summary>
public interface IPreapprovalWrite : IApplicationService
{
    /// <summary>
    /// Open → Claimed. Idempotent for the report already holding the grant (a rejected report resubmitting), which
    /// also skips the deadline: it was filed in time. Throws <see cref="InvalidOperationException"/> with the reason
    /// when the grant is not claimable.
    /// </summary>
    Task<GrantStamp> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);

    /// <summary>
    /// Claimed → Open when the holding report is withdrawn before approval or drops the grant. The report id stays on
    /// the grant for the trail.
    /// </summary>
    Task ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);

    /// <summary>Claimed → Consumed when the holding report is approved. Terminal; the amount and report id are kept.</summary>
    Task ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);
}
