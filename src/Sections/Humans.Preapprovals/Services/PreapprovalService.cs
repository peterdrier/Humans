using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Finance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>
/// Every state change is a person's action (or Expenses' on their behalf). Deadline reminders have a separate owner.
/// </summary>
/// <remarks>"Today" is the Europe/Madrid date, the zone the association books in.</remarks>
internal sealed partial class PreapprovalService(
    IPreapprovalRepository repository,
    IUserService users,
    IFinanceService finance,
    PreapprovalNotices notices,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<PreapprovalService> logger) : IPreapprovalManagement
{
    private readonly PreapprovalReader reader = new(repository, users, clock);

    private LocalDate Today => reader.Today;

    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        reader.GetProgramsAsync(ct);

    public Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default) =>
        reader.GetProgramAsync(programId, ct);

    public Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetGrantsForUserAsync(userId, ct);

    public Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct) =>
        reader.ListClaimableForUserAsync(userId, ct);

    public Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct) =>
        reader.GetGrantAsync(grantId, ct);

    public Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(Guid userId, CancellationToken ct) =>
        reader.ListManagedGrantIdsAsync(userId, ct);

    public Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default) =>
        reader.HasAnythingForUserAsync(userId, ct);

    public Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        reader.ManagesAnyProgramAsync(userId, ct);

    public async Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit = null)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null)
            return new(Refusal: $"Pre-approval {grantId} does not exist.");

        if (grant.Status == GrantStatus.Claimed && grant.ExpenseReportId == expenseReportId)
        {
            // The holding report resubmitting after a rejection: filed in time, so no deadline check, but a fresh stamp.
            await CompleteWriteAsync(grant.Id, _ => AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Re-stamped for expense report {expenseReportId} (resubmit): cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId), ct, afterCommit);
            return new(Stamp: Stamp(grant));
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

        await CompleteWriteAsync(grant.Id, async completionToken =>
        {
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Claimed by expense report {expenseReportId}: cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId);
            await notices.ResolveFileByAsync(grant.Id, actorUserId, completionToken);
        }, ct, afterCommit);
        return new(Stamp: Stamp(grant));
    }

    public async Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit = null)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Open;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        await CompleteWriteAsync(grant.Id, async completionToken =>
        {
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantReleased, grant,
                $"Released by withdrawal of expense report {expenseReportId}; open again until {grant.Program.ClaimBy.ToInvariantDate()}.",
                actorUserId, expenseReportId);
            await notices.NotifyFileByAsync(PreapprovalReader.ToInfo(grant), "Preapprovals_Notice_Released", NotificationPriority.Normal, completionToken);
        }, ct, afterCommit);
        return new();
    }

    public async Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit = null)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Consumed;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        await CompleteWriteAsync(grant.Id, _ => AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantConsumed, grant,
            $"Consumed by approval of expense report {expenseReportId}: {Euros(grant.MaxAmount)}.",
            actorUserId, expenseReportId), ct, afterCommit);
        return new();
    }

    private async Task CompleteWriteAsync(Guid grantId, Func<CancellationToken, Task> completion,
        CancellationToken ct, Action<Func<CancellationToken, Task>>? afterCommit)
    {
        async Task CompleteAsync(CancellationToken token)
        {
            try { await completion(token); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to finish notices for committed pre-approval {GrantId}", grantId);
            }
        }

        if (afterCommit is null) await CompleteAsync(ct);
        else afterCommit(CompleteAsync);
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

    private static GrantStamp Stamp(PreapprovalGrant grant) => new(
        grant.MaxAmount, grant.Program.HoldedAccountNumber, grant.Program.HoldedAccountId, grant.Program.ClaimBy);

    private async Task<UserInfo?> ResolveAvailablePersonAsync(Guid userId, CancellationToken ct)
    {
        var person = await users.GetUserInfoAsync(userId, ct);
        if (person is not null && !person.IsTombstone) return person;
        logger.LogWarning("Preapproval write refused for unavailable person {UserId}", userId);
        return null;
    }

    private const decimal MaxStoredAmount = 9_999_999_999_999_999.99m;

    private static PreapprovalRefusal? MoneyRefusal(decimal amount) =>
        amount <= 0 ? new(PreapprovalErrorKeys.AmountPositive)
        : amount > MaxStoredAmount ? new(PreapprovalErrorKeys.AmountTooLarge)
        : decimal.Round(amount, 2) != amount ? new(PreapprovalErrorKeys.AmountPrecision)
        : null;

    internal static string Euros(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture) + " €";
    private Task AuditGrantAsync(
        AuditActionDefinition action, Domain.PreapprovalGrant grant, string description, Guid actorUserId,
        Guid? expenseReportId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: expenseReportId ?? grant.ProgramId,
            relatedEntityType: expenseReportId is null ? AuditEntityTypes.Program : AuditEntityTypes.ExpenseReport);

    private Task AuditProgramAsync(
        AuditActionDefinition action, Domain.PreapprovalProgram program, string description, Guid actorUserId,
        Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditSubjectTypes.User);

}
