using NodaTime;

namespace Humans.Consent.Services;

/// <summary>One cached active, required document.</summary>
/// <param name="Id">Document id.</param>
/// <param name="Name">Display name.</param>
/// <param name="TeamId">Owning team.</param>
/// <param name="TeamName">Captured at warm time.</param>
/// <param name="LastSyncedAt">Last successful GitHub sync.</param>
/// <param name="Versions">Ordered by <c>EffectiveFrom</c> ascending.</param>
internal sealed record LegalDocumentInfo(
    Guid Id,
    string Name,
    Guid TeamId,
    string TeamName,
    Instant LastSyncedAt,
    IReadOnlyList<LegalDocumentVersionSnapshot> Versions);
