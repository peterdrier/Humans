using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Contracts;

/// <summary>
/// The writes Preapprovals exposes outside itself, alongside the read-only
/// <see cref="IPreapprovalService"/>: the three grant transitions an expense report drives as it
/// moves. Each runs inside the ambient <c>TransactionScope</c> Expenses opens around its own status
/// change, so the report and the grant commit together or not at all; a view component cannot sit
/// inside that transaction, which is why this is an interface. Each is audited with the report id.
/// Every other write (programs, issuing, approving, declining, revoking) is internal.
/// </summary>
public interface IPreapprovalWrite : IApplicationService
{
    /// <summary>
    /// <see cref="GrantStatus.Open"/> → <see cref="GrantStatus.Claimed"/> by
    /// <paramref name="expenseReportId"/>, returning the stamp the report must carry. Idempotent
    /// for the report already holding the grant (a rejected report resubmitting), which also
    /// skips the deadline: it was filed in time. Throws <see cref="InvalidOperationException"/>
    /// when the grant is not claimable, with the reason in the message.
    /// </summary>
    Task<GrantStamp> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);

    /// <summary>
    /// <see cref="GrantStatus.Claimed"/> → <see cref="GrantStatus.Open"/> when the holding report
    /// is withdrawn before approval, or drops the grant from its draft. The report id is kept on
    /// the grant for the trail.
    /// </summary>
    Task ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);

    /// <summary>
    /// <see cref="GrantStatus.Claimed"/> → <see cref="GrantStatus.Consumed"/> when the holding
    /// report is approved. Terminal; the amount and the report id are kept.
    /// </summary>
    Task ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);
}
