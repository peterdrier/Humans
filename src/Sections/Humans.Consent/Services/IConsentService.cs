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

/// <summary>
/// The section's own service surface: everything <see cref="IConsentServiceRead"/> and
/// <see cref="IConsentSubmission"/> expose across the boundary, plus the consent dashboard,
/// whose four row records have no consumer outside <c>ConsentController</c>.
/// </summary>
/// <remarks>
/// Kept as an interface rather than collapsed into the concrete service for two reasons:
/// <c>CachingConsentService</c> is the §15 decorator over it, and
/// <c>CachingConsentServiceTests</c> substitutes it — <c>MA0053</c> seals the concrete
/// class and Castle DynamicProxy cannot proxy a sealed type (Budget's rule, design §15
/// step 5).
/// </remarks>
internal interface IConsentService : IConsentServiceRead, IConsentSubmission, IApplicationService
{
    Task<ConsentDashboard> GetConsentDashboardAsync(Guid userId, CancellationToken ct = default);
}
