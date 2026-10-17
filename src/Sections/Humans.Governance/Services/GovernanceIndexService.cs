using Humans.Governance.Contracts;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class GovernanceIndexService(
    IApplicationServiceRead applicationDecisionService,
    ILegalDocumentService legalDocService,
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
            asociados.Count);
    }
}
