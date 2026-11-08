using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Services.Dtos;
using NodaTime;

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
    LocalDate? ApplicationTermExpiresAt,
    bool CanApply,
    bool IsApprovedColaborador,
    int ColaboradorCount,
    int AsociadoCount,
    IReadOnlyList<Guid> BoardMemberIds,
    IReadOnlyList<AssemblyVoteListItem> OpenVotes,
    int UnvotedApplicationCount);
