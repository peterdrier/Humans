using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

internal sealed record ConsentDashboard(
    IReadOnlyList<ConsentDashboardTeamGroup> Groups,
    IReadOnlyList<ConsentDashboardHistoryItem> History);

internal sealed record ConsentDashboardTeamGroup(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<ConsentDashboardDocument> Documents);

internal sealed record ConsentDashboardDocument(
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    Instant EffectiveFrom,
    bool HasConsented,
    Instant? ConsentedAt,
    string? ChangesSummary,
    Instant? LastUpdated);

internal sealed record ConsentDashboardHistoryItem(
    Guid DocumentVersionId,
    string DocumentName,
    string VersionNumber,
    Instant ConsentedAt);

/// <summary>Adds the consent dashboard, whose records the section's view renders directly.</summary>
internal interface IConsentManagement : IConsentService, IConsentSubmission, IApplicationService
{
    /// <summary>Current merge-chain ids keyed only by the requested ids; unknown ids stand alone.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetUserIdChainsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);
}
