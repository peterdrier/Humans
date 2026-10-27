using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

/// <summary>
/// Every state change is a person's action (or Expenses' on their behalf); the only timer is the reminder pass, which
/// never touches a grant.
/// </summary>
/// <remarks>"Today" is the Europe/Madrid date, the zone the association books in.</remarks>
internal sealed partial class PreapprovalService(
    IPreapprovalRepository repository,
    IUserService users,
    IUserEmailService userEmails,
    IHoldedFinanceService finance,
    INotificationEmitter notifications,
    INotificationAutoResolve notificationResolve,
    IEmailService email,
    PreapprovalsEmails emailFactory,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<PreapprovalService> logger) : IPreapprovalManagement
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    internal const string ReminderJobName = "PreapprovalRemindersJob";

    private LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        (await repository.GetProgramsAsync(ct)).Select(ToInfo).ToList();

    public async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program);
    }

    public async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct)).Select(ToInfo).ToList();

    public async Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct)
    {
        var today = Today;
        return (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct))
            .Where(g => g.Status == GrantStatus.Open && today <= g.Program.ClaimBy)
            .Select(ToOption)
            .ToList();
    }

    public async Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        return grant is null ? null : ToOption(grant);
    }

    public async Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(Guid userId, CancellationToken ct) =>
        (await repository.GetGrantIdsOfManagedProgramsAsync(await AllIdsAsync(userId, ct), ct)).ToHashSet();

    public async Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await AllIdsAsync(userId, ct);
        return await repository.HasGrantsForUsersAsync(ids, ct)
            || await repository.ManagesAnyProgramAsync(ids, ct)
            || await repository.AnyProgramAcceptsApplicationsAsync(Today, ct);
    }

    public async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
        await repository.ManagesAnyProgramAsync(await AllIdsAsync(userId, ct), ct);

    public async Task<PreapprovalWriteResult> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        if (grant is null)
            return new(Refusal: $"Pre-approval {grantId} does not exist.");

        if (grant.Status == GrantStatus.Claimed && grant.ExpenseReportId == expenseReportId)
        {
            // The holding report resubmitting after a rejection: filed in time, so no deadline check, but a fresh stamp.
            await AuditGrantAsync(AuditAction.PreapprovalGrantClaimed, grant,
                $"Re-stamped for expense report {expenseReportId} (resubmit): cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
                actorUserId, expenseReportId);
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

        await ResolveFileByAsync(grant.Id, actorUserId, ct);
        await AuditGrantAsync(AuditAction.PreapprovalGrantClaimed, grant,
            $"Claimed by expense report {expenseReportId}: cap {Euros(grant.MaxAmount)}, account {grant.Program.HoldedAccountNumber}.",
            actorUserId, expenseReportId);
        return new(Stamp: Stamp(grant));
    }

    public async Task<PreapprovalWriteResult> ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Open;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        await AuditGrantAsync(AuditAction.PreapprovalGrantReleased, grant,
            $"Released by withdrawal of expense report {expenseReportId}; open again until {grant.Program.ClaimBy.ToInvariantDate()}.",
            actorUserId, expenseReportId);
        await NotifyFileByAsync(ToInfo(grant), "Preapprovals_Notice_Released", NotificationPriority.Normal, ct);
        return new();
    }

    public async Task<PreapprovalWriteResult> ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct)
    {
        var (grant, refusal) = await HeldByAsync(grantId, expenseReportId, ct);
        if (grant is null) return new(Refusal: refusal);

        grant.Status = GrantStatus.Consumed;
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateGrantAsync(grant, ct);

        await AuditGrantAsync(AuditAction.PreapprovalGrantConsumed, grant,
            $"Consumed by approval of expense report {expenseReportId}: {Euros(grant.MaxAmount)}.",
            actorUserId, expenseReportId);
        return new();
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

    private static ProgramInfo ToInfo(PreapprovalProgram p) => new(
        p.Id, p.Name, p.Description, p.Kind, p.PerPersonAmount, p.TotalCeiling, p.HoldedAccountNumber,
        p.HoldedAccountId, p.AcceptsApplications, p.ClaimBy, p.Status, p.CreatedByUserId, p.CreatedAt,
        p.UpdatedAt,
        p.Managers.Select(m => m.UserId).ToList(),
        p.Grants.OrderBy(g => g.CreatedAt).Select(g => ToInfo(g, p)).ToList());

    private static GrantInfo ToInfo(PreapprovalGrant g) => ToInfo(g, g.Program);

    private static GrantInfo ToInfo(PreapprovalGrant g, PreapprovalProgram p) => new(
        g.Id, g.ProgramId, p.Name, p.Kind, g.BeneficiaryUserId, g.Label, g.MaxAmount, g.Status,
        g.ApplicationNote, g.RequestedAmount, g.DecidedByUserId, g.DecidedAt, g.ExpenseReportId,
        p.ClaimBy, g.CreatedAt, g.UpdatedAt);

    private static GrantOption ToOption(PreapprovalGrant g) => new(
        g.Id, g.Program.Name, g.Label, g.MaxAmount, g.Program.HoldedAccountNumber,
        g.Program.HoldedAccountId, g.Program.ClaimBy, g.Status);

    /// <summary>So a grant under a since-merged id is still theirs.</summary>
    private async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

    private static string Euros(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture) + " €";
}
