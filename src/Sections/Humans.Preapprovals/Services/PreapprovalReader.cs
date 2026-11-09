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
    IClock clock) : IApplicationService
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

    internal async Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct)
    {
        var today = Today;
        return (await repository.GetGrantsForUsersAsync(await AllIdsAsync(userId, ct), ct))
            .Where(g => g.Status == GrantStatus.Open && today <= g.Program.ClaimBy)
            .Select(ToOption)
            .ToList();
    }

    internal async Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct)
    {
        var grant = await repository.GetGrantAsync(grantId, ct);
        return grant is null ? null : ToOption(grant);
    }

    internal async Task<IReadOnlySet<Guid>> ListManagedGrantIdsAsync(Guid userId, CancellationToken ct) =>
        (await repository.GetGrantIdsOfManagedProgramsAsync(await AllIdsAsync(userId, ct), ct)).ToHashSet();

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
        g.Id, g.ProgramId, p.Name, p.Kind, g.BeneficiaryUserId, g.Label, g.MaxAmount, g.Status,
        g.ApplicationNote, g.RequestedAmount, g.DecidedByUserId, g.DecidedAt, g.ExpenseReportId,
        p.ClaimBy, g.CreatedAt, g.UpdatedAt);

    private static GrantOption ToOption(PreapprovalGrant g) => new(
        g.Id, g.Program.Name, g.Label, g.MaxAmount, g.Program.HoldedAccountNumber,
        g.Program.HoldedAccountId, g.Program.ClaimBy, g.Status);

    /// <summary>So a grant under a since-merged id is still theirs.</summary>
    internal async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return info is null ? [userId] : info.AllUserIds;
    }

}
