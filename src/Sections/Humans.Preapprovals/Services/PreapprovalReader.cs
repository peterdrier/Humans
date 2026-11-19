using Humans.Base.Interfaces;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Preapprovals.Services;

internal sealed class PreapprovalReader(
    IPreapprovalRepository repository,
    IUserService users,
    IClock clock,
    ILogger<PreapprovalService> logger) : IApplicationService
{
    internal static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    internal LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    internal async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default) =>
        (await repository.GetProgramsAsync(ct)).Select(ToInfo).ToList();

    internal async Task<ProgramInfo?> GetProgramAsync(Guid programId, CancellationToken ct = default)
    {
        var program = await repository.GetProgramAsync(programId, ct);
        return program is null ? null : ToInfo(program);
    }

    internal async Task<IReadOnlyList<GrantInfo>> GetGrantsForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct)).Select(ToInfo).ToList();

    internal async Task<IReadOnlyList<GrantOption>> GetGrantOptionsAsync(CancellationToken ct)
    {
        var today = Today;
        return (await repository.GetProgramsAsync(ct))
            .SelectMany(p => p.Grants.Select(g => ToOption(g, p, today)))
            .ToList();
    }

    internal async Task<bool> HasAnythingForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await AllIdsAsync(userId, ct);
        return await repository.HasGrantsForUsersAsync(ids, ct)
            || await repository.ManagesAnyProgramAsync(ids, ct)
            || await repository.AnyProgramAcceptsApplicationsAsync(Today, ct);
    }

    internal async Task<bool> ManagesAnyProgramAsync(Guid userId, CancellationToken ct = default) =>
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
