using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Services;

internal interface IGovernanceIndexService : IApplicationService
{
    Task<GovernanceIndexData> GetIndexDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The body's members, and its group and files when <paramref name="seesAllLinks"/> or the viewer is a member.</summary>
    Task<GovernanceBodyPageData> GetBodyPageAsync(
        GovernanceBody body, Guid viewerId, bool seesAllLinks, CancellationToken ct = default);
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
    int UnvotedApplicationCount,
    IReadOnlyList<GovernanceBodyAccess> YourAccess);

internal sealed record GovernanceBodyPageData(
    GovernanceBody Body,
    IReadOnlyList<Guid> MemberIds,
    GovernanceBodyAccess? Links);

/// <summary>A body's Google group address and files, as shown to members.</summary>
internal sealed record GovernanceBodyAccess(
    GovernanceBody Body,
    string? GroupAddress,
    IReadOnlyList<GovernanceBodyAccessFile> Files);

internal sealed record GovernanceBodyAccessFile(string Name, string? Url);
