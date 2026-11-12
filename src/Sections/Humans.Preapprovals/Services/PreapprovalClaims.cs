using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using NodaTime;

namespace Humans.Preapprovals.Services;

internal sealed class PreapprovalClaims(
    IPreapprovalRepository repository,
    PreapprovalNotices notices,
    PreapprovalJournal journal,
    PreapprovalReader reader,
    IClock clock,
    ILogger<PreapprovalService> logger) : IApplicationService
{
    private LocalDate Today => reader.Today;

    internal async Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null)
            return new(Refusal: $"Pre-approval {grantId} does not exist.");

        if (grant.Status == GrantStatus.Claimed && grant.ExpenseReportId == expenseReportId)
        {
            // The holding report resubmitting after a rejection: filed in time, so no deadline check, but a fresh stamp.
            Defer(grant.Id, _ => journal.AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Re-stamped for expense report {expenseReportId} (resubmit): cap {PreapprovalJournal.Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId), afterCommit);
            return new(Claimed: PreapprovalReader.ToOption(grant, grant.Program, Today));
        }

        var reason = grant.Status switch
        {
            GrantStatus.Revoked => "pre-approval revoked",
            GrantStatus.Claimed => "pre-approval already held by another expense report",
            GrantStatus.Consumed => "pre-approval already consumed",
            GrantStatus.Open when Today > grant.Program.ClaimBy =>
                $"claim deadline {grant.Program.ClaimBy.ToInvariantDate()} has passed",
            GrantStatus.Open => null,
            _ => $"pre-approval is {grant.Status}"
        };
        if (reason is not null)
            return new(Refusal: $"Pre-approval {grantId} cannot be claimed: {reason}.");

        grant.Status = GrantStatus.Claimed;
        grant.ExpenseReportId = expenseReportId;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, async completionToken =>
        {
            await journal.AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Claimed by expense report {expenseReportId}: cap {PreapprovalJournal.Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId);
            await notices.ResolveFileByAsync(grant.Id, actorUserId, completionToken);
        }, afterCommit);
        return new(Claimed: PreapprovalReader.ToOption(grant, grant.Program, Today));
    }

    internal async Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Open;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, async completionToken =>
        {
            await journal.AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantReleased, grant,
                $"Released by withdrawal of expense report {expenseReportId}; open again until {grant.Program.ClaimBy.ToInvariantDate()}.",
                actorUserId, expenseReportId);
            await notices.NotifyFileByAsync(PreapprovalReader.ToInfo(grant), "Preapprovals_Notice_Released", NotificationPriority.Normal, completionToken);
        }, afterCommit);
        return new();
    }

    internal async Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Consumed;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, _ => journal.AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantConsumed, grant,
            $"Consumed by approval of expense report {expenseReportId}: {PreapprovalJournal.Euros(grant.MaxAmount)}.",
            actorUserId, expenseReportId), afterCommit);
        return new();
    }

    private void Defer(Guid grantId, Func<CancellationToken, Task> completion,
        Action<Func<CancellationToken, Task>> afterCommit)
    {
        async Task CompleteAsync(CancellationToken token)
        {
            try { await completion(token); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to finish notices for committed pre-approval {GrantId}", grantId);
            }
        }

        afterCommit(CompleteAsync);
    }

    private async Task<(PreapprovalGrant? Grant, string? Refusal)> HeldByAsync(Guid grantId, Guid expenseReportId, CancellationToken ct)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null) return (null, $"Pre-approval {grantId} does not exist.");
        if (grant.Status == GrantStatus.Revoked)
            return (null, $"Pre-approval {grantId} cannot be used: pre-approval revoked.");
        if (grant.Status != GrantStatus.Claimed || grant.ExpenseReportId != expenseReportId)
            return (null, $"Pre-approval {grantId} is not held by expense report {expenseReportId} (status {grant.Status}).");
        return (grant, null);
    }

}
