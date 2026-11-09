using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Governance.Contracts;
using Humans.Consent.Contracts;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class GovernanceIndexService(
    IGovernanceService applicationDecisionService,
    ILegalDocumentService legalDocService,
    IAssemblyVoteService assemblyVotes,
    IRoleAssignmentService roleAssignments,
    IClock clock) : IGovernanceIndexService
{
    public async Task<GovernanceIndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default)
    {
        var applications = await applicationDecisionService.GetUserApplicationsAsync(userId, ct);
        var latestApplication = applications.MaxBy(a => a.SubmittedAt);
        var statutesContent = await legalDocService.GetDocumentContentAsync("statutes", ct);

        var today = clock.GetCurrentInstant().InUtc().Date;
        var colaboradores = await applicationDecisionService.GetActiveApprovedTierUserIdsAsync(MembershipTier.Colaborador, today, ct);
        var asociados = await applicationDecisionService.GetActiveApprovedTierUserIdsAsync(MembershipTier.Asociado, today, ct);
        var isApprovedColaborador = colaboradores.Contains(userId);

        var boardMemberIds = await roleAssignments.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);
        var unvoted = boardMemberIds.Contains(userId)
            ? await applicationDecisionService.GetUnvotedApplicationCountAsync(userId, ct)
            : 0;

        var openVotes = (await assemblyVotes.GetVotesForMemberAsync(userId, ct))
            .Where(v => v.Status == AssemblyVoteStatus.Open)
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
            asociados.Count,
            boardMemberIds,
            openVotes,
            unvoted);
    }
}
