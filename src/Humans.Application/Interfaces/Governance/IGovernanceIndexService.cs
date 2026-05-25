using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Governance;

public interface IGovernanceIndexService : IApplicationService
{
    Task<GovernanceIndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default);
}

public sealed record GovernanceIndexData(
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
