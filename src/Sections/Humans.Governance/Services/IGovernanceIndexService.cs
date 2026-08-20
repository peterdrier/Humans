using Humans.Users.Contracts;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;

namespace Humans.Governance.Services;

internal interface IGovernanceIndexService : IApplicationService
{
    Task<GovernanceIndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default);
}

internal sealed record GovernanceIndexData(
    Dictionary<string, string> StatutesContent,
    bool HasApplication,
    ApplicationStatus? ApplicationStatus,
    MembershipTier? ApplicationTier,
    DateTime? ApplicationSubmittedAt,
    DateTime? ApplicationResolvedAt,
    bool CanApply,
    bool IsApprovedColaborador,
    int ColaboradorCount,
    int AsociadoCount);
