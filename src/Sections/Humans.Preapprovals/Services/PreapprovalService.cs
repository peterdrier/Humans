using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Finance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Users.Contracts;
using NodaTime;
using Humans.Base.Interfaces;
using System.Globalization;

namespace Humans.Preapprovals.Services;

/// <summary>
/// Every state change is a person's action (or Expenses' on their behalf). Deadline reminders have a separate owner.
/// </summary>
/// <remarks>"Today" is the date in the org time zone, the zone the association books in.</remarks>
internal sealed partial class PreapprovalService(
    IOrgTimeZone orgTimeZone,
    IPreapprovalRepository repository,
    IUserService users,
    IFinanceService finance,
    PreapprovalNotices notices,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<PreapprovalService> logger) : IPreapprovalManagement
{
    public async Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null)
            return new(Refusal: $"Pre-approval {grantId} does not exist.");

        if (grant.Status == GrantStatus.Claimed && grant.ExpenseReportId == expenseReportId)
        {
            // The holding report resubmitting after a rejection: filed in time, so no deadline check, but a fresh stamp.
            Defer(grant.Id, _ => AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Re-stamped for expense report {expenseReportId} (resubmit): cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId), afterCommit);
            return new(Claimed: ToOption(grant, grant.Program, Today));
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
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantClaimed, grant,
                $"Claimed by expense report {expenseReportId}: cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId);
            await notices.ResolveFileByAsync(grant.Id, actorUserId, completionToken);
        }, afterCommit);
        return new(Claimed: ToOption(grant, grant.Program, Today));
    }

    public async Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Open;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, async completionToken =>
        {
            await AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantReleased, grant,
                $"Released by withdrawal of expense report {expenseReportId}; open again until {grant.Program.ClaimBy.ToInvariantDate()}.",
                actorUserId, expenseReportId);
            await notices.NotifyFileByAsync(ToInfo(grant), "Preapprovals_Notice_Released", NotificationPriority.Normal, completionToken);
        }, afterCommit);
        return new();
    }

    public async Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct, Action<Func<CancellationToken, Task>> afterCommit)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Consumed;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        Defer(grant.Id, _ => AuditGrantAsync(PreapprovalsAuditActions.PreapprovalGrantConsumed, grant,
            $"Consumed by approval of expense report {expenseReportId}: {Euros(grant.MaxAmount)}.",
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

    internal static string Euros(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture) + " €";
    internal Task AuditGrantAsync(
        AuditActionDefinition action, Domain.PreapprovalGrant grant, string description, Guid actorUserId,
        Guid? expenseReportId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Grant, grant.Id, description, actorUserId,
            relatedEntityId: expenseReportId ?? grant.ProgramId,
            relatedEntityType: expenseReportId is null ? AuditEntityTypes.Program : AuditEntityTypes.ExpenseReport);

    internal Task AuditProgramAsync(
        AuditActionDefinition action, Domain.PreapprovalProgram program, string description, Guid actorUserId,
        Guid? relatedUserId = null) =>
        auditLog.LogAsync(action, AuditEntityTypes.Program, program.Id, description, actorUserId,
            relatedEntityId: relatedUserId,
            relatedEntityType: relatedUserId is null ? null : AuditSubjectTypes.User);

    private DateTimeZone OrgZone => orgTimeZone.Zone;

    internal LocalDate Today => clock.GetCurrentInstant().InZone(OrgZone).Date;

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        (await repository.GetProgramsAsync(ct)).Select(ToInfo).ToList();

    public async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program);
    }

    public async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct)).Select(ToInfo).ToList();

    public async Task<IReadOnlyList<GrantOption>> GetGrantsAsync(CancellationToken ct)
    {
        var today = Today;
        return (await repository.GetProgramsAsync(ct))
            .SelectMany(p => p.Grants.Select(g => ToOption(g, p, today)))
            .ToList();
    }

    public async Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await AllIdsAsync(userId, ct);
        return await repository.HasGrantsForUsersAsync(ids, ct)
            || await repository.ManagesAnyProgramAsync(ids, ct)
            || await repository.AnyProgramAcceptsApplicationsAsync(Today, ct);
    }

    public async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        await repository.ManagesAnyProgramAsync(await AllIdsAsync(userId, ct), ct);

    internal static ProgramInfo ToInfo(PreapprovalProgram p) => new(
        p.Id, p.Name, p.Description, p.Kind, p.PerPersonAmount, p.TotalCeiling, p.HoldedAccountNumber,
        p.AcceptsApplications, p.ClaimBy, p.Status,
        p.Managers.Select(m => m.UserId).ToList(),
        p.Grants.Select(g => ToInfo(g, p)).ToList());

    internal static GrantInfo ToInfo(PreapprovalGrant g) => ToInfo(g, g.Program);

    internal static GrantInfo ToInfo(PreapprovalGrant g, PreapprovalProgram p) => new(
        g.Id, g.ProgramId, p.Name, g.BeneficiaryUserId, g.Label, g.MaxAmount, g.Status,
        g.ApplicationNote, g.RequestedAmount, g.DecidedByUserId, g.DecidedAt, g.ExpenseReportId,
        p.ClaimBy, g.CreatedAt, g.UpdatedAt);

    internal static GrantOption ToOption(PreapprovalGrant g, PreapprovalProgram p, LocalDate today) => new(
        g.Id, g.BeneficiaryUserId, p.Managers.Select(m => m.UserId).ToList(), p.Name, g.Label, g.MaxAmount,
        p.HoldedAccountNumber, p.HoldedAccountId, p.ClaimBy, g.Status,
        g.Status == GrantStatus.Open && today <= p.ClaimBy);

    /// <summary>So a grant under a since-merged id is still theirs.</summary>
    internal async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

    internal async Task<UserInfo?> ResolveAvailablePersonAsync(Guid userId, CancellationToken ct)
    {
        var person = await users.GetUserInfoAsync(userId, ct);
        if (person is not null && !person.IsTombstone) return person;
        logger.LogWarning("Preapproval write refused for unavailable person {UserId}", userId);
        return null;
    }
}
