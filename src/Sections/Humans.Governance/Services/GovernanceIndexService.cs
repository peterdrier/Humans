using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Governance.Contracts;
using Humans.Consent.Contracts;
using Humans.Governance.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class GovernanceIndexService(
    IGovernanceManagement applicationDecisionService,
    ILegalDocumentService legalDocService,
    IAssemblyVoteService assemblyVotes,
    IRoleAssignmentService roleAssignments,
    IUserService users,
    GovernanceBodyLinksService bodyLinks,
    IClock clock) : IGovernanceIndexService
{
    public async Task<GovernanceIndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default)
    {
        var applications = await applicationDecisionService.GetUserApplicationsAsync(userId, ct);
        var latestApplication = applications.MaxBy(a => a.SubmittedAt);
        var statutesContent = await legalDocService.GetDocumentContentAsync("statutes", ct);

        var rosters = await GetRostersAsync(ct);
        var colaboradores = rosters[GovernanceBody.Colaboradores];
        var isApprovedColaborador = colaboradores.Contains(userId);

        var boardMemberIds = rosters[GovernanceBody.Board];
        var unvoted = boardMemberIds.Contains(userId)
            ? await applicationDecisionService.GetUnvotedApplicationCountAsync(userId, ct)
            : 0;

        var openVotes = (await assemblyVotes.GetVotesForMemberAsync(userId, ct))
            .Where(v => v.Status == AssemblyVoteStatus.Open)
            .ToList();

        var yourAccess = (await bodyLinks.GetAllAsync(ct))
            .Where(l => rosters[l.Body].Contains(userId) && HasAnyLink(l))
            .OrderBy(l => l.Body)
            .Select(ToAccess)
            .ToList();

        return new GovernanceIndexData(
            statutesContent,
            latestApplication is not null,
            latestApplication?.Status,
            latestApplication?.MembershipTier,
            latestApplication?.SubmittedAt.ToDateTimeUtc(),
            latestApplication?.ResolvedAt?.ToDateTimeUtc(),
            latestApplication?.TermExpiresAt,
            latestApplication is null || latestApplication.Status != ApplicationStatus.Submitted,
            isApprovedColaborador,
            colaboradores.Count,
            rosters[GovernanceBody.Asociados].Count,
            boardMemberIds,
            openVotes,
            unvoted,
            yourAccess);
    }

    public async Task<GovernanceBodyPageData> GetBodyPageAsync(
        GovernanceBody body, Guid viewerId, bool seesAllLinks, CancellationToken ct = default)
    {
        var roster = (await GetRostersAsync(ct))[body];
        var links = seesAllLinks || roster.Contains(viewerId)
            ? (await bodyLinks.GetAllAsync(ct)).SingleOrDefault(l => l.Body == body)
            : null;
        return new GovernanceBodyPageData(body, roster, links is not null && HasAnyLink(links) ? ToAccess(links) : null);
    }

    /// <summary>Approved tier in term, or the Board role, for accounts that are still active members.</summary>
    private async Task<IReadOnlyDictionary<GovernanceBody, IReadOnlyList<Guid>>> GetRostersAsync(CancellationToken ct)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;
        var raw = new Dictionary<GovernanceBody, IReadOnlyList<Guid>>
        {
            [GovernanceBody.Asociados] = await applicationDecisionService.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, today, ct),
            [GovernanceBody.Colaboradores] = await applicationDecisionService.GetActiveApprovedTierUserIdsAsync(MembershipTier.Colaborador, today, ct),
            [GovernanceBody.Board] = await roleAssignments.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct),
        };
        var infos = await users.GetUserInfosAsync(raw.Values.SelectMany(ids => ids).Distinct().ToList(), ct);
        return raw.ToDictionary(
            e => e.Key,
            e => (IReadOnlyList<Guid>)[.. e.Value.Where(id => infos.TryGetValue(id, out var info) && info.IsActive)]);
    }

    private static GovernanceBodyAccess ToAccess(GovernanceBodyLinks links) => new(
        links.Body,
        links.GoogleGroupPrefix is { } prefix ? $"{prefix}@{DomainConstants.GoogleGroupDomain}" : null,
        [.. links.Files
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new GovernanceBodyAccessFile(f.Name, f.Url))]);

    private static bool HasAnyLink(GovernanceBodyLinks links) =>
        links.GoogleGroupPrefix is not null || links.Files.Count > 0;
}
