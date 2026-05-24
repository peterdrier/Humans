using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Governance;

public sealed class GovernanceIndexService(
    IApplicationDecisionService applicationDecisionService,
    ILegalDocumentService legalDocService,
    IUserServiceRead userService) : IGovernanceIndexService
{
    public async Task<GovernanceIndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default)
    {
        var applications = await applicationDecisionService.GetUserApplicationsAsync(userId, ct);
        var latestApplication = applications.Count > 0 ? applications[0] : null;
        var statutesContent = await legalDocService.GetDocumentContentAsync("statutes");

        var snapshot = await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false);
        var colaboradorCount = snapshot.Count(u => u.Profile?.MembershipTier == MembershipTier.Colaborador);
        var asociadoCount = snapshot.Count(u => u.Profile?.MembershipTier == MembershipTier.Asociado);

        var isApprovedColaborador = applications.Any(a =>
            a.Status == ApplicationStatus.Approved && a.MembershipTier == MembershipTier.Colaborador);

        return new GovernanceIndexData(
            statutesContent,
            latestApplication is not null,
            latestApplication?.Status,
            latestApplication?.MembershipTier,
            latestApplication?.SubmittedAt.ToDateTimeUtc(),
            latestApplication?.ResolvedAt?.ToDateTimeUtc(),
            latestApplication is null || latestApplication.Status != ApplicationStatus.Submitted,
            isApprovedColaborador,
            colaboradorCount,
            asociadoCount);
    }
}
